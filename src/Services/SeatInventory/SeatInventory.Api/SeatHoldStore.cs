using System.Reflection;
using SeatInventory.Api.Domain;
using StackExchange.Redis;

namespace SeatInventory.Api;

/// <summary>
/// Valkey/Redis access for seat holds. Every operation is one Lua script, so each is atomic.
/// All keys of a show carry the hash tag {showId}: in cluster mode they live on one shard.
/// </summary>
public sealed class SeatHoldStore(IConnectionMultiplexer redis, TimeProvider clock)
{
    private static readonly Dictionary<string, string> Scripts = LoadScripts();
    private IDatabase Db => redis.GetDatabase();

    // ---------- keys ----------
    private static string Tag(Guid showId) => "{" + showId.ToString("N") + "}";
    private static RedisKey Holds(Guid s) => $"holds:{Tag(s)}";
    private static RedisKey Booked(Guid s) => $"booked:{Tag(s)}";
    private static RedisKey Owner(Guid s) => $"owner:{Tag(s)}";
    private static RedisKey Seq(Guid s) => $"seq:{Tag(s)}";
    private static RedisKey ValidSeats(Guid s) => $"seats:{Tag(s)}";
    private static RedisKey Hold(Guid s, Guid h) => $"hold:{Tag(s)}:{h:N}";
    private static RedisKey UserHold(Guid s, string userId) => $"userhold:{Tag(s)}:{userId}";

    private long NowMs => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<HoldOutcome> HoldAsync(Guid showId, string userId, IReadOnlyList<string> seatIds)
    {
        var holdId = Guid.CreateVersion7();
        var now = NowMs;
        var expiry = now + (long)HoldPolicy.HoldDuration.TotalMilliseconds;

        RedisValue[] args =
        [
            now, expiry, (long)HoldPolicy.Grace.TotalMilliseconds, userId, holdId.ToString("N"),
            .. seatIds.Select(s => (RedisValue)s),
        ];

        var r = (RedisResult[])(await Db.ScriptEvaluateAsync(Scripts["hold"],
            [Holds(showId), Booked(showId), Owner(showId), Hold(showId, holdId), UserHold(showId, userId), Seq(showId), ValidSeats(showId)],
            args))!;

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiry);
        return (int)r[0] == 1
            ? new HoldOutcome(true, holdId, expiresAt, (long)r[1], HoldRejection.None, null)
            : new HoldOutcome(false, Guid.Empty, default, 0, HoldOutcome.ParseRejection((string)r[1]!), r.Length > 2 ? (string?)r[2] : null);
    }

    public async Task<ReleaseOutcome> ReleaseAsync(Guid showId, Guid holdId, string userId, bool onlyIfExpired)
    {
        var r = (RedisResult[])(await Db.ScriptEvaluateAsync(Scripts["release"],
            [Holds(showId), Owner(showId), Hold(showId, holdId), UserHold(showId, userId), Seq(showId)],
            [holdId.ToString("N"), onlyIfExpired ? "1" : "0", NowMs]))!;

        if ((int)r[0] == 1)
            return new ReleaseOutcome(ReleaseStatus.Released, (long)r[1], r.Skip(2).Select(x => (string)x!).ToList(), null);

        return (string)r[1]! switch
        {
            "NOT_FOUND" => new ReleaseOutcome(ReleaseStatus.NotFound, 0, [], null),
            "CONFIRMED" => new ReleaseOutcome(ReleaseStatus.Confirmed, 0, [], null),
            "ALREADY_RELEASED" => new ReleaseOutcome(ReleaseStatus.AlreadyReleased, 0, [], null),
            "NOT_EXPIRED" => new ReleaseOutcome(ReleaseStatus.NotExpired, 0, [], DateTimeOffset.FromUnixTimeMilliseconds(long.Parse((string)r[2]!))),
            var other => throw new InvalidOperationException($"Unexpected release result {other}"),
        };
    }

    /// <summary>Extends an active hold; returns false when it is gone, released or already expired.</summary>
    public async Task<bool> ExtendAsync(Guid showId, Guid holdId, string userId, DateTimeOffset newExpiry)
    {
        var r = (RedisResult[])(await Db.ScriptEvaluateAsync(Scripts["extend"],
            [Holds(showId), Owner(showId), Hold(showId, holdId), UserHold(showId, userId)],
            [holdId.ToString("N"), newExpiry.ToUnixTimeMilliseconds(), (long)HoldPolicy.Grace.TotalMilliseconds, NowMs]))!;
        return (int)r[0] == 1;
    }

    public async Task<long> ConfirmAsync(Guid showId, Guid holdId, string userId, IReadOnlyList<string> seatIds)
    {
        var r = (RedisResult[])(await Db.ScriptEvaluateAsync(Scripts["confirm"],
            [Holds(showId), Owner(showId), Booked(showId), Hold(showId, holdId), UserHold(showId, userId), Seq(showId)],
            [holdId.ToString("N"), .. seatIds.Select(s => (RedisValue)s)]))!;
        return (long)r[1];
    }

    public async Task<long> UnbookAsync(Guid showId, IReadOnlyList<string> seatIds) =>
        (long)(await Db.ScriptEvaluateAsync(Scripts["unbook"], [Booked(showId), Seq(showId)], seatIds.Select(s => (RedisValue)s).ToArray()));

    public async Task<AvailabilitySnapshot> SnapshotAsync(Guid showId)
    {
        var r = (RedisResult[])(await Db.ScriptEvaluateAsync(Scripts["snapshot"], [Holds(showId), Booked(showId), Seq(showId)], [NowMs]))!;
        return new AvailabilitySnapshot(showId, (long)r[0], ToStrings(r[1]), ToStrings(r[2]));
    }

    public async Task<HoldRecord?> GetHoldAsync(Guid showId, Guid holdId)
    {
        var fields = await Db.HashGetAllAsync(Hold(showId, holdId));
        if (fields.Length == 0) return null;
        var map = fields.ToDictionary(f => (string)f.Name!, f => (string)f.Value!);
        return new HoldRecord(
            holdId, showId, map["user"],
            map["seats"].Split(',', StringSplitOptions.RemoveEmptyEntries),
            DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(map["expires"])),
            map["state"]);
    }

    /// <summary>Loads (or reloads) a show: valid seats and sold seats from the durable table.</summary>
    public async Task LoadShowAsync(Guid showId, IReadOnlyList<string> allSeats, IReadOnlyList<string> bookedSeats)
    {
        var tx = Db.CreateTransaction();
        _ = tx.KeyDeleteAsync(ValidSeats(showId));
        _ = tx.KeyDeleteAsync(Booked(showId));
        if (allSeats.Count > 0) _ = tx.SetAddAsync(ValidSeats(showId), allSeats.Select(s => (RedisValue)s).ToArray());
        if (bookedSeats.Count > 0) _ = tx.SetAddAsync(Booked(showId), bookedSeats.Select(s => (RedisValue)s).ToArray());
        _ = tx.StringIncrementAsync(Seq(showId));
        await tx.ExecuteAsync();
    }

    private static string[] ToStrings(RedisResult r) => ((RedisResult[])r!).Select(x => (string)x!).ToArray();

    private static Dictionary<string, string> LoadScripts()
    {
        var asm = Assembly.GetExecutingAssembly();
        return asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".lua", StringComparison.Ordinal))
            .ToDictionary(
                n => Path.GetFileNameWithoutExtension(n).Split('.').Last(),
                n =>
                {
                    using var reader = new StreamReader(asm.GetManifestResourceStream(n)!);
                    return reader.ReadToEnd();
                });
    }
}
