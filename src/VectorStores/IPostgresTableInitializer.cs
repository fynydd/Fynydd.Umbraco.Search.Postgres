namespace Fynydd.Umbraco.Search.Postgres.VectorStores;

/// <summary>Prepares category tables without modifying existing vector data.</summary>
public interface IPostgresTableInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
