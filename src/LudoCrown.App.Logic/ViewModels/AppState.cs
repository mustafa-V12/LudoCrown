using LudoCrown.App.Game;
using LudoCrown.Core.Model;
using LudoCrown.Core.Protocol;

namespace LudoCrown.App.ViewModels;

public enum PrivateRoomMode { Create, Join, Existing }

/// <summary>
/// In-memory hand-off between screens (Shell navigation passes strings; complex objects live here).
/// Not persisted: offline games persist through <see cref="OfflineGameSession"/>'s save, online games
/// through the server.
/// </summary>
public sealed class AppState
{
    public IGameSession? CurrentSession { get; set; }
    public GameResult? LastResult { get; set; }
    public IReadOnlyList<OfflineSeat>? LastOfflineSeats { get; set; }

    public int RequestedPlayerCount { get; set; } = 2;
    /// <summary>Game mode chosen on the home / offline screens. Fixed for the whole match once it starts.</summary>
    public GameMode SelectedMode { get; set; } = GameMode.Classic;
    /// <summary>Ranked (league points, no boosts) or casual (boosts allowed) matchmaking.</summary>
    public MatchQueue SelectedQueue { get; set; } = MatchQueue.Ranked;
    /// <summary>Public id (friend code) of the player whose profile screen is open.</summary>
    public string? ViewedPlayerId { get; set; }
    public PrivateRoomMode PrivateRoomMode { get; set; }
    public string? PrivateRoomCode { get; set; }
    public RoomInfo? PendingRoom { get; set; }

    /// <summary>Set by a ludocrown://join/CODE deep link; consumed by the home screen.</summary>
    public string? PendingJoinCode { get; set; }

    /// <summary>When true, the email sign-in screen links the address to the current guest account.</summary>
    public bool EmailLinkMode { get; set; }

    public async Task EndSessionAsync()
    {
        if (CurrentSession is not null) await CurrentSession.DisposeAsync();
        CurrentSession = null;
    }
}
