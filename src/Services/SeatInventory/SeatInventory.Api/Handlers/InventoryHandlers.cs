using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;
using Npgsql;
using SeatInventory.Api.Domain;
using SeatInventory.Api.Endpoints;

namespace SeatInventory.Api.Handlers;

/// <summary>Catalog published a show: create its durable seats and open it for holds.</summary>
public sealed class ShowPublishedHandler(NpgsqlDataSource db, ShowSeatRepository seats, SeatHoldStore store)
    : IIntegrationEventHandler<ShowPublished>
{
    public async Task HandleAsync(ShowPublished evt, MessageContext ctx, CancellationToken ct)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await seats.CreateSeatsAsync(conn, tx, evt.ShowId, evt.Seats);
            await tx.CommitAsync(ct);
        }

        var (all, booked) = await seats.LoadAsync(evt.ShowId);
        await store.LoadShowAsync(evt.ShowId, all, booked);
    }
}

/// <summary>The delayed check scheduled when a hold was created (design §5, timeouts).</summary>
public sealed class HoldExpiryCheckHandler(SeatHoldStore store, SeatDeltaPublisher deltas, IEventPublisher events, IQueueSender queue, TimeProvider clock)
    : IIntegrationEventHandler<HoldExpiryCheck>
{
    public async Task HandleAsync(HoldExpiryCheck evt, MessageContext ctx, CancellationToken ct)
    {
        var hold = await store.GetHoldAsync(evt.ShowId, evt.HoldId);
        if (hold is null) return;

        var outcome = await store.ReleaseAsync(evt.ShowId, evt.HoldId, hold.UserId, onlyIfExpired: true);
        switch (outcome.Status)
        {
            case ReleaseStatus.NotExpired:
                // The hold was extended for payment: check again when the new expiry passes.
                var wait = outcome.ExpiresAt!.Value - clock.GetUtcNow() + TimeSpan.FromSeconds(1);
                await queue.SendAsync(InventoryEndpoints.HoldExpiryQueue, evt, wait, ct);
                break;

            case ReleaseStatus.Released:
                await deltas.PublishAsync(evt.ShowId, outcome.Sequence, "released", outcome.FreedSeats);
                await events.PublishAsync(new HoldExpired(evt.ShowId, evt.HoldId, outcome.FreedSeats), ct);
                break;

            case ReleaseStatus.AlreadyReleased:
                // Redelivery after a publish failure: publishing again is safe, Booking ignores duplicates.
                await events.PublishAsync(new HoldExpired(evt.ShowId, evt.HoldId, []), ct);
                break;

            // Confirmed or gone: nothing to do.
        }
    }
}

/// <summary>Sale committed in the Booking database: mark seats sold everywhere.</summary>
public sealed class BookingConfirmedHandler(NpgsqlDataSource db, ShowSeatRepository seats, SeatHoldStore store, SeatDeltaPublisher deltas)
    : IIntegrationEventHandler<BookingConfirmed>
{
    public async Task HandleAsync(BookingConfirmed evt, MessageContext ctx, CancellationToken ct)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            if (await Inbox.TryMarkProcessedAsync(conn, tx, ctx, nameof(BookingConfirmedHandler)))
                await seats.MarkBookedAsync(conn, tx, evt.ShowId, evt.BookingId, evt.SeatIds);
            await tx.CommitAsync(ct);
        }

        // Redis update is idempotent, so it runs on every delivery (repairs a crash between DB commit and here).
        var seq = await store.ConfirmAsync(evt.ShowId, evt.HoldId, evt.UserId, evt.SeatIds);
        await deltas.PublishAsync(evt.ShowId, seq, "sold", evt.SeatIds);
    }
}

/// <summary>Payment failed or the sale lost a race: free the held seats immediately.</summary>
public sealed class BookingFailedHandler(SeatHoldStore store, SeatDeltaPublisher deltas)
    : IIntegrationEventHandler<BookingFailed>
{
    public async Task HandleAsync(BookingFailed evt, MessageContext ctx, CancellationToken ct)
    {
        var hold = await store.GetHoldAsync(evt.ShowId, evt.HoldId);
        if (hold is null) return;
        var outcome = await store.ReleaseAsync(evt.ShowId, evt.HoldId, hold.UserId, onlyIfExpired: false);
        if (outcome.Status == ReleaseStatus.Released)
            await deltas.PublishAsync(evt.ShowId, outcome.Sequence, "released", outcome.FreedSeats);
    }
}

public sealed class BookingExpiredHandler(SeatHoldStore store, SeatDeltaPublisher deltas)
    : IIntegrationEventHandler<BookingExpired>
{
    public async Task HandleAsync(BookingExpired evt, MessageContext ctx, CancellationToken ct)
    {
        var hold = await store.GetHoldAsync(evt.ShowId, evt.HoldId);
        if (hold is null) return;
        var outcome = await store.ReleaseAsync(evt.ShowId, evt.HoldId, hold.UserId, onlyIfExpired: false);
        if (outcome.Status == ReleaseStatus.Released)
            await deltas.PublishAsync(evt.ShowId, outcome.Sequence, "released", outcome.FreedSeats);
    }
}

/// <summary>A sold booking was cancelled: return its seats to sale.</summary>
public sealed class BookingCancelledHandler(NpgsqlDataSource db, ShowSeatRepository seats, SeatHoldStore store, SeatDeltaPublisher deltas)
    : IIntegrationEventHandler<BookingCancelled>
{
    public async Task HandleAsync(BookingCancelled evt, MessageContext ctx, CancellationToken ct)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            if (await Inbox.TryMarkProcessedAsync(conn, tx, ctx, nameof(BookingCancelledHandler)))
                await seats.MarkAvailableAsync(conn, tx, evt.ShowId, evt.BookingId, evt.SeatIds);
            await tx.CommitAsync(ct);
        }

        var seq = await store.UnbookAsync(evt.ShowId, evt.SeatIds);
        await deltas.PublishAsync(evt.ShowId, seq, "unsold", evt.SeatIds);
    }
}
