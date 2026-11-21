using Fynydd.Umbraco.Search.Postgres.Indexers;
using Fynydd.Umbraco.Search.Postgres.VectorStores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;

namespace Umbraco.Search.Postgres.Tests;

public sealed class PostgresVectorStoreValidationTests : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresVectorStoreValidationTests()
    {
        var builder = new NpgsqlDataSourceBuilder("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1;Command Timeout=1");
        builder.UseVector();
        _dataSource = builder.Build();
    }

    private PostgresVectorStore CreateStore(Action<AiSearchIndexFilterOptions>? configure = null)
    {
        var options = new AiSearchIndexFilterOptions { Connection = { EmbeddingSize = 3 }, Categories = { ["Docs"] = new SearchCategory { IndexAlias = "Docs" } } };
        configure?.Invoke(options);
        return new PostgresVectorStore(_dataSource, Options.Create(options), NullLogger<PostgresVectorStore>.Instance);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16001)]
    public void Constructor_RejectsUnsupportedDimensions(int dimensions) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateStore(options => options.Connection.EmbeddingSize = (ulong)dimensions));

    [Theory]
    [InlineData(1)]
    [InlineData(16000)]
    public void Constructor_AcceptsSupportedDimensionBoundaries(int dimensions) =>
        Assert.NotNull(CreateStore(options => options.Connection.EmbeddingSize = (ulong)dimensions));

    [Fact]
    public void Constructor_RejectsDuplicateIndexAliases() =>
        Assert.Throws<ArgumentException>(() => CreateStore(options => options.Categories["News"] = new SearchCategory { IndexAlias = "docs" }));

    [Fact]
    public void Constructor_RejectsBlankDatabaseName() =>
        Assert.Throws<ArgumentException>(() => CreateStore(options => options.Connection.DatabaseName = " "));

    [Fact]
    public void Constructor_ValidatesIdentifierByteLimitWithoutTruncation()
    {
        Assert.NotNull(CreateStore(options => options.Connection.SchemaName = new string('é', 31) + "a"));
        Assert.Throws<ArgumentException>(() => CreateStore(options => options.Connection.SchemaName = new string('é', 32)));
        Assert.Throws<ArgumentException>(() => CreateStore(options => options.Categories[new string('Ⱥ', 22)] = new SearchCategory { IndexAlias = "Expanded" }));
    }

    [Fact]
    public void Constructor_RejectsNullCharactersInIdentifiers() =>
        Assert.Throws<ArgumentException>(() => CreateStore(options => options.Connection.SchemaName = "bad\0schema"));

    [Fact]
    public async Task Writes_RejectInvalidVectorsBeforeConnecting()
    {
        var store = CreateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync("Docs", "doc", null, 0, new float[] { 1, 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync("Docs", "doc", null, 0, new float[] { 0, 0, 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync("Docs", "doc", null, 0, new float[] { float.NaN, 1, 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertAsync("Docs", "doc", null, 0, new float[] { 1, float.PositiveInfinity, 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.UpsertAsync("Docs", "doc", null, -1, new float[] { 1, 0, 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SearchAsync("Docs", new float[] { 1, 0 }));
    }

    [Fact]
    public async Task Operations_PropagateCallerCancellationBeforeConnecting()
    {
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var token = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.InitializeAsync(token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync("Docs", "doc", null, 0, new float[] { 1, 0, 0 }, cancellationToken: token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync("Docs", "doc", null, token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteDocumentAsync("Docs", "doc", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetDocumentCountAsync("Docs", token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetVectorsByDocumentAsync("Docs", "doc", cancellationToken: token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync("Docs", new float[] { 1, 0, 0 }, cancellationToken: token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ResetAsync("Docs", token));
    }

    [Fact]
    public async Task Search_ConnectionRefusedReturnsNoResults()
    {
        Assert.Empty(await CreateStore().SearchAsync("Docs", new float[] { 1, 0, 0 }));
    }

    public void Dispose() => _dataSource.Dispose();
}
