using Fynydd.Umbraco.Search.Postgres.Indexers;
using Microsoft.Extensions.Options;

namespace Fynydd.Umbraco.Search.Postgres.VectorStores;

/// <summary>Initializes tables with a bounded startup wait.</summary>
public sealed class PostgresVectorStoreInitializer(
    IPostgresTableInitializer vectorStore,
    IOptions<AiSearchIndexFilterOptions> options,
    ILogger<PostgresVectorStoreInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.Connection.InitializationTimeoutSeconds)));

        try
        {
            await vectorStore.InitializeAsync(timeout.Token);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested == false && PostgresTransientFailure.IsTransient(exception))
        {
            logger.LogWarning(exception, "Postgres is unavailable; table initialization was skipped and Umbraco startup will continue. Rebuild affected indexes after recovery.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
