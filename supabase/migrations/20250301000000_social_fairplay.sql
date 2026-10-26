-- Clans, clan wars, companions, map events, reputation / fair play.
-- Same model as the economy migration: clients can only READ rows they are entitled to; every mutation goes through
-- SECURITY DEFINER functions executable by service_role only (trusted backend / dedicated server, never Unity).
-- Validated against PGlite only; not yet run on a real Supabase project. The Node dev backend in /server implements the
-- same rules on SQLite and is what the automated gameplay tests exercise.

-- ---------- moderators (granted by the operator with SQL; there is no client path) ----------
create table if not exists public.moderators (
  user_id uuid primary key references auth.users(id) on delete cascade,
  role text not null check (role in ('moderator', 'admin')),
  created_at timestamptz not null default now()
);

create or replace function public.is_moderator() returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from public.moderators where user_id = auth.uid())
$$;

-- ---------- clans ----------
create table if not exists public.clans (
  id bigint generated always as identity primary key,
  name text not null check (char_length(name) between 3 and 24),
  name_key text not null unique,
  tag text not null check (tag ~ '^[A-Za-z0-9]{2,5}$'),
  emblem_id integer not null default 1,
  description text not null default '' check (char_length(description) <= 200),
  join_policy text not null default 'request' check (join_policy in ('open', 'request', 'invite')),
  member_limit integer not null default 20 check (member_limit between 2 and 100),
  xp bigint not null default 0 check (xp >= 0),
  rating integer not null default 1000,
  wins integer not null default 0 check (wins >= 0),
  losses integer not null default 0 check (losses >= 0),
  draws integer not null default 0 check (draws >= 0),
  created_by uuid references auth.users(id) on delete set null,
  created_at timestamptz not null default now()
);
create unique index if not exists clans_tag_key on public.clans (upper(tag));

create table if not exists public.clan_members (
  clan_id bigint not null references public.clans(id) on delete cascade,
  user_id uuid not null unique references auth.users(id) on delete cascade, -- one clan per player
  role text not null check (role in ('member', 'officer', 'co_leader', 'leader')),
  contribution_xp bigint not null default 0 check (contribution_xp >= 0),
  joined_at timestamptz not null default now(),
  primary key (clan_id, user_id)
);
create unique index if not exists clan_one_leader on public.clan_members (clan_id) where role = 'leader';

create table if not exists public.clan_invitations (
  id bigint generated always as identity primary key,
  clan_id bigint not null references public.clans(id) on delete cascade,
  from_user uuid not null references auth.users(id) on delete cascade,
  to_user uuid not null references auth.users(id) on delete cascade,
  status text not null default 'pending' check (status in ('pending', 'accepted', 'rejected', 'cancelled', 'expired')),
  expires_at timestamptz not null,
  created_at timestamptz not null default now()
);
create unique index if not exists clan_invitation_pending on public.clan_invitations (clan_id, to_user) where status = 'pending';

create table if not exists public.clan_applications (
  id bigint generated always as identity primary key,
  clan_id bigint not null references public.clans(id) on delete cascade,
  user_id uuid not null references auth.users(id) on delete cascade,
  message text not null default '' check (char_length(message) <= 140),
  status text not null default 'pending' check (status in ('pending', 'accepted', 'rejected', 'cancelled')),
  created_at timestamptz not null default now()
);
create unique index if not exists clan_application_pending on public.clan_applications (clan_id, user_id) where status = 'pending';

-- ---------- clan wars (history rows deliberately do not cascade from clans) ----------
create table if not exists public.clan_matches (
  id text primary key,
  clan_a bigint not null,
  clan_b bigint not null,
  clan_a_name text not null,
  clan_b_name text not null,
  team_size integer not null check (team_size between 1 and 8),
  round_limit integer not null check (round_limit between 1 and 15),
  status text not null default 'pending' check (status in ('pending', 'live', 'completed', 'void', 'disputed')),
  server_id text,
  created_at timestamptz not null default now(),
  finished_at timestamptz,
  check (clan_a <> clan_b)
);

create table if not exists public.clan_match_results (
  match_id text primary key references public.clan_matches(id) on delete cascade, -- one result per match
  payload_hash text not null,
  winner_clan bigint,
  rounds_a integer not null default 0,
  rounds_b integer not null default 0,
  rated boolean not null default true,
  xp_a integer not null default 0 check (xp_a >= 0),
  xp_b integer not null default 0 check (xp_b >= 0),
  reverted boolean not null default false,
  reported_at timestamptz not null default now()
);

