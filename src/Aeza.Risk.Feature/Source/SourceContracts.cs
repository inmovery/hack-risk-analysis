using System.Globalization;

using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Source;
using Aeza.Risk.SharedKernel.Json;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Aeza.Risk.Feature.Source;

/// <summary>Документационная форма тела PUT snapshot (поля записей — оригинальные имена источника)</summary>
[PublicAPI]
public sealed record SnapshotEnvelope(
    int SchemaVersion, DateTimeOffset SourceAsOf, string? SourceRevision, bool? Synthetic,
    IReadOnlyList<SnapshotAccount> Accounts, IReadOnlyList<SnapshotServiceRecord> Services, IReadOnlyList<SnapshotTransaction> Transactions);

/// <summary>Аккаунт источника</summary>
[PublicAPI]
public sealed record SnapshotAccount(long Id, string Email, long? Balance, long? WithdrawBalance, long? TotalReplenished, long? BonusBalance, string? BonusState);

/// <summary>Услуга источника</summary>
[PublicAPI]
public sealed record SnapshotServiceRecord(
    long Id, string Name, long OwnerId, string? PaymentTerm, bool? AutoProlong, DateTimeOffset? CreatedAt, DateTimeOffset? ExpiresAt, string? Status, long? Price);

/// <summary>Транзакция источника</summary>
[PublicAPI]
public sealed record SnapshotTransaction(long Id, long? Amount, string? Status, DateTimeOffset? PerformedAt, DateTimeOffset? CreatedAt, string? Type, long ServiceId);

/// <summary>Счётчики записей тестовых данных</summary>
[PublicAPI]
public sealed record SnapshotCounts(int Accounts, int Services, int Transactions);

/// <summary>Тестовые данные загружены</summary>
[PublicAPI]
public sealed record SnapshotAcceptedResponse(
    long SourceRevision, long? JobId, bool Idempotent, SnapshotCounts Counts, IReadOnlyDictionary<string, int> Warnings, DateTimeOffset SourceAsOf, bool SourceAsOfAssumed);

/// <summary>Отчёт проверки тестовых данных без сохранения</summary>
[PublicAPI]
public sealed record ValidationReport(
    bool Valid, SnapshotCounts Counts, IReadOnlyDictionary<string, string[]> Errors, IReadOnlyDictionary<string, int> ErrorCounts,
    IReadOnlyDictionary<string, int> Warnings, DateTimeOffset? SourceAsOf, bool SourceAsOfAssumed);

/// <summary>Сведения о загрузке тестовых данных</summary>
[PublicAPI]
public sealed record SnapshotInfo(
    long Revision, DateTimeOffset SourceAsOf, bool SourceAsOfAssumed, DateTimeOffset ReceivedAt, string ContentHash, string RawSha256, string Adapter,
    int SchemaVersion, string? SourceRevisionLabel, bool Synthetic, string Provenance, SnapshotCounts Counts, IReadOnlyDictionary<string, int> Warnings,
    long RawBytes, string Actor, bool Active);

/// <summary>Общее для срезов источника: чтение тела с лимитом, момент выгрузки, маппинг сведений</summary>
internal static class SnapshotHttp
{
    /// <summary>Лимит тела загрузки тестовых данных</summary>
    public const long MaxBodyBytes = 50L * 1024 * 1024;

    public static SnapshotInfo ToInfo(this SourceSnapshotEntity s, bool active) => new(
        s.Revision, s.SourceAsOf, s.SourceAsOfAssumed, s.ReceivedAt, s.ContentHash, s.RawSha256, s.Adapter, s.SchemaVersion, s.SourceRevisionLabel,
        s.Synthetic, s.Provenance, new SnapshotCounts(s.AccountsCount, s.ServicesCount, s.TransactionsCount),
        AppJson.Deserialize<Dictionary<string, int>>(s.Warnings), s.RawBytes, s.Actor, active);

    public static SnapshotCounts Counts(this ParsedSnapshot parsed) =>
        new(parsed.Data.Accounts.Count, parsed.Data.Services.Count, parsed.Data.Transactions.Count);

    public static async Task<(byte[]? Body, ProblemHttpResult? Error)> ReadBodyAsync(HttpRequest req, CancellationToken ct, long maxBytes = MaxBodyBytes)
    {
        var feature = req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = maxBytes;
        if (req.ContentLength > maxBytes) return (null, TooLarge(maxBytes));
        try
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            if (ms.Length > maxBytes) return (null, TooLarge(maxBytes));
            return (ms.ToArray(), null);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(maxBytes));
        }
    }

    public static (DateTimeOffset? AsOf, bool Assumed, ProblemHttpResult? Error) ResolveAsOf(ParsedSnapshot parsed, HttpRequest req, TimeProvider time)
    {
        if (parsed.SourceAsOf is { } fromBody) return (fromBody, false, null);
        var q = req.Query["sourceAsOf"].ToString();
        if (!string.IsNullOrEmpty(q))
        {
            var hasZone = q.EndsWith('Z') || q.Contains('+', StringComparison.Ordinal) || q.LastIndexOf('-') > 9;
            if (!hasZone || !DateTimeOffset.TryParse(q, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return (null, false, AppProblems.Validation(new Dictionary<string, string[]> { ["sourceAsOf"] = ["ISO 8601 с часовым поясом."] }));
            return (d.ToUniversalTime(), false, null);
        }
        return (time.GetUtcNow(), true, null);
    }

    private static ProblemHttpResult TooLarge(long maxBytes) =>
        AppProblems.Create(StatusCodes.Status413PayloadTooLarge, $"Тело больше {maxBytes / 1024 / 1024} МБ", "payload_too_large");
}
