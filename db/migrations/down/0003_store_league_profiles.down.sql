-- Reverses 0003_store_league_profiles.sql. DESTROYS store, purchase, inventory, league and profile-setting data.
-- Run only on purpose (e.g. rolling back a failed deploy before any real purchase), inside a transaction:
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -1 -f db/migrations/down/0003_store_league_profiles.down.sql
DROP TABLE league_rewards;
DROP TABLE league_standings;
DROP TABLE league_groups;
DROP TABLE league_players;
DROP TABLE league_seasons;
DROP TABLE inventory_ledger;
DROP TABLE inventory;
DROP FUNCTION ludo_require(boolean, text);
ALTER TABLE wallet_ledger DROP COLUMN operation_key;
ALTER TABLE wallet_ledger DROP COLUMN purchase_id;
DELETE FROM wallet_ledger WHERE reason IN ('purchase', 'store_buy', 'boost_use', 'league_reward', 'refund');
ALTER TABLE wallet_ledger DROP CONSTRAINT wallet_ledger_reason_check;
ALTER TABLE wallet_ledger ADD CONSTRAINT wallet_ledger_reason_check
    CHECK (reason IN ('match_reward', 'admin_adjustment', 'reroll', 'starter_grant', 'dev_grant'));
DROP TABLE economy_operations;
DROP TABLE purchases;
DROP TABLE player_mode_stats;
DROP TABLE profile_settings;
ALTER TABLE profiles DROP COLUMN frame_id;
ALTER TABLE match_participants DROP COLUMN league_points;
ALTER TABLE match_participants DROP COLUMN boosts_used;
UPDATE matches SET mode = 'matchmaking' WHERE mode = 'casual';
ALTER TABLE matches DROP CONSTRAINT matches_mode_check;
ALTER TABLE matches ADD CONSTRAINT matches_mode_check CHECK (mode IN ('matchmaking', 'private'));
DELETE FROM schema_migrations WHERE version = '0003_store_league_profiles';