-- ---------- companions ----------
create table if not exists public.companion_catalog (
  id text primary key,
  name text not null,
  kind text not null,
  rarity text not null,
  unlock_kind text not null check (unlock_kind in ('default', 'coins', 'diamonds', 'level', 'reward')),
  price_coins integer not null default 0 check (price_coins >= 0),
  price_diamonds integer not null default 0 check (price_diamonds >= 0),
  min_level integer not null default 1,
  active boolean not null default true
);
create table if not exists public.companion_variants (
  companion_id text not null references public.companion_catalog(id) on delete cascade,
  variant_id text not null,
  name text not null,
  price_coins integer not null default 0 check (price_coins >= 0),
  price_diamonds integer not null default 0 check (price_diamonds >= 0),
  primary key (companion_id, variant_id)
);
create table if not exists public.player_companions (
  user_id uuid not null references auth.users(id) on delete cascade,
  companion_id text not null,
  variant_id text not null default 'default',
  xp integer not null default 0 check (xp >= 0),
  acquired_at timestamptz not null default now(),
  primary key (user_id, companion_id, variant_id),
  foreign key (companion_id, variant_id) references public.companion_variants(companion_id, variant_id)
);
create table if not exists public.player_companion_equipped (
  user_id uuid primary key references auth.users(id) on delete cascade,
  companion_id text not null,
  variant_id text not null,
  foreign key (user_id, companion_id, variant_id) references public.player_companions(user_id, companion_id, variant_id) on delete cascade
);

-- ---------- map events ----------
create table if not exists public.map_events (
  id text primary key,
  match_id text not null,
  slot text not null,
  type text not null check (type in ('supply_drop', 'objective', 'loot_zone', 'environmental')),
  x real not null, y real not null, z real not null, radius real not null check (radius > 0),
  announce_at timestamptz not null,
  start_at timestamptz not null,
  ends_at timestamptz not null,
  reward_coins integer not null default 0 check (reward_coins between 0 and 500),
  capacity integer not null default 0,
  objective_target integer not null default 0,
  status text not null default 'planned' check (status in ('planned', 'completed', 'cancelled')),
  winner_team integer,
  unique (match_id, slot),
  check (announce_at <= start_at and start_at < ends_at)
);
create table if not exists public.map_event_progress (
  event_id text not null references public.map_events(id) on delete cascade,
  team integer not null,
  progress integer not null default 0 check (progress >= 0),
  primary key (event_id, team)
);
create table if not exists public.map_event_claims (
  event_id text not null references public.map_events(id) on delete cascade,
  user_id uuid not null references auth.users(id) on delete cascade,
  claim_no integer not null default 1,
  coins integer not null default 0,
  created_at timestamptz not null default now(),
  primary key (event_id, user_id, claim_no) -- a player cannot collect the same event twice
);

-- ---------- fair play ----------
create table if not exists public.player_reports (
  id bigint generated always as identity primary key,
  reporter_id uuid not null references auth.users(id) on delete cascade,
  target_id uuid not null references auth.users(id) on delete cascade,
  category text not null check (category in ('cheating', 'aimbot', 'wallhack', 'speedhack', 'abusive_chat', 'harassment', 'offensive_name', 'afk', 'disruptive', 'other')),
  reason text not null check (char_length(reason) between 3 and 300),
  evidence text check (char_length(evidence) <= 500),
  match_id text,
  status text not null default 'open' check (status in ('open', 'dismissed', 'confirmed', 'expired')),
  reviewed_by uuid references auth.users(id) on delete set null,
  created_at timestamptz not null default now(),
  resolved_at timestamptz,
  check (reporter_id <> target_id)
);
create index if not exists player_reports_status_idx on public.player_reports (status, created_at);

create table if not exists public.player_reputation (
  user_id uuid primary key references auth.users(id) on delete cascade,
  score integer not null default 100 check (score between 0 and 100),
  updated_at timestamptz not null default now()
);
create table if not exists public.reputation_events (
  id bigint generated always as identity primary key,
  user_id uuid not null references auth.users(id) on delete cascade,
  kind text not null,
  delta integer not null,
  key text not null unique, -- idempotency: the same event can never change the score twice
  created_at timestamptz not null default now()
);

create table if not exists public.moderation_actions (
  id bigint generated always as identity primary key,
  target_id uuid not null references auth.users(id) on delete cascade,
  moderator_id uuid references auth.users(id) on delete set null,
  report_id bigint references public.player_reports(id) on delete set null,
  action text not null check (action in ('dismiss', 'note', 'warn', 'mute', 'suspend')),
  public_reason text,
  internal_note text,
  expires_at timestamptz,
  revoked_at timestamptz,
  created_at timestamptz not null default now()
);

