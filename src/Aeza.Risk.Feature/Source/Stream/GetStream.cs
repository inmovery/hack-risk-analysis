using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Feature.Source.Events;
using Aeza.Risk.SharedKernel.Abstraction;

using JetBrains.Annotations;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Feature.Source.Stream;

/// <summary>Состояние приёма источника: база, журнал, свежесть, качество потока и входящие коннекторы</summary>
[UsedImplicitly]
public class GetStream
{
    private static async Task<Ok<StreamStatusDto>> Handle(
        ISourceStreamRepository journal, ISnapshotRepository snapshots, ISourceImports imports, IOptions<StreamOptions> stream,
        IOptions<AuthOptions> auth, TimeProvider time, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        var coverage = await journal.GetCoverageAsync(ct);
        var counters = await journal.GetCountersAsync(now, ct);
        var states = (await journal.GetConnectorsAsync(ct)).ToDictionary(c => c.Name);
        var current = await snapshots.GetCurrentAsync(ct);
        var silentAfter = TimeSpan.FromMinutes(stream.Value.SilentAfterMinutes);
        var silent = coverage.LastEventAt is { } last && now - last > silentAfter;

        ConnectorDto Push(string key, bool enabled, ImportView? lastImport = null)
        {
            var s = states.GetValueOrDefault(key);
            var status = s?.LastReceivedAt is not { } at ? "idle" : now - at > silentAfter ? "silent" : "ok";
            // Импорт истории — разовая загрузка: молчание после неё нормально
            if (key == ConnectorNames.BatchImport) status = lastImport?.Status is "running" or "pending" or "uploading" ? "ok" : s is null ? "idle" : "ok";
            long? lag = s?.LastOccurredAt is { } o ? Math.Max(0, (long)(now - o).TotalSeconds) : null;
            return new ConnectorDto(key, enabled, status, s?.LastReceivedAt, s?.LastOccurredAt, s?.Watermark, lag, s?.Events ?? 0,
                s?.Rejected ?? 0, s?.Duplicates ?? 0, s?.LastError, s?.LastErrorAt, lastImport);
        }

        var lastImport = (await imports.ListAsync(1, ct)).FirstOrDefault();
        var connectors = new List<ConnectorDto>
        {
            Push(ConnectorNames.HttpPush, !string.IsNullOrEmpty(auth.Value.IngestToken)),
            Push(ConnectorNames.BatchImport, true, lastImport),
            new(ConnectorNames.Snapshot, true, current is null ? "idle" : "ok", current?.ReceivedAt, current?.SourceAsOf, null, null, current is null ? 0 : 1,
                0, 0, null, null, null),
        };
        connectors.AddRange(auth.Value.IngestConnectors.Keys.Order(StringComparer.Ordinal).Select(name => Push(name, true)));
        // Пакеты из панели и коннекторы, которых больше нет в конфигурации, но события от них были
        connectors.AddRange(states.Keys.Where(k => connectors.All(c => c.Key != k)).Order(StringComparer.Ordinal).Select(k => Push(k, k == ConnectorNames.Panel)));

        var seq = await journal.ListAsync(null, 1, null, SourceCodes.Applied, null, ct);
        return TypedResults.Ok(new StreamStatusDto(
            current is null ? null : new StreamBaseDto(current.Revision, current.SourceAsOf, current.Synthetic),
            seq.Count > 0 ? seq[0].Seq : 0, coverage.SourceUpdatedAt, coverage.StreamStartedAt, coverage.LastEventAt,
            coverage.Events, counters.EventsSinceBase, counters.Last24h, counters.OrphanServices, counters.OrphanTransactions,
            coverage.Synthetic, silent, stream.Value.SilentAfterMinutes,
            new StreamQualityDto(counters.DomainEvents24h, counters.UnknownTypes24h, counters.AccountsWithGaps, counters.OldestOrphanAt),
            connectors));
    }

    /// <inheritdoc />
    [UsedImplicitly]
    public class Endpoint : EndpointBase
    {
        /// <inheritdoc />
        protected override string Name => nameof(GetStream);

        /// <inheritdoc />
        protected override RouteHandlerBuilder MapEndpointCore(IEndpointRouteBuilder app)
            => app.MapGet("source/stream", Handle)
                .RequireAuthorization(Policies.Reader)
                .WithTags("Source")
                .WithSummary("Состояние приёма источника: база, журнал, свежесть, качество потока (пропуски, неизвестные типы) и входящие коннекторы");
    }
}
