CREATE TABLE IF NOT EXISTS feedback (
  id TEXT PRIMARY KEY,
  public_id TEXT NOT NULL UNIQUE,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  category TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'open',
  priority TEXT NOT NULL DEFAULT 'normal',
  title TEXT NOT NULL,
  description TEXT NOT NULL,
  contact TEXT NOT NULL DEFAULT '',
  source TEXT NOT NULL DEFAULT 'web',
  app_version TEXT NOT NULL DEFAULT '',
  core_version TEXT NOT NULL DEFAULT '',
  platform TEXT NOT NULL DEFAULT '',
  architecture TEXT NOT NULL DEFAULT '',
  os_version TEXT NOT NULL DEFAULT '',
  selected_player TEXT NOT NULL DEFAULT '',
  player_version TEXT NOT NULL DEFAULT '',
  connector_id TEXT NOT NULL DEFAULT '',
  connector_version TEXT NOT NULL DEFAULT '',
  latest_connector_version TEXT NOT NULL DEFAULT '',
  connection_status TEXT NOT NULL DEFAULT '',
  diagnostics_json TEXT NOT NULL DEFAULT '{}',
  country TEXT NOT NULL DEFAULT '',
  ip_hash TEXT NOT NULL,
  user_agent TEXT NOT NULL DEFAULT '',
  admin_note TEXT NOT NULL DEFAULT '',
  public_reply TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_feedback_created_at
  ON feedback(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_feedback_status_created
  ON feedback(status, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_feedback_player_created
  ON feedback(selected_player, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_feedback_ip_created
  ON feedback(ip_hash, created_at DESC);
