using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TransactionIngest.Data;
using TransactionIngest.Models;

namespace TransactionIngest.Services;

/// <summary>
/// Hourly ingestion pipeline:
///   1. Fetch the 24-hour snapshot.
///   2. Upsert records (insert new, update changed).
///   3. Revoke active records missing from the snapshot.
///   4. Finalize records older than the window.
/// Runs in a single transaction for safety.
/// </summary>
public sealed class TransactionIngestionService
{
    private readonly AppDbContext _db;
    private readonly ITransactionApiClient _apiClient;
    private readonly ILogger<TransactionIngestionService> _logger;
    private readonly int _windowHours;

    /// <summary>Counts from the ingestion run.</summary>
    public sealed record IngestionResult(int Fetched, int Inserted, int Updated, int Revoked, int Finalized);

    public TransactionIngestionService(
        AppDbContext db,
        ITransactionApiClient apiClient,
        IConfiguration configuration,
        ILogger<TransactionIngestionService> logger)
    {
        _db          = db;
        _apiClient   = apiClient;
        _logger      = logger;
        _windowHours = int.TryParse(configuration["Ingestion:WindowHours"], out var h) ? h : 24;
    }

    private static int ParseTransactionId(string id)
    {
        // Pull numeric ID from string (e.g., "T-1001" -> 1001)
        var digitsMatch = System.Text.RegularExpressions.Regex.Match(id, @"\d+");
        return digitsMatch.Success && int.TryParse(digitsMatch.Value, out var parsed) 
            ? parsed 
            : throw new FormatException($"Invalid TransactionId format: {id}");
    }

    /// <summary>Main entry point for the ingestion run.</summary>
    public async Task<IngestionResult> ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("=== Ingestion run started at {RunTime} UTC ===", DateTime.UtcNow);

        // Call the API (read-only, no DB lock yet)
        var snapshot = await _apiClient.FetchLast24HoursAsync(ct);
        _logger.LogInformation("Fetched {Count} transactions from the 24-hour snapshot.", snapshot.Count);

        // Quick lookup by ID for revocation checks later.
        var snapshotById = snapshot.ToDictionary(dto => ParseTransactionId(dto.TransactionId));

        var windowStart = DateTime.UtcNow.AddHours(-_windowHours);
        var runAt       = DateTime.UtcNow;

        int inserted  = 0;
        int updated   = 0;
        int revoked   = 0;
        int finalized = 0;

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Upsert phase.
            foreach (var dto in snapshot)
            {
                var parsedId = ParseTransactionId(dto.TransactionId);

                var existing = await _db.Transactions
                    .FirstOrDefaultAsync(t => t.TransactionId == parsedId, ct);

                if (existing is null)
                {
                    // New transaction — insert it.
                    var newTx = MapToEntity(dto, runAt, parsedId);
                    _db.Transactions.Add(newTx);
                    await _db.SaveChangesAsync(ct); // Get the ID for the audit log.

                    _db.AuditLogs.Add(BuildAuditEntry(newTx, ChangeType.Insert, runAt));
                    await _db.SaveChangesAsync(ct);

                    inserted++;
                    _logger.LogDebug("Inserted {TxId}.", dto.TransactionId);
                }
                else if (existing.Status == TransactionStatus.Finalized)
                {
                    // Already finalized, leave it alone.
                    _logger.LogDebug("Skipping finalized {TxId}.", dto.TransactionId);
                }
                else
                {
                    // Check for changes on active records.
                    var diffs = DetectChanges(existing, dto);
                    if (diffs.Count > 0)
                    {
                        ApplyChanges(existing, dto, runAt);
                        foreach (var (field, oldVal, newVal) in diffs)
                            _db.AuditLogs.Add(BuildUpdateEntry(existing, field, oldVal, newVal, runAt));

                        await _db.SaveChangesAsync(ct);
                        updated++;
                        _logger.LogDebug("Updated {TxId} — {Count} field(s) changed.", dto.TransactionId, diffs.Count);
                    }
                    else
                    {
                        _logger.LogDebug("{TxId} unchanged — nothing to do.", dto.TransactionId);
                    }
                }
            }

            // Revocation phase.
            var toRevoke = await _db.Transactions
                .Where(t => t.Status == TransactionStatus.Active && t.TransactionTime >= windowStart)
                .ToListAsync(ct);

            foreach (var tx in toRevoke.Where(tx => !snapshotById.ContainsKey(tx.TransactionId)))
            {
                tx.Status    = TransactionStatus.Revoked;
                tx.UpdatedAt = runAt;
                _db.AuditLogs.Add(BuildAuditEntry(tx, ChangeType.Revoked, runAt));
                revoked++;
                _logger.LogDebug("Revoked {TxId} — absent from snapshot.", tx.TransactionId);
            }
            await _db.SaveChangesAsync(ct);

