using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using LudoCrown.App.Api;
using LudoCrown.App.Auth;
using LudoCrown.App.Mvvm;
using LudoCrown.App.Services;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.ViewModels;

public sealed record HistoryRow(string Title, string Detail, bool Won);

public sealed record AvatarOption(string Id, bool IsSelected);

/// <summary>A cosmetic frame the player owns, with whether it is equipped.</summary>
public sealed record FrameOption(string Id, string Name, bool IsEquipped);

public sealed class ProfileViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly SessionService _session;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _nav;
    private readonly AppState _state;
    private MeResponse? _me;

    public ProfileViewModel(ApiClient api, SessionService session, IDialogService dialogs, INavigationService nav, AppState state)
    {
        _api = api;
        _session = session;
        _dialogs = dialogs;
        _nav = nav;
        _state = state;
        RenameCommand = new AsyncCommand(RenameAsync);
        AvatarCommand = new AsyncCommand(p => SetAvatarAsync(p as string));
        LinkCommand = new AsyncCommand(LinkAsync);
        RefreshCommand = new AsyncCommand(LoadAsync);
        EquipFrameCommand = new AsyncCommand(p => EquipFrameAsync(p as FrameOption));
        StoreCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Store));
        LeagueCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.League));
    }

    private InventoryResponse? _inventory;
    public ObservableCollection<StatRow> ModeStats { get; } = new();
    public ObservableCollection<FrameOption> Frames { get; } = new();
    public ICommand EquipFrameCommand { get; }
    public ICommand StoreCommand { get; }
    public ICommand LeagueCommand { get; }
    public string GemsText => _inventory is null ? "" : $"{_inventory.Balances.Gems:N0} gems";
    public string ShieldsText => _inventory is null ? "" : $"{_inventory.Balances.Count(Core.Model.Boosts.ShieldId)} Shield bottles";
    public bool HasFrames => Frames.Count > 0;
    public string? EquippedFrame => _inventory?.EquippedFrame;

    public MeResponse? Me { get => _me; private set { Set(ref _me, value); Refresh(); } }
    public ObservableCollection<HistoryRow> History { get; } = new();
    public IReadOnlyList<AvatarOption> AvatarOptions => Avatars.All.Select(a => new AvatarOption(a, a == AvatarId)).ToList();
    public bool HasNoHistory => History.Count == 0;
    public string DisplayName => _me?.DisplayName ?? "";
    public string AvatarId => _me?.AvatarId ?? "crown";
    public string LevelText => _me is null ? "" : $"Level {_me.Level}";
    public double LevelProgress => _me is null ? 0 : (_me.Xp % 250) / 250.0;
    public string XpText => _me is null ? "" : $"{_me.Xp % 250} / 250 XP to level {_me.Level + 1}";
    public string CoinsText => _me is null ? "" : $"{_me.Coins:N0} coins";
    public string FriendCode => _me?.FriendCode ?? "";
    public string GamesText => _me is null ? "" : _me.Stats.GamesPlayed.ToString();
    public string WinsText => _me is null ? "" : _me.Stats.GamesWon.ToString();
    public string WinRateText => _me is null || _me.Stats.GamesPlayed == 0 ? "—" : $"{100.0 * _me.Stats.GamesWon / _me.Stats.GamesPlayed:0}%";
    public string StreakText => _me is null ? "" : $"{_me.Stats.BestWinStreak}";
    public string CapturesText => _me is null ? "" : _me.Stats.PiecesCaptured.ToString();
    public bool IsGuest => _me?.IsGuest ?? _session.IsGuest;
    public string LinkedText => _me is null || _me.LinkedProviders.Count == 0 ? "Guest account — not linked" : "Signed in with " + string.Join(", ", _me.LinkedProviders.Select(Pretty));
    public ICommand RenameCommand { get; }
    public ICommand AvatarCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task OnAppearingAsync() => LoadAsync();

    private static string Pretty(string provider) => provider switch { "apple" => "Apple", "google" => "Google", "facebook" => "Facebook", "email" => "email", _ => provider };

    private void Refresh()
    {
        foreach (var p in new[] { nameof(DisplayName), nameof(AvatarId), nameof(AvatarOptions), nameof(LevelText), nameof(LevelProgress), nameof(XpText), nameof(CoinsText), nameof(FriendCode),
                     nameof(GamesText), nameof(WinsText), nameof(WinRateText), nameof(StreakText), nameof(CapturesText), nameof(IsGuest), nameof(LinkedText) })
            OnPropertyChanged(p);
    }

    private Task LoadAsync() => RunBusyAsync(async () =>
    {
        var me = await _api.GetAsync<MeResponse>("/me");
        if (!me.Ok) { Error = me.Message; return; }
        Me = me.Value;
        var inventory = await _api.GetAsync<InventoryResponse>("/me/inventory");
        if (inventory.Ok)
        {
            _inventory = inventory.Value;
            Frames.Clear();
            foreach (var f in _inventory!.Balances.Items.Where(i => i.Kind == "Frame"))
                Frames.Add(new FrameOption(f.Id, f.Name, f.Id == _inventory.EquippedFrame));
            foreach (var n in new[] { nameof(GemsText), nameof(ShieldsText), nameof(HasFrames), nameof(EquippedFrame) }) OnPropertyChanged(n);
        }
        var modes = await _api.GetAsync<List<ModeStatsDto>>("/me/stats/modes");
        ModeStats.Clear();
        foreach (var m in modes.Value ?? new())
            ModeStats.Add(new StatRow(m.ModeName, m.GamesPlayed.ToString(), m.GamesWon.ToString(), m.GamesPlayed == 0 ? "—" : $"{m.WinRatePercent}%", $"{m.CurrentWinStreak} (best {m.BestWinStreak})"));

        var history = await _api.GetAsync<List<MatchHistoryDto>>("/me/matches?limit=20");
        History.Clear();
        foreach (var h in history.Value ?? new())
        {
            var place = h.Result == "Abandoned" ? "Left early" : h.Placement == 1 ? "Won" : $"Placed {h.Placement}";
            var kind = h.Mode switch { "Private" => "Private table", "Casual" => "Casual", _ => "Ranked" };
            var gameMode = Core.Model.GameModes.Parse(h.GameMode) is Core.Model.GameMode gm ? Core.Model.GameModes.Get(gm).Name : "Classic";
            var mode = $"{gameMode} · {kind} · {h.PlayerCount}p" + (h.LeaguePoints is int lp ? $" · {(lp >= 0 ? "+" : "")}{lp} league pts" : "");
            var when = (h.EndedAt ?? h.StartedAt).ToLocalTime().ToString("d MMM, HH:mm");
            History.Add(new HistoryRow($"{place} · {mode}", $"vs {string.Join(", ", h.Opponents)} · {when}", h.Placement == 1 && h.Result != "Abandoned"));
        }
        OnPropertyChanged(nameof(HasNoHistory));
    });

    private async Task RenameAsync()
    {
        var name = await _dialogs.PromptAsync("Display name", $"{DisplayNames.MinLength}–{DisplayNames.MaxLength} characters.", "Save", DisplayName, DisplayNames.MaxLength);
        if (name is null || name == DisplayName) return;
        if (DisplayNames.Validate(name) is string reason) { await _dialogs.AlertAsync("Display name", reason); return; }
        var r = await _api.PatchAsync<object>("/me/profile", new { displayName = name });
        if (!r.Ok) { await _dialogs.AlertAsync("Couldn't save", r.Message ?? ""); return; }
        await LoadAsync();
    }

    private async Task EquipFrameAsync(FrameOption? frame)
    {
        if (frame is null) return;
        var r = await _api.PostAsync<object>("/me/frame", new { frameId = frame.IsEquipped ? null : frame.Id }, auth: true);
        if (r.Ok) await LoadAsync();
        else Error = r.Message;
    }

    private async Task SetAvatarAsync(string? id)
    {
        if (!Avatars.IsValid(id) || id == AvatarId) return;
        var r = await _api.PatchAsync<object>("/me/profile", new { avatarId = id });
        if (r.Ok) await LoadAsync();
    }

    private async Task LinkAsync()
    {
        var options = new List<string>();
        if (_session.AppleAvailable) options.Add("Sign in with Apple");
        if (_session.GoogleAvailable) options.Add("Google");
        if (_session.FacebookAvailable) options.Add("Facebook");
        if (_session.Providers?.Email ?? false) options.Add("Email");
        if (options.Count == 0) { await _dialogs.AlertAsync("Not available", "No sign-in methods are configured on this server yet."); return; }

        var choice = await _dialogs.ActionSheetAsync("Keep your progress — link an account", "Cancel", options.ToArray());
        AuthResult? result = choice switch
        {
            "Sign in with Apple" => await _session.SignInWithAppleAsync(link: true),
            "Google" => await _session.SignInWithBrowserAsync("google", link: true),
            "Facebook" => await _session.SignInWithBrowserAsync("facebook", link: true),
            _ => null,
        };
        if (choice == "Email") { _state.EmailLinkMode = true; await _nav.GoToAsync(Routes.EmailSignIn); return; }
        if (result is null || result.Outcome == AuthOutcome.Cancelled) return;
        if (!result.Ok) await _dialogs.AlertAsync("Couldn't link account", result.Message ?? "Please try again.");
        else await LoadAsync();
    }
}

