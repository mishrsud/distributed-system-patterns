# .NET 10 Transactional Outbox Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a runnable .NET 10 sample that atomically persists orders and outbox messages in SQL Server, safely publishes them from multiple API replicas to LocalStack EventBridge, and proves routing to SQS.

**Architecture:** Follow the lean Ardalis Clean Architecture shape: Core owns the domain, UseCases coordinates order placement, Infrastructure owns EF Core/SQL Server/AWS/background processing, and Web is the HTTP host and composition root. EF Core writes the order and outbox row in one transaction; a leased SQL Server work queue publishes outside that transaction with explicit at-least-once semantics.

**Tech Stack:** .NET SDK 10.0.102, ASP.NET Core 10, EF Core SQL Server 10.0.12, AWS SDK for .NET EventBridge 4.0.100.11, SQL Server 2025 Developer container, LocalStack, Docker Compose, xUnit 2.9.3.

---

## File map

All paths and commands below are relative to `transactional-outbox/dotnet10-eventbridge/` unless stated otherwise.

```text
TransactionalOutbox.slnx                         Solution definition
global.json                                      Pins .NET SDK 10.0.102
Directory.Build.props                            Shared compiler/build settings
Directory.Packages.props                         Central package versions
.config/dotnet-tools.json                        Pins the EF Core CLI locally
compose.yaml                                     SQL Server, LocalStack, optional Web
.env.example                                     Disposable local settings
localstack/ready.d/10-create-resources.sh        Event bus, rule, SQS, policy, target

src/TransactionalOutbox.Core/
  Common/IDomainEvent.cs                         Domain-event identity contract
  Common/Entity.cs                               Domain-event collection behavior
  Orders/Order.cs                                Order aggregate
  Orders/OrderPlacedDomainEvent.cs               Domain event raised by Order.Place

src/TransactionalOutbox.UseCases/
  Abstractions/IOrderRepository.cs               Persistence port
  Abstractions/IUnitOfWork.cs                    Atomic save port
  Orders/PlaceOrder/PlaceOrderCommand.cs         Use-case input
  Orders/PlaceOrder/PlaceOrderResult.cs           Use-case output
  Orders/PlaceOrder/PlaceOrderHandler.cs          Application orchestration

src/TransactionalOutbox.Infrastructure/
  DependencyInjection.cs                         Infrastructure composition
  Persistence/AppDbContext.cs                    EF unit of work
  Persistence/Configurations/OrderConfiguration.cs
  Persistence/Configurations/OutboxMessageConfiguration.cs
  Persistence/Interceptors/ConvertDomainEventsToOutboxInterceptor.cs
  Persistence/Migrations/*                       Generated schema
  Persistence/OrderRepository.cs                 Repository adapter
  Outbox/OutboxMessage.cs                        Durable publisher state
  Outbox/ClaimedOutboxMessage.cs                 Immutable claimed work
  Outbox/IOutboxStore.cs                         Processor-facing store contract
  Outbox/SqlServerOutboxStore.cs                 Atomic lease claim/state updates
  Outbox/OutboxOptions.cs                        Batch, lease, retry, retention options
  Outbox/RetrySchedule.cs                         Backoff plus jitter calculation
  Outbox/EventBridgeOptions.cs                    AWS/EventBridge settings
  Outbox/IEventPublisher.cs                      Broker port
  Outbox/PublishResult.cs                        Success/retry/dead-letter result
  Outbox/EventBridgePublisher.cs                 PutEvents adapter
  Outbox/EventBridgeStartupValidator.cs          Fails fast when the bus is absent
  Outbox/OutboxProcessor.cs                      One scoped processing cycle
  Outbox/OutboxPublisherService.cs               Hosted polling loop
  Outbox/OutboxCleanupService.cs                 Processed-row retention loop

src/TransactionalOutbox.Web/
  Program.cs                                      Composition and endpoints
  Contracts/PlaceOrderRequest.cs                 HTTP request
  Contracts/OrderResponse.cs                     HTTP response
  appsettings.json                                Safe defaults
  appsettings.Development.json                    Host-run local endpoints
  Dockerfile                                     Full-profile image

tests/TransactionalOutbox.UnitTests/
  Core/OrderTests.cs
  UseCases/PlaceOrderHandlerTests.cs
  Outbox/RetryScheduleTests.cs
  Outbox/EventBridgePublisherTests.cs
  Outbox/OutboxPublisherServiceTests.cs

tests/TransactionalOutbox.IntegrationTests/
  InfrastructureFixture.cs                       Shared real-infrastructure setup
  Persistence/AtomicOrderOutboxTests.cs
  Outbox/ConcurrentClaimTests.cs
  Outbox/LeaseAndStateTransitionTests.cs
  Outbox/EventBridgeToSqsTests.cs
  Outbox/CleanupTests.cs

README.md                                         Tutorial and runbook
```

## Task 1: Scaffold the lean Clean Architecture solution

**Files:**
- Create: `transactional-outbox/dotnet10-eventbridge/TransactionalOutbox.slnx`
- Create: `transactional-outbox/dotnet10-eventbridge/global.json`
- Create: `transactional-outbox/dotnet10-eventbridge/Directory.Build.props`
- Create: `transactional-outbox/dotnet10-eventbridge/Directory.Packages.props`
- Create: all six `.csproj` files listed in the file map

- [ ] **Step 1: Generate projects without restoring**

Run from `transactional-outbox/dotnet10-eventbridge`:

