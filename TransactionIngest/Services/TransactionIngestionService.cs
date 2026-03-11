using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TransactionIngest.Data;
using TransactionIngest.Models;

namespace TransactionIngest.Services;

/// <summary>
/// Runs the hourly ingestion pipeline. Each call to <see cref="ExecuteAsync"/> does:
///   1. Fetch the 24-hour snapshot from the API client.
///   2. Upsert each record by TransactionId — insert new ones, update changed ones.
///   3. Revoke any Active record that is missing from the snapshot but still within the window.
///   4. Finalize any record whose TransactionTime is older than the window.
///
/// Everything from step 2 onward runs inside a single database transaction,
/// so a failed run rolls back cleanly and can be retried safely.
/// </summary>
public sealed class TransactionIngestionService
{
    private readonly AppDbContext _db;
    private readonly ITransactionApiClient _apiClient;
    private readonly ILogger<TransactionIngestionService> _logger;
    private readonly int _windowHours;

    /// <summary>Summary counts returned after each run — used for logging and test assertions.</summary>
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

    /// <summary>
    /// Runs the full ingestion pipeline. Throws on failure (after rolling back).
    /// </summary>
    public async Task<IngestionResult> ExecuteAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("=== Ingestion run started at {RunTime} UTC ===", DateTime.UtcNow);

        // Fetch outside the DB transaction — this is a read-only API call.
        var snapshot = await _apiClient.FetchLast24HoursAsync(ct);
        _logger.LogInformation("Fetched {Count} transactions from the 24-hour snapshot.", snapshot.Count);

        // Dictionary for O(1) membership checks during revocation.
        var snapshotById = snapshot.ToDictionary(dto => dto.TransactionId, StringComparer.Ordinal);

        var windowStart = DateTime.UtcNow.AddHours(-_windowHours);
        var runAt       = DateTime.UtcNow;

        int inserted  = 0;
        int updated   = 0;
        int revoked   = 0;
        int finalized = 0;

        await using var dbTransaction = await _db.Database.BeginTransactionAsync(ct);
        try
        {
            // Step 2 — Upsert.
            foreach (var dto in snapshot)
            {
                var existing = await _db.Transactions
                    .FirstOrDefaultAsync(t => t.TransactionId == dto.TransactionId, ct);

                if (existing is null)
                {
                    // New transaction — insert it.
                    var newTx = MapToEntity(dto, runAt);
                    _db.Transactions.Add(newTx);
                    await _db.SaveChangesAsync(ct); // Need the generated Id before writing the audit row.

                    _db.AuditLogs.Add(BuildAuditEntry(newTx, ChangeType.Insert, runAt));
                    await _db.SaveChangesAsync(ct);

                    inserted++;
                    _logger.LogDebug("Inserted {TxId}.", dto.TransactionId);
                }
                else if (existing.Status == TransactionStatus.Finalized)
                {
                    // Finalized records are sealed — skip without touching them.
                    _logger.LogDebug("Skipping finalized {TxId}.", dto.TransactionId);
                }
                else
                {
                    // Existing and still active — check for field-level changes.
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

            // Step 3 — Revoke records that were in the window but are missing from this snapshot.
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

            // Step 4 — Finalize anything older than the window.
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

    /// <summary>
    /// Maps a DTO to a new Transaction entity.
    /// The full card number is hashed here and never stored in plaintext.
    /// </summary>
    private static Transaction MapToEntity(TransactionDto dto, DateTime runAt) =>
        new()
        {
            TransactionId   = dto.TransactionId,
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

    /// <summary>SHA-256 hex digest of a card number string.</summary>
    private static string HashCard(string cardNumber)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(cardNumber));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compares tracked fields between what is stored and what the API returned.
    /// Returns one tuple per changed field: (FieldName, OldValue, NewValue).
    /// Card numbers are compared via their hash to avoid touching the raw PAN.
    /// </summary>
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

    /// <summary>
    /// Writes the new field values onto the existing entity.
    /// EF Core change tracking will persist these on the next SaveChangesAsync.
    /// Also reactivates a previously revoked record if it reappears in a snapshot.
    /// </summary>
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

    /// <summary>Builds an audit entry for a lifecycle change (Insert / Revoked / Finalized).</summary>
    private static TransactionAuditLog BuildAuditEntry(Transaction tx, ChangeType type, DateTime runAt) =>
        new()
        {
            TransactionId = tx.TransactionId,
            TransactionFk = tx.Id,
            ChangedAt     = runAt,
            ChangeType    = type
            // FieldName / OldValue / NewValue intentionally left null for lifecycle events.
        };

    /// <summary>Builds an audit entry for a single field-level Update.</summary>
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
