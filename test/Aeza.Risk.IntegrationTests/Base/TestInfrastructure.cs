using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Aeza.Risk.Web;

using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(Aeza.Risk.IntegrationTests.Base.PostgresFixture))]

namespace Aeza.Risk.IntegrationTests.Base;

/// <summary>Один реальный PostgreSQL 17 (Testcontainers) на прогон; каждый тест-класс получает свою базу</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17.11-alpine").WithDatabase("postgres").WithUsername("aeza").WithPassword("test-password").WithCommand("-c", "max_connections=400").Build();

    public string ConnectionString(string database) =>
        new Npgsql.NpgsqlConnectionStringBuilder(_pg.GetConnectionString()) { Database = database, MaxPoolSize = 8 }.ConnectionString;

    public async ValueTask InitializeAsync() => await _pg.StartAsync();
    public async ValueTask DisposeAsync() => await _pg.DisposeAsync();
}

public static class TestConst
{
    public const string AdminLogin = "admin";
    public const string AdminPassword = "integration-admin-pass";
    public const string AnalystLogin = "analyst";
    public const string AnalystPassword = "integration-analyst-pass";
    public const string IngestToken = "integration-ingest-token";
    public static readonly DateTimeOffset SourceAsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset Now = new(2026, 10, 19, 16, 0, 0, TimeSpan.Zero);
    public static string OrganizerData => Path.Combine(AppContext.BaseDirectory, "organizer", "data.json");
}

/// <summary>Приложение целиком (реальный хост, worker, SignalR) поверх реальной БД; время фиксировано FakeTimeProvider</summary>
public sealed class AppFactory(
    string connectionString, FakeTimeProvider? time = null, string? seedFile = null, IReadOnlyDictionary<string, string>? settings = null,
    Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    public FakeTimeProvider Time { get; } = time ?? new FakeTimeProvider(TestConst.Now);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("App:DbConnectionString", connectionString);
        builder.UseSetting("App:AutoMigrate", "true");
        builder.UseSetting("Auth:AdminLogin", TestConst.AdminLogin);
        builder.UseSetting("Auth:AdminPassword", TestConst.AdminPassword);
        builder.UseSetting("Auth:AnalystLogin", TestConst.AnalystLogin);
        builder.UseSetting("Auth:AnalystPassword", TestConst.AnalystPassword);
        builder.UseSetting("Auth:IngestToken", TestConst.IngestToken);
        builder.UseSetting("Auth:LoginPerMinute", "1000");
        builder.UseSetting("Seed:File", seedFile ?? TestConst.OrganizerData);
        builder.UseSetting("Seed:SourceAsOf", TestConst.SourceAsOf.ToString("O"));
        builder.UseSetting("Seed:Label", "organizer:test_data_yandex");
        builder.UseSetting("Recalc:PollSeconds", "1");
        builder.UseSetting("Events:DebounceSeconds", "0"); // FakeTimeProvider не идёт сам: debounce проверяется отдельно
        // OpenTelemetry слушает Meter по имени во всём процессе: у параллельных тестовых хостов имена разные, иначе /metrics смешает их значения
        builder.UseSetting("OpenTelemetry:MeterName", $"Aeza.Risk.Test.{Guid.NewGuid():N}");
        foreach (var (key, value) in settings ?? new Dictionary<string, string>()) builder.UseSetting(key, value);
        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(Time);
            services?.Invoke(s);
        });
    }

    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    public async Task<HttpClient> LoginAsync(string login = TestConst.AdminLogin, string password = TestConst.AdminPassword)
    {
        var c = NewClient();
        var csrf = await c.GetCsrfAsync();
        var r = await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login, password }, csrf);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonObject>();
        c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", body!["csrfToken"]!.GetValue<string>());
        return c;
    }

    /// <summary>Ждёт публикации поколения, удовлетворяющего условию (worker асинхронный)</summary>
    public static async Task<JsonObject> WaitForGenerationAsync(HttpClient c, Func<JsonObject, bool> until, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        JsonObject? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var r = await c.GetAsync("/api/v1/analytics/summary");
            if (r.StatusCode == HttpStatusCode.OK)
            {
                last = (await r.Content.ReadFromJsonAsync<JsonObject>())!;
                if (until(last)) return last;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Generation condition not met; last: {last?["generation"]?.ToJsonString()}");
    }
}

public static class HttpExtensions
{
    public static async Task<string> GetCsrfAsync(this HttpClient c)
    {
        var r = await c.GetFromJsonAsync<JsonObject>("/api/v1/auth/csrf");
        return r!["token"]!.GetValue<string>();
    }

    public static Task<HttpResponseMessage> PostJsonWithCsrfAsync(this HttpClient c, string url, object body, string csrf)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        return c.SendAsync(req);
    }

    public static Task<HttpResponseMessage> PatchJsonAsync(this HttpClient c, string url, object body) =>
        c.SendAsync(new HttpRequestMessage(HttpMethod.Patch, url) { Content = JsonContent.Create(body) });

    public static Task<HttpResponseMessage> PutRawAsync(this HttpClient c, string url, string json, Action<HttpRequestMessage>? configure = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        configure?.Invoke(req);
        return c.SendAsync(req);
    }

    public static async Task<JsonObject> JsonAsync(this HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonObject>())!;

    public static async Task<long> ModelRevisionAsync(this HttpClient c)
    {
        var s = await c.GetFromJsonAsync<JsonObject>("/api/v1/model/settings");
        return s!["latest"]!["revision"]!.GetValue<long>();
    }
}

/// <summary>Своя БД на тест-класс; приложение поднимается в каждом тесте класса по требованию</summary>
public abstract class DbTestBase(PostgresFixture pg) : IAsyncLifetime
{
    protected readonly string Database = "t_" + Guid.NewGuid().ToString("N")[..16];
    protected PostgresFixture Pg => pg;
    protected AppFactory Factory = null!;
    /// <summary>Дополнительная конфигурация приложения тест-класса (например, режим раздельных доменов)</summary>
    protected virtual IReadOnlyDictionary<string, string>? Settings => null;
    /// <summary>Подмена сервисов тест-класса</summary>
    protected virtual void ConfigureServices(IServiceCollection services) { }

    public virtual async ValueTask InitializeAsync()
    {
        Factory = new AppFactory(pg.ConnectionString(Database), settings: Settings, services: ConfigureServices);
        using var c = Factory.NewClient();
        await Task.CompletedTask;
    }

    public virtual async ValueTask DisposeAsync() => await Factory.DisposeAsync();

    protected static JsonDocument OrganizerJson() => JsonDocument.Parse(File.ReadAllText(TestConst.OrganizerData));
}
