using LudoCrown.Core.Engine;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using LudoCrown.Server.Data;
using LudoCrown.Server.League;
using LudoCrown.Server.Matches;

namespace LudoCrown.Server.Tests;

public class BoostServerTests
{
    private static async Task<(ServerHarness H, Guid A, Guid B, string Id)> CasualAsync(int shieldsForA = 2, MatchMode mode = MatchMode.Casual)
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync("Alice");
        var b = await h.NewUserAsync("Bobby");
        if (shieldsForA > 0) await h.GrantAsync(a, shields: shieldsForA);
        await h.Matches.OnConnectedAsync(a);
        await h.Matches.OnConnectedAsync(b);
        var id = await h.Matches.StartMatchAsync(mode, new Guid?[] { a, b }, mode == MatchMode.Private ? "ABCDEF" : null);
        return (h, a, b, id.ToString("N"));
    }

    [Fact]
    public async Task Casual_and_private_matches_allow_one_boost_ranked_none()
    {
        Assert.Equal(1, MatchService.RulesFor(MatchMode.Casual, GameMode.Classic).MaxBoosts);
        Assert.Equal(1, MatchService.RulesFor(MatchMode.Private, GameMode.Quick).MaxBoosts);
        Assert.Equal(0, MatchService.RulesFor(MatchMode.Matchmaking, GameMode.Classic).MaxBoosts);
        Assert.Equal(7, MatchService.RulesFor(MatchMode.Casual, GameMode.Classic).MaxRerolls); // reroll rules unchanged
        Assert.Equal(5, MatchService.RulesFor(MatchMode.Matchmaking, GameMode.Quick).MaxRerolls);
    }

    [Fact]
    public async Task Shield_is_consumed_once_and_visible_to_everyone()
    {
        var (h, a, b, id) = await CasualAsync();
        var snapA = h.Notifier.LastSnapshot(a)!;
        Assert.Equal(BoostStatus.Available, snapA.BoostStatus);
        Assert.Equal(2, snapA.YourShieldBottles);
        Assert.Equal(MatchQueue.Casual, snapA.Queue);

        var req = Guid.NewGuid().ToString();
        var r = await h.Matches.UseBoostAsync(a, id, snapA.Version, req);
        Assert.Null(r.Error);
        Assert.Equal(1, r.BottlesLeft);
        Assert.Equal(0, r.BoostsRemaining);
        Assert.True(h.Notifier.LastSnapshot(b)!.Seats[0].ShieldActive); // opponent sees it
        Assert.Contains(h.Notifier.LastSnapshot(b)!.Events, e => e.Type == GameEventType.ShieldActivated);
        Assert.Equal(1, (await h.Store.GetBalancesAsync(a))!.Count(Boosts.ShieldId));
        Assert.Contains(h.Store.Ledger, l => l.User == a && l.Entry.Reason == Economy.Reasons.BoostUse && l.Entry.Delta == -1 && l.Entry.MatchId is not null);

        // Retry of the same request (e.g. after reconnecting): same answer, no second bottle used.
        var retry = await h.Matches.UseBoostAsync(a, id, snapA.Version, req);
        Assert.Equal(r, retry);
        Assert.Equal(1, (await h.Store.GetBalancesAsync(a))!.Count(Boosts.ShieldId));

        // A new request is refused by the per-match limit, nothing consumed.
        var again = await h.Matches.UseBoostAsync(a, id, h.Notifier.LastSnapshot(a)!.Version, Guid.NewGuid().ToString());
        Assert.Equal(ErrorCode.NoBoostsLeft, again.Error!.Code);
        Assert.Equal(1, (await h.Store.GetBalancesAsync(a))!.Count(Boosts.ShieldId));
    }

    [Fact]
    public async Task Ranked_matches_reject_boosts_without_consuming()
    {
        var h = new ServerHarness();
        var (a, _, matchId) = await h.StartTwoPlayerAsync();
        await h.GrantAsync(a, shields: 1);
        await h.Matches.OnBalancesChangedAsync(a);
        var snap = h.Notifier.LastSnapshot(a)!;
        Assert.Equal(BoostStatus.NotInThisMatch, snap.BoostStatus);
        var r = await h.Matches.UseBoostAsync(a, matchId.ToString("N"), snap.Version, Guid.NewGuid().ToString());
        Assert.Equal(ErrorCode.BoostNotAllowed, r.Error!.Code);
        Assert.Equal(1, (await h.Store.GetBalancesAsync(a))!.Count(Boosts.ShieldId));
    }

    [Fact]
    public async Task Boost_is_rejected_without_a_bottle_out_of_turn_after_rolling_and_when_stale()
    {
        var (h, a, b, id) = await CasualAsync(shieldsForA: 0);
        var snap = h.Notifier.LastSnapshot(a)!;
        Assert.Equal(BoostStatus.NoBottles, snap.BoostStatus);
        Assert.Equal(ErrorCode.NoBoostItem, (await h.Matches.UseBoostAsync(a, id, snap.Version, Guid.NewGuid().ToString())).Error!.Code);

        await h.GrantAsync(b, shields: 1);
        await h.Matches.OnBalancesChangedAsync(b);
        Assert.Equal(BoostStatus.NotYourTurn, h.Notifier.LastSnapshot(b)!.BoostStatus);
        Assert.Equal(ErrorCode.NotYourTurn, (await h.Matches.UseBoostAsync(b, id, snap.Version, Guid.NewGuid().ToString())).Error!.Code);

        await h.GrantAsync(a, shields: 1);
        await h.Matches.OnBalancesChangedAsync(a);
        Assert.Equal(ErrorCode.StaleVersion, (await h.Matches.UseBoostAsync(a, id, snap.Version + 5, Guid.NewGuid().ToString())).Error!.Code);
        h.Dice.Enqueue(6);
        await h.Matches.RollAsync(a, id, h.Notifier.LastSnapshot(a)!.Version);
        Assert.Equal(BoostStatus.OnlyBeforeRolling, h.Notifier.LastSnapshot(a)!.BoostStatus);
        Assert.Equal(ErrorCode.BoostNotAllowed, (await h.Matches.UseBoostAsync(a, id, h.Notifier.LastSnapshot(a)!.Version, Guid.NewGuid().ToString())).Error!.Code);
        Assert.Equal(1, (await h.Store.GetBalancesAsync(a))!.Count(Boosts.ShieldId));
        Assert.Equal(1, (await h.Store.GetBalancesAsync(b))!.Count(Boosts.ShieldId));
    }

    [Fact]
    public async Task Boost_survives_reconnect_and_server_restart_and_is_saved_with_the_result()
    {
        var (h, a, b, id) = await CasualAsync(mode: MatchMode.Private);
        await h.Matches.UseBoostAsync(a, id, h.Notifier.LastSnapshot(a)!.Version, Guid.NewGuid().ToString());
        await h.Matches.OnDisconnectedAsync(a);
        await h.Matches.OnConnectedAsync(a);
        Assert.True(h.Notifier.LastSnapshot(a)!.Seats[0].ShieldActive);

        var restarted = h.NewMatchService();
        await restarted.RecoverAsync();
        var room = restarted.GetRoom(Guid.Parse(id))!;
        Assert.True(room.State.Players[0].ShieldActive);
        Assert.Equal(1, room.State.Rules.MaxBoosts);
        Assert.Equal(1, room.Seats[0].ShieldBottles);
        Assert.Equal(ErrorCode.NoBoostsLeft, (await restarted.UseBoostAsync(a, id, room.State.Version, Guid.NewGuid().ToString())).Error!.Code);
    }

    [Fact]
    public async Task Bottles_bought_mid_match_show_up_immediately()
    {
        var (h, a, _, _) = await CasualAsync(shieldsForA: 0);
        await h.GrantAsync(a, coins: 2000);
        await h.Purchases.BuyWithCoinsAsync(a, "shield_single", Guid.NewGuid().ToString());
        await h.Matches.OnBalancesChangedAsync(a);
        Assert.Equal(1, h.Notifier.LastSnapshot(a)!.YourShieldBottles);
        Assert.Equal(BoostStatus.Available, h.Notifier.LastSnapshot(a)!.BoostStatus);
    }

    [Fact]
    public async Task Casual_queue_is_separate_from_ranked()
    {
        var h = new ServerHarness();
        var a = await h.NewUserAsync("Alice");
        var b = await h.NewUserAsync("Bobby");
        await h.Matchmaking.EnqueueAsync(a, 2, GameMode.Classic, MatchQueue.Ranked);
        var s = (await h.Matchmaking.EnqueueAsync(b, 2, GameMode.Classic, MatchQueue.Casual)).Status!;
        Assert.Equal(MatchmakingState.Searching, s.State);
        Assert.Equal(MatchQueue.Casual, s.Queue);
        var c = await h.NewUserAsync("Carol");
        var matched = (await h.Matchmaking.EnqueueAsync(c, 2, GameMode.Classic, MatchQueue.Casual)).Status!;
        Assert.Equal(MatchmakingState.Matched, matched.State);
        var room = h.Matches.GetRoom(Guid.Parse(matched.MatchId!))!;
        Assert.Equal(MatchMode.Casual, room.Mode);
        Assert.Equal(1, room.State.Rules.MaxBoosts);
        Assert.Null(h.Matches.ActiveMatchFor(a)); // ranked player still waiting
    }
}

