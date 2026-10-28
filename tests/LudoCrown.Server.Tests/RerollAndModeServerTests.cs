using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;

namespace LudoCrown.Server.Tests;

public class RerollServerTests
{
    private static string NewRequest() => Guid.NewGuid().ToString();

    private static async Task SetGemsAsync(ServerHarness h, Guid user, long target)
    {
        var balance = await h.Store.GetGemsAsync(user);
        if (balance > target) await h.Store.SpendGemsAsync(user, null, $"test:{Guid.NewGuid()}", (int)(balance - target), "admin_adjustment", h.Time.GetUtcNow());
        if (balance < target) await h.Store.GrantGemsAsync(user, (int)(target - balance), "admin_adjustment", $"test:{Guid.NewGuid()}", h.Time.GetUtcNow());
    }

    /// <summary>Starts a 2P match where Alice has a piece out and a pending roll of 2.</summary>
    private static async Task<(ServerHarness H, Guid A, Guid B, Guid Id, string M)> PendingRollAsync(GameMode mode = GameMode.Classic, long gems = Economy.StarterGems)
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync(mode: mode);
        await SetGemsAsync(h, a, gems);
        var room = h.Matches.GetRoom(id)!;
        room.Seats[0].Gems = gems;
        var s = room.State.Clone();
        s.Players[0].Pieces[0] = 10;
        room.State = s;
        h.Dice.Enqueue(2);
        Assert.Null(await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version));
        return (h, a, b, id, id.ToString("N"));
    }

    [Fact]
    public async Task New_accounts_start_with_the_documented_gem_balance()
    {
        var h = new ServerHarness();
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(await h.NewUserAsync()));
    }

    [Fact]
    public async Task Reroll_spends_one_gem_and_replaces_the_roll_with_a_server_value()
    {
        var (h, a, _, id, m) = await PendingRollAsync();
        var room = h.Matches.GetRoom(id)!;
        Assert.Equal(RerollStatus.Available, h.Notifier.LastSnapshot(a)!.RerollStatus);
        h.Dice.Enqueue(5);
        var r = await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest());
        Assert.Null(r.Error);
        Assert.Equal(5, r.NewRoll);
        Assert.Equal(Economy.StarterGems - 1, r.GemBalance);
        Assert.Equal(1, r.RerollsUsed);
        Assert.Equal(6, r.RerollsRemaining);
        Assert.Equal(5, room.State.PendingRoll);
        Assert.Equal(Economy.StarterGems - 1, await h.Store.GetGemsAsync(a));
        var snap = h.Notifier.LastSnapshot(a)!;
        Assert.Equal(Economy.StarterGems - 1, snap.YourGems);
        Assert.Equal(1, snap.Seats[0].RerollsUsed);
        Assert.Contains(snap.Events, e => e.Type == GameEventType.DiceRerolled);
    }

    [Fact]
    public async Task Retried_reroll_requests_are_charged_only_once()
    {
        var (h, a, _, id, m) = await PendingRollAsync();
        var room = h.Matches.GetRoom(id)!;
        var request = NewRequest();
        var version = room.State.Version;
        h.Dice.Enqueue(4, 6);
        var first = await h.Matches.RerollAsync(a, m, version, request);
        var retry = await h.Matches.RerollAsync(a, m, version, request); // e.g. client retried after a timeout
        Assert.Equal(first, retry);
        Assert.Equal(Economy.StarterGems - 1, await h.Store.GetGemsAsync(a));
        Assert.Equal(1, room.State.Players[0].RerollsUsed);
        Assert.Equal(4, room.State.PendingRoll);
    }

    [Fact]
    public async Task A_retry_after_a_server_restart_does_not_charge_again()
    {
        var (h, a, _, id, m) = await PendingRollAsync();
        var request = NewRequest();
        // Simulate: the gem was charged, then the server died before the reroll was saved.
        var spend = await h.Store.SpendGemsAsync(a, id, $"reroll:{id:N}:{request}", 1, Economy.Reasons.Reroll, h.Time.GetUtcNow());
        Assert.Equal(GemSpendOutcome.Spent, spend.Outcome);
        var restarted = h.NewMatchService();
        await restarted.RecoverAsync();
        await restarted.OnConnectedAsync(a);
        var room = restarted.GetRoom(id)!;
        h.Dice.Enqueue(3);
        var r = await restarted.RerollAsync(a, m, room.State.Version, request);
        Assert.Null(r.Error);
        Assert.Equal(Economy.StarterGems - 1, await h.Store.GetGemsAsync(a)); // still only one gem taken
        Assert.Equal(1, room.State.Players[0].RerollsUsed);
    }

    [Fact]
    public async Task Insufficient_gems_reject_the_reroll_without_changing_anything()
    {
        var (h, a, _, id, m) = await PendingRollAsync(gems: 0);
        var room = h.Matches.GetRoom(id)!;
        var before = room.State.Version;
        Assert.Equal(RerollStatus.InsufficientGems, h.Notifier.LastSnapshot(a)!.RerollStatus);
        var r = await h.Matches.RerollAsync(a, m, before, NewRequest());
        Assert.Equal(ErrorCode.InsufficientGems, r.Error!.Code);
        Assert.Equal(0, r.GemBalance);
        Assert.Equal(before, room.State.Version);
        Assert.Equal(2, room.State.PendingRoll);
        Assert.Equal(0, await h.Store.GetGemsAsync(a));
    }

    [Theory]
    [InlineData(GameMode.Classic, 7)]
    [InlineData(GameMode.Quick, 5)]
    public async Task Server_enforces_the_mode_reroll_limit(GameMode mode, int limit)
    {
        var (h, a, _, id, m) = await PendingRollAsync(mode, gems: 50);
        var room = h.Matches.GetRoom(id)!;
        for (var i = 0; i < limit; i++)
        {
            h.Dice.Enqueue(2);
            var ok = await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest());
            Assert.Null(ok.Error);
        }
        Assert.Equal(RerollStatus.NoRerollsLeft, h.Notifier.LastSnapshot(a)!.RerollStatus);
        var blocked = await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest());
        Assert.Equal(ErrorCode.NoRerollsLeft, blocked.Error!.Code);
        Assert.Equal(50 - limit, await h.Store.GetGemsAsync(a));
        Assert.Equal(limit, room.State.Players[0].RerollsUsed);
    }

    [Fact]
    public async Task Reroll_is_rejected_out_of_turn_before_rolling_after_moving_and_when_stale()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var m = id.ToString("N");
        var room = h.Matches.GetRoom(id)!;
        Assert.Equal(ErrorCode.RerollNotAllowed, (await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest())).Error!.Code); // no roll yet
        Assert.Equal(ErrorCode.NotYourTurn, (await h.Matches.RerollAsync(b, m, room.State.Version, NewRequest())).Error!.Code);

        var s = room.State.Clone();
        s.Players[0].Pieces[0] = 10;
        room.State = s;
        h.Dice.Enqueue(3);
        await h.Matches.RollAsync(a, m, room.State.Version);
        Assert.Equal(ErrorCode.StaleVersion, (await h.Matches.RerollAsync(a, m, room.State.Version - 1, NewRequest())).Error!.Code);
        await h.Matches.MoveAsync(a, m, 0, room.State.Version); // move committed → Bob's turn
        Assert.Equal(ErrorCode.NotYourTurn, (await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest())).Error!.Code);
        Assert.Equal(ErrorCode.InvalidRequest, (await h.Matches.RerollAsync(a, m, room.State.Version, "not-a-guid")).Error!.Code);
        Assert.Equal(ErrorCode.NotInMatch, (await h.Matches.RerollAsync(await h.NewUserAsync(), m, room.State.Version, NewRequest())).Error!.Code);
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(a)); // nothing charged by any rejected attempt
    }

    [Fact]
    public async Task A_no_move_roll_is_held_when_the_player_can_afford_a_reroll_and_can_then_be_passed()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var m = id.ToString("N");
        var room = h.Matches.GetRoom(id)!;
        h.Dice.Enqueue(3); // all pieces in yard → no legal move
        await h.Matches.RollAsync(a, m, room.State.Version);
        Assert.Equal(0, room.State.CurrentPlayer.Seat);
        var snap = h.Notifier.LastSnapshot(a)!;
        Assert.True(snap.CanPass);
        Assert.Equal(RerollStatus.Available, snap.RerollStatus);
        Assert.Empty(snap.LegalPieces);

        Assert.Null(await h.Matches.PassAsync(a, m, room.State.Version));
        Assert.Equal(1, room.State.CurrentPlayer.Seat);
        Assert.Equal(ErrorCode.NotYourTurn, (await h.Matches.PassAsync(a, m, room.State.Version))!.Code);
    }

    [Fact]
    public async Task A_held_roll_can_be_rerolled_into_a_six_to_enter()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        var m = id.ToString("N");
        var room = h.Matches.GetRoom(id)!;
        h.Dice.Enqueue(3, 6);
        await h.Matches.RollAsync(a, m, room.State.Version);
        var r = await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest());
        Assert.Equal(6, r.NewRoll);
        Assert.Equal(new[] { 0, 1, 2, 3 }, h.Notifier.LastSnapshot(a)!.LegalPieces);
    }

    [Fact]
    public async Task Without_gems_a_no_move_roll_passes_immediately()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        await SetGemsAsync(h, a, 0);
        var room = h.Matches.GetRoom(id)!;
        room.Seats[0].Gems = 0;
        h.Dice.Enqueue(3);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        Assert.Equal(1, room.State.CurrentPlayer.Seat);
    }

    [Fact]
    public async Task A_held_roll_is_passed_automatically_after_the_decision_window_without_counting_a_miss()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;
        h.Dice.Enqueue(3);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        Assert.Equal(0, room.State.CurrentPlayer.Seat);
        await h.TickAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(0, room.State.CurrentPlayer.Seat);  // still inside the 5 s decision window
        await h.TickAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, room.State.CurrentPlayer.Seat);  // passed automatically
        Assert.Equal(0, room.Seats[0].MissedTurns);       // not counted as a missed turn
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(a));
    }

    [Fact]
    public async Task Reroll_counts_are_saved_with_the_result()
    {
        var (h, a, b, id, m) = await PendingRollAsync(GameMode.Quick);
        var room = h.Matches.GetRoom(id)!;
        h.Dice.Enqueue(2);
        await h.Matches.RerollAsync(a, m, room.State.Version, NewRequest());
        await h.Matches.LeaveAsync(b, m);
        await h.Matches.LeaveAsync(a, m);
        var history = Assert.Single(await h.Store.GetHistoryAsync(a, 10));
        Assert.Equal(1, history.RerollsUsed);
        Assert.Equal(GameMode.Quick, history.GameMode);
    }
}

