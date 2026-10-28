# Ludo Crown rules ("Crown Classic")

These rules are implemented in `src/LudoCrown.Core/Engine/GameEngine.cs` and covered by `tests/LudoCrown.Core.Tests`. Online matches always use these defaults. Settings marked *configurable* can be changed through `RuleSet` (for example for future house-rule options).

## Board and setup
- The board is a 15×15 cross. The main track has 52 squares, each colour has a 5-square home column, and the crown sits in the centre.
- Colours in turn order, clockwise: **Ruby** (bottom-left), **Emerald** (top-left), **Topaz** (top-right), **Sapphire** (bottom-right).
- **2 players:** Ruby vs Topaz (opposite corners). **4 players:** all colours. The engine also supports 3 players (Ruby, Emerald, Topaz), but online play offers only 2 and 4.
- Each player has 4 pieces, which start in their yard. Ruby (seat 0) moves first.

## Turn
1. Roll one six-sided die.
2. Move one piece by the number rolled, if any piece can legally move.
3. If no piece can move, the turn passes. This also applies after a 6. The game shows a "no moves" notice.

## Entering play
- A piece leaves the yard only on a **6** (*configurable*: `EntryRolls`). It is placed on its colour's start square.

## Movement
- Pieces move clockwise along the main track. After 50 squares (one square short of a full lap) they turn into their own home column.
- Reaching the crown needs the **exact** roll. A roll that would overshoot is not a legal move for that piece.
- Your own pieces may share a square. There are no blockades.

## Captures and safe squares
- Landing exactly on a square occupied by opponent pieces sends **all** of those pieces back to their yards.
- **Safe squares:** the four start squares and the four star squares (the 9th square of each colour's quarter). Pieces there can't be captured, and pieces of different colours can share them.
- Pieces in a home column, or on the crown, can never be captured.

## Extra turns
- Rolling a **6** gives another roll after moving (*configurable*).
- **Capturing** gives another roll (*configurable*).
- Bringing a piece to the **crown** gives another roll (*configurable*).
- **Three 6s in a row** forfeit the third roll and end the turn (*configurable*).

## Game modes and winning
The mode is chosen before a match: in matchmaking, at a private table or offline. It is shown in the lobby and during the match, and it **cannot change once the match starts**. The server validates the mode and enforces its win condition. Modes live in a registry (`Core/Model/GameModes.cs`) that has room for up to five; only the two below exist, and the app shows only implemented modes.

| Mode | Win condition | Gem rerolls per player per match |
|---|---|---|
| **Classic** | The first player to bring **all 4** pieces to the crown wins | 7 |
| **Quick** | The first player to bring **1** piece to the crown wins | 5 |

All other rules (entering, movement, captures, safe squares, extra turns) are identical in both modes. When the match ends, the other players are ranked by pieces crowned, then by total progress along the track.

## Gem rerolls (online)
- **When:** after you roll and before you move, on your own turn. You can reroll as long as you have gems and uses left. A reroll is not possible after you've moved a piece, or before you've rolled.
- **Cost:** 1 gem per reroll. **Limit:** 7 per Classic match, 5 per Quick match. Each reroll uses one gem and one use.
- **Who decides:** the server rolls the new value. It **replaces** the pending roll; there is no choosing between the two.
  - If the new value is a 6, it counts exactly as if it had been your original roll: extra turn, and the three-6s rule.
- **Retries:** each request carries a request ID. A retried or duplicated request returns the same result and is never charged twice, even across reconnects or a server restart.
- **No legal move:** if you could reroll and the roll has no legal move, the server holds the roll for **5 seconds**. In that time you can reroll or pass. After 5 seconds it passes for you. This does not count as a missed turn.
  - With no gems or rerolls left, a roll without moves passes immediately, as before.
- The game shows your gem balance, rerolls used and remaining, the cost, and why a reroll is unavailable.
- **Offline games have no rerolls**, because gems belong to your online account. The offline screen explains this.
- Rerolls are allowed in **all** online matches, ranked league matches included, with these same limits.

## Ranked and casual matches
- **Ranked** (matchmaking): counts for the league (see LEAGUE.md). Boost bottles are not allowed.
- **Casual** (matchmaking) and **private tables**: no league points. Boost bottles are allowed.

## Shield bottle (boost, casual only)
- **Effect:** drink it on your turn, before rolling. Until your next turn starts, none of your pieces can be captured. An opponent who lands on a shielded piece just shares the square, as on a safe square.
- **What it doesn't change:** dice, legal moves, extra turns and turn order. It can't make a result certain.
- **Limit:** 1 per player per match. It is consumed only when the server accepts it, and a retried request never uses a second bottle.
- **Visibility:** every player sees the 🛡 marker on your badge while it is active.
- **Not allowed** in ranked league matches or offline. It ends at the start of your next turn, or when the match ends. If you disconnect, the shield still expires on schedule.

## Quick chat (online)
- 12 preset phrases and 8 emoji. **There is no free-text chat and no voice chat.**
- **Limits:** at most 6 messages per 20 seconds, and at least 1.5 seconds between messages.
- Messages from players you've blocked, or who have blocked you, are not delivered. You can mute a player for the current match by tapping their badge.
- You can turn chat off in Settings. You then neither send nor receive messages, and the server stops delivering to you.
- Messages are not stored. A player who is offline simply doesn't receive them.

## Online-only rules (server)
- **Turn timer:** 20 seconds per turn. If it runs out, the server plays the whole turn for you (rolls, then picks a move with the Normal bot logic).
- **Disconnects:** your turns get a 6-second timer and are auto-played until you reconnect. After 90 seconds offline you are marked as having **left**, and a bot plays your seat for the rest of the match.
- **Idle players:** 3 consecutive timed-out turns while connected also count as leaving.
- **Leaving** (by choice, inactivity or disconnect) means no rewards and counts as an abandon in your statistics.
- If every human leaves, the match ends as abandoned.

## Rewards (server-decided)

Gems: **+1** for winning a ranked or casual matchmaking match. Private tables and abandoned matches give none. New accounts start with 10 gems.


| | 2-player | 4-player |
|---|---|---|
| 1st place | 100 coins, 50 XP | 150 coins, 70 XP |
| 2nd place | 10 coins, 20 XP | 60 coins, 40 XP |
| 3rd / 4th place | — | 10 coins, 20 XP |

- Private tables give no coins (to prevent farming with friends). They give half XP when at least two humans play.
- Levels: one level per 250 XP.
