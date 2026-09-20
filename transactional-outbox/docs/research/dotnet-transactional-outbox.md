# Research: a dependency-light transactional outbox on .NET 10, SQL Server, and EventBridge

Research date: 2026-09-20

## Scope and source policy

This note supports a new sibling sample under `transactional-outbox`; it does not propose modifying the existing `gatherly` sample. It uses primary sources only: Microsoft documentation, AWS documentation, LocalStack documentation, Docker documentation, and the official Ardalis Clean Architecture documentation/repository. Statements labelled **Documented fact** summarize those sources. Statements labelled **Recommendation** or **Inference** are design conclusions for this sample rather than guarantees made by a source.

## Executive design recommendation

Use four production projects following the full Ardalis shape, but do not take dependencies on the Ardalis template's optional libraries:

```text
src/
  TransactionalOutbox.Core
  TransactionalOutbox.UseCases
  TransactionalOutbox.Infrastructure
  TransactionalOutbox.Web
tests/
  TransactionalOutbox.UnitTests
  TransactionalOutbox.IntegrationTests
```

The Web API places an order. The `Order` aggregate records an `OrderPlaced` domain event. A dependency-free EF Core `SaveChangesInterceptor` turns pending domain events into durable outbox rows before `SaveChanges`; EF Core then commits the order and outbox inserts together. An in-process `BackgroundService` repeatedly creates a DI scope, atomically claims a small batch using a SQL Server work-queue statement, commits that short claim transaction, and publishes each claimed event to EventBridge. LocalStack hosts a custom event bus and a rule that routes `OrderPlaced` to an SQS queue.

The publisher marks an outbox row processed only after `PutEvents` confirms success for that entry. It records delayed retries for transient failures and dead-letters permanent or exhausted failures in the outbox table. A lease makes abandoned claims eligible again. Publishing must remain outside the SQL transaction, so a crash after EventBridge accepts an event but before SQL Server records success can publish the same event again. This is intentionally **at-least-once**, not exactly-once.

The smallest justified runtime dependency set is:

- the ASP.NET Core shared framework;
- `Microsoft.EntityFrameworkCore.SqlServer` 10.0.x and EF design-time tooling;
- `AWSSDK.EventBridge` (the official AWS SDK package);
- the chosen test framework only in test projects.

Do not add MediatR, Quartz, Newtonsoft.Json, Scrutor, LocalStack.NET, or Polly. `BackgroundService`, built-in DI, `System.Text.Json`, and the AWS SDK already cover this sample's needs. Polly is permitted by the brief but would duplicate SDK call retries and obscure the durable retry schedule, so it is not recommended here.

## 1. Runtime and EF Core baseline

**Documented facts**

