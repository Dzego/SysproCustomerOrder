# SYSPRO Legacy Customer Import & Order API

A .NET 8 backend application that imports customer data from a legacy fixed-width file into SQL Server and exposes REST API endpoints for creating and retrieving customer orders.

The project demonstrates:

- Fixed-width legacy data parsing
- Repeatable customer imports
- Row-level import error handling
- SQL Server persistence using Entity Framework Core
- REST API design using ASP.NET Core Controllers
- Order creation and retrieval
- Computed order totals
- Customer order aggregation over a date range
- Basic validation and error handling
- Automated testing with xUnit

---

## Technology Stack

- C#
- .NET 8
- ASP.NET Core Web API
- Entity Framework Core
- SQL Server 2022
- Docker
- xUnit

---

## Project Structure

```text
SysproCustomerOrder/
│
├── Syspro.Api/
│   ├── Controllers/
│   ├── Data/
│   ├── DTOs/
│   ├── LegacyData/
│   │   └── customers_legacy.dat
│   ├── Migrations/
│   ├── Models/
│   ├── Services/
│   └── Program.cs
│
├── Syspro.Tests/
│   └── Services/
│
├── docker-compose.yml
├── SysproCustomerOrder.slnx
└── README.md
```

---

# Running the Project Locally

## Prerequisites

Make sure the following are installed:

- .NET 8 SDK
- Docker Desktop
- Git

You can verify the .NET installation with:

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

The project uses SQL Server running in Docker.

Start the database container:

```bash
docker compose up -d
```

Confirm that SQL Server is running:

```bash
docker ps
```

---

## 3. Configure the Database Connection

The application expects a SQL Server connection string named:

```text
DefaultConnection
```

For local development, database credentials should be configured outside source control.

One option is .NET User Secrets.

From the API project:

```bash
cd Syspro.Api
dotnet user-secrets init
```

Then configure the connection string:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" \
"Server=localhost,1433;Database=SysproCustomerOrderDb;User Id=sa;Password=<YOUR_PASSWORD>;TrustServerCertificate=True;"
```

Replace `<YOUR_PASSWORD>` with the password configured for your local SQL Server Docker container.

---

## 4. Apply the Database Migrations

From the `Syspro.Api` directory:

```bash
dotnet ef database update
```

This creates the database and required tables using the included Entity Framework Core migrations.

The main tables are:

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

The terminal will display the local address of the API.

For example:

```text
http://localhost:5204
```

If a different port is displayed, use the URL shown in the terminal.

---

# Legacy Customer Import

The initial customer data is loaded from the supplied legacy fixed-width file.

The file is located at:

```text
Syspro.Api/LegacyData/customers_legacy.dat
```

Each line represents one customer.

## Fixed-Width Format

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

Spaces are significant because fields are extracted according to their fixed positions.

`LegacyCustomerId` is stored as a string so values such as:

```text
0000012345
```

retain their leading zeros.

---

## Run the Customer Import

### Endpoint

```http
POST /api/import/customers
```

No request body is required.

Example:

```text
POST http://localhost:5204/api/import/customers
```

### Example Successful Response

```json
{
  "processed": 7,
  "created": 2,
  "updated": 3,
  "failed": 2
}
```

The exact `created` and `updated` counts depend on the current state of the database.

---

## Import Behaviour

The import is designed to be repeatable.

Customers are matched using their `LegacyCustomerId`.

If a customer does not already exist, a new customer is created.

If the `LegacyCustomerId` already exists, the existing customer is updated rather than creating a duplicate.

This means running the same legacy file multiple times does not create duplicate customers.

---

## Invalid Legacy Rows

An invalid row does not stop the entire import.

The import continues processing the remaining rows and records information about the failed row.

Each import run is stored in `ImportLogs`, including:

```text
ProcessedCount
CreatedCount
UpdatedCount
FailedCount
StartedAt
CompletedAt
```

Individual failed rows are stored in `ImportErrors`, including:

```text
LineNumber
RawData
Reason
```

Examples of validation failures include:

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

---

# Order API

Orders can only be created for customers that already exist in the database.

A customer can be identified using either:

- Internal `CustomerId`
- `LegacyCustomerId`

An order must contain at least one item.

---

## Create an Order Using LegacyCustomerId

### Endpoint

```http
POST /api/orders
```

### Example Request

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

## Create an Order Using Internal CustomerId

The API also supports the internal database customer ID.

### Example Request

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

### Example Response — 201 Created

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

Order timestamps are generated by the backend using UTC.

---

# Order Total Calculation

Order totals are calculated by the backend rather than supplied by the client.

Each item's total is:

```text
LineTotal = UnitPrice × Quantity
```

The complete order total is:

```text
Order Total = Sum of all LineTotals
```

For example:

```text
External Hard Drive
R1450 × 1 = R1450

USB Hub
R350 × 2 = R700

Order Total = R2150
```

The calculated totals are returned in the API response.

---

# Retrieve an Order by Id

### Endpoint

```http
GET /api/orders/{id}
```

Example:

```text
GET http://localhost:5204/api/orders/1
```

### Example Response — 200 OK

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

The response contains both the individual item totals and the computed total for the complete order.

---

# Customer Order Totals

The API can return customers with their aggregate order totals over a specified date range.

### Endpoint

```http
GET /api/customers/totals?fromDate={fromDate}&toDate={toDate}
```

Example:

```text
GET http://localhost:5204/api/customers/totals?fromDate=2026-08-18&toDate=2026-08-19
```

Both dates are supplied as query parameters.

### Example Response

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

Orders belonging to the same customer are aggregated together.

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

The test suite covers key application behaviour, including:

- Parsing a valid fixed-width legacy customer row
- Handling invalid legacy customer data
- Order total calculation
- API behaviour for a key endpoint

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

An order belongs to one customer and can contain many order items.

An import log represents one execution of the legacy customer import and can contain multiple import errors.

---

# Design Decisions

## LegacyCustomerId

`LegacyCustomerId` is stored separately from the application's internal customer `Id`.

It is stored as a string because legacy identifiers may contain leading zeros.

It also has a unique database constraint to prevent duplicate legacy customers.

## Fixed-Width Parsing

Legacy records are parsed according to their character positions rather than using delimiters.

The parser is kept separate from persistence logic so parsing can be tested independently.

## Repeatable Imports

`LegacyCustomerId` is used to determine whether a customer should be created or updated.

This allows the same legacy file to be imported repeatedly without creating duplicate customers.

## Import Error Handling

A malformed legacy row does not stop the entire import.

The error is recorded and processing continues with the next row.

## Order Totals

Order totals are derived from the order items:

```text
UnitPrice × Quantity
```

Totals are calculated when producing API responses rather than accepting a total supplied by the client.

## Timestamps

Order timestamps are generated by the backend in UTC to provide consistent timezone-independent storage.

---

# Stopping the Local Environment

Stop the SQL Server container:

```bash
docker compose stop
```

To stop and remove the container:

```bash
docker compose down
```

The SQL Server data is persisted using the configured Docker volume.