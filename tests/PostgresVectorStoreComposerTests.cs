using Fynydd.Umbraco.Search.Postgres.Composers;
using Fynydd.Umbraco.Search.Postgres.Indexers;
using Fynydd.Umbraco.Search.Postgres.Searchers;
using Fynydd.Umbraco.Search.Postgres.Services;
using Fynydd.Umbraco.Search.Postgres.VectorStores;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.AI.Search.Core.VectorStore;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Search.Core.Configuration;

namespace Umbraco.Search.Postgres.Tests;

public sealed class PostgresVectorStoreComposerTests
{
    [Fact]
    public void Compose_RegistersPostgresVectorStoreServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = CreateBuilder(services);

        new PostgresVectorStoreComposer().Compose(builder);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(PostgresVectorStore));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IAIVectorStore));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IPostgresTableInitializer));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(PostgresVectorStoreInitializer));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(ITextReplacementProvider));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FilteringAiVectorIndexer));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FilteringAiVectorSearcher));

        using var provider = services.BuildServiceProvider();
        var vectorStore = provider.GetRequiredService<PostgresVectorStore>();

        Assert.Same(vectorStore, provider.GetRequiredService<IAIVectorStore>());
        Assert.Same(vectorStore, provider.GetRequiredService<IPostgresTableInitializer>());
    }

    [Fact]
    public void Compose_RegistersDefaultIndexWhenNoCategoriesAreConfigured()
    {
        var services = new ServiceCollection();
        var builder = CreateBuilder(
            services,
            new Dictionary<string, string?>
            {
                ["Umbraco:AI:Search:Postgres:DisableDefaultIndex"] = "false"
            });

        new PostgresVectorStoreComposer().Compose(builder);

        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<IndexOptions>>().Value.GetContentIndexRegistrations();

        var registration = Assert.Single(registrations);
        Assert.Equal("UmbAI_Search", registration.IndexAlias);
        Assert.Equal(typeof(FilteringAiVectorIndexer), registration.Indexer);
        Assert.Equal(typeof(FilteringAiVectorSearcher), registration.Searcher);
    }

    [Fact]
    public void Compose_HonorsDefaultIndexDisablingWithConfiguredCategories()
    {
        var services = new ServiceCollection();
        var builder = CreateBuilder(
            services,
            new Dictionary<string, string?>
            {
                ["Umbraco:AI:Search:Postgres:DisableDefaultIndex"] = "true",
                ["Umbraco:AI:Search:Postgres:Categories:docs:IndexAlias"] = "Docs",
                ["Umbraco:AI:Search:Postgres:Categories:news:IndexAlias"] = "News"
            });

        new PostgresVectorStoreComposer().Compose(builder);

        using var provider = services.BuildServiceProvider();
        var registrations = provider.GetRequiredService<IOptions<IndexOptions>>().Value.GetContentIndexRegistrations();

        Assert.Equal(["Docs", "News"], registrations.Select(registration => registration.IndexAlias).Order());
        Assert.DoesNotContain(registrations, registration => registration.IndexAlias == "UmbAI_Search");
    }

    [Fact]
    public void Compose_SuppressesPreexistingDefaultContentIndex()
    {
        var services = new ServiceCollection();
        services.Configure<IndexOptions>(options => options.RegisterContentIndex<FilteringAiVectorIndexer, FilteringAiVectorSearcher, Umbraco.Cms.Search.Core.Services.ContentIndexing.IPublishedContentChangeStrategy>(
            "UmbAI_Search", Umbraco.Cms.Core.Models.UmbracoObjectTypes.Document));
        new PostgresVectorStoreComposer().Compose(CreateBuilder(services));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<IndexOptions>>().Value;

        Assert.Empty(options.GetContentIndexRegistrations());
        Assert.Null(options.GetContentIndexRegistration("UmbAI_Search"));
        Assert.Equal(typeof(FilteringAiVectorIndexer), options.GetIndexRegistration("UmbAI_Search")?.Indexer);
    }

    private static IUmbracoBuilder CreateBuilder(
        IServiceCollection services,
        Dictionary<string, string?>? configurationValues = null)
    {
        var builder = Substitute.For<IUmbracoBuilder>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues ?? new Dictionary<string, string?>())
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        builder.Services.Returns(services);
        builder.Config.Returns(configuration);

        return builder;
    }
}