public sealed class FriendsViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly IDialogService _dialogs;
    private readonly IShareService _share;
    private readonly INavigationService _nav;
    private readonly AppState _state;
    private string _myCode = "";

    public FriendsViewModel(ApiClient api, IDialogService dialogs, IShareService share, INavigationService nav, AppState state)
    {
        _api = api;
        _dialogs = dialogs;
        _share = share;
        _nav = nav;
        _state = state;
        AddCommand = new AsyncCommand(AddAsync);
        AcceptCommand = new AsyncCommand(p => p is FriendDto f ? AcceptAsync(f) : Task.CompletedTask);
        OptionsCommand = new AsyncCommand(p => p is FriendDto f ? OptionsAsync(f) : Task.CompletedTask);
        ShareCodeCommand = new AsyncCommand(() => _share.ShareTextAsync("My Ludo Crown friend code", $"Add me on Ludo Crown! My friend code is {MyCode}."));
        InviteCommand = new AsyncCommand(InviteAsync);
        RefreshCommand = new AsyncCommand(LoadAsync);
    }

    public ObservableCollection<FriendDto> Friends { get; } = new();
    public ObservableCollection<FriendDto> Requests { get; } = new();
    public string MyCode { get => _myCode; private set => Set(ref _myCode, value); }
    public bool HasRequests => Requests.Count > 0;
    public bool HasNoFriends => Friends.Count == 0;
    public ICommand AddCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand OptionsCommand { get; }
    public ICommand ShareCodeCommand { get; }
    public ICommand InviteCommand { get; }
    public ICommand RefreshCommand { get; }

    public override Task OnAppearingAsync() => LoadAsync();

    private Task LoadAsync() => RunBusyAsync(async () =>
    {
        var me = await _api.GetAsync<MeResponse>("/me");
        if (me.Ok) MyCode = me.Value!.FriendCode;
        var r = await _api.GetAsync<List<FriendDto>>("/friends");
        if (!r.Ok) { Error = r.Message; return; }
        Friends.Clear();
        Requests.Clear();
        foreach (var f in r.Value ?? new())
        {
            if (f.Status == "Accepted") Friends.Add(f);
            else if (f.IncomingRequest) Requests.Add(f);
            else Friends.Add(f); // outgoing, shown as "Request sent"
        }
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(HasNoFriends));
    });

    private async Task AddAsync()
    {
        var code = await _dialogs.PromptAsync("Add a friend", "Enter their 8-character friend code (find it on their Friends screen).", "Send request", null, 10);
        if (string.IsNullOrWhiteSpace(code)) return;
        var r = await _api.PostAsync<FriendRequestResponse>("/friends/requests", new { friendCode = code.Trim().ToUpperInvariant() }, auth: true);
        if (!r.Ok) { await _dialogs.AlertAsync("Couldn't add friend", r.Message ?? ""); return; }
        await _dialogs.AlertAsync("Friend request", r.Value!.Status == "Accepted" ? $"You and {r.Value.DisplayName} are now friends." : $"Request sent to {r.Value.DisplayName}.");
        await LoadAsync();
    }

    private async Task AcceptAsync(FriendDto f)
    {
        await _api.PostAsync<object>($"/friends/{f.UserId}/accept", null, auth: true);
        await LoadAsync();
    }

    private async Task OptionsAsync(FriendDto f)
    {
        var choice = await _dialogs.ActionSheetAsync(f.DisplayName, "Cancel", "View profile", "Remove friend", "Block", "Report");
        switch (choice)
        {
            case "View profile" when f.FriendCode.Length == 8:
                _state.ViewedPlayerId = f.FriendCode;
                await _nav.GoToAsync(Routes.PlayerProfile);
                return;
            case "Remove friend":
                await _api.DeleteAsync<object>($"/friends/{f.UserId}");
                break;
            case "Block":
                if (await _dialogs.ConfirmAsync($"Block {f.DisplayName}?", "You won't be matched with them, they can't join your tables, and you won't see their reactions.", "Block"))
                    await _api.PostAsync<object>($"/blocks/{f.UserId}", null, auth: true);
                break;
            case "Report":
                await ReportFlow.ReportAsync(_api, _dialogs, f.UserId, f.DisplayName, null);
                break;
        }
        await LoadAsync();
    }

    private async Task InviteAsync()
    {
        var choice = await _dialogs.ActionSheetAsync("Invite friends to a private table", "Cancel", "2 players", "4 players");
        if (choice is null || choice == "Cancel") return;
        _state.RequestedPlayerCount = choice.StartsWith('4') ? 4 : 2;
        _state.PrivateRoomMode = PrivateRoomMode.Create;
        await _nav.GoToAsync(Routes.PrivateRoom);
    }
}

