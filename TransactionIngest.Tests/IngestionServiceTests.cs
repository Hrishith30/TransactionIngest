using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TransactionIngest.Data;
using TransactionIngest.Models;
using TransactionIngest.Services;

namespace TransactionIngest.Tests;

/// <summary>
/// Tests for <see cref="TransactionIngestionService"/>.
/// Each test runs against its own isolated SQLite database (GUID-named) so they
/// are independent and can run in parallel. Databases are cleaned up after each test.
/// </summary>
public sealed class IngestionServiceTests : IAsyncDisposable
{
    // ── Per-test state ────────────────────────────────────────────────────
    private readonly string _dbPath;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    // Fixed timestamp used in all test DTOs — prevents spurious TransactionTime diffs
    // between consecutive runs and keeps idempotency/update tests reliable.
    // Adjusted dynamically to stay within the 24-hour ingestion window without millisecond precision issues.
    private static readonly DateTime FixedTransactionTime =
        new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc).AddHours(-12);

    // Each test instance gets its own DB file — no shared state across tests.
    public IngestionServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tx_test_{Guid.NewGuid():N}.db");

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        _db     = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:WindowHours"] = "24"
            })
            .Build();
    }

    // Delete the test DB file after each test so temp files don't accumulate.
    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    // ── Helper factory ────────────────────────────────────────────────────

    // Wires up the service under test with the test DB and a given API stub.
    private TransactionIngestionService CreateService(ITransactionApiClient apiClient) =>
        new(_db, apiClient, _config, NullLogger<TransactionIngestionService>.Instance);

    // Builds a TransactionDto with sensible defaults. Use the configure delegate to
    // override specific fields per test without repeating the boilerplate.
    private static TransactionDto MakeDto(
        string transactionId,
        Action<TransactionDto>? configure = null)
    {
        var dto = new TransactionDto
        {
            TransactionId = transactionId,
            CardNumber    = "4111111111111111",
            LocationCode  = "STO-01",
            ProductName   = "Widget",
            Amount        = 10.00m,
            Timestamp     = FixedTransactionTime // Fixed so two runs see the same timestamp.
        };
        configure?.Invoke(dto);
        return dto;
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>New transactions should be inserted as Active with a single Insert audit row each.</summary>
    [Fact]
    public async Task NewTransactions_AreInserted_WithInsertAuditEntry()
    {
        // Arrange: snapshot contains two brand-new transactions.
        var snapshot = new List<TransactionDto>
        {
            MakeDto("T-1001"),
            MakeDto("T-1002")
        };
        var service = CreateService(new StubApiClient(snapshot));

        // Act
        var result = await service.ExecuteAsync();

        // Assert counts
        Assert.Equal(2, result.Fetched);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Revoked);

        // Assert DB records
        var transactions = await _db.Transactions.ToListAsync();
        Assert.Equal(2, transactions.Count);
        Assert.All(transactions, tx => Assert.Equal(TransactionStatus.Active, tx.Status));

        // Assert audit trail
        var auditRows = await _db.AuditLogs.ToListAsync();
        Assert.Equal(2, auditRows.Count);
        Assert.All(auditRows, row => Assert.Equal(ChangeType.Insert, row.ChangeType));
    }

    /// <summary>When a tracked field changes between runs, the record updates and one per-field audit row is written.</summary>
    [Fact]
    public async Task UpdatedField_IsDetected_AndAuditRowRecorded()
    {
        // Arrange: first run inserts 1001 with Amount = 10.00.
        var firstSnapshot = new List<TransactionDto> { MakeDto("T-1001") };
        var firstService  = CreateService(new StubApiClient(firstSnapshot));
        await firstService.ExecuteAsync();

        // Clear the audit log so we can count only the second run's entries.
        _db.AuditLogs.RemoveRange(_db.AuditLogs);
        await _db.SaveChangesAsync();

        // Second run: Amount changes to 99.99.
        var updatedDto = MakeDto("T-1001", dto => dto.Amount = 99.99m);
        var secondService = CreateService(new StubApiClient([updatedDto]));

        // Act
        var result = await secondService.ExecuteAsync();

        // Assert counts
        Assert.Equal(0, result.Inserted);
        Assert.Equal(1, result.Updated);

        // Assert DB record reflects new amount.
        var tx = await _db.Transactions.SingleAsync(t => t.TransactionId == 1001);
        Assert.Equal(99.99m, tx.Amount);

        // Assert exactly one Update audit row for the Amount field.
        var updateLogs = await _db.AuditLogs
            .Where(a => a.ChangeType == ChangeType.Update)
            .ToListAsync();

        Assert.Single(updateLogs);
        Assert.Equal("Amount",   updateLogs[0].FieldName);
        Assert.Equal("10.00",    updateLogs[0].OldValue);
        Assert.Equal("99.99",    updateLogs[0].NewValue);
    }

    /// <summary>An Active transaction within the window that disappears from the snapshot should be Revoked.</summary>
    [Fact]
    public async Task AbsentTransaction_WithinWindow_IsRevoked()
    {
        // Arrange: first run inserts 1001 and 1002.
        var firstSnapshot = new List<TransactionDto>
        {
            MakeDto("T-1001"),
            MakeDto("T-1002")
        };
        await CreateService(new StubApiClient(firstSnapshot)).ExecuteAsync();

        // Second run: snapshot contains only 1001 → 1002 should be revoked.
        var secondSnapshot = new List<TransactionDto> { MakeDto("T-1001") };

        // Act
        var result = await CreateService(new StubApiClient(secondSnapshot)).ExecuteAsync();

        // Assert
        Assert.Equal(1, result.Revoked);

        var revokedTx = await _db.Transactions.SingleAsync(t => t.TransactionId == 1002);
        Assert.Equal(TransactionStatus.Revoked, revokedTx.Status);

        var revokedLog = await _db.AuditLogs
            .Where(a => a.TransactionId == 1002 && a.ChangeType == ChangeType.Revoked)
            .ToListAsync();
        Assert.Single(revokedLog);
    }

    /// <summary>Running twice with the same snapshot should produce no additional audit rows.</summary>
    [Fact]
    public async Task IdempotentRun_WithSameSnapshot_ProducesNoNewAuditRows()
    {
        // Arrange: initial run inserts two transactions.
        var snapshot = new List<TransactionDto>
        {
            MakeDto("T-2001"),
            MakeDto("T-2002")
        };

        await CreateService(new StubApiClient(snapshot)).ExecuteAsync();

        // Count audit rows after first run.
        var auditCountAfterFirstRun = await _db.AuditLogs.CountAsync();

        // Act: second run with identical snapshot.
        var result = await CreateService(new StubApiClient(snapshot)).ExecuteAsync();

        // Assert: no new inserts, no updates.
        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);

        // Total audit rows should be the same as after the first run.
        var auditCountAfterSecondRun = await _db.AuditLogs.CountAsync();
        Assert.Equal(auditCountAfterFirstRun, auditCountAfterSecondRun);
    }

    /// <summary>Finalized records are sealed — a re-delivered snapshot with different values should not change them.</summary>
    [Fact]
    public async Task FinalizedTransaction_IsNotModified_OnSubsequentRun()
    {
        // Arrange: directly insert a record whose TransactionTime is outside the window.
        var oldTx = new Transaction
        {
            TransactionId   = 9999,
            CardNumberHash  = "aaaa",
            CardLast4       = "1111",
            LocationCode    = "STO-01",
            ProductName     = "Old Widget",
            Amount          = 5.00m,
            TransactionTime = DateTime.UtcNow.AddHours(-30), // Beyond the 24-hour window.
            Status          = TransactionStatus.Finalized,
            CreatedAt       = DateTime.UtcNow.AddHours(-30),
            UpdatedAt       = DateTime.UtcNow.AddHours(-30)
        };
        _db.Transactions.Add(oldTx);
        await _db.SaveChangesAsync();

        // Snapshot includes 9999 with a different Amount — should be ignored.
        var snapshot = new List<TransactionDto>
        {
            MakeDto("T-9999", dto =>
            {
                dto.Amount    = 999.99m; // Changed amount — should be rejected.
                dto.Timestamp = DateTime.UtcNow.AddHours(-30);
            })
        };

        // Act
        var result = await CreateService(new StubApiClient(snapshot)).ExecuteAsync();

        // Assert: no inserts, no updates for finalized record.
        Assert.Equal(0, result.Inserted);
        Assert.Equal(0, result.Updated);

        // Database record should remain unchanged.
        var dbTx = await _db.Transactions.SingleAsync(t => t.TransactionId == 9999);
        Assert.Equal(5.00m,                    dbTx.Amount);
        Assert.Equal(TransactionStatus.Finalized, dbTx.Status);
    }

    /// <summary>An Active record whose TransactionTime is past the 24-hour window should be automatically Finalized.</summary>
    [Fact]
    public async Task ActiveTransaction_OlderThan24Hours_IsFinalized()
    {
        // Arrange: insert an Active record with a TransactionTime beyond the window.
        var oldTx = new Transaction
        {
            TransactionId   = 8888,
            CardNumberHash  = "bbbb",
            CardLast4       = "2222",
            LocationCode    = "STO-02",
            ProductName     = "Stale Item",
            Amount          = 7.77m,
            TransactionTime = DateTime.UtcNow.AddHours(-25), // Just outside the window.
            Status          = TransactionStatus.Active,
            CreatedAt       = DateTime.UtcNow.AddHours(-25),
            UpdatedAt       = DateTime.UtcNow.AddHours(-25)
        };
        _db.Transactions.Add(oldTx);
        await _db.SaveChangesAsync();

        // The snapshot is empty — 8888 is not in it.
        var result = await CreateService(new StubApiClient([])).ExecuteAsync();

        // Assert
        Assert.Equal(1, result.Finalized);

        var dbTx = await _db.Transactions.SingleAsync(t => t.TransactionId == 8888);
        Assert.Equal(TransactionStatus.Finalized, dbTx.Status);

        var finalizedLog = await _db.AuditLogs
            .Where(a => a.TransactionId == 8888 && a.ChangeType == ChangeType.Finalized)
            .ToListAsync();
        Assert.Single(finalizedLog);
    }

    // Simple stub that returns a fixed list — keeps tests fast and deterministic.
    private sealed class StubApiClient : ITransactionApiClient
    {
        private readonly IReadOnlyList<TransactionDto> _data;

        public StubApiClient(IReadOnlyList<TransactionDto> data) => _data = data;

        public Task<IReadOnlyList<TransactionDto>> FetchLast24HoursAsync(CancellationToken ct = default)
            => Task.FromResult(_data);
    }
}
