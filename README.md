# SYSPRO Legacy Customer Import & Order API

A .NET 8 backend application that imports customer data from a legacy fixed-width file into SQL Server and exposes REST API endpoints for creating, retrieving and aggregating customer orders.

The solution was developed as part of the SYSPRO Intermediate .NET Developer take-home assessment.

The project demonstrates:

- Fixed-width legacy data parsing
- Repeatable and idempotent-style customer imports
- Row-level import error handling
- Repository Pattern for the legacy import workflow
- SQL Server persistence using Entity Framework Core
- Entity Framework Core migrations
- REST API design using ASP.NET Core Controllers
- Order creation using either internal or legacy customer identifiers
- Order retrieval
- Computed order and line totals
- Customer order aggregation over a date range
- Basic validation and error handling
- Automated testing using xUnit

---

# Technology Stack

- C#
- .NET 8
- ASP.NET Core Web API
- Entity Framework Core
- SQL Server 2022
- Docker / Docker Compose
- xUnit
- EF Core InMemory provider for isolated service tests

---

# Project Structure

```text
SysproCustomerOrder/
│
├── Syspro.Api/
│   ├── Controllers/
│   │   ├── CustomersController.cs
│   │   ├── ImportController.cs
│   │   └── OrdersController.cs
│   │
│   ├── Data/
│   │   └── AppDbContext.cs
│   │
│   ├── DTOs/
│   │
│   ├── LegacyData/
│   │   └── customers_legacy.dat
│   │
│   ├── Migrations/
│   │
│   ├── Models/
│   │
│   ├── Repositories/
│   │   ├── ICustomerRepository.cs
│   │   ├── CustomerRepository.cs
│   │   ├── IImportRepository.cs
│   │   └── ImportRepository.cs
│   │
│   ├── Services/
│   │   ├── CustomerImportService.cs
│   │   ├── ICustomerImportService.cs
│   │   ├── LegacyCustomerParser.cs
│   │   ├── LegacyCustomerParseException.cs
│   │   ├── IOrderService.cs
│   │   └── OrderService.cs
│   │
│   └── Program.cs
│
├── Syspro.Tests/
│   ├── Services/
│   │   ├── LegacyCustomerParserTests.cs
│   │   └── OrderServiceTests.cs
│   │
│   └── Api/
│       └── OrdersApiTests.cs
│
├── docs/
│   ├── application-architecture.png
│   ├── legacy-import-flow.png
│   └── erd.png
│
├── docker-compose.yml
├── README.md
├── SOLUTION.md
└── SysproCustomerOrder.slnx
```

> `OrdersApiTests.cs` represents the lightweight API integration test required by the assessment. If this test has not yet been added, remove it from the structure until it exists.

---

# Architecture

The application uses a lightweight layered architecture.

```text
Client / Postman
        |
        v
ASP.NET Core Controllers
        |
        v
Application Services
        |
        +----------------------------+
        |                            |
        v                            v
Legacy Import                  Order Logic
        |                            |
        v                            |
Repository Interfaces                |
        |                            |
        v                            |
Repository Implementations           |
        |                            |
        +-------------+--------------+
                      |
                      v
                 AppDbContext
                      |
                      v
                  SQL Server
```

The Repository Pattern is used specifically for the legacy customer import workflow.

The order workflow currently uses `AppDbContext` directly from `OrderService`. This was an intentional scope and complexity decision rather than introducing repository abstractions purely for architectural symmetry.

The detailed architecture decisions and trade-offs are documented in `SOLUTION.md`.

---

# Running the Project Locally

## Prerequisites

Make sure the following are installed:

- .NET 8 SDK
- Docker Desktop
- Git

Verify the .NET installation:

```bash
dotnet --version
```

---

## 1. Clone the Repository

```bash
git clone https://github.com/Dzego/SysproCustomerOrder.git
cd SysproCustomerOrder
```

---

## 2. Start SQL Server

SQL Server runs locally using Docker Compose.

From the repository root:

```bash
docker compose up -d
```

Confirm that the container is running:

```bash
docker ps
```

---

## 3. Configure the Database Connection

The application expects a SQL Server connection string named:

