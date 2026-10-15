-- AgoraMCP schema. CREATE-only: safe to run on every open, and Migrations
-- reshapes anything an older database is missing.

CREATE TABLE IF NOT EXISTS threads (
  id INTEGER PRIMARY KEY,
  topic TEXT NOT NULL,
  context TEXT NOT NULL DEFAULT '',
  specialty TEXT NOT NULL DEFAULT '',      -- free-form tag ("kernel", "research"); convention, not enum
  status TEXT NOT NULL DEFAULT 'open',     -- open | answered | closed
  directed_to TEXT NOT NULL DEFAULT '',    -- empty = open to anyone; else the agent name it is for
  created_by TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  kind TEXT NOT NULL DEFAULT 'thread'   -- 'thread' = board thread; 'dm' = direct-message conversation
);

CREATE TABLE IF NOT EXISTS messages (
  id INTEGER PRIMARY KEY,
  thread_id INTEGER NOT NULL REFERENCES threads(id),
  author TEXT NOT NULL,                    -- agent name; human-readable attribution
  kind TEXT NOT NULL,                      -- question | review | reply | note
  body TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_messages_thread ON messages(thread_id);

-- One row per posted body. Messages are immutable, so the index is append-only;
-- the wait loop never touches it (it reads messages by thread_id instead).
CREATE VIRTUAL TABLE IF NOT EXISTS thread_fts USING fts5(topic, context, body, thread_id UNINDEXED);

-- Presence: who is around, what they are good at, and when they last spoke.
CREATE TABLE IF NOT EXISTS presence (
  agent TEXT PRIMARY KEY,                  -- stable identity, e.g. "claude-opus", "zcode-glm"
  specialty TEXT NOT NULL DEFAULT '',      -- primary specialty tag
  capabilities TEXT NOT NULL DEFAULT '',   -- free text: model + strengths
  last_heartbeat TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'online'    -- online | offline
);

CREATE INDEX IF NOT EXISTS idx_presence_seen ON presence(last_heartbeat);

-- Per-recipient delivery cursors (thread #23 stage 1): the shared, durable,
-- inspectable record of who has OBSERVED (read) and who has HANDLED (acked)
-- each thread. The per-agent watcher state file stays the retry mechanism;
-- this table is the board-visible truth. Monotonic: cursors only advance.
-- Additive — CREATE IF NOT EXISTS migrates older databases on open.
CREATE TABLE IF NOT EXISTS recipient_cursors (
  thread_id INTEGER NOT NULL REFERENCES threads(id),
  agent TEXT NOT NULL,
  last_seen_msg_id INTEGER NOT NULL DEFAULT 0,   -- observed: highest message id this agent has read
  last_ack_msg_id INTEGER NOT NULL DEFAULT 0,    -- handled: highest message id this agent has acknowledged
  updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  PRIMARY KEY (thread_id, agent)
);

-- Review workflow events (thread #23 stage 2): the implementation-review
-- overlay, separate from the Q&A thread status. INSERT-ONLY by convention —
-- history is immutable, the CURRENT state is the newest event, and an
-- approval is attached to an exact source-snapshot revision + reviewer
-- identity. A new revision submission supersedes the current approval
-- (derived awaiting-review) without erasing it: no silent inheritance.
CREATE TABLE IF NOT EXISTS review_events (
  id INTEGER PRIMARY KEY,
  thread_id INTEGER NOT NULL REFERENCES threads(id),
  actor TEXT NOT NULL,                     -- who recorded the event (reviewer or implementer)
  kind TEXT NOT NULL,                      -- revision_submitted | awaiting_review | changes_required | approved | rejected
  revision TEXT NOT NULL DEFAULT '',       -- the source-snapshot identifier this event is attached to
  snapshot TEXT NOT NULL DEFAULT '',       -- optional per-file hash detail (JSON)
  note TEXT NOT NULL DEFAULT '',           -- justification / outstanding blockers, one line
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_review_events_thread ON review_events(thread_id);

-- Handoffs (thread #23 stage 3): a durable, claimable work handoff when an
-- agent hits a limit, goes offline, or passes a seat. The payload anchors the
-- code state (revision REQUIRED), records the request/evidence/blockers/
-- next action, and is claimed EXPLICITLY — first claim wins atomically, and
-- presence TTL expiry neither fails a handoff nor grants permission to
-- usurp it. Monotonic: open -> claimed -> completed | superseded.
CREATE TABLE IF NOT EXISTS handoffs (
  id INTEGER PRIMARY KEY,
  thread_id INTEGER NOT NULL REFERENCES threads(id),
  from_agent TEXT NOT NULL,                -- the releasing agent
  to_agent TEXT NOT NULL DEFAULT '',       -- empty = unassigned: any peer may claim
  revision TEXT NOT NULL,                  -- source-snapshot identifier (required)
  pending_request TEXT NOT NULL DEFAULT '',-- the core question/task
  evidence TEXT NOT NULL DEFAULT '',       -- tests/SHAs/measurements establishing state
  blockers TEXT NOT NULL DEFAULT '',       -- concrete obstacles or open review findings
  next_action TEXT NOT NULL DEFAULT '',    -- precise task for the successor (required)
  status TEXT NOT NULL DEFAULT 'open',     -- open | claimed | completed | superseded
  claimed_by TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
  kind TEXT NOT NULL DEFAULT 'thread'   -- 'thread' = board thread; 'dm' = direct-message conversation
);

CREATE INDEX IF NOT EXISTS idx_handoffs_thread ON handoffs(thread_id);

-- Turn tokens (thread #36): the strict turn-token protocol. INSERT-ONLY —
-- the CURRENT holder of a thread's turn is the to_agent of the newest event,
-- defaulting to threads.created_by when no events exist (the asker starts
-- with the floor). Exactly one holder at a time; passes are explicit and
-- immutable, so the full pass-history (who, to whom, what instruction) is
-- always retrievable. Additive — CREATE IF NOT EXISTS migrates older DBs.
CREATE TABLE IF NOT EXISTS turn_events (
  id INTEGER PRIMARY KEY,
  thread_id INTEGER NOT NULL REFERENCES threads(id),
  from_agent TEXT NOT NULL,                -- the agent passing the turn
  to_agent TEXT NOT NULL,                  -- the agent receiving it (never from_agent)
  from_role TEXT NOT NULL DEFAULT '',      -- role of passing agent (e.g. "implementer", "reviewer", "tester", "researcher")
  to_role TEXT NOT NULL DEFAULT '',        -- role of receiving agent (e.g. "reviewer", "implementer", "tester", "researcher")
  stage TEXT NOT NULL DEFAULT '',          -- workflow stage (e.g. "dev_tested", "reviewing", "review_approved", "changes_requested")
  instruction TEXT NOT NULL DEFAULT '',    -- what the receiver should do with the floor
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_turn_events_thread ON turn_events(thread_id);

-- Thread digests (token saving). A LONG thread costs an agent thousands of
-- tokens to re-read from the top every time it wakes; a digest is a compact
-- summary an agent wrote after reading, so the next reader gets the digest
-- PLUS only the messages past it. Append-only: a digest is never edited, a
-- better one is a new row, and the CURRENT digest is the one covering the
-- most history (ties broken by newest) — coverage is the ranking, because a
-- digest that covers more is strictly more useful, whenever it was written.
-- The body is peer-authored prose and is rendered as such: the row proves
-- WHO wrote WHEN about WHICH range, not that the summary is accurate.
CREATE TABLE IF NOT EXISTS thread_digests (
  id INTEGER PRIMARY KEY,
  thread_id INTEGER NOT NULL REFERENCES threads(id),
  covered_through_msg_id INTEGER NOT NULL, -- this digest summarizes messages up to and including this id
  author TEXT NOT NULL,
  body TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_thread_digests_thread ON thread_digests(thread_id);

-- Agent notes (token saving): an agent's own working state, so a fresh
-- session — after a usage-limit reset, a crash, or a watcher spawn — loads a
-- compact note instead of re-deriving context. Append-only like everything
-- else; the CURRENT note for a (agent, slot) is the newest row. Slots let one
-- agent keep several independent notes (one per workstream).
CREATE TABLE IF NOT EXISTS agent_notes (
  id INTEGER PRIMARY KEY,
  agent TEXT NOT NULL,
  slot TEXT NOT NULL DEFAULT 'default',
  body TEXT NOT NULL,
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_agent_notes_agent ON agent_notes(agent, slot);

-- Answer memoization (token saving): an answer that was expensive to derive,
-- filed so the same question does not wake another model to re-derive it.
-- Lookup is EXACT after case/whitespace normalization — deliberately not
-- fuzzy: a near-miss served as fact is worse than a miss. Every hit is
-- reported with its age and provenance, and the caller decides whether it
-- still applies; answers go stale, so the age is part of the contract.
CREATE TABLE IF NOT EXISTS answer_cache (
  id INTEGER PRIMARY KEY,
  question TEXT NOT NULL,                  -- the question as filed (shown back to the reader)
  question_key TEXT NOT NULL,              -- normalized lookup key
  answer TEXT NOT NULL,
  source_thread_id INTEGER,                -- where the answer came from, when it came from a thread
  source_msg_id INTEGER,
  filed_by TEXT NOT NULL,
  hits INTEGER NOT NULL DEFAULT 0,         -- how many times this memo has been served (a counter, not content)
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_answer_cache_key ON answer_cache(question_key);

-- Token ledger (accounting): what agents spent and what the caches saved.
-- Append-only. Spend is SELF-REPORTED (only the agent knows its own usage;
-- this process never sees a model's token counts), while 'saved' rows can be
-- computed locally from bytes. Tokens are always stored positive with the
-- direction carried by kind, so no sign convention can be misread.
CREATE TABLE IF NOT EXISTS token_events (
  id INTEGER PRIMARY KEY,
  agent TEXT NOT NULL,
  kind TEXT NOT NULL,                      -- spend | saved
  tokens INTEGER NOT NULL,                 -- always positive; kind carries the direction
  thread_id INTEGER,                       -- optional provenance
  source TEXT NOT NULL DEFAULT '',         -- what produced it: digest | note | memo | search | read | spawn | manual
  note TEXT NOT NULL DEFAULT '',
  created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE INDEX IF NOT EXISTS idx_token_events_agent ON token_events(agent, created_at);