public static class ReportFlow
{
    /// <summary>Reports by public id (friend code) — the app never needs internal account ids.</summary>
    public static Task ReportAsync(ApiClient api, IDialogService dialogs, string publicId, string name, Guid? matchId) =>
        ReportCoreAsync(api, dialogs, name, (reason, details) => new { publicId, matchId, reason, details });

    public static Task ReportAsync(ApiClient api, IDialogService dialogs, Guid userId, string name, Guid? matchId) =>
        ReportCoreAsync(api, dialogs, name, (reason, details) => new { userId, matchId, reason, details });

    private static async Task ReportCoreAsync(ApiClient api, IDialogService dialogs, string name, Func<string, string, object> body)
    {
        var reasons = new Dictionary<string, string> { ["Offensive name"] = "offensive_name", ["Cheating"] = "cheating", ["Harassment"] = "harassment", ["Something else"] = "other" };
        var choice = await dialogs.ActionSheetAsync($"Report {name}", "Cancel", reasons.Keys.ToArray());
        if (choice is null || !reasons.TryGetValue(choice, out var reason)) return;
        var details = await dialogs.PromptAsync("Details (optional)", "Tell us briefly what happened.", "Send report", null, 500);
        if (details is null) return;
        var r = await api.PostAsync<object>("/reports", body(reason, details), auth: true);
        await dialogs.AlertAsync(r.Ok ? "Thanks" : "Couldn't send report", r.Ok ? "Our team will review your report." : r.Message ?? "");
    }
}

