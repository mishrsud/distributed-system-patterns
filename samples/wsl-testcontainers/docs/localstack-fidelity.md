# LocalStack fidelity record

This record lists what the sample proves against LocalStack and where a real-AWS contract test is still required. Each row points at the executable test that produces the evidence. Update the **LocalStack result** column from a real run; do not mark a row as observed without a linked TRX or CI run.

- LocalStack image: `localstack/localstack:2026.8.2` (see `tests/Integration.Tests/Infrastructure/ImageCatalog.cs`)
- Plan tier assumed: LocalStack Base
- Container configuration: `ENFORCE_IAM=1`, root credentials `test`/`test`, region `us-east-1`
- Upstream references:
  - IAM service: <https://docs.localstack.cloud/aws/services/iam/>
  - IAM policy enforcement: <https://docs.localstack.cloud/aws/developer-tools/security-testing/iam-policy-enforcement/>
  - IAM coverage: <https://docs.localstack.cloud/aws/developer-tools/security-testing/iam-coverage/>

## Behaviour matrix

| Behaviour | LocalStack result | AWS contract-test need | Known difference | Plan tier | Evidence test |
|---|---|---|---|---|---|
| S3 bucket create, object put/get with exact UTF-8 content | Not yet executed | Low — core CRUD is stable across LocalStack and AWS | Path-style addressing is forced; AWS default is virtual-hosted style. Bucket names are not globally unique in LocalStack. | Base | `LocalStackServiceTests.S3_round_trips_utf8_content` |
| Secrets Manager create and retrieve secret string | Not yet executed | Medium — rotation, KMS key policy, and resource policies are not exercised | No customer-managed KMS key; encryption is not verified. | Base | `LocalStackServiceTests.Secrets_manager_round_trips_secret_string` |
| SQS send and long-poll receive | Not yet executed | Medium — visibility timeout, redrive, and FIFO ordering semantics are not exercised | Delivery timing is effectively immediate; AWS is eventually consistent and may need longer polling. | Base | `LocalStackServiceTests.Sqs_delivers_sent_message` |
| EventBridge custom bus rule delivering to SQS target through a queue resource policy | Not yet executed | High — rule pattern matching and target resource-policy evaluation must be confirmed against AWS | Resource-policy enforcement on the SQS target for `events.amazonaws.com` may be evaluated differently from AWS; the test passes if delivery succeeds and does not prove a missing policy would be rejected. | Base | `LocalStackServiceTests.EventBridge_delivers_matching_event_to_sqs` |
| IAM identity policy: user denied `s3:CreateBucket` with no policy, allowed after attaching an allow policy | Not yet executed | High — IAM enforcement here is useful local evidence but **not** proof of AWS equivalence | Only identity-based allow/deny for one action is covered. Conditions, permission boundaries, SCPs, session policies, and resource-based policy combination are not exercised. Changes take effect immediately in LocalStack; AWS IAM is eventually consistent. | Base | `LocalStackIamTests.User_is_denied_create_bucket_until_an_allow_policy_is_attached` |

## IAM classification

IAM enforcement in LocalStack is classified as **useful local evidence, not proof of AWS equivalence**. The sample relies on it to catch missing permissions early. Production permission design still needs a contract test against a real AWS account.

Operations whose allow/deny direction must be checked in the upstream IAM coverage page before they are relied on:

- `s3:CreateBucket`: the sample exercises both deny and allow. Confirm on the coverage page that it is listed as enforced for the pinned image.
- `sqs:SendMessage` from `events.amazonaws.com` (service principal on a resource policy): the sample exercises only the allow path. Treat the deny direction as unrecorded until an explicit test proves it.

## Updating this record

1. Run `dotnet test tests/Integration.Tests --filter "FullyQualifiedName~LocalStack" --logger trx` in WSL with `LOCALSTACK_AUTH_TOKEN` set.
2. Replace `Not yet executed` with `Pass` or `Fail`, plus the date and the TRX or CI run link.
3. Add a row for every new difference found. Never paste the token, raw environment, or unsanitized container logs here.
