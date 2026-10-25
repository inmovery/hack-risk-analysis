using System.Text;
using System.Text.Json;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Entities;
using Aeza.Risk.Core.Options;
using Aeza.Risk.Core.Simulation;
using Aeza.Risk.Core.Source;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.SharedKernel.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aeza.Risk.Infrastructure.Source;

/// <summary>Настройки симуляции, пересоздание snapshot системы и связь с симулятором</summary>
public sealed class SimulationStore(
    AppDbContext db,
    ISnapshotRepository snapshots,
    IAuditLog audit,
    IRecalcSignal signal,
    TimeProvider time,
    IOptions<SimulationOptions> options,
    ILogger<SimulationStore> log) : ISimulationStore
{
    /// <summary>Момент пустой базы: раньше любого события, поэтому все события применяются</summary>
    public static readonly DateTimeOffset EmptyBaseAsOf = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public async Task<SimulationView> GetAsync(CancellationToken cancellationToken) => ToView(await LoadAsync(cancellationToken));

    /// <inheritdoc />
    public async Task<SimulationView> SaveSettingsAsync(SimulationSettings settings, string actor, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(cancellationToken);
        row.SettingsJson = AppJson.Serialize(settings with { Running = true });
        row.Revision++;
        row.UpdatedAt = time.GetUtcNow();
        row.UpdatedBy = actor;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(actor, "simulation_settings_saved", new { row.Revision, settings.Running, settings.TickSeconds, settings.Chaos, settings.World }, cancellationToken);
        return ToView(row);
    }

    /// <inheritdoc />
    public async Task<SimulationView> ResetAsync(string actor, CancellationToken cancellationToken)
    {
        if (!options.Value.EventsOnly) throw new InvalidOperationException("Пересоздание snapshot системы доступно только в режиме работы на событиях");
        var ct = cancellationToken;
        var now = time.GetUtcNow();
        var row = await LoadAsync(ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // Та же блокировка, что у приёма событий: пакеты старого snapshot системы после этой точки отклоняются по ревизии snapshot системы
            await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({SourceIngestion.WriteLockKey})", ct);
            await db.Entry(row).ReloadAsync(ct);
            await db.Database.ExecuteSqlRawAsync(WipeSql, ct);
            await db.AppState.Where(s => s.Id == 1).ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ActiveGenerationId, (long?)null)
                .SetProperty(s => s.LatestSourceSnapshotId, (long?)null)
                .SetProperty(s => s.SourceSeq, 0L)
                .SetProperty(s => s.SourceUpdatedAt, (DateTimeOffset?)null), ct);
            row.WorldRevision++;
            row.WorldStartedAt = Truncate(now);
            row.WorldSettingsJson = AppJson.Serialize(Settings(row).World);
            row.WorldResetAt = now;
            row.WorldResetBy = actor;
            // События старого snapshot системы симулятор уже не применит
            await db.SimulationCommands.Where(c => c.Status == EventComposer.Pending).ExecuteUpdateAsync(u => u
                .SetProperty(c => c.Status, EventComposer.Rejected)
                .SetProperty(c => c.Message, "Snapshot системы пересоздан до применения события")
                .SetProperty(c => c.UpdatedAt, (DateTimeOffset?)now), ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        await EnsureEmptyBaseAsync(ct);
        await audit.WriteAsync(actor, "simulation_world_reset", new { row.WorldRevision, row.WorldStartedAt, world = Settings(row).World }, ct);
        log.LogWarning("Simulation world reset to revision {Revision} by {Actor}: analyzer source data wiped", row.WorldRevision, actor);
        return ToView(row);
    }

    /// <inheritdoc />
    public async Task<SimulationView> RequestScenarioAsync(string actor, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(cancellationToken);
        row.ScenarioRequest++;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(actor, "simulation_scenario_requested", new { row.ScenarioRequest }, cancellationToken);
        return ToView(row);
    }

    /// <inheritdoc />
    public async Task<SimulationAgentInstructions> GetInstructionsAsync(CancellationToken cancellationToken)
    {
        var row = await LoadAsync(cancellationToken);
        var view = ToView(row);
        var commands = await db.SimulationCommands.AsNoTracking()
            .Where(c => c.WorldRevision == row.WorldRevision && c.Status == EventComposer.Pending)
            .OrderBy(c => c.Id).Take(20).ToListAsync(cancellationToken);
        return new SimulationAgentInstructions(view.EventsOnly, view.Settings, view.World, view.ScenarioRequest,
            commands.Select(c => new SimulationAgentCommand(c.Id, c.Type, c.AccountId, c.ServiceId, JsonDocument.Parse(c.DataJson).RootElement.Clone())).ToList());
    }

    /// <inheritdoc />
    public async Task<SimulationCommandView> EnqueueCommandAsync(string type, long accountId, long? serviceId, IReadOnlyDictionary<string, object> data,
        string actor, CancellationToken cancellationToken)
    {
        if (!await db.CurAccounts.AnyAsync(a => a.Id == accountId, cancellationToken))
            throw new ComposerTargetException("accountId", $"Клиента {accountId} нет в текущих данных");
        if (serviceId is { } sid && !await db.CurServices.AnyAsync(s => s.Id == sid && s.OwnerId == accountId, cancellationToken))
            throw new ComposerTargetException("serviceId", $"Услуги {sid} нет у клиента {accountId}");
        var row = await LoadAsync(cancellationToken);
        var command = new SimulationCommandEntity
        {
            CreatedAt = time.GetUtcNow(), CreatedBy = actor, WorldRevision = row.WorldRevision, Type = type, AccountId = accountId, ServiceId = serviceId,
            DataJson = AppJson.Serialize(data), Status = EventComposer.Pending,
        };
        db.SimulationCommands.Add(command);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(actor, "simulation_event_composed", new { command.Id, type, accountId, serviceId }, cancellationToken);
        return ToView(command);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SimulationCommandView>> ListCommandsAsync(int limit, CancellationToken cancellationToken) =>
        (await db.SimulationCommands.AsNoTracking().OrderByDescending(c => c.Id).Take(limit).ToListAsync(cancellationToken)).Select(ToView).ToList();

    /// <inheritdoc />
    public async Task ReportCommandsAsync(IReadOnlyList<SimulationCommandResult> results, CancellationToken cancellationToken)
    {
        var ids = results.Select(r => r.Id).ToList();
        var rows = await db.SimulationCommands.Where(c => ids.Contains(c.Id) && c.Status == EventComposer.Pending).ToListAsync(cancellationToken);
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            var r = results.First(x => x.Id == row.Id);
            row.Status = r.Status == EventComposer.Applied ? EventComposer.Applied : EventComposer.Rejected;
            row.EventIdsJson = AppJson.Serialize(r.EventIds ?? []);
            row.Message = r.Message is { Length: > 500 } m ? m[..500] : r.Message;
            row.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static SimulationCommandView ToView(SimulationCommandEntity c) =>
        new(c.Id, c.CreatedAt, c.CreatedBy, c.Type, c.AccountId, c.ServiceId, JsonDocument.Parse(c.DataJson).RootElement.Clone(), c.Status,
            AppJson.Deserialize<List<string>>(c.EventIdsJson), c.Message, c.UpdatedAt);

    /// <inheritdoc />
    public async Task ReportAsync(SimulationAgentStatus status, CancellationToken cancellationToken)
    {
        var row = await LoadAsync(cancellationToken);
        row.AgentStatusJson = AppJson.Serialize(status);
        row.AgentReportedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnsureEmptyBaseAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EventsOnly || await snapshots.AnyAsync(cancellationToken)) return;
        var raw = Encoding.UTF8.GetBytes(AppJson.Serialize(new
        {
            schemaVersion = 1, sourceAsOf = EmptyBaseAsOf, sourceRevision = "events-only", accounts = Array.Empty<object>(),
            services = Array.Empty<object>(), transactions = Array.Empty<object>(),
        }));
        var parsed = SnapshotParser.Parse(raw);
        var provenance = AppJson.Serialize(new { via = "events-only", note = "пустая база: все данные приходят событиями" });
        var result = await snapshots.StoreAsync(parsed, raw, new SnapshotRequestMeta(EmptyBaseAsOf, false, "system", provenance, null), cancellationToken);
        log.LogInformation("Events-only mode: empty base {Outcome}, revision {Revision}", result.Outcome, result.Revision);
        signal.Notify();
    }

    // Данные источника и всё, что из них посчитано; пользователи, ревизии модели, ML-модели, задания пересчёта и аудит остаются.
    // DELETE, а не TRUNCATE: у роли приложения только права на данные. Сначала зависимые таблицы, затем основные.
    // Идентификаторы не сбрасываются: номера поколений и событий не повторяются после пересоздания
    private const string WipeSql = """
        DELETE FROM customer_results;
        DELETE FROM ml_shadow_predictions;
        DELETE FROM generations;
        DELETE FROM expiry_outcomes;
        DELETE FROM customer_activity_daily;
        DELETE FROM source_import_chunks;
        DELETE FROM source_imports;
        DELETE FROM source_events;
        DELETE FROM source_domain_events;
        DELETE FROM source_account_streams;
        DELETE FROM source_connectors;
        DELETE FROM observed_service_events;
        DELETE FROM cur_transactions;
        DELETE FROM cur_services;
        DELETE FROM cur_accounts;
        DELETE FROM src_transactions;
        DELETE FROM src_services;
        DELETE FROM src_accounts;
        DELETE FROM source_snapshots;
        """;

    private async Task<SimulationStateEntity> LoadAsync(CancellationToken cancellationToken)
    {
        var row = await db.SimulationState.SingleOrDefaultAsync(s => s.Id == 1, cancellationToken);
        if (row is not null) return row;
        var now = time.GetUtcNow();
        var defaults = SimulationSettings.Default;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO simulation_state (id, settings_json, revision, updated_at, world_revision, world_started_at, world_settings_json, scenario_request)
            VALUES (1, {AppJson.Serialize(defaults)}::jsonb, 1, {now}, 1, {Truncate(now)}, {AppJson.Serialize(defaults.World)}::jsonb, 0)
            ON CONFLICT (id) DO NOTHING
            """, cancellationToken);
        return await db.SimulationState.SingleAsync(s => s.Id == 1, cancellationToken);
    }

    private SimulationView ToView(SimulationStateEntity row)
    {
        var settings = Settings(row);
        var world = AppJson.Deserialize<WorldSettings>(row.WorldSettingsJson);
        var status = row.AgentStatusJson is { } s ? AppJson.Deserialize<SimulationAgentStatus>(s) : null;
        var online = row.AgentReportedAt is { } at && time.GetUtcNow() - at <= TimeSpan.FromSeconds(options.Value.AgentOnlineSeconds);
        return new SimulationView(options.Value.EventsOnly, settings, row.Revision, row.UpdatedAt, row.UpdatedBy,
            new SimulationWorldView(row.WorldRevision, row.WorldStartedAt, world, row.WorldResetAt, row.WorldResetBy), row.ScenarioRequest,
            new SimulationAgentView(online, row.AgentReportedAt, status), new SimulationPendingView(settings.World != world));
    }

    // Симуляция постоянная: прежняя пауза из сохранённых настроек не действует
    private static SimulationSettings Settings(SimulationStateEntity row) => AppJson.Deserialize<SimulationSettings>(row.SettingsJson) with { Running = true };

    private static DateTimeOffset Truncate(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
}
