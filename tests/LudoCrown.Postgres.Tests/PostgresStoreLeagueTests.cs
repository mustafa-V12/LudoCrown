using LudoCrown.Core.Model;
using LudoCrown.Server.Data;
using LudoCrown.Server.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LudoCrown.Postgres.Tests;

/// <summary>Economy, purchases, profiles and league against a real PostgreSQL database (migration 0003).</summary>
public class PostgresEconomyLeagueTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("LUDOCROWN_TEST_POSTGRES");
    private static readonly SemaphoreSlim MigrateOnce = new(1, 1);
    private static bool _migrated;

    private static async Task<PostgresStore?> StoreAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString)) { Console.WriteLine("  (skipped: LUDOCROWN_TEST_POSTGRES not set)"); return null; }
        var db = NpgsqlDataSource.Create(ConnectionString);
        await MigrateOnce.WaitAsync();
        try { if (!_migrated) { await new MigrationRunner(db, NullLogger<MigrationRunner>.Instance).MigrateAsync(); _migrated = true; } }
        finally { MigrateOnce.Release(); }
        return new PostgresStore(db, TimeProvider.System);
    }

    private static EconomyOperation Op(Guid u, string key, params (string Asset, long Delta)[] changes) =>
        new(u, key, Economy.Reasons.AdminAdjustment, changes.Select(c => new AssetChange(c.Asset, c.Delta)).ToList(), DateTimeOffset.UtcNow);

    [Fact]
    public async Task Migration_0003_is_embedded()
    {
        Assert.Contains(MigrationRunner.LoadEmbedded(), m => m.Version == "0003_store_league_profiles");
        Assert.DoesNotContain(MigrationRunner.LoadEmbedded(), m => m.Version.Contains("down"));
    }

    [Fact]
    public async Task Economy_operations_are_atomic_idempotent_and_ledgered()
    {
        var s = await StoreAsync();
        if (s is null) return;
        var u = (await s.CreateUserAsync(false, "PgEco", "crown")).Id;

        Assert.Equal(EconomyOutcome.Insufficient, (await s.ApplyAsync(Op(u, "bad", (Economy.Coins, 100), (Economy.Gems, -1000)))).Outcome);
        Assert.Equal(0, await s.GetCoinsAsync(u)); // nothing applied
        Assert.Equal(EconomyOutcome.Insufficient, (await s.ApplyAsync(Op(u, "bad-item", (Boosts.ShieldId, -1)))).Outcome);

        var ok = Op(u, "grant-1", (Economy.Coins, 500), (Economy.Gems, 5), (Boosts.ShieldId, 2), (Items.FrameRoyal.Id, 1));
        var r = await s.ApplyAsync(ok);
        Assert.Equal(EconomyOutcome.Applied, r.Outcome);
        Assert.Equal((500L, Economy.StarterGems + 5L, 2, 1), (r.Balances.Coins, r.Balances.Gems, r.Balances.Count(Boosts.ShieldId), r.Balances.Count(Items.FrameRoyal.Id)));
        Assert.Equal(EconomyOutcome.AlreadyApplied, (await s.ApplyAsync(ok)).Outcome);
        Assert.Equal(500, await s.GetCoinsAsync(u));

        Assert.Equal(EconomyOutcome.Insufficient, (await s.ApplyAsync(Op(u, "frame-again", (Items.FrameRoyal.Id, 1)))).Outcome); // owned once
        Assert.Equal(EconomyOutcome.Applied, (await s.ApplyAsync(Op(u, "use-shield", (Boosts.ShieldId, -1)))).Outcome);
        Assert.Equal(1, (await s.GetBalancesAsync(u))!.Count(Boosts.ShieldId));

        var ledger = await s.GetLedgerAsync(u, 20);
        Assert.Contains(ledger, l => l.Asset == Economy.Coins && l.Delta == 500 && l.Reason == Economy.Reasons.AdminAdjustment);
        Assert.Contains(ledger, l => l.Asset == Boosts.ShieldId && l.Delta == -1);
        Assert.Equal(EconomyOutcome.UnknownUser, (await s.ApplyAsync(ok with { UserId = Guid.NewGuid() })).Outcome);
    }

    [Fact]
    public async Task Concurrent_spends_never_overdraw()
    {
        var s = await StoreAsync();
        if (s is null) return;
        var u = (await s.CreateUserAsync(false, "PgRace", "crown")).Id;
        await s.ApplyAsync(Op(u, "seed", (Economy.Coins, 5000)));
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => s.ApplyAsync(Op(u, $"buy-{i}", (Economy.Coins, -2000), (Boosts.ShieldId, 1)))));
        Assert.Equal(2, results.Count(r => r.Outcome == EconomyOutcome.Applied));
        var b = (await s.GetBalancesAsync(u))!;
        Assert.Equal(1000, b.Coins);
        Assert.Equal(2, b.Count(Boosts.ShieldId));

        var same = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => s.ApplyAsync(Op(u, "same-key", (Economy.Coins, -100)))));
        Assert.Equal(1, same.Count(r => r.Outcome == EconomyOutcome.Applied));
        Assert.Equal(900, await s.GetCoinsAsync(u));
    }

    [Fact]
    public async Task Purchases_are_unique_per_store_transaction_and_survive_account_deletion()
    {
        var s = await StoreAsync();
        if (s is null) return;
        var u = (await s.CreateUserAsync(false, "PgBuyer", "crown")).Id;
        var tx = "devtx-" + Guid.NewGuid();
        var rec = new PurchaseRecord(Guid.NewGuid(), u, PurchaseStores.DevSandbox, "gems_small", tx, tx, "development", PurchaseStatus.Verified, 1, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var (stored, created) = await s.CreatePurchaseAsync(rec);
        Assert.True(created);
        var (again, created2) = await s.CreatePurchaseAsync(rec with { Id = Guid.NewGuid() });
        Assert.False(created2);
        Assert.Equal(stored.Id, again.Id);

        await s.UpdatePurchaseAsync(stored.Id, PurchaseStatus.Credited, null, DateTimeOffset.UtcNow);
        var credited = (await s.GetPurchaseAsync(PurchaseStores.DevSandbox, tx))!;
        Assert.Equal(PurchaseStatus.Credited, credited.Status);
        Assert.NotNull(credited.CreditedAt);
        Assert.Single(await s.GetPurchasesForUserAsync(u, 10));

        var op = new EconomyOperation(u, $"purchase:dev_sandbox:{tx}", Economy.Reasons.Purchase, new[] { new AssetChange(Economy.Gems, 50) }, DateTimeOffset.UtcNow, PurchaseId: stored.Id);
        Assert.Equal(EconomyOutcome.Applied, (await s.ApplyAsync(op)).Outcome);

        await s.DeleteUserAsync(u);
        var kept = (await s.GetPurchaseAsync(PurchaseStores.DevSandbox, tx))!;
        Assert.Null(kept.UserId); // accounting record kept without the account
    }

    [Fact]
    public async Task Privacy_frames_and_mode_stats()
    {
        var s = await StoreAsync();
        if (s is null) return;
        var a = (await s.CreateUserAsync(false, "PgModeA", "crown")).Id;
        var b = (await s.CreateUserAsync(false, "PgModeB", "crown")).Id;
        Assert.Equal(PrivacySettings.Default, await s.GetPrivacyAsync(a));
        Assert.Equal(new PrivacySettings(false, true, false), await s.UpdatePrivacyAsync(a, false, null, false));
        Assert.Equal(new PrivacySettings(false, true, false), await s.GetPrivacyAsync(a));

        await s.SetFrameAsync(a, Items.FrameGilded.Id);
        Assert.Equal(Items.FrameGilded.Id, (await s.GetProfileAsync(a))!.FrameId);

        async Task Play(GameMode mode, ParticipantResult ra, ParticipantResult rb, DateTimeOffset started)
        {
            var id = Guid.NewGuid();
            await s.CreateMatchAsync(new MatchRecord(id, MatchMode.Casual, 2, null, started, new[]
            {
                new MatchParticipantRecord(0, a, false, "A", "Ruby"), new MatchParticipantRecord(1, b, false, "B", "Topaz"),
            }, mode), "{}");
            await s.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, started.AddMinutes(5), null, "{}", new[]
            {
                new ParticipantOutcome(0, a, ra == ParticipantResult.Win ? 1 : 2, ra, 0, 0, 0, BoostsUsed: 1),
                new ParticipantOutcome(1, b, rb == ParticipantResult.Win ? 1 : 2, rb, 0, 0, 0),
            }, 2, mode));
        }
        var t0 = DateTimeOffset.UtcNow.AddHours(-2);
        await Play(GameMode.Classic, ParticipantResult.Win, ParticipantResult.Loss, t0);
        await Play(GameMode.Classic, ParticipantResult.Win, ParticipantResult.Loss, t0.AddMinutes(10));
        await Play(GameMode.Quick, ParticipantResult.Abandoned, ParticipantResult.Win, t0.AddMinutes(20));

        var modes = await s.GetModeStatsAsync(a);
        var classic = modes.Single(m => m.Mode == GameMode.Classic);
        Assert.Equal((2, 2, 0, 0, 2, 2), (classic.GamesPlayed, classic.GamesWon, classic.GamesLost, classic.Abandons, classic.CurrentWinStreak, classic.BestWinStreak));
        var quick = modes.Single(m => m.Mode == GameMode.Quick);
        Assert.Equal((1, 0, 1), (quick.GamesPlayed, quick.GamesWon, quick.Abandons));
        Assert.Equal(2, (await s.GetModeStatsAsync(b)).Single(m => m.Mode == GameMode.Classic).GamesLost);

        var page = await s.GetHistoryAsync(a, 2);
        Assert.Equal(2, page.Count);
        Assert.Equal(GameMode.Quick, page[0].GameMode);
        Assert.Single(await s.GetHistoryAsync(a, 2, page[^1].StartedAt));
        Assert.Equal(MatchMode.Casual, page[0].Mode);
    }

    [Fact]
    public async Task League_groups_points_results_and_rewards()
    {
        var s = await StoreAsync();
        if (s is null) return;
        // Each test database run gets fresh season numbers above anything existing.
        var latest = await s.GetLatestSeasonAsync();
        var current = await s.GetCurrentSeasonAsync();
        if (current is not null) await s.SetSeasonStatusAsync(current.Id, SeasonStatus.Completed, DateTimeOffset.UtcNow);
        var season = await s.CreateSeasonAsync((latest?.Number ?? 0) + 1, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(13));
        Assert.Equal(season.Id, (await s.CreateSeasonAsync(season.Number, season.StartsAt, season.EndsAt)).Id); // idempotent
        Assert.Equal(season.Id, (await s.GetCurrentSeasonAsync())!.Id);

        var a = (await s.CreateUserAsync(false, "PgLeagueA", "crown")).Id;
        var b = (await s.CreateUserAsync(false, "PgLeagueB", "crown")).Id;
        var sa = await s.EnsureStandingAsync(season.Id, a, 30);
        Assert.Equal(sa, await s.EnsureStandingAsync(season.Id, a, 30)); // idempotent
        var sb = await s.EnsureStandingAsync(season.Id, b, 30);
        Assert.Equal(sa.GroupId, sb.GroupId);
        Assert.Equal(0, await s.GetDivisionAsync(a));

        var id = Guid.NewGuid();
        await s.CreateMatchAsync(new MatchRecord(id, MatchMode.Matchmaking, 2, null, DateTimeOffset.UtcNow, new[]
        {
            new MatchParticipantRecord(0, a, false, "A", "Ruby"), new MatchParticipantRecord(1, b, false, "B", "Topaz"),
        }), "{}");
        await s.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, DateTimeOffset.UtcNow, a, "{}", new[]
        {
            new ParticipantOutcome(0, a, 1, ParticipantResult.Win, 0, 0, 0, LeaguePoints: 20),
            new ParticipantOutcome(1, b, 2, ParticipantResult.Loss, 0, 0, 0, LeaguePoints: -10),
        }, 2, GameMode.Classic, season.Id));
        Assert.Equal(20, (await s.GetStandingAsync(season.Id, a))!.Points);
        Assert.Equal(0, (await s.GetStandingAsync(season.Id, b))!.Points); // floored
        Assert.Equal(20, (await s.GetHistoryAsync(a, 1))[0].LeaguePoints);

        var group = await s.GetGroupAsync(sa.GroupId, 30);
        Assert.Equal(new[] { a, b }, group.Select(g => g.UserId));
        Assert.Equal((await s.GetProfileAsync(a))!.FriendCode, group[0].PublicId);

        await s.SetSeasonStatusAsync(season.Id, SeasonStatus.Finalizing, null);
        await s.SaveSeasonResultsAsync(season.Id, new[]
        {
            new SeasonResult(a, 1, SeasonOutcome.Promoted, 1, new LeagueReward(1500, 5, 0)),
            new SeasonResult(b, 2, SeasonOutcome.Stayed, 0, new LeagueReward(800, 3, 0)),
        });
        await s.SaveSeasonResultsAsync(season.Id, new[] { new SeasonResult(a, 1, SeasonOutcome.Promoted, 1, new LeagueReward(9999, 99, 9)) }); // re-run: reward unchanged
        Assert.Equal(1, await s.GetDivisionAsync(a));
        Assert.Equal(SeasonOutcome.Promoted, (await s.GetStandingAsync(season.Id, a))!.Outcome);
        Assert.Equal(2, (await s.GetUngrantedRewardsAsync(season.Id)).Count);
        Assert.Equal(1500, (await s.GetRewardAsync(season.Id, a))!.Reward.Coins);
        await s.MarkRewardGrantedAsync(season.Id, a, DateTimeOffset.UtcNow);
        Assert.Single(await s.GetUngrantedRewardsAsync(season.Id));
        await s.SetSeasonStatusAsync(season.Id, SeasonStatus.Completed, DateTimeOffset.UtcNow);
        Assert.Equal(SeasonStatus.Completed, (await s.GetSeasonByNumberAsync(season.Number))!.Status);

        await s.DeleteUserAsync(b);
        Assert.Null(await s.GetStandingAsync(season.Id, b));
    }
}
