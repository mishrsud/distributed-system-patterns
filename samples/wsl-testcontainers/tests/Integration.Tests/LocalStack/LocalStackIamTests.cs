using System.Net;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.S3;
using Integration.Tests.Infrastructure;
using Xunit;

namespace Integration.Tests.LocalStack;

[Collection(LocalStackCollection.Name)]
public sealed class LocalStackIamTests(LocalStackFixture fixture)
{
    private const string CreateBucketPolicy = """
        {
          "Version": "2012-10-17",
          "Statement": [
            {
              "Effect": "Allow",
              "Action": "s3:CreateBucket",
              "Resource": "*"
            }
          ]
        }
        """;

    [Fact]
    public async Task User_is_denied_create_bucket_until_an_allow_policy_is_attached()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var clients = fixture.CreateClients();
        var userName = UniqueName.Create("user");
        string? accessKeyId = null;
        string? policyArn = null;
        string? createdBucket = null;
        var policyAttached = false;

        await clients.Iam.CreateUserAsync(new CreateUserRequest { UserName = userName }, cancellationToken);
        try
        {
            var accessKey = (await clients.Iam.CreateAccessKeyAsync(new CreateAccessKeyRequest { UserName = userName }, cancellationToken)).AccessKey;
            accessKeyId = accessKey.AccessKeyId;
            using var userS3 = clients.CreateS3Client(new BasicAWSCredentials(accessKey.AccessKeyId, accessKey.SecretAccessKey));

            var deniedBucket = UniqueName.Create("denied");
            var denied = await Assert.ThrowsAsync<AmazonS3Exception>(() => userS3.PutBucketAsync(deniedBucket, cancellationToken));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            var policy = await clients.Iam.CreatePolicyAsync(new CreatePolicyRequest { PolicyName = UniqueName.Create("policy"), PolicyDocument = CreateBucketPolicy }, cancellationToken);
            policyArn = policy.Policy.Arn;
            await clients.Iam.AttachUserPolicyAsync(new AttachUserPolicyRequest { UserName = userName, PolicyArn = policyArn }, cancellationToken);
            policyAttached = true;

            var allowedBucket = UniqueName.Create("allowed");
            var allowed = await userS3.PutBucketAsync(allowedBucket, cancellationToken);
            createdBucket = allowedBucket;
            Assert.Equal(HttpStatusCode.OK, allowed.HttpStatusCode);
        }
        finally
        {
            if (createdBucket is not null)
            {
                await clients.S3.DeleteBucketAsync(createdBucket, CancellationToken.None);
            }

            if (policyAttached)
            {
                await clients.Iam.DetachUserPolicyAsync(new DetachUserPolicyRequest { UserName = userName, PolicyArn = policyArn }, CancellationToken.None);
            }

            if (policyArn is not null)
            {
                await clients.Iam.DeletePolicyAsync(new DeletePolicyRequest { PolicyArn = policyArn }, CancellationToken.None);
            }

            if (accessKeyId is not null)
            {
                await clients.Iam.DeleteAccessKeyAsync(new DeleteAccessKeyRequest { UserName = userName, AccessKeyId = accessKeyId }, CancellationToken.None);
            }

            await clients.Iam.DeleteUserAsync(new DeleteUserRequest { UserName = userName }, CancellationToken.None);
        }
    }
}
