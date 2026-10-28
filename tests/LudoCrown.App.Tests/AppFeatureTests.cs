using System.Net;
using LudoCrown.App.Api;
using LudoCrown.App.Game;
using LudoCrown.App.Services;
using LudoCrown.App.ViewModels;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Tests;

public class RerollAndBoostClientTests
{
    private static MatchSnapshot Held(long version, RerollStatus status = RerollStatus.Available, int gems = 10) =>
        FakeConnection.Snapshot(version, 0, GamePhase.AwaitingMove, 0, pendingRoll: 3) with { RerollStatus = status, YourGems = gems, MaxRerolls = 7, CanPass = true };

    private static (AppHarness H, GameViewModel Vm) Game(MatchSnapshot snap)
    {
        var h = new AppHarness();
        h.State.CurrentSession = new OnlineGameSession(h.Connection, snap, new InlineMainThread());
        var vm = new GameViewModel(h.State, new NullFeedback(), h.Nav, h.Dialogs, new InlineMainThread(), h.Settings, new InstantDelay());
        return (h, vm);
    }

    [Fact]
    public async Task Reroll_asks_for_confirmation_and_sends_an_idempotency_key()
    {
        var (h, vm) = Game(Held(5));
        await vm.OnAppearingAsync();
        Assert.True(vm.ShowRerollBar);
        Assert.True(vm.CanReroll);
        Assert.True(vm.CanPass);
        Assert.Equal("10", vm.GemsText);
        Assert.Equal("Rerolls 0/7", vm.RerollCountText);

        h.Dialogs.ConfirmAnswers.Enqueue(false); // "Keep my roll"
        vm.RerollCommand.Execute(null);
        Assert.DoesNotContain(h.Connection.Calls, c => c.StartsWith("reroll"));

        h.Dialogs.ConfirmAnswers.Enqueue(true);
        vm.RerollCommand.Execute(null);
        var call = Assert.Single(h.Connection.Calls, c => c.StartsWith("reroll"));
        Assert.True(Guid.TryParse(call.Split(':')[2], out _));
    }

    [Fact]
    public async Task A_dropped_reroll_is_retried_with_the_same_key_and_a_new_state_gets_a_new_key()
    {
        var (h, vm) = Game(Held(5));
        h.Settings.SetBool(SettingKeys.ConfirmGemSpend, false);
        await vm.OnAppearingAsync();
        h.Connection.ThrowNextPaidCall = true;
        vm.RerollCommand.Execute(null);
        vm.RerollCommand.Execute(null);
        var calls = h.Connection.Calls.Where(c => c.StartsWith("reroll")).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(calls[0], calls[1]); // same version, same request id → server charges once

        h.Connection.PushState(Held(6));
        vm.RerollCommand.Execute(null);
        var third = h.Connection.Calls.Where(c => c.StartsWith("reroll")).Last();
        Assert.NotEqual(calls[0].Split(':')[2], third.Split(':')[2]);
    }

    [Theory]
    [InlineData(RerollStatus.InsufficientGems, "Not enough gems")]
    [InlineData(RerollStatus.NoRerollsLeft, "used all 7 rerolls")]
    [InlineData(RerollStatus.RollFirst, "Roll first")]
    [InlineData(RerollStatus.NotYourTurn, "only on your own turn")]
    public async Task Unavailable_rerolls_explain_why_and_call_nothing(RerollStatus status, string expected)
    {
        var (h, vm) = Game(Held(5, status));
        await vm.OnAppearingAsync();
        Assert.False(vm.CanReroll);
        Assert.Contains(expected, vm.RerollHint);
        vm.RerollCommand.Execute(null);
        Assert.DoesNotContain(h.Connection.Calls, c => c.StartsWith("reroll"));
    }

    [Fact]
    public async Task Pass_is_sent_for_a_held_roll()
    {
        var (h, vm) = Game(Held(5));
        await vm.OnAppearingAsync();
        Assert.Contains("reroll or pass", vm.Status);
        vm.PassCommand.Execute(null);
        Assert.Contains("pass:5", h.Connection.Calls);
    }

