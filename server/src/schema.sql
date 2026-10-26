-- Noxy dev backend schema (SQLite dialect). Portable to PostgreSQL with minor type changes
-- (INTEGER PRIMARY KEY -> BIGSERIAL, TEXT timestamps -> TIMESTAMPTZ, COLLATE NOCASE -> citext/lower() index).
-- Migrations are additive and tracked in schema_migrations; never drop data here.

CREATE TABLE IF NOT EXISTS players (
  id             INTEGER PRIMARY KEY AUTOINCREMENT,
  public_id      TEXT NOT NULL UNIQUE CHECK (length(public_id) = 10 AND public_id NOT GLOB '*[^0-9]*' AND substr(public_id,1,1) <> '0'),
  username       TEXT NOT NULL UNIQUE COLLATE NOCASE,
  password_salt  TEXT NOT NULL,
  password_hash  TEXT NOT NULL,
  level          INTEGER NOT NULL DEFAULT 1,
  xp             INTEGER NOT NULL DEFAULT 0,
  credits        INTEGER NOT NULL DEFAULT 0,
  cores          INTEGER NOT NULL DEFAULT 0,
  rank_name      TEXT NOT NULL DEFAULT 'Unranked',
  created_at     INTEGER NOT NULL,
  username_changed_at INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS sessions (
  token_hash  TEXT PRIMARY KEY,
  player_id   INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  created_at  INTEGER NOT NULL,
  expires_at  INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_sessions_player ON sessions(player_id);

CREATE TABLE IF NOT EXISTS player_statistics (
  player_id    INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  matches      INTEGER NOT NULL DEFAULT 0,
  wins         INTEGER NOT NULL DEFAULT 0,
  top3         INTEGER NOT NULL DEFAULT 0,
  kills        INTEGER NOT NULL DEFAULT 0,
  deaths       INTEGER NOT NULL DEFAULT 0,
  headshots    INTEGER NOT NULL DEFAULT 0,
  seconds_played INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS player_inventory (
  player_id   INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  item_id     TEXT NOT NULL,
  acquired_at INTEGER NOT NULL,
  PRIMARY KEY (player_id, item_id)
);

CREATE TABLE IF NOT EXISTS player_equipped_items (
  player_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  slot      TEXT NOT NULL,
  item_id   TEXT NOT NULL,
  PRIMARY KEY (player_id, slot)
);

CREATE TABLE IF NOT EXISTS player_settings (
  player_id        INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  friend_requests  TEXT NOT NULL DEFAULT 'everyone' CHECK (friend_requests IN ('everyone','nobody')),
  party_invites    TEXT NOT NULL DEFAULT 'friends'  CHECK (party_invites IN ('friends','nobody')),
  online_status    TEXT NOT NULL DEFAULT 'friends'  CHECK (online_status IN ('everyone','friends','nobody')),
  data_json        TEXT NOT NULL DEFAULT '{}',
  updated_at       INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS player_hud_layouts (
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  layout_key TEXT NOT NULL,
  data_json  TEXT NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (player_id, layout_key)
);

CREATE TABLE IF NOT EXISTS friend_requests (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  from_id    INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  to_id      INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  status     TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','accepted','declined','cancelled')),
  created_at INTEGER NOT NULL,
  CHECK (from_id <> to_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_friend_requests_pending ON friend_requests(from_id, to_id) WHERE status = 'pending';
CREATE INDEX IF NOT EXISTS idx_friend_requests_to ON friend_requests(to_id, status);

CREATE TABLE IF NOT EXISTS friendships (
  low_id     INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  high_id    INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (low_id, high_id),
  CHECK (low_id < high_id)
);
CREATE INDEX IF NOT EXISTS idx_friendships_high ON friendships(high_id);

CREATE TABLE IF NOT EXISTS player_blocks (
  blocker_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  blocked_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  created_at INTEGER NOT NULL,
  PRIMARY KEY (blocker_id, blocked_id),
  CHECK (blocker_id <> blocked_id)
);

CREATE TABLE IF NOT EXISTS parties (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  leader_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  mode_id    TEXT NOT NULL,
  created_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS party_members (
  party_id  INTEGER NOT NULL REFERENCES parties(id) ON DELETE CASCADE,
  player_id INTEGER NOT NULL UNIQUE REFERENCES players(id) ON DELETE CASCADE,
  ready     INTEGER NOT NULL DEFAULT 0,
  joined_at INTEGER NOT NULL,
  PRIMARY KEY (party_id, player_id)
);

CREATE TABLE IF NOT EXISTS party_invitations (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  party_id   INTEGER NOT NULL REFERENCES parties(id) ON DELETE CASCADE,
  from_id    INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  to_id      INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  mode_id    TEXT NOT NULL,
  status     TEXT NOT NULL DEFAULT 'pending' CHECK (status IN ('pending','accepted','declined','expired','cancelled')),
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_invites_to ON party_invitations(to_id, status);
CREATE INDEX IF NOT EXISTS idx_invites_pair ON party_invitations(from_id, to_id, created_at);

CREATE TABLE IF NOT EXISTS presence (
  player_id INTEGER PRIMARY KEY REFERENCES players(id) ON DELETE CASCADE,
  state     TEXT NOT NULL DEFAULT 'offline' CHECK (state IN ('lobby','in_match','offline')),
  last_seen INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS notifications (
  id         INTEGER PRIMARY KEY AUTOINCREMENT,
  player_id  INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  kind       TEXT NOT NULL,
  title      TEXT NOT NULL,
  body       TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  is_read    INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_notifications_player ON notifications(player_id, id);

CREATE TABLE IF NOT EXISTS reports (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  reporter_id INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  target_id   INTEGER NOT NULL REFERENCES players(id) ON DELETE CASCADE,
  reason      TEXT NOT NULL,
  created_at  INTEGER NOT NULL
);
