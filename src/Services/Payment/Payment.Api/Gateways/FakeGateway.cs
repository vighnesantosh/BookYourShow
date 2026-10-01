using System.Collections.Concurrent;
using System.Text.Json;

namespace Payment.Api.Gateways;

/// <summary>
/// Local stand-in for a real gateway. Payments are completed with POST /dev/payments/{id}/simulate,
/// which produces a webhook signed exactly like a real one, so the verification path is exercised.
/// </summary>
public sealed class FakeGateway(IConfiguration config) : IPaymentGateway
{
    private readonly ConcurrentDictionary<string, GatewayStatus> _state = new();
    private string Secret => config["Payments:Fake:WebhookSecret"] ?? throw new InvalidOperationException("Payments:Fake:WebhookSecret missing");

    public string Name => "fake";

    public Task<GatewayIntent> CreateIntentAsync(Guid paymentId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct)
    {
        var gatewayRef = $"fake_pi_{idempotencyKey}";
        _state.TryAdd(gatewayRef, GatewayStatus.RequiresAction);
        return Task.FromResult(new GatewayIntent(gatewayRef, $"{gatewayRef}_secret_{Guid.NewGuid():N}"));
    }

    public Task<GatewayStatus> GetStatusAsync(string gatewayRef, CancellationToken ct) =>
        Task.FromResult(_state.GetValueOrDefault(gatewayRef, GatewayStatus.RequiresAction));

    public Task CancelAsync(string gatewayRef, CancellationToken ct)
    {
        _state.TryUpdate(gatewayRef, GatewayStatus.Cancelled, GatewayStatus.RequiresAction);
        return Task.CompletedTask;
    }

    public Task RefundAsync(string gatewayRef, long amountMinor, string idempotencyKey, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Customer "pays" in the gateway UI. Returns the signed webhook the gateway would send.</summary>
    public (string Body, string Signature) Simulate(string gatewayRef, bool succeed, long amountMinor)
    {
        var status = succeed ? GatewayStatus.Succeeded : GatewayStatus.Failed;
        _state[gatewayRef] = status;
        var body = JsonSerializer.Serialize(new FakeWebhook($"evt_{Guid.NewGuid():N}", succeed ? "payment.succeeded" : "payment.failed", gatewayRef, amountMinor));
        return (body, WebhookSignature.HmacSha256Hex(Secret, body));
    }

    public GatewayWebhook? VerifyWebhook(string rawBody, IHeaderDictionary headers)
    {
        if (!WebhookSignature.Matches(WebhookSignature.HmacSha256Hex(Secret, rawBody), headers["X-Fake-Signature"]))
            throw new UnauthorizedAccessException("Invalid webhook signature.");

        var w = JsonSerializer.Deserialize<FakeWebhook>(rawBody)!;
        var status = w.Type switch
        {
            "payment.succeeded" => GatewayStatus.Succeeded,
            "payment.failed" => GatewayStatus.Failed,
            _ => (GatewayStatus?)null,
        };
        return status is null ? null : new GatewayWebhook(w.Id, w.GatewayRef, status.Value, w.Amount, w.Type);
    }

    private sealed record FakeWebhook(string Id, string Type, string GatewayRef, long Amount);
}