public class ModeServerTests
{
    [Fact]
    public async Task Quick_match_ends_when_the_first_piece_is_crowned_and_is_recorded_with_its_mode()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync(mode: GameMode.Quick);
        var room = h.Matches.GetRoom(id)!;
        Assert.Equal(GameMode.Quick, h.Notifier.LastSnapshot(a)!.Mode);
        Assert.Equal(1, h.Notifier.LastSnapshot(a)!.PiecesToWin);
        Assert.Equal(5, h.Notifier.LastSnapshot(a)!.MaxRerolls);
        var s = room.State.Clone();
        s.Players[0].Pieces = new[] { 53, -1, -1, -1 };
        room.State = s;
        h.Dice.Enqueue(3);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        await h.Matches.MoveAsync(a, id.ToString("N"), 0, room.State.Version);

        Assert.True(room.Ended);
        var ended = h.Notifier.Ended.Single(e => e.User == a).Info;
        Assert.Equal(GameMode.Quick, ended.Mode);
        Assert.Equal(1, ended.YourPlacement);
        Assert.Equal(1, ended.GemsAwarded);
        Assert.Equal(Economy.StarterGems + 1, await h.Store.GetGemsAsync(a));
        Assert.Equal(Economy.StarterGems, await h.Store.GetGemsAsync(b));
        Assert.Equal(GameMode.Quick, Assert.Single(await h.Store.GetHistoryAsync(b, 10)).GameMode);
    }

    [Fact]
    public async Task Classic_match_continues_after_the_first_crowned_piece()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync(mode: GameMode.Classic);
        var room = h.Matches.GetRoom(id)!;
        var s = room.State.Clone();
        s.Players[0].Pieces = new[] { 53, 10, -1, -1 };
        room.State = s;
        h.Dice.Enqueue(3);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        await h.Matches.MoveAsync(a, id.ToString("N"), 0, room.State.Version);
        Assert.False(room.Ended);
    }

    [Fact]
    public async Task Matchmaking_keeps_modes_in_separate_queues()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        var b = await h.NewUserAsync();
        var c = await h.NewUserAsync();
        await h.Matchmaking.EnqueueAsync(a, 2, GameMode.Classic);
        var (sb, _) = await h.Matchmaking.EnqueueAsync(b, 2, GameMode.Quick);
        Assert.Equal(MatchmakingState.Searching, sb!.State);
        var (sc, _) = await h.Matchmaking.EnqueueAsync(c, 2, GameMode.Quick);
        Assert.Equal(MatchmakingState.Matched, sc!.State);
        Assert.Equal(GameMode.Quick, sc.Mode);
        Assert.Equal(GameMode.Quick, h.Matches.GetRoom(h.Matches.ActiveMatchFor(b)!.Value)!.State.Mode);
        Assert.Null(h.Matches.ActiveMatchFor(a));
    }

    [Fact]
    public async Task Bot_filled_quick_matches_use_quick_rules()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        await h.Matchmaking.EnqueueAsync(a, 4, GameMode.Quick);
        h.Time.Advance(TimeSpan.FromSeconds(31));
        await h.Matchmaking.TickAsync();
        var room = h.Matches.GetRoom(h.Matches.ActiveMatchFor(a)!.Value)!;
        Assert.Equal(GameMode.Quick, room.State.Mode);
        Assert.Equal(1, room.State.Rules.PiecesToWin);
    }

    [Fact]
    public async Task Unknown_modes_are_rejected_by_the_server()
    {
        var h = new ServerHarness();
        var (s, e) = await h.Matchmaking.EnqueueAsync(await h.NewUserAsync(), 2, (GameMode)4);
        Assert.Null(s);
        Assert.Equal(ErrorCode.InvalidMode, e!.Code);
        var (room, e2) = await h.Rooms.CreateAsync(await h.NewUserAsync(), 2, (GameMode)9);
        Assert.Null(room);
        Assert.Equal(ErrorCode.InvalidMode, e2!.Code);
    }

    [Fact]
    public async Task Private_tables_carry_their_mode_into_the_match_and_the_rematch()
    {
        var h = new ServerHarness();
        var host = await h.NewUserAsync();
        var friend = await h.NewUserAsync();
        var (room, _) = await h.Rooms.CreateAsync(host, 2, GameMode.Quick);
        Assert.Equal(GameMode.Quick, room!.Mode);
        var (joined, _) = await h.Rooms.JoinAsync(friend, room.Code);
        Assert.Equal(GameMode.Quick, joined!.Mode);
        var (matchId, _) = await h.Rooms.StartAsync(host, room.RoomId);
        Assert.Equal(GameMode.Quick, h.Matches.GetRoom(Guid.Parse(matchId!))!.State.Mode);
        await h.Matches.LeaveAsync(host, matchId!);
        await h.Matches.LeaveAsync(friend, matchId!);
        var (rematch, _) = await h.Rooms.RematchAsync(host, matchId);
        Assert.Equal(GameMode.Quick, rematch!.Mode);
    }

    [Fact]
    public async Task The_mode_cannot_change_after_the_match_starts()
    {
        // There is no API to change rules; the rules live in the server-side state and are restored after a restart.
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync(mode: GameMode.Quick);
        var restarted = h.NewMatchService();
        await restarted.RecoverAsync();
        Assert.Equal(GameMode.Quick, restarted.GetRoom(id)!.State.Mode);
        Assert.Equal(id, restarted.ActiveMatchFor(a));
    }

    [Fact]
    public async Task Gem_rewards_follow_the_policy()
    {
        Assert.Equal(1, Matches.RewardPolicy.GemsFor(MatchMode.Matchmaking, 1, ParticipantResult.Win));
        Assert.Equal(0, Matches.RewardPolicy.GemsFor(MatchMode.Matchmaking, 2, ParticipantResult.Loss));
        Assert.Equal(0, Matches.RewardPolicy.GemsFor(MatchMode.Private, 1, ParticipantResult.Win));
        Assert.Equal(0, Matches.RewardPolicy.GemsFor(MatchMode.Matchmaking, 1, ParticipantResult.Abandoned));
        await Task.CompletedTask;
    }
}

