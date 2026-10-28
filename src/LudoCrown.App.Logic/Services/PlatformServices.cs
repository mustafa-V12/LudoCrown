using LudoCrown.Core.Model;
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
    public const string Store = "store";
    public const string League = "league";
    public const string PlayerProfile = "player";
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
    /// <summary>False where MAUI WebAuthenticator is unavailable (e.g. Windows desktop builds).</summary>
    bool IsSupported { get; }
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

    Task<MatchmakingStatus> FindMatchAsync(int playerCount, GameMode mode, MatchQueue queue);
    Task CancelMatchmakingAsync();
    Task<HubResult<RoomInfo>> CreatePrivateRoomAsync(int playerCount, GameMode mode);
    Task<HubResult<RoomInfo>> JoinPrivateRoomAsync(string code);
    Task<HubResult<string>> StartPrivateRoomAsync(string roomId);
    Task<ServerError?> LeaveRoomAsync(string id);
    Task<ServerError?> RollAsync(string matchId, long expectedVersion);
    Task<ServerError?> MoveAsync(string matchId, int pieceIndex, long expectedVersion);
    /// <summary>Gem reroll of the pending roll. Reuse the same <paramref name="requestId"/> when retrying so the server charges once.</summary>
    Task<RerollResult> RerollAsync(string matchId, long expectedVersion, string requestId);
    Task<ServerError?> PassTurnAsync(string matchId, long expectedVersion);
    /// <summary>Drinks a Shield bottle. Reuse the same <paramref name="requestId"/> when retrying.</summary>
    Task<BoostResult> UseBoostAsync(string matchId, long expectedVersion, string requestId);
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
    public const string ConfirmGemSpend = "settings.confirm_gem_spend";
    public const string LastMode = "play.last_mode";
    public const string LastQueue = "play.last_queue";
}

public enum StorePurchaseStatus { Purchased, Cancelled, Deferred, Failed }

/// <summary>Result of asking the platform store (StoreKit) to buy a product.</summary>
public sealed record StorePurchaseOutcome(StorePurchaseStatus Status, string? TransactionId = null, string? ProductId = null, string? Message = null);

/// <summary>A completed store transaction the app has not finished yet (delivered after a crash, Ask to Buy approval, etc.).</summary>
public sealed record UnfinishedTransaction(string TransactionId, string ProductId);

/// <summary>
/// Platform in-app purchases (StoreKit on iOS). The app never grants anything itself: after the store reports a purchase, the
/// app sends the transaction id to the server, which verifies it with Apple and credits the account; only then does the app
/// finish the transaction. Unsupported platforms (Windows, Android builds) report <see cref="IsSupported"/> = false.
/// </summary>
public interface IInAppPurchaseService
{
    bool IsSupported { get; }
    /// <summary>Localised price strings from the store, keyed by product id. Missing ids are not sold/configured.</summary>
    Task<IReadOnlyDictionary<string, string>> GetLocalizedPricesAsync(IReadOnlyList<string> productIds);
    Task<StorePurchaseOutcome> PurchaseAsync(string productId, Guid appAccountToken);
    Task<IReadOnlyList<UnfinishedTransaction>> GetUnfinishedTransactionsAsync();
    Task FinishAsync(string transactionId);
}

/// <summary>Used where StoreKit is not available.</summary>
public sealed class UnsupportedInAppPurchases : IInAppPurchaseService
{
    public bool IsSupported => false;
    public Task<IReadOnlyDictionary<string, string>> GetLocalizedPricesAsync(IReadOnlyList<string> productIds) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    public Task<StorePurchaseOutcome> PurchaseAsync(string productId, Guid appAccountToken) => Task.FromResult(new StorePurchaseOutcome(StorePurchaseStatus.Failed, Message: "In-app purchases are not available on this device."));
    public Task<IReadOnlyList<UnfinishedTransaction>> GetUnfinishedTransactionsAsync() => Task.FromResult<IReadOnlyList<UnfinishedTransaction>>(Array.Empty<UnfinishedTransaction>());
    public Task FinishAsync(string transactionId) => Task.CompletedTask;
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

    /// <summary>A local development server (localhost / emulator host / LAN address with a port).</summary>
    public bool IsLocalServer => Uri.TryCreate(ServerUrl, UriKind.Absolute, out var u)
                                 && (u.IsLoopback || u.Host == "10.0.2.2" || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || u.Scheme == "http");

    /// <summary>Short label shown in the app so players and testers always know which backend they are using.</summary>
    public string EnvironmentLabel => IsDevelopment || IsLocalServer ? $"Development server · {ServerUrl}" : "Online";
}
