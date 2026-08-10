namespace StonkWatch.Web.Contracts;

public record ReviewLogDto(
    Guid Id,
    Guid CandidateId,
    DateTimeOffset ReviewDate,
    decimal? Price,
    string? StatusAtReview,
    string? ThesisImpact,
    string? WhatChanged,
    bool LevelsChanged,
    string? NextAction,
    string? Notes);

public record LogReviewRequest(
    decimal? Price = null,
    string? StatusAtReview = null,
    string? ThesisImpact = null,
    string? WhatChanged = null,
    bool LevelsChanged = false,
    string? NextAction = null,
    string? Notes = null,
    DateTimeOffset? ReviewDate = null);
