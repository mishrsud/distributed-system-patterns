# .NET 10 Transactional Outbox Sample Design

Date: 2026-09-20

## Purpose

Build a research-backed, runnable transactional outbox sample for experienced and newer .NET developers. The sample demonstrates how to commit an order and its integration-event intent atomically in SQL Server, then deliver the event to LocalStack EventBridge with explicit at-least-once semantics.

The sample will be a new sibling of `transactional-outbox/gatherly`. The existing .NET 6 Gatherly sample remains unchanged.

The supporting primary-source research is in `transactional-outbox/docs/research/dotnet-transactional-outbox.md`.

## Goals

- Target .NET 10 and the latest compatible EF Core 10 patch.
- Run SQL Server and LocalStack in containers.
- Use Ardalis Clean Architecture as the dependency and project-structure guide.
- Avoid unnecessary third-party libraries.
- Publish `OrderPlaced` events to a LocalStack custom EventBridge bus.
- Route matching EventBridge events to SQS and prove that they arrived.
- Support multiple API replicas without publishing the same claimed row concurrently.
- Demonstrate retries, leases, poison-message handling, cleanup, and the unavoidable duplicate-delivery window.
- Provide automated verification and a detailed, copy-pasteable README.

## Non-goals

- An SQS consumer or fulfillment implementation.
- Exactly-once delivery.
- Strict global or per-order message ordering.
- A separately deployed outbox worker.
- Administrative replay or dead-letter APIs.
- Dashboards, distributed tracing, or an operational UI.
- EventBridge target DLQ or archive/replay configuration.
- Production secret management or production AWS deployment automation.

These are documented as possible extensions rather than implemented in the initial sample.

## Project structure

Use the full Ardalis project shape with lean dependencies:

```text
transactional-outbox/dotnet10-eventbridge/
  src/
    TransactionalOutbox.Core/
    TransactionalOutbox.UseCases/
    TransactionalOutbox.Infrastructure/
    TransactionalOutbox.Web/
  tests/
    TransactionalOutbox.UnitTests/
    TransactionalOutbox.IntegrationTests/
```

### Core

Contains the `Order` aggregate, domain types, and `OrderPlaced` domain event. It depends only on the BCL and exposes a minimal mechanism for collecting domain events.

### UseCases

Contains the `PlaceOrder` request and handler plus the abstractions required to persist a unit of work. It depends on Core and has no EF Core, AWS, or mediator dependency.

### Infrastructure

Contains:

- EF Core `DbContext`, entity configurations, and migrations;
- domain-event-to-outbox `SaveChangesInterceptor`;
- SQL Server outbox claiming and state transitions;
- EventBridge publisher adapter;
- scoped outbox processor;
- background-service implementation and dependency-registration extensions.

It depends on Core, UseCases, EF Core SQL Server, and the official AWS EventBridge SDK.

### Web

Contains HTTP endpoints, transport DTOs, configuration binding, and the application composition root. It hosts the outbox `BackgroundService` but delegates processing to scoped Infrastructure services.

### Tests

Unit tests cover deterministic business and outbox policy. Integration tests exercise real SQL Server and LocalStack instances supplied by the Compose environment.

The design adopts Ardalis dependency direction and responsibilities, not the template's optional FastEndpoints, Mediator, Specification, Result, or GuardClauses packages.

## Runtime flow

```text
POST /orders
  -> PlaceOrder use case
  -> Order aggregate records OrderPlaced
  -> SaveChanges interceptor creates OutboxMessage
  -> one SQL Server transaction commits Order + OutboxMessage
  -> BackgroundService leases pending messages
  -> AWS SDK PutEvents to LocalStack EventBridge
  -> EventBridge rule routes OrderPlaced to SQS
  -> README/test receives the SQS message as proof of delivery
```

The order endpoint calls `SaveChangesAsync` once. EF Core's relational transaction commits the order and outbox row together. No network publication occurs in that transaction.

## Domain and API

The example domain operation is placing an order.

`Order` stores:

- client-generated order ID;
- customer ID;
- total amount and currency;
- status;
- creation time.

The public HTTP surface is intentionally small:

- `POST /orders` validates and places an order, returning `201 Created`;
- `GET /orders/{id}` returns committed order state or `404 Not Found`.

Validation errors return `400 Bad Request`. No outbox administration endpoints are included.

## Event contract

The persisted and published integration event uses a stable logical name rather than a CLR type name.

