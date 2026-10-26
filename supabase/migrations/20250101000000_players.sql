-- Noxy: persistent player profiles with a permanent public 10-digit Player ID.
-- Internal identity  = auth.users.id (uuid, never shown to other players).
-- Public identity    = players.player_id (10 digits, NOT a credential).
-- Protected values (level/xp/coins/gems) are never writable by clients.

create table if not exists public.players (
    user_id     uuid primary key references auth.users (id) on delete cascade,
    player_id   text not null,
    username    text not null,
    avatar_url  text,
    level       integer not null default 1,
    xp          bigint  not null default 0,
    coins       bigint  not null default 0,
    gems        bigint  not null default 0,
    created_at  timestamptz not null default now(),
    updated_at  timestamptz not null default now(),
    constraint players_player_id_format check (player_id ~ '^[1-9][0-9]{9}$'),
    constraint players_player_id_key    unique (player_id),
    constraint players_username_format  check (username ~ '^[A-Za-z0-9_]{3,20}$'),
    constraint players_avatar_len       check (avatar_url is null or char_length(avatar_url) <= 512),
    constraint players_level_min        check (level >= 1),
    constraint players_nonneg           check (xp >= 0 and coins >= 0 and gems >= 0)
);

create unique index if not exists players_username_lower_key on public.players (lower(username));

-- ---------------------------------------------------------------------------------------------
-- Player ID generation (backend only). gen_random_uuid() is backed by a CSPRNG.
-- ---------------------------------------------------------------------------------------------
create or replace function public.generate_player_id()
returns text
language plpgsql
volatile
set search_path = ''
as $$
declare
    candidate text;
    n bigint;
begin
    loop
        n := ('x' || substr(replace(gen_random_uuid()::text, '-', ''), 1, 15))::bit(60)::bigint;
        candidate := (1000000000 + (n % 9000000000))::text;
        exit when not exists (select 1 from public.players p where p.player_id = candidate);
    end loop;
    return candidate;
end;
$$;

-- The unique constraint is the final arbiter; this retries if two sign-ups race to the same ID.
create or replace function public.create_player_for(p_user_id uuid)
returns public.players
language plpgsql
security definer
set search_path = ''
as $$
declare
    result public.players;
    attempts int := 0;
    pid text;
begin
    select * into result from public.players where user_id = p_user_id;
    if found then
        return result;
    end if;

    loop
        attempts := attempts + 1;
        pid := public.generate_player_id();
        begin
            insert into public.players (user_id, player_id, username)
            values (p_user_id, pid, 'Operator_' || pid)
            returning * into result;
            return result;
        exception
            when unique_violation then
                -- Either a concurrent insert for this user (return it) or an ID collision (retry).
                select * into result from public.players where user_id = p_user_id;
                if found then
                    return result;
                end if;
                if attempts >= 10 then
                    raise exception 'could not allocate a player id';
                end if;
        end;
    end loop;
end;
$$;

-- New account (any provider, email or anonymous guest) -> exactly one profile, created once.
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    perform public.create_player_for(new.id);
    return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- ---------------------------------------------------------------------------------------------
-- updated_at
-- ---------------------------------------------------------------------------------------------
create or replace function public.touch_updated_at()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    new.updated_at := now();
    return new;
end;
$$;

drop trigger if exists players_touch on public.players;
create trigger players_touch before update on public.players
    for each row execute function public.touch_updated_at();

-- ---------------------------------------------------------------------------------------------
-- Rate limiting (fixed window). Internal: clients can neither read nor call it.
-- ---------------------------------------------------------------------------------------------
create table if not exists public.rate_limits (
    bucket       text not null,
    user_id      uuid not null,
    window_start timestamptz not null,
    hits         integer not null default 0,
    primary key (bucket, user_id, window_start)
);
alter table public.rate_limits enable row level security;   -- no policies: no client access

create or replace function public.check_rate_limit(p_bucket text, p_user uuid, p_max integer, p_window_seconds integer)
returns boolean
language plpgsql
security definer
set search_path = ''
as $$
declare
    w timestamptz := to_timestamp(floor(extract(epoch from now()) / p_window_seconds) * p_window_seconds);
    h integer;
