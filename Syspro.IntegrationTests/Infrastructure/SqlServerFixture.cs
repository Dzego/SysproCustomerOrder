using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Syspro.IntegrationTests.Infrastructure;

// Owns one disposable SQL Server container for the whole test run. This is the
// only source of connection strings in this project, so these tests cannot reach
// a developer or production database.
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Pinned so that every run tests against the same engine version.
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private const int LogLinesOnFailure = 30;

    private MsSqlContainer? _container;

    public async Task InitializeAsync()
    {
        // xUnit creates this fixture even when every test is skipped.
        if (DockerAvailability.ShouldSkip)
        {
            return;
        }

        _container = new MsSqlBuilder(Image).Build();

        var timer = Stopwatch.StartNew();

        try
        {
            await _container.StartAsync();
        }
        catch (Exception exception)
        {
            // Every test depends on this, so a failure here fails them all at once. Keep
            // what explains it, because the container is removed when the fixture ends.
            throw new InvalidOperationException(
                $"The SQL Server container failed to start after {timer.Elapsed.TotalSeconds:F1} s. " +
                $"Its state was {_container.State}. Its last log lines were:\n{await ReadLogTailAsync(_container)}",
                exception);
        }

        Console.WriteLine($"SQL Server container was ready after {timer.Elapsed.TotalSeconds:F1} s.");
    }

    private static async Task<string> ReadLogTailAsync(MsSqlContainer container)
    {
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync();

            return string.Join('\n', (stdout + stderr).Split('\n').TakeLast(LogLinesOnFailure));
        }
        catch (Exception exception)
        {
            return $"(The logs could not be read: {exception.Message})";
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public string CreateConnectionString(string databaseName)
    {
        if (_container is null)
        {
            throw new InvalidOperationException("The SQL Server container is not running.");
        }

        return new SqlConnectionStringBuilder(_container.GetConnectionString())
        {
            InitialCatalog = databaseName
        }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