create table if not exists public.appeals (
  id bigint generated always as identity primary key,
  action_id bigint not null unique references public.moderation_actions(id) on delete cascade, -- one appeal per action
  user_id uuid not null references auth.users(id) on delete cascade,
  message text not null check (char_length(message) between 10 and 500),
  status text not null default 'open' check (status in ('open', 'upheld', 'overturned')),
  created_at timestamptz not null default now()
);

create table if not exists public.audit_log (
  id bigint generated always as identity primary key,
  actor_id uuid,
  actor_kind text not null,
  action text not null,
  target text,
  detail text,
  created_at timestamptz not null default now()
);
create or replace function public.audit_log_immutable() returns trigger language plpgsql as $$
begin raise exception 'audit_log is append-only'; end $$;
drop trigger if exists audit_log_no_update on public.audit_log;
create trigger audit_log_no_update before update or delete on public.audit_log
  for each row execute function public.audit_log_immutable();

-- ---------- row level security ----------
alter table public.moderators enable row level security;
alter table public.clans enable row level security;
alter table public.clan_members enable row level security;
alter table public.clan_invitations enable row level security;
alter table public.clan_applications enable row level security;
alter table public.clan_matches enable row level security;
alter table public.clan_match_results enable row level security;
alter table public.companion_catalog enable row level security;
alter table public.companion_variants enable row level security;
alter table public.player_companions enable row level security;
alter table public.player_companion_equipped enable row level security;
alter table public.map_events enable row level security;
alter table public.map_event_progress enable row level security;
alter table public.map_event_claims enable row level security;
alter table public.player_reports enable row level security;
alter table public.player_reputation enable row level security;
alter table public.reputation_events enable row level security;
alter table public.moderation_actions enable row level security;
alter table public.appeals enable row level security;
alter table public.audit_log enable row level security;

create or replace function public.my_clan_id() returns bigint
language sql stable security definer set search_path = public as $$
  select clan_id from public.clan_members where user_id = auth.uid()
$$;

-- Clans are publicly browsable; membership lists, invitations and applications only to the people involved.
create policy clans_read on public.clans for select to authenticated using (true);
create policy clan_members_read on public.clan_members for select to authenticated using (clan_id = public.my_clan_id());
create policy clan_invites_read on public.clan_invitations for select to authenticated using (to_user = auth.uid() or clan_id = public.my_clan_id());
create policy clan_apps_read on public.clan_applications for select to authenticated using (user_id = auth.uid() or clan_id = public.my_clan_id());
create policy clan_matches_read on public.clan_matches for select to authenticated using (clan_a = public.my_clan_id() or clan_b = public.my_clan_id());
create policy clan_results_read on public.clan_match_results for select to authenticated
  using (exists (select 1 from public.clan_matches m where m.id = match_id and (m.clan_a = public.my_clan_id() or m.clan_b = public.my_clan_id())));
create policy companions_catalog_read on public.companion_catalog for select to authenticated using (active);
create policy companion_variants_read on public.companion_variants for select to authenticated using (true);
create policy own_companions_read on public.player_companions for select to authenticated using (user_id = auth.uid());
create policy own_equipped_read on public.player_companion_equipped for select to authenticated using (user_id = auth.uid());
create policy own_claims_read on public.map_event_claims for select to authenticated using (user_id = auth.uid());
-- Reports are private: reporters see only their own; nobody sees reports filed about them.
create policy own_reports_read on public.player_reports for select to authenticated using (reporter_id = auth.uid());
create policy own_reputation_read on public.player_reputation for select to authenticated using (user_id = auth.uid());
-- Players see only the public part of actions against them (internal notes are filtered by the API / view below).
create policy own_actions_read on public.moderation_actions for select to authenticated using (target_id = auth.uid() and action <> 'note' and action <> 'dismiss');
create policy own_appeals_read on public.appeals for select to authenticated using (user_id = auth.uid());
-- audit_log, moderators, reputation_events, map events and progress have no client policy: service_role only.

revoke all on all tables in schema public from anon, authenticated;
grant select on public.clans, public.clan_members, public.clan_invitations, public.clan_applications, public.clan_matches,
  public.clan_match_results, public.companion_catalog, public.companion_variants, public.player_companions,
  public.player_companion_equipped, public.map_event_claims, public.player_reports, public.player_reputation,
  public.appeals to authenticated;
grant select (id, target_id, action, public_reason, expires_at, revoked_at, created_at) on public.moderation_actions to authenticated;

