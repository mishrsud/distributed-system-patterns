using Amazon;
using Amazon.EventBridge;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SQS;
using System.Text.Json;
using Xunit.Sdk;

namespace Integration.Tests.LocalStack;

public sealed class LocalStackClientFactory : IDisposable
{
    private static readonly AWSCredentials Credentials = new BasicAWSCredentials("test", "test");
    private static readonly RegionEndpoint Region = RegionEndpoint.USEast1;

    public LocalStackClientFactory(string serviceUrl)
    {
        S3 = new AmazonS3Client(Credentials, new AmazonS3Config { RegionEndpoint = Region, ServiceURL = serviceUrl, ForcePathStyle = true });
        Sqs = new AmazonSQSClient(Credentials, new AmazonSQSConfig { RegionEndpoint = Region, ServiceURL = serviceUrl });
        EventBridge = new AmazonEventBridgeClient(Credentials, new AmazonEventBridgeConfig { RegionEndpoint = Region, ServiceURL = serviceUrl });
        SecretsManager = new AmazonSecretsManagerClient(Credentials, new AmazonSecretsManagerConfig { RegionEndpoint = Region, ServiceURL = serviceUrl });
    }

    public AmazonS3Client S3 { get; }
    public AmazonSQSClient Sqs { get; }
    public AmazonEventBridgeClient EventBridge { get; }
    public AmazonSecretsManagerClient SecretsManager { get; }

    public void Dispose()
    {
        S3.Dispose();
        Sqs.Dispose();
        EventBridge.Dispose();
        SecretsManager.Dispose();
    }
}

internal static class LocalStackPolling
{
    internal static async Task<Amazon.SQS.Model.Message> ReceiveMessageAsync(AmazonSQSClient client, string queueUrl, Func<CancellationToken, Task<string>> getLogs, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            while (true)
            {
                var result = await client.ReceiveMessageAsync(new Amazon.SQS.Model.ReceiveMessageRequest { QueueUrl = queueUrl, WaitTimeSeconds = 2, MaxNumberOfMessages = 1 }, timeout.Token);
                if (result.Messages.Count > 0)
                {
                    return result.Messages[0];
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new XunitException($"Timed out waiting for a message on queue {queueUrl}. LocalStack logs:\n{await getLogs(CancellationToken.None)}");
        }
    }
}

internal static class LocalStackPolicy
{
    internal static string Create(string queueArn, string ruleArn) => JsonSerializer.Serialize(new
    {
        Version = "2012-10-17",
        Statement = new[]
        {
            new
            {
                Sid = "AllowEventBridgeRule",
                Effect = "Allow",
                Principal = new { Service = "events.amazonaws.com" },
                Action = "sqs:SendMessage",
                Resource = queueArn,
                Condition = new { ArnEquals = new Dictionary<string, string> { ["aws:SourceArn"] = ruleArn } }
            }
        }
    });
}
