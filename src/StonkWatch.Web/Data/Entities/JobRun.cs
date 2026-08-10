namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// One record per background job execution. Unattended work you cannot see is work you
/// cannot trust — this is what the dashboard reads to show the worker is alive.
/// </summary>
public class JobRun
{
    public Guid Id { get; set; }

    public required string Job { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Running;

    public int CandidatesChecked { get; set; }
    public int AlertsFired { get; set; }
    public int NotificationsSent { get; set; }

    /// <summary>Set when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Why a run did no work, e.g. "Market closed". Null on a normal run.</summary>
    public string? SkipReason { get; set; }
}

public static class JobNames
{
    public const string PriceCheck = "PriceCheck";
}
