# League: seasons, divisions and rewards

All numbers here come from `src/LudoCrown.Server/League/LeagueRules.cs`, and its tests cover them. If you change one, change both. The server owns everything here. Clients can read standings, but they cannot submit points, set ratings or claim rewards.

## Divisions

The six division names and crests are original to Ludo Crown, from lowest to highest:

| # | Division | Crest |
|---|---|---|
| 0 | Squire | shield |
| 1 | Knight | helm |
| 2 | Baron | banner |
| 3 | Earl | seal |
| 4 | Duke | orb |
| 5 | Monarch | crown |

New players start in **Squire**.

## Seasons
- A season lasts `League:SeasonDays` days: **14** by default, 7 in Development. Each season has a fixed start and end time, stored in `league_seasons`.
- The server creates the first season automatically. When a season's end time passes, a background job does the following:
  1. **Finalizes** the season: ranks every group, then applies promotion, demotion and rewards.
  2. **Opens** the next season.
- The app shows the time remaining. During the brief finalization window it shows "Season ending — rewards are being calculated".

## Which matches count
- Only **ranked matchmaking** matches with at least **2 human players** count. Matches against bots only, casual-queue matches and private tables never give league points.
- You join your division's group with your **first counted match** of the season.
- Ranked matches never allow boost bottles. **Gem rerolls are allowed in ranked matches**, with the same limits as everywhere else: 7 per Classic match and 5 per Quick match. This includes purchased gems. It was an explicit product decision; see DECISIONS.md.

## Points per counted match

| Result | 2 players | 4 players |
|---|---|---|
| 1st | +20 | +30 |
| 2nd | −10 | +10 |
| 3rd | — | −5 |
| 4th | — | −15 |
| Left the match (abandon) | −25 | −25 |

A season total never goes below 0.

## Groups and ranking
- Players are grouped by division in **groups of up to 30**. A new player joins the first open group of their division.
- Ranking inside a group is decided in this order:
  1. points,
  2. wins,
  3. whoever reached their total first,
  4. a stable ID order.
- The leaderboard query is bounded to the player's own group, at most 30 rows.

## Promotion and demotion (at season end)
- **Promotion:** the top 5 of a group move up one division, if they played at least **5** counted matches that season. Monarch is the top division.
- **Demotion:** the bottom 5 move down one division, but only in groups of at least **15** players. Small early groups never demote anyone. Squire is the floor.
- Everyone else stays in their division for the next season.

## Rewards (fixed, not random)

Rewards are paid to the top 10 of every group who played at least one counted match:

| Division | #1 | #2–5 | #6–10 |
|---|---|---|---|
| Monarch | 12,000 gold + 35 gems + 3 Shields | 6,000 + 18 + 2 | 2,500 + 8 + 1 |
| Duke | 8,500 + 25 + 2 | 4,200 + 12 + 1 | 1,800 + 6 + 1 |
| Earl | 6,000 + 18 + 2 | 3,000 + 9 + 1 | 1,300 + 4 |
| Baron | 4,000 + 12 + 1 | 2,000 + 6 + 1 | 900 + 3 |
| Knight | 2,500 + 8 + 1 | 1,300 + 4 | 600 + 2 |
| Squire | 1,500 + 5 | 800 + 3 | 400 + 1 |

- The table is monotonic: a higher division or a better rank never pays less.
- The app shows this table and the eligibility rules *before* the season ends.
- No real-world prizes or cash.
- **Idempotent:** each reward is one row in `league_rewards`, which is UNIQUE per season and player, plus one economy operation with the key `league:{season}:{user}`. If finalization is interrupted and runs again, nothing is paid twice. Every payout appears in the ledger with the reason `league_reward`.

## Statistics rules (profiles)
These rules apply to profile statistics (overall, Classic and Quick), not to league points:

- **Played:** every server-recorded online match a human finished or left.
- **Won / lost:** a win means 1st place. Every other finishing place is a loss.
- **Abandoned:** leaving by choice, after 3 idle turns, or after more than 90 seconds disconnected. It counts as played, is not a win, resets the win streak and earns no rewards.
- **Short disconnects:** a player who reconnects within the grace period is unaffected.
- **Not counted:** matches the server never completed, for example ones lost in a crash before recovery.
- **Rematches** are separate matches and count normally.
- **Win streak:** consecutive wins in completed matches. Any loss or abandon resets it to 0. "Best" is the highest streak ever reached.
- Offline games against bots are not part of online statistics.

## Local development
- `POST /dev/league/end-season` ends the current season immediately and runs finalization. It exists only in Development with `DevTools:AllowGemGrant=true`.
- The league screen labels development data clearly: "Development data (Development server) — not live rankings".
