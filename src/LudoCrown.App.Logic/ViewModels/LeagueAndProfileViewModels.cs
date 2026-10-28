using System.Collections.ObjectModel;
using System.Windows.Input;
using LudoCrown.App.Api;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;

namespace LudoCrown.App.ViewModels;

public sealed record LeagueRowView(int Rank, string PublicId, string DisplayName, string AvatarId, int Points, string Detail, bool IsYou, string Zone);
public sealed record RewardRowView(string Division, string Band1, string Band2, string Band3);

/// <summary>League screen: season clock, the player's division and group table, the reward table and the rules.</summary>
public sealed class LeagueViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly INavigationService _nav;
    private readonly AppState _state;
    private readonly AppConfig _config;
    private LeagueResponse? _league;

    public LeagueViewModel(ApiClient api, INavigationService nav, AppState state, AppConfig config)
    {
        _api = api;
        _nav = nav;
        _state = state;
        _config = config;
        RefreshCommand = new AsyncCommand(LoadAsync);
        OpenPlayerCommand = new AsyncCommand(p => p is LeagueRowView r ? OpenAsync(r.PublicId) : Task.CompletedTask);
    }

    public ObservableCollection<LeagueRowView> Group { get; } = new();
    public ObservableCollection<RewardRowView> Rewards { get; } = new();
    public ObservableCollection<string> Rules { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OpenPlayerCommand { get; }

    public string SeasonTitle => _league?.Season is SeasonDto s ? $"Season {s.Number}" : "League";
    public string SeasonClock => _league?.Season is SeasonDto s ? Remaining(s.SecondsRemaining, s.EndsAt) : (_league?.Enabled == false ? "The league is turned off on this server." : "");
    public string DivisionText => _league?.Me is MyStandingDto m ? m.DivisionName : "";
    public string StandingText => _league?.Me switch
    {
        null => "",
        { Joined: false } m => $"Play a ranked match with another player to join a {m.DivisionName} group this season.",
        var m => $"{m.Points} points · rank {m.GroupRank}/{m.GroupSize} · {m.Wins} wins in {m.MatchesPlayed} matches",
    };
    public string ZoneText => _league?.Me switch
    {
        { Joined: true, InPromotionZone: true, MatchesNeededForPromotion: > 0 } m => $"In the promotion zone — play {m.MatchesNeededForPromotion} more ranked matches to qualify.",
        { Joined: true, InPromotionZone: true } => "In the promotion zone ▲",
        { Joined: true, InDemotionZone: true } => "In the demotion zone ▼",
        _ => "",
    };
    public string BandHeader1 => _league?.Rewards.Bands.ElementAtOrDefault(0) ?? "#1";
    public string BandHeader2 => _league?.Rewards.Bands.ElementAtOrDefault(1) ?? "#2–5";
    public string BandHeader3 => _league?.Rewards.Bands.ElementAtOrDefault(2) ?? "#6–10";
    public string LastSeasonText => _league?.LastSeason is LastSeasonDto l
        ? $"Season {l.Number}: finished #{l.FinalRank} ({l.Outcome})" + (l.Reward is RewardCellDto r ? $" · reward {Reward(r)}{(l.RewardGranted ? " ✓ added" : " (being added)")}" : "")
        : "";
    public bool HasLastSeason => LastSeasonText.Length > 0;
    /// <summary>Development data must never look like the live leaderboard.</summary>
    public string EnvironmentNotice => _league is not null && (_league.Environment != "Production" || _config.IsDevelopment)
        ? $"Development data ({_league.Environment} server) — not live rankings." : "";
    public bool HasEnvironmentNotice => EnvironmentNotice.Length > 0;

    public override Task OnAppearingAsync() => LoadAsync();

    public static string Remaining(long seconds, DateTimeOffset endsAt)
    {
        if (seconds <= 0) return "Season ending — rewards are being calculated.";
        var t = TimeSpan.FromSeconds(seconds);
        var left = t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, t.Minutes)}m";
        return $"Ends in {left} ({endsAt.ToLocalTime():d MMM HH:mm})";
    }

    public static string Reward(RewardCellDto r) => string.Join(" + ", new[]
    {
        r.Coins > 0 ? $"{r.Coins:N0} gold" : null, r.Gems > 0 ? $"{r.Gems} gems" : null, r.Shields > 0 ? $"{r.Shields} Shield" : null,
    }.Where(x => x is not null));

    private Task LoadAsync() => RunBusyAsync(async () =>
    {
        var r = await _api.GetAsync<LeagueResponse>("/league");
        if (!r.Ok) { Error = r.Message; return; }
        _league = r.Value!;
        Group.Clear();
        var n = _league.Group.Count;
        foreach (var g in _league.Group)
        {
            var zone = _league.Me is { Division: < 5 } && g.Rank <= 5 ? "▲" : _league.Me is { Division: > 0 } && n >= 15 && g.Rank > n - 5 ? "▼" : "";
            Group.Add(new LeagueRowView(g.Rank, g.PublicId, g.DisplayName, g.AvatarId, g.Points, $"{g.Wins} W · {g.MatchesPlayed} played", g.IsYou, zone));
        }
        Rewards.Clear();
        foreach (var row in _league.Rewards.Rows.OrderByDescending(x => x.Division))
            Rewards.Add(new RewardRowView(row.DivisionName, Reward(row.Bands[0]), Reward(row.Bands[1]), Reward(row.Bands[2])));
        Rules.Clear();
        foreach (var rule in _league.Rewards.Rules) Rules.Add(rule);
        foreach (var p in new[] { nameof(SeasonTitle), nameof(SeasonClock), nameof(DivisionText), nameof(StandingText), nameof(ZoneText), nameof(BandHeader1), nameof(BandHeader2),
                     nameof(BandHeader3), nameof(LastSeasonText), nameof(HasLastSeason), nameof(EnvironmentNotice), nameof(HasEnvironmentNotice) })
            OnPropertyChanged(p);
    });

    private Task OpenAsync(string publicId)
    {
        _state.ViewedPlayerId = publicId;
        return _nav.GoToAsync(Routes.PlayerProfile);
    }
}