    [Fact]
    public async Task Shield_is_offered_in_casual_matches_with_its_exact_effect()
    {
        var snap = FakeConnection.Snapshot(3) with { Queue = MatchQueue.Casual, MaxBoosts = 1, BoostStatus = BoostStatus.Available, YourShieldBottles = 2 };
        var (h, vm) = Game(snap);
        await vm.OnAppearingAsync();
        Assert.True(vm.ShowBoost);
        Assert.True(vm.CanBoost);
        Assert.Equal("🛡 Shield (2)", vm.BoostButtonText);
        Assert.Equal("Classic · Casual", vm.ModeChip);
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        vm.BoostCommand.Execute(null);
        Assert.Single(h.Connection.Calls, c => c.StartsWith("boost:3:"));
    }

    [Fact]
    public async Task Ranked_matches_say_boosts_are_off()
    {
        var snap = FakeConnection.Snapshot(3) with { Queue = MatchQueue.Ranked, MaxBoosts = 0, BoostStatus = BoostStatus.NotInThisMatch, Mode = GameMode.Quick, MaxRerolls = 5 };
        var (h, vm) = Game(snap);
        await vm.OnAppearingAsync();
        Assert.False(vm.ShowBoost);
        Assert.True(vm.BoostsOffNotice);
        Assert.Equal("Quick · Ranked", vm.ModeChip);
        Assert.Contains("1 piece", vm.ModeText);
        vm.BoostCommand.Execute(null);
        Assert.DoesNotContain(h.Connection.Calls, c => c.StartsWith("boost"));
    }

    [Fact]
    public async Task Muted_players_chat_is_hidden()
    {
        var (h, vm) = Game(FakeConnection.Snapshot(1));
        await vm.OnAppearingAsync();
        var shown = new List<string>();
        vm.ReactionReceived += (r, _) => shown.Add(r.ReactionId);
        h.Connection.PushReaction(new ReactionInfo(1, "gg"));
        vm.ToggleMuteCommand.Execute(1);
        h.Connection.PushReaction(new ReactionInfo(1, "hello"));
        Assert.Equal(new[] { "gg" }, shown);
        Assert.Equal(12, vm.ChatPhrases.Count);
        Assert.Equal(8, vm.ChatEmoji.Count);
    }

    [Fact]
    public async Task Opening_a_seat_shows_the_public_profile()
    {
        var snap = FakeConnection.Snapshot(1) with
        {
            Seats = new[] { new SeatInfo(0, PlayerColor.Ruby, "Me", "crown", false, true, PublicId: "AAAAAAAA"), new SeatInfo(1, PlayerColor.Topaz, "Them", "lion", false, true, PublicId: "BBBBBBBB") },
        };
        var (h, vm) = Game(snap);
        await vm.OnAppearingAsync();
        h.Dialogs.SheetAnswers.Enqueue("View profile");
        vm.OpenPlayerCommand.Execute(1);
        Assert.Equal("BBBBBBBB", h.State.ViewedPlayerId);
        Assert.Equal(Routes.PlayerProfile, h.Nav.Last);
    }
}

public class ModeSelectionClientTests
{
    [Fact]
    public void Mode_picker_lists_only_implemented_modes_and_remembers_the_choice()
    {
        var state = new AppState();
        var settings = new MemorySettings();
        var picker = new ModePicker(state, settings);
        Assert.Equal(new[] { "Classic", "Quick" }, picker.Options.Select(o => o.Name));
        picker.SelectCommand.Execute(picker.Options[1]);
        Assert.Equal(GameMode.Quick, state.SelectedMode);
        Assert.True(picker.Options[1].IsSelected);
        Assert.Contains("1 piece", picker.SelectedSummary);
        Assert.Equal(GameMode.Quick, new ModePicker(new AppState(), settings).Options.Single(o => o.IsSelected).Mode);
    }

    [Fact]
    public async Task Matchmaking_and_private_tables_send_the_selected_mode_and_queue()
    {
        var h = new AppHarness();
        h.State.SelectedMode = GameMode.Quick;
        h.State.SelectedQueue = MatchQueue.Casual;
        var mm = new MatchmakingViewModel(h.Connection, h.State, h.Nav, new InlineMainThread());
        Assert.Equal("2-player Quick · Casual", mm.Title);
        await mm.OnAppearingAsync();
        Assert.Contains("find:2:Quick:Casual", h.Connection.Calls);

        h.State.PrivateRoomMode = PrivateRoomMode.Create;
        var room = new PrivateRoomViewModel(h.Connection, h.State, h.Nav, new NoShare(), new InlineMainThread());
        await room.OnAppearingAsync();
        Assert.Contains("create:2:Quick", h.Connection.Calls);
        Assert.StartsWith("Quick:", room.ModeText);
    }

