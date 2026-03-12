using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TransactionIngest.Data;
using TransactionIngest.Services;

// Load appsettings.json.
IConfiguration configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

var services = new ServiceCollection();

services.AddLogging(builder =>
{
    builder.ClearProviders();
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

services.AddSingleton(configuration);

var connectionString = configuration.GetConnectionString("Default") ?? "Data Source=transactions.db";
services.AddDbContext<AppDbContext>(opts => opts.UseSqlite(connectionString));

// Choose between mock or real API client.
bool useMock = bool.TryParse(configuration["MockFeed:Enabled"], out var m) && m;
if (useMock)
{
    services.AddScoped<ITransactionApiClient, MockTransactionApiClient>();
}
else
{
    services.AddHttpClient<HttpTransactionApiClient>();
    services.AddScoped<ITransactionApiClient, HttpTransactionApiClient>();
}
services.AddScoped<TransactionIngestionService>();

await using var serviceProvider = services.BuildServiceProvider();

// Ensure DB is created on first run.
using (var scope = serviceProvider.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Run ingestion once and print results.
using (var scope = serviceProvider.CreateScope())
{
    var ingestionService = scope.ServiceProvider.GetRequiredService<TransactionIngestionService>();
    var logger           = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var result = await ingestionService.ExecuteAsync();

        Console.WriteLine();
        Console.WriteLine("┌─────────────────────────────────────────┐");
        Console.WriteLine("│        Transaction Ingestion Run        │");
        Console.WriteLine("├─────────────────────────────────────────┤");
        Console.WriteLine($"│  Fetched   : {result.Fetched,5} transactions         │");
        Console.WriteLine($"│  Inserted  : {result.Inserted,5}                      │");
        Console.WriteLine($"│  Updated   : {result.Updated,5}                      │");
        Console.WriteLine($"│  Revoked   : {result.Revoked,5}                      │");
        Console.WriteLine($"│  Finalized : {result.Finalized,5}                      │");
        Console.WriteLine("└─────────────────────────────────────────┘");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex, "Ingestion run failed.");
        Environment.Exit(1);
    }
}
