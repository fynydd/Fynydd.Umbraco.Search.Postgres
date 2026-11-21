using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Pgvector.Npgsql;

namespace Umbraco.Search.Postgres.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("pgvector/pgvector:0.8.7-pg18-trixie")
        .WithEnvironment("POSTGRES_DB", "search_tests")
        .WithEnvironment("POSTGRES_USER", "postgres")
        .WithEnvironment("POSTGRES_PASSWORD", "local_test_password")
        .WithPortBinding(5432, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(5432))
        .Build();

    public string ConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1",
        Port = _container.GetMappedPublicPort(5432),
        Database = "search_tests",
        Username = "postgres",
        Password = "local_test_password",
        CommandTimeout = 10,
        Timeout = 5,
        SslMode = SslMode.Disable
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("CREATE EXTENSION vector", connection);
        await command.ExecuteNonQueryAsync();
    }

    public NpgsqlDataSource CreateDataSource(string? connectionString = null)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString ?? ConnectionString);
        builder.UseVector();
        return builder.Build();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
