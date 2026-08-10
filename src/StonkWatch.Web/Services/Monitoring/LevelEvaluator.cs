using StonkWatch.Web.Data;
using StonkWatch.Web.Data.Entities;

namespace StonkWatch.Web.Services.Monitoring;

public enum CrossingDirection
{
    /// <summary>Triggered once price reaches or exceeds the level (reclaims, targets).</summary>
    Up,

    /// <summary>Triggered once price reaches or falls below the level (supports, invalidation).</summary>
    Down
}

/// <summary>One price level on a candidate that the worker watches.</summary>
public record MonitoredLevel(
    string Key, AlertType Type, CrossingDirection Direction, decimal Value, string Label);

/// <summary>A level that went from not-triggered to triggered between two ticks.</summary>
public record LevelCrossing(MonitoredLevel Level, decimal PreviousPrice, decimal Price)
{
    public string Key => Level.Key;
}

/// <summary>
/// Decides which of a candidate's levels were crossed between two prices. Deliberately pure —
/// no database, no clock, no I/O — because this is the highest-consequence arithmetic in the
/// app and every branch of it needs to be cheap to test.
/// </summary>
public static class LevelEvaluator
{
    /// <summary>
    /// A level counts as triggered once price *reaches* it, in either direction. Touching
    /// invalidation exactly is therefore a break, and touching a reclaim trigger exactly is
    /// a reclaim — for alerting purposes "price got to your line" is the event worth an email.
    /// </summary>
    public static bool IsTriggered(MonitoredLevel level, decimal price) =>
        level.Direction == CrossingDirection.Up
            ? price >= level.Value
            : price <= level.Value;

    /// <summary>
    /// Levels the candidate actually defines, in the order they should be reported.
    /// A support zone is watched at its upper bound — that is the edge price crosses first,
    /// and it fires whether price settles into the zone or blows straight through it.
    /// </summary>
    public static IReadOnlyList<MonitoredLevel> LevelsFor(Candidate c)
    {
        var levels = new List<MonitoredLevel>();

        Add(LevelKeys.Invalidation, AlertType.Invalidation, CrossingDirection.Down,
            c.Invalidation, "invalidation");

        Add(LevelKeys.SupportZone, AlertType.PrimarySupport, CrossingDirection.Down,
            c.SupportHigh ?? c.SupportLow, "primary support");

        Add(LevelKeys.SecondarySupportZone, AlertType.SecondarySupport, CrossingDirection.Down,
            c.SecondarySupportHigh ?? c.SecondarySupportLow, "secondary support");

        Add(LevelKeys.ReclaimTrigger1, AlertType.ReclaimTrigger, CrossingDirection.Up,
            c.ReclaimTrigger1, "reclaim trigger 1");

        Add(LevelKeys.ReclaimTrigger2, AlertType.ReclaimTrigger, CrossingDirection.Up,
            c.ReclaimTrigger2, "reclaim trigger 2");

        Add(LevelKeys.T1, AlertType.Target, CrossingDirection.Up, c.T1, "target 1");
        Add(LevelKeys.T2, AlertType.Target, CrossingDirection.Up, c.T2, "target 2");

        return levels;

        void Add(string key, AlertType type, CrossingDirection direction, decimal? value, string label)
        {
            if (value is { } v)
            {
                levels.Add(new MonitoredLevel(key, type, direction, v, label));
            }
        }
    }

    /// <summary>
    /// Levels that went from not-triggered to triggered between <paramref name="previous"/> and
    /// <paramref name="current"/>. Returns nothing when <paramref name="previous"/> is null:
    /// the first tick for a candidate only records its price, so deploying the worker does not
    /// fire every already-breached level on every ticker at once.
    /// </summary>
    public static IReadOnlyList<LevelCrossing> Evaluate(
        Candidate candidate, decimal? previous, decimal current)
    {
        if (previous is not { } prior)
        {
            return [];
        }

        var crossings = new List<LevelCrossing>();
        foreach (var level in LevelsFor(candidate))
        {
            if (!IsTriggered(level, prior) && IsTriggered(level, current))
            {
                crossings.Add(new LevelCrossing(level, prior, current));
            }
        }

        return crossings;
    }

    /// <summary>
    /// Whether price has moved far enough back past a level to re-arm an alert that already
    /// fired. Without this margin a ticker oscillating on its trigger re-fires every tick.
    /// </summary>
    public static bool HasReArmed(MonitoredLevel level, decimal price, decimal marginPercent)
    {
        var margin = Math.Abs(level.Value) * (marginPercent / 100m);

        return level.Direction == CrossingDirection.Up
            ? price < level.Value - margin
            : price > level.Value + margin;
    }
}
