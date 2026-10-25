using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

using Aeza.Risk.IntegrationTests.Base;

namespace Aeza.Risk.IntegrationTests;

public sealed class SecurityTests(PostgresFixture pg) : DbTestBase(pg)
{
    public static TheoryData<string, string> ProtectedEndpoints => new()
    {
        { "GET", "/api/v1/auth/me" },
        { "POST", "/api/v1/auth/logout" },
        { "GET", "/api/v1/customers" },
        { "GET", "/api/v1/customers/1" },
        { "GET", "/api/v1/analytics/summary" },
        { "GET", "/api/v1/model/settings" },
        { "PATCH", "/api/v1/model/settings" },
        { "PUT", "/api/v1/source/snapshot" },
        { "GET", "/api/v1/ml/status" },
        { "GET", "/api/v1/export/customers" },
        { "POST", "/hubs/risk/negotiate?negotiateVersion=1" },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task WithoutAuthentication_EveryRequiredEndpointIsClosed(string method, string path)
    {
        using var c = Factory.NewClient();
        var csrf = await c.GetCsrfAsync();
        var req = new HttpRequestMessage(new HttpMethod(method), path);
        req.Headers.Add("X-CSRF-TOKEN", csrf);
        if (method is "PATCH" or "PUT") req.Content = JsonContent.Create(new { });
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("example.test", body);
    }

    [Fact]
    public async Task SwaggerUi_IsPublic_LoginFromSwaggerOpensData()
    {
        // Документация открыта без входа (данных в ней нет); операции закрыты, вход — POST /auth/login из «Try it out».
        using var c = Factory.NewClient();
        var html = new HttpRequestMessage(HttpMethod.Get, "/api/v1/docs/index.html");
        html.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var ui = await c.SendAsync(html);
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/openapi.json")).StatusCode);
        var init = await c.GetAsync("/api/v1/docs/aeza-init.js");
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        // Бейдж validator.swagger.io: внешний запрос с адресом спецификации, запрещён CSP img-src — отключён.
        Assert.Contains("validatorUrl: null", await init.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/customers")).StatusCode);

        // Interceptor Swagger UI берёт X-CSRF-TOKEN из cookie XSRF-TOKEN, выданной при открытии страницы.
        var xsrf = Uri.UnescapeDataString(ui.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("XSRF-TOKEN=")).Split(';')[0]["XSRF-TOKEN=".Length..]);
        var login = await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AdminLogin, password = TestConst.AdminPassword }, xsrf);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_RequiresCsrf_AndFailuresAreIndistinguishable()
    {
        using var c = Factory.NewClient();
        var noCsrf = await c.PostAsJsonAsync("/api/v1/auth/login", new { login = TestConst.AdminLogin, password = TestConst.AdminPassword });
        Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);

