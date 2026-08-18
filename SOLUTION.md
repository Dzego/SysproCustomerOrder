## Repository Pattern and Persistence Strategy

The Repository Pattern was one of the architectural approaches considered for
the persistence layer of this application.

It was applied to the legacy customer import workflow through focused repository
interfaces:

```text
ICustomerRepository
IImportRepository
```

with concrete implementations responsible for interacting with Entity Framework
Core and `AppDbContext`.

The import flow therefore follows:

```text
ImportController
        |
        v
CustomerImportService
        |
        +----------------------+
        |                      |
        v                      v
ICustomerRepository      IImportRepository
        |                      |
        v                      v
CustomerRepository       ImportRepository
        |                      |
        +----------+-----------+
                   |
                   v
              AppDbContext
                   |
                   v
               SQL Server
```

### Why Repository Pattern Was Chosen for the Import Workflow

The customer import contains more than simple CRUD operations. It coordinates
several responsibilities:

- Reading legacy records.
- Parsing fixed-width data.
- Finding customers by `LegacyCustomerId`.
- Deciding whether to create or update a customer.
- Recording import execution information.
- Recording row-level failures.
- Continuing the import when individual records are invalid.

Separating persistence behind `ICustomerRepository` and `IImportRepository`
allows `CustomerImportService` to concentrate on orchestrating this workflow
without also containing Entity Framework Core query details.

For example, the service expresses its requirement as:

```csharp
await _customerRepository
    .GetByLegacyCustomerIdAsync(record.LegacyCustomerId);
```

rather than implementing the underlying EF Core query itself.

This also creates a useful testing boundary. Repository interfaces can be
replaced by test doubles when testing the import workflow independently from
the database.

### Domain-Focused Repositories

A generic repository such as:

```text
IRepository<T>
```

was deliberately avoided.

Instead, repository interfaces describe operations that are meaningful to the
application.

For example:

```text
ICustomerRepository
    GetByLegacyCustomerIdAsync(...)
    AddAsync(...)
    SaveChangesAsync(...)

IImportRepository
    AddLogAsync(...)
    SaveChangesAsync(...)
```

This keeps the abstraction focused on actual application requirements rather
than creating a generic CRUD wrapper around Entity Framework Core.

---

## Why the Repository Pattern Was Not Applied to Orders

The Repository Pattern was not applied uniformly across every part of the
application.

The order functionality currently uses `AppDbContext` directly from
`OrderService`.

This was an intentional scope and complexity trade-off rather than an
architectural limitation.

The order requirements for this assessment are relatively focused:

- Create an order.
- Resolve an existing customer.
- Retrieve an order by ID.
- Calculate order totals.
- Aggregate customer order totals over a date range.

The persistence queries supporting these operations are currently small and
easy to understand.

Introducing:

```text
IOrderRepository
OrderRepository
```

solely to make every service follow the same structural pattern would add
another abstraction without currently removing significant complexity.

One of the design principles followed in this solution was to introduce
abstractions where they provide a clear benefit rather than applying patterns
mechanically.

The import workflow benefits from repositories because it coordinates parsing,
create/update decisions, import logging and failure handling.

For the current order workflow, direct `AppDbContext` usage keeps the
implementation concise while still maintaining separation between the HTTP
controllers and application logic.

### Time and Scope Consideration

The assignment has a deliberately constrained implementation timeframe, so
priority was given to complete, testable functionality over introducing
additional layers purely for consistency.

Given more development time, the decision would not automatically be to create
an `OrderRepository`. The order persistence logic would first be evaluated for
complexity.

If the order domain grew to include more complex queries, multiple persistence
operations, additional transactional requirements, or a need to isolate EF Core
from the application service, introducing focused abstractions such as:

```text
IOrderRepository
ICustomerOrderQueryRepository
```

would become reasonable.

This keeps the architecture evolutionary: abstractions are introduced in
response to demonstrated complexity rather than anticipated complexity.

---

## Repository Pattern Trade-offs

### Benefits in this solution

Using repositories for the import workflow provides:

- Separation between import orchestration and persistence.
- Focused persistence operations.
- Improved unit-test isolation.
- Reduced EF Core knowledge inside `CustomerImportService`.
- A clear boundary for customer and import persistence.
- Flexibility to evolve persistence independently of the import workflow.

### Costs

The pattern also introduces:

- Additional interfaces.
- Additional implementation classes.
- More dependency-injection registrations.
- Another layer developers need to navigate.

For the import workflow, these costs were justified by the separation gained.

For the current order workflow, they were not considered necessary.

The resulting design intentionally demonstrates both approaches and the
reasoning behind choosing an abstraction based on the complexity and needs of
each part of the application.