public class QuickChatTests
{
    [Fact]
    public async Task Chat_uses_presets_with_rate_limits_and_a_window_cap()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var m = id.ToString("N");
        Assert.Equal(ErrorCode.InvalidRequest, (await h.Matches.ReactAsync(a, m, "call me at 555-0100"))!.Code);
        Assert.All(Reactions.Items, i => Assert.True(i.Text.Length is > 0 and <= 20));
        for (var i = 0; i < Reactions.MaxPerWindow; i++)
        {
            Assert.Null(await h.Matches.ReactAsync(a, m, Reactions.All[i]));
            h.Time.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.Equal(ErrorCode.RateLimited, (await h.Matches.ReactAsync(a, m, "gg"))!.Code); // window full
        h.Time.Advance(TimeSpan.FromSeconds(Reactions.WindowSeconds));
        Assert.Null(await h.Matches.ReactAsync(a, m, "gg"));
        Assert.Contains(h.Notifier.Reactions, r => r.User == b && r.Reaction.ReactionId == "gg" && r.Reaction.Seat == 0);
    }

    [Fact]
    public async Task Players_with_chat_off_neither_send_nor_receive()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        var m = id.ToString("N");
        await h.Store.UpdatePrivacyAsync(b, null, null, false);
        await h.Matches.OnChatSettingChangedAsync(b, false);
        Assert.Null(await h.Matches.ReactAsync(a, m, "hello"));
        Assert.DoesNotContain(h.Notifier.Reactions, r => r.User == b);
        Assert.Contains(h.Notifier.Reactions, r => r.User == a); // sender gets the echo
        h.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ErrorCode.ChatDisabled, (await h.Matches.ReactAsync(b, m, "hello"))!.Code);
    }

    [Fact]
    public async Task Server_kill_switch_disables_chat_and_blocks_hide_messages()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        await h.Store.BlockAsync(b, a, h.Time.GetUtcNow());
        Assert.Null(await h.Matches.ReactAsync(a, id.ToString("N"), "hello"));
        Assert.DoesNotContain(h.Notifier.Reactions, r => r.User == b);

        h.MatchOptions.ChatEnabled = false;
        h.Time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(ErrorCode.ChatDisabled, (await h.Matches.ReactAsync(b, id.ToString("N"), "hello"))!.Code);
    }

    [Fact]
    public async Task Left_players_cannot_chat()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync();
        await h.Matches.LeaveAsync(a, id.ToString("N"));
        Assert.Equal(ErrorCode.NotInMatch, (await h.Matches.ReactAsync(a, id.ToString("N"), "hello"))!.Code);
    }
}

