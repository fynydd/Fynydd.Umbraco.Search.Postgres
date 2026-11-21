namespace Fynydd.Umbraco.Search.Postgres.Indexers;

/// <summary>
/// Semantic-search settings bound from the Umbraco:AI:Search:Postgres configuration section.
/// </summary>
public sealed class AiSearchIndexFilterOptions
{
    /// <summary>
    /// Postgres connection and table-maintenance settings.
    /// </summary>
    public PostgresConnectionOptions Connection { get; set; } = new();

    /// <summary>
    /// Disables the default AI search index so only explicitly configured category indexes are registered.
    /// </summary>
    public bool DisableDefaultIndex { get; set; } = true;

    /// <summary>
    /// Named semantic-search categories, such as Documentation, each with its own query and indexing settings.
    /// </summary>
    public Dictionary<string, SearchCategory> Categories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Connection settings for the administrator-provisioned Postgres database.
/// </summary>
public sealed class PostgresConnectionOptions
{
    public string ServerAddress { get; set; } = "localhost";
    public int ServerPort { get; set; } = 5432;
    public string DatabaseName { get; set; } = "umbraco_search";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = string.Empty;
    public Npgsql.SslMode SslMode { get; set; } = Npgsql.SslMode.Prefer;
    public string SchemaName { get; set; } = "public";
    public ulong EmbeddingSize { get; set; } = 1024;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int InitializationTimeoutSeconds { get; set; } = 5;
}

/// <summary>
/// Combines semantic-search query behavior with the content type indexing rules searched by that category.
/// </summary>
public sealed class SearchCategory
{
    /// <summary>
    /// Umbraco search index alias backed by a Postgres category table.
    /// </summary>
    public string IndexAlias { get; set; } = "UmbAI_Search";

    /// <summary>
    /// Embedding profile alias intended for embedding search queries.
    /// </summary>
    public string EmbeddingsProfileAlias { get; set; } = "embeddings";

    /// <summary>
    /// Maximum vector chunk matches requested before score filtering and document grouping.
    /// </summary>
    public int TopK { get; set; } = 10;

    /// <summary>
    /// Maximum result documents rendered for this category after grouping.
    /// </summary>
    public int Take { get; set; } = 10;

    /// <summary>
    /// Minimum cosine similarity score allowed for vector results in this category.
    /// </summary>
    public double? MinScore { get; set; }

    /// <summary>
    /// Optional token chunk-size override for all indexing rules in this category.
    /// </summary>
    public int? ChunkSize { get; set; }

    /// <summary>
    /// Optional token overlap between adjacent chunks for all indexing rules in this category.
    /// </summary>
    public int? ChunkOverlap { get; set; }

    /// <summary>
    /// Per-document-type indexing configurations included in this search category.
    /// </summary>
    public List<SearchIndexDocument> Indexing { get; set; } = [];
}

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedMember.Global
/// <summary>
/// Controls how one or more Umbraco document types are rendered into semantic-search text.
/// </summary>
public sealed class SearchIndexDocument
{
    /// <summary>
    /// Umbraco document type aliases handled by this indexing configuration.
    /// </summary>
    public List<string> DocumentTypeAliases { get; set; } = [];

    /// <summary>
    /// Markdown template and weighted field settings used to build searchable text.
    /// </summary>
    public SearchTextOptions SearchText { get; set; } = new();

    /// <summary>
    /// Chunk splitting settings and context repeated on each chunk.
    /// </summary>
    public SearchChunkingOptions Chunking { get; set; } = new();
}

/// <summary>
/// Controls which source text is rendered and embedded for semantic search.
/// </summary>
public sealed class SearchTextOptions
{
    /// <summary>
    /// Markdown template rendered once per document. Tokens use property aliases, dotted picker or block-list paths, pipe-delimited fallbacks, optional trailing weights like :2, and built-ins like Name, Breadcrumb, Url, and ContentType.
    /// </summary>
    public string MarkdownTemplate { get; set; } = string.Empty;
}

/// <summary>
/// Controls how rendered Markdown is split into token-limited chunks.
/// </summary>
public sealed class SearchChunkingOptions
{
    /// <summary>
    /// Optional per-document-type token chunk-size override.
    /// </summary>
    public int? ChunkSize { get; set; }

    /// <summary>
    /// Optional per-document-type token overlap between adjacent chunks.
    /// </summary>
    public int? ChunkOverlap { get; set; }

    /// <summary>
    /// When true, splits Markdown at headings before token chunking so heading context is preserved.
    /// </summary>
    public bool? UseHeadingAwareChunking { get; set; }

    /// <summary>
    /// Stable document context repeated on every generated chunk.
    /// </summary>
    public SearchChunkContextOptions Context { get; set; } = new();
}

/// <summary>
/// Property aliases used as repeated chunk context without requiring those properties in the Markdown template.
/// </summary>
public sealed class SearchChunkContextOptions
{
    /// <summary>
    /// Category or taxonomy property aliases repeated as chunk context and stored as result metadata.
    /// </summary>
    public List<string> CategoryPropertyAliases { get; set; } = [];

    /// <summary>
    /// Title property aliases repeated as H1 Markdown context on each chunk.
    /// </summary>
    public List<string> TitlePropertyAliases { get; set; } = [];

    /// <summary>
    /// Section-title property aliases repeated as H2 Markdown context on each chunk.
    /// </summary>
    public List<string> SectionTitlePropertyAliases { get; set; } = [];

    /// <summary>
    /// Additional property aliases repeated as plain Markdown context on each chunk.
    /// </summary>
    public List<string> AdditionalPropertyAliases { get; set; } = [];
}