EventBridge fields:

- `Source`: `sample.orders`;
- `DetailType`: `OrderPlaced`;
- `EventBusName`: configured;
- `Detail`: JSON containing event ID, correlation ID, schema version, occurrence time, order ID, customer ID, total amount, and currency.

The event ID is generated before persistence and remains stable across retries. It is the identity downstream consumers must use for deduplication. Serialization uses `System.Text.Json`; no assembly-qualified names or unsafe polymorphic deserialization are stored.

## Outbox persistence model

`OutboxMessages` contains:

- `Id`: stable event/outbox GUID;
- `EventType`: stable logical event name;
- `SchemaVersion`;
- `Payload`;
- `OccurredOnUtc`;
- `AttemptCount`;
- `NextAttemptOnUtc`;
- `LockedBy` and `LockedUntilUtc`;
- `ProcessedOnUtc` and `EventBridgeEventId`;
- `LastError`;
- `DeadLetteredOnUtc`;
- SQL Server `rowversion`.

The `SaveChangesInterceptor` copies pending domain events into tracked outbox entities before EF sends changes to the database. The same `DbContext`, connection, and transaction persist both order and outbox row. Only asynchronous saves are supported by the application path; the synchronous interceptor path must not silently bypass event capture.

Client-generated IDs make EF connection-retry ambiguity safer. Domain events are cleared only after they have been copied to tracked outbox entities, and retry behavior is covered by tests.

## Multi-instance claim algorithm

Each polling cycle creates a DI scope and resolves a scoped processor and `DbContext`. The processor atomically selects, leases, and returns a bounded batch using a parameterized SQL Server `UPDATE ... OUTPUT` statement over a CTE with:

- `UPDLOCK` to take update locks;
- `READPAST` to skip rows claimed by another transaction;
- `ROWLOCK` to favor row-level locks;
- eligibility filters for processing, dead-letter, retry, and lease state;
- deterministic requested selection order by occurrence time and ID.

The claim sets a unique worker ID, lease expiration, and increments the attempt count. All eligibility and lease comparisons use `SYSUTCDATETIME()` to avoid application-host clock skew.

The short claim transaction commits before any EventBridge call. Publishing while SQL locks are held is prohibited. `READPAST` and `ROWLOCK` reduce normal queue contention but do not promise zero blocking or globally ordered delivery.

Success and failure updates include the worker ID in their predicates. A publisher whose lease has expired cannot overwrite state written by a newer owner. An abandoned lease becomes claimable after expiry.

## Publication and delivery semantics

The processor publishes one claimed outbox message per `PutEvents` request in the initial implementation. This favors clarity over throughput.

A message is marked processed only when:

- `FailedEntryCount` is zero;
- the corresponding result entry exists;
- it contains no error code;
- it contains a nonempty EventBridge event ID.

An HTTP-successful SDK call alone is not sufficient. Null or malformed result data is treated as failure. The configured event bus is validated for the sample environment because AWS documents a nonexistent-bus case in which `PutEvents` may still return HTTP 200 without delivering the event.

The atomic guarantee ends when SQL Server commits the order and outbox row. Delivery is at least once. If the process crashes after EventBridge accepts an event but before SQL Server records success, the lease expires and the message is published again. Consumers must enforce idempotency, normally with a unique constraint over consumer name and event ID.

`ProcessedOnUtc` proves that EventBridge accepted the entry; receiving the matching SQS message separately proves that the configured rule delivered it to this target.

## Retry, poison-message, and cleanup behavior

- AWS SDK retry behavior handles a small number of immediate transport/service failures.
- Durable message-level retries are recorded in SQL Server across polling cycles and process restarts.
- Retryable failures clear the lease and set `NextAttemptOnUtc` using bounded exponential backoff plus jitter.
- Permanent EventBridge entry errors are dead-lettered immediately.
- Retryable failures are dead-lettered when the configured attempt limit is exhausted.
- Stored error summaries are length-bounded.
- Host shutdown cancellation is propagated and is not recorded as a message failure.
- Dead-letter rows are retained for diagnosis.
- Periodic cleanup deletes only successfully processed rows older than the configured retention interval.

Polly is permitted but will not be added initially. Layering it over AWS SDK retries and the durable database schedule would obscure timing and risk multiplicative retries.

## Background service behavior

The hosted service is a thin singleton. It creates an asynchronous DI scope per polling cycle because `DbContext` and the processor are scoped.

