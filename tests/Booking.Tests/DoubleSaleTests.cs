using Booking.Api.Application;
using Booking.Api.Domain;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Booking.Tests;

/// <summary>
/// Guard 2 of the design: even if two bookings for the same seat both get paid
/// (e.g. Valkey lost a hold in a failover), exactly one is confirmed and the other is refunded.
/// </summary>
public sealed class DoubleSaleTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:17").Build();
    private NpgsqlDataSource _db = null!;
    private BookingSaga _saga = null!;
    private readonly Guid _show = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = NpgsqlDataSource.Create(_pg.GetConnectionString());
        var schema = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Sql", "03-booking.sql")))
            .Replace(@"\connect booking", "");
        await using var conn = await _db.OpenConnectionAsync();
        await conn.ExecuteAsync(schema);
        _saga = new BookingSaga(_db, NullLogger<BookingSaga>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    [Fact]
    public async Task Fifty_paid_bookings_for_the_same_seats_produce_exactly_one_sale()
    {
        var bookings = new List<Guid>();
        for (var i = 0; i < 50; i++) bookings.Add(await InsertPendingBookingAsync($"user-{i}", ["A1", "A2"], 100_000));

        var outcomes = await Task.WhenAll(bookings.Select(id => _saga.ConfirmAsync(id, Guid.NewGuid(), 100_000, CancellationToken.None)));

        Assert.Equal(1, outcomes.Count(o => o == BookingStatus.Confirmed));
        Assert.Equal(49, outcomes.Count(o => o == BookingStatus.Failed));

        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal(2, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM booking_seat WHERE show_id = @Show", new { Show = _show }));

        // Every loser asked Payment for a refund through the outbox.
        var refunds = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM outbox WHERE event_type = 'BookingFailed' AND (payload->>'refundRequired')::boolean");
        Assert.Equal(49, refunds);
    }

    [Fact]
    public async Task Duplicate_payment_event_is_harmless()
    {
        var id = await InsertPendingBookingAsync("alice", ["B1"], 50_000);
        var payment = Guid.NewGuid();

        Assert.Equal(BookingStatus.Confirmed, await _saga.ConfirmAsync(id, payment, 50_000, CancellationToken.None));
        Assert.Equal(BookingStatus.Confirmed, await _saga.ConfirmAsync(id, payment, 50_000, CancellationToken.None));

        await using var conn = await _db.OpenConnectionAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<long>("SELECT count(*) FROM outbox WHERE event_type = 'BookingConfirmed'"));
    }

    [Fact]
    public async Task Late_payment_after_hold_expiry_still_confirms_if_seats_are_free()
    {
        var id = await InsertPendingBookingAsync("carol", ["C1"], 30_000, out var holdId);
        await _saga.ExpireByHoldAsync(holdId, CancellationToken.None);

        Assert.Equal(BookingStatus.Confirmed, await _saga.ConfirmAsync(id, Guid.NewGuid(), 30_000, CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_amount_fails_and_refunds()
    {
        var id = await InsertPendingBookingAsync("dave", ["D1"], 30_000);
        Assert.Equal(BookingStatus.Failed, await _saga.ConfirmAsync(id, Guid.NewGuid(), 1, CancellationToken.None));
    }

    private Task<Guid> InsertPendingBookingAsync(string user, string[] seats, long total) =>
        InsertPendingBookingAsync(user, seats, total, out _);

    private Task<Guid> InsertPendingBookingAsync(string user, string[] seats, long total, out Guid holdId)
    {
        var bookingId = Guid.CreateVersion7();
        var hold = Guid.NewGuid();
        holdId = hold;
        return Insert();

        async Task<Guid> Insert()
        {
            await using var conn = await _db.OpenConnectionAsync();
            await conn.ExecuteAsync(
                """
                INSERT INTO booking (booking_id, user_id, show_id, hold_id, status, total_minor, currency)
                VALUES (@BookingId, @User, @Show, @Hold, 'PENDING_PAYMENT', @Total, 'INR')
                """, new { BookingId = bookingId, User = user, Show = _show, Hold = hold, Total = total });
            foreach (var seat in seats)
                await conn.ExecuteAsync(
                    "INSERT INTO booking_line (booking_id, seat_id, category, price_minor) VALUES (@BookingId, @Seat, 'GOLD', @Price)",
                    new { BookingId = bookingId, Seat = seat, Price = total / seats.Length });
            return bookingId;
        }
    }
}
