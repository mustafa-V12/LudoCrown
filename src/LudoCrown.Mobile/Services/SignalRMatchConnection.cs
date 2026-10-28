using System.Text.Json.Serialization;
using LudoCrown.App.Auth;
using LudoCrown.App.Services;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace LudoCrown.Mobile.Services;

/// <summary>
/// SignalR connection to the authoritative game server. Reconnects automatically with back-off; when the
/// app returns to the foreground or the network comes back, it restarts the connection if needed. After any
/// reconnect, open screens call Resync so the board matches the server exactly.
/// </summary>
public sealed class SignalRMatchConnection : IMatchConnection
{
    private readonly AppConfig _config;
    private readonly SessionService _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private HubConnection? _hub;
    private ConnectionStatus _status = ConnectionStatus.Disconnected;

    public SignalRMatchConnection(AppConfig config, SessionService session)
    {
        _config = config;
        _session = session;
        _session.SessionChanged += async s => { if (s is null) await DisconnectAsync(); };
        Connectivity.Current.ConnectivityChanged += async (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet && _hub is not null && _hub.State == HubConnectionState.Disconnected)
                await TryRestartAsync();
        };
    }

    public ConnectionStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(value);
        }
    }

    public event Action<ConnectionStatus>? StatusChanged;
    public event Action<MatchSnapshot>? MatchState;
    public event Action<RoomInfo>? RoomUpdated;
    public event Action<MatchmakingStatus>? MatchmakingStatusChanged;
    public event Action<ReactionInfo>? Reaction;
    public event Action<MatchEndedInfo>? MatchEnded;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_session.IsSignedIn) throw new InvalidOperationException("Sign in to play online.");
            _hub ??= Build();
            if (_hub.State == HubConnectionState.Connected) return;
            if (_hub.State != HubConnectionState.Disconnected) return; // connecting/reconnecting already
            Status = ConnectionStatus.Connecting;
            try
            {
                await _hub.StartAsync(ct);
                Status = ConnectionStatus.Connected;
            }
            catch
            {
                Status = ConnectionStatus.Disconnected;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync()
    {
        if (_hub is null) return;
        var hub = _hub;
        _hub = null;
        try { await hub.StopAsync(); await hub.DisposeAsync(); }
        catch (Exception) { /* already gone */ }
        Status = ConnectionStatus.Disconnected;
    }

    /// <summary>Call when the app returns to the foreground.</summary>
    public async Task TryRestartAsync()
    {
        if (!_session.IsSignedIn || _hub is null || _hub.State != HubConnectionState.Disconnected) return;
        try { await ConnectAsync(); } catch (Exception) { /* screens show their own retry UI */ }
    }

    private HubConnection Build()
    {
        var hub = new HubConnectionBuilder()
            .WithUrl(_config.ServerUrl.TrimEnd('/') + HubNames.Path, o => o.AccessTokenProvider = () => _session.GetAccessTokenAsync())
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30) })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

        hub.On<MatchSnapshot>(HubNames.OnMatchState, s => MatchState?.Invoke(s));
        hub.On<RoomInfo>(HubNames.OnRoomUpdated, r => RoomUpdated?.Invoke(r));
        hub.On<MatchmakingStatus>(HubNames.OnMatchmakingStatus, s => MatchmakingStatusChanged?.Invoke(s));
        hub.On<ReactionInfo>(HubNames.OnReaction, r => Reaction?.Invoke(r));
        hub.On<MatchEndedInfo>(HubNames.OnMatchEnded, i => MatchEnded?.Invoke(i));

        hub.Reconnecting += _ => { Status = ConnectionStatus.Reconnecting; return Task.CompletedTask; };
        hub.Reconnected += _ => { Status = ConnectionStatus.Connected; return Task.CompletedTask; };
        hub.Closed += _ => { Status = ConnectionStatus.Disconnected; return Task.CompletedTask; };
        return hub;
    }

    private HubConnection Hub => _hub ?? throw new InvalidOperationException("Not connected.");

    public Task<MatchmakingStatus> FindMatchAsync(int playerCount, GameMode mode, MatchQueue queue) =>
        Hub.InvokeAsync<MatchmakingStatus>(HubNames.FindMatch, playerCount, GameModes.Get(mode).Id, queue == MatchQueue.Casual ? "casual" : "ranked");
    public Task CancelMatchmakingAsync() => _hub is null ? Task.CompletedTask : _hub.InvokeAsync(HubNames.CancelMatchmaking);
    public Task<HubResult<RoomInfo>> CreatePrivateRoomAsync(int playerCount, GameMode mode) => Hub.InvokeAsync<HubResult<RoomInfo>>(HubNames.CreatePrivateRoom, playerCount, GameModes.Get(mode).Id);
    public Task<HubResult<RoomInfo>> JoinPrivateRoomAsync(string code) => Hub.InvokeAsync<HubResult<RoomInfo>>(HubNames.JoinPrivateRoom, code);
    public Task<HubResult<string>> StartPrivateRoomAsync(string roomId) => Hub.InvokeAsync<HubResult<string>>(HubNames.StartPrivateRoom, roomId);
    public Task<ServerError?> LeaveRoomAsync(string id) => _hub is null ? Task.FromResult<ServerError?>(null) : _hub.InvokeAsync<ServerError?>(HubNames.LeaveRoom, id);
    public Task<ServerError?> RollAsync(string matchId, long expectedVersion) => Hub.InvokeAsync<ServerError?>(HubNames.Roll, matchId, expectedVersion);
    public Task<ServerError?> MoveAsync(string matchId, int pieceIndex, long expectedVersion) => Hub.InvokeAsync<ServerError?>(HubNames.Move, matchId, pieceIndex, expectedVersion);
    public Task<RerollResult> RerollAsync(string matchId, long expectedVersion, string requestId) => Hub.InvokeAsync<RerollResult>(HubNames.Reroll, matchId, expectedVersion, requestId);
    public Task<ServerError?> PassTurnAsync(string matchId, long expectedVersion) => Hub.InvokeAsync<ServerError?>(HubNames.PassTurn, matchId, expectedVersion);
    public Task<BoostResult> UseBoostAsync(string matchId, long expectedVersion, string requestId) => Hub.InvokeAsync<BoostResult>(HubNames.UseBoost, matchId, expectedVersion, requestId);
    public Task<MatchSnapshot?> ResyncAsync(string? matchId) => Hub.InvokeAsync<MatchSnapshot?>(HubNames.Resync, matchId);
    public Task<ServerError?> SendReactionAsync(string matchId, string reactionId) => Hub.InvokeAsync<ServerError?>(HubNames.SendReaction, matchId, reactionId);
    public Task<HubResult<RoomInfo>> RequestRematchAsync(string matchId) => Hub.InvokeAsync<HubResult<RoomInfo>>(HubNames.RequestRematch, matchId);

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