It processes bounded batches, immediately looks for more work while batches are nonempty, and waits with cancellation plus small jitter when idle. Expected cycle-level infrastructure failures are logged and delayed rather than terminating the host. Cancellation is always propagated promptly through database and AWS calls.

## LocalStack and Docker Compose

A single idempotent LocalStack `ready.d` script creates:

1. a custom EventBridge event bus;
2. a standard SQS queue;
3. an SQS resource policy granting the matching EventBridge rule permission to send;
4. an enabled rule matching `source = sample.orders` and `detail-type = OrderPlaced`;
5. the SQS target.

Initialization uses stable names and fails visibly when a command fails. Verification checks LocalStack's initialization state.

The application has no LocalStack-specific code path. Configuration supplies the service URL, authentication region, credentials, event bus, source, and detail type. Host execution uses `localhost`; container execution uses the LocalStack service name. A real AWS deployment can use the normal SDK configuration chain.

Compose behavior:

- unprofiled services run SQL Server and LocalStack for local `dotnet run` development;
- the `full` profile additionally builds and runs the Web API;
- SQL Server data uses a named volume and a readiness health check;
- LocalStack exposes its readiness/initialization state;
- EF migrations remain explicit and are never applied automatically at API startup.

A pristine environment therefore requires a migration step before the API is usable, including before the first complete-stack run. The README will not inaccurately describe this first run as a single command.

The README calls out that Microsoft's SQL Server Linux container is supported on x86-64 Linux and not officially supported under ARM emulation. Local SA credentials are disposable development values and are not presented as production practice.

## Dependencies

Production dependencies are limited to:

- ASP.NET Core shared framework;
- latest compatible `Microsoft.EntityFrameworkCore.SqlServer` 10.0.x;
- matching EF Core design-time tooling;
- official `AWSSDK.EventBridge` package.

Built-in DI, `BackgroundService`, logging, options, and `System.Text.Json` replace MediatR, Quartz, Scrutor, Newtonsoft.Json, and equivalent helper packages.

Tests use xUnit and ASP.NET Core test hosting. These dependencies remain confined to test projects. Integration tests use the already-running Compose services rather than adding a container-orchestration library.

## Verification strategy

Automated tests and README exercises verify:

1. Placing an order persists exactly one order and one outbox row.
2. A forced database failure commits neither record.
3. Simultaneous claimers receive disjoint outbox IDs.
4. An abandoned lease becomes claimable after expiry.
5. A stale lease owner cannot record success or failure.
6. EventBridge success marks the row processed and the event appears in SQS.
7. A partial or per-entry `PutEvents` failure does not mark the row processed.
8. Retryable failures schedule a later attempt.
9. Permanent and exhausted failures become dead-lettered.
10. A simulated crash after publication but before the success update produces a duplicate after lease expiry, documenting the at-least-once boundary.
11. Cleanup removes only sufficiently old processed rows.
12. Host shutdown cancels polling and in-flight work promptly.

## README requirements

The README must include:

- the dual-write problem and transactional-outbox guarantee;
- an architecture and message-flow explanation;
- dependency choices and why excluded libraries are unnecessary;
- prerequisites and the SQL Server ARM-emulation caveat;
- infrastructure-only and full-profile Compose commands;
- the explicit migration command;
- commands to place and retrieve an order;
- SQL queries for inspecting the order and outbox state;
- an AWS CLI command to receive the EventBridge-routed SQS message;
- steps to cause publication failure, observe durable retries, and recover;
- a duplicate-delivery exercise;
- the at-least-once and consumer-idempotency contract;
- operational limitations and future extensions.

## Future extensions

- Move the hosted processor into a separate Worker executable without changing Core or UseCases.
- Batch `PutEvents` entries while preserving response-index mapping.
- Add an idempotent SQS consumer/inbox example.
- Add EventBridge target DLQs and archive/replay.
- Add metrics, traces, dashboards, and alerting.
- Add authenticated administrative dead-letter inspection and replay.
- Validate behavior against real AWS rather than treating LocalStack as proof of service parity.

## Acceptance criteria

The sample is complete when a developer can follow the README to start SQL Server and LocalStack, apply EF migrations, run the API, place an order, observe its outbox lifecycle, and receive the matching message from SQS. Automated tests must cover the atomicity, concurrency, retry, lease, failure, cleanup, and duplicate-delivery behaviors listed above. No existing Gatherly files are modified.
