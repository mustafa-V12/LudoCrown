using LudoCrown.App.Game;
using LudoCrown.App.Services;
using LudoCrown.Core.AI;
using LudoCrown.Core.Dice;
using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Tests;

public class OfflineGameSessionTests
{
    private static OfflineSeat Human(string n = "You") => new(n, false, BotDifficulty.Normal, "crown");
    private static OfflineSeat Bot(string n, BotDifficulty d = BotDifficulty.Normal) => new(n, true, d, "knight");

    [Fact]
    public async Task An_all_bot_game_plays_to_completion_offline_and_clears_the_save()
    {
        var settings = new MemorySettings();
        var session = OfflineGameSession.NewGame(new[] { Bot("A", BotDifficulty.Easy), Bot("B", BotDifficulty.Normal), Bot("C", BotDifficulty.Hard), Bot("D") },
            new SeededDiceRoller(3), new InstantDelay(), settings, new Random(3));
        GameResult? result = null;
        session.Ended += r => result = r;
        await session.StartAsync();
        Assert.NotNull(result);
        Assert.Equal(GamePhase.Finished, session.State.Phase);
        Assert.Equal(4, result!.Standings.Count);
        Assert.Null(settings.GetString(SettingKeys.OfflineSave));
    }

    [Fact]
    public async Task Human_can_roll_and_move_only_on_their_turn_and_bots_reply()
    {
        var settings = new MemorySettings();
        settings.SetBool(SettingKeys.AutoMoveSingle, false);
        var dice = new ScriptedDiceRoller(6, 2, 3);
        var session = OfflineGameSession.NewGame(new[] { Human(), Bot("Bot") }, dice, new InstantDelay(), settings);
        await session.StartAsync();
        Assert.True(session.State.CanRoll);

        await session.MoveAsync(0); // ignored: must roll first
        Assert.Equal(0, session.State.Version);

        await session.RollAsync(); // 6
        Assert.Equal(new[] { 0, 1, 2, 3 }, session.State.LegalPieces);
        await session.MoveAsync(1);
        Assert.Equal(0, session.EngineState.Players[0].Pieces[1]);
        Assert.True(session.State.CanRoll); // six = extra roll

        await session.RollAsync(); // 2 -> must move piece 1
        await session.MoveAsync(1);
        Assert.Equal(2, session.EngineState.Players[0].Pieces[1]);

        // Bot's turn ran automatically: it rolled a 3 with no piece out, so it's our turn again.
        Assert.Equal(0, session.State.CurrentSeat);
        Assert.Equal(0, dice.Remaining);
    }

    [Fact]
    public async Task Auto_move_plays_a_single_legal_move()
    {
        var settings = new MemorySettings();
        var session = OfflineGameSession.NewGame(new[] { Human(), Human("Friend") }, new ScriptedDiceRoller(6, 4), new InstantDelay(), settings);
        await session.StartAsync();
        await session.RollAsync(); // 6: four options, no auto-move
        Assert.Equal(GamePhase.AwaitingMove, session.State.Phase);
        await session.MoveAsync(0);
        await session.RollAsync(); // 4: only piece 0 can move → auto
        Assert.Equal(4, session.EngineState.Players[0].Pieces[0]);
        Assert.Equal(1, session.State.CurrentSeat);
    }

    [Fact]
    public async Task Pass_and_play_lets_each_local_human_act_on_their_turn()
    {
        var session = OfflineGameSession.NewGame(new[] { Human("Ana"), Human("Ben") }, new ScriptedDiceRoller(1, 2), new InstantDelay(), new MemorySettings());
        await session.StartAsync();
        await session.RollAsync();
        Assert.Equal(1, session.State.CurrentSeat);
        Assert.True(session.State.CanRoll);
        Assert.True(session.State.CurrentSeatView.IsLocalHuman);
    }

    [Fact]
    public async Task An_interrupted_game_resumes_exactly_where_it_was()
    {
        var settings = new MemorySettings();
        settings.SetBool(SettingKeys.AutoMoveSingle, false);
        var session = OfflineGameSession.NewGame(new[] { Human(), Bot("Bot", BotDifficulty.Hard) }, new ScriptedDiceRoller(6), new InstantDelay(), settings);
        await session.StartAsync();
        await session.RollAsync();
        await session.MoveAsync(2);
        var before = session.EngineState;
        await session.DisposeAsync(); // app closed

        var resumed = OfflineGameSession.TryResume(new ScriptedDiceRoller(), new InstantDelay(), settings);
        Assert.NotNull(resumed);
        Assert.Equal(before.Version, resumed!.EngineState.Version);
        Assert.Equal(0, resumed.EngineState.Players[0].Pieces[2]);
        Assert.Equal("Bot", resumed.State.Seats[1].Name);
        Assert.True(resumed.State.Seats[1].IsBot);
    }

