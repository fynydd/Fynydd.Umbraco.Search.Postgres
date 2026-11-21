using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fynydd.Umbraco.Search.Postgres.Extensions;
using Fynydd.Umbraco.Search.Postgres.Indexers;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Umbraco.AI.Search.Core.VectorStore;

namespace Fynydd.Umbraco.Search.Postgres.VectorStores;

/// <summary>Stores Umbraco search vectors in category tables with culture and segment row fields.</summary>
public class PostgresVectorStore : IAIVectorStore, IPostgresTableInitializer
{
    private const string TableMarker = "Fynydd.Umbraco.Search.Postgres";
    private readonly NpgsqlDataSource _dataSource;
    private readonly AiSearchIndexFilterOptions _options;
    private readonly ILogger<PostgresVectorStore> _logger;
    private readonly Dictionary<string, string> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _dimensions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _tableLocks = new(StringComparer.Ordinal);

    public PostgresVectorStore(NpgsqlDataSource dataSource, IOptions<AiSearchIndexFilterOptions> options, ILogger<PostgresVectorStore> logger)
    {
        _dataSource = dataSource;
        _options = options.Value;
        _logger = logger;
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Connection.DatabaseName);
        ValidateIdentifier(_options.Connection.SchemaName);

        if (_options.Connection.EmbeddingSize is < 1 or > 16000)
            throw new ArgumentOutOfRangeException(nameof(options), "EmbeddingSize must be between 1 and 16000.");

        foreach (var (categoryName, category) in _options.Categories)
        {
            ValidateIdentifier(categoryName);
            ValidateIdentifier(categoryName.ToLowerInvariant());
            ArgumentException.ThrowIfNullOrWhiteSpace(category.IndexAlias);

            if (_tables.TryAdd(category.IndexAlias, categoryName.ToLowerInvariant()) == false)
                throw new ArgumentException($"Each category requires a unique IndexAlias; '{category.IndexAlias}' is duplicated.", nameof(options));
        }

        if (_options.DisableDefaultIndex == false)
            _tables.TryAdd("UmbAI_Search", "umbai_search");

