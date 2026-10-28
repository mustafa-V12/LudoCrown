using System.Net;
using System.Text;
using System.Text.Json;
using LudoCrown.App.Services;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.Tests;

public sealed class MemorySettings : ISettingsStore
{
    public Dictionary<string, string?> Values { get; } = new();
    public string? GetString(string key) => Values.GetValueOrDefault(key);
    public void SetString(string key, string? value) { if (value is null) Values.Remove(key); else Values[key] = value; }
    public bool GetBool(string key, bool fallback) => Values.TryGetValue(key, out var v) && v is not null ? v == "true" : fallback;
    public void SetBool(string key, bool value) => Values[key] = value ? "true" : "false";
}

public sealed class MemorySecureStore : ISecureStore
{
    public Dictionary<string, string> Values { get; } = new();
    public Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value) { Values[key] = value; return Task.CompletedTask; }
    public void Remove(string key) => Values.Remove(key);
}

public sealed class InstantDelay : IDelay
{
    public int Calls;
    public Task Delay(TimeSpan duration, CancellationToken ct = default) { Calls++; ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
}

public sealed class InlineMainThread : IMainThread
{
    public void Post(Action action) => action();
}

public sealed class RecordingNavigation : INavigationService
{
    public List<string> Routes { get; } = new();
    public string? Last => Routes.LastOrDefault();
    public Task GoToAsync(string route) { Routes.Add(route); return Task.CompletedTask; }
    public Task GoBackAsync() { Routes.Add("back"); return Task.CompletedTask; }
    public Task ReplaceAsync(string route) { Routes.Add("replace:" + route); return Task.CompletedTask; }
}

public sealed class ScriptedDialogs : IDialogService
{
    public List<string> Alerts { get; } = new();
    public Queue<string?> PromptAnswers { get; } = new();
    public Queue<bool> ConfirmAnswers { get; } = new();
    public Queue<string?> SheetAnswers { get; } = new();
    public Task AlertAsync(string title, string message, string ok = "OK") { Alerts.Add($"{title}: {message}"); return Task.CompletedTask; }
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel = "Cancel") => Task.FromResult(ConfirmAnswers.Count > 0 && ConfirmAnswers.Dequeue());
    public Task<string?> PromptAsync(string title, string message, string accept = "OK", string? initial = null, int maxLength = 50, bool numeric = false) => Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null);
    public Task<string?> ActionSheetAsync(string title, string cancel, params string[] options) => Task.FromResult(SheetAnswers.Count > 0 ? SheetAnswers.Dequeue() : null);
}

public sealed class NullFeedback : IFeedbackService
{
    public List<SoundEffect> Played { get; } = new();
    public void Play(SoundEffect effect) => Played.Add(effect);
    public void Haptic(bool strong = false) { }
}

public sealed class FakeBrowser : IAuthBrowser
{
    public bool IsSupported { get; set; } = true;
    public Uri? LastStart;
    public IReadOnlyDictionary<string, string>? Result;
    public Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri startUrl, Uri callbackUrl) { LastStart = startUrl; return Task.FromResult(Result); }
}

public sealed class FakeApple : IAppleSignIn
{
    public bool IsSupported { get; set; } = true;
    public AppleSignInResult? Result { get; set; }
    public Task<AppleSignInResult?> SignInAsync() => Task.FromResult(Result);
}

/// <summary>HTTP handler that routes requests to lambdas, recording what was sent.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    public List<(HttpMethod Method, string Path, string? Body, string? Auth)> Requests { get; } = new();
    public Func<HttpMethod, string, string?, string?, (HttpStatusCode, object?)> Handler { get; set; } = (_, _, _, _) => (HttpStatusCode.NotFound, null);
    public bool Offline { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline) throw new HttpRequestException("offline");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.PathAndQuery;
        var auth = request.Headers.Authorization?.Parameter;
        Requests.Add((request.Method, path, body, auth));
        var (status, payload) = Handler(request.Method, path, body, auth);
        var response = new HttpResponseMessage(status);
        if (payload is not null) response.Content = new StringContent(JsonSerializer.Serialize(payload, Api.ApiClient.Json), Encoding.UTF8, "application/json");
        return response;
    }
}

