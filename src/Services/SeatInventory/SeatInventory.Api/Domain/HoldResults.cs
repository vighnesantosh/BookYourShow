namespace SeatInventory.Api.Domain;

public static class HoldPolicy
{
    public const int MaxSeatsPerHold = 10;
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PaymentHoldDuration = TimeSpan.FromMinutes(10);

    /// <summary>The hold record outlives its expiry so the delayed expiry check can still read it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);
}

public enum HoldRejection
{
    None,
    ShowNotOnSale,
    UserHasActiveHold,
    UnknownSeat,
    SeatBooked,
    SeatHeld,
}

public sealed record HoldOutcome(bool Success, Guid HoldId, DateTimeOffset ExpiresAt, long Sequence, HoldRejection Rejection, string? ConflictSeat)
{
    public static HoldRejection ParseRejection(string code) => code switch
    {
        "SHOW_NOT_ON_SALE" => HoldRejection.ShowNotOnSale,
        "USER_HAS_ACTIVE_HOLD" => HoldRejection.UserHasActiveHold,
        "UNKNOWN_SEAT" => HoldRejection.UnknownSeat,
        "SEAT_BOOKED" => HoldRejection.SeatBooked,
        "SEAT_HELD" => HoldRejection.SeatHeld,
        _ => throw new InvalidOperationException($"Unknown hold rejection {code}"),
    };
}

public enum ReleaseStatus { Released, NotFound, Confirmed, AlreadyReleased, NotExpired }

public sealed record ReleaseOutcome(ReleaseStatus Status, long Sequence, IReadOnlyList<string> FreedSeats, DateTimeOffset? ExpiresAt);

public sealed record HoldRecord(Guid HoldId, Guid ShowId, string UserId, IReadOnlyList<string> SeatIds, DateTimeOffset ExpiresAt, string State)
{
    public bool IsActive(DateTimeOffset now) => State == "ACTIVE" && ExpiresAt > now;
}

public sealed record AvailabilitySnapshot(Guid ShowId, long Sequence, IReadOnlyList<string> Booked, IReadOnlyList<string> Held);
