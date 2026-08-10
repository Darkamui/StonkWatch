using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;
using StonkWatch.Web.Services.Monitoring;

namespace StonkWatch.Web.Tests;

/// <summary>
/// The evaluator decides whether a real alert email goes out, so every branch is pinned here.
/// </summary>
public class LevelEvaluatorTests
{
    /// <summary>
    /// A candidate with the full ladder, in the order a real thesis stacks them:
    /// invalidation 42, secondary support 44–46, primary support 52–55, reclaims 59 and 63,
    /// targets 70 and 80.
    /// </summary>
    private static Candidate FullyLevelled() => new()
    {
        Ticker = "ASTS",
        Invalidation = 42m,
        SupportLow = 52m,
        SupportHigh = 55m,
        SecondarySupportLow = 44m,
        SecondarySupportHigh = 46m,
        ReclaimTrigger1 = 59m,
        ReclaimTrigger2 = 63m,
        T1 = 70m,
        T2 = 80m
    };

    private static string[] KeysFrom(IEnumerable<LevelCrossing> crossings) =>
        crossings.Select(c => c.Key).ToArray();

    // ---------- First run ----------

    [Fact]
    public void No_previous_price_never_crosses()
    {
        // Otherwise deploying the worker fires every already-breached level at once.
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: null, current: 10m));
    }

    [Fact]
    public void No_previous_price_never_crosses_even_far_above_every_level()
    {
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: null, current: 999m));
    }

    // ---------- Downward levels ----------

    [Fact]
    public void Breaking_invalidation_crosses()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 43m, current: 41m);

        var crossing = Assert.Single(crossings);
        Assert.Equal(LevelKeys.Invalidation, crossing.Key);
        Assert.Equal(AlertType.Invalidation, crossing.Level.Type);
        Assert.Equal(CrossingDirection.Down, crossing.Level.Direction);
        Assert.Equal(42m, crossing.Level.Value);
        Assert.Equal(41m, crossing.Price);
        Assert.Equal(43m, crossing.PreviousPrice);
    }

    [Fact]
    public void Touching_invalidation_exactly_crosses()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 43m, current: 42m);

        Assert.Equal(LevelKeys.Invalidation, Assert.Single(crossings).Key);
    }

    [Fact]
    public void Entering_the_support_zone_from_above_crosses_at_the_upper_bound()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 57m, current: 54m);

        var crossing = Assert.Single(crossings);
        Assert.Equal(LevelKeys.SupportZone, crossing.Key);
        Assert.Equal(55m, crossing.Level.Value);
    }

    [Fact]
    public void Touching_the_top_of_the_support_zone_crosses()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 57m, current: 55m);

        Assert.Equal(LevelKeys.SupportZone, Assert.Single(crossings).Key);
    }

    [Fact]
    public void Blowing_straight_through_the_support_zone_still_crosses_it()
    {
        // 57 -> 50 skips the whole 52-55 zone. The support alert must still fire.
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 57m, current: 50m);

        Assert.Equal([LevelKeys.SupportZone], KeysFrom(crossings));
    }

    [Fact]
    public void Already_inside_the_support_zone_does_not_re_cross()
    {
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: 54m, current: 53m));
    }

    [Fact]
    public void Rising_out_of_the_support_zone_does_not_cross_it()
    {
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: 53m, current: 57m));
    }

    [Fact]
    public void Falling_into_secondary_support_crosses_only_secondary()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 50m, current: 45m);

        Assert.Equal([LevelKeys.SecondarySupportZone], KeysFrom(crossings));
    }

    // ---------- Upward levels ----------

    [Fact]
    public void Reclaiming_the_first_trigger_crosses()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 58m, current: 60m);

        var crossing = Assert.Single(crossings);
        Assert.Equal(LevelKeys.ReclaimTrigger1, crossing.Key);
        Assert.Equal(CrossingDirection.Up, crossing.Level.Direction);
    }

    [Fact]
    public void Touching_a_reclaim_trigger_exactly_crosses()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 58m, current: 59m);

        Assert.Equal(LevelKeys.ReclaimTrigger1, Assert.Single(crossings).Key);
    }

    [Fact]
    public void Starting_exactly_on_a_reclaim_trigger_does_not_re_cross_it()
    {
        // Already at or above the level means already triggered; only a fresh crossing counts.
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: 59m, current: 61m));
    }

    [Fact]
    public void Falling_below_a_reclaim_trigger_does_not_cross_it()
    {
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: 61m, current: 58m));
    }

    [Fact]
    public void Reaching_the_first_target_crosses()
    {
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 68m, current: 71m);

        var crossing = Assert.Single(crossings);
        Assert.Equal(LevelKeys.T1, crossing.Key);
        Assert.Equal(AlertType.Target, crossing.Level.Type);
    }

    // ---------- Multiple levels in one tick ----------

    [Fact]
    public void A_single_gap_up_crosses_every_level_it_passes()
    {
        // 58 -> 81 passes reclaim 59, reclaim 63, target 70 and target 80.
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 58m, current: 81m);

        Assert.Equal(
            [LevelKeys.ReclaimTrigger1, LevelKeys.ReclaimTrigger2, LevelKeys.T1, LevelKeys.T2],
            KeysFrom(crossings));
    }

    [Fact]
    public void A_single_gap_down_crosses_every_level_it_passes()
    {
        // 57 -> 40 passes support 55, secondary 46 and invalidation 42.
        var crossings = LevelEvaluator.Evaluate(FullyLevelled(), previous: 57m, current: 40m);

        Assert.Equal(
            [LevelKeys.Invalidation, LevelKeys.SupportZone, LevelKeys.SecondarySupportZone],
            KeysFrom(crossings));
    }

    [Fact]
    public void An_unchanged_price_crosses_nothing()
    {
        Assert.Empty(LevelEvaluator.Evaluate(FullyLevelled(), previous: 57m, current: 57m));
    }

    // ---------- Sparse candidates ----------

    [Fact]
    public void A_candidate_with_no_levels_crosses_nothing()
    {
        var bare = new Candidate { Ticker = "NEW" };

        Assert.Empty(LevelEvaluator.LevelsFor(bare));
        Assert.Empty(LevelEvaluator.Evaluate(bare, previous: 100m, current: 1m));
    }

    [Fact]
    public void A_support_zone_with_only_a_low_bound_is_watched_at_that_bound()
    {
        var candidate = new Candidate { Ticker = "ASTS", SupportLow = 52m };

        var level = Assert.Single(LevelEvaluator.LevelsFor(candidate));
        Assert.Equal(52m, level.Value);

        Assert.Equal(
            [LevelKeys.SupportZone],
            KeysFrom(LevelEvaluator.Evaluate(candidate, previous: 53m, current: 51m)));
    }

    [Fact]
    public void A_support_zone_with_only_a_high_bound_is_watched_at_that_bound()
    {
        var candidate = new Candidate { Ticker = "ASTS", SupportHigh = 55m };

        Assert.Equal(55m, Assert.Single(LevelEvaluator.LevelsFor(candidate)).Value);
    }

    [Fact]
    public void Only_the_levels_that_are_set_are_monitored()
    {
        var candidate = new Candidate { Ticker = "ASTS", Invalidation = 48m, T1 = 70m };

        Assert.Equal(
            [LevelKeys.Invalidation, LevelKeys.T1],
            LevelEvaluator.LevelsFor(candidate).Select(l => l.Key));
    }

    [Fact]
    public void Levels_are_reported_worst_news_first()
    {
        // Invalidation leads so a digest opens with the most consequential line.
        Assert.Equal(
            LevelKeys.Invalidation,
            LevelEvaluator.LevelsFor(FullyLevelled())[0].Key);
    }

    // ---------- IsTriggered ----------

    [Theory]
    [InlineData(47, true)]
    [InlineData(48, true)]
    [InlineData(49, false)]
    public void IsTriggered_for_a_downward_level_includes_the_level_itself(decimal price, bool expected)
    {
        var level = new MonitoredLevel(
            LevelKeys.Invalidation, AlertType.Invalidation, CrossingDirection.Down, 48m, "invalidation");

        Assert.Equal(expected, LevelEvaluator.IsTriggered(level, price));
    }

    [Theory]
    [InlineData(58, false)]
    [InlineData(59, true)]
    [InlineData(60, true)]
    public void IsTriggered_for_an_upward_level_includes_the_level_itself(decimal price, bool expected)
    {
        var level = new MonitoredLevel(
            LevelKeys.ReclaimTrigger1, AlertType.ReclaimTrigger, CrossingDirection.Up, 59m, "reclaim trigger 1");

        Assert.Equal(expected, LevelEvaluator.IsTriggered(level, price));
    }

    // ---------- Re-arming ----------

    [Theory]
    // Invalidation 48, 0.5% margin = 0.24, so re-arm above 48.24.
    [InlineData(47.0, false)]
    [InlineData(48.0, false)]
    [InlineData(48.20, false)]
    [InlineData(48.50, true)]
    public void A_downward_level_re_arms_once_price_climbs_clear_of_it(decimal price, bool expected)
    {
        var level = new MonitoredLevel(
            LevelKeys.Invalidation, AlertType.Invalidation, CrossingDirection.Down, 48m, "invalidation");

        Assert.Equal(expected, LevelEvaluator.HasReArmed(level, price, 0.5m));
    }

    [Theory]
    // Reclaim 59, 0.5% margin = 0.295, so re-arm below 58.705.
    [InlineData(60.0, false)]
    [InlineData(59.0, false)]
    [InlineData(58.80, false)]
    [InlineData(58.50, true)]
    public void An_upward_level_re_arms_once_price_falls_clear_of_it(decimal price, bool expected)
    {
        var level = new MonitoredLevel(
            LevelKeys.ReclaimTrigger1, AlertType.ReclaimTrigger, CrossingDirection.Up, 59m, "reclaim trigger 1");

        Assert.Equal(expected, LevelEvaluator.HasReArmed(level, price, 0.5m));
    }

    [Fact]
    public void A_zero_margin_re_arms_as_soon_as_price_clears_the_level()
    {
        var level = new MonitoredLevel(
            LevelKeys.Invalidation, AlertType.Invalidation, CrossingDirection.Down, 48m, "invalidation");

        Assert.False(LevelEvaluator.HasReArmed(level, 48m, 0m));
        Assert.True(LevelEvaluator.HasReArmed(level, 48.01m, 0m));
    }

    [Fact]
    public void Oscillating_inside_the_margin_never_re_arms()
    {
        // The scenario the margin exists for: a ticker chopping around its trigger must not
        // re-arm and re-fire on every tick.
        var level = new MonitoredLevel(
            LevelKeys.ReclaimTrigger1, AlertType.ReclaimTrigger, CrossingDirection.Up, 59m, "reclaim trigger 1");

        foreach (var price in new[] { 58.9m, 59.1m, 58.95m, 59.05m })
        {
            Assert.False(LevelEvaluator.HasReArmed(level, price, 0.5m));
        }
    }
}
