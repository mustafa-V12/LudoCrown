using LudoCrown.Server.Data;
using LudoCrown.Server.Options;
using LudoCrown.Server.Postgres;
using LudoCrown.Server.Store;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LudoCrown.Postgres.Tests;

/// <summary>The real PurchaseService against PostgreSQL, so table constraints are exercised for every purchase state.</summary>
public class PostgresPurchaseFlowTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("LUDOCROWN_TEST_POSTGRES");
    private static readonly SemaphoreSlim MigrateOnce = new(1, 1);
    private static bool _migrated;

    private static async Task<(PostgresStore Store, PurchaseService Purchases)?> SetupAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString)) { Console.WriteLine("  (skipped: LUDOCROWN_TEST_POSTGRES not set)"); return null; }
        var db = NpgsqlDataSource.Create(ConnectionString);
        await MigrateOnce.WaitAsync();
        try { if (!_migrated) { await new MigrationRunner(db, NullLogger<MigrationRunner>.Instance).MigrateAsync(); _migrated = true; } }
        finally { MigrateOnce.Release(); }
        var store = new PostgresStore(db, TimeProvider.System);
        var service = new PurchaseService(new IPurchaseVerifier[] { new DevSandboxVerifier() }, store, store,
            Microsoft.Extensions.Options.Options.Create(new StoreOptions()), TimeProvider.System, NullLogger<PurchaseService>.Instance);
        return (store, service);
    }

    private static string Tx() => "devtx-" + Guid.NewGuid();

    [Fact]
    public async Task Pending_then_verified_purchase_is_credited_once()
    {
        if (await SetupAsync() is not var (store, purchases)) return;
        var u = await store.CreateUserAsync(false, "PgBuyer", "lion");
        var before = (await store.GetBalancesAsync(u.Id))!.Coins;
        var tx = Tx();

        var pending = await purchases.ProcessAsync(u.Id, new PurchaseClaim(PurchaseStores.DevSandbox, "coins_small", tx, "pending"));
        Assert.Equal(PurchaseOutcome.Pending, pending.Outcome);
        Assert.Equal(before, (await store.GetBalancesAsync(u.Id))!.Coins);

        var credited = await purchases.ProcessAsync(u.Id, new PurchaseClaim(PurchaseStores.DevSandbox, "coins_small", tx));
        Assert.Equal(PurchaseOutcome.Credited, credited.Outcome);
        Assert.Equal(before + 5000, (await store.GetBalancesAsync(u.Id))!.Coins);

        var again = await purchases.ProcessAsync(u.Id, new PurchaseClaim(PurchaseStores.DevSandbox, "coins_small", tx));
        Assert.Equal(PurchaseOutcome.AlreadyCredited, again.Outcome);
        Assert.Equal(before + 5000, (await store.GetBalancesAsync(u.Id))!.Coins);
    }

    [Fact]
    public async Task Rejected_purchase_credits_nothing_and_cannot_be_claimed_by_someone_else()
    {
        if (await SetupAsync() is not var (store, purchases)) return;
        var a = await store.CreateUserAsync(false, "PgA", "lion");
        var b = await store.CreateUserAsync(false, "PgB", "lion");
        var gems = (await store.GetBalancesAsync(a.Id))!.Gems;

        var failed = await purchases.ProcessAsync(a.Id, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_small", Tx(), "fail"));
        Assert.Equal(PurchaseOutcome.Rejected, failed.Outcome);
        Assert.Equal(gems, (await store.GetBalancesAsync(a.Id))!.Gems);

        var tx = Tx();
        Assert.Equal(PurchaseOutcome.Credited, (await purchases.ProcessAsync(a.Id, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_small", tx))).Outcome);
        var theft = await purchases.ProcessAsync(b.Id, new PurchaseClaim(PurchaseStores.DevSandbox, "gems_small", tx));
        Assert.Equal(PurchaseOutcome.Rejected, theft.Outcome);
        Assert.Equal(gems + 50, (await store.GetBalancesAsync(a.Id))!.Gems);
    }

    [Fact]
    public async Task Gold_purchase_is_idempotent_per_request()
    {
        if (await SetupAsync() is not var (store, purchases)) return;
        var u = await store.CreateUserAsync(false, "PgGold", "lion");
        await store.ApplyAsync(new EconomyOperation(u.Id, "seed:" + Guid.NewGuid(), Economy.Reasons.AdminAdjustment, new[] { new AssetChange(Economy.Coins, 3000) }, DateTimeOffset.UtcNow));
        var req = Guid.NewGuid().ToString();
        Assert.Equal(BuyOutcome.Bought, (await purchases.BuyWithCoinsAsync(u.Id, "shield_single", req)).Outcome);
        Assert.Equal(BuyOutcome.AlreadyBought, (await purchases.BuyWithCoinsAsync(u.Id, "shield_single", req)).Outcome);
        var bal = (await store.GetBalancesAsync(u.Id))!;
        Assert.Equal(1000, bal.Coins);
        Assert.Equal(BuyOutcome.NotEnoughCoins, (await purchases.BuyWithCoinsAsync(u.Id, "shield_single", Guid.NewGuid().ToString())).Outcome);
    }
}
