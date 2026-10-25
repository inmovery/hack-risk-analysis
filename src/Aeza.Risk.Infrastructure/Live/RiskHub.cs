using System.Collections.Concurrent;
using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Core.Security;
using Aeza.Risk.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aeza.Risk.Infrastructure.Live;

/// <summary>Hub live-обновлений. Сервер только рассылает события с идентификаторами поколений (без PII); клиент по ним перечитывает данные через REST</summary>
[Authorize(Policy = Policies.Reader)]
public sealed class RiskHub(HubConnectionRegistry registry) : Hub
{
    /// <summary>Путь hub-а</summary>
    public const string Path = "/hubs/risk";

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        var sid = Context.User?.FindFirst(AuthClaims.SessionId)?.Value;
        if (sid is null)
        {
            Context.Abort();
            return Task.CompletedTask;
        }
        registry.Add(sid, Context);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        registry.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>Соединения этой реплики по сессиям: при logout/отзыве/истечении сессии соединения закрываются принудительно</summary>
public sealed class HubConnectionRegistry : ILiveSessions
{
    private readonly ConcurrentDictionary<string, (string Sid, string Hash, HubCallerContext Context)> _byConnection = new();

    /// <summary>Число открытых соединений</summary>
    public int Count => _byConnection.Count;

    /// <summary>Зарегистрировать соединение сессии</summary>
    public void Add(string sid, HubCallerContext ctx) => _byConnection[ctx.ConnectionId] = (sid, SessionStore.Hash(sid), ctx);

    /// <summary>Убрать соединение</summary>
    public void Remove(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    /// <inheritdoc />
    public int AbortSession(string sid) => AbortSessionHash(SessionStore.Hash(sid));

    /// <summary>Закрыть соединения сессии по её хешу (отзыв с другой реплики); возвращает их число</summary>
    public int AbortSessionHash(string hash)
    {
        var n = 0;
        foreach (var (id, (_, h, ctx)) in _byConnection)
        {
            if (h != hash) continue;
            ctx.Abort();
            _byConnection.TryRemove(id, out _);
            n++;
        }
        return n;
    }

    /// <summary>Сессии с открытыми соединениями</summary>
    public IReadOnlyCollection<string> ActiveSessionIds() => _byConnection.Values.Select(v => v.Sid).Distinct().ToList();
}

/// <summary>Закрывает live-соединения, чьи сессии истекли или отозваны: раз в 30 с и сразу по запросу (отзыв всех сессий пользователя)</summary>
public sealed class HubSessionSweeper(HubConnectionRegistry registry, ISessionStore sessions, ILogger<HubSessionSweeper> log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    /// <summary>Проверить соединения сейчас, не дожидаясь таймера</summary>
    public void RequestSweep()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _wake.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            foreach (var sid in registry.ActiveSessionIds())
            {
                try
                {
                    if (await sessions.IsValidAsync(sid, stoppingToken)) continue;
                    var n = registry.AbortSession(sid);
                    log.LogInformation("Closed {Count} live connections of an expired session", n);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogWarning(ex, "Session sweep failed");
                }
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
