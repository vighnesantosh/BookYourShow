using BookYourShow.Contracts;
using Payment.Api.Application;
using Payment.Api.Gateways;

namespace Payment.Api.Endpoints;

public static class PaymentEndpoints
{
    public static IEndpointRouteBuilder MapPaymentEndpoints(this IEndpointRouteBuilder app, IWebHostEnvironment env)
    {
        // ---------- internal: called by Booking ----------
        var internalApi = app.MapGroup("/internal/payments").WithTags("Internal");

        internalApi.MapPost("/", async (CreatePaymentRequest body, PaymentProcessor processor, CancellationToken ct) =>
        {
            if (body.AmountMinor <= 0) return Results.Problem("Amount must be positive.", statusCode: 400);
            return Results.Ok(await processor.CreateAsync(body, ct));
        });

        internalApi.MapGet("/by-booking/{bookingId:guid}", async (Guid bookingId, bool? refresh, PaymentProcessor processor, CancellationToken ct) =>
            await processor.GetByBookingAsync(bookingId, refresh ?? false, ct) is { } view ? Results.Ok(view) : Results.NotFound());

        // ---------- gateway webhooks ----------
        // Production: API Gateway → Lambda verifies the signature and enqueues to SQS FIFO (group = bookingId),
        // and this same processing runs in the queue consumer. Locally the endpoint processes inline.
        app.MapPost("/webhooks/payments/{gateway}", async (string gateway, HttpRequest request, IPaymentGateway active, PaymentProcessor processor, CancellationToken ct) =>
        {
            if (!string.Equals(gateway, active.Name, StringComparison.OrdinalIgnoreCase)) return Results.NotFound();

            using var reader = new StreamReader(request.Body);
            var raw = await reader.ReadToEndAsync(ct);

            GatewayWebhook? webhook;
            try
            {
                webhook = active.VerifyWebhook(raw, request.Headers);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }

            if (webhook is not null) await processor.ApplyGatewayEventAsync(webhook, raw, ct);
            return Results.Ok();   // always 200 quickly once verified, so the gateway stops retrying
        }).WithTags("Webhooks");

        // ---------- development only: play the customer at the gateway ----------
        if (env.IsDevelopment())
        {
            app.MapPost("/dev/payments/{paymentId:guid}/simulate", async (
                Guid paymentId, string? outcome, bool? dropWebhook,
                PaymentProcessor processor, FakeGateway fake, CancellationToken ct) =>
            {
                var payment = await processor.FindAsync(paymentId, ct);
                if (payment is null) return Results.NotFound();

                var succeed = !string.Equals(outcome, "failed", StringComparison.OrdinalIgnoreCase);
                var (body, signature) = fake.Simulate(payment.GatewayRef, succeed, payment.AmountMinor);

                // dropWebhook=true leaves the result only at the "gateway", to demo reconciliation.
                if (dropWebhook != true)
                {
                    var headers = new HeaderDictionary { ["X-Fake-Signature"] = signature };
                    var webhook = fake.VerifyWebhook(body, headers)!;
                    await processor.ApplyGatewayEventAsync(webhook, body, ct);
                }

                return Results.Ok(new { paymentId, outcome = succeed ? "succeeded" : "failed", webhookDelivered = dropWebhook != true });
            }).WithTags("Development");
        }

        return app;
    }
}
