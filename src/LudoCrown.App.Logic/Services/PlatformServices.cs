using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Services;

/// <summary>Secrets (tokens) — iOS Keychain via MAUI SecureStorage.</summary>
public interface ISecureStore
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string value);
    void Remove(string key);
}

/// <summary>Non-secret preferences and the offline saved game — MAUI Preferences.</summary>
public interface ISettingsStore
{
    string? GetString(string key);
    void SetString(string key, string? value);
    bool GetBool(string key, bool fallback);
    void SetBool(string key, bool value);
}

public static class Routes
{
    public const string SignIn = "//signin";
    public const string Home = "//home";
    public const string Matchmaking = "matchmaking";
    public const string PrivateRoom = "privateroom";
    public const string OfflineSetup = "offlinesetup";
    public const string Game = "game";
    public const string Results = "results";
    public const string Profile = "profile";
    public const string Friends = "friends";
    public const string Settings = "settings";
    public const string EmailSignIn = "emailsignin";
    public const string Rules = "rules";
    public const string BlockedPlayers = "blocked";
}

public interface INavigationService
{
    Task GoToAsync(string route);
    Task GoBackAsync();
    /// <summary>Replaces the current page (e.g. matchmaking → game) so Back does not return to it.</summary>
    Task ReplaceAsync(string route);
}

public interface IDialogService
{
    Task AlertAsync(string title, string message, string ok = "OK");
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel");
    Task<string?> PromptAsync(string title, string message, string accept = "OK", string? initial = null, int maxLength = 50, bool numeric = false);
    Task<string?> ActionSheetAsync(string title, string cancel, params string[] options);
}

public enum SoundEffect { DiceRoll, Move, Capture, Enter, Finish, Win, Lose, Tap, Turn }

/// <summary>Sounds and haptics. Implementations respect the user's settings.</summary>
public interface IFeedbackService
{
    void Play(SoundEffect effect);
    void Haptic(bool strong = false);
}

public interface IShareService
{
    Task ShareTextAsync(string title, string text);
    Task OpenUrlAsync(string url);
}

public interface IConnectivityService
{
    bool IsOnline { get; }
}

/// <summary>Opens the system browser sheet for OAuth (MAUI WebAuthenticator / ASWebAuthenticationSession).</summary>
public interface IAuthBrowser
{
    /// <summary>Returns the callback query parameters, or null if the user cancelled.</summary>
    Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri startUrl, Uri callbackUrl);
}

public sealed record AppleSignInResult(string IdentityToken, string? AuthorizationCode, string? FullName);

/// <summary>Native Sign in with Apple sheet (iOS only).</summary>
public interface IAppleSignIn
{
    bool IsSupported { get; }
    Task<AppleSignInResult?> SignInAsync();
}

/// <summary>Delays, injectable so bot pacing and animations are instant in tests.</summary>
public interface IDelay
{
    Task Delay(TimeSpan duration, CancellationToken ct = default);
}

public sealed class RealDelay : IDelay
{
    public Task Delay(TimeSpan duration, CancellationToken ct = default) => Task.Delay(duration, ct);
}

/// <summary>Marshals callbacks (e.g. SignalR events) onto the UI thread.</summary>
public interface IMainThread
{
    void Post(Action action);
}

public enum ConnectionStatus { Disconnected, Connecting, Connected, Reconnecting }

/// <summary>Real-time connection to the game server (SignalR in the MAUI app).</summary>
public interface IMatchConnection : IAsyncDisposable
{
    ConnectionStatus Status { get; }
    event Action<ConnectionStatus>? StatusChanged;
    event Action<MatchSnapshot>? MatchState;
    event Action<RoomInfo>? RoomUpdated;
    event Action<MatchmakingStatus>? MatchmakingStatusChanged;
    event Action<ReactionInfo>? Reaction;
    event Action<MatchEndedInfo>? MatchEnded;

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync();

    Task<MatchmakingStatus> FindMatchAsync(int playerCount);
    Task CancelMatchmakingAsync();
    Task<HubResult<RoomInfo>> CreatePrivateRoomAsync(int playerCount);
    Task<HubResult<RoomInfo>> JoinPrivateRoomAsync(string code);
    Task<HubResult<string>> StartPrivateRoomAsync(string roomId);
    Task<ServerError?> LeaveRoomAsync(string id);
    Task<ServerError?> RollAsync(string matchId, long expectedVersion);
    Task<ServerError?> MoveAsync(string matchId, int pieceIndex, long expectedVersion);
    Task<MatchSnapshot?> ResyncAsync(string? matchId);
    Task<ServerError?> SendReactionAsync(string matchId, string reactionId);
    Task<HubResult<RoomInfo>> RequestRematchAsync(string matchId);
}

public static class SettingKeys
{
    public const string Sound = "settings.sound";
    public const string Haptics = "settings.haptics";
    public const string ReduceMotion = "settings.reduce_motion";
    public const string ShowReactions = "settings.show_reactions";
    public const string AutoMoveSingle = "settings.auto_move_single";
    public const string ServerUrlOverride = "settings.server_url";
    public const string OfflineSave = "offline.save";
    public const string OfflineName = "offline.player_name";
    public const string SeenOnboarding = "app.seen_onboarding";
}

/// <summary>App configuration. The production server URL must be set before release (see docs/RELEASE_CHECKLIST.md).</summary>
public sealed class AppConfig
{
    /// <summary>TODO before release: replace with your deployed server, e.g. https://api.your-domain.com</summary>
    public const string ProductionServerUrl = "https://YOUR-SERVER-HOST";
    public const string AppCallbackUrl = "ludocrown://auth";
    /// <summary>TODO before release: your published privacy policy and support pages.</summary>
    public const string PrivacyPolicyUrl = "https://YOUR-WEBSITE/privacy";
    public const string SupportUrl = "https://YOUR-WEBSITE/support";

    public required string ServerUrl { get; init; }
    public bool IsDevelopment { get; init; }
    public bool ServerConfigured => !ServerUrl.Contains("YOUR-SERVER-HOST", StringComparison.Ordinal);
}
