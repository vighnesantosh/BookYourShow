using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.SimpleNotificationService;
using Amazon.SimpleNotificationService.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using BookYourShow.Contracts;
using Microsoft.Extensions.Options;
using SnsAttribute = Amazon.SimpleNotificationService.Model.MessageAttributeValue;
using SqsAttribute = Amazon.SQS.Model.MessageAttributeValue;

namespace BookYourShow.BuildingBlocks.Messaging;

public static class AwsClientFactory
{
    public static IAmazonSimpleNotificationService CreateSns(AwsOptions o) =>
        string.IsNullOrWhiteSpace(o.ServiceUrl)
            ? new AmazonSimpleNotificationServiceClient(Amazon.RegionEndpoint.GetBySystemName(o.Region))
            : new AmazonSimpleNotificationServiceClient(new BasicAWSCredentials("test", "test"),
                new AmazonSimpleNotificationServiceConfig { ServiceURL = o.ServiceUrl, AuthenticationRegion = o.Region });

    public static IAmazonSQS CreateSqs(AwsOptions o) =>
        string.IsNullOrWhiteSpace(o.ServiceUrl)
            ? new AmazonSQSClient(Amazon.RegionEndpoint.GetBySystemName(o.Region))
            : new AmazonSQSClient(new BasicAWSCredentials("test", "test"),
                new AmazonSQSConfig { ServiceURL = o.ServiceUrl, AuthenticationRegion = o.Region });
}

/// <summary>Publishes to one SNS topic; consumers subscribe SQS queues with a filter policy on "type".</summary>
public sealed class SnsEventPublisher(IAmazonSimpleNotificationService sns, IOptions<MessagingOptions> options) : IEventPublisher
{
    public Task PublishAsync<TEvent>(TEvent evt, CancellationToken ct = default) where TEvent : IIntegrationEvent =>
        PublishRawAsync(EventTypes.NameOf<TEvent>(), JsonSerializer.Serialize(evt, MessagingJson.Options), Guid.CreateVersion7().ToString(), ct);

    public async Task PublishRawAsync(string eventType, string payloadJson, string messageId, CancellationToken ct = default)
    {
        var request = new PublishRequest
        {
            TopicArn = options.Value.TopicArn,
            Message = payloadJson,
            MessageAttributes = new Dictionary<string, SnsAttribute>
            {
                ["type"] = new() { DataType = "String", StringValue = eventType },
                ["messageId"] = new() { DataType = "String", StringValue = messageId },
            },
        };
        await sns.PublishAsync(request, ct);
    }
}

public sealed class SqsQueueSender(IAmazonSQS sqs) : IQueueSender
{
    private readonly ConcurrentDictionary<string, string> _urls = new();

    public async Task SendAsync<TEvent>(string queueName, TEvent evt, TimeSpan delay, CancellationToken ct = default) where TEvent : IIntegrationEvent
    {
        var url = await ResolveUrlAsync(queueName, ct);
        await sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = url,
            MessageBody = JsonSerializer.Serialize(evt, MessagingJson.Options),
            DelaySeconds = (int)Math.Clamp(delay.TotalSeconds, 0, 900),
            MessageAttributes = new Dictionary<string, SqsAttribute>
            {
                ["type"] = new() { DataType = "String", StringValue = EventTypes.NameOf<TEvent>() },
                ["messageId"] = new() { DataType = "String", StringValue = Guid.CreateVersion7().ToString() },
            },
        }, ct);
    }

    private async Task<string> ResolveUrlAsync(string queueName, CancellationToken ct)
    {
        if (_urls.TryGetValue(queueName, out var url)) return url;
        var response = await sqs.GetQueueUrlAsync(queueName, ct);
        return _urls[queueName] = response.QueueUrl;
    }
}
