using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Aeza.Risk.Core;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.Infrastructure.Data.Repositories;

using Microsoft.EntityFrameworkCore;

namespace Aeza.Risk.Infrastructure.Ml;

/// <summary>Теневые модели: пакет model-bundle.zip от tools/Aeza.Risk.Ml проверяется (карточка, схема и порядок признаков, SHA-256, ONNX Runtime на проверочных строках), сохраняется в БД и включается</summary>
public sealed class MlModelStore(AppDbContext db, IAuditLog audit, TimeProvider time) : IMlModels
{
    /// <summary>Лимит пакета модели</summary>
    public const long MaxBundleBytes = 50L * 1024 * 1024;

    private const int MinMaturedPerClass = 30;

    /// <inheritdoc />
    public async Task<MlModelActivated> UploadAsync(Stream bundle, string actor, CancellationToken cancellationToken)
    {
        var (onnx, card, report) = await ReadBundleAsync(bundle, cancellationToken);
        var root = card.RootElement;
        string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        if (root.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.Number && format.GetInt32() != 1)
            throw new MlModelRejectedException("card_invalid", "Поддерживается карточка model.json format = 1");
        if (Str("featureSchema") != MlFeatures.Schema)
            throw new MlModelRejectedException("schema_mismatch", $"Схема признаков модели «{Str("featureSchema")}», анализатор считает {MlFeatures.Schema}: переобучите модель");
        var features = root.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Array
            ? f.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        if (!features.SequenceEqual(MlFeatures.Names))
            throw new MlModelRejectedException("features_mismatch", "Список и порядок признаков в карточке не совпадают со схемой анализатора");
        if (Str("input") != OnnxShadowRuntime.Input || Str("output") != OnnxShadowRuntime.Output)
            throw new MlModelRejectedException("card_invalid", $"Карточка должна объявлять вход {OnnxShadowRuntime.Input} и выход {OnnxShadowRuntime.Output}");
        var sha = Convert.ToHexStringLower(SHA256.HashData(onnx));
        if (!string.Equals(Str("onnxSha256"), sha, StringComparison.OrdinalIgnoreCase))
            throw new MlModelRejectedException("onnx_hash_mismatch", "SHA-256 model.onnx не совпадает с карточкой");
        var version = Str("version");
        if (version.Length is 0 or > 100) throw new MlModelRejectedException("card_invalid", "В карточке нет версии модели");
        OnnxShadowRuntime.Validate(onnx);

        var now = time.GetUtcNow();
        var entity = new MlModelEntity
        {
            Version = version, FeatureSchema = MlFeatures.Schema, Kind = Str("model") is { Length: > 0 and <= 40 } k ? k : "unknown",
            Synthetic = !root.TryGetProperty("synthetic", out var syn) || syn.ValueKind != JsonValueKind.False,
            CardJson = root.GetRawText(), ReportMarkdown = report, Onnx = onnx, OnnxSha256 = sha, UploadedAt = now, UploadedBy = actor,
        };
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.MlModels.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        var state = await db.AppState.FromSqlRaw("SELECT * FROM app_state WHERE id = 1 FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (state is null) db.AppState.Add(state = new AppStateEntity());
        state.ActiveMlModelId = entity.Id;
        state.MlRevision++;
        var job = RecalcQueue.Enqueue(db, "ml_model", now, actor);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await audit.WriteAsync(actor, "ml_model_activated", new { modelId = entity.Id, version, kind = entity.Kind, sha256 = sha, synthetic = entity.Synthetic }, cancellationToken);
        return new MlModelActivated(ToView(entity), job.Id);
    }

    /// <inheritdoc />
    public async Task<long?> DeactivateAsync(string actor, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var state = await db.AppState.FromSqlRaw("SELECT * FROM app_state WHERE id = 1 FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (state?.ActiveMlModelId is not { } id) return null;
        state.ActiveMlModelId = null;
        state.MlRevision++;
        var job = RecalcQueue.Enqueue(db, "ml_model", time.GetUtcNow(), actor);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await audit.WriteAsync(actor, "ml_model_deactivated", new { modelId = id }, cancellationToken);
        return job.Id;
    }

    /// <inheritdoc />
    public async Task<MlActiveModel?> GetActiveAsync(CancellationToken cancellationToken)
    {
        var id = await db.AppState.AsNoTracking().Select(s => s.ActiveMlModelId).SingleOrDefaultAsync(cancellationToken);
        if (id is null) return null;
        var m = await db.MlModels.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Version, x.Kind, x.FeatureSchema, x.Synthetic, x.UploadedAt, x.UploadedBy, x.OnnxSha256, Bytes = x.Onnx.Length, x.CardJson, x.ReportMarkdown })
            .SingleOrDefaultAsync(cancellationToken);
        return m is null
            ? null
            : new MlActiveModel(new MlModelView(m.Id, m.Version, m.Kind, m.FeatureSchema, m.Synthetic, m.UploadedAt, m.UploadedBy, m.OnnxSha256, m.Bytes), m.CardJson, m.ReportMarkdown);
    }

