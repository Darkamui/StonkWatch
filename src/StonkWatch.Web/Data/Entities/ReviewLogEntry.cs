namespace StonkWatch.Web.Data.Entities;

public class ReviewLogEntry
{
    public Guid Id { get; set; }

    public Guid CandidateId { get; set; }
    public Candidate? Candidate { get; set; }

    public DateTimeOffset ReviewDate { get; set; }
    public decimal? Price { get; set; }
    public CandidateStatus? StatusAtReview { get; set; }
    public ThesisImpact? ThesisImpact { get; set; }
    public string? WhatChanged { get; set; }
    public bool LevelsChanged { get; set; }
    public string? NextAction { get; set; }
    public string? Notes { get; set; }
}