public sealed class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsStore _settings;
    private readonly SessionService _session;
    private readonly ApiClient _api;
    private readonly IDialogService _dialogs;
    private readonly INavigationService _nav;
    private readonly IShareService _share;
    private readonly AppConfig _config;

    public SettingsViewModel(ISettingsStore settings, SessionService session, ApiClient api, IDialogService dialogs, INavigationService nav, IShareService share, AppConfig config)
    {
        _settings = settings;
        _session = session;
        _api = api;
        _dialogs = dialogs;
        _nav = nav;
        _share = share;
        _config = config;
        SignOutCommand = new AsyncCommand(SignOutAsync);
        DeleteAccountCommand = new AsyncCommand(DeleteAccountAsync);
        ExportCommand = new AsyncCommand(ExportAsync);
        PrivacyCommand = new AsyncCommand(() => _share.OpenUrlAsync(AppConfig.PrivacyPolicyUrl));
        SupportCommand = new AsyncCommand(() => _share.OpenUrlAsync(AppConfig.SupportUrl));
        RulesCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.Rules));
        BlockedCommand = new AsyncCommand(() => _nav.GoToAsync(Routes.BlockedPlayers));
        SetServerCommand = new AsyncCommand(SetServerAsync);
        DevGrantCommand = new AsyncCommand(DevGrantAsync);
    }

    private PrivacyDto? _privacy;
    public ICommand DevGrantCommand { get; }
    public bool ConfirmGemSpend { get => _settings.GetBool(SettingKeys.ConfirmGemSpend, true); set { _settings.SetBool(SettingKeys.ConfirmGemSpend, value); OnPropertyChanged(); } }
    public bool PrivacyLoaded => _privacy is not null;
    public bool ShowStatsPublicly { get => _privacy?.ShowStats ?? true; set => _ = SavePrivacyAsync(showStats: value); }
    public bool ShowLeaguePublicly { get => _privacy?.ShowLeague ?? true; set => _ = SavePrivacyAsync(showLeague: value); }
    public bool ChatEnabled { get => _privacy?.ChatEnabled ?? true; set => _ = SavePrivacyAsync(chatEnabled: value); }
    public string EnvironmentText => _config.EnvironmentLabel;

    private async Task SavePrivacyAsync(bool? showStats = null, bool? showLeague = null, bool? chatEnabled = null)
    {
        if (_privacy is null) return;
        var r = await _api.PatchAsync<PrivacyDto>("/me/privacy", new { showStats, showLeague, chatEnabled });
        if (r.Ok) _privacy = r.Value;
        else Error = r.Message;
        OnPropertyChanged(nameof(ShowStatsPublicly));
        OnPropertyChanged(nameof(ShowLeaguePublicly));
        OnPropertyChanged(nameof(ChatEnabled));
    }

    /// <summary>DEVELOPMENT ONLY: asks the development server for test currency. The server refuses this outside Development.</summary>
    private Task DevGrantAsync() => RunBusyAsync(async () =>
    {
        if (!_config.IsDevelopment) return;
        var r = await _api.PostAsync<BalancesDto>("/dev/economy/grant", new { coins = 5000, gems = 20, shields = 3 }, auth: true);
        await _dialogs.AlertAsync(r.Ok ? "Test items added (development)" : "Not available",
            r.Ok ? $"Now {r.Value!.Coins:N0} gold, {r.Value.Gems} gems, {r.Value.Count(Core.Model.Boosts.ShieldId)} Shield bottles." : r.Message ?? "The server didn't allow it.");
    });

    public bool Sound { get => _settings.GetBool(SettingKeys.Sound, true); set { _settings.SetBool(SettingKeys.Sound, value); OnPropertyChanged(); } }
    public bool Haptics { get => _settings.GetBool(SettingKeys.Haptics, true); set { _settings.SetBool(SettingKeys.Haptics, value); OnPropertyChanged(); } }
    public bool ReduceMotion { get => _settings.GetBool(SettingKeys.ReduceMotion, false); set { _settings.SetBool(SettingKeys.ReduceMotion, value); OnPropertyChanged(); } }
    public bool ShowReactions { get => _settings.GetBool(SettingKeys.ShowReactions, true); set { _settings.SetBool(SettingKeys.ShowReactions, value); OnPropertyChanged(); } }
    public bool AutoMoveSingle { get => _settings.GetBool(SettingKeys.AutoMoveSingle, true); set { _settings.SetBool(SettingKeys.AutoMoveSingle, value); OnPropertyChanged(); } }
    public bool IsSignedIn => _session.IsSignedIn;
    public bool IsDevelopment => _config.IsDevelopment;
    public string ServerUrl => _config.ServerUrl;
    public string VersionText { get; set; } = "";
    public ICommand SignOutCommand { get; }
    public ICommand DeleteAccountCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PrivacyCommand { get; }
    public ICommand SupportCommand { get; }
    public ICommand RulesCommand { get; }
    public ICommand BlockedCommand { get; }
    public ICommand SetServerCommand { get; }

    public override async Task OnAppearingAsync()
    {
        OnPropertyChanged(nameof(IsSignedIn));
        if (_session.IsSignedIn)
        {
            var r = await _api.GetAsync<PrivacyDto>("/me/privacy");
            if (r.Ok) _privacy = r.Value;
        }
        foreach (var p in new[] { nameof(PrivacyLoaded), nameof(ShowStatsPublicly), nameof(ShowLeaguePublicly), nameof(ChatEnabled), nameof(EnvironmentText) }) OnPropertyChanged(p);
    }

    private async Task SignOutAsync()
    {
        var warning = _session.IsGuest
            ? "You're on a guest account. If you sign out without linking Apple, Google, Facebook or email, you can't get this account back."
            : "You can sign back in at any time.";
        if (!await _dialogs.ConfirmAsync("Sign out?", warning, "Sign out")) return;
        await _session.SignOutAsync();
        await _nav.GoToAsync(Routes.SignIn);
    }

    private async Task DeleteAccountAsync()
    {
        if (!await _dialogs.ConfirmAsync("Delete account?", "This permanently deletes your profile, stats, friends and sign-in methods. Opponents' match history will show \"Deleted player\". This can't be undone.", "Continue"))
            return;
        var typed = await _dialogs.PromptAsync("Confirm deletion", "Type DELETE to permanently delete your account.", "Delete account");
        if (!string.Equals(typed?.Trim(), "DELETE", StringComparison.Ordinal)) return;

        await RunBusyAsync(async () =>
        {
            var me = await _api.GetAsync<MeResponse>("/me");
            string? appleCode = null;
            if (me.Ok && me.Value!.LinkedProviders.Contains("apple"))
                appleCode = await _session.GetAppleAuthorizationCodeAsync(); // lets the server revoke Sign in with Apple
            var r = await _session.DeleteAccountAsync(appleCode);
            if (!r.Ok) { Error = r.Message; return; }
            await _dialogs.AlertAsync("Account deleted", r.Value?.Note ?? "Your account and data have been deleted.");
            await _nav.GoToAsync(Routes.SignIn);
        });
    }

    private Task ExportAsync() => RunBusyAsync(async () =>
    {
        var r = await _api.GetAsync<JsonElement>("/me/export");
        if (!r.Ok) { Error = r.Message; return; }
        var json = JsonSerializer.Serialize(r.Value, new JsonSerializerOptions { WriteIndented = true });
        await _share.ShareTextAsync("My Ludo Crown data", json);
    });

    private async Task SetServerAsync()
    {
        if (!_config.IsDevelopment) return;
        var url = await _dialogs.PromptAsync("Server URL (development)", "Restart the app after changing. Leave empty for the default.", "Save", _settings.GetString(SettingKeys.ServerUrlOverride) ?? _config.ServerUrl, 200);
        if (url is null) return;
        _settings.SetString(SettingKeys.ServerUrlOverride, string.IsNullOrWhiteSpace(url) ? null : url.Trim());
        await _dialogs.AlertAsync("Saved", "Close and reopen the app to use the new server.");
    }
}

