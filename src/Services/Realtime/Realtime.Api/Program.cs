using BookYourShow.BuildingBlocks.Hosting;
using Realtime.Api;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("realtime");

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Valkey")!));

// Backplane on its own small Valkey cluster in production so fan-out never slows seat holds.
builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString("Backplane")!, o =>
        o.Configuration.ChannelPrefix = RedisChannel.Literal("bys-realtime"));

builder.Services.AddHostedService<SeatDeltaBroadcaster>();

var app = builder.Build();
app.UseServiceDefaults();
app.UseDefaultFiles();
app.UseStaticFiles();          // demo seat-map page at /index.html (Development convenience)
app.MapHub<SeatHub>("/hub");
app.Run();
