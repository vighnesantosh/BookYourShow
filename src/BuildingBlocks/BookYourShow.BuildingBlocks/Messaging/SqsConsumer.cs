using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookYourShow.BuildingBlocks.Messaging;

/// <summary>
/// Long-polls each configured SQS queue and dispatches messages to typed handlers.
/// A message is deleted only after its handler succeeds; a failure leaves it to reappear
/// after the visibility timeout and, after maxReceiveCount, land in the queue's DLQ.
/// Handlers must therefore be idempotent (see <see cref="Inbox"/>).
/// </summary>
public sealed class SqsConsumer(
    IAmazonSQS sqs,
    IServiceScopeFactory scopes,
    HandlerRegistry registry,
    IOptions<MessagingOptions> options,
    ILogger<SqsConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Value.ConsumeQueues.Select(q => PollQueueAsync(q, stoppingToken)));

    private async Task PollQueueAsync(string queueName, CancellationToken ct)
    {
        string? queueUrl = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                queueUrl ??= (await sqs.GetQueueUrlAsync(queueName, ct)).QueueUrl;

                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = queueUrl,
                    MaxNumberOfMessages = 10,
                    WaitTimeSeconds = 20,
                    MessageAttributeNames = ["All"],
                    MessageSystemAttributeNames = ["ApproximateReceiveCount"],
                }, ct);

                var messages = response.Messages ?? [];
                if (messages.Count == 0) continue;

                await Parallel.ForEachAsync(messages,
                    new ParallelOptions { MaxDegreeOfParallelism = options.Value.MaxConcurrency, CancellationToken = ct },
                    async (m, token) => await HandleOneAsync(queueUrl, m, token));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Polling {Queue} failed; retrying in 2 s", queueName);
                queueUrl = null;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async Task HandleOneAsync(string queueUrl, Message m, CancellationToken ct)
    {
        var attrs = m.MessageAttributes ?? [];
        var type = attrs.TryGetValue("type", out var t) ? t.StringValue : null;
        var messageId = (attrs.TryGetValue("messageId", out var id) ? id.StringValue : null) ?? m.MessageId;
        var receiveCount = (m.Attributes ?? []).TryGetValue("ApproximateReceiveCount", out var rc) && int.TryParse(rc, out var n) ? n : 1;

        if (type is null || !registry.TryGet(type, out var invoke))
        {
            logger.LogWarning("No handler for message type {Type}; deleting {MessageId}", type, m.MessageId);
            await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle, ct);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await invoke(scope.ServiceProvider, m.Body, new MessageContext(messageId, type, receiveCount), ct);
            await sqs.DeleteMessageAsync(queueUrl, m.ReceiptHandle, ct);
        }
        catch (Exception ex)
        {
            // Not deleted: SQS redelivers after the visibility timeout, then moves it to the DLQ.
            logger.LogError(ex, "Handler for {Type} failed (attempt {Attempt}) on {MessageId}", type, receiveCount, messageId);
        }
    }
}
