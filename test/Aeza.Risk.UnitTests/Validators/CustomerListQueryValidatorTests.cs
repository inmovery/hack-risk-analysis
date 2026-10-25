using Aeza.Risk.Feature.Customers.List;

using FluentAssertions;

using FluentValidation.TestHelper;

using Xunit;

namespace Aeza.Risk.UnitTests.Validators;

public class CustomerListQueryValidatorTests(ITestOutputHelper output) : BaseUnitTest(output)
{
    private readonly CustomerListQueryValidator _validator = new();

    private static CustomerListQuery Query(string? sortBy = null, string? sortDirection = null, string[]? riskLevel = null, string? scoreState = null,
        int? page = null, int? pageSize = null, string? search = null, string[]? fullChurnLevel = null, string? fullChurnState = null, string[]? paymentTrend = null) =>
        new(sortBy, sortDirection, riskLevel, scoreState, null, search, page, pageSize, null, fullChurnLevel, fullChurnState, paymentTrend);

    [Theory]
    [InlineData("fullChurnRisk", "desc")]
    [InlineData("paymentChange", "asc")]
    public void Should_Pass_For_Churn_Indicator_Sorts(string sortBy, string direction)
    {
        _validator.TestValidate(Query(sortBy, direction)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Should_Pass_For_Valid_Churn_Filters()
    {
        _validator.TestValidate(Query(fullChurnLevel: ["high", "medium"], fullChurnState: "churned", paymentTrend: ["decline", "insufficient_history"]))
            .ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Should_Reject_Unknown_Churn_Filters_With_CamelCase_Keys()
    {
        var result = _validator.TestValidate(Query(fullChurnLevel: ["huge"], fullChurnState: "gone", paymentTrend: ["down"]));
        result.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo("fullChurnLevel", "fullChurnState", "paymentTrend");
    }

    [Fact]
    public void Should_Pass_When_All_Parameters_Omitted()
    {
        _validator.TestValidate(Query()).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("id", "asc")]
    [InlineData("email", "desc")]
    [InlineData("balance", "asc")]
    [InlineData("totalReplenished", "desc")]
    [InlineData("riskScore", "desc")]
    [InlineData("atRisk", "asc")]
    public void Should_Pass_For_Every_Required_Sort(string sortBy, string direction)
    {
        _validator.TestValidate(Query(sortBy, direction)).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Should_Reject_Unknown_Sort_Field_With_CamelCase_Key()
    {
        var result = _validator.TestValidate(Query(sortBy: "email;drop table users"));
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be("sortBy");
    }

    [Theory]
    [InlineData("sideways")]
    [InlineData("")]
    public void Should_Reject_Unknown_Direction(string direction)
    {
        _validator.TestValidate(Query(sortDirection: direction)).Errors.Should().Contain(e => e.PropertyName == "sortDirection");
    }

    [Fact]
    public void Should_Reject_Unknown_Risk_Level_And_State()
    {
        var result = _validator.TestValidate(Query(riskLevel: ["high", "critical"], scoreState: "done"));
        result.Errors.Select(e => e.PropertyName).Should().BeEquivalentTo("riskLevel", "scoreState");
    }

    [Theory]
    [InlineData(0, 50, "page")]
    [InlineData(1, 0, "pageSize")]
    [InlineData(1, 201, "pageSize")]
    public void Should_Reject_Page_Out_Of_Range(int page, int pageSize, string property)
    {
        _validator.TestValidate(Query(page: page, pageSize: pageSize)).Errors.Should().ContainSingle().Which.PropertyName.Should().Be(property);
    }

    [Fact]
    public void Should_Reject_Too_Long_Search()
    {
        _validator.TestValidate(Query(search: new string('a', 321))).Errors.Should().ContainSingle().Which.PropertyName.Should().Be("search");
    }

    [Fact]
    public void ToCriteria_Applies_Defaults()
    {
        var criteria = Query().ToCriteria();
        criteria.SortBy.Should().Be("riskScore");
        criteria.Ascending.Should().BeFalse();
        criteria.Page.Should().Be(1);
        criteria.PageSize.Should().Be(50);
    }
}
