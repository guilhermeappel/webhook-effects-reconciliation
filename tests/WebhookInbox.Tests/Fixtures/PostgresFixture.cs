using Npgsql;
using Testcontainers.PostgreSql;

namespace WebhookInbox.Tests;

/// <summary>
/// One Postgres server shared by every test class that uses the <see cref="SharedPostgres"/> collection, instead
/// of a container per test. Starting a container is by far the slowest step of a test; a test that needs a
/// database asks for a fresh one, so tests still share no state.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(TestImages.Postgres).Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database with a unique name and returns the connection string for it.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        // Every database has its own connection pool, which keeps idle connections open after the test that
        // used it has finished; left alone they add up across a run and exhaust the server's connection limit.
        // Tests run one at a time, so whatever is pooled at this point belongs to a test that is over.
        NpgsqlConnection.ClearAllPools();

        var name = $"test_{Guid.NewGuid():N}";
        var server = _container.GetConnectionString();

        await using (var connection = new NpgsqlConnection(server))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(server) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class SharedPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
