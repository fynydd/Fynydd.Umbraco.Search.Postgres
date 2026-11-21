# Fynydd.Umbraco.Search.Postgres

Postgres with pgvector storage for Umbraco 18 semantic search. This package preserves the source Umbraco.Search package's configurable indexing and search pipeline, replacing its vector storage backend with native Postgres tables and queries.

## Features

- Automatic registration with `Umbraco.AI.Search` and Umbraco content/media search.
- Independent category tables, with culture and segment variations stored in rows.
- Markdown templates, weighted properties, fallbacks, dotted picker/block paths, breadcrumbs, and heading-aware chunking.
- Category and document-type chunk size/overlap overrides and repeated chunk context.
- Optional nested text replacements before embedding.
- Cosine search, typed metadata filters, snippets, access metadata, and document grouping/paging.
- Transactional document replacement: a failed vector write preserves existing chunks.
- Startup preserves existing data. Only an explicit index reset clears a category.
- Bounded startup waits and graceful handling of temporary database outages.

This package is for new installations. It does not migrate another vector store's data.

## Requirements

- .NET 10 and Umbraco CMS 18.
- `Umbraco.AI.Search` and a configured AI connection/default embedding profile.
- Postgres 13+ with pgvector 0.8.0+ installed and enabled by the administrator.
- A database role with schema `USAGE` and `CREATE`, owning the tables this package creates.

The package never creates databases or enables extensions. It creates and manages its category tables, indexes, and vector rows. The configured schema must already exist, and the extension's schema must be visible in the role's `search_path`.

Administrator setup, run against the intended search database:

```sql
CREATE EXTENSION IF NOT EXISTS vector;
GRANT CONNECT ON DATABASE umbraco_search TO umbraco_search;
GRANT USAGE, CREATE ON SCHEMA public TO umbraco_search;
```

Create the database and login role separately using your normal administration process. Umbraco's own CMS database can continue using its existing database provider.

## Install and configure

```sh
dotnet add package Fynydd.Umbraco.Search.Postgres
```

Configure the AI connection and a default embedding profile in the Umbraco backoffice. Match `EmbeddingSize` to that model's output dimension.

Merge [appsettings.example.json](appsettings.example.json) into your site's settings. A minimal configuration is:

```json
{
    "Umbraco": {
        "AI": {
            "Search": {
                "Postgres": {
                    "DisableDefaultIndex": true,
                    "Connection": {
                        "ServerAddress": "localhost",
                        "ServerPort": 5432,
                        "DatabaseName": "umbraco_search",
                        "Username": "umbraco_search",
                        "Password": "",
                        "SslMode": "Prefer",
                        "SchemaName": "public",
                        "EmbeddingSize": 1024,
                        "RequestTimeoutSeconds": 30,
                        "InitializationTimeoutSeconds": 5
                    },
                    "Categories": {
                        "Documentation": {
                            "IndexAlias": "Documentation",
                            "EmbeddingsProfileAlias": "embeddings",
                            "ChunkSize": 150,
                            "ChunkOverlap": 25,
                            "TopK": 150,
                            "Take": 15,
                            "MinScore": 0.2,
                            "Indexing": [
                                {
                                    "DocumentTypeAliases": ["docPage", "docsPage"],
                                    "SearchText": {
                                        "MarkdownTemplate": "# {headline|Name:3}\n\n{body}\n\n{Url}"
                                    },
                                    "Chunking": {
                                        "UseHeadingAwareChunking": true,
                                        "Context": {
                                            "TitlePropertyAliases": ["headline"]
                                        }
                                    }
                                }
                            ]
                        }
                    }
                }
            }
        }
    }
}
```

Set the password through user secrets, your secret store, or the environment variable `Umbraco__AI__Search__Postgres__Connection__Password`. For remote connections use an appropriate `SslMode`, such as `VerifyFull`, with trusted server certificates.

The package includes `appsettings-schema.Umbraco.Search.Postgres.json` for editor completion. Its schema fragment describes the `Postgres` object under `Umbraco:AI:Search`.

### Category tables and variations

`Categories:Documentation` creates `public."documentation"`; its `IndexAlias` connects Umbraco search calls to that table. Each category requires a distinct index alias. Table names are lowercase category keys, safely quoted as SQL identifiers. Names must fit Postgres's 63-byte identifier limit; long names are rejected rather than silently truncated.

Each row contains `document_id`, a normalized `variation_key`, `culture`, `segment`, `chunk_index`, `embedding`, and JSONB `metadata`. The document/variation/chunk key makes writes repeatable. Culture and segment matching is case insensitive, while their original values are retained in the row.

Search uses the same variation fallbacks as the source package: culture+segment, culture-only, segment-only, and invariant. Results remain ranked by cosine score across those variations.

The default `UmbAI_Search` content scan is disabled unless enabled or explicitly assigned to a category. When enabled without a category, it uses the `umbai_search` table. Calls to unconfigured index aliases are ignored.

The package marks the tables it creates and refuses to use a preexisting, unrelated table with the same name. Choose a dedicated schema or another category name if a name is already taken.

### Search and chunk settings

