namespace BookYourShow.Contracts;

/// <summary>
/// Marker for every event that crosses a service boundary.
/// The event's CLR type name is used as the routing "type" attribute (SNS filter policies).
/// </summary>
public interface IIntegrationEvent;

public static class EventTypes
{
    public static string NameOf<T>() where T : IIntegrationEvent => typeof(T).Name;
    public static string NameOf(Type t) => t.Name;
}

// ---------- Catalog ----------

public sealed record SeatDefinition(string SeatId, string Category);

public sealed record ShowPublished(
    Guid ShowId,
    Guid EventId,
    Guid HallId,
    DateTimeOffset StartsAt,
    DateTimeOffset SaleOpensAt,
    IReadOnlyList<SeatDefinition> Seats) : IIntegrationEvent;

// ---------- Seat Inventory ----------

/// <summary>Sent to the inventory's own delayed queue when a hold is created.</summary>
public sealed record HoldExpiryCheck(Guid ShowId, Guid HoldId) : IIntegrationEvent;

/// <summary>A hold timed out before its booking was confirmed; its seats are free again.</summary>
public sealed record HoldExpired(Guid ShowId, Guid HoldId, IReadOnlyList<string> SeatIds) : IIntegrationEvent;

// ---------- Booking ----------

public sealed record BookingConfirmed(
    Guid BookingId, Guid ShowId, Guid HoldId, string UserId,
    IReadOnlyList<string> SeatIds, long AmountMinor, string Currency) : IIntegrationEvent;

public sealed record BookingFailed(
    Guid BookingId, Guid ShowId, Guid HoldId, IReadOnlyList<string> SeatIds,
    string Reason, bool RefundRequired, Guid? PaymentId) : IIntegrationEvent;

public sealed record BookingExpired(Guid BookingId, Guid ShowId, Guid HoldId, Guid? PaymentId) : IIntegrationEvent;

public sealed record BookingCancelled(
    Guid BookingId, Guid ShowId, string UserId, IReadOnlyList<string> SeatIds,
    Guid? PaymentId, long AmountMinor) : IIntegrationEvent;

// ---------- Payment ----------

public sealed record PaymentSucceeded(Guid PaymentId, Guid BookingId, long AmountMinor, string Currency, string GatewayRef) : IIntegrationEvent;

public sealed record PaymentFailed(Guid PaymentId, Guid BookingId, string Reason) : IIntegrationEvent;

public sealed record RefundCompleted(Guid PaymentId, Guid BookingId, long AmountMinor) : IIntegrationEvent;
