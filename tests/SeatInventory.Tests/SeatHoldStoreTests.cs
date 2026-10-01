using SeatInventory.Api;
using SeatInventory.Api.Domain;
using StackExchange.Redis;
using Testcontainers.Redis;
using Xunit;

namespace SeatInventory.Tests;

/// <summary>Runs the real Lua scripts against a real Valkey/Redis in Docker.</summary>
public sealed class SeatHoldStoreTests : IAsyncLifetime
{
    private readonly RedisContainer _redis = new RedisBuilder().WithImage("valkey/valkey:8").Build();
    private ConnectionMultiplexer _mux = null!;
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow); // real "now": Redis TTLs are wall-clock
    private SeatHoldStore _store = null!;
    private readonly Guid _show = Guid.NewGuid();
    private static readonly string[] AllSeats = Enumerable.Range(0, 5).SelectMany(r => Enumerable.Range(1, 20).Select(n => $"{(char)('A' + r)}{n}")).ToArray();

    public async Task InitializeAsync()
    {
        await _redis.StartAsync();
        _mux = await ConnectionMultiplexer.ConnectAsync(_redis.GetConnectionString());
        _store = new SeatHoldStore(_mux, _clock);
        await _store.LoadShowAsync(_show, AllSeats, []);
    }

    public async Task DisposeAsync()
    {
        await _mux.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [Fact]
    public async Task Hold_is_all_or_nothing()
    {
        var first = await _store.HoldAsync(_show, "alice", ["A1", "A2"]);
        Assert.True(first.Success);

        var overlap = await _store.HoldAsync(_show, "bob", ["A3", "A2"]);
        Assert.False(overlap.Success);
        Assert.Equal(HoldRejection.SeatHeld, overlap.Rejection);
        Assert.Equal("A2", overlap.ConflictSeat);

        // A3 must not have been taken by the failed attempt.
        Assert.True((await _store.HoldAsync(_show, "bob", ["A3"])).Success);
    }

    [Fact]
    public async Task One_active_hold_per_user_per_show()
    {
        Assert.True((await _store.HoldAsync(_show, "alice", ["B1"])).Success);
        var second = await _store.HoldAsync(_show, "alice", ["B2"]);
        Assert.Equal(HoldRejection.UserHasActiveHold, second.Rejection);
    }

    [Fact]
    public async Task Expired_hold_frees_seats_lazily()
    {
        Assert.True((await _store.HoldAsync(_show, "alice", ["C1"])).Success);
        _clock.Advance(HoldPolicy.HoldDuration + TimeSpan.FromSeconds(1));

        var bob = await _store.HoldAsync(_show, "bob", ["C1"]);
        Assert.True(bob.Success);
    }

    [Fact]
    public async Task Expiry_release_does_not_touch_seats_now_held_by_someone_else()
    {
        var alice = await _store.HoldAsync(_show, "alice", ["C5"]);
        _clock.Advance(HoldPolicy.HoldDuration + TimeSpan.FromSeconds(1));
        var bob = await _store.HoldAsync(_show, "bob", ["C5"]);

        var release = await _store.ReleaseAsync(_show, alice.HoldId, "alice", onlyIfExpired: true);
        Assert.Equal(ReleaseStatus.Released, release.Status);
        Assert.Empty(release.FreedSeats);

        var snapshot = await _store.SnapshotAsync(_show);
        Assert.Contains("C5", snapshot.Held);   // still Bob's
        Assert.True((await _store.GetHoldAsync(_show, bob.HoldId))!.IsActive(_clock.GetUtcNow()));
    }

    [Fact]
    public async Task Extended_hold_is_not_released_by_the_first_expiry_check()
    {
        var hold = await _store.HoldAsync(_show, "alice", ["D1"]);
        Assert.True(await _store.ExtendAsync(_show, hold.HoldId, "alice", _clock.GetUtcNow() + HoldPolicy.PaymentHoldDuration));
        _clock.Advance(HoldPolicy.HoldDuration + TimeSpan.FromSeconds(1));

        var check = await _store.ReleaseAsync(_show, hold.HoldId, "alice", onlyIfExpired: true);
        Assert.Equal(ReleaseStatus.NotExpired, check.Status);
        Assert.False((await _store.HoldAsync(_show, "bob", ["D1"])).Success);
    }

    [Fact]
    public async Task Confirmed_seats_are_sold_and_cannot_be_released()
    {
        var hold = await _store.HoldAsync(_show, "alice", ["E1", "E2"]);
        await _store.ConfirmAsync(_show, hold.HoldId, "alice", ["E1", "E2"]);

        Assert.Equal(ReleaseStatus.Confirmed, (await _store.ReleaseAsync(_show, hold.HoldId, "alice", false)).Status);
        var again = await _store.HoldAsync(_show, "bob", ["E1"]);
        Assert.Equal(HoldRejection.SeatBooked, again.Rejection);

        // The user may hold again after confirming (their userhold key is cleared).
        Assert.True((await _store.HoldAsync(_show, "alice", ["E3"])).Success);
    }

    [Fact]
    public async Task Unknown_seat_and_unknown_show_are_rejected()
    {
        Assert.Equal(HoldRejection.UnknownSeat, (await _store.HoldAsync(_show, "alice", ["Z99"])).Rejection);
        Assert.Equal(HoldRejection.ShowNotOnSale, (await _store.HoldAsync(Guid.NewGuid(), "alice", ["A1"])).Rejection);
    }

    [Fact]
    public async Task Two_thousand_concurrent_users_never_hold_the_same_seat()
    {
        var random = new Random(42);
        var attempts = Enumerable.Range(0, 2000).Select(i =>
        {
            var seats = AllSeats.OrderBy(_ => random.Next()).Take(random.Next(1, 5)).ToArray();
            return (User: $"user-{i}", Seats: seats);
        }).ToList();

        var results = await Task.WhenAll(attempts.Select(async a => (a.Seats, Outcome: await _store.HoldAsync(_show, a.User, a.Seats))));

        var heldSeats = results.Where(r => r.Outcome.Success).SelectMany(r => r.Seats).ToList();
        Assert.Equal(heldSeats.Count, heldSeats.Distinct().Count());     // no seat in two holds

        var snapshot = await _store.SnapshotAsync(_show);
        Assert.Equal(heldSeats.OrderBy(s => s), snapshot.Held.OrderBy(s => s));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