begin
    delete from public.rate_limits where window_start < now() - interval '1 day';
    insert into public.rate_limits as r (bucket, user_id, window_start, hits)
    values (p_bucket, p_user, w, 1)
    on conflict (bucket, user_id, window_start) do update set hits = r.hits + 1
    returning r.hits into h;
    return h <= p_max;
end;
$$;

-- ---------------------------------------------------------------------------------------------
-- Row Level Security + column-level grants
-- ---------------------------------------------------------------------------------------------
alter table public.players enable row level security;
alter table public.players force row level security;

drop policy if exists players_select_own on public.players;
create policy players_select_own on public.players
    for select to authenticated
    using (user_id = (select auth.uid()));

drop policy if exists players_update_own on public.players;
create policy players_update_own on public.players
    for update to authenticated
    using (user_id = (select auth.uid()))
    with check (user_id = (select auth.uid()));

-- No insert/delete policy: profiles are created only by the trigger / ensure_player().
revoke all on public.players from anon, authenticated;
grant select on public.players to authenticated;
grant update (username, avatar_url) on public.players to authenticated;

revoke all on public.rate_limits from anon, authenticated;

-- ---------------------------------------------------------------------------------------------
-- RPCs
-- ---------------------------------------------------------------------------------------------

-- Idempotent: returns the caller's profile, creating it only if it does not exist yet.
create or replace function public.ensure_player()
returns public.players
language plpgsql
security definer
set search_path = ''
as $$
declare
    uid uuid := auth.uid();
begin
    if uid is null then
        raise exception 'not authenticated' using errcode = '28000';
    end if;
    if not public.check_rate_limit('ensure_player', uid, 30, 60) then
        raise exception 'rate limited' using errcode = '54000';
    end if;
    return public.create_player_for(uid);
end;
$$;

-- Public, minimal view of another player. Returns only non-sensitive fields; rate limited.
create or replace function public.find_player(p_player_id text)
returns table (player_id text, username text, avatar_url text, level integer)
language plpgsql
security definer
set search_path = ''
as $$
declare
    uid uuid := auth.uid();
begin
    if uid is null then
        raise exception 'not authenticated' using errcode = '28000';
    end if;
    if p_player_id is null or p_player_id !~ '^[1-9][0-9]{9}$' then
        return;
    end if;
    if not public.check_rate_limit('find_player', uid, 20, 60) then
        raise exception 'rate limited' using errcode = '54000';
    end if;
    return query
        select p.player_id, p.username, p.avatar_url, p.level
        from public.players p
        where p.player_id = p_player_id;
end;
$$;

-- Server-side only: used by trusted game servers / edge functions with the service role.
create or replace function public.grant_progress(p_user uuid, p_xp bigint, p_coins bigint, p_gems bigint)
returns public.players
language plpgsql
security definer
set search_path = ''
as $$
declare
    result public.players;
begin
    update public.players
       set xp    = xp + greatest(p_xp, 0),
           coins = coins + greatest(p_coins, 0),
           gems  = gems + greatest(p_gems, 0),
           level = 1 + ((xp + greatest(p_xp, 0)) / 1000)::int
     where user_id = p_user
     returning * into result;
    return result;
end;
$$;

revoke all on function public.generate_player_id()  from public, anon, authenticated;
revoke all on function public.create_player_for(uuid) from public, anon, authenticated;
revoke all on function public.handle_new_user()      from public, anon, authenticated;
revoke all on function public.check_rate_limit(text, uuid, integer, integer) from public, anon, authenticated;
revoke all on function public.grant_progress(uuid, bigint, bigint, bigint)   from public, anon, authenticated;
revoke all on function public.ensure_player() from public, anon;
revoke all on function public.find_player(text) from public, anon;
grant execute on function public.ensure_player()    to authenticated;
grant execute on function public.find_player(text)  to authenticated;
grant execute on function public.grant_progress(uuid, bigint, bigint, bigint) to service_role;
