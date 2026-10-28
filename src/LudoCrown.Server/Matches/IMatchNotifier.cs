using LudoCrown.Core.Protocol;

namespace LudoCrown.Server.Matches;

/// <summary>Pushes messages to a specific user (all their connected devices). Abstracted for tests.</summary>
public interface IMatchNotifier
{
    Task SendSnapshotAsync(Guid userId, MatchSnapshot snapshot);
    Task SendRoomUpdatedAsync(Guid userId, RoomInfo room);
    Task SendMatchmakingStatusAsync(Guid userId, MatchmakingStatus status);
    Task SendReactionAsync(Guid userId, ReactionInfo reaction);
    Task SendMatchEndedAsync(Guid userId, MatchEndedInfo info);
}

/// <summary>Rewards are decided only here, on the server, from the authoritative result.</summary>
public static class RewardPolicy
{
    public static (int Coins, int Xp) For(Data.MatchMode mode, int playerCount, int placement, Data.ParticipantResult result, int humanCount)
    {
        if (result == Data.ParticipantResult.Abandoned) return (0, 0);

        var xp = (playerCount, placement) switch
        {
            (2, 1) => 50,
            (2, _) => 20,
            (_, 1) => 70,
            (_, 2) => 40,
            _ => 20,
        };

        if (mode == Data.MatchMode.Private)
            // No coins in private rooms (prevents farming with friends); XP only when playing other people.
            return (0, humanCount >= 2 ? xp / 2 : 0);

        var coins = (playerCount, placement) switch
        {
            (2, 1) => 100,
            (2, _) => 10,
            (_, 1) => 150,
            (_, 2) => 60,
            _ => 10,
        };
        return (coins, xp);
    }

    /// <summary>Gems earned: 1 for winning a matchmaking match. Private tables and abandons earn none.</summary>
    public static int GemsFor(Data.MatchMode mode, int placement, Data.ParticipantResult result) =>
        mode == Data.MatchMode.Matchmaking && result == Data.ParticipantResult.Win && placement == 1 ? 1 : 0;
}
