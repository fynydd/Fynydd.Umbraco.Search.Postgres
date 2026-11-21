using Fynydd.Umbraco.Search.Postgres.Indexers;
using Fynydd.Umbraco.Search.Postgres.VectorStores;
using Npgsql;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Umbraco.Search.Postgres.Tests;

public sealed class PostgresVectorStoreInitializerTests
{
    [Fact]
    public async Task StartAsync_PostgresUnavailable_CompletesWithoutStoppingHost()
    {
        var store = new RecordingTableInitializer(_ =>
            Task.FromException(new NpgsqlException("Connection refused.", new System.Net.Sockets.SocketException())));
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StartAsync_PostgresDeadlineExceeded_CompletesWithoutStoppingHost()
    {
        var store = new RecordingTableInitializer(_ =>
            Task.FromException(new TimeoutException("Deadline exceeded.")));
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StartAsync_PostgresCancelledWithoutHostCancellation_CompletesWithoutStoppingHost()
    {
        var store = new RecordingTableInitializer(_ =>
            Task.FromException(new OperationCanceledException("Postgres cancelled the request.")));
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StartAsync_InitializationTimeout_CompletesWithoutStoppingHost()
    {
        var store = new RecordingTableInitializer(token =>
            Task.Delay(Timeout.InfiniteTimeSpan, token));
        var initializer = CreateInitializer(store, initializationTimeoutSeconds: 1);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
        Assert.True(store.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_HostCancellation_PropagatesCancellation()
    {
        var store = new RecordingTableInitializer(token =>
        {
            token.ThrowIfCancellationRequested();

            return Task.CompletedTask;
        });
        var initializer = CreateInitializer(store);
        using var cancellationTokenSource = new CancellationTokenSource();

        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            initializer.StartAsync(cancellationTokenSource.Token));

        Assert.Equal(1, store.InitializeCalls);
        Assert.True(store.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_HealthyStore_CompletesInitialization()
    {
        var store = new RecordingTableInitializer();
        var initializer = CreateInitializer(store);

        await initializer.StartAsync(CancellationToken.None);

        Assert.Equal(1, store.InitializeCalls);
        Assert.False(store.LastCancellationToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StartAsync_NonTransientFailure_Propagates()
    {
        var store = new RecordingTableInitializer(_ =>
            Task.FromException(new InvalidOperationException("Programming error.")));
        var initializer = CreateInitializer(store);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.StartAsync(CancellationToken.None));

        Assert.Equal("Programming error.", exception.Message);
        Assert.Equal(1, store.InitializeCalls);
    }

    [Fact]
    public async Task StopAsync_CompletesWithoutWork()
    {
        var store = new RecordingTableInitializer();
        var initializer = CreateInitializer(store);

        await initializer.StopAsync(CancellationToken.None);

        Assert.Equal(0, store.InitializeCalls);
    }

    private static PostgresVectorStoreInitializer CreateInitializer(
        IPostgresTableInitializer store,
        int initializationTimeoutSeconds = 5) =>
        new(
            store,
            Options.Create(new AiSearchIndexFilterOptions
            {
                Connection =
                {
                    InitializationTimeoutSeconds = initializationTimeoutSeconds
                }
            }),
            NullLogger<PostgresVectorStoreInitializer>.Instance);

    private sealed class RecordingTableInitializer(Func<CancellationToken, Task>? initialize = null) : IPostgresTableInitializer
    {
        public int InitializeCalls { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Task InitializeAsync(CancellationToken cancellationToken = new())
        {
            InitializeCalls++;
            LastCancellationToken = cancellationToken;

            return initialize?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