```bash
dotnet new sln --name TransactionalOutbox --format slnx
dotnet new classlib --name TransactionalOutbox.Core --output src/TransactionalOutbox.Core --framework net10.0 --no-restore
dotnet new classlib --name TransactionalOutbox.UseCases --output src/TransactionalOutbox.UseCases --framework net10.0 --no-restore
dotnet new classlib --name TransactionalOutbox.Infrastructure --output src/TransactionalOutbox.Infrastructure --framework net10.0 --no-restore
dotnet new web --name TransactionalOutbox.Web --output src/TransactionalOutbox.Web --framework net10.0 --no-restore
dotnet new xunit --name TransactionalOutbox.UnitTests --output tests/TransactionalOutbox.UnitTests --framework net10.0 --no-restore
dotnet new xunit --name TransactionalOutbox.IntegrationTests --output tests/TransactionalOutbox.IntegrationTests --framework net10.0 --no-restore
```

Expected: six projects and `TransactionalOutbox.slnx` are created; no package restore runs.

- [ ] **Step 2: Add projects and dependency-direction references**

```bash
dotnet sln add src/TransactionalOutbox.Core/TransactionalOutbox.Core.csproj src/TransactionalOutbox.UseCases/TransactionalOutbox.UseCases.csproj src/TransactionalOutbox.Infrastructure/TransactionalOutbox.Infrastructure.csproj src/TransactionalOutbox.Web/TransactionalOutbox.Web.csproj tests/TransactionalOutbox.UnitTests/TransactionalOutbox.UnitTests.csproj tests/TransactionalOutbox.IntegrationTests/TransactionalOutbox.IntegrationTests.csproj
dotnet add src/TransactionalOutbox.UseCases/TransactionalOutbox.UseCases.csproj reference src/TransactionalOutbox.Core/TransactionalOutbox.Core.csproj
dotnet add src/TransactionalOutbox.Infrastructure/TransactionalOutbox.Infrastructure.csproj reference src/TransactionalOutbox.Core/TransactionalOutbox.Core.csproj src/TransactionalOutbox.UseCases/TransactionalOutbox.UseCases.csproj
dotnet add src/TransactionalOutbox.Web/TransactionalOutbox.Web.csproj reference src/TransactionalOutbox.UseCases/TransactionalOutbox.UseCases.csproj src/TransactionalOutbox.Infrastructure/TransactionalOutbox.Infrastructure.csproj
dotnet add tests/TransactionalOutbox.UnitTests/TransactionalOutbox.UnitTests.csproj reference src/TransactionalOutbox.Core/TransactionalOutbox.Core.csproj src/TransactionalOutbox.UseCases/TransactionalOutbox.UseCases.csproj src/TransactionalOutbox.Infrastructure/TransactionalOutbox.Infrastructure.csproj
dotnet add tests/TransactionalOutbox.IntegrationTests/TransactionalOutbox.IntegrationTests.csproj reference src/TransactionalOutbox.Infrastructure/TransactionalOutbox.Infrastructure.csproj src/TransactionalOutbox.Web/TransactionalOutbox.Web.csproj
```

Expected: Core has no project reference; every other reference points inward except Web's composition-root reference to Infrastructure.

- [ ] **Step 3: Pin SDK, compiler settings, and packages**

Create `global.json`:

```json
{
  "sdk": {
    "version": "10.0.102",
    "rollForward": "latestPatch"
  }
}
```

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
```

Create `Directory.Packages.props`:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="AWSSDK.EventBridge" Version="4.0.100.11" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.12" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageVersion Include="xunit" Version="2.9.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
  </ItemGroup>
</Project>
```

Add EF Core SQL Server, EF Design, and AWS EventBridge references only to Infrastructure. Add `Microsoft.AspNetCore.Mvc.Testing` only to IntegrationTests. Keep test SDK/xUnit packages private to test projects.

Pin the EF CLI as a repository-local tool:

```bash
dotnet new tool-manifest
dotnet tool install dotnet-ef --version 10.0.12
```

Expected: `.config/dotnet-tools.json` records `dotnet-ef` 10.0.12; no global tool state is changed.

- [ ] **Step 4: Restore and verify the empty structure**

Run:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Expected: restore, build, and both generated tests succeed with zero warnings.

- [ ] **Step 5: Commit the scaffold if Git is available**

```bash
git add .
git commit -m "chore: scaffold transactional outbox sample"
```

If the workspace still has no `.git`, record “commit skipped: workspace is not a Git repository” in the execution notes and continue without initializing a repository.

## Task 2: Build the Order aggregate test-first

**Files:**
- Create: `src/TransactionalOutbox.Core/Common/IDomainEvent.cs`
- Create: `src/TransactionalOutbox.Core/Common/Entity.cs`
- Create: `src/TransactionalOutbox.Core/Orders/Order.cs`
- Create: `src/TransactionalOutbox.Core/Orders/OrderPlacedDomainEvent.cs`
- Create: `tests/TransactionalOutbox.UnitTests/Core/OrderTests.cs`
- Delete: generated `Class1.cs` and generated `UnitTest1.cs`

- [ ] **Step 1: Write the failing aggregate test**

```csharp
[Fact]
public void Place_creates_order_and_one_stable_domain_event()
{
    var orderId = Guid.NewGuid();
    var customerId = Guid.NewGuid();
    var now = new DateTimeOffset(2026, 9, 20, 0, 0, 0, TimeSpan.Zero);

    Order order = Order.Place(orderId, customerId, 125.50m, "AUD", now);

    Assert.Equal(OrderStatus.Placed, order.Status);
    OrderPlacedDomainEvent domainEvent = Assert.IsType<OrderPlacedDomainEvent>(Assert.Single(order.DomainEvents));
    Assert.Equal(orderId, domainEvent.OrderId);
    Assert.NotEqual(Guid.Empty, domainEvent.Id);
    Assert.Equal(now, domainEvent.OccurredOnUtc);
}
```

