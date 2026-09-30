using Amazon.EventBridge.Model;
using Amazon.SQS.Model;
using Integration.Tests.Infrastructure;
using Xunit;

namespace Integration.Tests.LocalStack;

[Collection(LocalStackCollection.Name)]
public sealed class LocalStackServiceTests(LocalStackFixture fixture)
{
    [Fact]
    public void LocalStack_auth_token_is_available_without_being_logged()
    {
        Assert.False(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOCALSTACK_AUTH_TOKEN")),
            "Set LOCALSTACK_AUTH_TOKEN in the WSL environment or CI secret store.");
    }

    [Fact]
    public async Task S3_round_trips_utf8_content()
    {
        var bucket = UniqueName.Create("sample").ToLowerInvariant();
        var key = UniqueName.Create("object");
        var expected = $"content-{Guid.NewGuid():N}";

        using var clients = fixture.CreateClients();
        await clients.S3.PutBucketAsync(bucket, TestContext.Current.CancellationToken);
        await using (var requestBody = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(expected)))
        {
            await clients.S3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = bucket, Key = key, InputStream = requestBody }, TestContext.Current.CancellationToken);
        }

        using var response = await clients.S3.GetObjectAsync(bucket, key, TestContext.Current.CancellationToken);
        using var reader = new StreamReader(response.ResponseStream);
        Assert.Equal(expected, await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Secrets_manager_round_trips_secret_string()
    {
        using var clients = fixture.CreateClients();
        var name = UniqueName.Create("secret");
        var expected = $"secret-{Guid.NewGuid():N}";
        await clients.SecretsManager.CreateSecretAsync(new Amazon.SecretsManager.Model.CreateSecretRequest { Name = name, SecretString = expected }, TestContext.Current.CancellationToken);
        var actual = await clients.SecretsManager.GetSecretValueAsync(new Amazon.SecretsManager.Model.GetSecretValueRequest { SecretId = name }, TestContext.Current.CancellationToken);
        Assert.Equal(expected, actual.SecretString);
    }

    [Fact]
    public async Task Sqs_delivers_sent_message()
    {
        using var clients = fixture.CreateClients();
        var queue = await clients.Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName.Create("queue") }, TestContext.Current.CancellationToken);
        var expected = $"message-{Guid.NewGuid():N}";
        await clients.Sqs.SendMessageAsync(queue.QueueUrl, expected, TestContext.Current.CancellationToken);
        var message = await LocalStackPolling.ReceiveMessageAsync(clients.Sqs, queue.QueueUrl, fixture.GetSanitizedLogsAsync, TestContext.Current.CancellationToken);
        Assert.Equal(expected, message.Body);
    }

    [Fact]
    public async Task EventBridge_delivers_matching_event_to_sqs()
    {
        using var clients = fixture.CreateClients();
        var queue = await clients.Sqs.CreateQueueAsync(new CreateQueueRequest { QueueName = UniqueName.Create("queue") }, TestContext.Current.CancellationToken);
        var queueAttributes = await clients.Sqs.GetQueueAttributesAsync(new GetQueueAttributesRequest { QueueUrl = queue.QueueUrl, AttributeNames = ["QueueArn"] }, TestContext.Current.CancellationToken);
        var busName = UniqueName.Create("bus");
        var ruleName = UniqueName.Create("rule");
        var bus = await clients.EventBridge.CreateEventBusAsync(new Amazon.EventBridge.Model.CreateEventBusRequest { Name = busName }, TestContext.Current.CancellationToken);
        var rule = await clients.EventBridge.PutRuleAsync(new PutRuleRequest { Name = ruleName, EventBusName = busName, EventPattern = "{\"source\":[\"sample.wsl\"]}" }, TestContext.Current.CancellationToken);
        await clients.Sqs.SetQueueAttributesAsync(new SetQueueAttributesRequest { QueueUrl = queue.QueueUrl, Attributes = new Dictionary<string, string> { ["Policy"] = LocalStackPolicy.Create(queueAttributes.QueueARN, rule.RuleArn) } }, TestContext.Current.CancellationToken);
        await clients.EventBridge.PutTargetsAsync(new PutTargetsRequest { Rule = ruleName, EventBusName = busName, Targets = [new Target { Id = "sqs", Arn = queueAttributes.QueueARN }] }, TestContext.Current.CancellationToken);
        var detail = $"detail-{Guid.NewGuid():N}";
        await clients.EventBridge.PutEventsAsync(new PutEventsRequest { Entries = [new PutEventsRequestEntry { EventBusName = busName, Source = "sample.wsl", DetailType = "sample", Detail = $"{{\"value\":\"{detail}\"}}" }] }, TestContext.Current.CancellationToken);
        var message = await LocalStackPolling.ReceiveMessageAsync(clients.Sqs, queue.QueueUrl, fixture.GetSanitizedLogsAsync, TestContext.Current.CancellationToken);
        Assert.Contains(detail, message.Body);
    }
}