-- ---------- trusted functions (service_role only) ----------
create or replace function public.create_clan(p_user uuid, p_name text, p_tag text) returns bigint
language plpgsql security definer set search_path = public as $$
declare v_id bigint;
begin
  if exists (select 1 from public.clan_members where user_id = p_user) then raise exception 'already_in_clan'; end if;
  insert into public.clans(name, name_key, tag, created_by) values (p_name, lower(regexp_replace(trim(p_name), '\s+', ' ', 'g')), p_tag, p_user) returning id into v_id;
  insert into public.clan_members(clan_id, user_id, role) values (v_id, p_user, 'leader');
  insert into public.audit_log(actor_id, actor_kind, action, target) values (p_user, 'player', 'clan.create', v_id::text);
  return v_id;
end $$;

-- Applies a dedicated-server-verified result exactly once. A replay with the same payload is a no-op;
-- a different payload for a finished match raises instead of silently overwriting.
create or replace function public.record_clan_match_result(p_match text, p_hash text, p_winner bigint, p_rounds_a int, p_rounds_b int, p_rated boolean, p_xp_a int, p_xp_b int)
returns boolean
language plpgsql security definer set search_path = public as $$
declare m public.clan_matches; existing public.clan_match_results;
begin
  select * into m from public.clan_matches where id = p_match for update;
  if not found then raise exception 'match_not_found'; end if;
  select * into existing from public.clan_match_results where match_id = p_match;
  if found then
    if existing.payload_hash <> p_hash then raise exception 'conflicting_result'; end if;
    return false;
  end if;
  if m.status <> 'live' then raise exception 'match_not_live'; end if;
  insert into public.clan_match_results(match_id, payload_hash, winner_clan, rounds_a, rounds_b, rated, xp_a, xp_b)
    values (p_match, p_hash, p_winner, p_rounds_a, p_rounds_b, p_rated, p_xp_a, p_xp_b);
  update public.clan_matches set status = 'completed', finished_at = now() where id = p_match;
  update public.clans set xp = xp + p_xp_a, wins = wins + (p_winner = m.clan_a)::int, losses = losses + (p_winner = m.clan_b)::int where id = m.clan_a;
  update public.clans set xp = xp + p_xp_b, wins = wins + (p_winner = m.clan_b)::int, losses = losses + (p_winner = m.clan_a)::int where id = m.clan_b;
  return true;
end $$;

-- Idempotent reputation change.
create or replace function public.reputation_apply(p_user uuid, p_kind text, p_delta int, p_key text) returns int
language plpgsql security definer set search_path = public as $$
declare v_score int;
begin
  insert into public.player_reputation(user_id) values (p_user) on conflict do nothing;
  begin
    insert into public.reputation_events(user_id, kind, delta, key) values (p_user, p_kind, p_delta, p_key);
  exception when unique_violation then
    select score into v_score from public.player_reputation where user_id = p_user;
    return v_score;
  end;
  update public.player_reputation set score = greatest(0, least(100, score + p_delta)), updated_at = now() where user_id = p_user returning score into v_score;
  return v_score;
end $$;

-- Report intake: validated and throttled; it only queues a review and never punishes anyone.
create or replace function public.submit_player_report(p_reporter uuid, p_target uuid, p_category text, p_reason text, p_evidence text, p_match text, p_daily_limit int default 15)
returns bigint
language plpgsql security definer set search_path = public as $$
declare v_id bigint;
begin
  if p_reporter = p_target then raise exception 'cannot_report_self'; end if;
  if (select count(*) from public.player_reports where reporter_id = p_reporter and created_at > now() - interval '1 day') >= p_daily_limit then
    raise exception 'report_limit';
  end if;
  insert into public.player_reports(reporter_id, target_id, category, reason, evidence, match_id) values (p_reporter, p_target, p_category, p_reason, p_evidence, p_match) returning id into v_id;
  return v_id;
end $$;

-- Only service_role may call the mutating functions. is_moderator / my_clan_id are read helpers used by policies.
revoke all on function public.create_clan(uuid, text, text), public.record_clan_match_result(text, text, bigint, int, int, boolean, int, int),
  public.reputation_apply(uuid, text, int, text), public.submit_player_report(uuid, uuid, text, text, text, text, int) from public, anon, authenticated;
grant execute on function public.create_clan(uuid, text, text), public.record_clan_match_result(text, text, bigint, int, int, boolean, int, int),
  public.reputation_apply(uuid, text, int, text), public.submit_player_report(uuid, uuid, text, text, text, text, int) to service_role;
grant execute on function public.my_clan_id(), public.is_moderator() to authenticated;