- [ ] **Step 2: Run the test and confirm the red state**

Run:

```bash
dotnet test tests/TransactionalOutbox.UnitTests --filter FullyQualifiedName~OrderTests
```

Expected: FAIL to compile because `Order` and `OrderPlacedDomainEvent` do not exist.

- [ ] **Step 3: Implement the minimal domain model**

Use this public shape:

```csharp
public interface IDomainEvent
{
    Guid Id { get; }
    DateTimeOffset OccurredOnUtc { get; }
}

public abstract class Entity
{
    private readonly List<IDomainEvent> _domainEvents = [];
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();
    protected void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);
    public IReadOnlyList<IDomainEvent> DequeueDomainEvents()
    {
        IDomainEvent[] events = [.. _domainEvents];
        _domainEvents.Clear();
        return events;
    }
}

public sealed record OrderPlacedDomainEvent(
    Guid Id,
    DateTimeOffset OccurredOnUtc,
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    string Currency) : IDomainEvent;
```

`Order.Place` must reject empty IDs, non-positive totals, and currencies other than three uppercase ASCII characters. It creates exactly one event with a new GUID and the supplied UTC timestamp. Include a private parameterless constructor for EF, private setters, and an `OrderStatus` enum with `Placed`.

- [ ] **Step 4: Add negative tests and make them pass**

Add theories for empty order/customer IDs, zero/negative totals, and invalid currency values. Run:

```bash
dotnet test tests/TransactionalOutbox.UnitTests --filter FullyQualifiedName~OrderTests
```

Expected: all `OrderTests` pass.

- [ ] **Step 5: Commit**

```bash
git add src/TransactionalOutbox.Core tests/TransactionalOutbox.UnitTests/Core
git commit -m "feat: add order aggregate and domain event"
```

Skip only when no Git repository exists.

## Task 3: Implement the PlaceOrder use case test-first

**Files:**
- Create: `src/TransactionalOutbox.UseCases/Abstractions/IOrderRepository.cs`
- Create: `src/TransactionalOutbox.UseCases/Abstractions/IUnitOfWork.cs`
- Create: `src/TransactionalOutbox.UseCases/Orders/PlaceOrder/PlaceOrderCommand.cs`
- Create: `src/TransactionalOutbox.UseCases/Orders/PlaceOrder/PlaceOrderResult.cs`
- Create: `src/TransactionalOutbox.UseCases/Orders/PlaceOrder/PlaceOrderHandler.cs`
- Create: `tests/TransactionalOutbox.UnitTests/UseCases/PlaceOrderHandlerTests.cs`

- [ ] **Step 1: Write a failing orchestration test with handwritten fakes**

```csharp
[Fact]
public async Task Handle_adds_order_then_commits_once()
{
    var repository = new RecordingOrderRepository();
    var unitOfWork = new RecordingUnitOfWork();
    var handler = new PlaceOrderHandler(repository, unitOfWork, TimeProvider.System);
    var command = new PlaceOrderCommand(Guid.NewGuid(), 42.25m, "AUD");

    PlaceOrderResult result = await handler.HandleAsync(command, CancellationToken.None);

    Assert.NotEqual(Guid.Empty, result.OrderId);
    Assert.Equal(result.OrderId, Assert.Single(repository.Added).Id);
    Assert.Equal(1, unitOfWork.SaveCalls);
}
```