        if (_tables.Values.Distinct(StringComparer.Ordinal).Count() != _tables.Count)
            throw new ArgumentException("Category names must produce distinct lowercase table names.", nameof(options));
    }

    /// <summary>Validates the existing extension and prepares configured tables without clearing data.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var table in _tables.Values)
            await EnsureTableAsync(table, cancellationToken);
    }

    public Task UpsertAsync(string indexName, string documentId, string? culture, int chunkIndex, ReadOnlyMemory<float> vector,
        IDictionary<string, object>? metadata = null, CancellationToken cancellationToken = default) =>
        UpsertManyAsync(indexName, documentId, [new AIVectorEntry(documentId, culture, chunkIndex, vector, metadata)], cancellationToken);

    /// <summary>Upserts all supplied chunks in one transaction.</summary>
    public Task UpsertManyAsync(string indexName, string documentId, IEnumerable<AIVectorEntry> entries, CancellationToken cancellationToken = default) =>
        WriteAsync(indexName, documentId, entries, false, cancellationToken);

    /// <summary>Atomically replaces a document's complete set of chunks and variations.</summary>
    public Task ReplaceDocumentAsync(string indexName, string documentId, IEnumerable<AIVectorEntry> entries, CancellationToken cancellationToken = default) =>
        WriteAsync(indexName, documentId, entries, true, cancellationToken);

    private async Task WriteAsync(string indexName, string documentId, IEnumerable<AIVectorEntry> entries, bool replace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_tables.TryGetValue(indexName, out var table) == false)
            return;

        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        var chunks = entries.ToList();

        foreach (var chunk in chunks)
        {
            ValidateVector(chunk.Vector);

            if (chunk.ChunkIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(entries), "Chunk indexes must be nonnegative.");
        }

        if (chunks.Count == 0 && replace == false)
            return;

        await WithTableAsync(table, async () =>
        {
            ValidateStoredDimensions(table);
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var batch = new NpgsqlBatch(connection, transaction);

            if (replace)
            {
                var delete = new NpgsqlBatchCommand($"DELETE FROM {Qualified(table)} WHERE document_id = $1");
                delete.Parameters.AddWithValue(documentId);
                batch.BatchCommands.Add(delete);
            }

            foreach (var chunk in chunks)
            {
                var (culture, segment) = AiVariationKey.Parse(chunk.Culture);
                var command = new NpgsqlBatchCommand($"""
                    INSERT INTO {Qualified(table)} (document_id, variation_key, culture, segment, chunk_index, embedding, metadata)
                    VALUES ($1, $2, $3, $4, $5, $6, $7)
                    ON CONFLICT (document_id, variation_key, chunk_index) DO UPDATE
                    SET culture = EXCLUDED.culture, segment = EXCLUDED.segment,
                        embedding = EXCLUDED.embedding, metadata = EXCLUDED.metadata
                    """);
                command.Parameters.AddWithValue(documentId);
                command.Parameters.AddWithValue(NormalizeVariation(chunk.Culture));
                command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)culture ?? DBNull.Value);
                command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)segment ?? DBNull.Value);
                command.Parameters.AddWithValue(chunk.ChunkIndex);
                command.Parameters.AddWithValue(new Vector(chunk.Vector));
                command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, PostgresMetadata.Create(documentId, chunk.Culture, chunk.ChunkIndex, chunk.Metadata));
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(string indexName, string documentId, string? culture, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(indexName, documentId, culture, false, cancellationToken);

    public Task DeleteDocumentAsync(string indexName, string documentId, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync(indexName, documentId, null, true, cancellationToken);

    private async Task DeleteCoreAsync(string indexName, string documentId, string? culture, bool allVariations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_tables.TryGetValue(indexName, out var table) == false)
            return;

        await WithTableAsync(table, async () =>
        {
            await using var command = _dataSource.CreateCommand($"DELETE FROM {Qualified(table)} WHERE document_id = $1" +
                (allVariations ? string.Empty : " AND variation_key = $2"));
            command.Parameters.AddWithValue(documentId);

            if (allVariations == false)
                command.Parameters.AddWithValue(NormalizeVariation(culture));

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture = null,
        int topK = 10, CancellationToken cancellationToken = default) =>
        SearchAsync(indexName, queryVector, culture, topK, null, cancellationToken);

    /// <summary>Returns the highest cosine scores across the requested variation and its fallbacks, with typed metadata filters.</summary>
    public async Task<IReadOnlyList<AIVectorSearchResult>> SearchAsync(string indexName, ReadOnlyMemory<float> queryVector, string? culture,
        int topK, IReadOnlyDictionary<string, IReadOnlyCollection<object?>?>? payloadFilters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (topK <= 0 || _tables.TryGetValue(indexName, out var table) == false)
            return [];

        ValidateVector(queryVector);

        try
        {
            return await WithTableAsync<IReadOnlyList<AIVectorSearchResult>>(table, async () =>
            {
                ValidateStoredDimensions(table);
                await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

                // Iterative scans prevent selective metadata/variation filters from exhausting the first HNSW candidates.
                await using (var settings = new NpgsqlCommand("SET LOCAL hnsw.iterative_scan = strict_order", connection, transaction))
                    await settings.ExecuteNonQueryAsync(cancellationToken);

                await using var command = new NpgsqlCommand(string.Empty, connection, transaction);
                command.Parameters.AddWithValue(new Vector(queryVector));
                command.Parameters.AddWithValue(AiVariationKey.SearchKeys(culture).Select(NormalizeVariation).ToArray());
                command.Parameters.AddWithValue(topK);
                var predicates = new List<string> { "variation_key = ANY($2)" };

                if (payloadFilters is not null)
                {
                    foreach (var (key, values) in payloadFilters)
                    {
                        if (string.IsNullOrWhiteSpace(key) || values is null)
                            continue;

                        var alternatives = new List<string>();

                        foreach (var value in values.Distinct())
                        {
                            if (value is null)
                                continue;

                            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new Dictionary<string, object> { [key] = value }));
                            var scalarParameter = command.Parameters.Count;
                            command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(new Dictionary<string, object> { [key] = new[] { value } }));
                            alternatives.Add($"(metadata @> ${scalarParameter} OR metadata @> ${command.Parameters.Count})");
                        }

                        if (alternatives.Count > 0)
                            predicates.Add("(" + string.Join(" OR ", alternatives) + ")");
                    }
                }

                command.CommandText = $"""
                    SELECT document_id, 1 - (embedding <=> $1) AS score, metadata::text
                    FROM {Qualified(table)} WHERE {string.Join(" AND ", predicates)}
                    ORDER BY embedding <=> $1 LIMIT $3
                    """;
                var results = new List<AIVectorSearchResult>();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken))
                        results.Add(new AIVectorSearchResult(reader.GetString(0), (float)reader.GetDouble(1), PostgresMetadata.Read(reader.GetString(2))));

                await transaction.CommitAsync(cancellationToken);
                return results;
            }, cancellationToken);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested == false && PostgresTransientFailure.IsTransient(exception))
        {
            _logger.LogWarning(exception, "Postgres is unavailable; vector search for {IndexName} returned no results", indexName);
            return [];
        }
    }

    /// <summary>Reads all chunks, or the exact requested variation, without a row-count limit.</summary>
    public async Task<IReadOnlyList<AIVectorEntry>> GetVectorsByDocumentAsync(string indexName, string documentId, string? culture = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_tables.TryGetValue(indexName, out var table) == false)
            return [];

        return await WithTableAsync<IReadOnlyList<AIVectorEntry>>(table, async () =>
        {
            await using var command = _dataSource.CreateCommand($"""
                SELECT culture, segment, chunk_index, embedding, metadata::text
                FROM {Qualified(table)} WHERE document_id = $1
                """ + (string.IsNullOrWhiteSpace(culture) ? string.Empty : " AND variation_key = $2") + " ORDER BY chunk_index, variation_key");
            command.Parameters.AddWithValue(documentId);

            if (string.IsNullOrWhiteSpace(culture) == false)
                command.Parameters.AddWithValue(NormalizeVariation(culture));

            var results = new List<AIVectorEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var variation = AiVariationKey.Create(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
                results.Add(new AIVectorEntry(documentId, variation, reader.GetInt32(2), reader.GetFieldValue<Vector>(3).ToArray(), PostgresMetadata.Read(reader.GetString(4))));
            }

            return results;
        }, cancellationToken);
    }

    public async Task<long> GetDocumentCountAsync(string indexName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_tables.TryGetValue(indexName, out var table) == false)
            return 0;

        return await WithTableAsync(table, async () =>
        {
            await using var command = _dataSource.CreateCommand($"SELECT COUNT(DISTINCT document_id) FROM {Qualified(table)}");
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }, cancellationToken);
    }

    /// <summary>Clears only this category's rows, adapting dimensions only on explicit reset.</summary>
    public async Task ResetAsync(string indexName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_tables.TryGetValue(indexName, out var table) == false)
            return;

        await EnsureTableAsync(table, cancellationToken);
        var tableLock = _tableLocks.GetOrAdd(table, _ => new SemaphoreSlim(1, 1));
        await tableLock.WaitAsync(cancellationToken);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await LockTableSetupAsync(connection, transaction, table, cancellationToken);
            await ValidateOwnershipAsync(connection, transaction, table, cancellationToken);
            _logger.LogWarning("Resetting Postgres category table {Table}; its vector data will be cleared", Qualified(table));
            await using var command = new NpgsqlCommand($"""
                TRUNCATE TABLE {Qualified(table)};
                DROP INDEX IF EXISTS {Quote(_options.Connection.SchemaName)}.{Quote(IndexName(table, "vector"))};
                ALTER TABLE {Qualified(table)} ALTER COLUMN embedding TYPE vector({_options.Connection.EmbeddingSize});
                {CreateIndexesSql(table, (int)_options.Connection.EmbeddingSize)}
                """, connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _dimensions[table] = (int)_options.Connection.EmbeddingSize;
        }
        finally
        {
            tableLock.Release();
        }
    }

    private async Task<T> WithTableAsync<T>(string table, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await EnsureTableAsync(table, cancellationToken);

        try
        {
            return await operation();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _dimensions.TryRemove(table, out _);
            await EnsureTableAsync(table, cancellationToken);
            return await operation();
        }
    }

    private async Task EnsureTableAsync(string table, CancellationToken cancellationToken)
    {
        if (_dimensions.ContainsKey(table))
            return;

        var tableLock = _tableLocks.GetOrAdd(table, _ => new SemaphoreSlim(1, 1));
        await tableLock.WaitAsync(cancellationToken);

        try
        {
            if (_dimensions.ContainsKey(table))
                return;

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var extension = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector') AND to_regtype('vector') IS NOT NULL", connection, transaction))
                if (await extension.ExecuteScalarAsync(cancellationToken) is not true)
                    throw new InvalidOperationException("The administrator must enable pgvector in this database and make its schema visible through search_path before using semantic search.");

            await LockTableSetupAsync(connection, transaction, table, cancellationToken);
            await using var exists = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection, transaction);
            exists.Parameters.AddWithValue(Qualified(table));

            if (await exists.ExecuteScalarAsync(cancellationToken) is true)
                await ValidateOwnershipAsync(connection, transaction, table, cancellationToken);
            else
            {
                await using var create = new NpgsqlCommand($"""
                    CREATE TABLE {Qualified(table)} (
                        document_id text NOT NULL,
                        variation_key text NOT NULL,
                        culture text,
                        segment text,
                        chunk_index integer NOT NULL CHECK (chunk_index >= 0),
                        embedding vector({_options.Connection.EmbeddingSize}) NOT NULL,
                        metadata jsonb NOT NULL DEFAULT jsonb_build_object(),
                        PRIMARY KEY (document_id, variation_key, chunk_index)
                    );
                    COMMENT ON TABLE {Qualified(table)} IS '{TableMarker}';
                    """, connection, transaction);
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var dimensions = new NpgsqlCommand("SELECT atttypmod FROM pg_attribute WHERE attrelid = to_regclass($1) AND attname = 'embedding' AND atttypid = to_regtype('vector')", connection, transaction);
            dimensions.Parameters.AddWithValue(Qualified(table));
            var actual = await dimensions.ExecuteScalarAsync(cancellationToken);

            if (actual is not int size || size <= 0)
                throw new InvalidOperationException($"Table {Qualified(table)} must have a dimensioned pgvector embedding column.");

            if (size != (int)_options.Connection.EmbeddingSize)
                _logger.LogWarning("Preserved table {Table} with {ActualSize} dimensions; configuration expects {ExpectedSize}. Explicitly reset this index to change dimensions.", Qualified(table), size, _options.Connection.EmbeddingSize);

            await using var indexes = new NpgsqlCommand(CreateIndexesSql(table, size), connection, transaction);
            await indexes.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            _dimensions[table] = size;
        }
        finally
        {
            tableLock.Release();
        }
    }

    private async Task ValidateOwnershipAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT obj_description(to_regclass($1), 'pg_class')", connection, transaction);
        command.Parameters.AddWithValue(Qualified(table));

        if (await command.ExecuteScalarAsync(cancellationToken) is not string marker || marker != TableMarker)
            throw new InvalidOperationException($"Table {Qualified(table)} already exists and is not managed by this package. Choose another category name or schema.");
    }

    private async Task LockTableSetupAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1, 0))", connection, transaction);
        command.Parameters.AddWithValue(Qualified(table));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string CreateIndexesSql(string table, int dimensions) => $"""
        CREATE INDEX IF NOT EXISTS {Quote(IndexName(table, "variation"))} ON {Qualified(table)} (variation_key);
        CREATE INDEX IF NOT EXISTS {Quote(IndexName(table, "metadata"))} ON {Qualified(table)} USING gin (metadata jsonb_path_ops);
        """ + (dimensions <= 2000
        ? $"CREATE INDEX IF NOT EXISTS {Quote(IndexName(table, "vector"))} ON {Qualified(table)} USING hnsw (embedding vector_cosine_ops);"
        : string.Empty);

    private string IndexName(string table, string kind) => "umb_ai_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Qualified(table))))[..16].ToLowerInvariant() + "_" + kind;

    private string Qualified(string table) => Quote(_options.Connection.SchemaName) + "." + Quote(table);
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static string NormalizeVariation(string? variation)
    {
        var (culture, segment) = AiVariationKey.Parse(variation);
        return AiVariationKey.Create(culture, segment)?.ToLowerInvariant() ?? string.Empty;
    }

    private static void ValidateIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);

        if (Encoding.UTF8.GetByteCount(identifier) > 63 || identifier.Contains('\0'))
            throw new ArgumentException("Postgres identifiers must contain at most 63 UTF-8 bytes and no null characters.", nameof(identifier));
    }

    private void ValidateStoredDimensions(string table)
    {
        if (_dimensions[table] != (int)_options.Connection.EmbeddingSize)
            throw new InvalidOperationException($"Table {Qualified(table)} has different embedding dimensions. Explicitly reset the index to use the configured size.");
    }

    private void ValidateVector(ReadOnlyMemory<float> vector)
    {
        if (vector.Length != (int)_options.Connection.EmbeddingSize)
            throw new ArgumentException($"Vector must have {_options.Connection.EmbeddingSize} dimensions.", nameof(vector));

        var nonzero = false;

        for (var index = 0; index < vector.Length; index++)
        {
            if (float.IsFinite(vector.Span[index]) == false)
                throw new ArgumentException("Vector values must be finite.", nameof(vector));

            nonzero |= vector.Span[index] != 0;
        }

        if (nonzero == false)
            throw new ArgumentException("Cosine search requires a nonzero vector.", nameof(vector));
    }
}