public class ProfileAndStatsTests
{
    private static ParticipantOutcome O(int seat, Guid? user, int place, ParticipantResult r) => new(seat, user, place, r, 0, 0, 0);

    private static async Task CompleteAsync(InMemoryStore s, GameMode mode, Guid a, Guid b, ParticipantResult ra, ParticipantResult rb)
    {
        var id = Guid.NewGuid();
        await s.CreateMatchAsync(new MatchRecord(id, MatchMode.Matchmaking, 2, null, DateTimeOffset.UtcNow, new[]
        {
            new MatchParticipantRecord(0, a, false, "A", "Ruby"), new MatchParticipantRecord(1, b, false, "B", "Topaz"),
        }, mode), "{}");
        await s.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, DateTimeOffset.UtcNow, null, "{}",
            new[] { O(0, a, ra == ParticipantResult.Win ? 1 : 2, ra), O(1, b, rb == ParticipantResult.Win ? 1 : 2, rb) }, 2, mode));
    }

    [Fact]
    public async Task Classic_and_quick_stats_are_separate_with_defined_streaks_and_abandons()
    {
        var s = new InMemoryStore();
        var a = (await s.CreateUserAsync(false, "Alpha", "crown")).Id;
        var b = (await s.CreateUserAsync(false, "Bravo", "crown")).Id;
        await CompleteAsync(s, GameMode.Classic, a, b, ParticipantResult.Win, ParticipantResult.Loss);
        await CompleteAsync(s, GameMode.Classic, a, b, ParticipantResult.Win, ParticipantResult.Loss);
        await CompleteAsync(s, GameMode.Quick, a, b, ParticipantResult.Abandoned, ParticipantResult.Win);
        await CompleteAsync(s, GameMode.Classic, a, b, ParticipantResult.Win, ParticipantResult.Loss);

        var modes = await s.GetModeStatsAsync(a);
        var classic = modes.Single(m => m.Mode == GameMode.Classic);
        var quick = modes.Single(m => m.Mode == GameMode.Quick);
        Assert.Equal((3, 3, 0, 0, 3, 3), (classic.GamesPlayed, classic.GamesWon, classic.GamesLost, classic.Abandons, classic.CurrentWinStreak, classic.BestWinStreak));
        Assert.Equal((1, 0, 0, 1, 0), (quick.GamesPlayed, quick.GamesWon, quick.GamesLost, quick.Abandons, quick.CurrentWinStreak));
        Assert.Equal(100, classic.WinRatePercent);
        var overall = await s.GetStatsAsync(a);
        Assert.Equal(4, overall.GamesPlayed);
        Assert.Equal(1, overall.CurrentWinStreak); // the abandon reset the overall streak
        Assert.Equal(3, (await s.GetModeStatsAsync(b)).Single(m => m.Mode == GameMode.Classic).GamesLost);
    }

    [Fact]
    public async Task Privacy_settings_default_open_and_update_partially()
    {
        var s = new InMemoryStore();
        var a = (await s.CreateUserAsync(false, "Alpha", "crown")).Id;
        Assert.Equal(PrivacySettings.Default, await s.GetPrivacyAsync(a));
        var p = await s.UpdatePrivacyAsync(a, false, null, null);
        Assert.Equal(new PrivacySettings(false, true, true), p);
        p = await s.UpdatePrivacyAsync(a, null, false, false);
        Assert.Equal(new PrivacySettings(false, false, false), p);
    }

    [Fact]
    public async Task Seats_show_public_ids_and_frames_never_account_ids()
    {
        var h = new ServerHarness();
        var (a, b, _) = await h.StartTwoPlayerAsync();
        var snap = h.Notifier.LastSnapshot(a)!;
        var profileB = await h.Store.GetProfileAsync(b);
        Assert.Equal(profileB!.FriendCode, snap.Seats[1].PublicId);
        var json = System.Text.Json.JsonSerializer.Serialize(snap);
        Assert.DoesNotContain(a.ToString(), json);
        Assert.DoesNotContain(b.ToString(), json);
        Assert.DoesNotContain(a.ToString("N"), json.Replace(snap.MatchId, ""));
    }

    [Fact]
    public async Task History_is_paged_newest_first()
    {
        var s = new InMemoryStore();
        var a = (await s.CreateUserAsync(false, "Alpha", "crown")).Id;
        var b = (await s.CreateUserAsync(false, "Bravo", "crown")).Id;
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            await s.CreateMatchAsync(new MatchRecord(id, MatchMode.Matchmaking, 2, null, start.AddMinutes(i), new[]
            {
                new MatchParticipantRecord(0, a, false, "A", "Ruby"), new MatchParticipantRecord(1, b, false, "B", "Topaz"),
            }), "{}");
            await s.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, start.AddMinutes(i + 1), a, "{}",
                new[] { O(0, a, 1, ParticipantResult.Win), O(1, b, 2, ParticipantResult.Loss) }, 2));
        }
        var page1 = await s.GetHistoryAsync(a, 2);
        Assert.Equal(2, page1.Count);
        Assert.True(page1[0].StartedAt > page1[1].StartedAt);
        var page2 = await s.GetHistoryAsync(a, 2, page1[^1].StartedAt);
        Assert.Equal(2, page2.Count);
        Assert.True(page2[0].StartedAt < page1[^1].StartedAt);
        Assert.Single(await s.GetHistoryAsync(a, 2, page2[^1].StartedAt));
    }
}

