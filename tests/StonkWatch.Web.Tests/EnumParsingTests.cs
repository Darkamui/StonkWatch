using StonkWatch.Web.Data;

namespace StonkWatch.Web.Tests;

/// <summary>
/// The loose-parsing contract is what lets MCP tools accept natural-language values
/// ("high priority, near trigger") without exact C# casing. These tests lock it in.
/// </summary>
public class EnumParsingTests
{
    [Theory]
    [InlineData("NearTrigger")]
    [InlineData("neartrigger")]
    [InlineData("near trigger")]
    [InlineData("NEAR TRIGGER")]
    [InlineData("near-trigger")]
    [InlineData("NEAR_TRIGGER")]
    [InlineData("Near-Trigger")]
    [InlineData("  near trigger  ")]
    public void ParseOrDefault_accepts_loose_formatting(string raw)
    {
        Assert.Equal(
            CandidateStatus.NearTrigger,
            EnumParsing.ParseOrDefault(raw, CandidateStatus.Idea));
    }

    [Theory]
    [InlineData("high", Priority.High)]
    [InlineData("MEDIUM", Priority.Medium)]
    [InlineData("Low", Priority.Low)]
    public void ParseOrDefault_is_case_insensitive(string raw, Priority expected)
    {
        Assert.Equal(expected, EnumParsing.ParseOrDefault(raw, Priority.Medium));
    }

    [Fact]
    public void ParseOrDefault_returns_current_when_raw_is_null()
    {
        Assert.Equal(
            CandidateStatus.Ready,
            EnumParsing.ParseOrDefault(null, CandidateStatus.Ready));
    }

    [Fact]
    public void ParseOrDefault_throws_on_unknown_value()
    {
        var ex = Assert.Throws<ValidationException>(
            () => EnumParsing.ParseOrDefault("banana", CandidateStatus.Idea));

        // The message must list the valid options — it is what an MCP client sees.
        Assert.Contains("banana", ex.Message);
        Assert.Contains(nameof(CandidateStatus.NearTrigger), ex.Message);
    }

    [Fact]
    public void ParseNullableOrDefault_returns_current_when_raw_is_null()
    {
        Assert.Equal(
            Conviction.A,
            EnumParsing.ParseNullableOrDefault<Conviction>(null, Conviction.A));
    }

    [Fact]
    public void ParseNullableOrDefault_clears_on_empty_string()
    {
        Assert.Null(EnumParsing.ParseNullableOrDefault<Conviction>("", Conviction.A));
    }

    [Fact]
    public void ParseNullableOrDefault_parses_a_value()
    {
        Assert.Equal(
            Conviction.B,
            EnumParsing.ParseNullableOrDefault<Conviction>("b", Conviction.A));
    }

    [Fact]
    public void ParseNullableOrDefault_throws_on_unknown_value()
    {
        Assert.Throws<ValidationException>(
            () => EnumParsing.ParseNullableOrDefault<Conviction>("Z", null));
    }

    [Theory]
    [InlineData("complete", DataQuality.Complete)]
    [InlineData("PARTIAL", DataQuality.Partial)]
    [InlineData("un available", DataQuality.Unavailable)]
    public void ParseOrDefault_handles_every_loose_enum_used_by_mcp(string raw, DataQuality expected)
    {
        Assert.Equal(expected, EnumParsing.ParseOrDefault(raw, DataQuality.Unavailable));
    }

    [Theory]
    [InlineData("improved", ThesisImpact.Improved)]
    [InlineData("un-changed", ThesisImpact.Unchanged)]
    [InlineData("WEAKENED", ThesisImpact.Weakened)]
    [InlineData("Invalidated", ThesisImpact.Invalidated)]
    public void ParseOrDefault_handles_thesis_impact(string raw, ThesisImpact expected)
    {
        Assert.Equal(expected, EnumParsing.ParseOrDefault(raw, ThesisImpact.Unchanged));
    }
}