- .NET 10 is an LTS release supported until November 2028. EF Core 10 targets .NET 10, was released as LTS, and has the same support horizon. Microsoft recommends using the latest patch of a supported EF Core major version ([.NET releases and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support), [EF Core releases and planning](https://learn.microsoft.com/en-us/ef/core/what-is-new/), [What's new in EF Core 10](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew)).
- The Microsoft-maintained SQL Server provider is installed with `Microsoft.EntityFrameworkCore.SqlServer` and configured with `UseSqlServer`. For `UseSqlServer`, connection resiliency is opt-in with `EnableRetryOnFailure` ([SQL Server provider](https://learn.microsoft.com/en-us/ef/core/providers/sql-server/)).
- As of the research date, NuGet lists `Microsoft.EntityFrameworkCore.SqlServer` 10.0.12 as the current 10.x patch ([NuGet package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/)).

**Recommendation**

- Target `net10.0` everywhere and centrally pin EF Core packages to the latest 10.0.x patch used by the repository. Keep the EF runtime and tools packages on the same patch.
- Enable SQL Server connection resiliency, but treat it separately from the durable outbox retry policy. EF retries database commands; outbox retry fields govern a message's next publish attempt across process restarts.

## 2. Atomic write: order plus outbox row

**Documented facts**

- On a relational provider, one `SaveChanges` call is transactional by default: either all changes are applied or none are. When `SaveChanges` runs inside an existing transaction, EF creates a savepoint first, except when SQL Server MARS prevents savepoints ([EF Core transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions)).
- EF interceptors can observe, modify, or suppress EF operations. `ISaveChangesInterceptor`/`SaveChangesInterceptor` exposes interception points around `SaveChanges`, and interceptors are registered per `DbContext` ([EF Core interceptors](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors), [`ISaveChangesInterceptor`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.entityframeworkcore.diagnostics.isavechangesinterceptor?view=efcore-10.0)).
- Retrying execution strategies replay each query or `SaveChanges` as a unit. A manually initiated transaction must itself be run through `Database.CreateExecutionStrategy()` so the complete unit can be replayed. A connection loss during commit can leave the commit outcome unknown; Microsoft specifically recommends client-generated keys or explicit state verification to make retries safe ([EF Core connection resiliency](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency)).
- The transactional outbox addresses the dual-write failure by writing business data and the event to the same database transaction, then relaying it separately. A rollback must not emit the event ([AWS transactional outbox guidance](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html)).

**Recommendation**

- Give `Order`, the domain event, and the outbox message client-generated GUIDs. A stable event/outbox ID is both the integration-event identity and the future consumer idempotency key.
- Capture events in `SavingChangesAsync` and add outbox entities to the same `DbContext`. Do not open a second context or connection from the interceptor. That keeps the outbox insert in the same transaction as the order insert.
- Support only async saves in the sample. Either implement the synchronous interception path identically or throw a clear exception from it; otherwise callers could bypass event capture by calling synchronous `SaveChanges`.
- Serialize a stable, explicit integration-event contract using `System.Text.Json`. Store a logical event name and schema version, not a CLR assembly-qualified type name. This keeps persisted data independent of namespace/refactoring changes and avoids unsafe polymorphic deserialization.
- Clear domain events only once they have been copied to tracked outbox entities. Test the transient-failure/retry path so replay cannot create a second outbox ID or lose an event.
- The order endpoint needs only one `SaveChangesAsync`; do not introduce a manual transaction for this use case. If implementation later adds multiple saves in one transaction, wrap the entire transaction in EF's execution strategy.
- Do not enable MARS in the connection string; it provides no benefit to this sample and disables EF's automatic savepoints.

## 3. Delivery guarantee and idempotency boundary

**Documented facts**

- An outbox relay can publish twice if it crashes after broker publication but before recording completion. AWS recommends idempotent consumers that track processed message IDs ([AWS transactional outbox guidance](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html)).
- At-least-once delivery prevents loss but permits duplicates; idempotent consumers make repeated handling safe ([Azure Publisher-Subscriber pattern](https://learn.microsoft.com/en-us/azure/architecture/patterns/publisher-subscriber)).
- EventBridge itself notes that, in rare cases, a rule or target can be invoked more than once for a single event ([EventBridge troubleshooting](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-troubleshooting.html)).

**Recommendation**

- State the guarantee precisely in the README: the order and intent-to-publish are atomic; relay delivery is at least once; downstream consumers must deduplicate on the outbox/event ID.
- Do not describe `ProcessedOnUtc` as proving that SQS received the event. It proves only that EventBridge accepted the `PutEvents` entry. The separate SQS inspection step proves the configured rule delivered it to that target.
- There is no consumer in this scope, so demonstrate the idempotency contract in the event envelope and document the usual consumer-side unique constraint on `(ConsumerName, EventId)` rather than pretending the sample implements end-to-end exactly-once processing.

## 4. Multi-instance-safe SQL Server claiming

### Relevant SQL Server primitives

**Documented facts**

- `UPDLOCK` takes update locks for reads and holds them until the transaction completes ([SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17#updlock)).
- `READPAST` skips row locks held by other transactions and is explicitly described as useful for SQL Server work queues. It does not skip page locks and is restricted to `READ COMMITTED` or `REPEATABLE READ`. With `READ_COMMITTED_SNAPSHOT` enabled at `READ COMMITTED`, `READCOMMITTEDLOCK` is required for this pattern ([SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17#readpast)).
- `ROWLOCK` asks SQL Server to take row locks where it would otherwise take page/table locks; it does not turn lock escalation or all blocking into an impossibility ([SQL Server table hints](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17#rowlock)).
- `OUTPUT` can return the rows affected by an `UPDATE`, but SQL Server does not guarantee output order. It can also return rows for a statement that later errors and rolls back, so callers must discard the result when the command/transaction fails ([SQL Server `OUTPUT`](https://learn.microsoft.com/en-us/sql/t-sql/queries/output-clause-transact-sql?view=sql-server-ver17)).
- A SQL Server `rowversion` can be mapped with EF's `IsRowVersion()` and used as an optimistic concurrency token; EF includes the original token in the update predicate and throws `DbUpdateConcurrencyException` when no row matches ([EF Core concurrency](https://learn.microsoft.com/en-us/ef/core/saving/concurrency)).

### Claim design

**Recommendation**

Use a single, parameterized SQL statement in a short transaction to select, lease, and return a batch. The shape is:

```sql
;WITH claimable AS
(
    SELECT TOP (@BatchSize) *
    FROM dbo.OutboxMessages WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE ProcessedOnUtc IS NULL
      AND DeadLetteredOnUtc IS NULL
      AND NextAttemptOnUtc <= SYSUTCDATETIME()
      AND (LockedUntilUtc IS NULL OR LockedUntilUtc < SYSUTCDATETIME())
    ORDER BY OccurredOnUtc, Id
)
UPDATE claimable
SET LockedBy = @WorkerId,
    LockedUntilUtc = DATEADD(second, @LeaseSeconds, SYSUTCDATETIME()),
    AttemptCount = AttemptCount + 1
OUTPUT INSERTED.Id,
       INSERTED.EventType,
       INSERTED.SchemaVersion,
       INSERTED.Payload,
       INSERTED.OccurredOnUtc,
       INSERTED.AttemptCount;
```

This is intentionally provider-specific infrastructure code; execute it as a parameterized `DbCommand` using the connection owned by the EF `DbContext`. Keeping it in Infrastructure preserves the Core/UseCases boundaries without adding a micro-ORM dependency.

Commit the claim before any network call. Holding SQL locks while calling EventBridge would make throughput and availability depend on network latency and could create broad blocking. A lease, by contrast, lets another instance reclaim work after a crashed publisher. Use a unique worker ID and include `LockedBy` in the later success/failure update predicate so a stale worker cannot overwrite a newer lease owner.

Use database UTC (`SYSUTCDATETIME`) for eligibility and lease comparisons to avoid clock skew among API replicas. Give the claim query a supporting filtered/composite index beginning with the completion/dead-letter/next-attempt/lease eligibility columns and ending with `OccurredOnUtc, Id`; verify the actual plan rather than hard-coding an index hint.

Do not rely only on EF optimistic concurrency for claiming. A query-then-save loop makes every worker fetch the same candidates and resolve conflicts afterward. The atomic update/lease avoids that thundering herd and returns only rows actually claimed. A `rowversion` remains useful as defense in depth for state transitions.

**Inference and limits**

- `READPAST` plus `ROWLOCK` reduces normal queue contention; it does not promise zero blocking because page locks, schema locks, index maintenance, and lock escalation still exist.
- Selection order can be requested, but parallel workers and EventBridge/SQS standard delivery mean global publish/receive ordering is not guaranteed. The sample should not claim ordered delivery.

## 5. Retry, poison-message, and cleanup policy

**Recommendation**

Suggested outbox fields:

```text
Id, EventType, SchemaVersion, Payload, OccurredOnUtc,
ProcessedOnUtc, EventBridgeEventId,
AttemptCount, NextAttemptOnUtc, LastError,
LockedBy, LockedUntilUtc, DeadLetteredOnUtc, RowVersion
```

- Claim increments `AttemptCount` and creates a lease.
- Success stores `ProcessedOnUtc` and EventBridge's event ID, and clears lease/error fields.
- A retryable failure stores a bounded error summary, clears the lease, and sets `NextAttemptOnUtc` using exponential backoff plus jitter.
- A non-retryable error, serialization/configuration error, or exhausted retry limit stores `DeadLetteredOnUtc`; it must not be continuously selected.
- Retain failed rows for diagnosis. Periodically delete only successfully processed rows older than a configurable retention interval. `ExecuteDeleteAsync` is sufficient; concurrent cleanup instances are harmless when they target already-processed rows.

The durable schedule should operate at the message level across poll cycles. The AWS SDK may perform a small number of immediate request retries, but Polly should not wrap it with another independent retry loop. This keeps worst-case call latency bounded and prevents multiplicative retries. AWS documents that the SDK retries throttling and dropped connections and exposes retry/timeout configuration; standard mode uses a bounded number of retries ([AWS SDK for .NET retries and timeouts](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/retries-timeouts.html)).

## 6. `BackgroundService` lifecycle and DI

**Documented facts**

- `BackgroundService.ExecuteAsync` represents the service lifetime, receives cancellation when shutdown starts, and should complete promptly. Long blocking startup work should not precede the first asynchronous wait ([ASP.NET Core hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)).
- Hosted services do not receive a DI scope automatically. `AddDbContext` registers `DbContext` as scoped by default, and a singleton must create an explicit scope before resolving a scoped dependency ([scoped services in `BackgroundService`](https://learn.microsoft.com/en-us/dotnet/core/extensions/scoped-service), [DI service lifetimes](https://learn.microsoft.com/en-us/dotnet/core/extensions/dependency-injection/service-lifetimes)).
- In .NET 10, all of `BackgroundService.ExecuteAsync` runs on a background thread; startup-order-sensitive work belongs in lifecycle methods instead ([.NET 10 `BackgroundService` behavior change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/extensions/10.0/backgroundservice-executeasync-task)).

**Recommendation**

- Register one thin hosted service in Web. On each poll, use `IServiceScopeFactory.CreateAsyncScope()` and resolve a scoped outbox processor/`DbContext` from that scope.
- Use `PeriodicTimer` or a cancellation-aware delay. Pass the stopping token through SQL and AWS calls. Catch and log expected per-cycle failures so one transient outage does not terminate the hosted service; never swallow cancellation.
- Add a small randomized idle delay so replicas do not poll in lockstep. Process bounded batches and loop immediately while work remains, then delay when no work is available.
- The hosted service belongs to Infrastructure because scheduling and delivery are infrastructure concerns; Web owns only composition/registration.

## 7. EventBridge `PutEvents` correctness

**Documented facts**

- The .NET SDK's `PutEventsAsync` sends custom events and accepts a cancellation token ([AWS SDK for .NET `PutEventsAsync`](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/EventBridge/MIEventBridgePutEventsAsyncPutEventsRequestCancellationToken.html)).
- `PutEvents` is a per-entry operation: one response entry corresponds by index to each request entry. A response can contain both successes and failures. Callers must inspect `FailedEntryCount` and each result entry rather than treating an HTTP-successful SDK call as success for every event ([Sending events with `PutEvents`](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-putevents.html), [.NET `PutEventsResponse`](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/EventBridge/TPutEventsResponse.html)).
- Retryable entry errors include `InternalFailure` and `ThrottlingException`. Errors such as access denied, invalid arguments, malformed detail, and source/detail-type authorization failures are non-retryable ([`PutEventsResultEntry`](https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_PutEventsResultEntry.html)).
- Publishing to a nonexistent event bus is a dangerous special case: AWS documents that EventBridge can return HTTP 200 without a failed entry while no rule matches and the event is dropped ([Sending events with `PutEvents`](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-putevents.html)).
- In AWS SDK for .NET v4, collection properties such as `Entries` can be `null` when absent rather than automatically initialized ([.NET `PutEventsResponse`](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/EventBridge/TPutEventsResponse.html)).

**Recommendation**

- Prefer one `PutEvents` entry per claimed message in this educational implementation. That makes state transitions and failure injection obvious. A future throughput extension can batch up to the service limit while preserving the index mapping.
- Mark success only when `FailedEntryCount == 0`, the corresponding result entry exists, has a nonempty `EventId`, and has no error code. Treat a malformed/null response as failure.
- Classify documented transient entry errors for delayed retry and documented permanent errors for dead-lettering. Transport/service exceptions that survive SDK retries should also enter the durable delayed retry path.
- Validate the configured event-bus name at startup for the sample environment, and make LocalStack initialization health visible. A successful `PutEvents` call alone cannot detect the nonexistent-bus case.
- Put `Id`, `OccurredOnUtc`, `SchemaVersion`, `CorrelationId`, and order data in `Detail`; use stable values such as `Source = "sample.orders"` and `DetailType = "OrderPlaced"` for the rule.

## 8. EventBridge to SQS and LocalStack

**Documented facts**

- EventBridge rules match events and route them to targets; SQS standard and FIFO queues are supported target types. EventBridge requires permission to access an SQS target, provided through an execution role or the queue's resource policy ([EventBridge targets](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-targets.html), [resource-based policies](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-use-resource-based.html)).
- LocalStack supports EventBridge rules with SQS targets. Its documented flow is create bus, create rule with an event pattern, attach a target, then publish an event ([LocalStack EventBridge](https://docs.localstack.cloud/aws/services/events/)).
- LocalStack `READY` hooks run after it is ready to serve requests. Executable `.sh` or `.py` files mounted under `/etc/localstack/init/ready.d/` are run in alphanumeric order, and initialization state is queryable at `/_localstack/init/ready` ([LocalStack initialization hooks](https://docs.localstack.cloud/aws/customization/advanced/initialization-hooks/)).
- For .NET clients, LocalStack recommends configuring the official AWS client with `ServiceURL`. When an explicit region is needed with a custom URL, set `AuthenticationRegion`; setting `RegionEndpoint` can override `ServiceURL` and accidentally send the request to AWS ([LocalStack AWS SDK for .NET](https://docs.localstack.cloud/aws/connecting/aws-sdks/dotnet/)).
- LocalStack documents standard SQS `ReceiveMessage` verification and also provides a development-only peek endpoint that reads queue contents without changing visibility or access metrics ([LocalStack SQS](https://docs.localstack.cloud/aws/services/sqs/)).

**Recommendation**

- A single idempotent `ready.d` script should create:
  1. a custom event bus;
  2. a standard SQS queue;
  3. a queue resource policy granting `events.amazonaws.com` `sqs:SendMessage` constrained to the rule ARN;
  4. an enabled rule matching `source: sample.orders` and `detail-type: OrderPlaced`;
  5. the queue target.
- Make repeated initialization safe by using create-or-update APIs and stable names. Fail the script on command failures, and make Compose/README verification check the READY hook status.
- Configure the API container with `ServiceURL=http://localstack:4566`, an authentication region such as `ap-southeast-2`, and dummy local credentials. Configure a host-run API with `http://localhost:4566` (or `localhost.localstack.cloud`). Keep endpoint, region, credentials, bus, source, and detail type in options rather than branching application logic for LocalStack.
- Prove delivery with `ReceiveMessage` for AWS-compatible instructions; optionally document LocalStack's side-effect-free peek URL as a convenient local assertion.
- This demo queue is the intended target, not an outbox poison queue. The outbox's `DeadLetteredOnUtc` represents publisher failures before EventBridge accepts an entry. An EventBridge target DLQ is a separate future-production concern for events accepted by EventBridge but not delivered to a target ([EventBridge DLQs](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-rule-dlq.html)).

## 9. SQL Server and Compose

**Documented facts**

- Microsoft's current quickstart uses `mcr.microsoft.com/mssql/server:2025-latest`, requires `ACCEPT_EULA=Y`, and uses `MSSQL_SA_PASSWORD` (the older `SA_PASSWORD` name is deprecated). The password must meet SQL Server's password policy ([SQL Server Linux container quickstart](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver17)).
- Container-local database data is lost with the container unless it is persisted; Microsoft demonstrates a named volume mounted at `/var/opt/mssql` ([restore/use a SQL Server container](https://learn.microsoft.com/en-us/sql/linux/tutorial-restore-backup-in-sql-server-container?view=sql-server-ver17)).
- SQL Server Linux container images are supported on Intel/AMD x86-64 Linux hosts. Microsoft says emulation/translation environments such as Rosetta 2 and QEMU are not tested or supported ([SQL Server 2025 on Linux release notes](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-release-notes-2025?view=sql-server-ver17)).
- Compose profiles selectively activate services; services without profiles start by default, while profiled services start only when their profile is enabled ([Docker Compose profiles](https://docs.docker.com/compose/how-tos/profiles/)).

**Recommendation**

- Use SQL Server 2025 Developer in the sample and pin an explicit tested image digest or immutable tag if reproducibility matters; `latest` is convenient but mutable. Persist `/var/opt/mssql` in a named volume and include a real readiness health check, not just container-running status.
- Leave SQL Server and LocalStack unprofiled so `docker compose up -d` starts infrastructure only. Put the Web API in a `full` profile so `docker compose --profile full up --build` adds the application.
- Keep migrations explicit as agreed. From a pristine database, the documented run sequence must apply `dotnet ef database update` before starting/using the API. Do not silently migrate in `Program.cs`, and do not add a migration container. This means the first full-stack run is deliberately not literally one command; that is the consequence of the explicit-migration requirement.
- Call out the ARM64 caveat prominently because developers on Apple Silicon may be able to run the x64 image through emulation, but Microsoft does not support that configuration.
- Never present the sample SA password as production guidance. Use it only for the disposable local environment and keep production secret/authentication guidance out of the sample's implementation scope.

## 10. Ardalis Clean Architecture mapping

**Documented facts**

- The full Ardalis template uses Core, UseCases, Infrastructure, and Web projects. Core is the center and should have very few external dependencies. UseCases depends on Core but not Infrastructure. Infrastructure implements external-resource concerns behind abstractions. Web is the entry point and composition root. The template organizes tests by unit, functional, and integration kinds ([Ardalis design decisions](https://ardalis.github.io/CleanArchitecture/design-decisions/), [Ardalis getting started](https://ardalis.github.io/CleanArchitecture/getting-started/)).
- Ardalis describes the template as a starting structure, not a reference application, and explicitly expects dependencies to be swapped according to the application's needs ([Ardalis design decisions](https://ardalis.github.io/CleanArchitecture/design-decisions/)).
- Ardalis migration guidance places EF migrations in Infrastructure while using Web as the startup project, and shows explicit `dotnet ef database update` commands ([Ardalis getting started](https://ardalis.github.io/CleanArchitecture/getting-started/#running-migrations)).

**Recommendation**

Map the sample as follows:

| Project | Responsibilities | Allowed dependencies |
|---|---|---|
| Core | `Order` aggregate/value types, `OrderPlaced` domain event, domain-event collection abstraction | BCL only |
| UseCases | `PlaceOrder` request/handler, ports such as order repository/unit of work or application DB abstraction | Core; no mediator package required |
| Infrastructure | EF `DbContext`, entity mappings, migrations, SaveChanges interceptor, outbox storage/claiming, EventBridge adapter, scoped processor, hosted service registration | Core, UseCases, EF SQL Server, AWS SDK |
| Web | HTTP endpoint, request/response DTOs, configuration binding, DI composition | UseCases and Infrastructure |
| UnitTests | Domain and retry/backoff/state-transition tests | Test-only dependencies |
| IntegrationTests | Real SQL Server concurrency/claim tests and LocalStack publication test | Test-only dependencies plus production projects |

Use plain endpoint/controller code and a small handler class rather than copying the Ardalis template's FastEndpoints, Mediator, Specification, Result, or GuardClauses dependencies. “Ardalis style” here means inward dependency direction and project responsibilities, not adopting every package in the template.

## 11. Existing repository context

The existing `transactional-outbox/gatherly` sample is a .NET 6 six-project solution. Inspection found:

- EF Core SQL Server 6.0.8, MediatR, Quartz, Newtonsoft.Json, and Scrutor dependencies;
- a `SaveChangesInterceptor` that converts domain events to outbox rows and clears the aggregate events;
- a Quartz job that selects the first 20 unprocessed rows, publishes them through in-process MediatR, marks them processed, then calls `SaveChanges`;
- no atomic multi-instance claim, lease, retry schedule, poison/dead-letter transition, cleanup, external broker delivery, Compose environment, or tests in the visible solution;
- polymorphic payload persistence using Newtonsoft `TypeNameHandling.All` and CLR type names;
- a local trusted-connection string and no container orchestration.

**Inference**

The old sample is useful as a historical contrast but should remain untouched. The new sample should not copy its job implementation: two replicas can select the same unprocessed rows, and a process failure loses per-message progress until the final batch save. Its interceptor is a useful starting concept, but the new implementation should use stable integration-event contracts, deterministic IDs, `System.Text.Json`, and explicit retry-safe tests.

## 12. Required verification scenarios

The runnable sample should prove these properties, preferably with automated integration tests plus README commands:

1. Placing an order creates exactly one order and one outbox row in a single save.
2. A forced database failure leaves neither row committed.
3. Two simultaneous claimers receive disjoint outbox IDs.
4. An abandoned lease becomes claimable after expiry.
5. EventBridge success marks the row processed and the matching event appears in SQS.
6. A `PutEvents` partial/entry failure does not mark the row processed.
7. Retryable failures schedule a later attempt; permanent or exhausted failures dead-letter the row.
8. A simulated crash after publish but before the success update causes a duplicate publication after lease expiry, documenting the at-least-once boundary.
9. Cleanup deletes only sufficiently old processed rows and preserves pending/dead-letter rows.
10. Graceful host shutdown cancels polling and in-flight SDK/database calls promptly.

## 13. Explicit non-goals and future extensions

Do not implement a downstream SQS consumer, dashboards, distributed tracing, admin replay endpoints, a separate worker deployment, EventBridge target DLQ/replay tooling, or strict per-aggregate ordering in the first sample. Document them as extensions. The boundaries above allow the hosted service to move into a Worker executable later without changing Core or UseCases.

## Key constraints to carry into implementation

1. The atomic guarantee ends at the SQL Server commit; broker delivery remains at least once.
2. Claim in one short SQL operation, commit, then publish outside the transaction.
3. A lease is mandatory if claimed work is committed before publication.
4. Check per-entry `PutEvents` results; HTTP success is insufficient.
5. Stable client-generated event IDs are mandatory for retries and consumer deduplication.
6. Use database time for leases and retry eligibility.
7. `BackgroundService` must create scopes for EF `DbContext` and honor cancellation.
8. LocalStack startup must create the bus/rule/queue/policy and expose initialization failure.
9. Explicit migrations mean a pristine full-stack run has a required migration step.
10. SQL Server's container is not officially supported under ARM emulation.

## Evidence limitations

- Microsoft documents the behavior of `UPDLOCK`, `READPAST`, `ROWLOCK`, `OUTPUT`, and transactions individually, but does not publish this exact lease-claim statement as an official transactional-outbox recipe. The statement and schema are a recommendation synthesized from those documented primitives and must be concurrency-tested against the chosen SQL Server image.
- LocalStack documents support for EventBridge-to-SQS and the required APIs, but emulator behavior is not proof of AWS service parity. The sample demonstrates local integration; real-AWS validation remains a production adoption step.
- Neither EF Core nor EventBridge can remove the publish/mark-complete failure window without a distributed transaction they both participate in. The at-least-once conclusion follows from the documented outbox failure mode and should be treated as a system contract, not an implementation defect.
