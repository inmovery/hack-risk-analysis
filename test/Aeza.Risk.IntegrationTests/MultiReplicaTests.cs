using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Aeza.Risk.Core.Abstractions;
using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.IntegrationTests.Base;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Две реплики сервиса на одной БД</summary>
public sealed class MultiReplicaTests(PostgresFixture pg) : DbTestBase(pg)
{
    [Fact]
    public async Task GenerationPublished_ReachesHubClientsOfBothReplicas()
    {
        using var a = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(a, s => s["generation"] is not null);
        await using var b = SecondReplica();
        var (cookie, _) = await LoginRawAsync(Factory);

        var onA = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        var onB = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hubA = Hub(Factory, cookie);
        await using var hubB = Hub(b, cookie); // сессия создана на A: ключи DataProtection и сессии — в общей БД
        hubA.On<JsonObject>("generationPublished", e => onA.TrySetResult(e));
        hubB.On<JsonObject>("generationPublished", e => onB.TrySetResult(e));
        await hubA.StartAsync();
        await hubB.StartAsync();

        var rev = await a.ModelRevisionAsync();
        var patch = await a.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { suspended_billing = 31 } });
        Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);

        var ea = await onA.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var eb = await onB.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(rev + 1, ea["modelRevision"]!.GetValue<long>());
        Assert.Equal(ea["generationId"]!.GetValue<long>(), eb["generationId"]!.GetValue<long>());
    }

    [Fact]
    public async Task Logout_OnOneReplica_IsRejectedByAnotherWithoutWaitingForCache()
    {
        using var warm = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(warm, s => s["generation"] is not null);
        await using var b = SecondReplica();
        var (cookie, csrf) = await LoginRawAsync(Factory);
        using var onB = Raw(b);
        using var onA = Raw(Factory);

        Assert.Equal(HttpStatusCode.OK, (await onB.SendAsync(Get("/api/v1/auth/me", cookie))).StatusCode); // проверка закэширована на B на 15 с
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("Cookie", cookie);
        logout.Headers.Add("X-CSRF-TOKEN", csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await onA.SendAsync(logout)).StatusCode);

        // Отзыв приходит на B через канал за доли секунды; без канала B пускал бы ещё до 15 с
        Assert.True(await EventuallyAsync(async () => (await onB.SendAsync(Get("/api/v1/auth/me", cookie))).StatusCode == HttpStatusCode.Unauthorized,
            TimeSpan.FromSeconds(5)), "сессия, отозванная на A, должна отклоняться на B сразу");
    }

    [Fact]
    public async Task SetPassword_FromMigrator_RevokesSessionsOnRunningReplicas()
    {
        using var warm = await Factory.LoginAsync();
        await using var b = SecondReplica();
        var (cookie, _) = await LoginRawAsync(Factory, TestConst.AnalystLogin, TestConst.AnalystPassword);
        using var onB = Raw(b);
        Assert.Equal(HttpStatusCode.OK, (await onB.SendAsync(Get("/api/v1/auth/me", cookie))).StatusCode);

        // Тот же код, что у команды migrator set-password (отдельный процесс с собственным подключением к БД)
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var sp = scope.ServiceProvider;
            var admin = new UserAdministration(sp.GetRequiredService<IUserRepository>(), sp.GetRequiredService<IPasswordHasher>(),
                TimeProvider.System, sp.GetRequiredService<AppDbContext>(), NullLogger<UserAdministration>.Instance);
            Assert.Equal(0, await admin.SetPasswordAsync(TestConst.AnalystLogin, "another-analyst-pass-1", CancellationToken.None));
        }

        Assert.True(await EventuallyAsync(async () => (await onB.SendAsync(Get("/api/v1/auth/me", cookie))).StatusCode == HttpStatusCode.Unauthorized,
            TimeSpan.FromSeconds(5)), "сессии пользователя после set-password должны отклоняться всеми репликами сразу");
    }

    [Fact]
    public async Task LoginLockout_IsSharedAcrossReplicas()
    {
        using var warm = await Factory.LoginAsync();
        await using var b = SecondReplica();
        using var onA = Factory.NewClient();
        for (var i = 0; i < 5; i++) // Auth:MaxFailedAttempts = 5
        {
            var r = await onA.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AnalystLogin, password = "wrong-password" }, await onA.GetCsrfAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        }

        // Верный пароль на другой реплике: пара (логин, IP) уже заблокирована — ответ тот же 401
        using var onB = b.NewClient();
        var locked = await onB.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AnalystLogin, password = TestConst.AnalystPassword }, await onB.GetCsrfAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        // Блокируется только эта пара: другой логин входит
        var other = await onB.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AdminLogin, password = TestConst.AdminPassword }, await onB.GetCsrfAsync());
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        // В БД — только хеш ключа: ни логина, ни IP
        await using var conn = new NpgsqlConnection(Pg.ConnectionString(Database));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT key_hash FROM login_throttle", conn);
        var keys = new List<string>();
        await using (var rd = await cmd.ExecuteReaderAsync())
            while (await rd.ReadAsync()) keys.Add(rd.GetString(0));
        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.Matches("^[0-9a-f]{64}$", k));
    }

    [Fact]
    public async Task ListenConnectionLost_ReplicaReconnects_SendsResync_AndKeepsDeliveringEvents()
    {
        using var a = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(a, s => s["generation"] is not null);
        var (cookie, _) = await LoginRawAsync(Factory);
        var resync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = Hub(Factory, cookie);
        hub.On("resync", () => resync.TrySetResult());
        hub.On<JsonObject>("generationPublished", e => published.TrySetResult(e));
        await hub.StartAsync();

        // Обрыв соединения LISTEN этой БД (как при рестарте или failover PostgreSQL)
        await using (var conn = new NpgsqlConnection(Pg.ConnectionString(Database)))
        {
            await conn.OpenAsync();
            await using var kill = new NpgsqlCommand(
                "SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE application_name = 'aeza-live' AND datname = current_database()", conn);
            Assert.True((long)(await kill.ExecuteScalarAsync())! >= 1);
        }
        await resync.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var rev = await a.ModelRevisionAsync();
        Assert.Equal(HttpStatusCode.Accepted, (await a.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { suspended_billing = 29 } })).StatusCode);
        var e = await published.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(rev + 1, e["modelRevision"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/health/ready")).StatusCode);
    }

    /// <summary>Вторая реплика: свой хост (кэш, hub, worker), та же БД</summary>
    private AppFactory SecondReplica() => new(Pg.ConnectionString(Database));

    private static HubConnection Hub(AppFactory f, string cookie) => new HubConnectionBuilder()
        .WithUrl(new Uri(f.Server.BaseAddress, "/hubs/risk"), o =>
        {
            o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
            o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
            o.Headers["Cookie"] = cookie;
        })
        .Build();

    private static HttpClient Raw(AppFactory f) => f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, AllowAutoRedirect = false });

    private static HttpRequestMessage Get(string url, string cookie)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", cookie);
        return req;
    }

    /// <summary>Вход без контейнера cookie: заголовок Cookie переносится на другую реплику как есть</summary>
    private static async Task<(string Cookie, string Csrf)> LoginRawAsync(AppFactory f, string login = TestConst.AdminLogin, string password = TestConst.AdminPassword)
    {
        using var c = Raw(f);
        var cookies = new Dictionary<string, string>();
        var first = await c.GetAsync("/api/v1/auth/csrf");
        Merge(first, cookies);
        var token = (await first.Content.ReadFromJsonAsync<JsonObject>())!["token"]!.GetValue<string>();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { login, password }) };
        req.Headers.Add("X-CSRF-TOKEN", token);
        req.Headers.Add("Cookie", Header(cookies));
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Merge(r, cookies);
        var csrf = (await r.Content.ReadFromJsonAsync<JsonObject>())!["csrfToken"]!.GetValue<string>();
        return (Header(cookies), csrf);
    }

    private static void Merge(HttpResponseMessage r, Dictionary<string, string> cookies)
    {
        if (!r.Headers.TryGetValues("Set-Cookie", out var values)) return;
        foreach (var v in values)
        {
            var pair = v.Split(';')[0];
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0) cookies[pair[..eq]] = pair[(eq + 1)..];
        }
    }

    private static string Header(Dictionary<string, string> cookies) => string.Join("; ", cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    private static async Task<bool> EventuallyAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }
        return await condition();
    }
}
