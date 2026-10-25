using System.Net;
using System.Net.Http.Headers;
using Xunit;

using Aeza.Risk.IntegrationTests.Base;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Раздельные домены: этот хост — только API (без панели), панель на другом origin ходит сюда через CORS с cookie</summary>
public sealed class SplitDomainTests(PostgresFixture pg) : DbTestBase(pg)
{
    private const string Front = "https://app.example.test";
    private const string Evil = "https://evil.example.test";

    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string>
    {
        ["CorsOrigins:0"] = Front,
        ["Spa:Enabled"] = "false",
    };

    [Fact]
    public async Task ApiHost_ServesNoPanel_RootOpensSwagger()
    {
        using var c = Factory.NewClient();
        var root = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, root.StatusCode);
        Assert.Equal("/api/v1/docs", root.Headers.Location!.OriginalString);
        foreach (var path in new[] { "/customers", "/login", "/index.html" })
            Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync(path)).StatusCode);

        var html = new HttpRequestMessage(HttpMethod.Get, "/api/v1/docs");
        html.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var docs = await c.SendAsync(html);
        Assert.True(docs.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect, $"docs → {docs.StatusCode}");
        Assert.DoesNotContain("/login", docs.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/docs/index.html")).StatusCode);
    }

    [Fact]
    public async Task Cors_AllowsOnlyConfiguredFrontOrigin_WithCredentials()
    {
        using var c = Factory.NewClient();
        var preflight = Preflight("/api/v1/auth/login", Front);
        var ok = await c.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(Front, ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", ok.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Contains("x-csrf-token", ok.Headers.GetValues("Access-Control-Allow-Headers").Single().ToLowerInvariant());

        var denied = await c.SendAsync(Preflight("/api/v1/auth/login", Evil));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));

        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf");
        get.Headers.Add("Origin", Front);
        var r = await c.SendAsync(get);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(Front, r.Headers.GetValues("Access-Control-Allow-Origin").Single());
    }

    [Fact]
    public async Task Hub_RejectsForeignOrigin()
    {
        // WebSocket-рукопожатие CORS не проверяет — hub сам отклоняет чужой origin.
        using var c = Factory.NewClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(Negotiate(Evil))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(Negotiate(Front))).StatusCode); // свой origin, но без входа
    }

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Options, path);
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        return req;
    }

    private static HttpRequestMessage Negotiate(string origin)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/hubs/risk/negotiate?negotiateVersion=1");
        req.Headers.Add("Origin", origin);
        return req;
    }
}

/// <summary>Docs:RequireAuth=true: документация доступна только после входа (браузер на хосте с панелью → форма входа)</summary>
public sealed class ClosedDocsTests(PostgresFixture pg) : DbTestBase(pg)
{
    protected override IReadOnlyDictionary<string, string> Settings => new Dictionary<string, string> { ["Docs:RequireAuth"] = "true" };

    [Fact]
    public async Task Docs_RequireLogin_WhenConfigured()
    {
        using var anon = Factory.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/v1/docs/index.html")).StatusCode);
        var html = new HttpRequestMessage(HttpMethod.Get, "/api/v1/docs/index.html");
        html.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var r = await anon.SendAsync(html);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/login?returnUrl=" + Uri.EscapeDataString("/api/v1/docs/index.html"), r.Headers.Location!.OriginalString);

        using var c = await Factory.LoginAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/openapi.json")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/v1/docs/index.html")).StatusCode);
    }
}