    [Fact]
    public async Task Offline_games_use_the_selected_mode_and_explain_rerolls_are_online_only()
    {
        var h = new AppHarness();
        var setup = new OfflineSetupViewModel(h.State, h.Nav, h.Dialogs, h.Settings, new InstantDelay());
        setup.Modes.Select(GameMode.Quick);
        setup.PlayerCount = 2;
        setup.StartCommand.Execute(null);
        var session = Assert.IsType<OfflineGameSession>(h.State.CurrentSession);
        Assert.Equal(GameMode.Quick, session.State.Mode);
        Assert.Equal(1, session.EngineState.Rules.PiecesToWin);

        var vm = new GameViewModel(h.State, new NullFeedback(), h.Nav, h.Dialogs, new InlineMainThread(), h.Settings, new InstantDelay());
        await vm.OnAppearingAsync();
        Assert.False(vm.ShowRerollBar);
        Assert.False(vm.ShowBoost);
        Assert.True(vm.ShowOfflineNote);
        Assert.Equal("Quick · Offline", vm.ModeChip);
        Assert.Contains("online matches", RerollText.Reason(session.State));
    }

    [Fact]
    public async Task Results_show_mode_gems_and_league_points()
    {
        var h = new AppHarness();
        var seats = new[] { new SeatView(0, PlayerColor.Ruby, "Me", "crown", false, true, true, 1), new SeatView(1, PlayerColor.Topaz, "Them", "lion", false, true, false, 0) };
        h.State.LastResult = new GameResult(new[] { 0, 1 }, seats, 1, 100, 50, true, "m1", false, "won", GameMode.Quick, 1, 20, MatchKind.Ranked);
        var vm = new ResultsViewModel(h.State, h.Nav, h.Connection, h.Settings, new InstantDelay());
        await vm.OnAppearingAsync();
        Assert.Equal("Ranked · Quick", vm.Subtitle);
        Assert.Equal("+100 gold   +1 gem   +50 XP", vm.RewardsText);
        Assert.Equal("League: +20 points", vm.LeagueText);
        vm.PlayAgainCommand.Execute(null);
        Assert.Equal(GameMode.Quick, h.State.SelectedMode);
        Assert.Equal(MatchQueue.Ranked, h.State.SelectedQueue);
    }

    [Fact]
    public async Task Home_always_shows_which_backend_is_used()
    {
        var h = new AppHarness();
        var vm = new HomeViewModel(h.Session, h.Api, h.Nav, h.Dialogs, h.State, h.Settings, h.Config);
        await vm.OnAppearingAsync();
        Assert.StartsWith("Offline", vm.EnvironmentText);

        h.Http.Handler = (_, path, _, _) => path == "/auth/guest" ? (HttpStatusCode.OK, AppHarness.Tokens()) : (HttpStatusCode.NotFound, null);
        await h.Session.SignInAsGuestAsync(null);
        h.Http.Offline = true;
        await vm.OnAppearingAsync();
        Assert.StartsWith("Server unreachable", vm.EnvironmentText);
        h.Http.Offline = false;
        h.Http.Handler = (_, path, _, _) => path == "/me" ? (HttpStatusCode.OK, Me()) : (HttpStatusCode.NotFound, null);
        await vm.OnAppearingAsync();
        Assert.StartsWith("Development server", vm.EnvironmentText);
        Assert.Equal("12", vm.GemsText);
        vm.SelectQueueCommand.Execute("casual");
        Assert.Equal(MatchQueue.Casual, h.State.SelectedQueue);
        Assert.Contains("Shield", vm.QueueDescription);
    }

    internal static object Me() => new
    {
        userId = Guid.Empty, isGuest = true, displayName = "Tester", avatarId = "crown", friendCode = "ABCDEFGH", level = 1, xp = 0, xpForNextLevel = 250, coins = 500,
        stats = new { gamesPlayed = 0, gamesWon = 0, twoPlayerWins = 0, fourPlayerWins = 0, piecesCaptured = 0, abandons = 0, currentWinStreak = 0, bestWinStreak = 0 },
        linkedProviders = Array.Empty<string>(), gems = 12,
    };

    private sealed class NoShare : IShareService
    {
        public Task ShareTextAsync(string title, string text) => Task.CompletedTask;
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
    }
}

