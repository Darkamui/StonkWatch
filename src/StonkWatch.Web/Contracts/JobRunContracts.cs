namespace StonkWatch.Web.Contracts;

public record JobRunDto(
    Guid Id,
    string Job,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string Status,
    int CandidatesChecked,
    int AlertsFired,
    int NotificationsSent,
    string? Error,
    string? SkipReason);