    [Fact]
    public void Corrupted_saves_are_discarded()
    {
        var settings = new MemorySettings();
        settings.SetString(SettingKeys.OfflineSave, "{not json");
        Assert.Null(OfflineGameSession.TryResume(new CryptoDiceRoller(), new InstantDelay(), settings));
        Assert.Null(settings.GetString(SettingKeys.OfflineSave));
    }

    [Fact]
    public async Task Notices_explain_when_a_roll_has_no_moves()
    {
        var session = OfflineGameSession.NewGame(new[] { Human(), Human("B") }, new ScriptedDiceRoller(3), new InstantDelay(), new MemorySettings());
        var notices = new List<string>();
        session.Notice += notices.Add;
        await session.StartAsync();
        await session.RollAsync();
        Assert.Contains(notices, n => n.Contains("no moves"));
    }
}

public class OnlineGameSessionTests
{
    [Fact]
    public async Task Intents_carry_the_displayed_version_and_only_legal_pieces_are_sent()
    {
        var conn = new FakeConnection();
        var session = new OnlineGameSession(conn, FakeConnection.Snapshot(5), new InlineMainThread());
        await session.StartAsync();
        Assert.True(session.State.CanRoll);
        await session.RollAsync();
        Assert.Contains("roll:m1:5", conn.Calls);

        conn.PushState(FakeConnection.Snapshot(6, phase: GamePhase.AwaitingMove, legal: new[] { 2 }, pendingRoll: 6));
        await session.MoveAsync(1); // not legal → not sent
        await session.MoveAsync(2);
        Assert.DoesNotContain("move:m1:1:6", conn.Calls);
        Assert.Contains("move:m1:2:6", conn.Calls);
    }

    [Fact]
    public async Task Cannot_act_on_the_opponents_turn()
    {
        var conn = new FakeConnection();
        var session = new OnlineGameSession(conn, FakeConnection.Snapshot(1, currentSeat: 1), new InlineMainThread());
        await session.RollAsync();
        Assert.DoesNotContain(conn.Calls, c => c.StartsWith("roll"));
        Assert.False(session.State.CanRoll);
    }

    [Fact]
    public void Older_or_foreign_snapshots_are_ignored()
    {
        var conn = new FakeConnection();
        var session = new OnlineGameSession(conn, FakeConnection.Snapshot(10), new InlineMainThread());
        conn.PushState(FakeConnection.Snapshot(9, currentSeat: 1));
        Assert.Equal(10, session.State.Version);
        conn.PushState(FakeConnection.Snapshot(11, currentSeat: 1) with { MatchId = "other" });
        Assert.Equal(10, session.State.Version);
    }

    [Fact]
    public void Reconnecting_resyncs_the_board_from_the_server()
    {
        var conn = new FakeConnection { ResyncResult = FakeConnection.Snapshot(20, currentSeat: 1) };
        var session = new OnlineGameSession(conn, FakeConnection.Snapshot(10), new InlineMainThread());
        var notices = new List<string>();
        session.Notice += notices.Add;
        conn.PushStatus(ConnectionStatus.Reconnecting);
        conn.PushStatus(ConnectionStatus.Connected);
        Assert.Contains("resync:m1", conn.Calls);
        Assert.Equal(20, session.State.Version);
        Assert.Contains(notices, n => n.Contains("Reconnected"));
    }

    [Fact]
    public void Server_errors_are_shown_but_stale_versions_are_silent()
    {
        var conn = new FakeConnection { NextError = new ServerError(ErrorCode.StaleVersion, "stale") };
        var session = new OnlineGameSession(conn, FakeConnection.Snapshot(1), new InlineMainThread());
        var notices = new List<string>();
        session.Notice += notices.Add;
        session.RollAsync().GetAwaiter().GetResult();
        Assert.Empty(notices);
        conn.NextError = new ServerError(ErrorCode.NotYourTurn, "It's not your turn.");
        session.RollAsync().GetAwaiter().GetResult();
        Assert.Contains("It's not your turn.", notices);
    }

    [Fact]
    public void Match_end_is_reported_once_with_server_rewards()
    {
        var conn = new FakeConnection();
        var session = new OnlineGameSession(conn, FakeConnection.Snapshot(1), new InlineMainThread()) { IsPrivate = true };
        var results = new List<GameResult>();
        session.Ended += results.Add;
        var info = new MatchEndedInfo("m1", new[] { 0, 1 }, 1, 100, 50, "won");
        conn.PushEnded(info);
        conn.PushEnded(info);
        var r = Assert.Single(results);
        Assert.Equal(100, r.CoinsAwarded);
        Assert.True(r.IsPrivate);
    }
}
