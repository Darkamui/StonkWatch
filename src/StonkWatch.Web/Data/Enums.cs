namespace StonkWatch.Web.Data;

public enum Priority
{
    Low,
    Medium,
    High
}

public enum CandidateStatus
{
    Idea,
    Watch,
    NearTrigger,
    Reanalyze,
    Ready,
    Invalidated,
    Entered
}

public enum Conviction
{
    C,
    B,
    A
}

public enum DataQuality
{
    Unavailable,
    Partial,
    Complete
}

public enum ThesisImpact
{
    Invalidated,
    Weakened,
    Unchanged,
    Improved
}

public enum AlertType
{
    PrimarySupport,
    SecondarySupport,
    ReclaimTrigger,
    Invalidation,
    Target
}

public enum JobStatus
{
    Running,
    Succeeded,
    Failed
}
