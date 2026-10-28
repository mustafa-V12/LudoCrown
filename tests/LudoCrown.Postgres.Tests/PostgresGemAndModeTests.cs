using LudoCrown.Core.Model;
using LudoCrown.Server.Data;
using LudoCrown.Server.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LudoCrown.Postgres.Tests;

/// <summary>Gems, rerolls and game modes against a real PostgreSQL database (migration 0002).</summary>
public class PostgresGemAndModeTests
{
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("LUDOCROWN_TEST_POSTGRES");
    private static readonly SemaphoreSlim MigrateOnce = new(1, 1);
    private static bool _migrated;

    private static async Task<PostgresStore?> StoreAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString))
        {
            Console.WriteLine("  (skipped: LUDOCROWN_TEST_POSTGRES not set)");
            return null;
        }
        var db = NpgsqlDataSource.Create(ConnectionString);
        await MigrateOnce.WaitAsync();
        try
        {
            if (!_migrated) { await new MigrationRunner(db, NullLogger<MigrationRunner>.Instance).MigrateAsync(); _migrated = true; }
        }
        finally { MigrateOnce.Release(); }
        return new PostgresStore(db, TimeProvider.System);
    }

    [Fact]
    public async Task Migration_0002_is_embedded_after_0001()
    {
        var migrations = MigrationRunner.LoadEmbedded();
        Assert.Equal("0002_game_modes_and_gems", migrations[1].Version);
        Assert.Contains("gems", migrations[1].Sql);
    }

    [Fact]
    public async Task New_accounts_start_with_starter_gems()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var u = await store.CreateUserAsync(true, "GemStart", "lion");
        Assert.Equal(Economy.StarterGems, await store.GetGemsAsync(u.Id));
    }

    [Fact]
    public async Task Spending_is_idempotent_per_key_and_never_negative()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var u = await store.CreateUserAsync(false, "GemSpend", "lion");
        var match = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = $"reroll:{match:N}:{Guid.NewGuid()}";

        var first = await store.SpendGemsAsync(u.Id, null, key, 1, Economy.Reasons.Reroll, now);
        Assert.Equal(GemSpendOutcome.Spent, first.Outcome);
        Assert.Equal(Economy.StarterGems - 1, first.Balance);

        var retry = await store.SpendGemsAsync(u.Id, null, key, 1, Economy.Reasons.Reroll, now);
        Assert.Equal(GemSpendOutcome.AlreadySpent, retry.Outcome);
        Assert.Equal(Economy.StarterGems - 1, await store.GetGemsAsync(u.Id));

        for (var i = 1; i < Economy.StarterGems; i++)
            Assert.True((await store.SpendGemsAsync(u.Id, null, $"k{i}", 1, Economy.Reasons.Reroll, now)).Charged);
        var broke = await store.SpendGemsAsync(u.Id, null, "one-too-many", 1, Economy.Reasons.Reroll, now);
        Assert.Equal(GemSpendOutcome.InsufficientGems, broke.Outcome);
        Assert.Equal(0, broke.Balance);
        Assert.Equal(0, await store.GetGemsAsync(u.Id));

        var unknown = await store.SpendGemsAsync(Guid.NewGuid(), null, "x", 1, Economy.Reasons.Reroll, now);
        Assert.Equal(GemSpendOutcome.UnknownUser, unknown.Outcome);
    }

    [Fact]
    public async Task Concurrent_spends_with_the_same_key_charge_once()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var u = await store.CreateUserAsync(false, "GemRace", "lion");
        var key = "race-" + Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.SpendGemsAsync(u.Id, null, key, 1, Economy.Reasons.Reroll, DateTimeOffset.UtcNow)));
        Assert.Equal(1, results.Count(r => r.Outcome == GemSpendOutcome.Spent));
        Assert.Equal(Economy.StarterGems - 1, await store.GetGemsAsync(u.Id));
    }

    [Fact]
    public async Task Grants_are_idempotent_per_key()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var u = await store.CreateUserAsync(false, "GemGrant", "lion");
        var key = "dev:" + Guid.NewGuid();
        Assert.Equal(Economy.StarterGems + 25, await store.GrantGemsAsync(u.Id, 25, Economy.Reasons.DevGrant, key, DateTimeOffset.UtcNow));
        Assert.Equal(Economy.StarterGems + 25, await store.GrantGemsAsync(u.Id, 25, Economy.Reasons.DevGrant, key, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Quick_match_saves_mode_rerolls_and_gem_reward_once()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var a = await store.CreateUserAsync(false, "QuickA", "crown");
        var b = await store.CreateUserAsync(false, "QuickB", "crown");
        var matchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateMatchAsync(new MatchRecord(matchId, MatchMode.Matchmaking, 2, null, now, new[]
        {
            new MatchParticipantRecord(0, a.Id, false, "QuickA", "Ruby"),
            new MatchParticipantRecord(1, b.Id, false, "QuickB", "Topaz"),
        }, GameMode.Quick), "{}");

        var active = (await store.GetActiveMatchesAsync()).Single(m => m.Match.Id == matchId);
        Assert.Equal(GameMode.Quick, active.Match.GameMode);

        var completion = new MatchCompletion(matchId, MatchStatus.Completed, now.AddMinutes(2), a.Id, "{}", new[]
        {
            new ParticipantOutcome(0, a.Id, 1, ParticipantResult.Win, 100, 50, 0, GemsAwarded: 1, RerollsUsed: 3),
            new ParticipantOutcome(1, b.Id, 2, ParticipantResult.Loss, 10, 20, 0, GemsAwarded: 0, RerollsUsed: 5),
        }, 2);
        Assert.True(await store.CompleteMatchAsync(completion));
        Assert.False(await store.CompleteMatchAsync(completion));

        Assert.Equal(Economy.StarterGems + 1, await store.GetGemsAsync(a.Id));
        Assert.Equal(Economy.StarterGems, await store.GetGemsAsync(b.Id));

        var ha = Assert.Single(await store.GetHistoryAsync(a.Id, 10));
        Assert.Equal(GameMode.Quick, ha.GameMode);
        Assert.Equal(3, ha.RerollsUsed);
        Assert.Equal(1, ha.GemsAwarded);
        var hb = Assert.Single(await store.GetHistoryAsync(b.Id, 10));
        Assert.Equal(5, hb.RerollsUsed);
    }

    [Fact]
    public async Task Classic_is_the_default_mode()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var a = await store.CreateUserAsync(false, "ClassicA", "crown");
        var matchId = Guid.NewGuid();
        await store.CreateMatchAsync(new MatchRecord(matchId, MatchMode.Private, 2, "QWERTY", DateTimeOffset.UtcNow, new[]
        {
            new MatchParticipantRecord(0, a.Id, false, "ClassicA", "Ruby"),
            new MatchParticipantRecord(1, null, true, "Bot", "Topaz"),
        }), "{}");
        Assert.Equal(GameMode.Classic, (await store.GetActiveMatchesAsync()).Single(m => m.Match.Id == matchId).Match.GameMode);
    }

    [Fact]
    public async Task Deleting_a_user_removes_their_gem_ledger()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var u = await store.CreateUserAsync(false, "GemDelete", "lion");
        await store.SpendGemsAsync(u.Id, null, "del-key", 1, Economy.Reasons.Reroll, DateTimeOffset.UtcNow);
        await store.DeleteUserAsync(u.Id);
        Assert.Equal(GemSpendOutcome.UnknownUser, (await store.SpendGemsAsync(u.Id, null, "after", 1, Economy.Reasons.Reroll, DateTimeOffset.UtcNow)).Outcome);
    }
}
