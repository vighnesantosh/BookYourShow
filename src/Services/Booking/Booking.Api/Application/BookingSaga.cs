using Booking.Api.Domain;
using BookYourShow.BuildingBlocks.Outbox;
using BookYourShow.Contracts;
using Dapper;
using Npgsql;

namespace Booking.Api.Application;

internal sealed record BookingRow(
    Guid BookingId, string UserId, Guid ShowId, Guid HoldId, string Status,
    long TotalMinor, string Currency, Guid? PaymentId, string? FailureReason, DateTime CreatedAt);

/// <summary>
/// Saga steps driven by payment and hold events. State lives in Aurora, every step runs in one
/// transaction with its outbox event, so any instance can resume after a crash (design §6).
/// </summary>
public sealed class BookingSaga(NpgsqlDataSource db, ILogger<BookingSaga> logger)
{
    internal const string SelectBooking =
        """
        SELECT booking_id AS BookingId, user_id AS UserId, show_id AS ShowId, hold_id AS HoldId, status AS Status,
               total_minor AS TotalMinor, currency AS Currency, payment_id AS PaymentId,
               failure_reason AS FailureReason, created_at AS CreatedAt
        FROM booking
        """;

    /// <summary>
    /// Payment succeeded: try to turn the booking's lines into sold seats.
    /// The booking_seat primary key (show_id, seat_id) makes a double sale impossible:
    /// if any seat is already sold, the whole confirmation is rolled back and the payment refunded.
    /// </summary>
    public async Task<string> ConfirmAsync(Guid bookingId, Guid paymentId, long amountMinor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(SelectBooking + " WHERE booking_id = @Id FOR UPDATE", new { Id = bookingId }, tx);
        if (booking is null)
        {
            logger.LogWarning("Payment {PaymentId} for unknown booking {BookingId}", paymentId, bookingId);
            return "UNKNOWN";
        }

        if (booking.Status == BookingStatus.Confirmed) return booking.Status;   // duplicate delivery

        var lines = (await conn.QueryAsync<BookingLine>(
            "SELECT seat_id AS SeatId, category AS Category, price_minor AS PriceMinor FROM booking_line WHERE booking_id = @Id",
            new { Id = bookingId }, tx)).AsList();
        var seatIds = lines.Select(l => l.SeatId).ToList();

        if (!BookingStatus.AcceptsPayment(booking.Status))
        {
            // Money arrived for a booking that is already closed (failed/cancelled): give it back.
            await OutboxWriter.AddAsync(conn, tx, new BookingFailed(bookingId, booking.ShowId, booking.HoldId, seatIds,
                $"Payment received while booking was {booking.Status}", RefundRequired: true, paymentId));
            await tx.CommitAsync(ct);
            return booking.Status;
        }

        if (amountMinor != booking.TotalMinor)
        {
            await FailInTxAsync(conn, tx, booking, seatIds, "Paid amount does not match booking total", refund: true, paymentId);
            await tx.CommitAsync(ct);
            return BookingStatus.Failed;
        }

        var sold = await conn.ExecuteAsync(
            """
            INSERT INTO booking_seat (show_id, seat_id, booking_id, price_minor)
            SELECT @ShowId, seat_id, booking_id, price_minor FROM booking_line WHERE booking_id = @BookingId
            ORDER BY seat_id          -- same lock order in every transaction: no deadlocks between racing bookings
            ON CONFLICT (show_id, seat_id) DO NOTHING
            """, new { booking.ShowId, BookingId = bookingId }, tx);

        if (sold != lines.Count)
        {
            // Lost the race (only possible if the hold expired or Valkey lost it): undo partial rows, refund.
            await conn.ExecuteAsync("DELETE FROM booking_seat WHERE booking_id = @Id", new { Id = bookingId }, tx);
            await FailInTxAsync(conn, tx, booking, seatIds, "Seats were sold to another customer", refund: true, paymentId);
            await tx.CommitAsync(ct);
            logger.LogWarning("Booking {BookingId} lost its seats after payment; refund requested", bookingId);
            return BookingStatus.Failed;
        }

        BookingStatus.EnsureTransition(booking.Status, BookingStatus.Confirmed);
        await conn.ExecuteAsync(
            """
            UPDATE booking SET status = 'CONFIRMED', payment_id = @PaymentId, updated_at = now(), version = version + 1
            WHERE booking_id = @Id
            """, new { Id = bookingId, PaymentId = paymentId }, tx);
        await OutboxWriter.AddAsync(conn, tx, new BookingConfirmed(
            bookingId, booking.ShowId, booking.HoldId, booking.UserId, seatIds, booking.TotalMinor, booking.Currency.Trim()));

        await tx.CommitAsync(ct);
        return BookingStatus.Confirmed;
    }

    /// <summary>Payment declined or cancelled: fail the booking so the hold is released at once.</summary>
    public async Task FailAsync(Guid bookingId, string reason, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(SelectBooking + " WHERE booking_id = @Id FOR UPDATE", new { Id = bookingId }, tx);
        if (booking is null || !BookingStatus.CanTransition(booking.Status, BookingStatus.Failed) || booking.Status != BookingStatus.PendingPayment)
            return;

        var seatIds = await SeatIdsAsync(conn, tx, bookingId);
        await FailInTxAsync(conn, tx, booking, seatIds, reason, refund: false, booking.PaymentId);
        await tx.CommitAsync(ct);
    }

    /// <summary>Seat Inventory reports the hold timed out.</summary>
    public async Task ExpireByHoldAsync(Guid holdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(SelectBooking + " WHERE hold_id = @HoldId FOR UPDATE", new { HoldId = holdId }, tx);
        if (booking is null || booking.Status != BookingStatus.PendingPayment) return;

        await conn.ExecuteAsync(
            "UPDATE booking SET status = 'EXPIRED', failure_reason = 'Hold expired before payment', updated_at = now(), version = version + 1 WHERE booking_id = @Id",
            new { Id = booking.BookingId }, tx);
        await OutboxWriter.AddAsync(conn, tx, new BookingExpired(booking.BookingId, booking.ShowId, booking.HoldId, booking.PaymentId));
        await tx.CommitAsync(ct);
    }

    internal static async Task FailInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, BookingRow booking,
        IReadOnlyList<string> seatIds, string reason, bool refund, Guid? paymentId)
    {
        BookingStatus.EnsureTransition(booking.Status, BookingStatus.Failed);
        await conn.ExecuteAsync(
            """
            UPDATE booking SET status = 'FAILED', failure_reason = @Reason, payment_id = COALESCE(@PaymentId, payment_id),
                   updated_at = now(), version = version + 1
            WHERE booking_id = @Id
            """, new { Id = booking.BookingId, Reason = reason, PaymentId = paymentId }, tx);
        await OutboxWriter.AddAsync(conn, tx, new BookingFailed(booking.BookingId, booking.ShowId, booking.HoldId, seatIds, reason, refund, paymentId));
    }

    internal static async Task<IReadOnlyList<string>> SeatIdsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid bookingId) =>
        (await conn.QueryAsync<string>("SELECT seat_id FROM booking_line WHERE booking_id = @Id ORDER BY seat_id", new { Id = bookingId }, tx)).AsList();
}