```text
DefaultConnection
```

Database credentials should not be committed to source control.

For local development, .NET User Secrets can be used.

From the API project:

```bash
cd Syspro.Api
dotnet user-secrets init
```

Configure the connection string:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
"Server=localhost,1433;Database=SysproCustomerOrderDb;User Id=sa;Password=<YOUR_PASSWORD>;TrustServerCertificate=True;"
```

Replace `<YOUR_PASSWORD>` with the password configured for your local SQL Server Docker container.

---

## 4. Apply Database Migrations

From the `Syspro.Api` directory:

```bash
dotnet ef database update
```

This creates the database using the included Entity Framework Core migrations.

The main application tables are:

```text
Customers
Orders
OrderItems
ImportLogs
ImportErrors
```

---

## 5. Run the API

From the `Syspro.Api` directory:

```bash
dotnet run
```

The terminal will display the local URL.

For example:

```text
http://localhost:5204
```

If a different port is displayed, use the URL shown in the terminal.

---

# Legacy Customer Import

The initial customer data is loaded from the supplied fixed-width legacy file.

The file is located at:

```text
Syspro.Api/LegacyData/customers_legacy.dat
```

Each line represents one customer.

---

## Fixed-Width Format

The parser reads each field according to its fixed character positions.

```text
Positions 1-10   LegacyCustomerId
Positions 11-40  FullName
Positions 41-70  Email
Positions 71-78  SignupDate (yyyyMMdd)
Positions 79-80  Tier
```

Example:

```text
0000012345John Smith                    john.smith@example.com        20210315A 
0000012346Jane Doe                      jane.doe@example.com          20201228B 
0000012347Acme Corp                     ops@acmecorp.example          20190501C 
```

Spaces are significant because the parser extracts fields according to character positions.

`LegacyCustomerId` is stored as a string so identifiers such as:

```text
0000012345
```

retain their leading zeros.

---

# Run the Customer Import

## Endpoint

```http
POST /api/import/customers
```

No request body is required.

Example:

```text
POST http://localhost:5204/api/import/customers
```

## Example Successful Response

```json
{
  "processed": 7,
  "created": 2,
  "updated": 3,
  "failed": 2
}
```

The exact `created` and `updated` values depend on the current state of the database.

---

# Import Behaviour

The import is designed to be repeatable.

Customers are matched using `LegacyCustomerId`.

If the customer does not exist:

```text
Create Customer
```

If the customer already exists:

```text
Update Customer
```

This means running the same legacy file repeatedly does not create duplicate customers.

A unique database constraint on `LegacyCustomerId` provides an additional database-level safeguard against duplicates.

---

# Repository Pattern for Import Persistence

The legacy customer import uses focused repository abstractions:

```text
ICustomerRepository
IImportRepository
```

with concrete implementations:

```text
CustomerRepository
ImportRepository
```

The flow is:

```text
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

This keeps `CustomerImportService` focused on the import workflow rather than Entity Framework Core query details.

The service is responsible for:

- Parsing legacy records
- Determining whether customers should be created or updated
- Maintaining import counts
- Handling invalid legacy rows
- Coordinating the import workflow

The repositories are responsible for persistence operations.

A generic `IRepository<T>` abstraction was deliberately avoided. The repositories expose focused operations required by the application rather than wrapping every EF Core CRUD operation.

The Repository Pattern is currently used for the import workflow only.

`OrderService` uses `AppDbContext` directly because the current order persistence operations are relatively focused and straightforward.

Adding `IOrderRepository` and `OrderRepository` purely for structural consistency would introduce additional abstraction without currently removing meaningful complexity.

More detail about this trade-off is provided in `SOLUTION.md`.

---

# Invalid Legacy Rows

A malformed row does not stop the complete import.

Instead:

```text
Invalid row
    |
    v
Record ImportError
    |
    v
Increment FailedCount
    |
    v
Continue with next row
```

Every import execution is represented by an `ImportLog`.

The log records:

```text
ProcessedCount
CreatedCount
UpdatedCount
FailedCount
StartedAt
CompletedAt
```

Individual failed rows are stored in `ImportErrors`.

