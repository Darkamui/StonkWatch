using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
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
            LiveQuoteCache cache, TimeProvider time, IServiceScopeFactory scopeFactory,
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

            var keepalive = TimeSpan.FromSeconds(options.Value.KeepaliveSeconds);
            return TypedResults.ServerSentEvents(
                StreamAsync(scopeFactory, cache, time, keepalive, ct));
        });

        // Ok rather than Created: there is no GET-by-id route for a single item, so
        // Results.Created's Location header would point at a 404. Don't "fix" this to 201
        // without adding that route first.
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

    /// <summary>
    /// The throttle on <see cref="StreamAsync"/>'s mid-stream item-list refresh: at most one
    /// re-read per window, so a tick storm on a symbol this connection doesn't recognize (a
    /// delisted ticker still on the tape, say) cannot turn into a database read per tick.
    /// </summary>
    private static readonly TimeSpan RefreshThrottle = TimeSpan.FromSeconds(2);

    private static async IAsyncEnumerable<SseItem<WatchlistRowDto?>> StreamAsync(
        IServiceScopeFactory scopeFactory, LiveQuoteCache cache, TimeProvider time,
        TimeSpan keepaliveInterval,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var items = await LoadItemsAsync(scopeFactory, ct);
        var bySymbol = items.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            yield return new SseItem<WatchlistRowDto?>(ToRow(item, cache.Get(item.Symbol)), "quote");
        }

        // Never set from the opening burst above: the first tick for a symbol this
        // connection doesn't recognize must refresh immediately (a symbol added right
        // after connecting is the common case), not wait out the throttle window.
        DateTimeOffset? lastRefreshAt = null;

        await using var subscription = cache.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var moveNextTask = subscription.MoveNextAsync().AsTask();

        while (true)
        {
            // Raced against the subscription read rather than layered on top of it: an
            // await-foreach has no point at which to also wait on a timer, so the
            // enumerator is driven by hand here.
            var keepaliveDelay = Task.Delay(keepaliveInterval, time, ct);
            var winner = await Task.WhenAny(moveNextTask, keepaliveDelay);

            if (winner == keepaliveDelay)
            {
                // Observes the delay task: completes normally when the interval elapsed,
                // or rethrows OperationCanceledException when ct (RequestAborted) fired
                // instead, which ends this iterator exactly as the old await-foreach did —
                // the `await using` above still unsubscribes on the way out.
                await keepaliveDelay;
                yield return new SseItem<WatchlistRowDto?>(null, "ping");
                continue;
            }

            if (!await moveNextTask)
            {
                yield break;
            }

            var quote = subscription.Current;
            if (!bySymbol.TryGetValue(quote.Symbol, out var item))
            {
                var now = time.GetUtcNow();
                if (lastRefreshAt is null || now - lastRefreshAt.Value >= RefreshThrottle)
                {
                    // Re-read fully replaces the map (not a merge), so a symbol removed
                    // from the watchlist since this connection opened is dropped by this
                    // refresh too, not just symbols that were added.
                    items = await LoadItemsAsync(scopeFactory, ct);
                    bySymbol = items.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);
                    lastRefreshAt = now;
                    bySymbol.TryGetValue(quote.Symbol, out item);
                }
            }

            // Still unrecognized after a throttled or fresh refresh has no row to update;
            // drop it rather than inventing one.
            if (item is not null)
            {
                yield return new SseItem<WatchlistRowDto?>(ToRow(item, quote), "quote");
            }

            moveNextTask = subscription.MoveNextAsync().AsTask();
        }
    }

    /// <summary>
    /// Resolves <see cref="WatchlistService"/> from a scope created and disposed within this
    /// call, rather than accepting it as a parameter. <see cref="StreamAsync"/> is a
    /// long-lived iterator backing an SSE connection that can stay open for hours; holding a
    /// request-scoped service (and the <c>DbContext</c> behind it) across that lifetime is
    /// what Task 9's fix round retired. Every read here — the opening burst and every
    /// mid-stream refresh — gets its own scope, held only for the read itself.
    /// </summary>
    private static async Task<List<WatchlistItemDto>> LoadItemsAsync(
        IServiceScopeFactory scopeFactory, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<WatchlistService>();
        return await service.ListItemsAsync(ct);
    }

    private static WatchlistRowDto ToRow(WatchlistItemDto item, LiveQuote? quote) => new(
        item.Id, item.GroupId, item.Symbol,
        item.DisplayName ?? item.Symbol,
        item.SortOrder,
        quote?.Last, quote?.ChangePercent, quote?.Volume, quote?.ExtendedPrice, quote?.LastAt);
}