Markdown supports `{propertyAlias}`, `{headline|Name}`, `{headline:3}`, dotted paths such as `{author.Name}`, and built-ins including `{Breadcrumb}`, `{Url}`, and `{ContentType}`. A trailing weight repeats the corresponding text. Chunk context can include category, title, section title, and additional property aliases.

Chunk size/overlap use document-rule overrides, then category overrides, then the global `Umbraco:AI:Search` defaults. The registered Umbraco searcher retains the source package's global `DefaultTopK`/`MinScore`, access checks, best-chunk document grouping, and pagination. Category `TopK`, `Take`, `MinScore`, and `EmbeddingsProfileAlias` remain available to custom query code, as in the source package.

Embedding vectors must be finite, nonzero, and match `EmbeddingSize` (1–16000). Tables with up to 2000 dimensions receive a cosine HNSW index; larger vectors use exact cosine search. Metadata and variation indexes support filtering. HNSW is approximate; strict iterative scanning helps selective filters find enough matches.

## Populate and query

In the backoffice, open **Settings → Search**, then rebuild each configured category index. Publishing, updating, unpublishing, and deleting content use Umbraco's existing indexing lifecycle.

Use Umbraco's normal search abstraction, or inject `PostgresVectorStore` for direct vector searches and metadata filters:

```csharp
using Fynydd.Umbraco.Search.Postgres.Extensions;
using Fynydd.Umbraco.Search.Postgres.Indexers;
using Fynydd.Umbraco.Search.Postgres.VectorStores;
using Microsoft.Extensions.Options;
using Umbraco.AI.Core.Embeddings;

public sealed class DocumentationSearch(
    PostgresVectorStore store,
    IAIEmbeddingService embeddings,
    IOptions<AiSearchIndexFilterOptions> options)
{
    public async Task<IReadOnlyList<Umbraco.AI.Search.Core.VectorStore.AIVectorSearchResult>> SearchAsync(
        string query, string? culture, string? segment, CancellationToken cancellationToken)
    {
        var category = options.Value.Categories["Documentation"];
        var embedding = await embeddings.GenerateEmbeddingAsync(
            builder => builder.WithAlias(category.EmbeddingsProfileAlias), query, cancellationToken);
        var matches = await store.SearchAsync(
            category.IndexAlias, embedding.Vector, AiVariationKey.Create(culture, segment), category.TopK,
            new Dictionary<string, IReadOnlyCollection<object?>?>
            {
                ["documentTypeAlias"] = new object?[] { "docPage", "docsPage" }
            }, cancellationToken);

        return matches
            .Where(match => match.Score >= (category.MinScore ?? 0.2))
            .GroupBy(match => match.DocumentId)
            .Select(group => group.First())
            .Take(category.Take)
            .ToList();
    }
}
```

Direct store calls return vector chunks and metadata; apply published-content visibility and member-access checks before rendering them. The registered Umbraco searcher performs the source package's `accessIds` checks. Metadata filters combine different keys with AND and values within a key with OR. String, numeric, and boolean types remain distinct; array metadata matches individual elements. Null/empty filter lists are ignored. Returned metadata includes `snippet`, `chunkText`, and other fields assembled by the indexer.

## Optional text replacements

Implement `Fynydd.Umbraco.Search.Postgres.Services.ITextReplacementProvider` and register it:

```csharp
builder.Services.AddSingleton<ITextReplacementProvider, MyTextReplacementProvider>();
```

`GetReplacements()` returns `IReadOnlyDictionary<string, string>`. Include any delimiters in each key. Replacements run before chunking and support nested values. The default provider returns an empty dictionary.

## Startup, recovery, and reset

Normal startup creates missing category tables and preserves rows. A dimension mismatch preserves the existing table and reports the mismatch; searches/writes requiring the new size fail until you explicitly reset and rebuild that index. Resets clear only the selected category and adapt its embedding column, preserving table grants.

Temporary connectivity failures do not stop Umbraco startup. Direct vector searches return no results; publishing/deletion vector updates are skipped and logged. Rebuild affected indexes after recovery. Configuration, missing extension, permission, and SQL errors remain visible. Caller-requested cancellation propagates from store operations.

Persist ASP.NET Core Data Protection keys for deployed Umbraco AI connections, following your application's hosting requirements. Otherwise connection secrets may become unreadable after a host restart.

## Build and test

```sh
dotnet build Umbraco.Search.Postgres.slnx
dotnet test Umbraco.Search.Postgres.slnx
./pack.sh
```

The tests include source indexing/search behavior, a SQLite Umbraco boot check, and disposable local Postgres/pgvector integration tests. Docker must be running locally. The integration suite uses `pgvector/pgvector:0.8.7-pg18-trixie`; its databases and extension are provisioned by the test fixture, separate from the package.

`pack.sh` and `pack.ps1` create the Release NuGet package in `src/nupkg`. `clean.sh` and `clean.ps1` remove local build output. The inherited Umbraco dependency graph currently reports ImageSharp 3.1.12 vulnerability warnings; these are not suppressed. Updating that dependency needs separate compatibility and licensing review.
