-- Noxy: identity hardening found by the Supabase integration audit.
-- Non-destructive: no rows or columns are removed, and existing data is not rewritten.

-- 1. avatar_url was any string up to 512 characters, so a client could store javascript:/file:/data: URLs that other
--    players' clients might later render or open. Only https URLs are accepted for new writes. NOT VALID leaves any
--    existing rows alone (validate them yourself with: alter table public.players validate constraint players_avatar_https;).
do $$
begin
    if not exists (select 1 from pg_constraint where conname = 'players_avatar_https') then
        alter table public.players
            add constraint players_avatar_https
            check (avatar_url is null or avatar_url ~* '^https://[^[:space:]]+$') not valid;
    end if;
end
$$;

-- 2. players.coins / players.gems / players.xp / players.level are NOT the source of truth for gameplay. The game
--    backend's idempotent ledger (player_wallets + currency_transactions, see 20250201000000_economy.sql) owns
--    currency, and the game backend owns XP/level. These columns are kept only so existing deployments do not break.
comment on column public.players.coins is 'DEPRECATED legacy mirror, never authoritative. The wallet ledger in the game backend owns currency.';
comment on column public.players.gems  is 'DEPRECATED legacy mirror, never authoritative. The wallet ledger in the game backend owns premium currency.';
comment on column public.players.xp    is 'Informational. The game backend owns XP and level; clients can never write this column.';
comment on column public.players.level is 'Informational. The game backend owns XP and level; clients can never write this column.';

-- 3. grant_progress() added currency with no ledger entry, no idempotency key and no audit trail, which bypasses the
--    duplicate-credit protection the economy relies on. Nothing in the project calls it, so it is no longer executable
--    by any API role. (The function itself is kept; a database owner can still call it for manual repairs.)
revoke execute on function public.grant_progress(uuid, bigint, bigint, bigint) from service_role;
