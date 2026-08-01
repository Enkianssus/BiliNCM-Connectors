CREATE TABLE IF NOT EXISTS compatibility_reports (
  id TEXT PRIMARY KEY,
  fingerprint TEXT NOT NULL UNIQUE,
  first_seen_at TEXT NOT NULL,
  last_seen_at TEXT NOT NULL,
  reports_count INTEGER NOT NULL DEFAULT 1,
  player TEXT NOT NULL,
  player_version TEXT NOT NULL,
  connector_version TEXT NOT NULL DEFAULT '',
  architecture TEXT NOT NULL DEFAULT '',
  client_sha256 TEXT NOT NULL,
  common_sha256 TEXT NOT NULL,
  known_profile_matched INTEGER NOT NULL DEFAULT 0,
  execution_allowed INTEGER NOT NULL DEFAULT 0,
  summary TEXT NOT NULL DEFAULT '',
  diagnostics_json TEXT NOT NULL DEFAULT '{}',
  country TEXT NOT NULL DEFAULT '',
  ip_hash TEXT NOT NULL,
  user_agent TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_compatibility_last_seen
  ON compatibility_reports(last_seen_at DESC);
CREATE INDEX IF NOT EXISTS idx_compatibility_player_version
  ON compatibility_reports(player, player_version, last_seen_at DESC);
CREATE INDEX IF NOT EXISTS idx_compatibility_ip_last_seen
  ON compatibility_reports(ip_hash, last_seen_at DESC);
