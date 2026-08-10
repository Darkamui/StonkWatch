namespace StonkWatch.Web.Contracts;

public record CandidateDto(
    Guid Id,
    string Ticker,
    string? Company,
    string? Exchange,
    string? Currency,
    string Priority,
    string Status,
    string? Conviction,
    string? PreferredSetup,
    string? Thesis,
    decimal? CurrentPrice,
    decimal? ReviewedPrice,
    DateTimeOffset? LastReviewed,
    decimal? SupportLow,
    decimal? SupportHigh,
    decimal? SecondarySupportLow,
    decimal? SecondarySupportHigh,
    decimal? ReclaimTrigger1,
    decimal? ReclaimTrigger2,
    decimal? Invalidation,
    decimal? T1,
    decimal? T2,
    string? NextEvent,
    DateOnly? EventDate,
    string DataQuality,
    string? MainRisk,
    string? SourceNotes,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    decimal? LastQuote = null,
    DateTimeOffset? QuoteAt = null);

public record CandidateDetailDto(CandidateDto Candidate, List<AlertDto> Alerts, List<ReviewLogDto> ReviewLogs);

/// <summary>
/// Every field but Ticker is optional and loosely-typed (plain strings for enums) so MCP tool calls
/// built from natural language ("high priority, near trigger") don't need exact C# enum casing —
/// the service layer coerces and validates.
/// </summary>
public record CreateCandidateRequest(
    string Ticker,
    string? Company = null,
    string? Exchange = null,
    string? Currency = null,
    string? Priority = null,
    string? Status = null,
    string? Conviction = null,
    string? PreferredSetup = null,
    string? Thesis = null,
    decimal? CurrentPrice = null,
    decimal? SupportLow = null,
    decimal? SupportHigh = null,
    decimal? SecondarySupportLow = null,
    decimal? SecondarySupportHigh = null,
    decimal? ReclaimTrigger1 = null,
    decimal? ReclaimTrigger2 = null,
    decimal? Invalidation = null,
    decimal? T1 = null,
    decimal? T2 = null,
    string? NextEvent = null,
    DateOnly? EventDate = null,
    string? DataQuality = null,
    string? MainRisk = null,
    string? SourceNotes = null);

/// <summary>
/// PATCH semantics: omitted (null) fields leave the existing value unchanged. To clear a nullable
/// text field, send an empty string. Ticker itself is immutable (it's the route key).
/// </summary>
public record UpdateCandidateRequest(
    string? Company = null,
    string? Exchange = null,
    string? Currency = null,
    string? Priority = null,
    string? Status = null,
    string? Conviction = null,
    string? PreferredSetup = null,
    string? Thesis = null,
    decimal? CurrentPrice = null,
    decimal? SupportLow = null,
    decimal? SupportHigh = null,
    decimal? SecondarySupportLow = null,
    decimal? SecondarySupportHigh = null,
    decimal? ReclaimTrigger1 = null,
    decimal? ReclaimTrigger2 = null,
    decimal? Invalidation = null,
    decimal? T1 = null,
    decimal? T2 = null,
    string? NextEvent = null,
    DateOnly? EventDate = null,
    string? DataQuality = null,
    string? MainRisk = null,
    string? SourceNotes = null);