public class StoreClientTests
{
    private static object Catalog(bool sandbox = false) => new
    {
        products = new object[]
        {
            new { id = "gems_small", kind = "iap", title = "Pouch of Gems", description = "50 gems", contents = new[] { new { asset = "gems", amount = 50, label = "50 gems" } }, coinPrice = (int?)null, appStoreProductId = "PLACEHOLDER.BUNDLE.ID.gems_small", owned = false },
            new { id = "shield_single", kind = "gold", title = "Shield Bottle", description = "Shield", contents = new[] { new { asset = "boost_shield", amount = 1, label = "1× Shield Bottle" } }, coinPrice = (int?)2000, appStoreProductId = (string?)null, owned = false },
            new { id = "frame_gilded", kind = "gold", title = "Gilded Frame", description = "Frame", contents = new[] { new { asset = "frame_gilded", amount = 1, label = "1× Gilded Frame" } }, coinPrice = (int?)5000, appStoreProductId = (string?)null, owned = false },
        },
        stores = sandbox ? new[] { "app_store", "dev_sandbox" } : new[] { "app_store" },
        appStoreConfigured = false, devSandbox = sandbox, placeholderProductIds = true, environment = "Development",
    };

    private static object Balances(long coins = 0, long gems = 10, int shields = 0) => new { coins, gems, items = shields > 0 ? new[] { new { id = "boost_shield", name = "Shield Bottle", description = "", kind = "Boost", quantity = shields } } : Array.Empty<object>() };

    private static (AppHarness H, FakeIap Iap, StoreViewModel Vm) Make(bool iap, bool sandbox, Func<string, string?, (HttpStatusCode, object?)> purchase)
    {
        var h = new AppHarness();
        var fake = new FakeIap { IsSupported = iap };
        fake.Prices["PLACEHOLDER.BUNDLE.ID.gems_small"] = "12,00 kr";
        h.Http.Handler = (method, path, body, _) => path switch
        {
            "/store/catalog" => (HttpStatusCode.OK, Catalog(sandbox)),
            "/me/inventory" => (HttpStatusCode.OK, new { balances = Balances(), equippedFrame = (string?)null }),
            "/store/purchase-token" => (HttpStatusCode.OK, new { appAccountToken = Guid.Parse("22222222-2222-2222-2222-222222222222") }),
            _ => purchase(path, body),
        };
        return (h, fake, new StoreViewModel(h.Api, fake, h.Dialogs, h.Config));
    }

    [Fact]
    public async Task Real_money_prices_come_from_the_store()
    {
        var (_, _, vm) = Make(iap: true, sandbox: false, (_, _) => (HttpStatusCode.NotFound, null));
        await vm.OnAppearingAsync();
        Assert.Equal("12,00 kr", vm.Gems.Single().PriceText);
        Assert.Equal("2,000 gold", vm.Boosts.Single().PriceText.Replace(' ', ','));
        Assert.Contains("rerolls", vm.GemUseText);
    }

    [Fact]
    public async Task Verified_purchase_is_credited_then_finished()
    {
        var (h, iap, vm) = Make(iap: true, sandbox: false, (path, body) => path == "/store/purchases"
            ? (HttpStatusCode.OK, new { status = "credited", message = "Pouch of Gems added to your account.", balances = Balances(gems: 60), granted = Array.Empty<object>() })
            : (HttpStatusCode.NotFound, null));
        await vm.OnAppearingAsync();
        vm.BuyCommand.Execute(vm.Gems.Single());
        Assert.Equal(Guid.Parse("22222222-2222-2222-2222-222222222222"), iap.Purchases.Single().Token);
        Assert.Contains(h.Http.Requests, r => r.Path == "/store/purchases" && r.Body!.Contains("\"store\":\"app_store\"") && r.Body.Contains("2000000001"));
        Assert.Equal(new[] { "2000000001" }, iap.Finished);
        Assert.Equal("60", vm.GemsText);
        Assert.Equal(StoreItemState.Done, vm.Gems.Single().State);
    }