public class WalletStoreTests
{
    [Fact]
    public async Task Spending_is_idempotent_and_never_goes_negative()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var r1 = await h.Store.SpendGemsAsync(u, null, "k1", 4, "reroll", h.Time.GetUtcNow());
        var r2 = await h.Store.SpendGemsAsync(u, null, "k1", 4, "reroll", h.Time.GetUtcNow());
        Assert.Equal(GemSpendOutcome.Spent, r1.Outcome);
        Assert.Equal(GemSpendOutcome.AlreadySpent, r2.Outcome);
        Assert.Equal(6, r2.Balance);
        var r3 = await h.Store.SpendGemsAsync(u, null, "k2", 7, "reroll", h.Time.GetUtcNow());
        Assert.Equal(GemSpendOutcome.InsufficientGems, r3.Outcome);
        Assert.Equal(6, await h.Store.GetGemsAsync(u));
        Assert.Equal(GemSpendOutcome.UnknownUser, (await h.Store.SpendGemsAsync(Guid.NewGuid(), null, "k", 1, "reroll", h.Time.GetUtcNow())).Outcome);
    }

    [Fact]
    public async Task Grants_are_idempotent_per_key()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        Assert.Equal(15, await h.Store.GrantGemsAsync(u, 5, "dev_grant", "g1", h.Time.GetUtcNow()));
        Assert.Equal(15, await h.Store.GrantGemsAsync(u, 5, "dev_grant", "g1", h.Time.GetUtcNow()));
    }

    [Fact]
    public async Task Concurrent_spends_with_the_same_key_charge_once()
    {
        var h = new ServerHarness();
        var u = await h.NewUserAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => h.Store.SpendGemsAsync(u, null, "same", 1, "reroll", h.Time.GetUtcNow()))));
        Assert.Equal(1, results.Count(r => r.Outcome == GemSpendOutcome.Spent));
        Assert.Equal(Economy.StarterGems - 1, await h.Store.GetGemsAsync(u));
    }
}
