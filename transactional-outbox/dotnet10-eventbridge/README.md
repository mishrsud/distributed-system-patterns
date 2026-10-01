# Transactional outbox on .NET 10, SQL Server and EventBridge

A runnable tutorial for the transactional outbox pattern. An ASP.NET Core API saves an order and an
`OrderPlaced` event in **one SQL Server transaction**. A background publisher then relays the event to
**Amazon EventBridge** (LocalStack locally), where a rule routes it to an **SQS** queue you can read.

You will start the infrastructure, apply the EF Core migration, place an order, watch the outbox row move
through its lifecycle, break the publisher on purpose, and see a duplicate delivery happen. The sample is
deliberately small and dependency-light so every moving part is visible.

- [The problem: dual writes](#the-problem-dual-writes)
- [What the outbox guarantees (and what it does not)](#what-the-outbox-guarantees-and-what-it-does-not)
- [Architecture and message flow](#architecture-and-message-flow)
- [Dependency choices](#dependency-choices)
- [Prerequisites](#prerequisites)
- [Run it locally](#run-it-locally)
- [Place and retrieve an order](#place-and-retrieve-an-order)
- [Inspect SQL state](#inspect-sql-state)
- [Receive the message from SQS](#receive-the-message-from-sqs)
- [Exercise: make publishing fail, watch durable retries, recover](#exercise-make-publishing-fail-watch-durable-retries-recover)
- [Exercise: duplicate delivery](#exercise-duplicate-delivery)
- [The delivery contract and consumer idempotency](#the-delivery-contract-and-consumer-idempotency)
- [Configuration reference](#configuration-reference)
- [Running the tests](#running-the-tests)
- [Compose profiles and the full stack](#compose-profiles-and-the-full-stack)
- [Operational limitations](#operational-limitations)
- [Future extensions](#future-extensions)
- [Sources](#sources)

## The problem: dual writes

Placing an order has two side effects: a row in the database, and an event telling other systems about it.
The naive implementation does both:

```csharp
await db.SaveChangesAsync();          // 1. commit the order
await eventBridge.PutEventsAsync(...); // 2. tell the world
```

These are two separate systems with no shared transaction, so one can succeed while the other fails:

- The commit succeeds, then the process crashes or the network drops before step 2. The order exists, but
  nobody is ever told.
- You reverse the order (publish first) and the commit then fails. Other systems act on an order that does
  not exist.

Wrapping both in `try/catch` does not fix this, because a crash between the two statements leaves no code
running to repair it. This is the *dual-write problem*.

## What the outbox guarantees (and what it does not)

The transactional outbox removes the second system from the critical path. The application writes the
business row **and** an event row to the *same database* in *one transaction*. Either both commit or neither
does, so "order saved but event lost" and "event emitted for a rolled-back order" cannot happen. A separate
relay process later reads the event rows and publishes them.

What you get:

- **Atomicity**: an order and its `OutboxMessages` row commit or roll back together.
- **No lost events** while the database survives: the event is durable before anything touches the network.

What you do not get, and must design for:

- **Exactly-once delivery.** The relay can publish an event, then crash before recording that it did. When
  the lease expires the event is published again. Delivery is **at least once**. See
  [the delivery contract](#the-delivery-contract-and-consumer-idempotency).
- **Ordering across messages.** Messages are claimed in `OccurredOnUtc, Id` order, but retries and multiple
  publishers mean consumers must not rely on global ordering.

AWS describes the same pattern and its duplicate-publication caveat in its
[transactional outbox guidance](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html).
The repository's research note, [dotnet-transactional-outbox.md](../docs/research/dotnet-transactional-outbox.md),
records the sources and the reasoning behind each design decision here.

## Architecture and message flow

The solution follows Ardalis-style Clean Architecture dependency direction:

| Project | Depends on | Responsibility |
| --- | --- | --- |
| `TransactionalOutbox.Core` | BCL only | `Order` entity, `OrderPlacedDomainEvent` |
| `TransactionalOutbox.UseCases` | Core | `PlaceOrderHandler`, repository and unit-of-work abstractions. No EF Core, AWS or mediator reference. |
| `TransactionalOutbox.Infrastructure` | Core, UseCases, EF Core SQL Server, AWSSDK.EventBridge | `AppDbContext`, migrations, the outbox store, the EventBridge publisher, hosted services |
| `TransactionalOutbox.Web` | all of the above | Minimal API endpoints and the composition root |

There are two phases.

### Phase 1: write (in the request)

1. `POST /orders` calls `PlaceOrderHandler`, which creates an `Order`. The aggregate records an
   `OrderPlacedDomainEvent`.
2. On `SaveChangesAsync`, `ConvertDomainEventsToOutboxInterceptor` (an EF Core `ISaveChangesInterceptor`)
   turns each domain event into an `OutboxMessage` and adds it to the same `DbContext`.
3. EF Core saves the order and the outbox row in **one transaction**. The HTTP response is `201 Created`.
   Nothing in this phase talks to AWS, so an EventBridge outage never fails order placement.

### Phase 2: relay (in the background)

`OutboxPublisherService` is a thin `BackgroundService`. Each cycle it creates a DI scope and runs
`OutboxProcessor`:

1. **Claim (short transaction).** `SqlServerOutboxStore` runs one parameterized
   `UPDATE ... OUTPUT` over a CTE selecting eligible rows `WITH (UPDLOCK, READPAST, ROWLOCK)`. It sets a unique
   worker ID and a lease expiry (`LockedBy`, `LockedUntilUtc`) and increments `AttemptCount`. Eligible means
   not processed, not dead-lettered, `NextAttemptOnUtc` due, and either unlocked or lease expired. Eligibility,
   lease expiry and retry times are all computed with `SYSUTCDATETIME()`, so they read one clock: the
   database's, and application-host clock skew cannot cause tight retry loops. **The transaction commits
   before any network call.** (Two timestamps still come from the host clock: the initial `NextAttemptOnUtc`,
   which the interceptor sets to the event's `OccurredOnUtc`, and the cleanup cutoff. Skew there only shifts
   first-publish latency or retention slightly; it cannot cause duplicates or retry storms.)
2. **Publish.** `EventBridgePublisher` sends one claimed message per `PutEvents` request, with
   `Source = sample.orders`, `DetailType = OrderPlaced` and the stored JSON as `Detail`.
3. **Acknowledge.** On success the row gets `ProcessedOnUtc` and the EventBridge event ID. On failure it gets a
   retry time or, for permanent failures, a dead-letter timestamp. Every one of these updates includes
   `LockedBy = @workerId` in its `WHERE` clause, so a publisher whose lease expired cannot overwrite state
   written by a newer owner.

`OutboxCleanupService` separately deletes **processed** rows older than `Outbox:ProcessedRetention`
(default 7 days). Dead-lettered rows are never deleted automatically.

### Why no SQL locks are held during network calls

The claim holds row locks only for the duration of one fast `UPDATE`. If the publisher held that
transaction open while calling EventBridge, a slow or hung network call would pin locks, block other
publishers, and risk transaction timeouts. Instead, the *lease* (`LockedBy` + `LockedUntilUtc`) is the
durable claim: it marks a row as "mine until time T" without any open transaction. If a publisher dies, the
lease simply expires and another worker takes the row.

One claim leases a whole batch, but messages are published one at a time, so the lease has to cover more than
one publish. The SDK's per-request timeout and retry count (`EventBridge:RequestTimeout` 5 s,
`EventBridge:MaxErrorRetry` 2) bound one publish to roughly 15 s (`RequestTimeout x (MaxErrorRetry + 1)`, plus
the SDK's short backoff between retries). Startup validation requires `Outbox:LeaseDuration` (default 30 s) to
be longer than that budget, so a single publish is not expected to outlive its lease. Before each publish,
`OutboxProcessor` checks the time since the claim: once the remaining lease is shorter than one publish budget
it stops the batch and releases the unpublished rows (clearing the lease and undoing the attempt the claim
counted), so a slow EventBridge cannot leave rows being published by two workers at once.

`UPDLOCK` + `READPAST` is the SQL Server queue idiom: concurrent publishers skip rows another publisher is
claiming instead of blocking on them. See the
[table hints documentation](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17).

### Checking the per-entry `PutEvents` result

A successful HTTP response from `PutEvents` does **not** mean the event was accepted. The response carries one
result entry per request entry, and individual entries can fail inside a 200 response. `EventBridgePublisher`
marks a message processed only when **all** of these hold:

- the response and its single result entry exist (null or malformed data is a failure);
- `FailedEntryCount` is zero;
- the entry has no `ErrorCode`;
- the entry has a non-empty EventBridge event ID.

Everything else is classified as retryable, except `AccessDeniedException`, `InvalidArgument` and
`MalformedDetail`, which are permanent. EventBridge can also return HTTP 200 for an event bus that does not
exist, with the event silently dropped, so `EventBridgeStartupValidator` calls `DescribeEventBus` at startup and
the host refuses to start if the configured bus is missing. See the
[PutEvents documentation](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-putevents.html).

### Retries, dead-lettering and shutdown

- The AWS SDK handles a couple of immediate transport retries. **Durable** retries live in SQL.
- A retryable failure clears the lease and sets `NextAttemptOnUtc` to `min(2^attempt seconds, MaxRetryDelay)`
  plus up to 25 % jitter (see `RetrySchedule`). The error text is stored in `LastError`, capped at 2048
  characters.
- A permanent entry error dead-letters the row immediately. A retryable failure dead-letters it once
  `AttemptCount` reaches `Outbox:MaxAttempts` (default 10). Dead-lettered rows keep their `LastError`.
- Host shutdown cancellation propagates and is **not** recorded as a message failure. The row's lease just
  expires and another publisher picks it up. One exception: if EventBridge already accepted the event when
  shutdown begins, the success update still runs (with its own short timeout) so the row is marked processed
  instead of being published again.
- Cycle-level failures (for example SQL Server being unavailable) are logged and delayed; they do not
  terminate the host.

There is no Polly: stacking a second retry loop on top of the SDK's would multiply retries and unbounded
latency, and the SQL schedule already provides the long-horizon policy.

## Dependency choices

| Production dependency | Why |
| --- | --- |
| ASP.NET Core shared framework | Minimal API, DI, `BackgroundService`, logging, options, `System.Text.Json` |
| `Microsoft.EntityFrameworkCore.SqlServer` 10.0.x (+ matching design-time tooling) | Persistence, migrations, the save-changes interceptor |
| `AWSSDK.EventBridge` | The official EventBridge client |

Libraries you would often reach for are deliberately absent:

- **MediatR / Mediator**: `PlaceOrderHandler` is a plain scoped class called from the endpoint. One handler
  does not need a dispatch pipeline.
- **Quartz / Hangfire**: a `BackgroundService` polling loop is enough for a single recurring job.
- **Scrutor**: a few explicit `AddScoped` lines are clearer than assembly scanning.
- **Newtonsoft.Json**: `System.Text.Json` ships in the framework.
- **Polly**: see above; the durable retry schedule is data in SQL.
- **MassTransit / CAP / NServiceBus**: excellent products that ship their own outbox. They would hide the
  mechanics this sample exists to show.

Test-only packages (xUnit, `AWSSDK.SQS` for reading the queue) live in the test projects only.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download). The sample pins it in `global.json` with a minimum of
  10.0.102; any 10.0 feature band (10.0.1xx, 2xx, 3xx, 4xx, ...) works.
- Docker with Compose v2
- `curl`

The SQL Server image is Microsoft's Linux container. Microsoft supports it on **x86-64** hosts and states that
emulation and translation environments (Rosetta 2, QEMU) are **not tested or supported**
([SQL Server 2025 on Linux release notes](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-release-notes-2025?view=sql-server-ver17)).
`compose.yaml` therefore sets `platform: linux/amd64` on the `sqlserver` service. On Apple silicon this works
through Docker's amd64 emulation and is fine for a local tutorial, but it is **unsupported**: expect slower
startup (the health check allows a 90-second warm-up) and do not draw production conclusions from it.

## Run it locally

All commands run from this directory (`transactional-outbox/dotnet10-eventbridge`).

### 1. Create your `.env`

```bash
cp .env.example .env
```

`.env` holds `MSSQL_SA_PASSWORD`, a disposable development value that SQL Server's password policy accepts.
The file is gitignored. Never reuse that password anywhere real.

The same password is also hard-coded in two places that run on your machine rather than in Compose: the
connection string in `src/TransactionalOutbox.Web/appsettings.Development.json` and the default connection
string in `tests/TransactionalOutbox.IntegrationTests/InfrastructureFixture.cs`. If you change it in `.env`,
update both (or override them with the `ConnectionStrings__SqlServer` environment variable).

### 2. Start SQL Server and LocalStack

```bash
docker compose up -d --wait
```

`--wait` returns once both health checks pass. This starts only the two infrastructure services; the API runs
on your machine so you can debug it.

| Service | Host address | Notes |
| --- | --- | --- |
| SQL Server | `localhost,14339` (not 1433, to avoid clashing with a local instance) | login `sa`, password from `.env`; app database `TransactionalOutbox`; integration-test database `TransactionalOutboxTests` |
| LocalStack | `http://localhost:4566` | EventBridge and SQS only |

Compose uses the fixed project name `transactional-outbox`, so a second copy of this stack cannot run beside
it (host ports are fixed too).

On first start LocalStack runs `localstack/ready.d/10-create-resources.sh`, an idempotent script that creates
the `orders` event bus, the `order-placed` SQS queue, a queue policy letting the rule send to it, the enabled
rule `route-order-placed` (source `sample.orders`, detail-type `OrderPlaced`), and the SQS target. It exits
non-zero on any failure, which makes the LocalStack health check fail visibly.

> LocalStack is pinned to **4.14.0**, which runs without an account. The 2026.x images require a
> `LOCALSTACK_AUTH_TOKEN`. To opt in, set `LOCALSTACK_IMAGE` and `LOCALSTACK_AUTH_TOKEN` in `.env` (see the
> commented lines in `.env.example`).
>
> LocalStack has **no persistence** here: restarting the container discards its state and re-runs the ready
> hook against an empty instance.

### 3. Apply the EF Core migration

Migrations are **never** applied automatically at startup, in any profile. You apply them explicitly. The EF
CLI is pinned as a local tool in `.config/dotnet-tools.json`, so restore it first:

```bash
dotnet tool restore
```

```bash
dotnet tool run dotnet-ef database update --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web
```

(`dotnet ef database update ...` is equivalent once the tool is restored.) The migration lives in
Infrastructure; Web is the startup project that supplies configuration. It reads the development connection
string from `appsettings.Development.json`, which points at `localhost,14339`. This creates the
`TransactionalOutbox` database with the `Orders` and `OutboxMessages` tables.

### 4. Run the API

```bash
dotnet run --project src/TransactionalOutbox.Web
```

The default launch profile `http` listens on **http://localhost:5080** in the `Development` environment and
sets dummy AWS credentials (`AWS_ACCESS_KEY_ID=test`, `AWS_SECRET_ACCESS_KEY=test`) from
`Properties/launchSettings.json`. LocalStack accepts any credentials; never use real ones against it.

Leave this terminal running and open a second one for the rest of the tutorial.

At startup the app validates its options and checks that the `orders` event bus exists. If you see
`EventBridge event bus 'orders' could not be validated`, LocalStack is not healthy yet (or its ready hook
failed): check `docker compose ps` and `docker compose logs localstack`.

`GET /health/live` returns `200` and is **liveness only**: it does not check SQL Server or EventBridge.

## Place and retrieve an order

```bash
curl --request POST http://localhost:5080/orders --header 'Content-Type: application/json' --data '{"customerId":"11111111-1111-1111-1111-111111111111","totalAmount":42.50,"currency":"AUD"}'
```

Expected: `201 Created`, a `Location` header, and a body such as `{"orderId":"3b959700-4843-400c-8a7c-f71c37f8cde8"}`.
Copy the ID and fetch the order back:

```bash
curl http://localhost:5080/orders/<orderId>
```

```json
{"id":"3b959700-...","customerId":"11111111-...","totalAmount":42.50,"currency":"AUD","status":"Placed","createdOnUtc":"2026-10-01T00:52:38.979609+00:00"}
```

Other responses: an unknown ID returns `404`; invalid input (for example `"totalAmount":-1`, or malformed JSON)
returns `400` with a problem-details body.

> The API is **unauthenticated by design** and exposes no outbox admin endpoints. It is a teaching sample, not
> production-hardened. See [Operational limitations](#operational-limitations).

## Inspect SQL state

These commands run `sqlcmd` inside the SQL Server container. `$MSSQL_SA_PASSWORD` is expanded *inside* the
container, where Compose has already set it from `.env`.

The order:

```bash
docker compose exec sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d TransactionalOutbox -W -Q "SELECT Id, CustomerId, TotalAmount, Currency, Status, CreatedOnUtc FROM dbo.Orders"'
```

The outbox row:

```bash
docker compose exec sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d TransactionalOutbox -W -Q "SELECT Id, EventType, AttemptCount, NextAttemptOnUtc, LockedBy, ProcessedOnUtc, EventBridgeEventId, LastError, DeadLetteredOnUtc FROM dbo.OutboxMessages ORDER BY OccurredOnUtc"'
```

Within a couple of seconds a healthy row looks like this:

| Column | Value | Meaning |
| --- | --- | --- |
| `Id` | a GUID | The outbox ID, which is also the `eventId` inside the event JSON. **Stable across retries.** The order's ID is the event's `correlationId` and `orderId`. |
| `AttemptCount` | `1` | Incremented by each claim |
| `LockedBy` | `NULL` | Lease released on success |
| `ProcessedOnUtc` | a timestamp | The publisher recorded success |
| `EventBridgeEventId` | a GUID | The ID EventBridge returned for the accepted entry |
| `LastError`, `DeadLetteredOnUtc` | `NULL` | Nothing went wrong |

The row is written in the same transaction as the order, so the pair is never seen apart: an order without an
outbox row (or the reverse) would mean the pattern is broken.

## Receive the message from SQS

The rule routes the EventBridge event into the `order-placed` queue. Read it with the AWS CLI that ships in the
LocalStack container (`awslocal` is the AWS CLI pointed at LocalStack):

```bash
docker compose exec localstack awslocal sqs receive-message --queue-url http://sqs.ap-southeast-2.localhost.localstack.cloud:4566/000000000000/order-placed --region ap-southeast-2
```

The `Body` is the EventBridge envelope (`source`, `detail-type`, EventBridge's own `id`) and the `detail`
object is the JSON the outbox stored:

```json
{
  "version": "0", "id": "f6035ef0-...", "detail-type": "OrderPlaced", "source": "sample.orders",
  "detail": {
    "eventId": "0872c246-...", "correlationId": "3b959700-...", "schemaVersion": 1,
    "occurredOnUtc": "2026-10-01T00:52:38.979609+00:00", "orderId": "3b959700-...",
    "customerId": "11111111-...", "totalAmount": 42.5, "currency": "AUD"
  }
}
```

Two different IDs appear: the envelope `id` is assigned by EventBridge per `PutEvents` accept (and is what is
stored in `EventBridgeEventId`); `detail.eventId` is the outbox row ID and never changes across retries or
republications. **Consumers deduplicate on `detail.eventId`.**

`receive-message` hides the message for the queue's visibility timeout rather than deleting it, so an
immediate second call may return nothing. To clear the queue between experiments:

```bash
docker compose exec localstack awslocal sqs purge-queue --queue-url http://sqs.ap-southeast-2.localhost.localstack.cloud:4566/000000000000/order-placed --region ap-southeast-2
```

## Exercise: make publishing fail, watch durable retries, recover

The point: an EventBridge outage does not lose or block orders. Messages wait in SQL and retry with backoff.

**1. Take LocalStack down** (keep the API running):

```bash
docker compose stop localstack
```

**2. Place an order.** It still returns `201`: the write path never touches AWS.

```bash
curl --request POST http://localhost:5080/orders --header 'Content-Type: application/json' --data '{"customerId":"22222222-2222-2222-2222-222222222222","totalAmount":10.00,"currency":"AUD"}'
```

**3. Watch the retries.** Run this a few times, ten or so seconds apart:

```bash
docker compose exec sqlserver bash -c '/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d TransactionalOutbox -W -Q "SELECT Id, AttemptCount, NextAttemptOnUtc, SYSUTCDATETIME() AS NowUtc, LockedBy, ProcessedOnUtc, LastError FROM dbo.OutboxMessages WHERE ProcessedOnUtc IS NULL"'
```

What you should see (abridged):

```text
AttemptCount NextAttemptOnUtc              NowUtc                      ProcessedOnUtc LastError
4            2026-10-01 00:55:38 +00:00    2026-10-01 00:55:40         NULL           HttpRequestException: Connection refused (localhost:4566)
```

- `AttemptCount` keeps rising: attempts at roughly 2, 4, 8, 16, 32, 64 seconds apart, capped at
  `Outbox:MaxRetryDelay` (5 minutes), each with up to 25 % jitter.
- `NextAttemptOnUtc` moves further into the future after each failure. (A row mid-attempt briefly shows a
  `LockedBy` worker; the lease is cleared when the retry is scheduled.)
- `LastError` records the failure, bounded in length.
- The app log shows the warnings for each failed attempt.
- `DeadLetteredOnUtc` stays `NULL` until `Outbox:MaxAttempts` (10) is reached. With the default backoff that is
  roughly 15 minutes, so you can recover long before it.

**4. Recover.** Restart LocalStack and wait for it to be healthy; the ready hook recreates the bus, queue,
rule and target on the empty instance.

```bash
docker compose start localstack
```

```bash
docker compose ps localstack
```

Wait for `(healthy)`. The next retry that comes due succeeds. Because of exponential backoff this can take up to
the current delay (the later the attempt, the longer the wait), so be patient and re-run the query from step 3
(drop the `WHERE` clause to see processed rows too). When it succeeds `ProcessedOnUtc` and
`EventBridgeEventId` are set, `LockedBy` is cleared and `LastError` is `NULL` again, and `AttemptCount` keeps its
final value as a record of how many tries it took. Then receive from SQS as before to confirm the event arrived.

> **Why does restarting LocalStack not break the app?** The `AmazonEventBridge` client is stateless HTTP, so it
> reconnects as soon as the service is back. The startup bus check only runs once at host start. In real AWS
> the bus is persistent, so nothing needs recreating.

### Outbox dead-letter vs. an EventBridge target DLQ

These are two different things, and this sample implements only the first.

- **Outbox dead-letter** (`DeadLetteredOnUtc` set in SQL): the *publisher* could not get EventBridge to accept
  the event, either because of a permanent error (`AccessDeniedException`, `InvalidArgument`, `MalformedDetail`)
  or because retries were exhausted. The event never reached EventBridge. The row is kept for diagnosis; there is
  no replay tooling yet.
- **EventBridge target DLQ** (a *future extension*): EventBridge *accepted* the event, but could not deliver it to
  a rule target (for example SQS permissions are wrong). That is configured on the rule target in EventBridge,
  not in this app. See [EventBridge dead-letter queues](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-rule-dlq.html).

The `order-placed` SQS queue in this sample is the demo *target*, not a poison queue.

To see a dead-letter yourself, you would need a permanent error; this sample does not ship a switch for that.
The `OutboxProcessorTests` unit tests and the `LeaseAndStateTransitionTests` integration tests cover the dead-letter transitions.

## Exercise: duplicate delivery

Delivery is at least once, so a crash in the wrong place produces a duplicate. Reproducing it by killing a
process at exactly the right instant is flaky, so an integration test does it deterministically: it publishes
through the real publisher and real LocalStack, then makes every following state update throw as if the process
had died. After the lease expires a second worker republishes the **same** outbox message.

It needs the infrastructure from [step 2](#2-start-sql-server-and-localstack) running (the API does not have to
be):

```bash
dotnet test tests/TransactionalOutbox.IntegrationTests --filter "FullyQualifiedName~EventBridgeToSqsTests.CrashBetweenPublishAndSuccessUpdateCausesDuplicateWithSameEventId"
```

The test is `CrashBetweenPublishAndSuccessUpdateCausesDuplicateWithSameEventId` in
`tests/TransactionalOutbox.IntegrationTests/Outbox/EventBridgeToSqsTests.cs`. Read its body: it asserts that SQS
holds **two** messages whose `detail.eventId` is identical, that the row ends up processed, and that
`AttemptCount` is `2`. A passing test means the duplicate was produced and observed. The duplicate is expected
behaviour, not a bug.

Note this test purges the `order-placed` queue and uses the separate `TransactionalOutboxTests` database,
which it drops and recreates; your `TransactionalOutbox` data is untouched.

## The delivery contract and consumer idempotency

- The publisher guarantees **at-least-once** delivery to EventBridge for every committed order.
- A message can be delivered more than once: after a crash between `PutEvents` and the success update, if a
  publish is slow enough to outlast its lease despite the per-batch budget check, and, rarely, because EventBridge itself may invoke a target more than once
  ([EventBridge troubleshooting](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-troubleshooting.html)).
- Therefore **consumers must be idempotent**. The key is `detail.eventId`: the outbox row ID, identical on every
  retry and duplicate. A typical consumer stores processed event IDs (an *inbox* table with a unique
  constraint) in the same transaction as its side effects and skips an ID it has seen.
- Do not use the EventBridge envelope `id` as the key: a duplicate publication gets a *new* envelope ID.

The sample does not include a consumer. An idempotent SQS consumer is listed under
[future extensions](#future-extensions).

## Configuration reference

Defaults live in `src/TransactionalOutbox.Web/appsettings.json`; `appsettings.Development.json` supplies the
local SQL connection string and LocalStack endpoint. Override any key with environment variables using `__` as
the separator (for example `Outbox__BatchSize=5`).

| Key | Default | Meaning |
| --- | --- | --- |
| `Outbox:Enabled` | `true` | Turn the startup bus check, publisher and cleanup services off (useful for running only the API) |
| `Outbox:BatchSize` | `20` | Messages claimed per cycle |
| `Outbox:MaxAttempts` | `10` | Attempts before a retryable failure is dead-lettered |
| `Outbox:LeaseDuration` | `00:00:30` | How long a claim is held before another worker may take it |
| `Outbox:IdleDelay` | `00:00:02` | Wait when no messages are eligible (plus small jitter) |
| `Outbox:MaxRetryDelay` | `00:05:00` | Cap for the exponential backoff |
| `Outbox:ProcessedRetention` | `7.00:00:00` | Age at which processed rows are deleted |
| `Outbox:CleanupInterval` | `01:00:00` | How often cleanup runs |
| `EventBridge:EventBusName` | `orders` | Target bus (validated at startup) |
| `EventBridge:Source` / `DetailType` | `sample.orders` / `OrderPlaced` | Must match the rule's event pattern |
| `EventBridge:ServiceUrl` | empty | Set to LocalStack locally; leave empty to use real AWS |
| `EventBridge:AuthenticationRegion` | `ap-southeast-2` (Development) | Region used when `ServiceUrl` is set |
| `EventBridge:RequestTimeout` | `00:00:05` | SDK per-request timeout |
| `EventBridge:MaxErrorRetry` | `2` | SDK immediate retries per publish. `RequestTimeout x (MaxErrorRetry + 1)` must be less than `Outbox:LeaseDuration` (validated at startup). |

The application has no LocalStack-specific code path. Pointing it at LocalStack is purely configuration:
service URL, region and dummy credentials. For .NET clients LocalStack recommends `ServiceURL` plus
`AuthenticationRegion` ([LocalStack AWS SDK for .NET](https://docs.localstack.cloud/aws/connecting/aws-sdks/dotnet/)).

## Running the tests

Unit tests need nothing running:

```bash
dotnet test tests/TransactionalOutbox.UnitTests
```

Integration tests use real SQL Server and LocalStack from `docker compose up -d --wait`:

```bash
dotnet test tests/TransactionalOutbox.IntegrationTests
```

They cover atomic order-plus-outbox writes, concurrent claims across workers, lease expiry and ownership checks,
retry and dead-letter transitions, cleanup, the HTTP endpoints, composition rules, and EventBridge-to-SQS
delivery including the duplicate case. The integration tests share the infrastructure's fixed ports and the
`order-placed` queue, so run them one test run at a time.

## Compose profiles and the full stack

| Command | Starts |
| --- | --- |
| `docker compose up -d --wait` | SQL Server and LocalStack only. You run the API with `dotnet run`. |
| `docker compose --profile full up -d --build --wait` | The above plus the `web` service, built from `src/TransactionalOutbox.Web/Dockerfile`. |

In the `full` profile the API listens on **http://localhost:8080**, runs in the `Production` environment, and
receives its connection string, `EventBridge__ServiceUrl=http://localstack:4566` and dummy credentials from
`compose.yaml`. **Migrations are still not applied automatically.** On a fresh volume run the
[migration step](#3-apply-the-ef-core-migration) from your machine first, and the API will find the schema. Do not
run the `dotnet run` and `full` API at the same time against the same database unless you want two publishers
competing for rows (which is safe, and a good way to see lease-based claiming).

```bash
curl http://localhost:8080/health/live
```

Stop the full stack's API but keep the infrastructure:

```bash
docker compose --profile full stop web
```

Tear everything down, **deleting the SQL data volume**:

```bash
docker compose --profile full down -v
```

## Operational limitations

This is a tutorial, not a production template.

- **No authentication or authorization** on any endpoint, and no outbox admin endpoints. It is not
  production-hardened. Add authentication before exposing anything beyond localhost.
- **Dummy credentials and a development SA password.** The `sa` login is for local use only.
- **At-least-once, not exactly-once**, as described above. There is no consumer or inbox in this repository.
- **One message per `PutEvents` request.** Simple and keeps per-message error handling obvious, at the cost of
  throughput. EventBridge allows batching up to 10 entries per request.
- **Polling, not push.** Latency is bounded below by `Outbox:IdleDelay`, and an idle system still polls SQL.
- **No dead-letter replay or inspection tooling.** Dead-lettered rows stay in `OutboxMessages` until you handle
  them in SQL. `AccessDeniedException` is classified as permanent, so an IAM misconfiguration dead-letters every
  message published while it lasts; fixing IAM does not bring them back without a replay tool (listed under
  future extensions).
- **IAM on real AWS.** The publisher needs `events:PutEvents` on the bus, and the startup validator also needs
  `events:DescribeEventBus`; without it the host refuses to start.
- **No metrics, tracing or alerting.** Only logs.
- **LocalStack is not AWS.** Passing here does not prove parity with real EventBridge or SQS behaviour (IAM,
  quotas, throttling, retry semantics, delivery timing). Validate against real AWS before relying on this.
- **SQL Server on Apple silicon runs under unsupported amd64 emulation.**
- **LocalStack state is ephemeral**, and its 4.14.0 image is pinned for licensing reasons, not because it is the
  newest.
- **`/health/live` is liveness only**, not readiness.

## Future extensions

- Move the hosted processor into a separate Worker executable without changing Core or UseCases.
- Batch `PutEvents` entries while preserving response-index mapping.
- Add an idempotent SQS consumer and inbox example.
- Add EventBridge target DLQs and archive/replay.
- Add metrics, traces, dashboards and alerting.
- Add authenticated administrative dead-letter inspection and replay.
- Validate behaviour against real AWS rather than treating LocalStack as proof of service parity.

## Sources

Repository:

- [Research note: a dependency-light transactional outbox on .NET 10](../docs/research/dotnet-transactional-outbox.md)

Primary documentation:

- AWS: [Transactional outbox pattern](https://docs.aws.amazon.com/prescriptive-guidance/latest/cloud-design-patterns/transactional-outbox.html)
- AWS: [Sending events with PutEvents](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-putevents.html),
  [PutEventsResultEntry](https://docs.aws.amazon.com/eventbridge/latest/APIReference/API_PutEventsResultEntry.html),
  [EventBridge dead-letter queues](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-rule-dlq.html),
  [EventBridge targets](https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-targets.html)
- Microsoft: [EF Core transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions),
  [EF Core interceptors](https://learn.microsoft.com/en-us/ef/core/logging-events-diagnostics/interceptors),
  [connection resiliency](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency),
  [migrations](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/)
- Microsoft: [SQL Server table hints (UPDLOCK, READPAST, ROWLOCK)](https://learn.microsoft.com/en-us/sql/t-sql/queries/hints-transact-sql-table?view=sql-server-ver17),
  [OUTPUT clause](https://learn.microsoft.com/en-us/sql/t-sql/queries/output-clause-transact-sql?view=sql-server-ver17),
  [SQL Server Linux container quickstart](https://learn.microsoft.com/en-us/sql/linux/quickstart-install-connect-docker?view=sql-server-ver17)
- Microsoft: [Hosted services in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0)
- LocalStack: [EventBridge](https://docs.localstack.cloud/aws/services/events/),
  [SQS](https://docs.localstack.cloud/aws/services/sqs/),
  [Initialization hooks](https://docs.localstack.cloud/aws/customization/advanced/initialization-hooks/),
  [AWS SDK for .NET](https://docs.localstack.cloud/aws/connecting/aws-sdks/dotnet/)
