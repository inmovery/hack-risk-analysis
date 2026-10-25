using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using Aeza.Risk.Infrastructure.Data;
using Aeza.Risk.IntegrationTests.Base;
using Aeza.Risk.SourceSimulator;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace Aeza.Risk.IntegrationTests;

/// <summary>Симулятор источника → анализатор на реальном PostgreSQL</summary>
public sealed class SimulatorDeliveryTests(PostgresFixture pg) : DbTestBase(pg)
{
    private static HttpClient Ingest(AppFactory f)
    {
        var c = f.NewClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestConst.IngestToken);
        return c;
    }

    private static OutboxSender Sender(HttpClient http, string dir, string runId) =>
        new(http, Path.Combine(dir, Dataset.EventsFile), Path.Combine(dir, "events.cursor"), runId,
            new SenderOptions { BatchSize = 250, MinDelay = TimeSpan.FromMilliseconds(50), MaxDelay = TimeSpan.FromSeconds(1) }, NullLogger.Instance);

    [Fact]
    public async Task History_IsDelivered_ExactlyOnce_AcrossAnalyzerAndSimulatorRestarts()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aeza-sim-it", Guid.NewGuid().ToString("N"));
        var manifest = Dataset.Generate(new SimConfig { Customers = 40, Days = 20 }, 99, TestConst.Now, dir, truthDaily: false);
        var runId = manifest["runId"]!.GetValue<string>();
        var end = Iso.Parse(manifest["end"]!.GetValue<string>());
        var lines = File.ReadAllLines(Path.Combine(dir, Dataset.EventsFile));
        var length = new FileInfo(Path.Combine(dir, Dataset.EventsFile)).Length;

        await Factory.DisposeAsync();
        var db = Pg.ConnectionString(Database + "_sim");
        Factory = new AppFactory(db, seedFile: Path.Combine(Path.GetTempPath(), "does-not-exist.json")); // пустая база анализатора
        using (var ingest = Ingest(Factory))
        {
            var first = Sender(ingest, dir, runId);
            Assert.True(await first.UploadSnapshotAsync(Path.Combine(dir, Dataset.SnapshotFile), CancellationToken.None));
            first.StopAfterBatches = 2; // симулятор «упал» после двух подтверждённых пакетов
            await first.RunAsync(() => length, () => end, stopWhenDrained: false, CancellationToken.None);
            Assert.Equal(500, first.Cursor.Events);
            Assert.Equal(0, first.Totals.Rejected);
        }

        // Перезапуск анализатора (та же база) и симулятора (позиция — из файла)
        await Factory.DisposeAsync();
        Factory = new AppFactory(db, seedFile: Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
        using (var ingest = Ingest(Factory))
        {
            var second = Sender(ingest, dir, runId);
            Assert.True(second.Cursor.SnapshotUploaded);
            Assert.Equal(500, second.Cursor.Events);
            await second.RunAsync(() => length, () => end, stopWhenDrained: true, CancellationToken.None);
            Assert.Equal(lines.Length, second.Cursor.Events);
            Assert.Equal(0, second.Totals.Rejected);
            Assert.Equal(0, second.Totals.Stale);
        }

        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var stored = await ctx.SourceDomainEvents.Where(e => e.Type != "stream.watermark").Select(e => e.EventId).ToListAsync();
        Assert.Equal(lines.Length, stored.Count);
        Assert.Equal(lines.Select(l => JsonNode.Parse(l)!["eventId"]!.GetValue<string>()).Order(), stored.Order());
        Assert.Equal(0, await ctx.SourceDomainEvents.CountAsync(e => !e.KnownType));
        Assert.Equal(0, await ctx.SourceEvents.CountAsync(e => e.Outcome == "stale"));
        Assert.Equal(0, await ctx.SourceAccountStreams.CountAsync(s => s.Missing.Length > 0)); // пропусков accountSeq нет
        var connector = await ctx.SourceConnectors.SingleAsync(c => c.Name == "http-push");
        Assert.Equal(end, connector.Watermark);
        // Водяные знаки — по ходу отправки (первый пакет, затем раз в минуту) и в конце; дублей событий нет
        var watermarks = await ctx.SourceDomainEvents.CountAsync(e => e.Type == "stream.watermark");
        Assert.InRange(watermarks, 1, 3);
        Assert.Equal(lines.Length + watermarks, connector.Events);

        // Расчёт по клиентам набора
        await AssertGenerationAsync();
    }

    [Fact]
    public async Task History_ViaImport_IsAppliedOnce_ThenWatermark_AndRepeatIsNoop()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aeza-sim-it", Guid.NewGuid().ToString("N"));
        var manifest = Dataset.Generate(new SimConfig { Customers = 30, Days = 15 }, 77, TestConst.Now, dir, truthDaily: false);
        var runId = manifest["runId"]!.GetValue<string>();
        var end = Iso.Parse(manifest["end"]!.GetValue<string>());
        var lines = File.ReadAllLines(Path.Combine(dir, Dataset.EventsFile));

        await Factory.DisposeAsync();
        Factory = new AppFactory(Pg.ConnectionString(Database + "_imp"), seedFile: Path.Combine(Path.GetTempPath(), "does-not-exist.json"));
        using var ingest = Ingest(Factory);
        Assert.True(await Sender(ingest, dir, runId).UploadSnapshotAsync(Path.Combine(dir, Dataset.SnapshotFile), CancellationToken.None));
        // Токен коннектора видит статус своего импорта — источник следит за прогрессом без входа в панель
        var view = await DatasetImporter.ImportAsync(ingest, dir, runId, end, NullLogger.Instance, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal("succeeded", view["status"]!.GetValue<string>());
        Assert.Equal(lines.Length, view["eventsTotal"]!.GetValue<long>());
        Assert.Equal(0, view["rejected"]!.GetValue<long>());
        Assert.Equal(0, view["stale"]!.GetValue<long>());

        // Повторный запуск: импорт уже выполнен (import.json) — файл не загружается заново
        var again = await DatasetImporter.ImportAsync(ingest, dir, runId, end, NullLogger.Instance, TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal(view["id"]!.GetValue<long>(), again["id"]!.GetValue<long>());

        using var scope = Factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(lines.Length, await ctx.SourceDomainEvents.CountAsync(e => e.Type != "stream.watermark"));
        Assert.Equal(1, await ctx.SourceImports.CountAsync());
        Assert.Equal(end, (await ctx.SourceConnectors.SingleAsync(c => c.Name == "http-push")).Watermark);
        Assert.True(await ctx.CustomerActivity.CountAsync() > 0);
        await AssertGenerationAsync();
    }

    private async Task AssertGenerationAsync()
    {
        using var admin = await Factory.LoginAsync();
        var gen = await AppFactory.WaitForGenerationAsync(admin, s => s["generation"]?["sourceSeq"]?.GetValue<long>() > 0, 60);
        Assert.Equal("stream", gen["generation"]!["sourceKind"]!.GetValue<string>());
        Assert.True(gen["customers"]!["total"]!.GetValue<int>() > 0);
    }
}
