using BookYourShow.BuildingBlocks.Hosting;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.Contracts;
using SeatInventory.Api;
using SeatInventory.Api.Endpoints;
using SeatInventory.Api.Handlers;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("seat-inventory")
       .AddPostgres("Inventory")
       .AddMessagingWithOutbox();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Valkey")!));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SeatHoldStore>();
builder.Services.AddSingleton<SeatDeltaPublisher>();
builder.Services.AddSingleton<ShowSeatRepository>();

builder.Services
    .AddEventHandler<ShowPublished, ShowPublishedHandler>()
    .AddEventHandler<HoldExpiryCheck, HoldExpiryCheckHandler>()
    .AddEventHandler<BookingConfirmed, BookingConfirmedHandler>()
    .AddEventHandler<BookingFailed, BookingFailedHandler>()
    .AddEventHandler<BookingExpired, BookingExpiredHandler>()
    .AddEventHandler<BookingCancelled, BookingCancelledHandler>();

var app = builder.Build();
app.UseServiceDefaults();
app.MapInventoryEndpoints();
app.Run();

public partial class Program;
