using System.Text.Json;
using BookYourShow.Contracts;

namespace BookYourShow.BuildingBlocks.Messaging;

public static class MessagingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Context of one received message, passed to handlers for idempotency and tracing.</summary>
public sealed record MessageContext(string MessageId, string EventType, int ReceiveCount);

public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task HandleAsync(TEvent evt, MessageContext context, CancellationToken ct);
}

/// <summary>Publishes integration events to the service bus (SNS topic → per-consumer SQS queues).</summary>
public interface IEventPublisher
{
    Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : IIntegrationEvent;

    /// <summary>Used by the outbox relay: the event is already serialized.</summary>
    Task PublishRawAsync(string eventType, string payloadJson, string messageId, CancellationToken ct = default);
}

/// <summary>Sends a message straight to a queue, optionally delayed (SQS DelaySeconds, max 900 s).</summary>
public interface IQueueSender
{
    Task SendAsync<TEvent>(string queueName, TEvent evt, TimeSpan delay, CancellationToken ct = default) where TEvent : IIntegrationEvent;
}

public sealed class MessagingOptions
{
    public const string Section = "Messaging";

    /// <summary>SNS topic every service publishes to.</summary>
    public string TopicArn { get; set; } = "";

    /// <summary>SQS queue names this service consumes.</summary>
    public List<string> ConsumeQueues { get; set; } = [];

    /// <summary>Parallel handlers per receive batch.</summary>
    public int MaxConcurrency { get; set; } = 10;
}

public sealed class AwsOptions
{
    public const string Section = "AWS";

    /// <summary>Set for LocalStack (e.g. http://localstack:4566). Leave empty on AWS to use the default credential chain.</summary>
    public string? ServiceUrl { get; set; }

    public string Region { get; set; } = "us-east-1";
}