Each error records:

```text
LineNumber
RawData
Reason
```

Example parsing errors include:

```text
Invalid signup date: 20241340
```

and:

```text
Invalid tier: Z
```

The supported legacy tiers are:

```text
A
B
C
```

Parsing failures are represented by the custom:

```text
LegacyCustomerParseException
```

This allows the import workflow to distinguish expected legacy parsing failures from unrelated application failures.

---

# Order API

Orders can only be created for customers that already exist in the database.

A customer can be identified using either:

- Internal `CustomerId`
- `LegacyCustomerId`

An order must contain at least one item.

---

# Create an Order Using LegacyCustomerId

## Endpoint

```http
POST /api/orders
```

## Example Request

```json
{
  "legacyCustomerId": "0000012346",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-003",
      "description": "Monitor",
      "unitPrice": 2500.00,
      "quantity": 1
    },
    {
      "sku": "SKU-004",
      "description": "USB-C Cable",
      "unitPrice": 250.00,
      "quantity": 2
    }
  ]
}
```

---

# Create an Order Using Internal CustomerId

The same endpoint can identify a customer using the application's internal database ID.

## Example Request

```json
{
  "customerId": 1003,
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-020",
      "description": "External Hard Drive",
      "unitPrice": 1450.00,
      "quantity": 1
    },
    {
      "sku": "SKU-021",
      "description": "USB Hub",
      "unitPrice": 350.00,
      "quantity": 2
    }
  ]
}
```

## Example Response — 201 Created

```json
{
  "id": 1,
  "customerId": 1003,
  "orderDate": "2026-08-18T20:03:44.335706Z",
  "currency": "ZAR",
  "status": "Created",
  "total": 2150.00,
  "items": [
    {
      "id": 1,
      "sku": "SKU-020",
      "description": "External Hard Drive",
      "unitPrice": 1450.00,
      "quantity": 1,
      "lineTotal": 1450.00
    },
    {
      "id": 2,
      "sku": "SKU-021",
      "description": "USB Hub",
      "unitPrice": 350.00,
      "quantity": 2,
      "lineTotal": 700.00
    }
  ]
}
```

Order timestamps are generated by the backend in UTC.

---

# Order Total Calculation

Order totals are calculated by the backend rather than supplied by the client.

For each item:

```text
LineTotal = UnitPrice × Quantity
```

The complete order total is:

```text
OrderTotal = Sum of all LineTotals
```

For example:

```text
External Hard Drive
R1450 × 1 = R1450

USB Hub
R350 × 2 = R700

Order Total = R2150
```

This ensures the total returned by the API is derived from the actual order items.

---

# Retrieve an Order by Id

## Endpoint

```http
GET /api/orders/{id}
```

Example:

```text
GET http://localhost:5204/api/orders/1
```

## Example Response — 200 OK

```json
{
  "id": 1,
  "customerId": 1003,
  "orderDate": "2026-08-18T20:03:44.335706Z",
  "currency": "ZAR",
  "status": "Created",
  "total": 2150.00,
  "items": [
    {
      "id": 1,
      "sku": "SKU-020",
      "description": "External Hard Drive",
      "unitPrice": 1450.00,
      "quantity": 1,
      "lineTotal": 1450.00
    },
    {
      "id": 2,
      "sku": "SKU-021",
      "description": "USB Hub",
      "unitPrice": 350.00,
      "quantity": 2,
      "lineTotal": 700.00
    }
  ]
}
```

The response contains both individual line totals and the computed complete order total.

If the order does not exist, the API returns:

```text
404 Not Found
```

---

# Customer Order Totals

The API returns customers with their aggregate order totals over a specified date range.

## Endpoint

```http
GET /api/customers/totals?fromDate={fromDate}&toDate={toDate}
```

Example:

```text
GET http://localhost:5204/api/customers/totals?fromDate=2026-08-18&toDate=2026-08-19
```

Both dates are supplied as query parameters.

## Example Response

