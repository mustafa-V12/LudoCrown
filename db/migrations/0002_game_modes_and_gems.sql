-- Ludo Crown — game modes, gems and rerolls.
-- Adds: gem balances, gem entries in the ledger (with idempotency keys so a spend can never be applied twice),
-- the game mode of each match, and rerolls used / gems awarded per participant.

-- Gem balance (earned only; spent on rerolls). Never negative.
ALTER TABLE wallets ADD COLUMN gems bigint NOT NULL DEFAULT 0 CONSTRAINT ck_wallets_gems CHECK (gems >= 0);

-- The ledger now records both currencies and supports idempotent operations.
ALTER TABLE wallet_ledger ADD COLUMN currency text NOT NULL DEFAULT 'coins' CONSTRAINT ck_ledger_currency CHECK (currency IN ('coins', 'gems'));
ALTER TABLE wallet_ledger ADD COLUMN idempotency_key text CONSTRAINT ck_ledger_idem_len CHECK (idempotency_key IS NULL OR length(idempotency_key) BETWEEN 1 AND 200);
ALTER TABLE wallet_ledger DROP CONSTRAINT uq_ledger_match_reward;
ALTER TABLE wallet_ledger DROP CONSTRAINT wallet_ledger_reason_check;
ALTER TABLE wallet_ledger ADD CONSTRAINT wallet_ledger_reason_check
    CHECK (reason IN ('match_reward', 'admin_adjustment', 'reroll', 'starter_grant', 'dev_grant'));
-- A match pays each currency out once per player.
CREATE UNIQUE INDEX ux_ledger_match_reward ON wallet_ledger (user_id, match_id, currency) WHERE reason = 'match_reward';
-- A retried spend/grant with the same key is a no-op.
CREATE UNIQUE INDEX ux_ledger_idempotency ON wallet_ledger (user_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX ix_ledger_user_time ON wallet_ledger (user_id, created_at);

-- Existing accounts receive the same starter grant as new ones.
UPDATE wallets SET gems = gems + 10;
INSERT INTO wallet_ledger (user_id, delta, reason, currency, idempotency_key)
SELECT user_id, 10, 'starter_grant', 'gems', 'starter' FROM wallets
ON CONFLICT DO NOTHING;

-- Game mode of each match (fixed when the match is created).
ALTER TABLE matches ADD COLUMN game_mode text NOT NULL DEFAULT 'classic' CONSTRAINT ck_matches_game_mode CHECK (game_mode IN ('classic', 'quick'));
CREATE INDEX ix_matches_mode_ended ON matches (game_mode, ended_at) WHERE status <> 'active';

-- Per-player reroll usage and gem rewards, saved with the result.
ALTER TABLE match_participants ADD COLUMN rerolls_used smallint NOT NULL DEFAULT 0 CONSTRAINT ck_participants_rerolls CHECK (rerolls_used BETWEEN 0 AND 20);
ALTER TABLE match_participants ADD COLUMN gems_awarded integer NOT NULL DEFAULT 0 CONSTRAINT ck_participants_gems CHECK (gems_awarded >= 0);
