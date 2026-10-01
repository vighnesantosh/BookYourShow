using Amazon.SimpleNotificationService;
using Amazon.SQS;
using BookYourShow.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BookYourShow.BuildingBlocks.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>SNS publisher, SQS sender and (if any queues are configured) the SQS consumer.</summary>
    public static IServiceCollection AddAwsMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<MessagingOptions>(config.GetSection(MessagingOptions.Section));
        services.Configure<AwsOptions>(config.GetSection(AwsOptions.Section));

        services.AddSingleton(sp => AwsClientFactory.CreateSns(sp.GetRequiredService<IOptions<AwsOptions>>().Value));
        services.AddSingleton(sp => AwsClientFactory.CreateSqs(sp.GetRequiredService<IOptions<AwsOptions>>().Value));
        services.AddSingleton<IEventPublisher, SnsEventPublisher>();
        services.AddSingleton<IQueueSender, SqsQueueSender>();

        services.AddSingleton(sp =>
        {
            var registry = new HandlerRegistry();
            foreach (var reg in sp.GetServices<HandlerRegistration>()) reg.Apply(registry);
            return registry;
        });
        services.AddHostedService<SqsConsumer>();
        return services;
    }

    public static IServiceCollection AddEventHandler<TEvent, THandler>(this IServiceCollection services)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        services.AddScoped<THandler>();
        services.AddSingleton(new HandlerRegistration(r => r.Register<TEvent, THandler>()));
        return services;
    }

    public sealed record HandlerRegistration(Action<HandlerRegistry> Apply);
}