public class LeagueTests
{
    [Fact]
    public void Points_and_rewards_follow_the_documented_table()
    {
        Assert.Equal(20, LeagueRules.PointsFor(2, 1, ParticipantResult.Win));
        Assert.Equal(-10, LeagueRules.PointsFor(2, 2, ParticipantResult.Loss));
        Assert.Equal(30, LeagueRules.PointsFor(4, 1, ParticipantResult.Win));
        Assert.Equal(10, LeagueRules.PointsFor(4, 2, ParticipantResult.Loss));
        Assert.Equal(-5, LeagueRules.PointsFor(4, 3, ParticipantResult.Loss));
        Assert.Equal(-15, LeagueRules.PointsFor(4, 4, ParticipantResult.Loss));
        Assert.Equal(-25, LeagueRules.PointsFor(4, 2, ParticipantResult.Abandoned));
        Assert.Equal(6, LeagueRules.Divisions.Count);
        Assert.Null(LeagueRules.RewardFor(0, 11, 5));
        Assert.Null(LeagueRules.RewardFor(0, 1, 0)); // must have played
        // Monotonic: higher division / better band never pays less.
        for (var d = 0; d < 6; d++)
        for (var band = 0; band < 3; band++)
        {
            var r = LeagueRules.RewardCell(d, band);
            if (d > 0) Assert.True(r.Coins >= LeagueRules.RewardCell(d - 1, band).Coins && r.Gems >= LeagueRules.RewardCell(d - 1, band).Gems);
            if (band > 0) Assert.True(r.Coins <= LeagueRules.RewardCell(d, band - 1).Coins);
        }
    }

