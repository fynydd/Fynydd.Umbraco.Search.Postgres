using Fynydd.Umbraco.Search.Postgres.Extensions;
using Fynydd.Umbraco.Search.Postgres.Indexers;
using Fynydd.Umbraco.Search.Postgres.VectorStores;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Umbraco.AI.Search.Core.VectorStore;

namespace Umbraco.Search.Postgres.Tests;

public sealed class PostgresVectorStoreIntegrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly NpgsqlDataSource _dataSource = fixture.CreateDataSource();
    private readonly AiSearchIndexFilterOptions _options = new()
    {
        Connection = new PostgresConnectionOptions { SchemaName = "s_" + Guid.NewGuid().ToString("N"), EmbeddingSize = 3 },
        Categories = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Documentation"] = new SearchCategory { IndexAlias = "Docs" },
            ["News"] = new SearchCategory { IndexAlias = "News" }
        }
    };

    private PostgresVectorStore Store => CreateStore();
    private PostgresVectorStore CreateStore() => new(_dataSource, Options.Create(_options), NullLogger<PostgresVectorStore>.Instance);
    private string Table(string name = "documentation") => Quote(_options.Connection.SchemaName) + "." + Quote(name);
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static AIVectorEntry Entry(string? culture = null, int chunk = 0, float[]? vector = null, IDictionary<string, object>? metadata = null) =>
        new("doc", culture, chunk, vector ?? [1, 0, 0], metadata ?? new Dictionary<string, object>());

    public async Task InitializeAsync() => await ExecuteAsync($"CREATE SCHEMA {Quote(_options.Connection.SchemaName)}");

    public async Task DisposeAsync()
    {
        await ExecuteAsync($"DROP SCHEMA {Quote(_options.Connection.SchemaName)} CASCADE");
        await _dataSource.DisposeAsync();
    }

    [Fact]
    public async Task Initialize_CreatesCategoryTablesWithVariationColumns()
    {
        await Store.InitializeAsync();
        await using var command = _dataSource.CreateCommand("SELECT table_name, column_name FROM information_schema.columns WHERE table_schema = $1");
        command.Parameters.AddWithValue(_options.Connection.SchemaName);
        var columns = new List<(string Table, string Column)>();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
            columns.Add((reader.GetString(0), reader.GetString(1)));

        Assert.Equal(["documentation", "news"], columns.Select(column => column.Table).Distinct().Order());
        Assert.Contains(("documentation", "culture"), columns);
        Assert.Contains(("documentation", "segment"), columns);
        Assert.Contains(("documentation", "embedding"), columns);
        Assert.Contains(("documentation", "metadata"), columns);

        await using var indexes = _dataSource.CreateCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = $1 AND tablename = 'documentation'");
        indexes.Parameters.AddWithValue(_options.Connection.SchemaName);
        var definitions = new List<string>();
        await using var indexReader = await indexes.ExecuteReaderAsync();

        while (await indexReader.ReadAsync())
            definitions.Add(indexReader.GetString(0));

        Assert.Contains(definitions, definition => definition.Contains("USING hnsw"));
        Assert.Contains(definitions, definition => definition.Contains("USING gin"));
    }

    [Fact]
    public async Task Initialize_PreservesVectorsAcrossRestarts()
    {
        await Store.UpsertAsync("Docs", "saved", "en-US", 0, new float[] { 1, 0, 0 });
        var restarted = CreateStore();
        await restarted.InitializeAsync();

        var result = Assert.Single(await restarted.SearchAsync("Docs", new float[] { 1, 0, 0 }, "en-US"));
        Assert.Equal("saved", result.DocumentId);
        Assert.Equal(1, await restarted.GetDocumentCountAsync("Docs"));
    }

    [Fact]
    public async Task Initialize_DimensionChangePreservesDataUntilExplicitReset()
    {
        await Store.UpsertManyAsync("Docs", "doc", [Entry()]);
        _options.Connection.EmbeddingSize = 2;
        var resized = CreateStore();
        await resized.InitializeAsync();

        Assert.Equal(3, Assert.Single(await resized.GetVectorsByDocumentAsync("Docs", "doc")).Vector.Length);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resized.UpsertAsync("Docs", "new", null, 0, new float[] { 1, 0 }));
        await resized.ResetAsync("Docs");
        Assert.Equal(0, await resized.GetDocumentCountAsync("Docs"));
        await resized.UpsertAsync("Docs", "new", null, 0, new float[] { 1, 0 });
        Assert.Equal(2, Assert.Single(await resized.GetVectorsByDocumentAsync("Docs", "new")).Vector.Length);
    }

    [Fact]
    public async Task Search_ReturnsTopCosineScoresInDescendingOrder()
    {
        var store = Store;
        await store.UpsertAsync("Docs", "best", null, 0, new float[] { 1, 0, 0 });
        await store.UpsertAsync("Docs", "near", null, 0, new float[] { 0.8f, 0.6f, 0 });
        await store.UpsertAsync("Docs", "orthogonal", null, 0, new float[] { 0, 1, 0 });
        await store.UpsertAsync("Docs", "opposite", null, 0, new float[] { -1, 0, 0 });

        var results = await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, topK: 2);

        Assert.Equal(["best", "near"], results.Select(result => result.DocumentId));
        Assert.Equal(1d, results[0].Score, 5);
        Assert.Equal(0.8d, results[1].Score, 5);
        var all = await store.SearchAsync("Docs", new float[] { 1, 0, 0 });
        Assert.Equal(-1d, all[^1].Score, 5);
        Assert.Empty(await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, topK: 0));
    }

    [Fact]
    public async Task Search_AppliesTypedFiltersBeforeTopK()
    {
        var store = Store;
        await store.UpsertAsync("Docs", "excluded", null, 0, new float[] { 1, 0, 0 }, new Dictionary<string, object> { ["kind"] = "other" });
        await store.UpsertAsync("Docs", "match", null, 0, new float[] { 0.8f, 0.6f, 0 }, new Dictionary<string, object>
        {
            ["kind"] = "article", ["enabled"] = true, ["number"] = 7, ["weight"] = 0.5, ["tags"] = new[] { "red", "blue" }
        });
        await store.UpsertAsync("Docs", "wrongType", null, 0, new float[] { 0.9f, 0.1f, 0 }, new Dictionary<string, object>
        {
            ["kind"] = "article", ["enabled"] = "true", ["number"] = 7, ["weight"] = 0.5, ["tags"] = "blue"
        });
        foreach (var (field, value) in new Dictionary<string, object> { ["kind"] = "other", ["number"] = "7", ["weight"] = 0.6, ["tags"] = new[] { "green" } })
        {
            var metadata = new Dictionary<string, object> { ["kind"] = "article", ["enabled"] = true, ["number"] = 7, ["weight"] = 0.5, ["tags"] = new[] { "blue" } };
            metadata[field] = value;
            await store.UpsertAsync("Docs", "wrong_" + field, null, 0, new float[] { 0.9f, 0.1f, 0 }, metadata);
        }
        var filters = new Dictionary<string, IReadOnlyCollection<object?>?>
        {
            ["kind"] = ["page", "article"], ["enabled"] = [true], ["number"] = [7L], ["weight"] = [0.5m], ["tags"] = ["blue"],
            ["ignored"] = [null], ["empty"] = [], ["null"] = null
        };

        var result = Assert.Single(await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, null, 1, filters));
        Assert.Equal("match", result.DocumentId);
        Assert.True(Assert.IsType<bool>(result.Metadata!["enabled"]));
        Assert.Equal(7L, result.Metadata["number"]);
    }

    [Fact]
    public async Task Search_UsesCultureAndSegmentFallbacksWithoutLeakingOtherVariations()
    {
        var store = Store;
        var variations = new Dictionary<string, string?>
        {
            ["invariant"] = null, ["culture"] = "en-US", ["segment"] = AiVariationKey.Create(null, "mobile"),
            ["both"] = AiVariationKey.Create("en-US", "mobile"), ["french"] = "fr-FR", ["tablet"] = AiVariationKey.Create("en-US", "tablet")
        };

        foreach (var (document, variation) in variations)
            await store.UpsertAsync("Docs", document, variation, 0, new float[] { 1, 0, 0 });

        var results = await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, AiVariationKey.Create("EN-us", "MOBILE"));
        Assert.Equal(["both", "culture", "invariant", "segment"], results.Select(result => result.DocumentId).Order());
        Assert.Equal("invariant", Assert.Single(await store.SearchAsync("Docs", new float[] { 1, 0, 0 })).DocumentId);
        Assert.Equal(["culture", "invariant"], (await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, "en-US")).Select(result => result.DocumentId).Order());
    }

    [Fact]
    public async Task Upsert_ReplacesSameChunkAndRoundTripsMetadata()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry("en-US"), Entry(chunk: 1)]);
        await store.UpsertManyAsync("Docs", "doc", [Entry("EN-us", vector: [0, 1, 0], metadata: new Dictionary<string, object>
        {
            ["snippet"] = "New snippet", ["accessIds"] = "member", ["bool"] = true, ["integer"] = 12,
            ["real"] = 1.25, ["nested"] = new Dictionary<string, object> { ["key"] = "value" },
            ["array"] = new object?[] { "text", 2, null }, ["documentId"] = "forged", ["chunkIndex"] = 100, ["segment"] = "forged"
        })]);

        var entry = Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "doc", "en-US"));
        Assert.Equal("EN-us", entry.Culture);
        Assert.Equal(new float[] { 0, 1, 0 }, entry.Vector.ToArray());
        Assert.Equal("New snippet", entry.Metadata!["snippet"]);
        Assert.Equal("member", entry.Metadata["accessIds"]);
        Assert.Equal("doc", entry.Metadata["documentId"]);
        Assert.Equal(0L, entry.Metadata["chunkIndex"]);
        Assert.Equal(12L, entry.Metadata["integer"]);
        Assert.Equal(1.25, entry.Metadata["real"]);
        Assert.True(Assert.IsType<bool>(entry.Metadata["bool"]));
        Assert.Equal("value", Assert.IsType<Dictionary<string, object>>(entry.Metadata["nested"])["key"]);
        Assert.Null(Assert.IsType<List<object>>(entry.Metadata["array"])[2]);
        Assert.DoesNotContain("segment", entry.Metadata.Keys);
        Assert.Equal(2, (await store.GetVectorsByDocumentAsync("Docs", "doc")).Count);
    }

    [Fact]
    public async Task Delete_RemovesExactVariationThenWholeDocumentOnlyWithinCategory()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry(), Entry("en-US"), Entry(AiVariationKey.Create("en-US", "mobile")), Entry("fr-FR")]);
        await store.UpsertManyAsync("News", "doc", [Entry()]);
        await store.UpsertManyAsync("Docs", "other", [Entry()]);
        await store.DeleteAsync("Docs", "doc", "en-US");

        Assert.Equal(3, (await store.GetVectorsByDocumentAsync("Docs", "doc")).Count);
        Assert.Empty(await store.GetVectorsByDocumentAsync("Docs", "doc", "en-US"));
        Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "doc", AiVariationKey.Create("en-US", "mobile")));
        await store.DeleteAsync("Docs", "doc", null);
        Assert.Equal(2, (await store.GetVectorsByDocumentAsync("Docs", "doc")).Count);
        await store.DeleteDocumentAsync("Docs", "doc");
        Assert.Empty(await store.GetVectorsByDocumentAsync("Docs", "doc"));
        Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "other"));
        Assert.Equal(1, await store.GetDocumentCountAsync("News"));
    }

    [Fact]
    public async Task Reset_ClearsOnlySelectedCategoryAndCountsDistinctDocuments()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry(), Entry("en-US"), Entry(chunk: 1)]);
        await store.UpsertManyAsync("Docs", "second", [Entry()]);
        await store.UpsertManyAsync("News", "doc", [Entry()]);

        Assert.Equal(2, await store.GetDocumentCountAsync("Docs"));
        await store.ResetAsync("Docs");
        Assert.Equal(0, await store.GetDocumentCountAsync("Docs"));
        Assert.Equal(1, await store.GetDocumentCountAsync("News"));
        await store.UpsertManyAsync("Docs", "new", [Entry()]);
        Assert.Equal("new", Assert.Single(await store.SearchAsync("Docs", new float[] { 1, 0, 0 })).DocumentId);
    }

    [Fact]
    public async Task ReplaceDocument_RemovesStaleChunksAndVariationsAtomically()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry(), Entry(chunk: 1), Entry("fr-FR")]);
        await store.ReplaceDocumentAsync("Docs", "doc", [Entry("en-US", vector: [0, 1, 0])]);

        var entry = Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "doc"));
        Assert.Equal("en-US", entry.Culture);
        Assert.Equal(new float[] { 0, 1, 0 }, entry.Vector.ToArray());
        await store.ReplaceDocumentAsync("Docs", "doc", []);
        Assert.Empty(await store.GetVectorsByDocumentAsync("Docs", "doc"));
    }

    [Fact]
    public async Task ReplaceDocument_DatabaseFailureRollsBackDeletion()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry(metadata: new Dictionary<string, object> { ["snippet"] = "saved" })]);

        await Assert.ThrowsAsync<PostgresException>(() => store.ReplaceDocumentAsync("Docs", "doc", [Entry(metadata: new Dictionary<string, object> { ["invalidJsonbText"] = "\0" })]));

        var saved = Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "doc"));
        Assert.Equal("saved", saved.Metadata!["snippet"]);
        Assert.Equal(new float[] { 1, 0, 0 }, saved.Vector.ToArray());
    }

    [Fact]
    public async Task UpsertMany_InvalidLaterVectorPreservesExistingData()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry()]);
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpsertManyAsync("Docs", "doc", [Entry(chunk: 1), Entry(chunk: 2, vector: [1, 0])]));
        Assert.Equal(0, Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "doc")).ChunkIndex);
    }

    [Fact]
    public async Task UpsertMany_DatabaseFailureRollsBackEarlierChunks()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", [Entry()]);
        await Assert.ThrowsAsync<PostgresException>(() => store.UpsertManyAsync("Docs", "doc",
            [Entry(chunk: 1), Entry(chunk: 2, metadata: new Dictionary<string, object> { ["invalid"] = "\0" })]));
        Assert.Equal(0, Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "doc")).ChunkIndex);
    }

    [Fact]
    public async Task DefaultIndex_WhenEnabledUsesItsOwnTable()
    {
        _options.DisableDefaultIndex = false;
        var store = Store;
        await store.UpsertManyAsync("UmbAI_Search", "default", [Entry()]);
        Assert.Equal("default", Assert.Single(await store.SearchAsync("UmbAI_Search", new float[] { 1, 0, 0 })).DocumentId);
        Assert.Equal(0, await store.GetDocumentCountAsync("Docs"));
        Assert.Equal(1L, await ScalarAsync($"SELECT COUNT(*) FROM {Table("umbai_search")}"));
    }

    [Fact]
    public async Task CachedTable_RecoversAfterExternalDrop()
    {
        var store = Store;
        await store.InitializeAsync();
        await ExecuteAsync($"DROP TABLE {Table()}");
        await store.UpsertManyAsync("Docs", "doc", [Entry()]);
        Assert.Equal(1, await store.GetDocumentCountAsync("Docs"));
    }

    [Fact]
    public async Task Initialize_ConcurrentHostsCreateTablesWithoutRaces()
    {
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => CreateStore().InitializeAsync()));
        await Store.UpsertManyAsync("Docs", "doc", [Entry()]);
        Assert.Equal(1, await Store.GetDocumentCountAsync("Docs"));
    }

    [Fact]
    public async Task Initialize_RefusesExistingUnmanagedTableWithoutChangingIt()
    {
        await ExecuteAsync($"CREATE TABLE {Table()} (value text); INSERT INTO {Table()} VALUES ('saved')");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Store.InitializeAsync());
        Assert.Equal("saved", await ScalarAsync($"SELECT value FROM {Table()}"));
    }

    [Fact]
    public async Task IdentifiersAndFilterValues_AreSafelyQuotedAndParameterized()
    {
        var category = "Docs\"; DROP TABLE sentinel; --";
        _options.Categories.Clear();
        _options.Categories[category] = new SearchCategory { IndexAlias = "Docs" };
        var value = "x' OR true; DROP TABLE sentinel; --";
        await ExecuteAsync($"CREATE TABLE {Table("sentinel")} (value text)");
        var store = Store;
        await store.UpsertAsync("Docs", "doc", null, 0, new float[] { 1, 0, 0 }, new Dictionary<string, object> { ["key'--"] = value });
        var filters = new Dictionary<string, IReadOnlyCollection<object?>?> { ["key'--"] = [value] };
        Assert.Equal("doc", Assert.Single(await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, null, 5, filters)).DocumentId);
        Assert.Equal(0L, await ScalarAsync($"SELECT COUNT(*) FROM {Table("sentinel")}"));
        filters["key'--"] = ["wrong"];
        Assert.Empty(await store.SearchAsync("Docs", new float[] { 1, 0, 0 }, null, 5, filters));
    }

    [Fact]
    public async Task LargeDimensions_UseExactCosineSearch()
    {
        _options.Connection.EmbeddingSize = 2001;
        var vector = new float[2001];
        vector[0] = 1;
        var store = Store;
        await store.UpsertAsync("Docs", "large", null, 0, vector);
        Assert.Equal("large", Assert.Single(await store.SearchAsync("Docs", vector)).DocumentId);
        Assert.Equal(2001, Assert.Single(await store.GetVectorsByDocumentAsync("Docs", "large")).Vector.Length);
    }

    [Fact]
    public async Task GetVectors_ReturnsEveryChunkBeyondFormerPageLimit()
    {
        var store = Store;
        await store.UpsertManyAsync("Docs", "doc", Enumerable.Range(0, 10001).Select(index => Entry(chunk: index)));
        var entries = await store.GetVectorsByDocumentAsync("Docs", "doc");
        Assert.Equal(10001, entries.Count);
        Assert.Equal(10000, entries[^1].ChunkIndex);
        Assert.Equal(1, await store.GetDocumentCountAsync("Docs"));
    }

    [Fact]
    public async Task UnknownIndex_IsIgnoredWithoutCreatingTables()
    {
        var store = Store;
        await store.UpsertManyAsync("Unknown", "doc", [Entry()]);
        await store.DeleteAsync("Unknown", "doc", null);
        await store.DeleteDocumentAsync("Unknown", "doc");
        await store.ResetAsync("Unknown");
        Assert.Empty(await store.SearchAsync("Unknown", new float[] { 1, 0, 0 }));
        Assert.Empty(await store.GetVectorsByDocumentAsync("Unknown", "doc"));
        Assert.Equal(0, await store.GetDocumentCountAsync("Unknown"));
        await using var command = _dataSource.CreateCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = $1");
        command.Parameters.AddWithValue(_options.Connection.SchemaName);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Search_CallerCancellationEscapesBlockedQuery()
    {
        var store = Store;
        await store.InitializeAsync();
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand($"LOCK TABLE {Table()} IN ACCESS EXCLUSIVE MODE", connection, transaction);
        await command.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync("Docs", new float[] { 1, 0, 0 }, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Initialize_UsesRestrictedRoleWithoutDatabaseOrExtensionCreation()
    {
        var role = "r_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE ROLE {Quote(role)} LOGIN PASSWORD 'local_restricted_password'; GRANT USAGE, CREATE ON SCHEMA {Quote(_options.Connection.SchemaName)} TO {Quote(role)}");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Username = role, Password = "local_restricted_password" };
        await using var dataSource = fixture.CreateDataSource(connection.ConnectionString);
        var store = new PostgresVectorStore(dataSource, Options.Create(_options), NullLogger<PostgresVectorStore>.Instance);
        await store.InitializeAsync();
        await store.UpsertManyAsync("Docs", "doc", [Entry()]);
        Assert.Equal(1, await store.GetDocumentCountAsync("Docs"));
        await store.ResetAsync("Docs");
        Assert.Equal(0, await store.GetDocumentCountAsync("Docs"));
    }

    [Fact]
    public async Task Initialize_MissingExtensionRequiresAdministratorAction()
    {
        var database = "no_vector_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync($"CREATE DATABASE {Quote(database)}");

        try
        {
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database };
            await using var dataSource = fixture.CreateDataSource(connection.ConnectionString);
            var store = new PostgresVectorStore(dataSource, Options.Create(_options), NullLogger<PostgresVectorStore>.Instance);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.InitializeAsync());
            Assert.Contains("administrator", exception.Message);
            await using var command = dataSource.CreateCommand("SELECT COUNT(*) FROM pg_extension WHERE extname = 'vector'");
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
        finally
        {
            await ExecuteAsync($"DROP DATABASE {Quote(database)} WITH (FORCE)");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var command = _dataSource.CreateCommand(sql);
        return await command.ExecuteScalarAsync();
    }
}