public sealed record StatRow(string Label, string Played, string Won, string WinRate, string Streak);

/// <summary>Another player's public profile (opened from the board, results, friends or league). Shows only public fields.</summary>
public sealed class PlayerProfileViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _nav;
    private readonly AppState _state;
    private PublicProfileDto? _p;

    public PlayerProfileViewModel(ApiClient api, IDialogService dialogs, INavigationService nav, AppState state)
    {
        _api = api;
        _dialogs = dialogs;
        _nav = nav;
        _state = state;
        AddFriendCommand = new AsyncCommand(AddFriendAsync);
        BlockCommand = new AsyncCommand(BlockAsync);
        ReportCommand = new AsyncCommand(() => _p is null ? Task.CompletedTask : ReportFlow.ReportAsync(_api, _dialogs, _p.PublicId, _p.DisplayName, null));
    }

    public ObservableCollection<StatRow> Stats { get; } = new();
    public ICommand AddFriendCommand { get; }
    public ICommand BlockCommand { get; }
    public ICommand ReportCommand { get; }
    public string DisplayName => _p?.DisplayName ?? "";
    public string AvatarId => _p?.AvatarId ?? "crown";
    public string? FrameId => _p?.FrameId;
    public string LevelText => _p is null ? "" : $"Level {_p.Level}";
    public string PublicId => _p?.PublicId ?? "";
    public bool StatsHidden => _p is { StatsVisible: false };
    public string LeagueText => _p switch
    {
        null => "",
        { LeagueVisible: false } => "League standing is private.",
        { League: null } => "Not in the league this season.",
        { League: var l } => $"{l!.DivisionName} · {l.Points} points" + (l.GroupRank is int r ? $" · rank {r} in group" : "") + $" (season {l.Season})",
    };
    public bool CanInteract => _p is { IsYou: false };
    public bool CanAddFriend => _p is { IsYou: false, IsFriend: false };

    public override Task OnAppearingAsync() => RunBusyAsync(async () =>
    {
        if (_state.ViewedPlayerId is not string id) { await _nav.GoBackAsync(); return; }
        var r = await _api.GetAsync<PublicProfileDto>($"/players/{Uri.EscapeDataString(id)}");
        if (!r.Ok) { Error = r.Kind == ApiErrorKind.NotFound ? "This profile isn't available." : r.Message; return; }
        _p = r.Value!;
        Stats.Clear();
        static StatRow Row(ModeStatsDto m) => new(m.ModeName, m.GamesPlayed.ToString(), m.GamesWon.ToString(), m.GamesPlayed == 0 ? "—" : $"{m.WinRatePercent}%", $"{m.CurrentWinStreak} (best {m.BestWinStreak})");
        if (_p.Overall is ModeStatsDto all) Stats.Add(Row(all));
        foreach (var m in _p.Modes) Stats.Add(Row(m));
        foreach (var p in new[] { nameof(DisplayName), nameof(AvatarId), nameof(FrameId), nameof(LevelText), nameof(PublicId), nameof(StatsHidden), nameof(LeagueText), nameof(CanInteract), nameof(CanAddFriend) })
            OnPropertyChanged(p);
    });

    private async Task AddFriendAsync()
    {
        if (_p is null) return;
        var r = await _api.PostAsync<FriendRequestResponse>("/friends/requests", new { friendCode = _p.PublicId }, auth: true);
        await _dialogs.AlertAsync(r.Ok ? "Friend request" : "Couldn't add friend",
            r.Ok ? (r.Value!.Status == "Accepted" ? $"You and {_p.DisplayName} are now friends." : $"Request sent to {_p.DisplayName}.") : r.Message ?? "");
    }

    private async Task BlockAsync()
    {
        if (_p is null) return;
        if (!await _dialogs.ConfirmAsync($"Block {_p.DisplayName}?", "You won't be matched with them, they can't join your tables, and you won't see their chat messages.", "Block")) return;
        var r = await _api.PostAsync<object>($"/players/{Uri.EscapeDataString(_p.PublicId)}/block", null, auth: true);
        if (r.Ok) await _nav.GoBackAsync();
        else Error = r.Message;
    }
}