    private static StandingRecord S(int i, int division, int points, int matches, int wins = 0) =>
        new(1, new Guid(i, 0, 0, new byte[8]), 1, division, points, matches, wins, DateTimeOffset.UnixEpoch.AddMinutes(i));

    [Fact]
    public void Promotion_and_demotion_rules()
    {
        var group = Enumerable.Range(1, 20).Select(i => S(i, 2, 400 - i * 10, i == 3 ? 4 : 10)).ToList();
        var results = LeagueRules.ResultsForGroup(group).ToDictionary(r => r.UserId);
        Assert.Equal(SeasonOutcome.Promoted, results[group[0].UserId].Outcome);
        Assert.Equal(3, results[group[0].UserId].NextDivision);
        Assert.Equal(SeasonOutcome.Stayed, results[group[2].UserId].Outcome); // rank 3 but only 4 matches
        Assert.Equal(SeasonOutcome.Demoted, results[group[19].UserId].Outcome);
        Assert.Equal(SeasonOutcome.Demoted, results[group[15].UserId].Outcome);
        Assert.Equal(SeasonOutcome.Stayed, results[group[14].UserId].Outcome);
        Assert.Equal(1, results[group[0].UserId].FinalRank);

        var small = Enumerable.Range(1, 6).Select(i => S(i, 1, 100 - i, 6)).ToList();
        Assert.DoesNotContain(LeagueRules.ResultsForGroup(small), r => r.Outcome == SeasonOutcome.Demoted); // groups < 15 never demote

        var top = new[] { S(1, LeagueRules.TopDivision, 500, 20) };
        Assert.Equal(SeasonOutcome.Stayed, LeagueRules.ResultsForGroup(top)[0].Outcome);
        var bottom = Enumerable.Range(1, 16).Select(i => S(i, 0, 100 - i, 6)).ToList();
        Assert.DoesNotContain(LeagueRules.ResultsForGroup(bottom), r => r.Outcome == SeasonOutcome.Demoted); // Squire is the floor
    }

    [Fact]
    public void Ties_break_on_wins_then_who_got_there_first()
    {
        var a = S(1, 0, 100, 5, wins: 3) with { LastPointsAt = DateTimeOffset.UnixEpoch.AddHours(2) };
        var b = S(2, 0, 100, 5, wins: 4) with { LastPointsAt = DateTimeOffset.UnixEpoch.AddHours(3) };
        var c = S(3, 0, 100, 5, wins: 3) with { LastPointsAt = DateTimeOffset.UnixEpoch.AddHours(1) };
        Assert.Equal(new[] { b.UserId, c.UserId, a.UserId }, LeagueRules.Rank(new[] { a, b, c }).Select(s => s.UserId));
    }