public sealed class FakeConnection : IMatchConnection
{
    public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;
    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<MatchSnapshot>? MatchState;
    public event Action<RoomInfo>? RoomUpdated;
    public event Action<MatchmakingStatus>? MatchmakingStatusChanged;
    public event Action<ReactionInfo>? Reaction;
    public event Action<MatchEndedInfo>? MatchEnded;

    public List<string> Calls { get; } = new();
    public MatchSnapshot? ResyncResult { get; set; }
    public MatchmakingStatus FindResult { get; set; } = new(MatchmakingState.Searching, 2, 1, null, null);
    public ServerError? NextError { get; set; }
    public bool FailConnect { get; set; }

    public Task ConnectAsync(CancellationToken ct = default)
    {
        Calls.Add("connect");
        if (FailConnect) throw new HttpRequestException("no network");
        Status = ConnectionStatus.Connected;
        return Task.CompletedTask;
    }
    public Task DisconnectAsync() { Status = ConnectionStatus.Disconnected; return Task.CompletedTask; }
    public Task<MatchmakingStatus> FindMatchAsync(int playerCount, Core.Model.GameMode mode, MatchQueue queue) { Calls.Add($"find:{playerCount}:{mode}:{queue}"); return Task.FromResult(FindResult); }
    public Task CancelMatchmakingAsync() { Calls.Add("cancel"); return Task.CompletedTask; }
    public Task<HubResult<RoomInfo>> CreatePrivateRoomAsync(int playerCount, Core.Model.GameMode mode) { Calls.Add($"create:{playerCount}:{mode}"); return Task.FromResult(new HubResult<RoomInfo>(Room(true) with { Mode = mode }, null)); }
    public RerollResult RerollAnswer { get; set; } = new(null, 9, 1, 6, 4);
    public BoostResult BoostAnswer { get; set; } = new(null, 2, 1, 0);
    /// <summary>When set, the next paid call throws (simulates a dropped connection with an unknown outcome).</summary>
    public bool ThrowNextPaidCall { get; set; }
    public Task<RerollResult> RerollAsync(string matchId, long expectedVersion, string requestId)
    {
        Calls.Add($"reroll:{expectedVersion}:{requestId}");
        if (ThrowNextPaidCall) { ThrowNextPaidCall = false; throw new HttpRequestException("dropped"); }
        return Task.FromResult(RerollAnswer);
    }
    public Task<ServerError?> PassTurnAsync(string matchId, long expectedVersion) { Calls.Add($"pass:{expectedVersion}"); return Task.FromResult(NextError); }
    public Task<BoostResult> UseBoostAsync(string matchId, long expectedVersion, string requestId)
    {
        Calls.Add($"boost:{expectedVersion}:{requestId}");
        if (ThrowNextPaidCall) { ThrowNextPaidCall = false; throw new HttpRequestException("dropped"); }
        return Task.FromResult(BoostAnswer);
    }
    public Task<HubResult<RoomInfo>> JoinPrivateRoomAsync(string code) { Calls.Add($"join:{code}"); return Task.FromResult(new HubResult<RoomInfo>(code == "ABCDEF" ? Room(false) : null, code == "ABCDEF" ? null : new ServerError(Core.Engine.ErrorCode.RoomNotFound, "No open table with that code."))); }
    public Task<HubResult<string>> StartPrivateRoomAsync(string roomId) { Calls.Add("start"); return Task.FromResult(new HubResult<string>("m1", null)); }
    public Task<ServerError?> LeaveRoomAsync(string id) { Calls.Add($"leave:{id}"); return Task.FromResult<ServerError?>(null); }
    public Task<ServerError?> RollAsync(string matchId, long expectedVersion) { Calls.Add($"roll:{matchId}:{expectedVersion}"); return Task.FromResult(NextError); }
    public Task<ServerError?> MoveAsync(string matchId, int pieceIndex, long expectedVersion) { Calls.Add($"move:{matchId}:{pieceIndex}:{expectedVersion}"); return Task.FromResult(NextError); }
    public Task<MatchSnapshot?> ResyncAsync(string? matchId) { Calls.Add($"resync:{matchId}"); return Task.FromResult(ResyncResult); }
    public Task<ServerError?> SendReactionAsync(string matchId, string reactionId) { Calls.Add($"react:{reactionId}"); return Task.FromResult<ServerError?>(null); }
    public Task<HubResult<RoomInfo>> RequestRematchAsync(string matchId) { Calls.Add($"rematch:{matchId}"); return Task.FromResult(new HubResult<RoomInfo>(Room(true), null)); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void PushState(MatchSnapshot s) => MatchState?.Invoke(s);
    public void PushEnded(MatchEndedInfo i) => MatchEnded?.Invoke(i);
    public void PushStatus(ConnectionStatus s) { Status = s; StatusChanged?.Invoke(s); }
    public void PushMatchmaking(MatchmakingStatus s) => MatchmakingStatusChanged?.Invoke(s);
    public void PushRoom(RoomInfo r) => RoomUpdated?.Invoke(r);
    public void PushReaction(ReactionInfo r) => Reaction?.Invoke(r);

    public static RoomInfo Room(bool host, bool started = false, int seated = 1) => new("room-1", "ABCDEF", 2,
        Enumerable.Range(0, seated).Select(i => new SeatInfo(i, (Core.Model.PlayerColor)(i * 2), $"P{i}", "crown", false, true)).ToList(), host, started, started ? "m1" : null);

    public static MatchSnapshot Snapshot(long version, int currentSeat = 0, Core.Model.GamePhase phase = Core.Model.GamePhase.AwaitingRoll, int yourSeat = 0, int[]? legal = null, int? pendingRoll = null) => new(
        "m1", version, phase, currentSeat, pendingRoll, pendingRoll, new[] { new[] { -1, -1, -1, -1 }, new[] { -1, -1, -1, -1 } },
        new[] { new SeatInfo(0, Core.Model.PlayerColor.Ruby, "Me", "crown", false, true), new SeatInfo(1, Core.Model.PlayerColor.Topaz, "Them", "lion", false, true) },
        Array.Empty<Core.Engine.GameEvent>(), null, yourSeat, DateTimeOffset.UtcNow.AddSeconds(20), legal ?? Array.Empty<int>());
}

/// <summary>Stands in for StoreKit.</summary>
public sealed class FakeIap : IInAppPurchaseService
{
    public bool IsSupported { get; set; } = true;
    public Dictionary<string, string> Prices { get; } = new();
    public StorePurchaseOutcome NextOutcome { get; set; } = new(StorePurchaseStatus.Purchased, "2000000001");
    public List<UnfinishedTransaction> Unfinished { get; } = new();
    public List<string> Finished { get; } = new();
    public List<(string Product, Guid Token)> Purchases { get; } = new();
    public Task<IReadOnlyDictionary<string, string>> GetLocalizedPricesAsync(IReadOnlyList<string> productIds) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(Prices.Where(p => productIds.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    public Task<StorePurchaseOutcome> PurchaseAsync(string productId, Guid appAccountToken)
    {
        Purchases.Add((productId, appAccountToken));
        if (NextOutcome.Status == StorePurchaseStatus.Purchased) Unfinished.Add(new UnfinishedTransaction(NextOutcome.TransactionId!, productId));
        return Task.FromResult(NextOutcome with { ProductId = productId });
    }
    public Task<IReadOnlyList<UnfinishedTransaction>> GetUnfinishedTransactionsAsync() => Task.FromResult<IReadOnlyList<UnfinishedTransaction>>(Unfinished.ToList());
    public Task FinishAsync(string transactionId) { Finished.Add(transactionId); Unfinished.RemoveAll(u => u.TransactionId == transactionId); return Task.CompletedTask; }
}
