using StonkWatch.Web.Contracts;

namespace StonkWatch.Web.Pages.Candidates;

/// <summary>Shared field set for the New and Detail/Edit forms.</summary>
public class CandidateFormInput
{
    public string? Company { get; set; }
    public string? Exchange { get; set; }
    public string? Currency { get; set; }
    public string Priority { get; set; } = "Medium";
    public string Status { get; set; } = "Idea";
    public string? Conviction { get; set; }
    public string? PreferredSetup { get; set; }
    public string? Thesis { get; set; }
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
    public string DataQuality { get; set; } = "Unavailable";
    public string? MainRisk { get; set; }
    public string? SourceNotes { get; set; }

    public UpdateCandidateRequest ToUpdateRequest() => new(
        Company, Exchange, Currency, Priority, Status, Conviction, PreferredSetup, Thesis,
        CurrentPrice, SupportLow, SupportHigh, SecondarySupportLow, SecondarySupportHigh,
        ReclaimTrigger1, ReclaimTrigger2, Invalidation, T1, T2,
        NextEvent, EventDate, DataQuality, MainRisk, SourceNotes);

    public CreateCandidateRequest ToCreateRequest(string ticker) => new(
        ticker, Company, Exchange, Currency, Priority, Status, Conviction, PreferredSetup, Thesis,
        CurrentPrice, SupportLow, SupportHigh, SecondarySupportLow, SecondarySupportHigh,
        ReclaimTrigger1, ReclaimTrigger2, Invalidation, T1, T2,
        NextEvent, EventDate, DataQuality, MainRisk, SourceNotes);

    public static CandidateFormInput FromDto(CandidateDto c) => new()
    {
        Company = c.Company,
        Exchange = c.Exchange,
        Currency = c.Currency,
        Priority = c.Priority,
        Status = c.Status,
        Conviction = c.Conviction,
        PreferredSetup = c.PreferredSetup,
        Thesis = c.Thesis,
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
        EventDate = c.EventDate,
        DataQuality = c.DataQuality,
        MainRisk = c.MainRisk,
        SourceNotes = c.SourceNotes
    };
}
