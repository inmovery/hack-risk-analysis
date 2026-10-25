using Aeza.Risk.Core;
using Aeza.Risk.Feature.Model;
using Aeza.Risk.Feature.Model.PatchSettings;

using FluentAssertions;

using FluentValidation.TestHelper;

using Xunit;

namespace Aeza.Risk.UnitTests.Validators;

public class SettingsPatchTests(ITestOutputHelper output) : BaseUnitTest(output)
{
    private readonly SettingsPatchValidator _validator = new();

    private static SettingsPatch Patch(long baseRevision = 1, ThresholdsPatch? thresholds = null, ClockPatch? clock = null, Dictionary<string, decimal>? weights = null) =>
        new(baseRevision, null, null, thresholds, clock, weights, null);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Should_Reject_Missing_Base_Revision(long baseRevision)
    {
        _validator.TestValidate(Patch(baseRevision)).ShouldHaveValidationErrorFor("baseRevision");
    }

    [Fact]
    public void Should_Pass_Valid_Base_Revision()
    {
        _validator.TestValidate(Patch(7)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Merge_Changes_Only_Passed_Fields_And_Drops_Unknown_Stored_Keys()
    {
        var current = ModelSettings.Default(FeatureCatalog.All) with
        {
            Weights = FeatureCatalog.All.ToDictionary(f => f.Key, f => f.DefaultWeight).Append(new KeyValuePair<string, decimal>("removed_feature", 10)).ToDictionary(),
        };
        var merged = ModelSettingsRules.Merge(current, Patch(weights: new() { ["waiting_payment"] = 41m }));

        merged.Weights["waiting_payment"].Should().Be(41m);
        merged.Weights.Should().NotContainKey("removed_feature");
        merged.HorizonDays.Should().Be(current.HorizonDays);
        merged.Thresholds.Should().Be(current.Thresholds);
        ModelSettingsRules.Validate(merged).Should().BeEmpty();
    }

    [Fact]
    public void Validate_Reports_Field_Paths_For_Invalid_Merge()
    {
        var current = ModelSettings.Default(FeatureCatalog.All);
        var merged = ModelSettingsRules.Merge(current, Patch(thresholds: new ThresholdsPatch(80, 60), clock: new ClockPatch("sundial", null)));

        ModelSettingsRules.Validate(merged).Keys.Should().Contain(["thresholds", "clock.mode"]);
    }
}