            // Finalization phase.
            var toFinalize = await _db.Transactions
                .Where(t => (t.Status == TransactionStatus.Active || t.Status == TransactionStatus.Revoked)
                         && t.TransactionTime < windowStart)
                .ToListAsync(ct);

            foreach (var tx in toFinalize)
            {
                tx.Status    = TransactionStatus.Finalized;
                tx.UpdatedAt = runAt;
                _db.AuditLogs.Add(BuildAuditEntry(tx, ChangeType.Finalized, runAt));
                finalized++;
                _logger.LogDebug("Finalized {TxId} — older than {Hours}h window.", tx.TransactionId, _windowHours);
            }
            await _db.SaveChangesAsync(ct);

            await dbTransaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ingestion run failed — rolling back.");
            await dbTransaction.RollbackAsync(ct);
            throw;
        }

        var result = new IngestionResult(snapshot.Count, inserted, updated, revoked, finalized);
        _logger.LogInformation(
            "=== Run complete | Fetched: {F} | Inserted: {I} | Updated: {U} | Revoked: {R} | Finalized: {Z} ===",
            result.Fetched, result.Inserted, result.Updated, result.Revoked, result.Finalized);

        return result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Map DTO to entity and hash the card number.</summary>
    private static Transaction MapToEntity(TransactionDto dto, DateTime runAt, int parsedId) =>
        new()
        {
            TransactionId   = parsedId,
            CardNumberHash  = HashCard(dto.CardNumber),
            CardLast4       = dto.CardNumber.Length >= 4 ? dto.CardNumber[^4..] : dto.CardNumber,
            LocationCode    = dto.LocationCode,
            ProductName     = dto.ProductName,
            Amount          = dto.Amount,
            TransactionTime = dto.Timestamp.ToUniversalTime(),
            Status          = TransactionStatus.Active,
            CreatedAt       = runAt,
            UpdatedAt       = runAt
        };

    /// <summary>SHA-256 hash for card numbers.</summary>
    private static string HashCard(string cardNumber)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(cardNumber));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Identify which fields have changed.</summary>
    private static List<(string Field, string OldValue, string NewValue)> DetectChanges(
        Transaction existing, TransactionDto dto)
    {
        var diffs = new List<(string, string, string)>();

        var incomingHash = HashCard(dto.CardNumber);
        if (!string.Equals(existing.CardNumberHash, incomingHash, StringComparison.OrdinalIgnoreCase))
            diffs.Add(("CardNumber", $"****{existing.CardLast4}", $"****{dto.CardNumber[^4..]}"));

        if (!string.Equals(existing.LocationCode, dto.LocationCode, StringComparison.Ordinal))
            diffs.Add(("LocationCode", existing.LocationCode, dto.LocationCode));

        if (!string.Equals(existing.ProductName, dto.ProductName, StringComparison.Ordinal))
            diffs.Add(("ProductName", existing.ProductName, dto.ProductName));

        if (existing.Amount != dto.Amount)
            diffs.Add(("Amount", existing.Amount.ToString("F2"), dto.Amount.ToString("F2")));

        if (existing.TransactionTime != dto.Timestamp.ToUniversalTime())
            diffs.Add(("TransactionTime",
                existing.TransactionTime.ToString("O"),
                dto.Timestamp.ToUniversalTime().ToString("O")));

        return diffs;
    }

    /// <summary>Apply new values and reactivate if needed.</summary>
    private static void ApplyChanges(Transaction existing, TransactionDto dto, DateTime runAt)
    {
        existing.CardNumberHash  = HashCard(dto.CardNumber);
        existing.CardLast4       = dto.CardNumber.Length >= 4 ? dto.CardNumber[^4..] : dto.CardNumber;
        existing.LocationCode    = dto.LocationCode;
        existing.ProductName     = dto.ProductName;
        existing.Amount          = dto.Amount;
        existing.TransactionTime = dto.Timestamp.ToUniversalTime();
        existing.Status          = TransactionStatus.Active; // Reactivate if it was previously revoked.
        existing.UpdatedAt       = runAt;
    }

    /// <summary>Audit entry for life-cycle changes.</summary>
    private static TransactionAuditLog BuildAuditEntry(Transaction tx, ChangeType type, DateTime runAt) =>
        new()
        {
            TransactionId = tx.TransactionId,
            TransactionFk = tx.Id,
            ChangedAt     = runAt,
            ChangeType    = type
            // These fields stay null for lifecycle events.
        };

    /// <summary>Audit entry for field updates.</summary>
    private static TransactionAuditLog BuildUpdateEntry(
        Transaction tx, string field, string oldVal, string newVal, DateTime runAt) =>
        new()
        {
            TransactionId = tx.TransactionId,
            TransactionFk = tx.Id,
            ChangedAt     = runAt,
            ChangeType    = ChangeType.Update,
            FieldName     = field,
            OldValue      = oldVal,
            NewValue      = newVal
        };
}
