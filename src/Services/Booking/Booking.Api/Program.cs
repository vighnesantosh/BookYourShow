using Booking.Api.Application;
using Booking.Api.Clients;
using Booking.Api.Endpoints;
using Booking.Api.Handlers;
using BookYourShow.BuildingBlocks.Hosting;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("booking")
       .AddPostgres("Booking")
       .AddMessagingWithOutbox();

var services = builder.Configuration.GetSection("Services");

// Hot-path dependencies: retries with jitter, per-attempt timeout, circuit breaker.
builder.Services.AddHttpClient<InventoryClient>(c => c.BaseAddress = new Uri(services["Inventory"]!)).AddStandardResilienceHandler();
builder.Services.AddHttpClient<CatalogClient>(c => c.BaseAddress = new Uri(services["Catalog"]!)).AddStandardResilienceHandler();
builder.Services.AddHttpClient<PaymentClient>(c => c.BaseAddress = new Uri(services["Payment"]!)).AddStandardResilienceHandler();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<BookingSaga>();
builder.Services.AddHostedService<ReconciliationWorker>();

builder.Services
    .AddEventHandler<PaymentSucceeded, PaymentSucceededHandler>()
    .AddEventHandler<PaymentFailed, PaymentFailedHandler>()
    .AddEventHandler<HoldExpired, HoldExpiredHandler>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapBookingEndpoints();
app.Run();

public partial class Program;
