namespace TransactionIngest.Services;

/// <summary>
/// Fetches the 24-hour transaction snapshot from the upstream payments gateway.
/// In production this calls a real HTTP endpoint; locally it reads from mock_feed.json.
/// </summary>
public interface ITransactionApiClient
{
    /// <summary>
    /// Returns all transactions that occurred within the last 24 hours.
    /// The list may be unordered and may include late arrivals.
    /// </summary>
    Task<IReadOnlyList<TransactionDto>> FetchLast24HoursAsync(CancellationToken ct = default);
}