    /// <inheritdoc />
    public async Task<MlOnlineEvaluation> EvaluateAsync(long modelId, CancellationToken cancellationToken)
    {
        var rows = await (from p in db.MlShadowPredictions.AsNoTracking()
                          where p.ModelId == modelId
                          join o in db.ExpiryOutcomes.AsNoTracking() on new { p.ServiceId, p.ExpiresAt } equals new { o.ServiceId, o.ExpiresAt } into outcomes
                          from o in outcomes.DefaultIfEmpty()
                          select new { p.Probability, p.RulesScore, Outcome = o == null ? null : o.Outcome }).ToListAsync(cancellationToken);
        var matured = rows.Where(r => r.Outcome is ExpiryOutcomes.Renewed or ExpiryOutcomes.NotRenewed).ToList();
        var positives = matured.Count(r => r.Outcome == ExpiryOutcomes.NotRenewed);
        var pending = rows.Count(r => r.Outcome is null);
        if (positives < MinMaturedPerClass || matured.Count - positives < MinMaturedPerClass)
            return new MlOnlineEvaluation(rows.Count, matured.Count, positives, pending, null, null);
        var labels = matured.Select(r => r.Outcome == ExpiryOutcomes.NotRenewed ? 1 : 0).ToList();
        return new MlOnlineEvaluation(rows.Count, matured.Count, positives, pending,
            RankingMetrics.Evaluate(labels, matured.Select(r => r.Probability).ToList(), probabilities: true),
            RankingMetrics.Evaluate(labels, matured.Select(r => (double)(r.RulesScore ?? 0)).ToList(), probabilities: false));
    }

    private static async Task<(byte[] Onnx, JsonDocument Card, string? Report)> ReadBundleAsync(Stream bundle, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await bundle.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxBundleBytes) throw new MlModelRejectedException("payload_too_large", $"Пакет больше {MaxBundleBytes / 1024 / 1024} МБ");
        buffer.Position = 0;
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);
            byte[]? Entry(string name, long max)
            {
                var e = zip.GetEntry(name);
                if (e is null) return null;
                // Размер в заголовке zip не доверяем: читаем не больше лимита
                using var s = e.Open();
                using var ms = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = s.Read(chunk)) > 0)
                {
                    if (ms.Length + read > max) throw new MlModelRejectedException("bundle_invalid", $"{name} больше лимита");
                    ms.Write(chunk, 0, read);
                }
                return ms.ToArray();
            }
            var onnx = Entry("model.onnx", MaxBundleBytes) ?? throw new MlModelRejectedException("bundle_invalid", "В пакете нет model.onnx");
            var cardBytes = Entry("model.json", 1024 * 1024) ?? throw new MlModelRejectedException("bundle_invalid", "В пакете нет model.json");
            var report = Entry("report.md", 1024 * 1024) is { } r ? Encoding.UTF8.GetString(r) : null;
            JsonDocument card;
            try
            {
                card = JsonDocument.Parse(cardBytes);
            }
            catch (JsonException)
            {
                throw new MlModelRejectedException("card_invalid", "model.json — не JSON");
            }
            if (card.RootElement.ValueKind != JsonValueKind.Object) throw new MlModelRejectedException("card_invalid", "model.json должен быть объектом");
            return (onnx, card, report);
        }
        catch (InvalidDataException)
        {
            throw new MlModelRejectedException("bundle_invalid", "Пакет должен быть zip с model.onnx и model.json");
        }
    }

    private static MlModelView ToView(MlModelEntity m) =>
        new(m.Id, m.Version, m.Kind, m.FeatureSchema, m.Synthetic, m.UploadedAt, m.UploadedBy, m.OnnxSha256, m.Onnx.Length);
}
