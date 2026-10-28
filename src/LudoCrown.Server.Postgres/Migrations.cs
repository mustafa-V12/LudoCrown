using LudoCrown.Server.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LudoCrown.Server.Postgres;

/// <summary>
/// Applies the embedded db/migrations/*.sql files in name order, each in its own transaction, recording
/// them in schema_migrations. An advisory lock prevents two server instances migrating at once.
/// Runs at startup when Database:MigrateOnStartup is true (default in Development). In production you
/// can instead run the SQL files with psql as the migrator role from your deployment pipeline.
/// </summary>
public sealed class MigrationRunner(NpgsqlDataSource db, ILogger<MigrationRunner> logger)
{
    public static IReadOnlyList<(string Version, string Sql)> LoadEmbedded()
    {
        var asm = typeof(MigrationRunner).Assembly;
        return asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n =>
            {
                using var stream = asm.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return (n["migrations.".Length..^".sql".Length], reader.ReadToEnd());
            })
            .ToList();
    }

    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using (var create = new NpgsqlCommand(Sql.CreateMigrationsTable, conn)) await create.ExecuteNonQueryAsync(ct);

        var applied = 0;
        foreach (var (version, sql) in LoadEmbedded())
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var lockCmd = new NpgsqlCommand(Sql.MigrationLock, conn, tx)) await lockCmd.ExecuteNonQueryAsync(ct);

            var done = new HashSet<string>();
            await using (var q = new NpgsqlCommand(Sql.GetAppliedMigrations, conn, tx))
            await using (var r = await q.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) done.Add(r.GetString(0));

            if (done.Contains(version)) { await tx.RollbackAsync(ct); continue; }

            logger.LogInformation("Applying migration {Version}", version);
            await using (var m = new NpgsqlCommand(sql, conn, tx)) await m.ExecuteNonQueryAsync(ct);
            await using (var rec = new NpgsqlCommand(Sql.RecordMigration, conn, tx))
            {
                rec.Parameters.Add(new NpgsqlParameter { Value = version });
                await rec.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            applied++;
        }
        return applied;
    }
}

/// <summary>Runs migrations (optional) and deletes expired auth codes/tokens and old match records daily.</summary>
public sealed class PostgresMaintenanceService(MigrationRunner migrations, NpgsqlDataSource db, PostgresOptions options, TimeProvider time, ILogger<PostgresMaintenanceService> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.MigrateOnStartup)
        {
            var n = await migrations.MigrateAsync(cancellationToken);
            logger.LogInformation("Database migrations applied: {Count}", n);
        }
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                var now = time.GetUtcNow();
                await using var conn = await db.OpenConnectionAsync(stoppingToken);
                await Exec(conn, Sql.PurgeAuthCodes, now.AddDays(-1), stoppingToken);
                await Exec(conn, Sql.PurgeRefreshTokens, now.AddDays(-7), stoppingToken);
                if (options.MatchRetentionDays > 0)
                    await Exec(conn, Sql.PurgeOldMatches, now.AddDays(-options.MatchRetentionDays), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Database maintenance failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, DateTimeOffset cutoff, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = cutoff.ToUniversalTime() });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

public sealed class PostgresOptions
{
    public bool MigrateOnStartup { get; set; }
    /// <summary>Finished matches older than this are deleted (0 keeps them forever). Default: 2 years.</summary>
    public int MatchRetentionDays { get; set; } = 730;
}

public static class PostgresServiceCollectionExtensions
{
    public static IServiceCollection AddLudoPostgres(this IServiceCollection services, string connectionString, PostgresOptions? options = null)
    {
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton(options ?? new PostgresOptions());
        services.AddSingleton<PostgresStore>();
        services.AddSingleton<IAccountStore>(sp => sp.GetRequiredService<PostgresStore>());
        services.AddSingleton<IProfileStore>(sp => sp.GetRequiredService<PostgresStore>());
        services.AddSingleton<IMatchStore>(sp => sp.GetRequiredService<PostgresStore>());
        services.AddSingleton<ISocialStore>(sp => sp.GetRequiredService<PostgresStore>());
        services.AddSingleton<IWalletStore>(sp => sp.GetRequiredService<PostgresStore>());
        services.AddSingleton<MigrationRunner>();
        services.AddHostedService<PostgresMaintenanceService>();
        services.AddHealthChecks().AddCheck<PostgresHealthCheck>("postgres");
        return services;
    }
}

public sealed class PostgresHealthCheck(NpgsqlDataSource db) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Database unreachable", ex);
        }
    }
}