    private static async Task<(ServerHarness H, Guid A, Guid B)> PlayRankedAsync(ServerHarness? existing = null)
    {
        var h = existing ?? new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync();
        await h.Matches.LeaveAsync(b, id.ToString("N")); // Bob leaves: Alice plays on vs the bot seat
        await h.Matches.LeaveAsync(a, id.ToString("N")); // both gone: match ends as abandoned
        return (h, a, b);
    }

    [Fact]
    public async Task Ranked_results_with_two_humans_update_league_standings()
    {
        var h = new ServerHarness();
        var (a, b, id) = await h.StartTwoPlayerAsync(mode: GameMode.Quick);
        var room = h.Matches.GetRoom(id)!;
        // Alice one step from the crown in Quick mode.
        var s = room.State.Clone();
        s.Players[0].Pieces[0] = 54;
        room.State = s;
        h.Dice.Enqueue(2);
        await h.Matches.RollAsync(a, id.ToString("N"), room.State.Version);
        await h.Matches.MoveAsync(a, id.ToString("N"), 0, room.State.Version);
        Assert.True(room.Ended);

        var season = (await h.Store.GetCurrentSeasonAsync())!;
        var sa = (await h.Store.GetStandingAsync(season.Id, a))!;
        var sb = (await h.Store.GetStandingAsync(season.Id, b))!;
        Assert.Equal(20, sa.Points);
        Assert.Equal(0, sb.Points); // -10 floored at 0
        Assert.Equal(1, sa.Wins);
        Assert.Equal(sa.GroupId, sb.GroupId);
        Assert.Contains(h.Notifier.Ended, e => e.User == a && e.Info.LeaguePoints == 20 && e.Info.Queue == MatchQueue.Ranked);
        Assert.Equal(20, (await h.Store.GetHistoryAsync(a, 5))[0].LeaguePoints);
    }

    [Fact]
    public async Task Casual_private_and_bot_matches_give_no_league_points()
    {
        var h = new ServerHarness();
        var (a, _, id) = await h.StartTwoPlayerAsync(matchMode: MatchMode.Casual);
        await h.Matches.LeaveAsync(a, id.ToString("N"));
        var lone = await h.NewUserAsync("Lonely");
        await h.Matches.OnConnectedAsync(lone);
        var botMatch = await h.Matches.StartMatchAsync(MatchMode.Matchmaking, new Guid?[] { lone, null }, null);
        await h.Matches.LeaveAsync(lone, botMatch.ToString("N"));
        var season = await h.Store.GetCurrentSeasonAsync();
        if (season is not null)
        {
            Assert.Null(await h.Store.GetStandingAsync(season.Id, a));
            Assert.Null(await h.Store.GetStandingAsync(season.Id, lone));
        }
        Assert.All(h.Notifier.Ended, e => Assert.Null(e.Info.LeaguePoints));
    }

    [Fact]
    public async Task Abandoning_a_ranked_match_costs_points()
    {
        var (h, a, b) = await PlayRankedAsync();
        var season = (await h.Store.GetCurrentSeasonAsync())!;
        Assert.Equal(1, (await h.Store.GetStandingAsync(season.Id, b))!.MatchesPlayed);
        Assert.Equal(0, (await h.Store.GetStandingAsync(season.Id, b))!.Points);
    }

