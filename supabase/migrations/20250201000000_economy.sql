-- Economy: wallet, ledger, Booyah rewards, payments. Clients can only READ their own rows.
-- All mutation goes through SECURITY DEFINER functions executable by service_role only
-- (called from trusted Edge Functions / the dedicated server, never from Unity).
-- Validated against PGlite only; not yet run on a real Supabase project.

create table if not exists public.economy_settings (
  key text primary key,
  value jsonb not null
);
insert into public.economy_settings(key, value) values
  ('booyah', '{"enabled": true, "diamonds": 500, "min_players": 10, "min_seconds": 120, "daily_cap": 3}')
on conflict (key) do nothing;

create table if not exists public.player_wallets (
  user_id uuid primary key references auth.users(id) on delete cascade,
  coins bigint not null default 0 check (coins >= 0),
  diamonds bigint not null default 0 check (diamonds >= 0),
  lifetime_diamonds_earned bigint not null default 0,
  lifetime_diamonds_purchased bigint not null default 0,
  lifetime_coins_earned bigint not null default 0,
  lifetime_coins_spent bigint not null default 0,
  updated_at timestamptz not null default now()
);

create table if not exists public.currency_transactions (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  currency text not null check (currency in ('coins', 'diamonds')),
  amount bigint not null check (amount <> 0),
  balance_after bigint not null check (balance_after >= 0),
  kind text not null,
  idempotency_key text not null unique,
  created_at timestamptz not null default now()
);
create index if not exists currency_transactions_user_idx on public.currency_transactions (user_id, created_at desc);

create table if not exists public.diamond_packages (
  id text primary key,
  diamonds integer not null check (diamonds > 0),
  product_id text not null unique,
  enabled boolean not null default true
);
insert into public.diamond_packages(id, diamonds, product_id) values
  ('d100', 100, 'noxy.diamonds.100'), ('d310', 310, 'noxy.diamonds.310'), ('d520', 520, 'noxy.diamonds.520'),
  ('d1060', 1060, 'noxy.diamonds.1060'), ('d2180', 2180, 'noxy.diamonds.2180'), ('d5600', 5600, 'noxy.diamonds.5600')
on conflict (id) do nothing;

create table if not exists public.payment_transactions (
  id uuid primary key default gen_random_uuid(),
  user_id uuid not null references auth.users(id) on delete cascade,
  package_id text not null references public.diamond_packages(id),
  provider text not null,
  provider_transaction_id text,
  status text not null default 'pending' check (status in ('pending', 'succeeded', 'cancelled', 'failed', 'refunded')),
  created_at timestamptz not null default now(),
  unique (provider, provider_transaction_id)
);

create table if not exists public.match_rewards (
  id uuid primary key default gen_random_uuid(),
  match_id text not null,
  user_id uuid not null references auth.users(id) on delete cascade,
  diamonds bigint not null default 0,
  coins bigint not null default 0,
  booyah boolean not null default false,
  created_at timestamptz not null default now(),
  unique (match_id, user_id)
);

create table if not exists public.cosmetic_catalog (
  id text primary key,
  coin_price bigint check (coin_price >= 0),
  diamond_price bigint check (diamond_price >= 0)
);
create table if not exists public.player_cosmetics (
  user_id uuid not null references auth.users(id) on delete cascade,
  item_id text not null references public.cosmetic_catalog(id),
  acquired_at timestamptz not null default now(),
  primary key (user_id, item_id)
);

-- RLS: read own rows only; no client write policies exist.
do $$
declare t text;
begin
  foreach t in array array['player_wallets','currency_transactions','payment_transactions','match_rewards','player_cosmetics'] loop
    execute format('alter table public.%I enable row level security', t);
    execute format('drop policy if exists %I on public.%I', t || '_read_own', t);
    execute format('create policy %I on public.%I for select to authenticated using (user_id = auth.uid())', t || '_read_own', t);
    execute format('revoke all on public.%I from anon, authenticated', t);
    execute format('grant select on public.%I to authenticated', t);
  end loop;
  foreach t in array array['economy_settings','diamond_packages','cosmetic_catalog'] loop
    execute format('alter table public.%I enable row level security', t);
    execute format('revoke all on public.%I from anon, authenticated', t);
  end loop;
end $$;
drop policy if exists packages_read on public.diamond_packages;
create policy packages_read on public.diamond_packages for select to authenticated using (enabled);
grant select on public.diamond_packages to authenticated;
drop policy if exists catalog_read on public.cosmetic_catalog;
create policy catalog_read on public.cosmetic_catalog for select to authenticated using (true);
grant select on public.cosmetic_catalog to authenticated;

