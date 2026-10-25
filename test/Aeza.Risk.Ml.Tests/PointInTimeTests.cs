using System.Globalization;
using System.Text.Json;

using Aeza.Risk.Core;
using Aeza.Risk.SourceSimulator;

using Xunit;

namespace Aeza.Risk.Ml.Tests;

/// <summary>Набор point-in-time: строка решения в момент t не зависит от событий после t</summary>
public sealed class PointInTimeTests
{
    private static readonly DateTimeOffset End = new(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);

    internal static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aeza-ml-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void TruncatedJournal_GivesSameEarlierRows_FeaturesDoNotSeeFuture()
    {
        var full = TempDir();
        Dataset.Generate(new SimConfig { Customers = 200, Days = 150 }, 11, End, full, truthDaily: false);
        var cut = End.AddDays(-50);
        var truncated = TempDir();
        File.Copy(Path.Combine(full, Dataset.SnapshotFile), Path.Combine(truncated, Dataset.SnapshotFile));
        File.WriteAllLines(Path.Combine(truncated, Dataset.EventsFile), File.ReadLines(Path.Combine(full, Dataset.EventsFile)).Where(line =>
        {
            using var doc = JsonDocument.Parse(line);
            return DateTimeOffset.Parse(doc.RootElement.GetProperty("occurredAt").GetString()!, CultureInfo.InvariantCulture) < cut;
        }));

        var a = new PointInTimeBuilder().Build(new FileJournal(full));
        var b = new PointInTimeBuilder().Build(new FileJournal(truncated));

        Assert.True(b.Rows.Count > 300, $"мало строк до среза: {b.Rows.Count}");
        Assert.True(b.Positives > 20, $"мало «не продлено» до среза: {b.Positives}");
        Assert.True(a.Rows.Count > b.Rows.Count);
        var byKey = a.Rows.ToDictionary(r => (r.ServiceId, r.DecisionAt));
        foreach (var r in b.Rows)
        {
            Assert.True(r.DecisionAt < cut);
            Assert.True(byKey.TryGetValue((r.ServiceId, r.DecisionAt), out var same), $"решение {r.ServiceId}@{r.DecisionAt:o} пропало в полном журнале");
            Assert.Equal(same.Features, r.Features);
            Assert.Equal(same.RulesScore, r.RulesScore);
            Assert.Equal(same.RulesScoreWide, r.RulesScoreWide);
            Assert.Equal(same.Label, r.Label);
            Assert.Equal(same.ExpiresAt, r.ExpiresAt);
        }
        // Сроки без исхода к срезу — цензура (их нет в b), а не «продлено»; исход до E + N бывает, только если он уже случился (удаление, продление)
        Assert.True(b.Censored > 0);
    }

    [Fact]
    public void Dataset_HasNoIdentifiersInFeatures_AndRoundTripsThroughCsv()
    {
        var dir = TempDir();
        Dataset.Generate(new SimConfig { Customers = 120, Days = 90 }, 5, End, dir, truthDaily: false);
        var build = new PointInTimeBuilder().Build(new FileJournal(dir));
        var outDir = TempDir();
        DatasetFile.Write(build, outDir, "test", synthetic: true, graceDays: 5);
        var (rows, manifest) = DatasetFile.Read(outDir);

        Assert.Equal(MlFeatures.Schema, manifest["featureSchema"]!.GetValue<string>());
        Assert.Equal(build.Rows.Count, rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(build.Rows[i].Features, rows[i].Features);
            Assert.Equal(build.Rows[i].Label, rows[i].Label);
            Assert.Equal(build.Rows[i].RulesScore, rows[i].RulesScore);
        }
        Assert.DoesNotContain(MlFeatures.Names, n => n == "id" || n.EndsWith("_id", StringComparison.Ordinal) || n.StartsWith("id_", StringComparison.Ordinal)
                                                     || n.Contains("email", StringComparison.Ordinal));
        // Ни одна строка не размечена раньше, чем созрел её исход
        Assert.All(rows, r => Assert.True(r.DecisionAt < r.ExpiresAt));
    }
}
