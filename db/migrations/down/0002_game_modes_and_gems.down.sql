-- Reverses 0002_game_modes_and_gems.sql (apply 0003's down script first). DESTROYS gem balances and gem ledger rows.
--   psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -1 -f db/migrations/down/0002_game_modes_and_gems.down.sql
ALTER TABLE match_participants DROP COLUMN gems_awarded;
ALTER TABLE match_participants DROP COLUMN rerolls_used;
DROP INDEX ix_matches_mode_ended;
ALTER TABLE matches DROP COLUMN game_mode;
DELETE FROM wallet_ledger WHERE currency = 'gems';
DROP INDEX ix_ledger_user_time;
DROP INDEX ux_ledger_idempotency;
DROP INDEX ux_ledger_match_reward;
ALTER TABLE wallet_ledger DROP CONSTRAINT wallet_ledger_reason_check;
ALTER TABLE wallet_ledger ADD CONSTRAINT wallet_ledger_reason_check CHECK (reason IN ('match_reward', 'admin_adjustment'));
ALTER TABLE wallet_ledger ADD CONSTRAINT uq_ledger_match_reward UNIQUE (user_id, match_id, reason);
ALTER TABLE wallet_ledger DROP COLUMN idempotency_key;
ALTER TABLE wallet_ledger DROP COLUMN currency;
ALTER TABLE wallets DROP COLUMN gems;
DELETE FROM schema_migrations WHERE version = '0002_game_modes_and_gems';
