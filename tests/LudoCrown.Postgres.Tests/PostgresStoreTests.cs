using LudoCrown.Server.Data;
using LudoCrown.Server.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LudoCrown.Postgres.Tests;

/// <summary>Runs the real PostgresStore against a real database (see the .csproj for setup).</summary>
public class PostgresStoreTests
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
    public async Task Migrations_are_embedded_in_order()
    {
        var migrations = MigrationRunner.LoadEmbedded();
        Assert.NotEmpty(migrations);
        Assert.Equal("0001_initial_schema", migrations[0].Version);
        Assert.Contains("CREATE TABLE users", migrations[0].Sql);
    }

    [Fact]
    public async Task Creating_a_user_creates_profile_stats_and_wallet()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var user = await store.CreateUserAsync(true, "PgTester", "lion");
        Assert.True((await store.GetUserAsync(user.Id))!.IsGuest);
        var profile = await store.GetProfileAsync(user.Id);
        Assert.Equal("PgTester", profile!.DisplayName);
        Assert.Equal(8, profile.FriendCode.Length);
        Assert.Equal(profile.UserId, (await store.GetProfileByFriendCodeAsync(profile.FriendCode))!.UserId);
        Assert.Equal(0, (await store.GetStatsAsync(user.Id)).GamesPlayed);
        Assert.Equal(0, await store.GetCoinsAsync(user.Id));

        var updated = await store.UpdateProfileAsync(user.Id, "Renamed", null, DateTimeOffset.UtcNow);
        Assert.Equal("Renamed", updated.DisplayName);
        Assert.Equal("lion", updated.AvatarId);
        var many = await store.GetProfilesAsync(new[] { user.Id, Guid.NewGuid() });
        Assert.Single(many);
    }

    [Fact]
    public async Task Identities_are_unique_per_provider_subject()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var a = await store.CreateUserAsync(false, "IdentA", "crown");
        var b = await store.CreateUserAsync(false, "IdentB", "crown");
        var subject = "sub-" + Guid.NewGuid();
        Assert.True(await store.AddIdentityAsync(a.Id, AuthProviders.Google, subject, null));
        Assert.True(await store.AddIdentityAsync(a.Id, AuthProviders.Google, subject, null)); // idempotent for same user
        Assert.False(await store.AddIdentityAsync(b.Id, AuthProviders.Google, subject, null));
        Assert.Equal(a.Id, await store.FindUserByIdentityAsync(AuthProviders.Google, subject));
        Assert.Null(await store.FindUserByIdentityAsync(AuthProviders.Google, "nobody"));
        await store.SetGuestAsync(a.Id, false);
        Assert.Single(await store.GetIdentitiesAsync(a.Id));
    }

    [Fact]
    public async Task Refresh_tokens_and_auth_codes()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var u = await store.CreateUserAsync(false, "Tokens", "crown");
        var now = DateTimeOffset.UtcNow;
        var hash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var id = Guid.NewGuid();
        await store.StoreRefreshTokenAsync(new RefreshTokenRecord(id, u.Id, hash, now, now.AddDays(1), null, null));
        var t = await store.GetRefreshTokenAsync(hash);
        Assert.Equal(u.Id, t!.UserId);
        Assert.Equal(hash, t.TokenHash);
        var replacement = Guid.NewGuid();
        await store.RevokeRefreshTokenAsync(id, replacement, now);
        Assert.Equal(replacement, (await store.GetRefreshTokenAsync(hash))!.ReplacedBy);

        var codeHash = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var payload = $"pg-{Guid.NewGuid()}@example.com";
        await store.CreateAuthCodeAsync(new AuthCodeRecord(Guid.NewGuid(), AuthCodeKinds.EmailOtp, codeHash, null, payload, now.AddMinutes(10), 0));
        var live = await store.FindLiveAuthCodeAsync(AuthCodeKinds.EmailOtp, payload, now);
        Assert.NotNull(live);
        Assert.Equal(1, await store.IncrementAuthCodeAttemptsAsync(live!.Id));
        Assert.NotNull(await store.ConsumeAuthCodeAsync(AuthCodeKinds.EmailOtp, codeHash, now));
        Assert.Null(await store.ConsumeAuthCodeAsync(AuthCodeKinds.EmailOtp, codeHash, now)); // single use
    }

    [Fact]
    public async Task Match_lifecycle_rewards_once_and_appears_in_history()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var a = await store.CreateUserAsync(false, "MatchA", "crown");
        var b = await store.CreateUserAsync(false, "MatchB", "crown");
        var matchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateMatchAsync(new MatchRecord(matchId, MatchMode.Matchmaking, 2, null, now, new[]
        {
            new MatchParticipantRecord(0, a.Id, false, "MatchA", "Ruby"),
            new MatchParticipantRecord(1, b.Id, false, "MatchB", "Topaz"),
        }), "{\"v\":0}");
        await store.SaveLiveStateAsync(matchId, "{\"v\":5}", 5);
        await store.SaveLiveStateAsync(matchId, "{\"v\":3}", 3); // older version ignored

        var active = (await store.GetActiveMatchesAsync()).Single(m => m.Match.Id == matchId);
        Assert.Contains("5", active.StateJson);
        Assert.Equal(2, active.Match.Participants.Count);

        var completion = new MatchCompletion(matchId, MatchStatus.Completed, now.AddMinutes(5), a.Id, "{\"v\":9}", new[]
        {
            new ParticipantOutcome(0, a.Id, 1, ParticipantResult.Win, 100, 50, 2),
            new ParticipantOutcome(1, b.Id, 2, ParticipantResult.Loss, 10, 20, 1),
        }, 2);
        Assert.True(await store.CompleteMatchAsync(completion));
        Assert.False(await store.CompleteMatchAsync(completion)); // idempotent

        Assert.Equal(100, await store.GetCoinsAsync(a.Id));
        Assert.Equal(10, await store.GetCoinsAsync(b.Id));
        var sa = await store.GetStatsAsync(a.Id);
        Assert.Equal(1, sa.GamesWon);
        Assert.Equal(1, sa.TwoPlayerWins);
        Assert.Equal(1, sa.CurrentWinStreak);
        Assert.Equal(2, sa.PiecesCaptured);
        Assert.Equal(50, (await store.GetProfileAsync(a.Id))!.Xp);
        Assert.DoesNotContain(await store.GetActiveMatchesAsync(), m => m.Match.Id == matchId);

        var history = await store.GetHistoryAsync(b.Id, 10);
        var item = Assert.Single(history);
        Assert.Equal(ParticipantResult.Loss, item.Result);
        Assert.Equal(new[] { "MatchA" }, item.Opponents);
    }

    [Fact]
    public async Task Friends_blocks_and_reports()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var a = await store.CreateUserAsync(false, "FriendA", "crown");
        var b = await store.CreateUserAsync(false, "FriendB", "swan");
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(FriendshipStatus.Pending, await store.RequestFriendAsync(a.Id, b.Id, now));
        var incoming = Assert.Single(await store.GetFriendsAsync(b.Id));
        Assert.True(incoming.IncomingRequest);
        Assert.Equal(FriendshipStatus.Accepted, await store.RequestFriendAsync(b.Id, a.Id, now)); // asking back accepts
        Assert.Equal(FriendshipStatus.Accepted, Assert.Single(await store.GetFriendsAsync(a.Id)).Status);

        await store.BlockAsync(a.Id, b.Id, now);
        Assert.True(await store.IsBlockedEitherWayAsync(b.Id, a.Id));
        Assert.Empty(await store.GetFriendsAsync(a.Id)); // blocking removes the friendship
        Assert.Equal(new[] { b.Id }, await store.GetBlockedAsync(a.Id));
        await store.UnblockAsync(a.Id, b.Id);
        Assert.False(await store.IsBlockedEitherWayAsync(a.Id, b.Id));

        await store.AddReportAsync(new ReportRecord(Guid.NewGuid(), a.Id, b.Id, null, "offensive_name", "test", now));
    }

    [Fact]
    public async Task Deleting_a_user_cascades_and_anonymises_history()
    {
        var store = await StoreAsync();
        if (store is null) return;
        var a = await store.CreateUserAsync(false, "DeleteMe", "crown");
        var b = await store.CreateUserAsync(false, "Keeper", "crown");
        var matchId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await store.CreateMatchAsync(new MatchRecord(matchId, MatchMode.Private, 2, "ABCDEF", now, new[]
        {
            new MatchParticipantRecord(0, a.Id, false, "DeleteMe", "Ruby"),
            new MatchParticipantRecord(1, b.Id, false, "Keeper", "Topaz"),
        }), "{}");
        await store.CompleteMatchAsync(new MatchCompletion(matchId, MatchStatus.Completed, now, a.Id, "{}", new[]
        {
            new ParticipantOutcome(0, a.Id, 1, ParticipantResult.Win, 0, 25, 0),
            new ParticipantOutcome(1, b.Id, 2, ParticipantResult.Loss, 0, 10, 0),
        }, 2));
        await store.AddIdentityAsync(a.Id, AuthProviders.Email, $"del-{Guid.NewGuid()}@example.com", null);
        await store.RequestFriendAsync(a.Id, b.Id, now);

        await store.DeleteUserAsync(a.Id);

        Assert.Null(await store.GetUserAsync(a.Id));
        Assert.Null(await store.GetProfileAsync(a.Id));
        Assert.Empty(await store.GetIdentitiesAsync(a.Id));
        Assert.Empty(await store.GetFriendsAsync(b.Id));
        Assert.Equal(new[] { "Deleted player" }, Assert.Single(await store.GetHistoryAsync(b.Id, 10)).Opponents);
    }
}
