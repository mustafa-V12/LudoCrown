using LudoCrown.Core.Board;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Server.Data;

namespace LudoCrown.Server.Tests;

public class MatchServiceTests
{
    [Fact]
    public async Task Starting_a_match_sends_each_player_a_snapshot_with_their_own_seat()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        Assert.Equal(0, h.Notifier.LastSnapshot(a)!.YourSeat);
        Assert.Equal(1, h.Notifier.LastSnapshot(b)!.YourSeat);
        Assert.Equal(id, h.Matches.ActiveMatchFor(a));
        Assert.Equal(id, h.Matches.ActiveMatchFor(b));
    }

    [Fact]
    public async Task Server_rolls_the_dice_and_the_client_cannot_choose_the_value()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        h.Dice.Enqueue(6);
        var error = await h.Matches.RollAsync(a, id.ToString("N"), 0);
        Assert.Null(error);
        var snap = h.Notifier.LastSnapshot(a)!;
        Assert.Equal(6, snap.PendingRoll);
        Assert.Equal(GamePhase.AwaitingMove, snap.Phase);
        Assert.Equal(new[] { 0, 1, 2, 3 }, snap.LegalPieces);
    }

    [Fact]
    public async Task Acting_out_of_turn_is_rejected_and_state_is_unchanged()
    {
        var h = new ServerHarness();
        var (_, b, id) = await h.StartTwoPlayerAsync();
        var error = await h.Matches.RollAsync(b, id.ToString("N"), 0);
        Assert.Equal(ErrorCode.NotYourTurn, error!.Code);
        Assert.Equal(0, h.Matches.GetRoom(id)!.State.Version);
    }

    [Fact]
    public async Task Stale_commands_are_rejected_and_the_client_is_resynced()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        h.Dice.Enqueue(6);
        await h.Matches.RollAsync(a, id.ToString("N"), 0);
        var before = h.Notifier.Snapshots.Count;
        var error = await h.Matches.MoveAsync(a, id.ToString("N"), 0, expectedVersion: 0);
        Assert.Equal(ErrorCode.StaleVersion, error!.Code);
        Assert.Equal(before + 1, h.Notifier.Snapshots.Count);
        Assert.Equal(1, h.Notifier.LastSnapshot(a)!.Version);
    }

    [Fact]
    public async Task Illegal_moves_are_rejected()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;
        room.State = room.State.Clone();
        room.State.Players[0].Pieces[0] = 10;
        h.Dice.Enqueue(3);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        var error = await h.Matches.MoveAsync(a, id.ToString("N"), 1, room.State.Version); // piece 1 still in yard
        Assert.Equal(ErrorCode.IllegalMove, error!.Code);
        Assert.Equal(ErrorCode.InvalidPiece, (await h.Matches.MoveAsync(a, id.ToString("N"), 9, room.State.Version))!.Code);
    }

    [Fact]
    public async Task Non_participants_cannot_act_or_resync()
    {
        var h = new ServerHarness();
        var (_, _, id) = await h.StartTwoPlayerAsync();
        var stranger = await h.NewUserAsync("Eve");
        Assert.Equal(ErrorCode.NotInMatch, (await h.Matches.RollAsync(stranger, id.ToString("N"), 0))!.Code);
        Assert.Null(await h.Matches.ResyncAsync(stranger, id.ToString("N")));
        Assert.Equal(ErrorCode.UnknownMatch, (await h.Matches.RollAsync(stranger, "not-a-match", 0))!.Code);
    }

    [Fact]
    public async Task A_timed_out_player_has_their_turn_played_and_is_abandoned_after_repeated_misses()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;
        var match = id.ToString("N");

        for (var miss = 1; miss <= 3; miss++)
        {
            // Alice idles: no roll. After the turn timer the server plays her turn.
            h.Dice.Enqueue(2); // no legal move, so the turn passes to Bob
            await h.TickAsync(TimeSpan.FromSeconds(21));
            if (miss < 3)
            {
                Assert.Equal(1, room.State.CurrentPlayer.Seat);
                Assert.Equal(miss, room.Seats[0].MissedTurns);
                // Bob plays normally.
                h.Dice.Enqueue(2);
                Assert.Null(await h.Matches.RollAsync(b, match, room.State.Version));
            }
        }
        Assert.True(room.Seats[0].Abandoned);
        Assert.Null(h.Matches.ActiveMatchFor(a));
        Assert.True(h.Notifier.LastSnapshot(b)!.Seats[0].IsBot);
    }

    [Fact]
    public async Task Acting_resets_the_missed_turn_counter()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;
        h.Dice.Enqueue(2);
        await h.TickAsync(TimeSpan.FromSeconds(21));
        Assert.Equal(1, room.Seats[0].MissedTurns);
        h.Dice.Enqueue(2, 2);
        await h.Matches.RollAsync(b, id.ToString("N"), room.State.Version);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        Assert.Equal(0, room.Seats[0].MissedTurns);
    }

    [Fact]
    public async Task Disconnected_players_get_a_short_turn_and_a_reconnect_restores_them()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;

        await h.Matches.OnDisconnectedAsync(a);
        Assert.False(room.Seats[0].Connected);
        Assert.False(h.Notifier.LastSnapshot(b)!.Seats[0].IsConnected);
        Assert.True(room.Deadline <= h.Time.GetUtcNow().AddSeconds(6));

        h.Dice.Enqueue(1);
        await h.TickAsync(TimeSpan.FromSeconds(7));
        Assert.Equal(1, room.State.CurrentPlayer.Seat); // auto-played

        await h.Matches.OnConnectedAsync(a);
        Assert.True(room.Seats[0].Connected);
        var snap = h.Notifier.LastSnapshot(a)!;
        Assert.Equal(room.State.Version, snap.Version);
        Assert.False(room.Seats[0].Abandoned);
    }

    [Fact]
    public async Task A_player_disconnected_past_the_grace_period_is_replaced_by_a_bot()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;
        await h.Matches.OnDisconnectedAsync(b);
        for (var i = 0; i < 95 && !room.Ended; i++)
        {
            // Alice keeps playing her own turns; Bob's turns are auto-played while he is away.
            if (room.State.CurrentPlayer.Seat == 0)
            {
                if (room.State.Phase == GamePhase.AwaitingRoll) await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
                if (room.State.CurrentPlayer.Seat == 0 && room.State.Phase == GamePhase.AwaitingMove)
                    await h.Matches.MoveAsync(a, id.ToString("N"), GameEngine.GetLegalMoves(room.State)[0].Piece, room.State.Version);
            }
            await h.TickAsync(TimeSpan.FromSeconds(1));
        }
        Assert.True(room.Seats[1].Abandoned);
        Assert.False(room.Ended);
        Assert.Null(h.Matches.ActiveMatchFor(b));
        Assert.Equal(id, h.Matches.ActiveMatchFor(a));
    }

    [Fact]
    public async Task When_every_human_leaves_the_match_ends_as_abandoned_without_rewards()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        await h.Matches.LeaveAsync(a, id.ToString("N"));
        await h.Matches.LeaveAsync(b, id.ToString("N"));
        Assert.True(h.Matches.GetRoom(id)!.Ended);
        Assert.Equal(0, await h.Store.GetCoinsAsync(a));
        var stats = await h.Store.GetStatsAsync(a);
        Assert.Equal(1, stats.Abandons);
        Assert.Empty(await h.Store.GetActiveMatchesAsync());
    }

    [Fact]
    public async Task Winning_records_results_and_rewards_on_the_server_exactly_once()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var room = h.Matches.GetRoom(id)!;
        var s = room.State.Clone();
        s.Players[0].Pieces = new[] { 56, 56, 56, 52 };
        room.State = s;

        h.Dice.Enqueue(4);
        Assert.Null(await h.Matches.RollAsync(a, id.ToString("N"), s.Version));
        Assert.Null(await h.Matches.MoveAsync(a, id.ToString("N"), 3, room.State.Version));

        Assert.True(room.Ended);
        var winner = h.Notifier.Ended.Single(e => e.User == a).Info;
        Assert.Equal(1, winner.YourPlacement);
        Assert.Equal(100, winner.CoinsAwarded);
        Assert.Equal(100, await h.Store.GetCoinsAsync(a));
        Assert.Equal(10, await h.Store.GetCoinsAsync(b));
        Assert.Equal(1, (await h.Store.GetStatsAsync(a)).GamesWon);
        Assert.Equal(0, (await h.Store.GetStatsAsync(b)).GamesWon);
        Assert.Equal(1, (await h.Store.GetStatsAsync(b)).GamesPlayed);

        // Further commands are rejected and rewards are not granted again.
        Assert.Equal(ErrorCode.GameOver, (await h.Matches.RollAsync(b, id.ToString("N"), room.State.Version))!.Code);
        var again = await h.Store.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, h.Time.GetUtcNow(), a, "{}",
            new[] { new ParticipantOutcome(0, a, 1, ParticipantResult.Win, 100, 50, 0) }, 2));
        Assert.False(again);
        Assert.Equal(100, await h.Store.GetCoinsAsync(a));

        var history = await h.Store.GetHistoryAsync(a, 10);
        Assert.Equal(ParticipantResult.Win, Assert.Single(history).Result);
    }

    [Fact]
    public async Task Bots_play_their_own_turns_after_a_short_delay()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync();
        await h.Matches.OnConnectedAsync(a);
        var id = await h.Matches.StartMatchAsync(MatchMode.Matchmaking, new Guid?[] { a, null }, null);
        var room = h.Matches.GetRoom(id)!;
        h.Dice.Enqueue(2); // Alice: no move
        await h.Matches.RollAsync(a, id.ToString("N"), 0);
        Assert.Equal(1, room.State.CurrentPlayer.Seat);
        h.Dice.Enqueue(3); // bot: no move
        await h.TickAsync(TimeSpan.FromSeconds(1.1));
        Assert.Equal(0, room.State.CurrentPlayer.Seat);
    }

    [Fact]
    public async Task Reactions_are_preset_rate_limited_and_respect_blocks()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        Assert.Equal(ErrorCode.InvalidRequest, (await h.Matches.ReactAsync(a, id.ToString("N"), "hello there"))!.Code);
        Assert.Null(await h.Matches.ReactAsync(a, id.ToString("N"), "gg"));
        Assert.Contains(h.Notifier.Reactions, r => r.User == b && r.Reaction.ReactionId == "gg");
        Assert.Equal(ErrorCode.RateLimited, (await h.Matches.ReactAsync(a, id.ToString("N"), "wow"))!.Code);

        await h.Store.BlockAsync(b, a, h.Time.GetUtcNow());
        h.Time.Advance(TimeSpan.FromSeconds(3));
        await h.Matches.ReactAsync(a, id.ToString("N"), "wave");
        Assert.DoesNotContain(h.Notifier.Reactions, r => r.User == b && r.Reaction.ReactionId == "wave");
    }

    [Fact]
    public async Task Live_matches_are_recovered_after_a_server_restart()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        h.Dice.Enqueue(6);
        await h.Matches.RollAsync(a, id.ToString("N"), 0);
        await h.Matches.MoveAsync(a, id.ToString("N"), 0, 1);

        var restarted = h.NewMatchService();
        await restarted.RecoverAsync();
        var room = restarted.GetRoom(id)!;
        Assert.Equal(2, room.State.Version);
        Assert.Equal(BoardGeometry.StartProgress, room.State.Players[0].Pieces[0]);
        Assert.Equal(id, restarted.ActiveMatchFor(a));
        Assert.False(room.Seats[0].Connected);

        await restarted.OnConnectedAsync(a);
        h.Dice.Enqueue(1);
        Assert.Null(await restarted.RollAsync(a, id.ToString("N"), 2));
    }

    [Theory]
    [InlineData(MatchMode.Matchmaking, 2, 1, ParticipantResult.Win, 2, 100, 50)]
    [InlineData(MatchMode.Matchmaking, 4, 2, ParticipantResult.Loss, 4, 60, 40)]
    [InlineData(MatchMode.Matchmaking, 4, 1, ParticipantResult.Abandoned, 4, 0, 0)]
    [InlineData(MatchMode.Private, 2, 1, ParticipantResult.Win, 2, 0, 25)]
    [InlineData(MatchMode.Private, 4, 1, ParticipantResult.Win, 1, 0, 0)]
    public void Reward_policy(MatchMode mode, int players, int placement, ParticipantResult result, int humans, int coins, int xp)
    {
        Assert.Equal((coins, xp), Matches.RewardPolicy.For(mode, players, placement, result, humans));
    }
}
