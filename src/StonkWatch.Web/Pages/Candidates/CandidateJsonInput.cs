using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Pages.Candidates;

/// <summary>
/// The shape pasted into the Add/Update JSON boxes. Flat, camelCase-by-default field names
/// matching what the (now removed) MCP tools used, so existing habits/notes built around that
/// shape keep working. Deserialized with PropertyNameCaseInsensitive, so exact casing doesn't
/// matter. "Setup" (not "PreferredSetup") is the property name specifically so it round-trips
/// as "setup" in the pasted JSON without needing a JsonPropertyName override that would also
/// affect the JSON API's contracts.
/// </summary>
public class CandidateJsonInput
{
    public string? Ticker { get; set; }
    public string? Company { get; set; }
    public string? Exchange { get; set; }
    public string? Currency { get; set; }
    public string? Priority { get; set; }
    public string? Status { get; set; }
    public string? Conviction { get; set; }
    public string? DataQuality { get; set; }
    public string? Setup { get; set; }
    public string? Thesis { get; set; }
    public string? MainRisk { get; set; }
    public string? SourceNotes { get; set; }
    public decimal? CurrentPrice { get; set; }
    public decimal? SupportLow { get; set; }
    public decimal? SupportHigh { get; set; }
    public decimal? SecondarySupportLow { get; set; }
    public decimal? SecondarySupportHigh { get; set; }
    public decimal? ReclaimTrigger1 { get; set; }
    public decimal? ReclaimTrigger2 { get; set; }
    public decimal? Invalidation { get; set; }
    public decimal? T1 { get; set; }
    public decimal? T2 { get; set; }
    public string? NextEvent { get; set; }
    public DateOnly? EventDate { get; set; }

    public CreateCandidateRequest ToCreateRequest() => new(
        Ticker ?? "", Company, Exchange, Currency, Priority, Status, Conviction, Setup, Thesis,
        CurrentPrice, SupportLow, SupportHigh, SecondarySupportLow, SecondarySupportHigh,
        ReclaimTrigger1, ReclaimTrigger2, Invalidation, T1, T2,
        NextEvent, EventDate, DataQuality, MainRisk, SourceNotes);

    public UpdateCandidateRequest ToUpdateRequest() => new(
        Company, Exchange, Currency, Priority, Status, Conviction, Setup, Thesis,
        CurrentPrice, SupportLow, SupportHigh, SecondarySupportLow, SecondarySupportHigh,
        ReclaimTrigger1, ReclaimTrigger2, Invalidation, T1, T2,
        NextEvent, EventDate, DataQuality, MainRisk, SourceNotes);

    public static CandidateJsonInput FromDto(CandidateDto c) => new()
    {
        Ticker = c.Ticker,
        Company = c.Company,
        Exchange = c.Exchange,
        Currency = c.Currency,
        Priority = c.Priority,
        Status = c.Status,
        Conviction = c.Conviction,
        DataQuality = c.DataQuality,
        Setup = c.PreferredSetup,
        Thesis = c.Thesis,
        MainRisk = c.MainRisk,
        SourceNotes = c.SourceNotes,
        CurrentPrice = c.CurrentPrice,
        SupportLow = c.SupportLow,
        SupportHigh = c.SupportHigh,
        SecondarySupportLow = c.SecondarySupportLow,
        SecondarySupportHigh = c.SecondarySupportHigh,
        ReclaimTrigger1 = c.ReclaimTrigger1,
        ReclaimTrigger2 = c.ReclaimTrigger2,
        Invalidation = c.Invalidation,
        T1 = c.T1,
        T2 = c.T2,
        NextEvent = c.NextEvent,
        EventDate = c.EventDate
    };
}