The fakes implement only the two port interfaces; do not introduce a mocking library.

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test tests/TransactionalOutbox.UnitTests --filter FullyQualifiedName~PlaceOrderHandlerTests
```

Expected: FAIL to compile because the handler and ports do not exist.

- [ ] **Step 3: Implement the use-case contracts**

```csharp
public interface IOrderRepository
{
    void Add(Order order);
    Task<Order?> GetByIdAsync(Guid id, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed record PlaceOrderCommand(Guid CustomerId, decimal TotalAmount, string Currency);
public sealed record PlaceOrderResult(Guid OrderId);
```

`PlaceOrderHandler.HandleAsync` generates the order ID, reads UTC from the injected `TimeProvider`, calls `Order.Place`, adds it, saves once, and returns the ID. It performs no broker operation.

- [ ] **Step 4: Run the focused and full unit suites**

```bash
dotnet test tests/TransactionalOutbox.UnitTests --filter FullyQualifiedName~PlaceOrderHandlerTests
dotnet test tests/TransactionalOutbox.UnitTests
```

Expected: PASS with no warnings.

- [ ] **Step 5: Commit**

```bash
git add src/TransactionalOutbox.UseCases tests/TransactionalOutbox.UnitTests/UseCases
git commit -m "feat: add place order use case"
```

## Task 4: Persist Order and OutboxMessage atomically

**Files:**
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/AppDbContext.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/OrderRepository.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/Configurations/OrderConfiguration.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/OutboxMessage.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/Configurations/OutboxMessageConfiguration.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/Interceptors/ConvertDomainEventsToOutboxInterceptor.cs`
- Create: `tests/TransactionalOutbox.IntegrationTests/InfrastructureFixture.cs`
- Create: `tests/TransactionalOutbox.IntegrationTests/Persistence/AtomicOrderOutboxTests.cs`

- [ ] **Step 1: Write the failing real-SQL atomicity test**

```csharp
[Fact]
public async Task SaveChanges_persists_order_and_matching_outbox_row()
{
    await using AppDbContext db = _fixture.CreateDbContext();
    Order order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 10m, "AUD", DateTimeOffset.UtcNow);
    db.Orders.Add(order);

    await db.SaveChangesAsync(CancellationToken.None);

    OutboxMessage message = await db.OutboxMessages.SingleAsync(CancellationToken.None);
    Assert.Equal(order.Id, JsonDocument.Parse(message.Payload).RootElement.GetProperty("orderId").GetGuid());
}
```

Add a second test that installs a throwing `SaveChangesInterceptor` after conversion and asserts a failed save leaves both table counts at zero. Add a third test whose interceptor fails only the first save attempt; retry `SaveChangesAsync` on the same context and assert exactly one order and one outbox row are committed. This proves dequeuing the domain event cannot duplicate or lose its already-tracked outbox entity on a caller retry.

- [ ] **Step 2: Run and confirm failure against the Compose SQL connection string**

```bash
ConnectionStrings__SqlServer='Server=localhost,1433;Database=TransactionalOutboxTests;User Id=sa;Password=Local_dev_Only_123!;TrustServerCertificate=True' dotnet test tests/TransactionalOutbox.IntegrationTests --filter FullyQualifiedName~AtomicOrderOutboxTests
```

Expected: FAIL because persistence types/schema do not exist.

- [ ] **Step 3: Implement EF entities and mappings**

`AppDbContext` implements `IUnitOfWork`, exposes `DbSet<Order> Orders` and `DbSet<OutboxMessage> OutboxMessages`, and applies configurations from its assembly.

Use this outbox state shape:

```csharp
public sealed class OutboxMessage
{
    public Guid Id { get; init; }
    public required string EventType { get; init; }
    public int SchemaVersion { get; init; }
    public required string Payload { get; init; }
    public DateTimeOffset OccurredOnUtc { get; init; }
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptOnUtc { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public DateTimeOffset? ProcessedOnUtc { get; set; }
    public string? EventBridgeEventId { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadLetteredOnUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
```

Map money to `decimal(18,2)`, all timestamps to `datetimeoffset`, payload/error lengths to `nvarchar(max)`/`nvarchar(2048)`, and `RowVersion` with `IsRowVersion()`. Add a filtered eligibility index covering processed/dead-letter/retry/lease/order columns.

- [ ] **Step 4: Implement deterministic event conversion**

In `SavingChangesAsync`, enumerate tracked `Entity` instances, dequeue their events, and switch explicitly on `OrderPlacedDomainEvent`. Serialize this stable contract using web JSON defaults:

```csharp
new
{
    eventId = domainEvent.Id,
    correlationId = domainEvent.OrderId,
    schemaVersion = 1,
    occurredOnUtc = domainEvent.OccurredOnUtc,
    orderId = domainEvent.OrderId,
    customerId = domainEvent.CustomerId,
    totalAmount = domainEvent.TotalAmount,
    currency = domainEvent.Currency
}
```

Create the outbox row with `EventType = "orders.order-placed"`, `SchemaVersion = 1`, and `NextAttemptOnUtc = OccurredOnUtc`. Throw `NotSupportedException` for an unmapped event. Override the synchronous interception path to throw `InvalidOperationException("Use SaveChangesAsync so domain events are captured.")`.

- [ ] **Step 5: Run the tests**

```bash
dotnet test tests/TransactionalOutbox.IntegrationTests --filter FullyQualifiedName~AtomicOrderOutboxTests
dotnet test tests/TransactionalOutbox.UnitTests
```

Expected: atomicity tests and all unit tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/TransactionalOutbox.Infrastructure tests/TransactionalOutbox.IntegrationTests
git commit -m "feat: persist orders and outbox messages atomically"
```

## Task 5: Add multi-instance-safe leasing and state transitions

**Files:**
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/ClaimedOutboxMessage.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/IOutboxStore.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/SqlServerOutboxStore.cs`
- Create: `tests/TransactionalOutbox.IntegrationTests/Outbox/ConcurrentClaimTests.cs`
- Create: `tests/TransactionalOutbox.IntegrationTests/Outbox/LeaseAndStateTransitionTests.cs`

- [ ] **Step 1: Write failing concurrency and lease tests**

Seed 20 due rows, start two stores behind a `Barrier`, claim ten rows per worker, and assert:

```csharp
Assert.Equal(20, first.Select(x => x.Id).Concat(second.Select(x => x.Id)).Distinct().Count());
Assert.Empty(first.Select(x => x.Id).Intersect(second.Select(x => x.Id)));
```

Add tests proving an unexpired lease cannot be claimed, an expired lease can be claimed, and `MarkProcessedAsync` returns `false` for a stale `workerId`.

- [ ] **Step 2: Run and confirm failure**

```bash
dotnet test tests/TransactionalOutbox.IntegrationTests --filter 'FullyQualifiedName~ConcurrentClaimTests|FullyQualifiedName~LeaseAndStateTransitionTests'
```

Expected: FAIL because the store does not exist.

- [ ] **Step 3: Define the store contract**

```csharp
public interface IOutboxStore
{
    Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimAsync(string workerId, int batchSize, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> MarkProcessedAsync(Guid id, string workerId, string eventBridgeEventId, CancellationToken cancellationToken);
    Task<bool> ScheduleRetryAsync(Guid id, string workerId, DateTimeOffset nextAttemptOnUtc, string error, CancellationToken cancellationToken);
    Task<bool> DeadLetterAsync(Guid id, string workerId, string error, CancellationToken cancellationToken);
    Task<int> DeleteProcessedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);
}
```

`ClaimedOutboxMessage` contains the immutable fields needed to publish plus `AttemptCount`.

- [ ] **Step 4: Implement the atomic SQL Server claim**

Execute this parameterized statement using `DbConnection`/`DbCommand` from `AppDbContext` inside a short EF transaction:

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
OUTPUT INSERTED.Id, INSERTED.EventType, INSERTED.SchemaVersion,
       INSERTED.Payload, INSERTED.OccurredOnUtc, INSERTED.AttemptCount;
```

Read every returned row before committing. Roll back and discard rows if command execution or commit fails.

Implement success/retry/dead-letter updates as parameterized `ExecuteSqlInterpolatedAsync` statements whose predicates include `Id`, `LockedBy`, `ProcessedOnUtc IS NULL`, and `DeadLetteredOnUtc IS NULL`. Clear lease fields on each terminal/scheduled transition. Truncate stored errors to 2048 characters.

- [ ] **Step 5: Run the SQL concurrency suite repeatedly**

```bash
for run in 1 2 3 4 5; do dotnet test tests/TransactionalOutbox.IntegrationTests --no-restore --filter 'FullyQualifiedName~ConcurrentClaimTests|FullyQualifiedName~LeaseAndStateTransitionTests' || exit 1; done
```

Expected: five clean passes with disjoint claims and no flaky stale-owner update.

- [ ] **Step 6: Commit**

```bash
git add src/TransactionalOutbox.Infrastructure/Outbox tests/TransactionalOutbox.IntegrationTests/Outbox
git commit -m "feat: add leased SQL Server outbox claims"
```

## Task 6: Implement deterministic retry policy and EventBridge result handling

**Files:**
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/OutboxOptions.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/RetrySchedule.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/EventBridgeOptions.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/IEventPublisher.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/PublishResult.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/EventBridgePublisher.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/EventBridgeStartupValidator.cs`
- Create: `tests/TransactionalOutbox.UnitTests/Outbox/RetryScheduleTests.cs`
- Create: `tests/TransactionalOutbox.UnitTests/Outbox/EventBridgePublisherTests.cs`

- [ ] **Step 1: Write failing retry-schedule tests**

Inject a `Random` source or `Func<double>` so tests can pin jitter. Verify attempts 1, 2, and 3 produce bounded delays based on 2, 4, and 8 seconds, and that the maximum never exceeds the configured cap.

```csharp
TimeSpan delay = schedule.GetDelay(attemptCount: 3, jitterSample: 0.5);
Assert.InRange(delay, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10));
```

- [ ] **Step 2: Implement options and retry calculation**

```csharp
public sealed class OutboxOptions
{
    public int BatchSize { get; init; } = 20;
    public int MaxAttempts { get; init; } = 10;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ProcessedRetention { get; init; } = TimeSpan.FromDays(7);
}
```

Use `min(2^attemptCount seconds, MaxRetryDelay)` plus up to 25% positive jitter. Clamp the exponent before conversion so large attempt counts cannot overflow.

- [ ] **Step 3: Write failing publisher tests with a fake `IAmazonEventBridge`**

Cover:

- success only when one response entry has a nonempty `EventId` and no error;
- `InternalFailure` and `ThrottlingException` return retryable failure;
- `AccessDeniedException`, `InvalidArgument`, and `MalformedDetail` return permanent failure;
- null/missing entries and thrown service/transport exceptions return retryable failure;
- request fields and JSON detail match the stable contract.

```csharp
PublishResult result = await publisher.PublishAsync(message, cancellationToken);
Assert.Equal(PublishOutcome.RetryableFailure, result.Outcome);
Assert.Equal("ThrottlingException", result.ErrorCode);
```

- [ ] **Step 4: Implement EventBridgePublisher**

Build exactly one `PutEventsRequestEntry` using configured bus/source/detail type and the stored payload. Call `PutEventsAsync(request, cancellationToken)`. Treat a response as success only when `FailedEntryCount == 0`, the single result exists, its `ErrorCode` is empty, and its `EventId` is nonempty. Return a discriminated `PublishResult` rather than throwing expected per-message outcomes.

Do not add Polly. Configure bounded AWS SDK retries through `AmazonEventBridgeConfig` during dependency registration.

- [ ] **Step 5: Implement and test startup bus validation**

Add a validator test using a fake `IAmazonEventBridge`: `DescribeEventBusAsync` success permits startup, while not-found or transport failure throws with the configured bus name. Implement `EventBridgeStartupValidator` as an `IHostedService` that calls `DescribeEventBusAsync` before returning from `StartAsync`; register it only when outbox processing is enabled.

- [ ] **Step 6: Run unit tests**

Run:

```bash
dotnet test tests/TransactionalOutbox.UnitTests --filter 'FullyQualifiedName~RetryScheduleTests|FullyQualifiedName~EventBridgePublisherTests'
```

Expected: PASS for every response/error classification.

- [ ] **Step 7: Commit**

```bash
git add src/TransactionalOutbox.Infrastructure/Outbox tests/TransactionalOutbox.UnitTests/Outbox
git commit -m "feat: publish outbox events to EventBridge"
```

## Task 7: Orchestrate processing, retries, dead-lettering, and cleanup

**Files:**
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/OutboxProcessor.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/OutboxPublisherService.cs`
- Create: `src/TransactionalOutbox.Infrastructure/Outbox/OutboxCleanupService.cs`
- Create: `tests/TransactionalOutbox.UnitTests/Outbox/OutboxProcessorTests.cs`
- Create: `tests/TransactionalOutbox.UnitTests/Outbox/OutboxPublisherServiceTests.cs`
- Create: `tests/TransactionalOutbox.IntegrationTests/Outbox/CleanupTests.cs`

- [ ] **Step 1: Write failing processor state-transition tests**

Use handwritten fake store/publisher objects. Verify:

```csharp
[Theory]
[InlineData(PublishOutcome.Success, ExpectedTransition.Processed)]
[InlineData(PublishOutcome.RetryableFailure, ExpectedTransition.RetryScheduled)]
[InlineData(PublishOutcome.PermanentFailure, ExpectedTransition.DeadLettered)]
public async Task ProcessBatch_transitions_each_claimed_message(
    PublishOutcome outcome,
    ExpectedTransition expectedTransition)
```

Also verify a retryable failure at `MaxAttempts` dead-letters, cancellation escapes without calling a failure transition, and a stale-owner `false` result is logged but not retried inline.

- [ ] **Step 2: Implement one scoped processing cycle**

`OutboxProcessor.ProcessBatchAsync(workerId, cancellationToken)` claims one batch and returns the number claimed. For each message:

```csharp
PublishResult result = await publisher.PublishAsync(message, cancellationToken);

if (result.Outcome == PublishOutcome.Success)
    await store.MarkProcessedAsync(message.Id, workerId, result.EventBridgeEventId!, cancellationToken);
else if (result.Outcome == PublishOutcome.PermanentFailure || message.AttemptCount >= options.MaxAttempts)
    await store.DeadLetterAsync(message.Id, workerId, result.ErrorSummary, cancellationToken);
else
    await store.ScheduleRetryAsync(message.Id, workerId, timeProvider.GetUtcNow() + retrySchedule.GetDelay(message.AttemptCount, Random.Shared.NextDouble()), result.ErrorSummary, cancellationToken);
```

Catch non-cancellation exceptions around an individual publish and schedule them as retryable failures. Never catch `OperationCanceledException` when the supplied token is cancelled.

- [ ] **Step 3: Implement the hosted polling loop**

The singleton `OutboxPublisherService` creates `workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}"`. Each iteration creates an `AsyncServiceScope`, resolves `OutboxProcessor`, processes batches immediately while the previous batch was nonempty, and uses `Task.Delay(options.IdleDelay + jitter, stoppingToken)` when empty or after a cycle-level failure.

Before implementation, add `OutboxPublisherServiceTests` with a processor that blocks on its cancellation token. Start the hosted service, stop it, and assert the token is observed and `StopAsync` completes within two seconds. This prevents shutdown cancellation from being swallowed.

- [ ] **Step 4: Implement and test cleanup**

`OutboxCleanupService` runs once per configured interval, creates a scope, computes `TimeProvider.GetUtcNow() - ProcessedRetention`, and calls `DeleteProcessedBeforeAsync`. The integration test seeds old/new processed, pending, and dead-letter rows and asserts only old processed rows are deleted.

- [ ] **Step 5: Run focused and full tests**

```bash
dotnet test tests/TransactionalOutbox.UnitTests --filter FullyQualifiedName~OutboxProcessorTests
dotnet test tests/TransactionalOutbox.IntegrationTests --filter FullyQualifiedName~CleanupTests
dotnet test
```

Expected: all tests pass; cancellation tests complete promptly.

- [ ] **Step 6: Commit**

```bash
git add src/TransactionalOutbox.Infrastructure/Outbox tests
git commit -m "feat: process retry and clean outbox messages"
```

## Task 8: Add composition and the HTTP API

**Files:**
- Create: `src/TransactionalOutbox.Infrastructure/DependencyInjection.cs`
- Modify: `src/TransactionalOutbox.Web/Program.cs`
- Create: `src/TransactionalOutbox.Web/Contracts/PlaceOrderRequest.cs`
- Create: `src/TransactionalOutbox.Web/Contracts/OrderResponse.cs`
- Modify: `src/TransactionalOutbox.Web/appsettings.json`
- Create: `src/TransactionalOutbox.Web/appsettings.Development.json`
- Create: `tests/TransactionalOutbox.IntegrationTests/Web/OrderEndpointsTests.cs`

- [ ] **Step 1: Write failing endpoint tests**

Use `WebApplicationFactory<Program>` with configuration overridden to the integration database and background publication disabled for endpoint isolation. Verify valid POST returns `201` and a subsequent GET returns the order; invalid customer ID/amount/currency returns `400` and creates no rows.

```csharp
HttpResponseMessage response = await client.PostAsJsonAsync("/orders", new
{
    customerId = Guid.NewGuid(),
    totalAmount = 29.95m,
    currency = "AUD"
});
Assert.Equal(HttpStatusCode.Created, response.StatusCode);
```

- [ ] **Step 2: Register Infrastructure explicitly**

`AddInfrastructure(configuration)` must:

- validate `ConnectionStrings:SqlServer`, `Outbox`, and `EventBridge` options on start, and register `EventBridgeStartupValidator` to prove the configured bus exists before background publication starts;
- register the interceptor and `AppDbContext` with `UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure())`;
- register repository/unit-of-work/store/publisher/processor services with scoped lifetimes;
- configure `AmazonEventBridgeConfig.ServiceURL` and `AuthenticationRegion` for LocalStack without setting `RegionEndpoint`;
- register the publisher and cleanup hosted services only when `Outbox:Enabled` is true.

- [ ] **Step 3: Implement endpoints**

Use minimal APIs:

```csharp
app.MapPost("/orders", async (PlaceOrderRequest request, PlaceOrderHandler handler, CancellationToken ct) =>
{
    PlaceOrderResult result = await handler.HandleAsync(
        new PlaceOrderCommand(request.CustomerId, request.TotalAmount, request.Currency), ct);
    return Results.Created($"/orders/{result.OrderId}", new { result.OrderId });
});

app.MapGet("/orders/{id:guid}", async (Guid id, IOrderRepository repository, CancellationToken ct) =>
{
    Order? order = await repository.GetByIdAsync(id, ct);
    return order is null ? Results.NotFound() : Results.Ok(OrderResponse.From(order));
});
```

Map `ArgumentException`/`ArgumentOutOfRangeException` to RFC 7807 `400` responses through a small exception handler. Add `/health/live`; do not claim dependency readiness there.

- [ ] **Step 4: Add safe configuration defaults**

Use an empty production connection string and EventBridge endpoint; Development supplies localhost defaults. Include options for batch size 20, max attempts 10, 30-second lease, 2-second idle delay, 5-minute retry cap, and 7-day processed retention. Never put real AWS credentials in source; use dummy `test` values only in the local Compose environment.

- [ ] **Step 5: Run API tests and manual smoke start**

```bash
dotnet test tests/TransactionalOutbox.IntegrationTests --filter FullyQualifiedName~OrderEndpointsTests
dotnet run --project src/TransactionalOutbox.Web --no-build
```

Expected: endpoint tests pass; app starts and `/health/live` returns 200 after the database has been migrated.

- [ ] **Step 6: Commit**

```bash
git add src tests/TransactionalOutbox.IntegrationTests/Web
git commit -m "feat: expose order placement API"
```

## Task 9: Add LocalStack initialization and Docker Compose

**Files:**
- Create: `localstack/ready.d/10-create-resources.sh`
- Create: `compose.yaml`
- Create: `.env.example`
- Create: `src/TransactionalOutbox.Web/Dockerfile`

- [ ] **Step 1: Write the idempotent LocalStack ready hook**

The executable shell script uses `awslocal` and stable values:

```bash
#!/usr/bin/env bash
set -euo pipefail

region="ap-southeast-2"
account_id="000000000000"
bus_name="orders"
queue_name="order-placed"
rule_name="route-order-placed"

awslocal events create-event-bus --name "$bus_name" --region "$region"
queue_url="$(awslocal sqs create-queue --queue-name "$queue_name" --region "$region" --query QueueUrl --output text)"
queue_arn="arn:aws:sqs:${region}:${account_id}:${queue_name}"
rule_arn="arn:aws:events:${region}:${account_id}:rule/${bus_name}/${rule_name}"

awslocal events put-rule --event-bus-name "$bus_name" --name "$rule_name" --region "$region" \
  --event-pattern '{"source":["sample.orders"],"detail-type":["OrderPlaced"]}'

policy="$(printf '{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Principal":{"Service":"events.amazonaws.com"},"Action":"sqs:SendMessage","Resource":"%s","Condition":{"ArnEquals":{"aws:SourceArn":"%s"}}}]}' "$queue_arn" "$rule_arn")"
awslocal sqs set-queue-attributes --queue-url "$queue_url" --attributes "Policy=$policy" --region "$region"
awslocal events put-targets --event-bus-name "$bus_name" --rule "$rule_name" --region "$region" \
  --targets "Id=order-placed-queue,Arn=$queue_arn"
```

Treat “already exists” as success through the create-or-update semantics of these APIs; a rerun must exit zero.

- [ ] **Step 2: Define Compose services**

`compose.yaml` must contain:

- `sqlserver`: `mcr.microsoft.com/mssql/server:2025-latest`, `ACCEPT_EULA=Y`, `MSSQL_PID=Developer`, password from `.env`, port 1433, named volume, and `sqlcmd` health check;
- `localstack`: `localstack/localstack:2026.08.3`, `SERVICES=events,sqs`, region, port 4566, ready-hook mount, and health check against `/_localstack/init/ready`;
- `web`: `profiles: [full]`, build from the Web Dockerfile, environment connection string/EventBridge URL using service DNS names, dependency health conditions, and port 8080.

Keep SQL Server and LocalStack unprofiled.

- [ ] **Step 3: Build and validate infrastructure**

```bash
cp .env.example .env
docker compose config --quiet
docker compose up -d
docker compose ps
curl --fail http://localhost:4566/_localstack/init/ready
```

Expected: SQL Server and LocalStack become healthy; LocalStack reports the ready hook successful.

- [ ] **Step 4: Prove ready-hook idempotency**

```bash
docker compose restart localstack
curl --retry 20 --retry-delay 2 --fail http://localhost:4566/_localstack/init/ready
```

Expected: restart succeeds and resources remain configured once with stable names.

- [ ] **Step 5: Build the full profile**

```bash
docker compose --profile full build
```

Expected: the .NET 10 Web image builds successfully. Do not start Web before the explicit migration task.

- [ ] **Step 6: Commit**

```bash
git add compose.yaml .env.example localstack src/TransactionalOutbox.Web/Dockerfile
git commit -m "chore: add SQL Server and LocalStack environment"
```

## Task 10: Create migrations and complete real-infrastructure tests

**Files:**
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/Migrations/*_InitialCreate.cs` via EF tooling
- Create: `src/TransactionalOutbox.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` via EF tooling
- Create: `tests/TransactionalOutbox.IntegrationTests/Outbox/EventBridgeToSqsTests.cs`
- Modify: test fixture and lease tests to include crash-window simulation

- [ ] **Step 1: Generate the initial migration explicitly**

```bash
dotnet tool restore
dotnet tool run dotnet-ef migrations add InitialCreate --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web --output-dir Persistence/Migrations
dotnet tool run dotnet-ef database update --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web
```

Expected: the locally pinned EF CLI creates `Orders`, `OutboxMessages`, rowversion, foreign-independent outbox data, and the eligibility index.

- [ ] **Step 2: Write the failing EventBridge-to-SQS integration test**

The test inserts an outbox row, runs one `OutboxProcessor` batch with the real AWS client pointed at LocalStack, then uses an AWS-compatible SQS HTTP/CLI helper to receive from `order-placed`. Assert the SQS body contains EventBridge `source = sample.orders`, `detail-type = OrderPlaced`, and the same stable event ID.

- [ ] **Step 3: Add the duplicate-delivery crash-window test**

Wrap the real publisher in a test decorator that lets EventBridge accept the message and then throws before `MarkProcessedAsync`. Advance/expire the lease in SQL, run another batch, receive two SQS messages, and assert both carry the same event ID. This test documents at-least-once delivery; do not “fix” the duplicate.

- [ ] **Step 4: Run all integration tests serially**

Disable parallel execution for the shared integration collection, recreate the test database at fixture startup, and purge SQS before each broker test.

```bash
dotnet test tests/TransactionalOutbox.IntegrationTests --no-restore --logger 'console;verbosity=normal'
```

Expected: all SQL concurrency, lease, endpoint, cleanup, EventBridge-to-SQS, and duplicate-window tests pass.

- [ ] **Step 5: Start the full profile after migration**

```bash
docker compose --profile full up -d --build
curl --fail http://localhost:8080/health/live
```

Expected: Web becomes healthy without running migrations automatically.

- [ ] **Step 6: Commit**

```bash
git add src/TransactionalOutbox.Infrastructure/Persistence/Migrations tests
git commit -m "test: verify transactional outbox end to end"
```

## Task 11: Write the detailed tutorial README

**Files:**
- Create: `transactional-outbox/dotnet10-eventbridge/README.md`

- [ ] **Step 1: Write the conceptual sections**

Explain the dual-write failure, order/outbox atomicity, lease/publish/ack flow, at-least-once crash window, consumer idempotency key, per-entry EventBridge result checking, and why no SQL locks are held during network calls. Link to the repository research note and primary sources rather than copying long passages.

- [ ] **Step 2: Write exact local-run commands**

Include this sequence with the final verified ports and paths:

```bash
cp .env.example .env
docker compose up -d
dotnet ef database update --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web
dotnet run --project src/TransactionalOutbox.Web
curl --request POST http://localhost:5080/orders --header 'Content-Type: application/json' --data '{"customerId":"11111111-1111-1111-1111-111111111111","totalAmount":42.50,"currency":"AUD"}'
docker compose exec localstack awslocal sqs receive-message --queue-url http://sqs.ap-southeast-2.localhost.localstack.cloud:4566/000000000000/order-placed --region ap-southeast-2
```

Verify and adjust the development HTTP port before committing the README.

- [ ] **Step 3: Add inspection and failure exercises**

Provide `sqlcmd` queries for `Orders` and `OutboxMessages`; commands to stop/restart LocalStack and observe `AttemptCount`, `NextAttemptOnUtc`, and recovery; and the exact integration-test filter that demonstrates duplicate publication. Clearly separate outbox dead-letter state from a future EventBridge target DLQ.

- [ ] **Step 4: Document deployment and platform caveats**

Document infrastructure-only versus `full` profile, the required explicit migration, dummy credentials, SQL Server's unsupported ARM-emulation status, LocalStack not proving AWS parity, and the future extensions listed in the design.

- [ ] **Step 5: Execute every README command from a clean state**

Use a disposable Compose project name so verification does not overwrite unrelated containers:

```bash
docker compose -p transactional-outbox-sample down -v
docker compose -p transactional-outbox-sample up -d
dotnet ef database update --project src/TransactionalOutbox.Infrastructure --startup-project src/TransactionalOutbox.Web
dotnet run --project src/TransactionalOutbox.Web
```

In a second terminal, execute the documented curl, SQL, SQS, failure, and recovery commands exactly. Correct any mismatch before proceeding.

- [ ] **Step 6: Commit**

```bash
git add README.md
git commit -m "docs: explain and operate transactional outbox sample"
```

## Task 12: Final verification against the specification

**Files:**
- Modify only files required by verification failures

- [ ] **Step 1: Verify dependency direction and package budget**

```bash
dotnet list src/TransactionalOutbox.Core package
dotnet list src/TransactionalOutbox.UseCases package
dotnet list src/TransactionalOutbox.Infrastructure package
dotnet list package --include-transitive
```

Expected: Core has no package dependency; UseCases has no infrastructure package; production-only explicit packages are EF Core SQL Server/Design and AWSSDK.EventBridge.

- [ ] **Step 2: Run formatting, build, and all tests**

```bash
dotnet format --verify-no-changes
dotnet build --no-restore
dotnet test --no-build --logger 'console;verbosity=normal'
```

Expected: every command exits 0 with no warnings.

- [ ] **Step 3: Re-run the concurrency stress test**

```bash
for run in 1 2 3 4 5 6 7 8 9 10; do dotnet test tests/TransactionalOutbox.IntegrationTests --no-build --filter FullyQualifiedName~ConcurrentClaimTests || exit 1; done
```

Expected: ten passes and no duplicate claimed IDs.

- [ ] **Step 4: Verify complete-stack behavior**

Apply the migration, start `docker compose --profile full up -d --build`, place one order, wait for its outbox row to become processed, and receive the matching SQS message. Confirm LocalStack initialization reports success and no Web container migration occurs at startup.

- [ ] **Step 5: Check repository scope**

```bash
find ../gatherly -type f -newer ../../docs/superpowers/specs/2026-09-20-transactional-outbox-design.md -print
```

Expected: no Gatherly file was modified by this work. Review the working tree manually as well if Git becomes available.

- [ ] **Step 6: Final commit if Git is available**

```bash
git add .
git commit -m "feat: add dotnet transactional outbox sample"
```

Skip if all changes are already committed or the workspace remains outside Git.
