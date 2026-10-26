-- Accept the requested display name from the sign-up form.
-- The value is user supplied, validated here, and never allowed to override an existing profile.
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
    requested_username text;
    profile_username text;
begin
    select * into result from public.players where user_id = p_user_id;
    if found then
        return result;
    end if;

    select u.raw_user_meta_data ->> 'username'
      into requested_username
      from auth.users u
     where u.id = p_user_id;

    if requested_username !~ '^[A-Za-z0-9_]{3,20}$'
       or exists (
           select 1 from public.players p
            where lower(p.username) = lower(requested_username)
       ) then
        requested_username := null;
    end if;

    loop
        attempts := attempts + 1;
        pid := public.generate_player_id();
        profile_username := coalesce(requested_username, 'Operator_' || pid);
        begin
            insert into public.players (user_id, player_id, username)
            values (p_user_id, pid, profile_username)
            returning * into result;
            return result;
        exception
            when unique_violation then
                select * into result from public.players where user_id = p_user_id;
                if found then
                    return result;
                end if;
                requested_username := null;
                if attempts >= 10 then
                    raise exception 'could not allocate a player id';
                end if;
        end;
    end loop;
end;
$$;

revoke all on function public.create_player_for(uuid) from public, anon, authenticated;
