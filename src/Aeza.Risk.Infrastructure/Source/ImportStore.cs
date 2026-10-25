using System.IO.Compression;
using System.Text;
using System.Text.Json;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Source;

/// <summary>Итог загрузки файла импорта</summary>
public sealed record ImportUpload(long ImportId, int Chunks, long Lines, long Bytes);

/// <summary>Пакетный импорт истории (коннектор batch-import)</summary>
public static class ImportStore
{
    public const int ChunkLines = 5000;
    public const long MaxLines = 3_000_000;
    public const int MaxLineChars = 256 * 1024;

    /// <summary>Нарезать файл на куски и сохранить; в очередь — только целиком загруженный файл</summary>
    public static async Task<ImportUpload> UploadAsync(AppDbContext db, Stream body, string actor, string connector, bool synthetic, DateTimeOffset now, CancellationToken ct)
    {
        var import = new SourceImportEntity { CreatedAt = now, Actor = actor, Connector = connector, Synthetic = synthetic, Status = ImportStatus.Uploading };
        db.SourceImports.Add(import);
        await db.SaveChangesAsync(ct);
        var counting = new CountingStream(body);
        try
        {
            var head = new byte[2];
            var n = await ReadAtLeastAsync(counting, head, ct);
            Stream input = new PrefixedStream(head.AsMemory(0, n), counting);
            if (n == 2 && head[0] == 0x1f && head[1] == 0x8b) input = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
            var lines = new List<string>(ChunkLines);
            long total = 0;
            var chunks = 0;
            long firstLine = 1, lineNo = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                lineNo++;
                if (line.Length > MaxLineChars) throw new ImportRejectedException("line_too_long", $"Строка {lineNo} длиннее {MaxLineChars} символов.");
                if (line.Length == 0 || string.IsNullOrWhiteSpace(line)) continue;
                if (++total > MaxLines) throw new ImportRejectedException("too_many_lines", $"Не более {MaxLines} событий в одном импорте.");
                if (lines.Count == 0) firstLine = lineNo;
                lines.Add(line);
                if (lines.Count < ChunkLines) continue;
                await SaveChunkAsync(db, import.Id, chunks++, firstLine, lines, ct);
                lines.Clear();
            }
            if (lines.Count > 0) await SaveChunkAsync(db, import.Id, chunks++, firstLine, lines, ct);
            if (total == 0) throw new ImportRejectedException("empty", "В файле нет событий.");
            await db.SourceImports.Where(i => i.Id == import.Id).ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Status, ImportStatus.Pending).SetProperty(i => i.ChunksTotal, chunks)
                .SetProperty(i => i.EventsTotal, total).SetProperty(i => i.Bytes, counting.BytesRead), ct);
            return new ImportUpload(import.Id, chunks, total, counting.BytesRead);
        }
        catch (Exception ex) when (ex is ImportRejectedException or InvalidDataException or IOException or OperationCanceledException)
        {
            // Незагруженный файл не исполняется: куски удаляются, импорт помечается неудачным
            var code = ex switch { ImportRejectedException r => r.Code, InvalidDataException => "invalid_gzip", OperationCanceledException => "upload_aborted", _ => "upload_failed" };
            await db.SourceImportChunks.Where(c => c.ImportId == import.Id).ExecuteDeleteAsync(CancellationToken.None);
            await db.SourceImports.Where(i => i.Id == import.Id).ExecuteUpdateAsync(u => u
                .SetProperty(i => i.Status, ImportStatus.Failed).SetProperty(i => i.ErrorCode, code).SetProperty(i => i.FinishedAt, now), CancellationToken.None);
            throw;
        }
    }

    private static async Task SaveChunkAsync(AppDbContext db, long importId, int no, long firstLine, List<string> lines, CancellationToken ct)
    {
        db.SourceImportChunks.Add(new SourceImportChunkEntity { ImportId = importId, No = no, Lines = lines.Count, FirstLine = firstLine, Data = Pack(lines) });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
    }

    public static byte[] Pack(IReadOnlyList<string> lines)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var w = new StreamWriter(gz, new UTF8Encoding(false)) { NewLine = "\n" })
            foreach (var l in lines) w.WriteLine(l);
        return ms.ToArray();
    }

    public static List<string> Unpack(byte[] data)
    {
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var r = new StreamReader(gz, Encoding.UTF8);
        var lines = new List<string>();
        while (r.ReadLine() is { } l) lines.Add(l);
        return lines;
    }

    /// <summary>Строка — один JSON-объект (иначе весь пакет парсера был бы отклонён)</summary>
    public static bool IsJsonObject(string line)
    {
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(line));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<int> ReadAtLeastAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return read;
    }

    /// <summary>Поток: сначала прочитанные байты, затем остальное (тело запроса не перематывается)</summary>
    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefix.Length == 0) return await rest.ReadAsync(buffer, cancellationToken);
            var n = Math.Min(buffer.Length, _prefix.Length);
            _prefix[..n].CopyTo(buffer);
            _prefix = _prefix[n..];
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Считает байты тела (размер загрузки)</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Статусы импорта</summary>
public static class ImportStatus
{
    public const string Uploading = "uploading";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <inheritdoc />
public sealed class SourceImports(AppDbContext db, Aeza.Risk.Infrastructure.BackgroundJobs.ImportSignal signal, TimeProvider time) : ISourceImports
{
    /// <inheritdoc />
    public async Task<ImportView> UploadAsync(Stream body, string actor, string connector, bool synthetic, CancellationToken cancellationToken)
    {
        var upload = await ImportStore.UploadAsync(db, body, actor, connector, synthetic, time.GetUtcNow(), cancellationToken);
        signal.Notify();
        return (await GetAsync(upload.ImportId, cancellationToken))!;
    }

    /// <inheritdoc />
    public async Task<ImportView?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var i = await db.SourceImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return i is null ? null : ToView(i);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImportView>> ListAsync(int limit, CancellationToken cancellationToken) =>
        (await db.SourceImports.AsNoTracking().OrderByDescending(i => i.Id).Take(limit).ToListAsync(cancellationToken)).Select(ToView).ToList();

    private static ImportView ToView(SourceImportEntity i) => new(i.Id, i.CreatedAt, i.Actor, i.Connector, i.Synthetic, i.Status, i.Bytes, i.ChunksTotal,
        i.ChunksDone, i.EventsTotal, i.Applied, i.Recorded, i.NoChange, i.Stale, i.Duplicates, i.Rejected,
        i.Errors is null ? [] : JsonSerializer.Deserialize<List<string>>(i.Errors) ?? [], i.ErrorCode, i.StartedAt, i.FinishedAt, i.JobId);
}