    [Fact]
    public async Task Pending_or_rejected_purchases_are_not_finished_or_shown_as_success()
    {
        var answer = "pending";
        var (_, iap, vm) = Make(iap: true, sandbox: false, (path, _) => path != "/store/purchases" ? (HttpStatusCode.NotFound, null)
            : answer == "pending" ? (HttpStatusCode.Accepted, new { status = "pending", message = "Waiting", balances = (object?)null, granted = Array.Empty<object>() })
            : (HttpStatusCode.UnprocessableEntity, new { status = "rejected", message = "The transaction signature is not valid.", balances = (object?)null, granted = Array.Empty<object>() }));
        await vm.OnAppearingAsync();
        vm.BuyCommand.Execute(vm.Gems.Single());
        Assert.Empty(iap.Finished);
        Assert.Equal(StoreItemState.Pending, vm.Gems.Single().State);

        answer = "rejected";
        iap.NextOutcome = new StorePurchaseOutcome(StorePurchaseStatus.Purchased, "2000000002");
        vm.BuyCommand.Execute(vm.Gems.Single());
        Assert.Empty(iap.Finished);
        Assert.Equal(StoreItemState.Failed, vm.Gems.Single().State);
        Assert.Equal("The transaction signature is not valid.", vm.Gems.Single().StatusText);
    }

    [Fact]
    public async Task Cancelled_and_deferred_purchases_never_reach_the_server()
    {
        var (h, iap, vm) = Make(iap: true, sandbox: false, (_, _) => (HttpStatusCode.NotFound, null));
        await vm.OnAppearingAsync();
        iap.NextOutcome = new StorePurchaseOutcome(StorePurchaseStatus.Cancelled);
        vm.BuyCommand.Execute(vm.Gems.Single());
        Assert.Contains("weren't charged", vm.Gems.Single().StatusText);
        iap.NextOutcome = new StorePurchaseOutcome(StorePurchaseStatus.Deferred);
        vm.BuyCommand.Execute(vm.Gems.Single());
        Assert.Contains("Ask to Buy", vm.Gems.Single().StatusText);
        Assert.DoesNotContain(h.Http.Requests, r => r.Path == "/store/purchases");
    }

    [Fact]
    public async Task Unfinished_transactions_are_recovered_when_the_store_opens()
    {
        var (h, iap, vm) = Make(iap: true, sandbox: false, (path, _) => path == "/store/purchases"
            ? (HttpStatusCode.OK, new { status = "credited", message = "Added", balances = Balances(gems: 60), granted = Array.Empty<object>() })
            : (HttpStatusCode.NotFound, null));
        iap.Unfinished.Add(new UnfinishedTransaction("2000000099", "PLACEHOLDER.BUNDLE.ID.gems_small"));
        await vm.OnAppearingAsync();
        Assert.Contains("2000000099", iap.Finished);
    }

    [Fact]
    public async Task Gold_purchases_confirm_and_report_not_enough_gold()
    {
        var (h, _, vm) = Make(iap: false, sandbox: false, (path, _) => path == "/store/buy"
            ? (HttpStatusCode.Conflict, new { status = "not_enough_coins", message = "You need 2,000 gold.", balances = Balances() })
            : (HttpStatusCode.NotFound, null));
        await vm.OnAppearingAsync();
        Assert.Contains("iPhone", vm.Notice);
        h.Dialogs.ConfirmAnswers.Enqueue(true);
        vm.BuyCommand.Execute(vm.Boosts.Single());
        Assert.Equal("You need 2,000 gold.", vm.Boosts.Single().StatusText);
        Assert.Equal(StoreItemState.Failed, vm.Boosts.Single().State);
        Assert.Contains(h.Http.Requests, r => r.Path == "/store/buy" && r.Body!.Contains("requestId"));
    }

    [Fact]
    public async Task Development_sandbox_simulates_pending_then_recovers()
    {
        var calls = 0;
        var (h, _, vm) = Make(iap: false, sandbox: true, (path, body) =>
        {
            if (path != "/store/purchases") return (HttpStatusCode.NotFound, null);
            calls++;
            return body!.Contains("\"simulate\":\"pending\"")
                ? (HttpStatusCode.Accepted, new { status = "pending", message = "pending", balances = (object?)null, granted = Array.Empty<object>() })
                : (HttpStatusCode.OK, new { status = "credited", message = "Added", balances = Balances(gems: 60), granted = Array.Empty<object>() });
        });
        await vm.OnAppearingAsync();
        Assert.True(vm.IsSandbox);
        Assert.Contains("SANDBOX", vm.Notice);
        h.Dialogs.SheetAnswers.Enqueue("Simulate pending verification");
        vm.BuyCommand.Execute(vm.Gems.Single());
        Assert.Equal(StoreItemState.Pending, vm.Gems.Single().State);
        var firstTx = h.Http.Requests.Last(r => r.Path == "/store/purchases").Body!;

        vm.RecoverCommand.Execute(null);
        Assert.Equal(StoreItemState.Done, vm.Gems.Single().State);
        var secondTx = h.Http.Requests.Last(r => r.Path == "/store/purchases").Body!;
        Assert.Equal(TxOf(firstTx), TxOf(secondTx)); // same transaction re-checked, not a new purchase
        Assert.Contains("dev_sandbox", secondTx);
    }

