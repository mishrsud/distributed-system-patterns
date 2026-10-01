# Transactional Outbox

Reliably publishing an event when you change data is harder than it looks. This folder contains two samples of
the **transactional outbox** pattern, plus the research behind the newer one.

[Back to the repository README](../README.md)

## Contents

- [The problem in one minute](#the-problem-in-one-minute)
- [The two samples](#the-two-samples)
- [`dotnet10-eventbridge`: the main sample](#dotnet10-eventbridge-the-main-sample)
- [`gatherly`: the .NET 6 baseline](#gatherly-the-net-6-baseline)
- [Choosing where to start](#choosing-where-to-start)
- [Guarantees and non-guarantees](#guarantees-and-non-guarantees)
- [Running and testing](#running-and-testing)
- [Research and design documents](#research-and-design-documents)

## The problem in one minute

Placing an order has two side effects: a database row, and an event telling other systems about it. The two live
in different systems with no shared transaction:

```csharp
await db.SaveChangesAsync();           // 1. commit the order
await eventBridge.PutEventsAsync(...); // 2. tell the world
```

If the process dies between the two lines, the order exists and nobody is told. Reverse the order and a failed
commit leaves other systems acting on an order that never existed. This is the *dual-write problem*.

The outbox removes the second system from the critical path. The application writes the business row **and** an
event row to the *same database* in *one transaction*. A separate relay reads the event rows later and publishes
them. Either both rows commit or neither does.

```text
POST /orders
  -> PlaceOrder use case
  -> Order aggregate records OrderPlaced
  -> SaveChanges interceptor creates OutboxMessage
  -> ONE SQL Server transaction commits Order + OutboxMessage
  -> background service leases pending messages
  -> PutEvents to EventBridge
  -> EventBridge rule routes OrderPlaced to SQS
```

## The two samples

| | [`dotnet10-eventbridge`](dotnet10-eventbridge/) | [`gatherly`](gatherly/) |
| --- | --- | --- |
| Purpose | The main, hardened tutorial | A simple baseline to compare against |
| Target | .NET 10, EF Core 10 | .NET 6, EF Core 6 |
| Domain | Orders | Gatherings, members, invitations |
| Event capture | EF Core `SaveChangesInterceptor` | EF Core `SaveChangesInterceptor` |
| Relay | `BackgroundService` polling loop | Quartz job |
| Delivery target | Amazon EventBridge (LocalStack) routed to SQS | In-process MediatR handlers |
| Multiple instances | Safe: leased claims with `UPDLOCK, READPAST, ROWLOCK` | Not addressed: no claiming or locking |
| Failure handling | Durable retries with backoff and jitter, dead-lettering, stale-lease protection | None: rows stay unprocessed until the job picks them up again |
| Serialization | `System.Text.Json`, stable logical event names | Newtonsoft.Json with `TypeNameHandling.All` |
| Dependencies | ASP.NET Core, EF Core SQL Server, `AWSSDK.EventBridge` | MediatR, Quartz, Scrutor, Newtonsoft.Json, Swashbuckle |
| Tests | Unit and integration (real SQL Server and LocalStack) | None |
| Status | Documented, tested, runnable | Left unchanged; reference only |

The comparison under *Failure handling* and *Multiple instances* reflects what the code in `gatherly` does today;
it is not a criticism of the pattern. It shows what a first, naive outbox leaves out and why the .NET 10 sample
adds each piece.

## `dotnet10-eventbridge`: the main sample

**Read the tutorial: [`dotnet10-eventbridge/README.md`](dotnet10-eventbridge/README.md).**

An ASP.NET Core API saves an order and an `OrderPlaced` event in one SQL Server transaction. A background
publisher relays the event to EventBridge, where a rule routes it to an SQS queue you can read. The tutorial
walks you through running it, placing an order, inspecting the outbox row, breaking the publisher on purpose,
and watching a duplicate delivery happen.

### Structure

The solution follows Ardalis-style Clean Architecture dependency direction, without the template's optional
libraries.

```text
dotnet10-eventbridge/
|-- src/
|   |-- TransactionalOutbox.Core            Order aggregate, OrderPlacedDomainEvent (BCL only)
|   |-- TransactionalOutbox.UseCases        PlaceOrderHandler, repository and unit-of-work abstractions
|   |-- TransactionalOutbox.Infrastructure  EF Core, interceptor, SQL Server outbox store, EventBridge publisher, hosted services
|   `-- TransactionalOutbox.Web             Minimal API endpoints and composition root
|-- tests/
|   |-- TransactionalOutbox.UnitTests       Deterministic business and outbox policy
|   `-- TransactionalOutbox.IntegrationTests  Real SQL Server and LocalStack from Docker Compose
|-- localstack/ready.d/                     Idempotent script that creates the bus, queue, rule and target
|-- compose.yaml                            SQL Server and LocalStack (and the API under the `full` profile)
`-- .env.example                            Disposable development values
```

### How the relay works

1. **Write.** `POST /orders` runs `PlaceOrderHandler`. The `Order` aggregate records an `OrderPlacedDomainEvent`.
   `ConvertDomainEventsToOutboxInterceptor` turns it into an `OutboxMessage` during `SaveChangesAsync`, so the
   order and outbox row commit in one transaction. Nothing in the request talks to AWS, so an EventBridge outage
   never fails order placement.
2. **Claim.** `OutboxPublisherService` (a thin `BackgroundService`) creates a DI scope per cycle.
   `SqlServerOutboxStore` runs one parameterized `UPDATE ... OUTPUT` over a CTE selecting eligible rows with
   `UPDLOCK, READPAST, ROWLOCK`. It stamps a worker ID and a lease expiry and commits **before** any network
   call. Eligibility and lease comparisons use `SYSUTCDATETIME()`, so the database clock is the only clock that
   matters.
3. **Publish.** `EventBridgePublisher` sends one message per `PutEvents` request. A message counts as published
   only if `FailedEntryCount` is zero and the result entry has no error code and a non-empty event ID. An
   HTTP 200 alone is not trusted.
4. **Acknowledge.** Success, retry and dead-letter updates all include the worker ID in their `WHERE` clause, so
   a publisher whose lease has expired cannot overwrite a newer owner's work.
5. **Clean up.** `OutboxCleanupService` deletes only *processed* rows older than the retention interval.
   Dead-lettered rows are kept.

### What it demonstrates

- Atomic order plus outbox write, including rollback when the database fails.
- Safe concurrent publishers: simultaneous claimers receive disjoint rows.
- Leases: an abandoned lease becomes claimable after expiry; a stale owner cannot record success or failure.
- Durable retries with bounded exponential backoff and jitter, persisted in SQL across restarts.
- Immediate dead-lettering for permanent errors (`AccessDeniedException`, `InvalidArgument`,
  `MalformedDetail`) and after `Outbox:MaxAttempts` for retryable ones.
- Partial or per-entry `PutEvents` failure does not mark a row processed.
- Startup validation that the configured event bus exists, because EventBridge can return HTTP 200 for a missing
  bus while dropping the event.
- Graceful shutdown: cancellation propagates and is not recorded as a message failure.
- The unavoidable duplicate-delivery window, reproduced deterministically by an integration test.

### Endpoints

| Method and path | Result |
| --- | --- |
| `POST /orders` | `201 Created` with the new order ID; `400` for invalid input |
| `GET /orders/{id}` | The committed order, or `404` |
| `GET /health/live` | `200`; liveness only, does not check SQL Server or EventBridge |

There are no outbox administration endpoints, and the API is unauthenticated by design.

### Configuration

Defaults are in `src/TransactionalOutbox.Web/appsettings.json`. Override any key with an environment variable
using `__` as the separator (for example `Outbox__BatchSize=5`).

| Key | Default | Meaning |
| --- | --- | --- |
| `Outbox:BatchSize` | `20` | Messages claimed per cycle |
| `Outbox:MaxAttempts` | `10` | Attempts before a retryable failure is dead-lettered |
| `Outbox:LeaseDuration` | `00:00:30` | How long a claim is held. Must exceed one publish budget; validated at startup |
| `Outbox:MaxRetryDelay` | `00:05:00` | Cap for exponential backoff |
| `Outbox:ProcessedRetention` | `7.00:00:00` | Age at which processed rows are deleted |
| `EventBridge:EventBusName` | `orders` | Target bus, validated at startup |
| `EventBridge:ServiceUrl` | empty | Set for LocalStack; leave empty to use real AWS |

The full reference is in the [tutorial](dotnet10-eventbridge/README.md#configuration-reference).

### Event contract

The published event uses a stable logical name, never a CLR type name.

| EventBridge field | Value |
| --- | --- |
| `Source` | `sample.orders` |
| `DetailType` | `OrderPlaced` |
| `EventBusName` | `orders` (configurable) |
| `Detail` | JSON: `eventId`, `correlationId`, `schemaVersion`, `occurredOnUtc`, `orderId`, `customerId`, `totalAmount`, `currency` |

`detail.eventId` is the outbox row ID and stays the same across retries and duplicates. **Consumers deduplicate
on it.** Do not use the EventBridge envelope `id`; a republished event gets a new one.

## `gatherly`: the .NET 6 baseline

A .NET 6 sample (`Gatherly.sln`) with a layered Domain / Application / Infrastructure / Persistence /
Presentation structure. It is kept unchanged as a reference and as the starting point the .NET 10 sample was
designed against.

Its outbox works like this:

1. Domain events raised by aggregates are converted into `OutboxMessage` rows by
   `ConvertDomainEventsToOutboxMessagesInterceptor` when EF Core saves changes.
2. `ProcessOutboxMessagesJob`, a Quartz job marked `[DisallowConcurrentExecution]`, loads up to 20 rows where
   `ProcessedOnUtc` is null, deserializes each with Newtonsoft.Json, publishes it in-process through MediatR, and
   stamps `ProcessedOnUtc`.
3. Domain event handlers (for example sending an invitation or registration email) react to the published
   events.

Treat it as a readable first iteration, and read the "Failure handling" and "Multiple instances" rows in
[the comparison table](#the-two-samples) before copying it. It processes rows without leases or locks, has no
retry or dead-letter state, and relies on a single scheduler instance. It also deserializes with
`TypeNameHandling.All`, which stores CLR type names in the payload. The .NET 10 sample avoids that by using
stable logical event names.

Open `gatherly/Gatherly.sln` in your IDE to explore it. It requires the .NET 6 SDK (or a newer SDK with a .NET 6
runtime installed) and a SQL Server instance. Its default connection string
(`src/Gatherly.App/appsettings.json`) targets `localhost` with Windows authentication (`Trusted_Connection`), so
change it to match your setup. It has no Compose file.

## Choosing where to start

- **You want to learn the pattern and see it run:** start with
  [`dotnet10-eventbridge`](dotnet10-eventbridge/README.md). It is the one with a runnable stack and tests.
- **You want to see what a naive outbox leaves out:** skim `gatherly` first, then read
  [How the relay works](#how-the-relay-works).
- **You want the evidence behind each design choice:** read the
  [research note](docs/research/dotnet-transactional-outbox.md), then the
  [design document](../docs/superpowers/specs/2026-09-20-transactional-outbox-design.md).

## Guarantees and non-guarantees

What the outbox gives you:

- **Atomicity.** An order and its outbox row commit or roll back together.
- **No lost events** while the database survives. The event is durable before anything touches the network.

What it does not give you, and what you must design for:

- **Exactly-once delivery.** If the publisher crashes after EventBridge accepts an event but before SQL Server
  records success, the lease expires and the event is published again. Delivery is **at least once**, so
  consumers must be idempotent, normally with an inbox table holding a unique constraint on consumer name and
  event ID.
- **Ordering.** Messages are claimed in `OccurredOnUtc, Id` order, but retries and multiple publishers mean
  consumers must not rely on global or per-order ordering.
- **Proof of downstream delivery.** `ProcessedOnUtc` proves EventBridge accepted the entry. Only seeing the
  matching SQS message proves the rule routed it to this target.

## Running and testing

All commands run from `dotnet10-eventbridge/`. Prerequisites: .NET 10 SDK, Docker with Compose v2, `curl`.

```bash
cd transactional-outbox/dotnet10-eventbridge
cp .env.example .env
docker compose up -d --wait                # SQL Server (localhost,14339) and LocalStack (localhost:4566)
dotnet tool restore
dotnet tool run dotnet-ef database update --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web
dotnet run --project src/TransactionalOutbox.Web   # http://localhost:5080
```

Migrations are **never** applied automatically, in any profile, so a pristine environment always needs the
`database update` step before the API is usable, including before the first `full`-profile run.

```bash
dotnet test tests/TransactionalOutbox.UnitTests          # nothing needs to be running
dotnet test tests/TransactionalOutbox.IntegrationTests   # needs docker compose up -d --wait
```

Run integration tests one run at a time: they share fixed ports and the `order-placed` queue. They use the
separate `TransactionalOutboxTests` database, which they drop and recreate, so your `TransactionalOutbox` data is
untouched.

| Command | Starts |
| --- | --- |
| `docker compose up -d --wait` | SQL Server and LocalStack only; you run the API with `dotnet run` |
| `docker compose --profile full up -d --build --wait` | The above plus the API at `http://localhost:8080` |
| `docker compose --profile full down -v` | Tears everything down and **deletes the SQL data volume** |

> **Apple silicon:** Microsoft supports the SQL Server Linux container on x86-64 only. `compose.yaml` sets
> `platform: linux/amd64`, which works through emulation but is unsupported.
>
> **LocalStack image:** the default is pinned to 4.14.0, which runs without an account. The 2026.x images need a
> `LOCALSTACK_AUTH_TOKEN`; opt in through `.env`. See `.env.example`.

## Research and design documents

| Document | Subject |
| --- | --- |
| [Tutorial README](dotnet10-eventbridge/README.md) | Step-by-step walkthrough, exercises, configuration and operational limitations |
| [Research note](docs/research/dotnet-transactional-outbox.md) | Primary-source research behind each design decision |
| [Design document](../docs/superpowers/specs/2026-09-20-transactional-outbox-design.md) | Goals, non-goals, claim algorithm, delivery semantics, verification strategy |
| [Implementation plan](../docs/superpowers/plans/2026-09-20-transactional-outbox.md) | Task-by-task build plan |

Possible extensions, none implemented: a separate Worker executable for the relay, batched `PutEvents`, an
idempotent SQS consumer with an inbox table, EventBridge target DLQs and archive/replay, metrics and tracing,
authenticated dead-letter inspection and replay, and validation against real AWS.
