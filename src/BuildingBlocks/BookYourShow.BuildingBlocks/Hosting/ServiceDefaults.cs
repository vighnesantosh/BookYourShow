using BookYourShow.BuildingBlocks.Idempotency;
using BookYourShow.BuildingBlocks.Messaging;
using BookYourShow.BuildingBlocks.Outbox;
using BookYourShow.BuildingBlocks.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace BookYourShow.BuildingBlocks.Hosting;

public static class ServiceDefaults
{
    /// <summary>Everything every service needs: OTel, health, problem details, OpenAPI, auth, current user.</summary>
    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        var services = builder.Services;

        services.AddProblemDetails();
        services.AddOpenApi();
        services.AddHealthChecks();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.AddIdempotency();

        // Cognito-issued JWTs. Authority = https://cognito-idp.<region>.amazonaws.com/<userPoolId>
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.Authority = builder.Configuration["Auth:Authority"];
                o.TokenValidationParameters.ValidateAudience = false; // Cognito access tokens carry client_id, not aud
                o.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
                o.MapInboundClaims = false;
            });
        services.AddAuthorization();

        // Production CORS is handled at CloudFront/API Gateway; Development allows the demo page.
        services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (builder.Environment.IsDevelopment()) p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        }));

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql")
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(serviceName)
                .AddOtlpExporter());

        return builder;
    }

    /// <summary>One NpgsqlDataSource (connection pool) per service database.</summary>
    public static WebApplicationBuilder AddPostgres(this WebApplicationBuilder builder, string connectionName)
    {
        var cs = builder.Configuration.GetConnectionString(connectionName)
                 ?? throw new InvalidOperationException($"ConnectionStrings:{connectionName} is missing");
        builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(cs).Build());
        return builder;
    }

    /// <summary>SNS/SQS messaging plus the outbox relay for this service's database.</summary>
    public static WebApplicationBuilder AddMessagingWithOutbox(this WebApplicationBuilder builder)
    {
        builder.Services.AddAwsMessaging(builder.Configuration);
        builder.Services.AddHostedService<OutboxRelay>();
        return builder;
    }

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        app.UseExceptionHandler(errors => errors.Run(async http =>
        {
            var ex = http.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, title) = ex switch
            {
                UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
                _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
            };
            await Results.Problem(title: title, statusCode: status).ExecuteAsync(http);
        }));
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health");
        app.MapOpenApi();
        return app;
    }
}
