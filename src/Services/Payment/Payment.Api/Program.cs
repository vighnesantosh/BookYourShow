using System.Net.Http.Headers;
using System.Text;
using BookYourShow.BuildingBlocks.Hosting;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;
using Payment.Api.Application;
using Payment.Api.Endpoints;
using Payment.Api.Gateways;
using Payment.Api.Handlers;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("payment")
       .AddPostgres("Payment")
       .AddMessagingWithOutbox();

// Gateway adapter chosen by configuration; a secondary gateway can be switched in during an outage.
builder.Services.AddSingleton<FakeGateway>();
if (string.Equals(builder.Configuration["Payments:Gateway"], "razorpay", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddHttpClient<IPaymentGateway, RazorpayGateway>(c =>
    {
        c.BaseAddress = new Uri("https://api.razorpay.com/");
        var key = $"{builder.Configuration["Payments:Razorpay:KeyId"]}:{builder.Configuration["Payments:Razorpay:KeySecret"]}";
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(key)));
    }).AddStandardResilienceHandler();
}
else
{
    builder.Services.AddSingleton<IPaymentGateway>(sp => sp.GetRequiredService<FakeGateway>());
}

builder.Services.AddScoped<PaymentProcessor>();

builder.Services
    .AddEventHandler<BookingFailed, BookingFailedHandler>()
    .AddEventHandler<BookingExpired, BookingExpiredHandler>()
    .AddEventHandler<BookingCancelled, BookingCancelledHandler>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapPaymentEndpoints(app.Environment);
app.Run();

public partial class Program;
