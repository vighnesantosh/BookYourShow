using BookYourShow.BuildingBlocks.Hosting;
using Catalog.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults("catalog")
       .AddPostgres("Catalog")
       .AddMessagingWithOutbox();

var app = builder.Build();
app.UseServiceDefaults();
app.MapAdminEndpoints();
app.MapPublicEndpoints();
app.Run();

public partial class Program;