public sealed class BlockedPlayersViewModel : ViewModelBase
{
    private readonly ApiClient _api;
    private readonly IDialogService _dialogs;

    public BlockedPlayersViewModel(ApiClient api, IDialogService dialogs)
    {
        _api = api;
        _dialogs = dialogs;
        UnblockCommand = new AsyncCommand(p => p is BlockedDto b ? UnblockAsync(b) : Task.CompletedTask);
    }

    public ObservableCollection<BlockedDto> Blocked { get; } = new();
    public bool IsEmpty => Blocked.Count == 0;
    public ICommand UnblockCommand { get; }

    public override Task OnAppearingAsync() => LoadAsync();

    private Task LoadAsync() => RunBusyAsync(async () =>
    {
        var r = await _api.GetAsync<List<BlockedDto>>("/blocks");
        if (!r.Ok) { Error = r.Message; return; }
        Blocked.Clear();
        foreach (var b in r.Value ?? new()) Blocked.Add(b);
        OnPropertyChanged(nameof(IsEmpty));
    });

    private async Task UnblockAsync(BlockedDto b)
    {
        if (!await _dialogs.ConfirmAsync($"Unblock {b.DisplayName}?", "You may be matched with them again.", "Unblock")) return;
        await _api.DeleteAsync<object>($"/blocks/{b.UserId}");
        await LoadAsync();
    }
}
