using Fynydd.Umbraco.Search.Postgres.Indexers;
using Fynydd.Umbraco.Search.Postgres.Searchers;
using Fynydd.Umbraco.Search.Postgres.Services;
using Fynydd.Umbraco.Search.Postgres.VectorStores;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.AI.Search.Startup;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;

namespace Fynydd.Umbraco.Search.Postgres.Composers;

/// <summary>Registers Postgres storage and the source package's Umbraco search pipeline.</summary>
[ComposeAfter(typeof(UmbracoAISearchComposer))]
public class PostgresVectorStoreComposer : IComposer
{
    private const string DataSourceKey = "Fynydd.Umbraco.Search.Postgres";

    public void Compose(IUmbracoBuilder builder)
    {
        builder.Services.AddOptions<AiSearchIndexFilterOptions>().BindConfiguration("Umbraco:AI:Search:Postgres");
        builder.Services.AddKeyedSingleton<NpgsqlDataSource>(DataSourceKey, (services, _) =>
        {
            var connection = services.GetRequiredService<IOptions<AiSearchIndexFilterOptions>>().Value.Connection;
            var connectionString = new NpgsqlConnectionStringBuilder
            {
                Host = connection.ServerAddress,
                Port = connection.ServerPort,
                Database = connection.DatabaseName,
                Username = connection.Username,
                Password = connection.Password,
                SslMode = connection.SslMode,
                Timeout = Math.Max(1, connection.RequestTimeoutSeconds),
                CommandTimeout = Math.Max(1, connection.RequestTimeoutSeconds)
            };
            var dataSource = new NpgsqlDataSourceBuilder(connectionString.ConnectionString);
            dataSource.UseVector();
            return dataSource.Build();
        });
        builder.Services.AddSingleton(services => new PostgresVectorStore(
            services.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey),
            services.GetRequiredService<IOptions<AiSearchIndexFilterOptions>>(),
            services.GetRequiredService<ILogger<PostgresVectorStore>>()));
        builder.Services.AddSingleton<IAIVectorStore>(services => services.GetRequiredService<PostgresVectorStore>());
        builder.Services.AddSingleton<IPostgresTableInitializer>(services => services.GetRequiredService<PostgresVectorStore>());
        builder.Services.AddHostedService<PostgresVectorStoreInitializer>();
        builder.Services.TryAddSingleton<ITextReplacementProvider, EmptyTextReplacementProvider>();
        builder.Services.AddTransient<FilteringAiVectorIndexer>();
        builder.Services.AddTransient<FilteringAiVectorSearcher>();
        builder.Services.AddOptions<IndexOptions>().Configure<IOptions<AiSearchIndexFilterOptions>>((options, searchOptions) =>
        {
            // A regular registration replaces the built-in content registration without scanning it.
            options.RegisterIndex<FilteringAiVectorIndexer, FilteringAiVectorSearcher>("UmbAI_Search");
            var aliases = searchOptions.Value.Categories.Values.Select(category => category.IndexAlias);

            if (searchOptions.Value.DisableDefaultIndex == false)
                aliases = aliases.Append("UmbAI_Search");

            foreach (var alias in aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                options.RegisterContentIndex<FilteringAiVectorIndexer, FilteringAiVectorSearcher, IPublishedContentChangeStrategy>(
                    alias, UmbracoObjectTypes.Document, UmbracoObjectTypes.Media);
        });
    }
}
