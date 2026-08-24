namespace StonkWatch.Web.Data.Entities;

/// <summary>
/// One row per JSON-driven update on the Candidate Detail page: the full candidate state
/// as it looked right before that update was applied. Field-level history browsing is
/// future work; for now this just captures the snapshot.
/// </summary>
public class CandidateHistoryEntry
{
    public Guid Id { get; set; }

    public Guid CandidateId { get; set; }
    public Candidate? Candidate { get; set; }

    public DateTimeOffset SnapshotAt { get; set; }
    public required string PreviousState { get; set; }
}
