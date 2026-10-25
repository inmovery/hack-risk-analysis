using Aeza.Risk.Core;
using Xunit;

namespace Aeza.Risk.UnitTests;

public sealed class SettingsValidationTests
{
    private static readonly ModelSettings Defaults = ModelSettings.Default(FeatureCatalog.All);

    private static IReadOnlyDictionary<string, string[]> Validate(ModelSettings s) => ModelSettingsValidator.Validate(s, FeatureCatalog.All.ToList());

    [Fact]
    public void Defaults_AreValid() => Assert.Empty(Validate(Defaults));

    [Fact]
    public void NegativeOrTooLargeWeight_IsRejected()
    {
        Assert.Contains("weights.no_autoprolong", Validate(Defaults with { Weights = With("no_autoprolong", -1m) }).Keys);
        Assert.Contains("weights.no_autoprolong", Validate(Defaults with { Weights = With("no_autoprolong", 100.5m) }).Keys);
        Assert.Contains("weights.no_autoprolong", Validate(Defaults with { Weights = With("no_autoprolong", 10.123m) }).Keys);
    }

    [Fact]
    public void AllZeroWeights_AreRejected()
    {
        var zero = Defaults.Weights.ToDictionary(k => k.Key, _ => 0m);
        Assert.Contains("weights", Validate(Defaults with { Weights = zero }).Keys);
    }

    [Fact]
    public void UnknownFeatureKey_IsRejected() =>
        Assert.Contains("weights.churn_probability", Validate(Defaults with { Weights = With("churn_probability", 10m) }).Keys);

    [Theory]
    [InlineData(60, 30)]
    [InlineData(30, 30)]
    [InlineData(0, 60)]
    [InlineData(30, 101)]
    public void ThresholdInversionOrOutOfRange_IsRejected(int medium, int high) =>
        Assert.NotEmpty(Validate(Defaults with { Thresholds = new Thresholds(medium, high) }));

    [Fact]
    public void WindowLongerThanHorizon_IsRejected() =>
        Assert.Contains("urgencyWindowDays", Validate(Defaults with { HorizonDays = 5, UrgencyWindowDays = 7 }).Keys);

    [Fact]
    public void Level_UsesUnroundedScore_AtBoundaries()
    {
        Assert.Equal(RiskLevel.Low, Defaults.LevelOf(29.999999m));
        Assert.Equal(RiskLevel.Medium, Defaults.LevelOf(30m));
        Assert.Equal(RiskLevel.Medium, Defaults.LevelOf(59.9999m));
        Assert.Equal(RiskLevel.High, Defaults.LevelOf(60m));
        Assert.Equal(RiskLevel.Unknown, Defaults.LevelOf(null));
    }

    private static IReadOnlyDictionary<string, decimal> With(string key, decimal value)
    {
        var d = Defaults.Weights.ToDictionary(k => k.Key, k => k.Value);
        d[key] = value;
        return d;
    }
}
