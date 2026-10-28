# Database (PostgreSQL)

The schema is built by the migrations in `db/migrations/` (`0001_initial_schema`, `0002_game_modes_and_gems`, `0003_store_league_profiles`), and roles are in `db/roles.sql`. All SQL the server runs is in `src/LudoCrown.Server.Postgres/Sql.cs`. You can check every statement against the schema with `tools/verify-sql.py`.

## Tables

| Table | Holds | Key constraints / indexes |
|---|---|---|
| `users` | Account ID, guest flag, timestamps | PK `id` |
| `auth_identities` | Linked sign-in methods: provider + provider subject ID, optional email. **No passwords.** | UNIQUE (`provider`, `subject`), index on `user_id`, provider CHECK |
| `profiles` | Display name, avatar ID, friend code, XP | Name length CHECK, avatar whitelist CHECK, UNIQUE `friend_code` |
| `player_stats` | Games, wins, captures, abandons, streaks | Non-negative CHECKs, wins ≤ games |
| `wallets` | Gold (`coins`) and `gems` balances | `coins >= 0`, `gems >= 0` |
| `wallet_ledger` | Every gold and gem change: currency, signed amount, reason, time, match or purchase, operation key | Reason CHECK; partial UNIQUE (`user_id`, `match_id`, `currency`) for match rewards; UNIQUE (`user_id`, `idempotency_key`) |
| `economy_operations` | One row per applied economy operation: user, idempotency key, reason | PK (`user_id`, `idempotency_key`). A retried request applies nothing. |
| `inventory` | Items owned: Shield bottles, frames | Item whitelist; `0 ≤ quantity ≤ 999`; frames at most 1 |
| `inventory_ledger` | Every item change, with reason, match or purchase | Reason CHECK, `delta <> 0` |
| `purchases` | Store transactions: store, product, transaction ID, environment, status (pending, verified, credited, rejected, refunded), failure reason | UNIQUE (`store`, `transaction_id`): one transaction is credited once, to one account. `dev_sandbox` ⇔ `environment = 'development'`. `user_id` is SET NULL on account deletion, kept for accounting and refunds. |
| `profile_settings` | Privacy: show stats, show league; chat on or off | PK `user_id` |
| `player_mode_stats` | Per-mode played, won, lost, abandons, streaks (Classic and Quick) | PK (`user_id`, `game_mode`); won + lost + abandons ≤ played |
| `league_seasons` | Season number, start, end, status (active, finalizing, completed) | `ends_at > starts_at`; only one open season (partial unique index) |
| `league_players` | Current division per player | `division` 0–5 |
| `league_groups` | Groups per season and division, member count | `member_count` 0–30; index of open groups |
| `league_standings` | Points, matches, wins, final rank and outcome per season and player | PK (`season_id`, `user_id`); `points >= 0`; ranking index |
| `league_rewards` | The reward paid per season and player | PK (`season_id`, `user_id`): paid once |
| `refresh_tokens` | SHA-256 **hashes** of refresh tokens, rotation chain | UNIQUE `token_hash`, partial index of active tokens per user |
| `auth_codes` | Short-lived single-use secrets (email codes, OAuth state, login codes, link tickets), stored as hashes | UNIQUE (`kind`, `code_hash`), index on expiry |
| `matches` | Mode (matchmaking = ranked, casual, private), game mode (classic, quick), player count, status, room code, timestamps, winner, live/final state JSON, version | Status CHECK, `ended_at` consistent with status, partial index on active matches |
| `match_participants` | Seat, user (NULL for bots and deleted accounts), name, colour, placement, result, rewards, gems awarded, rerolls used, boosts used, league points | PK (`match_id`, `seat`), bots can't have a user, index on (`user_id`, `match_id`) |
| `friendships` | One row per pair (`user_low` < `user_high`), pending/accepted, requester | Pair-order CHECK |
| `blocks` | Blocker → blocked | No self-blocks |
| `reports` | Reporter, reported user, match, reason, details (≤ 500 characters), status | Reason CHECK, partial index on open reports |
| `schema_migrations` | Applied migration versions | Created by the migration runner |

## Access rules

**Who can reach the database at all:**
- The database is reachable **only by the server**. The app never connects to it.
- `ludocrown_app` (the running server) has SELECT/INSERT/UPDATE/DELETE only. It cannot change the schema.
- `ludocrown_migrator` owns the schema and is used only for migrations.

**What the server lets each user do, per data type:**