        var csrf = await c.GetCsrfAsync();
        var wrongPassword = await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AdminLogin, password = "wrong-password!" }, csrf);
        var unknownLogin = await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = "nobody", password = "wrong-password!" }, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownLogin.StatusCode);
        Assert.Equal((await wrongPassword.JsonAsync())["title"]!.GetValue<string>(), (await unknownLogin.JsonAsync())["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Lockout_AfterFiveFailures_EvenCorrectPasswordIsRejected()
    {
        using var c = Factory.NewClient();
        var csrf = await c.GetCsrfAsync();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AnalystLogin, password = "bad-password-x" }, csrf)).StatusCode);
        var locked = await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AnalystLogin, password = TestConst.AnalystPassword }, csrf);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        Factory.Time.Advance(TimeSpan.FromMinutes(6));
        var after = await c.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AnalystLogin, password = TestConst.AnalystPassword }, csrf);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task CsrfIsEnforcedOnJsonWrites()
    {
        using var c = await Factory.LoginAsync();
        var rev = await c.ModelRevisionAsync();
        var noToken = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/model/settings") { Content = JsonContent.Create(new { baseRevision = rev, weights = new { waiting_payment = 41 } }) };
        c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(noToken)).StatusCode);
        var wrong = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/model/settings") { Content = JsonContent.Create(new { baseRevision = rev, weights = new { waiting_payment = 41 } }) };
        wrong.Headers.Add("X-CSRF-TOKEN", "forged-token");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(wrong)).StatusCode);
    }

    [Fact]
    public async Task AnalystCanRead_ButCannotWrite()
    {
        using var c = await Factory.LoginAsync(TestConst.AnalystLogin, TestConst.AnalystPassword);
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/model/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = 1, weights = new { waiting_payment = 41 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PutRawAsync("/api/v1/source/snapshot", "{}")).StatusCode);
    }

    [Fact]
    public async Task MachineTokens_AreScoped_AndMetricsArePublicAggregates()
    {
        using var c = Factory.NewClient();
        // /metrics — без авторизации: только агрегаты, без клиентов и настроек
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/metrics")).StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestConst.IngestToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/model/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/export/customers")).StatusCode);
        // Ingest-токен может загрузить снимок (без CSRF: Bearer не отправляется браузером автоматически)
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await c.PutRawAsync("/api/v1/source/snapshot", "{}")).StatusCode);

        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.PutRawAsync("/api/v1/source/snapshot", "{}")).StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesSessionOnServer_EvenIfCookieIsReplayed()
    {
        using var raw = Factory.NewClient();
        var csrf = await raw.GetCsrfAsync();
        var login = await raw.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AdminLogin, password = TestConst.AdminPassword }, csrf);
        var sessionCookie = login.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("aeza.session=")).Split(';')[0];
        var newCsrf = (await login.JsonAsync())["csrfToken"]!.GetValue<string>();
        Assert.Equal(HttpStatusCode.OK, (await raw.GetAsync("/api/v1/auth/me")).StatusCode);
        var logout = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout");
        logout.Headers.Add("X-CSRF-TOKEN", newCsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await raw.SendAsync(logout)).StatusCode);

        using var replay = Factory.CreateClient(new() { HandleCookies = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/me");
        req.Headers.Add("Cookie", sessionCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task SessionExpiry_ClosesAccess()
    {
        using var c = await Factory.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
        Factory.Time.Advance(TimeSpan.FromHours(9));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task UnknownApiRoute_IsJson404_NotSpaFallback()
    {
        using var c = await Factory.LoginAsync();
        var r = await c.GetAsync("/api/v1/does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("json", r.Content.Headers.ContentType?.MediaType ?? "");
    }

    [Fact]
    public async Task Hub_RequiresSession_AndDeliversGenerationPublished()
    {
        using var c = await Factory.LoginAsync();
        await AppFactory.WaitForGenerationAsync(c, s => s["generation"] is not null);
        var csrf = await c.GetCsrfAsync();
        using var raw = Factory.NewClient();
        var rawCsrf = await raw.GetCsrfAsync();
        var login = await raw.PostJsonWithCsrfAsync("/api/v1/auth/login", new { login = TestConst.AdminLogin, password = TestConst.AdminPassword }, rawCsrf);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith("aeza.session=")).Split(';')[0];

        var received = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(Factory.Server.BaseAddress, "/hubs/risk"), o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        hub.On<JsonObject>("generationPublished", e => received.TrySetResult(e));
        await hub.StartAsync();

        var rev = await c.ModelRevisionAsync();
        var patch = await c.PatchJsonAsync("/api/v1/model/settings", new { baseRevision = rev, weights = new { suspended_billing = 33 } });
        Assert.Equal(HttpStatusCode.Accepted, patch.StatusCode);
        var e = await received.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(rev + 1, e["modelRevision"]!.GetValue<long>());
        Assert.DoesNotContain("@", e.ToJsonString()); // никаких email в событиях
        Assert.NotNull(csrf);
    }
}
