using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services;

namespace StonkWatch.Web.Endpoints;

public static class CandidateEndpoints
{
    public static void MapCandidateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/candidates").RequireAuthorization("ApiKey");

        group.MapGet("", async (string? status, string? priority, CandidateService service, CancellationToken ct) =>
        {
            try
            {
                var statusFilter = status is null ? (CandidateStatus?)null : EnumParsing.ParseOrDefault<CandidateStatus>(status, default);
                var priorityFilter = priority is null ? (Priority?)null : EnumParsing.ParseOrDefault<Priority>(priority, default);
                var candidates = await service.ListAsync(statusFilter, priorityFilter, ct);
                return Results.Ok(candidates);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapGet("/{ticker}", async (string ticker, CandidateService service, CancellationToken ct) =>
        {
            var candidate = await service.GetByTickerAsync(ticker, ct);
            return candidate is null ? Results.NotFound() : Results.Ok(candidate);
        });

        group.MapPost("", async (CreateCandidateRequest request, CandidateService service, CancellationToken ct) =>
        {
            try
            {
                var created = await service.CreateAsync(request, ct);
                return Results.Created($"/candidates/{created.Ticker}", created);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapPatch("/{ticker}", async (string ticker, UpdateCandidateRequest request, CandidateService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateAsync(ticker, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/{ticker}", async (string ticker, CandidateService service, CancellationToken ct) =>
        {
            var deleted = await service.DeleteAsync(ticker, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        group.MapPost("/{ticker}/review", async (string ticker, LogReviewRequest request, CandidateService service, CancellationToken ct) =>
        {
            try
            {
                var entry = await service.LogReviewAsync(ticker, request, ct);
                return entry is null ? Results.NotFound() : Results.Ok(entry);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPost("/{ticker}/alerts", async (string ticker, CreateAlertRequest request, CandidateService service, CancellationToken ct) =>
        {
            try
            {
                var alert = await service.AddAlertAsync(ticker, request, ct);
                return alert is null ? Results.NotFound() : Results.Ok(alert);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapPatch("/{ticker}/alerts/{alertId:guid}", async (
            string ticker, Guid alertId, UpdateAlertRequest request, CandidateService service, CancellationToken ct) =>
        {
            var alert = await service.UpdateAlertAsync(ticker, alertId, request, ct);
            return alert is null ? Results.NotFound() : Results.Ok(alert);
        });

        group.MapDelete("/{ticker}/alerts/{alertId:guid}", async (
            string ticker, Guid alertId, CandidateService service, CancellationToken ct) =>
        {
            var deleted = await service.DeleteAlertAsync(ticker, alertId, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
