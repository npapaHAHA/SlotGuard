using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace SlotGuard.Tests;

public sealed class SlotGuardFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string? _externalConnection;
    private string? _schema;
    private WebApplicationFactory<Program>? _host;

    public static readonly DateTimeOffset Now = new(2026, 5, 11, 5, 30, 0, TimeSpan.Zero);
    public IServiceProvider Services => _host!.Services;

    public async Task InitializeAsync()
    {
        string connectionString;
        _externalConnection = Environment.GetEnvironmentVariable("SLOTGUARD_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(_externalConnection))
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
            connectionString = _container.GetConnectionString();
        }
        else
        {
            // Use a fresh schema to isolate each test run in a local PostgreSQL instance.
            var schema = $"slotguard_test_{Guid.NewGuid():N}";
            await ExecuteInExternalDatabase($"CREATE SCHEMA \"{schema}\"");
            _schema = schema;
            connectionString = new NpgsqlConnectionStringBuilder(_externalConnection) { SearchPath = schema }.ToString();
        }

        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["ConnectionStrings:SlotGuard"] = connectionString }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new FixedClock());
                services.AddDataProtection().UseEphemeralDataProtectionProvider();
            });
        });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
        if (_schema is not null)
            await ExecuteInExternalDatabase($"DROP SCHEMA \"{_schema}\" CASCADE");
    }

    public HttpClient CreateClient() => _host!.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false
    });

    public async Task<HttpClient> CreateGuest()
    {
        var client = CreateClient();
        using var response = await client.PostAsync("/api/session", null);
        response.EnsureSuccessStatusCode();
        return client;
    }

    private async Task ExecuteInExternalDatabase(string sql)
    {
        await using var connection = new NpgsqlConnection(_externalConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