| Data | Read | Write |
|---|---|---|
| Account, identities, tokens | Owner only (`/me`, export) | Server auth flows only |
| Profile | Owner; others see public fields (name, avatar, level, games, wins) unless either has blocked the other | Owner can change name (validated) and avatar (whitelist) |
| Stats, XP, coins, ledger | Owner (`/me`, export) | **Server only**, when a match completes |
| Gems, inventory, purchases, economy ledger | Owner (`/me/inventory`, `/me/ledger`, export) | **Server only**: rerolls, boosts, verified purchases, gold buys, rewards, refunds |
| Per-mode stats | Owner; others only if the owner allows it (`show_stats`) | **Server only** |
| League standings and rewards | Your own group's table (at most 30 rows); others' standing only if allowed (`show_league`) | **Server only** |
| Privacy settings | Owner | Owner (`PATCH /me/privacy`) |
| Matches and participants | Participants (own history) | **Server only** |
| Friendships | The two users | Request / accept / remove, by either user |
| Blocks | Blocker | Blocker |
| Reports | Operators (SQL) | Any signed-in user can file one (reason whitelist, length limit) |

## Migrations
- Migrations are versioned files `db/migrations/NNNN_description.sql`, applied in name order. Each runs in a transaction under an advisory lock, and the version is recorded in `schema_migrations`.
- **Automatically:** the server applies them at startup when `Database__MigrateOnStartup=true` (the default in Development).
- **Manually / CI:** `psql "$MIGRATOR_URL" -v ON_ERROR_STOP=1 -f db/migrations/<file>.sql`
- **New change:** add the next `NNNN_….sql` plus `db/migrations/down/NNNN_….down.sql`. Never edit an applied migration. Prefer additive changes, such as new nullable columns, then backfill.
- **Rolling back:** run the down scripts in reverse order as the migrator, then delete the version from `schema_migrations`:

  ```bash
  psql "$MIGRATOR_URL" -v ON_ERROR_STOP=1 -f db/migrations/down/0003_store_league_profiles.down.sql
  psql "$MIGRATOR_URL" -c "DELETE FROM schema_migrations WHERE version = '0003_store_league_profiles'"
  ```

  Down scripts **delete the data** in the tables they drop (purchases, league, inventory). Back up first. The scripts were tested on PostgreSQL 16: all migrations up, 0003 and 0002 down, then up again, followed by the SQL check.
- **What 0002 adds:** gem balances (existing accounts receive the 10 starter gems), gem ledger entries with idempotency keys, the game mode per match, and rerolls and gems per participant.
- **What 0003 adds:** casual matches, purchases, economy operations, inventory and its ledger, boosts and league points per participant, frames, privacy settings, per-mode stats, and the league tables.

## Retention
- Expired auth codes are purged daily, and expired or revoked refresh tokens after 7 days (`PostgresMaintenanceService`).
- Finished matches are deleted after `Database:MatchRetentionDays` (default 730 days). Statistics remain.
- Reports: keep while under review, then delete or anonymise according to your policy.

## Account deletion and data export
- **In-app deletion** (`DELETE /me`) runs one transaction that:
  1. renames the user's match seats to "Deleted player",
  2. detaches their reports,
  3. deletes the `users` row. This cascades to identities, profile, stats, wallet, ledgers, inventory, economy operations, privacy settings, mode stats, league standings and rewards, tokens, auth codes, friendships and blocks. Purchase records stay with `user_id = NULL`; store accounting and refund handling need them, and they contain no personal data. `match_participants.user_id` becomes NULL through `ON DELETE SET NULL`.
- **Sign in with Apple:** the app also obtains a fresh authorization code so the server can revoke the Apple token. This needs `Auth:Apple` key configuration.
- **Export** (`GET /me/export`) returns a JSON file with the account, identities, profile, privacy settings, stats (overall and per mode), gold, gems, inventory, the economy ledger, purchases, match history, friends and the blocked list.
- Requests made outside the app (for example by email to support) are handled with the same steps by an operator.

## What is stored where, and why

| Location | Data | Why |
|---|---|---|
| iPhone Keychain | Access + refresh tokens | Stay signed in. The Keychain is encrypted and not readable by other apps. |
| iPhone Preferences | Settings (sound, haptics, motion, reactions, auto-move); offline saved game; last offline player name | Device-local preferences; resuming offline games without an account |
| Server database | Accounts, linked identities, profiles, privacy settings, stats, match records, friends, blocks, reports, balances, inventory, purchases, ledgers, league data | Shared across devices; must be trustworthy and authoritative |
| Server memory | Live matches, matchmaking queues, private-room lobbies | Low-latency play. Live match state is also saved to the database after every move. |

## Moderation
Useful queries:

```sql
-- Open reports, newest first
SELECT r.created_at, r.reason, r.details, p.display_name AS reported, r.reported_id
FROM reports r LEFT JOIN profiles p ON p.user_id = r.reported_id
WHERE r.status = 'open' ORDER BY r.created_at DESC;

-- Rename an offensive display name
UPDATE profiles SET display_name = 'Player' || floor(random()*9000+1000)::int WHERE user_id = '...';

-- Close a report
UPDATE reports SET status = 'actioned' WHERE id = '...';
```