```json
[
  {
    "customerId": 2,
    "legacyCustomerId": "0000012346",
    "name": "Jane Doe",
    "total": 5050.00
  },
  {
    "customerId": 1003,
    "legacyCustomerId": "0000012349",
    "name": "Tech Solutions",
    "total": 2150.00
  },
  {
    "customerId": 1,
    "legacyCustomerId": "0000012345",
    "name": "John Smith",
    "total": 1250.00
  }
]
```

If a customer has multiple orders within the requested range, those orders are aggregated.

The results are ordered by total amount in descending order.

---

# Validation and Error Responses

The API performs basic validation for order creation and date-range queries.

Validation includes:

- Customer must exist.
- An order must contain at least one item.
- Item quantity must be greater than zero.
- Unit price cannot be negative.
- SKU is required.
- Currency must contain three characters.
- `fromDate` cannot be later than `toDate`.

---

## Customer Not Found

An order cannot be created for a customer that does not exist.

Example request:

```json
{
  "legacyCustomerId": "9999999999",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-001",
      "description": "Test Item",
      "unitPrice": 100.00,
      "quantity": 1
    }
  ]
}
```

Expected response:

```text
404 Not Found
```

---

## Order With No Items

Example:

```json
{
  "legacyCustomerId": "0000012345",
  "currency": "ZAR",
  "status": "Created",
  "items": []
}
```

Expected response:

```text
400 Bad Request
```

---

## Invalid Quantity

An item quantity must be greater than zero.

Example:

```json
{
  "legacyCustomerId": "0000012345",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-BAD",
      "description": "Invalid Item",
      "unitPrice": 100.00,
      "quantity": 0
    }
  ]
}
```

Expected response:

```text
400 Bad Request
```

---

## Invalid Unit Price

Unit prices cannot be negative.

Example:

```json
{
  "legacyCustomerId": "0000012345",
  "currency": "ZAR",
  "status": "Created",
  "items": [
    {
      "sku": "SKU-BAD",
      "description": "Invalid Item",
      "unitPrice": -100.00,
      "quantity": 1
    }
  ]
}
```

Expected response:

```text
400 Bad Request
```

---

## Order Not Found

Request:

```http
GET /api/orders/999999
```

Expected response:

```text
404 Not Found
```

---

## Invalid Date Range

Example:

```text
GET /api/customers/totals?fromDate=2026-12-31&toDate=2026-01-01
```

Expected response:

```text
400 Bad Request
```

---

# Automated Tests

The project uses xUnit for automated testing.

Run all tests from the repository root:

```bash
dotnet test
```

The test suite is organised by responsibility:

```text
Syspro.Tests/
├── Services/
│   ├── LegacyCustomerParserTests.cs
│   └── OrderServiceTests.cs
│
└── Api/
    └── OrdersApiTests.cs
```

---

## Legacy Customer Parser Tests

`LegacyCustomerParserTests` verifies the fixed-width parsing logic.

Coverage includes:

- Valid fixed-width legacy customer line
- Invalid signup date
- Invalid tier
- Invalid line length
- Empty input

Invalid parsing scenarios are expected to throw:

```text
LegacyCustomerParseException
```

---

## Order Service Tests

`OrderServiceTests` verifies order business behaviour.

Coverage includes:

- Correct total for an order containing multiple items
- Correct individual line totals
- Customer lookup using `LegacyCustomerId`
- Currency normalization
- Unknown customer handling
- Empty order items
- Invalid item quantities

The service tests use the EF Core InMemory provider.

Each test creates an isolated database using:

```csharp
.UseInMemoryDatabase(Guid.NewGuid().ToString())
```

This prevents tests from sharing database state and allows service tests to run without a local SQL Server instance.

---

## API Integration Test

The assessment requires at least one API pathway test.

The selected pathway is:

```text
Seed existing customer
        |
        v
POST /api/orders
        |
        v
201 Created
        |
        v
Read returned Order Id
        |
        v
GET /api/orders/{id}
        |
        v
200 OK
        |
        v
Verify items and computed totals
```

The lightweight integration test uses `WebApplicationFactory<Program>` to exercise the real ASP.NET Core HTTP pipeline while using an isolated test database.

This verifies more than calling a controller method directly because it exercises routing, dependency injection, JSON serialization and the controller/service integration together.

