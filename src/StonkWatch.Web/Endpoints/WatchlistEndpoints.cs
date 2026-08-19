using System.Runtime.CompilerServices;
using Microsoft.Extensions.Options;
using StonkWatch.Web.Contracts;
using StonkWatch.Web.Data;
using StonkWatch.Web.Services.MarketData;
using StonkWatch.Web.Services.Watchlist;

namespace StonkWatch.Web.Endpoints;

public static class WatchlistEndpoints
{
    public static void MapWatchlistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/watchlist").RequireAuthorization("CookieOrApiKey");

        group.MapGet("", async (
            WatchlistService service, LiveQuoteCache cache, TimeProvider time, CancellationToken ct) =>
            Results.Ok(await BuildViewAsync(service, cache, time, ct)));

        // Full state first, then changes only. Without the opening burst a symbol that
        // happens not to trade for ten minutes after a page load renders blank, which
        // looks broken rather than quiet.
        group.MapGet("/stream", (
            WatchlistService service, LiveQuoteCache cache, TimeProvider time,
            IOptions<LiveWatchlistOptions> options, CancellationToken ct) =>
        {
            // With the feature off nothing ever writes to the cache, so an open stream
            // would hang forever looking live. Say so instead.
            if (!options.Value.Enabled)
            {
                return Results.Problem(
                    "The live watchlist is not enabled on this server.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return TypedResults.ServerSentEvents(
                StreamAsync(service, cache, time, ct), eventType: "quote");
        });

        group.MapPost("/items", async (
            CreateWatchlistItemRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.AddItemAsync(request, ct));
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

        group.MapPatch("/items/{id:guid}", async (
            Guid id, UpdateWatchlistItemRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateItemAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        group.MapDelete("/items/{id:guid}", async (
            Guid id, WatchlistService service, CancellationToken ct) =>
            await service.RemoveItemAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/groups", async (
            CreateWatchlistGroupRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await service.AddGroupAsync(request, ct));
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

        group.MapPatch("/groups/{id:guid}", async (
            Guid id, UpdateWatchlistGroupRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                var updated = await service.UpdateGroupAsync(id, request, ct);
                return updated is null ? Results.NotFound() : Results.Ok(updated);
            }
            catch (ConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });

        group.MapDelete("/groups/{id:guid}", async (
            Guid id, WatchlistService service, CancellationToken ct) =>
            await service.RemoveGroupAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        group.MapPost("/reorder", async (
            ReorderRequest request, WatchlistService service, CancellationToken ct) =>
        {
            try
            {
                await service.ReorderAsync(request, ct);
                return Results.NoContent();
            }
            catch (ValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    private static async Task<WatchlistViewDto> BuildViewAsync(
        WatchlistService service, LiveQuoteCache cache, TimeProvider time, CancellationToken ct)
    {
        var groups = await service.ListGroupsAsync(ct);
        var items = await service.ListItemsAsync(ct);
        var rows = items.Select(i => ToRow(i, cache.Get(i.Symbol))).ToList();
        return new WatchlistViewDto(groups, rows, time.GetUtcNow());
    }

    private static async IAsyncEnumerable<WatchlistRowDto> StreamAsync(
        WatchlistService service, LiveQuoteCache cache, TimeProvider time,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var items = await service.ListItemsAsync(ct);
        var bySymbol = items.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            yield return ToRow(item, cache.Get(item.Symbol));
        }

        await foreach (var quote in cache.SubscribeAsync(ct))
        {
            // A tick for a symbol removed since this connection opened has no row to
            // update; drop it rather than inventing one.
            if (bySymbol.TryGetValue(quote.Symbol, out var item))
            {
                yield return ToRow(item, quote);
            }
        }
    }

    private static WatchlistRowDto ToRow(WatchlistItemDto item, LiveQuote? quote) => new(
        item.Id, item.GroupId, item.Symbol,
        item.DisplayName ?? item.Symbol,
        item.SortOrder,
        quote?.Last, quote?.ChangePercent, quote?.Volume, quote?.ExtendedPrice, quote?.LastAt);
}
