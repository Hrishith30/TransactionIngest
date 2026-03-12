using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TransactionIngest.Services;

/// <summary>HTTP client for the real payments API.</summary>
public sealed class HttpTransactionApiClient : ITransactionApiClient
{
    private readonly HttpClient _httpClient;
    private readonly string _snapshotUrl;
    private readonly ILogger<HttpTransactionApiClient> _logger;

    public HttpTransactionApiClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<HttpTransactionApiClient> logger)
    {
        _httpClient = httpClient;
        _logger     = logger;

        var baseUrl      = configuration["Api:BaseUrl"]      ?? throw new InvalidOperationException("Api:BaseUrl is not configured.");
        var snapshotPath = configuration["Api:SnapshotPath"] ?? throw new InvalidOperationException("Api:SnapshotPath is not configured.");

        _snapshotUrl = $"{baseUrl.TrimEnd('/')}/{snapshotPath.TrimStart('/')}";
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TransactionDto>> FetchLast24HoursAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("GET {Url}", _snapshotUrl);

        // Throws on error, which triggers a rollback.
        var transactions = await _httpClient.GetFromJsonAsync<List<TransactionDto>>(_snapshotUrl, ct);

        _logger.LogInformation("Received {Count} transactions from API.", transactions?.Count ?? 0);
        return transactions ?? [];
    }
}