-- The only balance mutator. Idempotent by key; the CHECK constraint blocks overdraw.
create or replace function public.wallet_apply(p_user uuid, p_currency text, p_amount bigint, p_kind text, p_key text)
returns bigint language plpgsql security definer set search_path = public as $$
declare v_balance bigint; v_existing bigint;
begin
  if p_currency not in ('coins', 'diamonds') then raise exception 'bad currency'; end if;
  select balance_after into v_existing from currency_transactions where idempotency_key = p_key;
  if found then return v_existing; end if;
  insert into player_wallets(user_id) values (p_user) on conflict do nothing;
  perform 1 from player_wallets where user_id = p_user for update;
  select balance_after into v_existing from currency_transactions where idempotency_key = p_key;
  if found then return v_existing; end if;
  if p_currency = 'coins' then
    update player_wallets set coins = coins + p_amount,
      lifetime_coins_earned = lifetime_coins_earned + greatest(p_amount, 0),
      lifetime_coins_spent = lifetime_coins_spent + greatest(-p_amount, 0), updated_at = now()
      where user_id = p_user returning coins into v_balance;
  else
    update player_wallets set diamonds = diamonds + p_amount,
      lifetime_diamonds_earned = lifetime_diamonds_earned + case when p_kind = 'purchase' then 0 else greatest(p_amount, 0) end,
      lifetime_diamonds_purchased = lifetime_diamonds_purchased + case when p_kind = 'purchase' then greatest(p_amount, 0) else 0 end,
      updated_at = now()
      where user_id = p_user returning diamonds into v_balance;
  end if;
  insert into currency_transactions(user_id, currency, amount, balance_after, kind, idempotency_key)
    values (p_user, p_currency, p_amount, v_balance, p_kind, p_key);
  return v_balance;
end $$;

-- Called only by the trusted dedicated server after it confirms the final winner.
create or replace function public.grant_booyah(p_match_id text, p_user uuid, p_players integer, p_seconds integer)
returns bigint language plpgsql security definer set search_path = public as $$
declare s jsonb; v_amount bigint; v_today integer;
begin
  select value into s from economy_settings where key = 'booyah';
  if s is null or not (s->>'enabled')::boolean then return 0; end if;
  if p_players < (s->>'min_players')::int or p_seconds < (s->>'min_seconds')::int then return 0; end if;
  select count(*) into v_today from match_rewards
    where user_id = p_user and booyah and created_at >= date_trunc('day', now());
  if v_today >= (s->>'daily_cap')::int then return 0; end if;
  v_amount := (s->>'diamonds')::bigint;
  insert into match_rewards(match_id, user_id, diamonds, booyah) values (p_match_id, p_user, v_amount, true)
    on conflict (match_id, user_id) do nothing;
  if not found then return 0; end if;
  perform wallet_apply(p_user, 'diamonds', v_amount, 'booyah', 'booyah:' || p_match_id || ':' || p_user);
  return v_amount;
end $$;

-- Single success path for a verified payment (receipt or signed webhook checked by the caller).
create or replace function public.settle_payment(p_payment uuid, p_provider_tx text)
returns text language plpgsql security definer set search_path = public as $$
declare p payment_transactions; d integer;
begin
  select * into p from payment_transactions where id = p_payment for update;
  if not found then raise exception 'unknown payment'; end if;
  if p.status = 'succeeded' then return 'succeeded'; end if;
  if p.status <> 'pending' then return p.status; end if;
  select diamonds into d from diamond_packages where id = p.package_id;
  update payment_transactions set status = 'succeeded', provider_transaction_id = p_provider_tx where id = p_payment;
  perform wallet_apply(p.user_id, 'diamonds', d, 'purchase', 'payment:' || p_payment);
  return 'succeeded';
end $$;

revoke all on function public.wallet_apply(uuid, text, bigint, text, text) from public, anon, authenticated;
revoke all on function public.grant_booyah(text, uuid, integer, integer) from public, anon, authenticated;
revoke all on function public.settle_payment(uuid, text) from public, anon, authenticated;
grant execute on function public.wallet_apply(uuid, text, bigint, text, text) to service_role;
grant execute on function public.grant_booyah(text, uuid, integer, integer) to service_role;
grant execute on function public.settle_payment(uuid, text) to service_role;
