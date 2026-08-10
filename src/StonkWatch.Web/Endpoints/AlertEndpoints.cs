using StonkWatch.Web.Services;

namespace StonkWatch.Web.Endpoints;

public static class AlertEndpoints
{
    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/alerts", async (bool? triggered, CandidateService service, CancellationToken ct) =>
        {
            var alerts = await service.GetAlertsAsync(triggered, ct);
            return Results.Ok(alerts);
        }).RequireAuthorization("ApiKey");

        app.MapPost("/api/candidates/{ticker}/alerts/{alertId:guid}/acknowledge", async (
            string ticker, Guid alertId, CandidateService service, CancellationToken ct) =>
        {
            var alert = await service.AcknowledgeAlertAsync(ticker, alertId, ct);
            return alert is null ? Results.NotFound() : Results.Ok(alert);
        }).RequireAuthorization("ApiKey");

        app.MapGet("/api/jobs/{job}/last", async (
            string job, CandidateService service, CancellationToken ct) =>
        {
            var run = await service.GetLastJobRunAsync(job, ct);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).RequireAuthorization("ApiKey");
    }
}
