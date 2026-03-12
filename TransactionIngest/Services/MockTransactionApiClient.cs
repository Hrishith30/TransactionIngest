using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TransactionIngest.Services;

/// <summary>Fake API client that reads from a local JSON file.</summary>
public sealed class MockTransactionApiClient : ITransactionApiClient
{
    private readonly string _feedFilePath;
    private readonly ILogger<MockTransactionApiClient> _logger;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MockTransactionApiClient(IConfiguration configuration, ILogger<MockTransactionApiClient> logger)
    {
        _logger = logger;

        var configuredPath = configuration["MockFeed:FilePath"] ?? "mock_feed.json";

        // Resolve path relative to the app base.
        _feedFilePath = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, configuredPath);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TransactionDto>> FetchLast24HoursAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_feedFilePath))
        {
            // Fail fast if the file is missing.
            throw new FileNotFoundException(
                $"Mock feed not found at '{_feedFilePath}'. " +
                "Check that mock_feed.json is in the project and MockFeed:FilePath is correct.",
                _feedFilePath);
        }

        _logger.LogInformation("Reading mock feed from '{FeedPath}'.", _feedFilePath);

        await using var stream = File.OpenRead(_feedFilePath);
        var transactions = await JsonSerializer.DeserializeAsync<List<TransactionDto>>(
            stream, _jsonOptions, ct);

        _logger.LogInformation("Loaded {Count} transactions from mock feed.", transactions?.Count ?? 0);
        return transactions ?? [];
    }
}
