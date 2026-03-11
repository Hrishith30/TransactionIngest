# Transaction Ingest

A .NET 10 console application that fetches a 24-hour snapshot of retail card transactions, upserts them by `TransactionId`, and maintains a full audit trail of every change.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

---

## Project Structure

```
Assignment/
├── TransactionIngest/              # Main console app
│   ├── Data/                       # EF Core DbContext
│   ├── Models/                     # Entities and enums
│   ├── Services/                   # Ingestion service and API clients
│   ├── appsettings.json            # Configuration
│   └── mock_feed.json              # Sample transaction data
├── TransactionIngest.Tests/        # xUnit test project
└── TransactionIngest.slnx          # Solution file
```

---

## Setup

Clone or download the repository, then restore dependencies:

```bash
dotnet restore
```

---

## Running the App

```bash
# From the solution root
dotnet run --project TransactionIngest

# Or from inside the project folder
cd TransactionIngest
dotnet run
```

On first run, the SQLite database (`transactions.db`) is created automatically — no migration step needed.

### Expected Output

```
┌─────────────────────────────────────────┐
│        Transaction Ingestion Run         │
├─────────────────────────────────────────┤
│  Fetched   :     5 transactions          │
│  Inserted  :     5                        │
│  Updated   :     0                        │
│  Revoked   :     0                        │
│  Finalized :     0                        │
└─────────────────────────────────────────┘
```

Run it again with the same data and all counts show `0` — that's idempotency working correctly.

---

## Running Tests

```bash
# From the solution root
dotnet test

# Or from the test project folder
cd TransactionIngest.Tests
dotnet test
```

**7 tests covering:**
- New transaction insertion
- Field-level update detection with old/new value capture
- Revocation of transactions absent from the snapshot
- Idempotency (same snapshot, no duplicate writes)
- Finalization of records older than 24 hours
- Immutability of finalized records

---

## Configuration (`appsettings.json`)

```json
{
  "ConnectionStrings": {
    "Default": "Data Source=transactions.db"
  },
  "Api": {
    "BaseUrl": "https://your-api.example.com",
    "SnapshotPath": "/api/transactions/snapshot"
  },
  "MockFeed": {
    "Enabled": true,
    "FilePath": "mock_feed.json"
  },
  "Ingestion": {
    "WindowHours": 24
  }
}
```

### Switching Between Mock and Live API

| Setting | Behaviour |
|---|---|
| `MockFeed:Enabled = true` | Reads from `mock_feed.json` (local development) |
| `MockFeed:Enabled = false` | Calls the real HTTP API at `Api:BaseUrl` + `Api:SnapshotPath` |

---

## Mock Feed (`mock_feed.json`)

The included feed has 5 sample transactions across two store locations. Edit it freely to test different scenarios:

- **Change an `amount`** → triggers an `Update` audit entry
- **Remove a transaction** → triggers a `Revoke` on the next run
- **Change a `timestamp` to 25+ hours ago** → triggers `Finalize`

---

## How It Works

Each run performs these steps inside a single database transaction (guaranteeing idempotency):

1. **Fetch** — load the 24-hour snapshot from the API client
2. **Upsert** — insert new records; compare existing ones field-by-field and write one audit row per changed field
3. **Revoke** — mark any `Active` record within the window that is missing from the snapshot
4. **Finalize** — seal any `Active` or `Revoked` record whose `TransactionTime` is older than the window

If the run fails at any point, the entire transaction rolls back and can be safely retried.

---

## Data Model

### Transactions table

| Column | Type | Notes |
|---|---|---|
| `TransactionId` | string | Upstream business key — upsert target |
| `CardNumberHash` | string | SHA-256 hash of the card number (PAN never stored) |
| `CardLast4` | string | Last 4 digits kept for display |
| `LocationCode` | string | POS location (max 20 chars) |
| `ProductName` | string | Product purchased (max 20 chars) |
| `Amount` | decimal(18,2) | Transaction amount |
| `TransactionTime` | DateTime (UTC) | When it occurred at the POS |
| `Status` | string | `Active`, `Revoked`, or `Finalized` |
| `CreatedAt` / `UpdatedAt` | DateTime (UTC) | Record housekeeping timestamps |

### AuditLogs table

One row per event. For `Update` events, one row per changed field.

| Column | Notes |
|---|---|
| `ChangeType` | `Insert`, `Update`, `Revoked`, or `Finalized` |
| `FieldName` | Which field changed (only for `Update`) |
| `OldValue` / `NewValue` | Before and after values (only for `Update`) |
| `ChangedAt` | UTC timestamp of the ingestion run |

---

## Assumptions

- `TransactionId` is treated as a string — the sample JSON uses values like `"T-1001"`, not integers.
- Card numbers are never stored. Only the SHA-256 hash (for change detection) and last 4 digits (for display) are persisted — in line with PCI-DSS recommendations.
- `EnsureCreated()` is used instead of EF migrations for simplicity. Changing the schema requires deleting `transactions.db` and letting it recreate on the next run.
- The 24-hour window is configurable via `Ingestion:WindowHours` in `appsettings.json`.
- The console app is designed to be invoked by an external scheduler (e.g. Windows Task Scheduler, cron). It runs once and exits.
