using System.Text.Json.Nodes;

namespace Payment.Api.Gateways;

/// <summary>
/// Razorpay adapter over its REST API (Orders, Payments, Refunds) — UPI, cards, wallets for India.
/// Webhook: X-Razorpay-Signature = HMAC-SHA256(rawBody, webhookSecret); X-Razorpay-Event-Id dedupes.
/// Basic auth (KeyId:KeySecret) is set on the HttpClient in Program.cs; secrets come from Secrets Manager.
/// </summary>
public sealed class RazorpayGateway(HttpClient http, IConfiguration config) : IPaymentGateway
{
    public string Name => "razorpay";

    private string WebhookSecret => config["Payments:Razorpay:WebhookSecret"]!;

    public async Task<GatewayIntent> CreateIntentAsync(Guid paymentId, long amountMinor, string currency, string idempotencyKey, CancellationToken ct)
    {
        // receipt is unique per booking; Razorpay returns the existing order for a repeated receipt in our flow
        // because Payment checks its own table (unique booking_id) before calling the gateway.
        using var response = await http.PostAsJsonAsync("v1/orders", new
        {
            amount = amountMinor,
            currency,
            receipt = idempotencyKey,
            notes = new { paymentId = paymentId.ToString() },
        }, ct);
        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
        var orderId = order!["id"]!.GetValue<string>();
        // The client opens Razorpay Checkout with this order id; no secret is exposed.
        return new GatewayIntent(orderId, orderId);
    }

    public async Task<GatewayStatus> GetStatusAsync(string gatewayRef, CancellationToken ct)
    {
        var order = await http.GetFromJsonAsync<JsonObject>($"v1/orders/{gatewayRef}", ct);
        return order!["status"]!.GetValue<string>() switch
        {
            "paid" => GatewayStatus.Succeeded,
            _ => GatewayStatus.RequiresAction, // "created" / "attempted": customer has not completed payment
        };
    }

    // Razorpay orders cannot be cancelled; an unpaid order simply lapses. A late payment is refunded by the saga.
    public Task CancelAsync(string gatewayRef, CancellationToken ct) => Task.CompletedTask;

    public async Task RefundAsync(string gatewayRef, long amountMinor, string idempotencyKey, CancellationToken ct)
    {
        var payments = await http.GetFromJsonAsync<JsonObject>($"v1/orders/{gatewayRef}/payments", ct);
        var captured = payments!["items"]!.AsArray()
            .FirstOrDefault(p => p!["status"]!.GetValue<string>() == "captured")
            ?? throw new InvalidOperationException($"No captured payment on order {gatewayRef}");

        using var response = await http.PostAsJsonAsync($"v1/payments/{captured["id"]!.GetValue<string>()}/refund",
            new { amount = amountMinor, receipt = idempotencyKey }, ct);
        response.EnsureSuccessStatusCode();
    }

    public GatewayWebhook? VerifyWebhook(string rawBody, IHeaderDictionary headers)
    {
        if (!WebhookSignature.Matches(WebhookSignature.HmacSha256Hex(WebhookSecret, rawBody), headers["X-Razorpay-Signature"]))
            throw new UnauthorizedAccessException("Invalid webhook signature.");

        var root = JsonNode.Parse(rawBody)!;
        var type = root["event"]!.GetValue<string>();
        var status = type switch
        {
            "order.paid" or "payment.captured" => GatewayStatus.Succeeded,
            "payment.failed" => GatewayStatus.Failed,
            _ => (GatewayStatus?)null,
        };
        if (status is null) return null;

        var payment = root["payload"]!["payment"]!["entity"]!;
        var eventId = headers["X-Razorpay-Event-Id"].ToString() is { Length: > 0 } id ? id : $"{type}:{payment["id"]}";
        return new GatewayWebhook(eventId, payment["order_id"]!.GetValue<string>(), status.Value, payment["amount"]!.GetValue<long>(), type);
    }
}
