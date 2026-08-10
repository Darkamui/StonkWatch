using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Pages.Candidates;

public sealed record LadderTick(string Label, string ValueDisplay, double Percent, string Kind);

public sealed record LadderPoint(string ValueDisplay, double Percent);

public sealed record LadderLegendItem(string Label, string ValueDisplay, string Kind);

public sealed class Ladder
{
    public required List<LadderTick> Ticks { get; init; }

    public required LadderPoint? Current { get; init; }

    public required List<LadderLegendItem> Legend { get; init; }
}

/// <summary>
/// Places a candidate's thesis levels (invalidation, support, reclaim triggers, targets) and its
/// current price on a single low-to-high axis so the UI can render "where price sits in the plan"
/// at a glance. Pure presentation math for the Candidates pages; not a domain service.
/// </summary>
public static class LevelLadder
{
    public static Ladder? Build(CandidateDto c)
    {
        var points = new List<(string Label, decimal Value, string Kind)>();

        void Add(string label, decimal? value, string kind)
        {
            if (value is { } v)
            {
                points.Add((label, v, kind));
            }
        }

        Add("Invalidation", c.Invalidation, "bear-strong");
        Add("2nd support low", c.SecondarySupportLow, "bear-soft");
        Add("2nd support high", c.SecondarySupportHigh, "bear-soft");
        Add("Support low", c.SupportLow, "bear-soft");
        Add("Support high", c.SupportHigh, "bear-soft");
        Add("Reclaim 1", c.ReclaimTrigger1, "bull-soft");
        Add("Reclaim 2", c.ReclaimTrigger2, "bull-soft");
        Add("Target 1", c.T1, "bull");
        Add("Target 2", c.T2, "bull-strong");

        var current = c.LastQuote ?? c.CurrentPrice;

        var allValues = points.Select(p => p.Value).ToList();
        if (current is { } currentForRange)
        {
            allValues.Add(currentForRange);
        }

        if (allValues.Count < 2)
        {
            return null;
        }

        var min = allValues.Min();
        var max = allValues.Max();
        if (max == min)
        {
            return null;
        }

        double Percent(decimal v) => (double)((v - min) / (max - min)) * 100.0;
        static string Fmt(decimal v) => v.ToString("0.00##");

        var ticks = points
            .Select(p => new LadderTick(p.Label, Fmt(p.Value), Percent(p.Value), p.Kind))
            .ToList();

        var currentPoint = current is { } cp ? new LadderPoint(Fmt(cp), Percent(cp)) : null;

        static string FmtRange(decimal? low, decimal? high) => (low, high) switch
        {
            ({ } l, { } h) when l != h => $"{Fmt(l)}–{Fmt(h)}",
            ({ } l, _) => Fmt(l),
            (_, { } h) => Fmt(h),
            _ => "—"
        };

        var legend = new List<LadderLegendItem>();
        if (c.Invalidation is { } inv)
        {
            legend.Add(new LadderLegendItem("Invalidation", Fmt(inv), "bear-strong"));
        }
        if (c.SecondarySupportLow is not null || c.SecondarySupportHigh is not null)
        {
            legend.Add(new LadderLegendItem("2nd support", FmtRange(c.SecondarySupportLow, c.SecondarySupportHigh), "bear-soft"));
        }
        if (c.SupportLow is not null || c.SupportHigh is not null)
        {
            legend.Add(new LadderLegendItem("Support", FmtRange(c.SupportLow, c.SupportHigh), "bear-soft"));
        }
        if (c.ReclaimTrigger1 is { } r1)
        {
            legend.Add(new LadderLegendItem("Reclaim 1", Fmt(r1), "bull-soft"));
        }
        if (c.ReclaimTrigger2 is { } r2)
        {
            legend.Add(new LadderLegendItem("Reclaim 2", Fmt(r2), "bull-soft"));
        }
        if (c.T1 is { } t1)
        {
            legend.Add(new LadderLegendItem("Target 1", Fmt(t1), "bull"));
        }
        if (c.T2 is { } t2)
        {
            legend.Add(new LadderLegendItem("Target 2", Fmt(t2), "bull-strong"));
        }

        return new Ladder { Ticks = ticks, Current = currentPoint, Legend = legend };
    }
}
