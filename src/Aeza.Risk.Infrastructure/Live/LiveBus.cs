using System.Diagnostics;
using System.Text.Json;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Live;
using Aeza.Risk.Infrastructure.Metrics;
using Aeza.Risk.Infrastructure.Security;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Aeza.Risk.Infrastructure.Live;

/// <summary>Канал live-событий между репликами</summary>
public static class LiveChannel
{
    /// <summary>Канал NOTIFY/LISTEN</summary>
    public const string Name = "aeza_live";

    /// <summary>Отозвана сессия (payload: idHash — SHA-256 sid)</summary>
    public const string SessionRevoked = "sessionRevoked";

    /// <summary>Отозваны все сессии пользователя (payload: userId), например после set-password</summary>
    public const string UserSessionsRevoked = "userSessionsRevoked";

    /// <summary>Сериализация как у SignalR (camelCase): клиенты получают события в прежнем виде</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Отправить сообщение всем репликам</summary>
    public static async Task NotifyAsync<T>(NpgsqlDataSource db, string type, T payload, CancellationToken cancellationToken)
    {
        await using var cmd = db.CreateCommand("SELECT pg_notify($1, $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = Name });
        cmd.Parameters.Add(new NpgsqlParameter { Value = Serialize(type, payload) });
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Текст сообщения канала: {"type":..., "payload":...}</summary>
    public static string Serialize<T>(string type, T payload) => JsonSerializer.Serialize(new LiveMessage<T>(type, payload), Json);

    private sealed record LiveMessage<T>(string Type, T Payload);
}

/// <summary>Отправка live-событий в канал PostgreSQL</summary>
public sealed class PgLiveNotifier(NpgsqlDataSource db, ILogger<PgLiveNotifier> log) : ILiveNotifier
{
    /// <inheritdoc />
    public Task JobQueuedAsync(JobQueuedEvent queued, CancellationToken cancellationToken) => SendAsync(LiveEvents.JobQueued, queued, cancellationToken);

    /// <inheritdoc />
    public Task GenerationPublishedAsync(GenerationPublishedEvent published, CancellationToken cancellationToken) =>
        SendAsync(LiveEvents.GenerationPublished, published, cancellationToken);

    /// <inheritdoc />
    public Task RecalcFailedAsync(RecalcFailedEvent failed, CancellationToken cancellationToken) => SendAsync(LiveEvents.RecalcFailed, failed, cancellationToken);

    /// <inheritdoc />
    public Task SourceChangedAsync(SourceChangedEvent changed, CancellationToken cancellationToken) => SendAsync(LiveEvents.SourceChanged, changed, cancellationToken);

    private async Task SendAsync<T>(string type, T payload, CancellationToken cancellationToken)
    {
        try
        {
            await LiveChannel.NotifyAsync(db, type, payload, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Live event {Type} was not sent to the channel", type);
        }
    }
}

/// <summary>Строка подключения выделенного соединения LISTEN (без пула, с keepalive)</summary>
public sealed record ListenConnectionString(string Value);

/// <summary>Состояние канала этой реплики (для /health/ready)</summary>
public sealed class LiveBridgeState
{
    private long _downSince = Stopwatch.GetTimestamp();
    private volatile bool _listening;

    /// <summary>Канал слушается</summary>
    public bool Listening => _listening;

    /// <summary>Сколько канал недоступен (0 — слушается)</summary>
    public TimeSpan Down => _listening ? TimeSpan.Zero : Stopwatch.GetElapsedTime(Interlocked.Read(ref _downSince));

    internal void SetListening() => _listening = true;

    internal void SetDown()
    {
        if (!_listening) return;
        Interlocked.Exchange(ref _downSince, Stopwatch.GetTimestamp());
        _listening = false;
    }
}

/// <summary>Слушатель канала: события — клиентам hub-а этой реплики</summary>
public sealed class LiveBridge(
    ListenConnectionString listen, LiveBridgeState state, IHubContext<RiskHub> hub, HubConnectionRegistry registry,
    SessionStore sessions, HubSessionSweeper sweeper, IServiceProvider services, ILogger<LiveBridge> log) : BackgroundService
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(15);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var connectedBefore = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = new NpgsqlConnection(listen.Value);
                conn.Notification += (_, e) => _ = DispatchAsync(e.Payload);
                await conn.OpenAsync(stoppingToken);
                await using (var cmd = new NpgsqlCommand($"LISTEN {LiveChannel.Name}", conn))
                    await cmd.ExecuteNonQueryAsync(stoppingToken);
                state.SetListening();
                delay = TimeSpan.FromSeconds(1);
                if (connectedBefore)
                {
                    log.LogInformation("Live channel restored, resync");
                    await ResyncAsync();
                }
                connectedBefore = true;
                while (!stoppingToken.IsCancellationRequested) await conn.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                state.SetDown();
                log.LogWarning(ex, "Live channel lost, reconnecting in {Delay}", delay);
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxDelay.Ticks));
            }
        }
    }

    private async Task DispatchAsync(string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var type = doc.RootElement.GetProperty("type").GetString();
            var payload = doc.RootElement.GetProperty("payload").Clone();
            switch (type)
            {
                case LiveChannel.SessionRevoked:
                    var hash = payload.GetProperty("idHash").GetString() ?? "";
                    sessions.Evict(hash);
                    registry.AbortSessionHash(hash);
                    break;
                case LiveChannel.UserSessionsRevoked:
                    sessions.EvictAll();
                    sweeper.RequestSweep();
                    break;
                case LiveEvents.GenerationPublished:
                    services.GetService<BusinessMetricsState>()?.RequestRefresh();
                    await hub.Clients.All.SendAsync(type, payload);
                    break;
                case LiveEvents.JobQueued or LiveEvents.RecalcFailed or LiveEvents.SourceChanged:
                    await hub.Clients.All.SendAsync(type, payload);
                    break;
                default:
                    log.LogDebug("Unknown live message {Type}", type);
                    break;
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Live message dispatch failed");
        }
    }

    private async Task ResyncAsync()
    {
        sessions.EvictAll();
        sweeper.RequestSweep();
        services.GetService<BusinessMetricsState>()?.RequestRefresh();
        await hub.Clients.All.SendAsync(LiveEvents.Resync);
    }
}

/// <summary>Готовность реплики включает канал событий</summary>
public sealed class LiveChannelHealthCheck(LiveBridgeState state) : IHealthCheck
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(state.Down > Grace
            ? HealthCheckResult.Unhealthy("live_channel_down", data: new Dictionary<string, object> { ["status"] = "live_channel_down" })
            : HealthCheckResult.Healthy("live", new Dictionary<string, object> { ["status"] = "live" }));
}