    [Fact]
    public async Task Season_end_promotes_rewards_once_and_opens_the_next_season()
    {
        var h = new ServerHarness();
        var season = (await h.League.EnsureSeasonAsync())!;
        Assert.Equal(1, season.Number);
        var players = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var u = await h.NewUserAsync($"Player{i}");
            players.Add(u);
            await h.League.EnsureStandingAsync(season.Id, u);
        }
        // Give points via completed ranked results: player i wins (6 - i) matches against the last player.
        foreach (var (u, i) in players.Select((u, i) => (u, i)))
        {
            for (var k = 0; k < 6 - i; k++)
            {
                var id = Guid.NewGuid();
                var loser = players[^1] == u ? players[0] : players[^1];
                await h.Store.CreateMatchAsync(new MatchRecord(id, MatchMode.Matchmaking, 2, null, h.Time.GetUtcNow(), new[]
                {
                    new MatchParticipantRecord(0, u, false, "W", "Ruby"), new MatchParticipantRecord(1, loser, false, "L", "Topaz"),
                }), "{}");
                await h.Store.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, h.Time.GetUtcNow(), u, "{}", new[]
                {
                    new ParticipantOutcome(0, u, 1, ParticipantResult.Win, 0, 0, 0, LeaguePoints: 20),
                    new ParticipantOutcome(1, loser, 2, ParticipantResult.Loss, 0, 0, 0, LeaguePoints: -10),
                }, 2, GameMode.Classic, season.Id));
                h.Time.Advance(TimeSpan.FromMinutes(1));
            }
        }
        var gemsBefore = await h.Store.GetGemsAsync(players[0]);

        h.Time.Advance(TimeSpan.FromDays(15));
        var next = (await h.League.EnsureSeasonAsync())!;
        Assert.Equal(2, next.Number);
        Assert.Equal(SeasonStatus.Completed, (await h.Store.GetSeasonByNumberAsync(1))!.Status);

        var top = (await h.Store.GetStandingAsync(season.Id, players[0]))!;
        Assert.Equal(1, top.FinalRank);
        Assert.Equal(SeasonOutcome.Promoted, top.Outcome); // rank 1 with 6+ matches
        Assert.Equal(1, await h.Store.GetDivisionAsync(players[0]));
        var reward = (await h.Store.GetRewardAsync(season.Id, players[0]))!;
        Assert.NotNull(reward.GrantedAt);
        Assert.Equal(gemsBefore + LeagueRules.RewardCell(0, 0).Gems, await h.Store.GetGemsAsync(players[0]));
        Assert.Contains(h.Store.Ledger, l => l.User == players[0] && l.Entry.Reason == Economy.Reasons.LeagueReward);

        // Running finalisation again (e.g. after a crash) grants nothing twice.
        await h.Store.SetSeasonStatusAsync(season.Id, SeasonStatus.Finalizing, null);
        await h.League.EnsureSeasonAsync();
        Assert.Equal(gemsBefore + LeagueRules.RewardCell(0, 0).Gems, await h.Store.GetGemsAsync(players[0]));
        Assert.Single(h.Store.Ledger, l => l.User == players[0] && l.Entry.Reason == Economy.Reasons.LeagueReward && l.Entry.Asset == Economy.Gems);

        // Next season: the promoted player starts in Knight.
        Assert.Equal(1, (await h.League.EnsureStandingAsync(next.Id, players[0])).Division);
    }

    [Fact]
    public async Task Points_after_the_season_froze_are_not_counted()
    {
        var h = new ServerHarness();
        var season = (await h.League.EnsureSeasonAsync())!;
        var u = await h.NewUserAsync("Late");
        await h.League.EnsureStandingAsync(season.Id, u);
        await h.Store.SetSeasonStatusAsync(season.Id, SeasonStatus.Finalizing, null);
        var id = Guid.NewGuid();
        await h.Store.CreateMatchAsync(new MatchRecord(id, MatchMode.Matchmaking, 2, null, h.Time.GetUtcNow(), new[]
        {
            new MatchParticipantRecord(0, u, false, "W", "Ruby"), new MatchParticipantRecord(1, null, true, "Bot", "Topaz"),
        }), "{}");
        await h.Store.CompleteMatchAsync(new MatchCompletion(id, MatchStatus.Completed, h.Time.GetUtcNow(), u, "{}", new[]
        {
            new ParticipantOutcome(0, u, 1, ParticipantResult.Win, 0, 0, 0, LeaguePoints: 20),
            new ParticipantOutcome(1, null, 2, ParticipantResult.Loss, 0, 0, 0),
        }, 2, GameMode.Classic, season.Id));
        Assert.Equal(0, (await h.Store.GetStandingAsync(season.Id, u))!.Points);
    }

    [Fact]
    public async Task Groups_hold_at_most_thirty_players()
    {
        var h = new ServerHarness();
        var season = (await h.League.EnsureSeasonAsync())!;
        var groups = new HashSet<long>();
        for (var i = 0; i < 31; i++) groups.Add((await h.League.EnsureStandingAsync(season.Id, await h.NewUserAsync($"P{i:00}x"))).GroupId);
        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public async Task Disabled_league_gives_no_season()
    {
        var h = new ServerHarness();
        h.LeagueOptions.Enabled = false;
        Assert.Null(await h.League.GetActiveSeasonAsync());
        Assert.Null(await h.League.EnsureSeasonAsync());
    }
}
