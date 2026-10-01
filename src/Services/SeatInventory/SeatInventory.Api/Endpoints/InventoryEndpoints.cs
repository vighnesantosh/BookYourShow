using BookYourShow.BuildingBlocks.Idempotency;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.BuildingBlocks.Security;
using BookYourShow.Contracts;
using SeatInventory.Api.Domain;

namespace SeatInventory.Api.Endpoints;

public sealed record HoldSeatsRequest(IReadOnlyList<string> SeatIds);
public sealed record HoldSeatsResponse(Guid HoldId, Guid ShowId, IReadOnlyList<string> SeatIds, DateTimeOffset ExpiresAt);
public sealed record ExtendHoldRequest(string UserId, DateTimeOffset ExpiresAt);

public static class InventoryEndpoints
{
    public const string HoldExpiryQueue = "inventory-hold-expiry";

    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        // ---------- public (through API Gateway) ----------
        var shows = app.MapGroup("/shows/{showId:guid}").WithTags("Seat inventory");

        shows.MapGet("/availability", async (Guid showId, SeatHoldStore store) =>
            Results.Ok(await store.SnapshotAsync(showId)));

        shows.MapPost("/holds", HoldSeatsAsync).RequireIdempotencyKey();

        shows.MapDelete("/holds/{holdId:guid}", async (Guid showId, Guid holdId, SeatHoldStore store, SeatDeltaPublisher deltas, ICurrentUser user) =>
        {
            var hold = await store.GetHoldAsync(showId, holdId);
            if (hold is null || hold.UserId != user.UserId) return Results.NotFound();

            var outcome = await store.ReleaseAsync(showId, holdId, hold.UserId, onlyIfExpired: false);
            if (outcome.Status == ReleaseStatus.Confirmed)
                return Results.Problem("Seats are already sold; cancel the booking instead.", statusCode: StatusCodes.Status409Conflict);

            await deltas.PublishAsync(showId, outcome.Sequence, "released", outcome.FreedSeats);
            return Results.NoContent();
        });

        // ---------- internal (service-to-service only; not routed by API Gateway) ----------
        var internalApi = app.MapGroup("/internal/shows/{showId:guid}").WithTags("Internal");

        internalApi.MapGet("/holds/{holdId:guid}", async (Guid showId, Guid holdId, SeatHoldStore store, TimeProvider clock) =>
        {
            var hold = await store.GetHoldAsync(showId, holdId);
            if (hold is null) return Results.NotFound();
            if (!hold.IsActive(clock.GetUtcNow())) return Results.StatusCode(StatusCodes.Status410Gone);
            return Results.Ok(new HoldView(hold.HoldId, hold.ShowId, hold.UserId, hold.SeatIds, hold.ExpiresAt));
        });

        internalApi.MapPost("/holds/{holdId:guid}/extend", async (Guid showId, Guid holdId, ExtendHoldRequest body, SeatHoldStore store) =>
        {
            var max = DateTimeOffset.UtcNow + HoldPolicy.PaymentHoldDuration;
            var target = body.ExpiresAt > max ? max : body.ExpiresAt;
            return await store.ExtendAsync(showId, holdId, body.UserId, target)
                ? Results.Ok(new { holdId, expiresAt = target })
                : Results.StatusCode(StatusCodes.Status410Gone);
        });

        internalApi.MapPost("/rebuild", async (Guid showId, ShowSeatRepository seats, SeatHoldStore store) =>
        {
            var (all, booked) = await seats.LoadAsync(showId);
            await store.LoadShowAsync(showId, all, booked);
            return Results.Ok(new { showId, seats = all.Count, booked = booked.Count });
        });

        return app;
    }

    private static async Task<IResult> HoldSeatsAsync(
        Guid showId, HoldSeatsRequest body, SeatHoldStore store, SeatDeltaPublisher deltas,
        IQueueSender queue, ICurrentUser user, ILoggerFactory loggers)
    {
        var seatIds = body.SeatIds?.Select(s => s.Trim().ToUpperInvariant()).Where(s => s.Length > 0).Distinct().ToList() ?? [];
        if (seatIds.Count == 0 || seatIds.Count > HoldPolicy.MaxSeatsPerHold)
            return Results.Problem($"Select between 1 and {HoldPolicy.MaxSeatsPerHold} seats.", statusCode: StatusCodes.Status400BadRequest);

        var outcome = await store.HoldAsync(showId, user.UserId, seatIds);
        if (!outcome.Success)
        {
            var (status, detail) = outcome.Rejection switch
            {
                HoldRejection.SeatBooked or HoldRejection.SeatHeld => (StatusCodes.Status409Conflict, $"Seat {outcome.ConflictSeat} is no longer available."),
                HoldRejection.UserHasActiveHold => (StatusCodes.Status409Conflict, "You already hold seats for this show. Release them or complete that booking first."),
                HoldRejection.UnknownSeat => (StatusCodes.Status400BadRequest, $"Seat {outcome.ConflictSeat} does not exist."),
                HoldRejection.ShowNotOnSale => (StatusCodes.Status404NotFound, "This show is not on sale."),
                _ => (StatusCodes.Status400BadRequest, "Hold rejected."),
            };
            return Results.Problem(detail, statusCode: status, extensions: new Dictionary<string, object?>
            {
                ["code"] = outcome.Rejection.ToString(),
                ["conflictSeat"] = outcome.ConflictSeat,
            });
        }

        await deltas.PublishAsync(showId, outcome.Sequence, "held", seatIds);

        try
        {
            // Timeout guard #3 (design §5): a delayed message re-checks this hold when it should expire.
            await queue.SendAsync(HoldExpiryQueue, new HoldExpiryCheck(showId, outcome.HoldId), HoldPolicy.HoldDuration + TimeSpan.FromSeconds(1));
        }
        catch (Exception ex)
        {
            // Lazy expiry in the hold script still frees the seats; only the push notification is delayed.
            loggers.CreateLogger("Holds").LogError(ex, "Could not schedule expiry check for hold {HoldId}", outcome.HoldId);
        }

        return Results.Created($"/shows/{showId}/holds/{outcome.HoldId}",
            new HoldSeatsResponse(outcome.HoldId, showId, seatIds, outcome.ExpiresAt));
    }
}
