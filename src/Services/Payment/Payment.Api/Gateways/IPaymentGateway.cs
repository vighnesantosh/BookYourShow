using System.Security.Cryptography;
using System.Text;

namespace Payment.Api.Gateways;

public sealed record GatewayIntent(string GatewayRef, string ClientSecret);

/// <summary>Normalized gateway status.</summary>
public enum GatewayStatus { RequiresAction, Succeeded, Failed, Cancelled }

/// <summary>A verified webhook, normalized across gateways.</summary>
public sealed record GatewayWebhook(string EventId, string GatewayRef, GatewayStatus Status, long AmountMinor, string RawType);

/// <summary>
/// Adapter per payment provider (Razorpay for UPI-heavy India traffic, Stripe for international cards).
/// Card data never reaches our servers: the client pays with the gateway's SDK using ClientSecret (PCI SAQ-A).
/// </summary>
public interface IPaymentGateway
{
    string Name { get; }

    /// <summary>Create a payment intent / order. <paramref name="idempotencyKey"/> = bookingId.</summary>
    Task<GatewayIntent> CreateIntentAsync(Guid paymentId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct);

    Task<GatewayStatus> GetStatusAsync(string gatewayRef, CancellationToken ct);

    Task CancelAsync(string gatewayRef, CancellationToken ct);

    Task RefundAsync(string gatewayRef, long amountMinor, string idempotencyKey, CancellationToken ct);

    /// <summary>Verifies the signature over the raw body; returns null for events we do not act on.</summary>
    GatewayWebhook? VerifyWebhook(string rawBody, IHeaderDictionary headers);
}

public static class WebhookSignature
{
    public static string HmacSha256Hex(string secret, string payload) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));

    /// <summary>Constant-time comparison so the signature cannot be probed byte by byte.</summary>
    public static bool Matches(string expectedHex, string? providedHex) =>
        providedHex is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expectedHex), Encoding.ASCII.GetBytes(providedHex.Trim().ToLowerInvariant()));
}