> Remove this subsection until the integration test has been implemented and is passing.

---

# Database Design

The main relationships are:

```text
Customer
   |
   | 1
   |
   | *
 Order
   |
   | 1
   |
   | *
OrderItem


ImportLog
   |
   | 1
   |
   | *
ImportError
```

A customer can have many orders.

An order belongs to one customer and contains one or more order items.

An import log represents one execution of the legacy customer import and can contain multiple import errors.

The complete ERD is documented in:

```text
docs/erd.png
```

---

# Design Decisions

## LegacyCustomerId

`LegacyCustomerId` is stored separately from the application's internal customer `Id`.

It is stored as a string because leading zeros are significant.

For example:

```text
Internal Id:       1003
LegacyCustomerId:  0000012349
```

A unique database constraint is used to prevent duplicate legacy customer identifiers.

---

## Fixed-Width Parsing

Legacy records are parsed according to their character positions rather than using delimiters.

Parsing is separated from persistence so that:

- The parser has a single responsibility.
- Parsing can be tested independently.
- The parser does not require database access.
- Import orchestration remains separate from file-format concerns.

---

## Repeatable Imports

`LegacyCustomerId` determines whether an imported customer should be created or updated.

This allows the same legacy file to be processed repeatedly without creating duplicate customers.

---

## Import Error Handling

A malformed legacy row does not terminate the complete import.

The error is recorded and processing continues with the next line.

This allows valid customers later in the file to still be imported.

---

## Repository Pattern

The Repository Pattern is used for the legacy customer import workflow.

`CustomerImportService` depends on:

```text
ICustomerRepository
IImportRepository
```

rather than querying `AppDbContext` directly.

The concrete repository implementations encapsulate EF Core persistence.

This provides a clear separation between:

```text
Import orchestration
        |
Repository abstraction
        |
Persistence implementation
```

The repository interfaces are domain-focused rather than using a generic `IRepository<T>` abstraction.

This keeps repository methods aligned with actual application requirements.

### Why repositories are not used for orders

The Repository Pattern was not applied mechanically across every service.

`OrderService` currently uses `AppDbContext` directly because the order persistence requirements are relatively small and straightforward.

Adding an `IOrderRepository` purely to make the architecture symmetrical would introduce another layer without currently removing significant complexity.

If the order domain grows to contain more complex persistence rules or queries, a focused order repository can be introduced later.

This trade-off is discussed in greater detail in `SOLUTION.md`.

---

## Order Totals

Order totals are derived from the order items:

```text
UnitPrice × Quantity
```

Totals are calculated by the backend rather than accepted from the client.

This avoids trusting client-supplied totals and prevents duplicated total state from becoming inconsistent with the underlying order items.

---

## UTC Timestamps

Order timestamps are generated by the backend using UTC.

```csharp
DateTime.UtcNow
```

UTC provides consistent timezone-independent persistence.

Conversion to a user's local timezone is considered a presentation concern.

---

## EF Core Migrations

Entity Framework Core migrations are used to create and evolve the database schema.

This was selected instead of maintaining a separate SQL creation script because the schema remains closely aligned with the entity configuration and can be reproduced locally using:

```bash
dotnet ef database update
```

---

# Architecture and Design Documentation

More detailed information about the architecture, persistence decisions, Repository Pattern trade-offs and testing strategy is available in:

```text
SOLUTION.md
```

Supporting diagrams are stored in:

```text
docs/
```

including:

```text
application-architecture.png
legacy-import-flow.png
erd.png
```

---

# Scope

The implementation intentionally focuses on the requirements of the assessment.

The solution avoids introducing unnecessary infrastructure such as:

```text
CQRS
MediatR
Generic repositories
Message queues
Caching
Authentication
Frontend application
Product catalogue
```

These patterns and technologies may be useful in a larger system, but introducing them here without a demonstrated requirement would increase complexity without improving the core migration and order workflows.

---

# Stopping the Local Environment

Stop SQL Server while keeping the container:

```bash
docker compose stop
```

To stop and remove the container:

```bash
docker compose down
```

The SQL Server data remains persisted using the configured Docker volume.