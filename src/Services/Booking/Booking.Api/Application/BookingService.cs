using Booking.Api.Clients;
using Booking.Api.Domain;
using BookYourShow.BuildingBlocks.Outbox;
using BookYourShow.Contracts;
using Dapper;
using Npgsql;

namespace Booking.Api.Application;

public sealed record CreateBookingRequest(Guid ShowId, Guid HoldId);

public sealed record PaymentInstructions(Guid PaymentId, string Gateway, string GatewayRef, string ClientSecret);

public sealed record BookingView(
    Guid BookingId, Guid ShowId, Guid HoldId, string Status, long TotalMinor, string Currency,
    IReadOnlyList<BookingLine> Seats, Guid? PaymentId, string? FailureReason, DateTime CreatedAt);

public sealed record CreateBookingResult(BookingView Booking, PaymentInstructions? Payment, DateTimeOffset? PayBefore);

public abstract record ServiceResult<T>
{
    public sealed record Ok(T Value) : ServiceResult<T>;
    public sealed record Error(int Status, string Detail) : ServiceResult<T>;
}

/// <summary>Command side of the booking API: create (start the saga) and cancel.</summary>
public sealed class BookingService(
    NpgsqlDataSource db,
    InventoryClient inventory,
    CatalogClient catalog,
    PaymentClient payments,
    TimeProvider clock,
    ILogger<BookingService> logger)
{
    public static readonly TimeSpan PaymentWindow = TimeSpan.FromMinutes(10);

    public async Task<ServiceResult<CreateBookingResult>> CreateAsync(string userId, CreateBookingRequest request, CancellationToken ct)
    {
        // A booking already exists for this hold (retry without Idempotency-Key replay, or a second tab).
        var existing = await GetByHoldAsync(request.HoldId, ct);
        if (existing is not null)
            return existing.UserId == userId
                ? new ServiceResult<CreateBookingResult>.Ok(new CreateBookingResult(await ToViewAsync(existing, ct), null, null))
                : new ServiceResult<CreateBookingResult>.Error(404, "Hold not found.");

        // 1. Re-validate the hold with Seat Inventory.
        var hold = await inventory.GetActiveHoldAsync(request.ShowId, request.HoldId, ct);
        if (hold is null || hold.UserId != userId)
            return new ServiceResult<CreateBookingResult>.Error(409, "Your seat hold has expired. Please select seats again.");

        // 2. Price the seats from Catalog (snapshot stored on the booking).
        var pricing = await catalog.GetPricingAsync(request.ShowId, ct);
        if (pricing is null) return new ServiceResult<CreateBookingResult>.Error(404, "Show is not on sale.");
        var (lines, total) = Pricing.PriceSeats(hold.SeatIds, pricing.CategoryBySeat, pricing.PriceMinorByCategory);

        // 3. Persist PENDING_PAYMENT with its lines.
        var bookingId = Guid.CreateVersion7();
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            try
            {
                await conn.ExecuteAsync(
                    """
                    INSERT INTO booking (booking_id, user_id, show_id, hold_id, status, total_minor, currency)
                    VALUES (@BookingId, @UserId, @ShowId, @HoldId, 'PENDING_PAYMENT', @Total, @Currency)
                    """, new { BookingId = bookingId, UserId = userId, request.ShowId, request.HoldId, Total = total, pricing.Currency }, tx);
                await conn.ExecuteAsync(
                    "INSERT INTO booking_line (booking_id, seat_id, category, price_minor) VALUES (@BookingId, @SeatId, @Category, @PriceMinor)",
                    lines.Select(l => new { BookingId = bookingId, l.SeatId, l.Category, l.PriceMinor }), tx);
                await tx.CommitAsync(ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // A concurrent request created the booking for this hold first (unique index on hold_id).
                var winner = (await GetByHoldAsync(request.HoldId, ct))!;
                return new ServiceResult<CreateBookingResult>.Ok(new CreateBookingResult(await ToViewAsync(winner, ct), null, null));
            }
        }

        // 4. Extend the hold for the payment window so a slow 3-D Secure / UPI approval is not cut off.
        var payBefore = clock.GetUtcNow() + PaymentWindow;
        if (!await inventory.ExtendHoldAsync(request.ShowId, request.HoldId, userId, payBefore, ct))
        {
            await FailAsync(bookingId, "Hold expired before payment started", ct);
            return new ServiceResult<CreateBookingResult>.Error(409, "Your seat hold has expired. Please select seats again.");
        }

        // 5. Create the payment intent (idempotent on bookingId at the gateway).
        CreatePaymentResponse payment;
        try
        {
            payment = await payments.CreateAsync(new CreatePaymentRequest(bookingId, userId, total, pricing.Currency), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Payment intent creation failed for booking {BookingId}", bookingId);
            await FailAsync(bookingId, "Payment provider unavailable", ct);
            return new ServiceResult<CreateBookingResult>.Error(503, "Payments are temporarily unavailable. Your seats have been released; please try again shortly.");
        }

        await using (var conn = await db.OpenConnectionAsync(ct))
            await conn.ExecuteAsync("UPDATE booking SET payment_id = @PaymentId, updated_at = now() WHERE booking_id = @Id",
                new { payment.PaymentId, Id = bookingId });

        var view = (await GetAsync(bookingId, ct))!;
        return new ServiceResult<CreateBookingResult>.Ok(new CreateBookingResult(
            view,
            new PaymentInstructions(payment.PaymentId, payment.Gateway, payment.GatewayRef, payment.ClientSecret),
            payBefore));
    }

    /// <summary>User cancels. A pending booking fails (releases the hold); a confirmed one is cancelled and refunded.</summary>
    public async Task<ServiceResult<BookingView>> CancelAsync(string userId, Guid bookingId, CancellationToken ct)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            var booking = await conn.QuerySingleOrDefaultAsync<BookingRow>(BookingSaga.SelectBooking + " WHERE booking_id = @Id FOR UPDATE", new { Id = bookingId }, tx);
            if (booking is null || booking.UserId != userId) return new ServiceResult<BookingView>.Error(404, "Booking not found.");

            var seatIds = await BookingSaga.SeatIdsAsync(conn, tx, bookingId);
            switch (booking.Status)
            {
                case BookingStatus.PendingPayment:
                    await BookingSaga.FailInTxAsync(conn, tx, booking, seatIds, "Cancelled by customer", refund: false, booking.PaymentId);
                    break;

                case BookingStatus.Confirmed:
                    // Cancellation policy (cut-off before show time, fees) is organiser-specific: open question in the design.
                    await conn.ExecuteAsync(
                        """
                        INSERT INTO booking_seat_history (show_id, seat_id, booking_id, price_minor, sold_at)
                        SELECT show_id, seat_id, booking_id, price_minor, sold_at FROM booking_seat WHERE booking_id = @Id;
                        DELETE FROM booking_seat WHERE booking_id = @Id;
                        UPDATE booking SET status = 'CANCELLED', updated_at = now(), version = version + 1 WHERE booking_id = @Id;
                        """, new { Id = bookingId }, tx);
                    await OutboxWriter.AddAsync(conn, tx, new BookingCancelled(bookingId, booking.ShowId, booking.UserId, seatIds, booking.PaymentId, booking.TotalMinor));
                    break;

                default:
                    return new ServiceResult<BookingView>.Error(409, $"A {booking.Status.ToLowerInvariant()} booking cannot be cancelled.");
            }

            await tx.CommitAsync(ct);
        }

        return new ServiceResult<BookingView>.Ok((await GetAsync(bookingId, ct))!);
    }

    /// <summary>Loads a booking; when userId is given, only if that user owns it.</summary>
    public async Task<BookingView?> GetAsync(Guid bookingId, CancellationToken ct, string? userId = null)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<BookingRow>(
            BookingSaga.SelectBooking + " WHERE booking_id = @Id AND (@UserId::text IS NULL OR user_id = @UserId)",
            new { Id = bookingId, UserId = userId });
        return row is null ? null : await ToViewAsync(row, ct);
    }

    public async Task<IReadOnlyList<BookingView>> ListForUserAsync(string userId, int limit, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<BookingRow>(
            BookingSaga.SelectBooking + " WHERE user_id = @UserId ORDER BY created_at DESC LIMIT @Limit",
            new { UserId = userId, Limit = Math.Clamp(limit, 1, 100) });
        var views = new List<BookingView>();
        foreach (var row in rows) views.Add(await ToViewAsync(row, ct));
        return views;
    }

    private async Task FailAsync(Guid bookingId, string reason, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var booking = await conn.QuerySingleAsync<BookingRow>(BookingSaga.SelectBooking + " WHERE booking_id = @Id FOR UPDATE", new { Id = bookingId }, tx);
        if (booking.Status != BookingStatus.PendingPayment) return;
        await BookingSaga.FailInTxAsync(conn, tx, booking, await BookingSaga.SeatIdsAsync(conn, tx, bookingId), reason, refund: false, null);
        await tx.CommitAsync(ct);
    }

    private async Task<BookingRow?> GetByHoldAsync(Guid holdId, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<BookingRow>(BookingSaga.SelectBooking + " WHERE hold_id = @HoldId", new { HoldId = holdId });
    }

    private async Task<BookingView> ToViewAsync(BookingRow row, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var lines = (await conn.QueryAsync<BookingLine>(
            "SELECT seat_id AS SeatId, category AS Category, price_minor AS PriceMinor FROM booking_line WHERE booking_id = @Id ORDER BY seat_id",
            new { Id = row.BookingId })).AsList();
        return new BookingView(row.BookingId, row.ShowId, row.HoldId, row.Status, row.TotalMinor, row.Currency.Trim(),
            lines, row.PaymentId, row.FailureReason, DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc));
    }
}
