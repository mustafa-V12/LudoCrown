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

## Winning
- The first player to bring all four pieces to the crown wins, and the match ends.
- Other players are ranked by pieces crowned, then by total progress along the track.

## Online-only rules (server)
- **Turn timer:** 20 seconds per turn. If it runs out, the server plays the whole turn for you (rolls, then picks a move with the Normal bot logic).
- **Disconnects:** your turns get a 6-second timer and are auto-played until you reconnect. After 90 seconds offline you are marked as having **left**, and a bot plays your seat for the rest of the match.
- **Idle players:** 3 consecutive timed-out turns while connected also count as leaving.
- **Leaving** (by choice, inactivity or disconnect) means no rewards and counts as an abandon in your statistics.
- If every human leaves, the match ends as abandoned.

## Rewards (server-decided)

| | 2-player | 4-player |
|---|---|---|
| 1st place | 100 coins, 50 XP | 150 coins, 70 XP |
| 2nd place | 10 coins, 20 XP | 60 coins, 40 XP |
| 3rd / 4th place | — | 10 coins, 20 XP |

- Private tables give no coins (to prevent farming with friends). They give half XP when at least two humans play.
- Levels: one level per 250 XP.
