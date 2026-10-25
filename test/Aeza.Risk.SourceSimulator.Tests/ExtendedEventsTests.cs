using System.Text;
using System.Text.Json.Nodes;

using Aeza.Risk.Core.Source;

using Xunit;

using static Aeza.Risk.SourceSimulator.Tests.GenerationTests;

namespace Aeza.Risk.SourceSimulator.Tests;

/// <summary>Дополнительные события: выключены — snapshot системы прежний; включены — сигналы переезда, опрос при уходе и контрольная группа удержания</summary>
public sealed class ExtendedEventsTests
{
    private static SimConfig Extended() => new()
    {
        Customers = 300, Days = 365,
        Extended = new ExtendedConfig { MigrationSignals = true, ExitSurveyResponse = 0.5, RetentionHoldoutShare = 0.3, HeterogeneousRetention = true },
    };

    [Fact]
    public void Off_ConfigJsonHasNoExtendedSection_SoEarlierWorldsKeepTheirRunId()
    {
        Assert.DoesNotContain("extended", new SimConfig().ToJson(), StringComparison.Ordinal);
        Assert.Contains("\"extended\"", Extended().ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void On_EmitsNewEvents_AllKnownToAnalyzer_SurveyOnlyAfterLoss_HoldoutGetsNoOffer()
    {
        var dir = TempDir();
        Dataset.Generate(Extended(), 21, End, dir, truthDaily: false);
        var lines = File.ReadAllLines(Path.Combine(dir, Dataset.EventsFile));
        foreach (var chunk in lines.Chunk(1000))
        {
            var batch = EventBatchParser.Parse(Encoding.UTF8.GetBytes("{\"synthetic\":true,\"events\":[" + string.Join(',', chunk) + "]}"), End.AddMinutes(1));
            Assert.True(batch.IsValid);
            Assert.False(batch.Warnings.ContainsKey("unknown_event_type"));
            Assert.All(batch.Events, e => Assert.True(e.IsValid));
        }

        var events = lines.Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        List<JsonObject> Of(string type) => events.Where(e => e["type"]!.GetValue<string>() == type).ToList();
        Assert.NotEmpty(Of("service.backup_downloaded"));
        Assert.Contains(Of("dns.records_changed"), e => e["data"]!["target"]!.GetValue<string>() == "external");
        Assert.NotEmpty(Of("retention.holdout"));
        Assert.NotEmpty(Of("exit.survey_answered"));

        // Опрос приходит только от клиента, который уже потерял все услуги
        var lost = File.ReadLines(Path.Combine(dir, Dataset.TruthFile)).Select(l => JsonNode.Parse(l)!.AsObject())
            .Where(t => t["k"]!.GetValue<string>() == "lost_all_services")
            .GroupBy(t => t["a"]!.GetValue<long>()).ToDictionary(g => g.Key, g => g.Select(t => Iso.Parse(t["at"]!.GetValue<string>())).Min());
        foreach (var s in Of("exit.survey_answered"))
        {
            var account = s["accountId"]!.GetValue<long>();
            Assert.True(lost.TryGetValue(account, out var at) && at <= Iso.Parse(s["occurredAt"]!.GetValue<string>()), $"опрос без ухода: {account}");
        }

        // Контрольная группа: предложение с тем же номером не отправляется
        var sent = Of("retention.offer_sent").Select(e => e["data"]!["offerId"]!.GetValue<long>()).ToHashSet();
        Assert.All(Of("retention.holdout"), h => Assert.DoesNotContain(h["data"]!["offerId"]!.GetValue<long>(), sent));
    }
}