    private static string TxOf(string body) => body[(body.IndexOf("devtx-", StringComparison.Ordinal))..][..42];
}

public class LeagueAndProfileClientTests
{
    [Fact]
    public async Task League_screen_shows_season_division_and_marks_development_data()
    {
        var h = new AppHarness();
        h.Http.Handler = (_, path, _, _) => path == "/league" ? (HttpStatusCode.OK, new
        {
            enabled = true, environment = "Development",
            season = new { number = 3, startsAt = DateTimeOffset.UtcNow.AddDays(-2), endsAt = DateTimeOffset.UtcNow.AddDays(5), secondsRemaining = 5 * 86400 + 3600, status = "active" },
            divisions = Array.Empty<object>(),
            me = new { joined = true, division = 1, divisionName = "Knight", points = 40, groupRank = 2, groupSize = 3, matchesPlayed = 3, wins = 2, inPromotionZone = true, inDemotionZone = false, matchesNeededForPromotion = 2 },
            group = new[]
            {
                new { rank = 1, publicId = "AAAAAAAA", displayName = "Top", avatarId = "lion", frameId = (string?)null, points = 60, matchesPlayed = 4, wins = 3, isYou = false },
                new { rank = 2, publicId = "BBBBBBBB", displayName = "Me", avatarId = "crown", frameId = (string?)null, points = 40, matchesPlayed = 3, wins = 2, isYou = true },
            },
            rewards = new { bands = new[] { "#1", "#2–5", "#6–10" }, rows = new[] { new { division = 0, divisionName = "Squire", bands = new[] { new { coins = 1500, gems = 5, shields = 0 }, new { coins = 800, gems = 3, shields = 0 }, new { coins = 400, gems = 1, shields = 0 } } } }, rules = new[] { "Rule one" } },
            lastSeason = (object?)null,
        }) : (HttpStatusCode.NotFound, null);
        var vm = new LeagueViewModel(h.Api, h.Nav, h.State, h.Config);
        await vm.OnAppearingAsync();
        Assert.Equal("Season 3", vm.SeasonTitle);
        Assert.StartsWith("Ends in 5d 1h", vm.SeasonClock);
        Assert.Equal("Knight", vm.DivisionText);
        Assert.Contains("play 2 more", vm.ZoneText);
        Assert.True(vm.HasEnvironmentNotice);
        Assert.Equal("1,500 gold + 5 gems", vm.Rewards.Single().Band1.Replace(' ', ','));
        vm.OpenPlayerCommand.Execute(vm.Group[0]);
        Assert.Equal("AAAAAAAA", h.State.ViewedPlayerId);
    }

    [Fact]
    public async Task Public_profile_respects_hidden_stats_and_reports_by_public_id()
    {
        var h = new AppHarness();
        h.State.ViewedPlayerId = "CCCCCCCC";
        h.Http.Handler = (method, path, body, _) => path switch
        {
            "/players/CCCCCCCC" => (HttpStatusCode.OK, new
            {
                publicId = "CCCCCCCC", displayName = "Private Pat", avatarId = "swan", frameId = "frame_royal", level = 4, isYou = false, isFriend = false,
                statsVisible = false, overall = (object?)null, modes = Array.Empty<object>(), leagueVisible = false, league = (object?)null,
            }),
            "/reports" => (HttpStatusCode.Accepted, null),
            _ => (HttpStatusCode.NotFound, null),
        };
        var vm = new PlayerProfileViewModel(h.Api, h.Dialogs, h.Nav, h.State);
        await vm.OnAppearingAsync();
        Assert.True(vm.StatsHidden);
        Assert.Empty(vm.Stats);
        Assert.Equal("League standing is private.", vm.LeagueText);
        Assert.True(vm.CanAddFriend);
        h.Dialogs.SheetAnswers.Enqueue("Cheating");
        h.Dialogs.PromptAnswers.Enqueue("");
        vm.ReportCommand.Execute(null);
        var report = h.Http.Requests.Single(r => r.Path == "/reports");
        Assert.Contains("\"publicId\":\"CCCCCCCC\"", report.Body);
        Assert.DoesNotContain("userId", report.Body);
    }
}
