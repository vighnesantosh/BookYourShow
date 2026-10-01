namespace BookYourShow.Contracts;

// Synchronous (HTTP) contracts used on the booking hot path.

/// <summary>Seat Inventory → Booking: a validated hold.</summary>
public sealed record HoldView(Guid HoldId, Guid ShowId, string UserId, IReadOnlyList<string> SeatIds, DateTimeOffset ExpiresAt);

/// <summary>Catalog → Booking: price per seat category for a show.</summary>
public sealed record ShowPricing(Guid ShowId, string Currency, IReadOnlyDictionary<string, long> PriceMinorByCategory, IReadOnlyDictionary<string, string> CategoryBySeat);

/// <summary>Booking → Payment: create a payment intent.</summary>
public sealed record CreatePaymentRequest(Guid BookingId, string UserId, long AmountMinor, string Currency);

public sealed record CreatePaymentResponse(Guid PaymentId, string Gateway, string GatewayRef, string ClientSecret, string Status);

public sealed record PaymentStatusView(Guid PaymentId, Guid BookingId, string Status, long AmountMinor, string Currency);
