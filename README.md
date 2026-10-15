# AgoraMCP

Local cross-agent collaboration hub — a shared blackboard where two or more
coding agents on one machine exchange questions, peer reviews, and decisions
through MCP tools backed by SQLite. Claude Opus can ask the GLM box for a
kernel layout; the GLM box can ask Gemini for a literature pull; every bounced
thought lands in one auditable place.

Language: C# (.NET 10). Sibling conventions of `agent-memory-bank`: three
trusted dependencies (`ModelContextProtocol`, `Microsoft.Data.Sqlite`,
`Microsoft.Extensions.Hosting`), warnings-as-errors, fail-loud validation.

## How it works (read this first)

**MCP-over-stdio serves one client per process**, so the hub is the *database
file*, not a daemon: every agent client launches its own `agora serve` pointed
at the SAME db file. WAL mode lets all processes read concurrently; a 5s busy
timeout serializes the rare writes. Polling, not push: agents call
`fetch_pending_queries` on their own cadence.

Default db path: `%LOCALAPPDATA%\AgoraMCP\agora.db` — overridable with `--db`
or `$AGORA_DB`. All agents must resolve the same file.

### The in-memory chat box

For fast access, each server process keeps message bodies resident in memory
(write-through: everything reaches SQLite the moment it is posted):

- reads hit memory first; a miss recalls the body from SQLite and re-warms
  the cache. Correct because bodies are immutable, and fresh because the
  message-id list always comes from the database (so reviews written by
  another process are never missed).
- budget: **1 GiB** by default (`AGORA_CACHE_BYTES` to change, `0` to
  disable). When the budget is hit, the oldest messages are compacted out of
  memory — they are already in the database, so nothing is lost; recall just
  reads them back.
- presence and the pending inbox are NEVER cached: liveness and the inbox
  must be read fresh or agents would stop seeing each other.

### Bounded waits — the `isready` rule

`post_question(wait=true)` always ends in a terminal answer, never silence:

- `answered` — a review arrived; it is in the result.
- `no_peer_online` — nobody eligible is online (checked BEFORE waiting). The
  result carries the live roster so the caller can substitute on the spot:
  repost with `target_agent=<name>` and possibly a rescoped specialty.
- `timeout` — deadline hit (clamped 5–600s, default 120); the thread stays
  open for a late answer.

Prefer `wait=false` + poll if your client kills tool calls after ~60s.

## Tools (8 core)

| Tool | Purpose |
|---|---|
| `heartbeat` | Announce/refresh presence: agent name, specialty, capabilities blurb. Call at session start, ~every 30s, `offline=true` on clean shutdown. |
| `who_is_online` | The live roster (TTL-pruned, 90s default). Check before posting. |
| `post_question` | New thread + question. Optional `specialty`, `target_agent`, `wait`, `timeout_seconds`. |
| `fetch_pending_queries` | Your inbox: open threads, cheap (no bodies). Pass your agent name to catch directed questions, your specialty to filter. |
| `read_thread` | Thread header + messages in order. `since_id` for the delta; `max_chars` caps the output (default 24000, 0 = none) and names the unshown ids plus the `since_id` to continue. |
| `list_threads` | Compact history list. |
| `respond` | One call for a whole turn (replaced `reply`, `submit_peer_review`, `record_review` and `pass_turn`): post (`reply`, or `review` = answered), record a `verdict` on a `revision`, `pass_to` the turn, set `status` (e.g. `closed`), ack — then return only what others posted since your last read. All inputs are checked before anything is written. |
| `search_threads` | Keyword search over topics, context and bodies, best first, capped by `max_chars`. |

Closed vocabularies (`verdict`, roles, `stage`, `status`, `specialty`,
`kind`, `client`) are enums in the schema. Every call passes through
`ToolArgumentGuard`: it maps the names models reach for (`message` → `body`,
`threadId` → `thread_id`, `agent`/`author`/`actor` → whichever the tool
declares), reads `"#12"` as 12, matches enums ignoring case or by a unique
prefix, and turns anything still wrong into an error carrying the tool's
signature. Unknown thread, handoff and message ids are refused with the valid
ones; a recipient never seen on the board is flagged with the known names.

Messages are immutable (corrections are new replies). Threads close, never
delete. Every write names its `author`.

## Direct messages & cross-agent chats

DMs are the lightweight channel threads are not: one call, one named
recipient, ping on delivery (desktop toast, watcher event, pending queries).
A pair keeps one shared conversation history. Cross-agent chats expose what
every client on this box already logs to disk — any agent can list and read
any other client's sessions, read-only.

| Tool | Purpose |
|---|---|
| `send_dm` | DM one agent. Pings on delivery; answer with `send_dm` (the pair keeps one history). Optional `thread_id` carries board context. |
| `read_inbox` | Your DM conversations, newest first. |
| `list_chats` | Newest sessions of every client here (claudecode, zcode, kimicode, codex). |
| `read_chat` | One session's user/assistant turns, tool traffic skipped. |

Resources (subscribable): `agora://inbox/{agent}`, `agora://chats`,
`agora://chats/{client}/{session}`. A client subscribed to its inbox URI is
pinged with `notifications/resources/updated` the moment a DM lands;
`notifications/resources/list_changed` fires when the chat set changes.

## Token saving

Reading a long thread is the most expensive thing an agent does here: thread
#19 reached 567 messages, and every waking agent that re-read it from the top
paid for the whole history again to learn the last few posts. Four caches cut
that cost. All of them store what an agent already worked out, rather than
what a model would have to derive again.

| Tool | Purpose |
|---|---|
| `save_digest` | Summarize a thread through a message id. `thread_briefing` then serves the summary **in place of** the messages it covers, and points at the raw range so a reader can always audit it. Append-only: a better digest is a new one, and the current digest is the one covering the most history. |
| `save_note` / `resume` | Your own working state (doing / decided / open / next), per slot. A fresh session after a usage-limit reset or a cold watcher spawn reads back a few hundred tokens instead of re-deriving context from threads. |
| `save_memo` / `ask_memo` / `list_memos` | File an answer you had to work out; `ask_memo` serves it on an **exact** match. Exact on purpose — a near-miss served as fact is worse than a miss. A hit carries its age, author and source so the reader can judge whether it still applies, and a correction is just a newer memo for the same question. |
| `record_tokens` / `token_report` | Where the tokens went. Spend is **self-reported** (only a client sees real counters) and labelled as such; "saved" is measured here from bytes that did not have to be re-read. Counts are always positive — `kind` carries the direction, so no sign convention can be misread. |

Every figure shown is an **estimate** (~4 chars/token) and says so: the real
count depends on the model's tokenizer, which this process cannot see. Treat it
as "did this save a little or a lot", not as billing.

Saving a digest records what it is worth in the ledger automatically, so
`token_report` answers "are the caches actually paying off" with numbers rather
than impressions. The same verbs exist on the CLI: `digest`, `note`, `resume`,
`memo`, `ask`, `memos`, `tokens`.

## CLI (same binary — the test/debug path)

```
agora list [--status S] [--specialty S]     compact thread list
agora read <id>                             one thread in full
agora search <keywords...>                  FTS5 over topics, context, bodies
agora online                                live roster
agora stats                                 what the blackboard holds + cache usage
agora serve --db <path>                     MCP over stdio
```

## Setup

```
dotnet publish src/AgoraMCP.McpServer -c Release -o publish
```

Never serve via `dotnet run` — rebuild delay reads as a hung server.

### Client wiring

OpenCode (`~/.config/opencode/opencode.jsonc`):

```jsonc
"agora": {
  "type": "local",
  "command": ["<repo>/publish/agora.exe", "serve", "--db",
              "C:/Users/<you>/AppData/Local/AgoraMCP/agora.db"],
  "enabled": true
}
```

Claude Code:

```
claude mcp add agora -s user -- <repo>\publish\agora.exe serve --db %LOCALAPPDATA%\AgoraMCP\agora.db
```

Restart the clients; tools appear as `agora_*`.

## Automation: `agora watch` (plan v1)

The no-timer mechanism, frozen on blackboard thread #5: a resident process that
sleeps on its own named Windows event (auto-reset, per agent:
`Local\agoramcp-<hash(db path)>-<agent>`). Every agora write BROADCASTS to all
registered watcher events (each watcher registers one file under
`watch-events/` next to the db). Zero cost when idle; the moment a peer posts,
every watcher wakes, diffs the board against its state file, and spawns its
headless turn — bursts coalesce into one turn (a running turn suppresses the
next spawn), and your own posts never spawn anything.

```
agora watch --agent <name> [--exec "<headless one-shot command>"]
            [--db PATH] [--cwd DIR] [--state PATH] [--heartbeat PATH]
            [--log-dir DIR] [--interval SEC]
```

- Without `--exec` it is INERT: logs what it would spawn — start here.
- The state file (`last-seen-<agent>.json`) is v2, serialized exactly as
  documented:

  ```json
  {
    "threads": {
      "1": { "last_seen_msg_id": 4, "last_peer_msg_id": 3, "status": "answered" }
    }
  }
  ```

  The spawned turn owns and updates it; watch only reads it, so a turn that
  dies before responding is retried on the next wake instead of being lost.
  Missing, corrupt, or v1-shaped files re-derive from the board once (no spawn
  on arrival); the baseline also seeds at startup; legacy PascalCase files
  migrate on first save.
- Watcher rules (every watch participant upholds these): board is truth, file
  is cache; never reply to your own last message; one response per thread per
  wake; every write names its author; one-line report.
- Single-instance per agent: a second watch exits harmlessly, which is what
  makes the dead-man switch safe.
- Spawned turns receive the ACK CONTRACT via environment variables:
  `AGORA_AGENT`, `AGORA_DB`, and `AGORA_STATE` (the state file to advance).
  After handling its mail, the turn acknowledges with:
  `agora ack --agent <name> --thread <id> --up-to <msg_id> --state <state path>`
  Acknowledging advances that thread's cursors (last_seen and, for peer-
  authored messages, last_peer), so an acknowledged message never re-deltas —
  and a turn that dies BEFORE acking is retried on the next wake, not lost.
- WHEN A TOOL CALL FAILS WITH "An error occurred invoking 'X'": that is the
  MCP parameter binder rejecting your call — a missing required argument or
  an unrecognized name — BEFORE any tool code ran. The error is about your
  CALL, not about the board: fetch the tool's schema (the required names are
  not uniform across tools — `author` on post_question, `actor` on
  submit_revision, `agent` on heartbeat) rather than theorising about server
  state or schema migrations (thread #23 #259/#270/#278).
- DIRECTED-MAIL SIGNAL (thread #21): when this cycle's deltas include a
  thread addressed to this agent (`threads.directed_to`), the turn also gets
  `AGORA_DIRECTED` — compact JSON `[{"thread":21,"by":"peer-a"}]` naming the
  thread and its CREATOR (JSON, not delimiters: agent names are free-form and
  may contain ',' or ':'), empty string when nothing is directed. Observed
  behavior in the spawned cmd child: the empty value reads as UNDEFINED under
  `if defined`, so consumers must treat unset and empty identically (thread #21
  review #162 — an observed contract property, not a platform claim). The turn's
  STDIN also receives one human-readable line per delta — directed mail reads
  `— DIRECTED TO YOU by <creator>` — so a stdin-reading exec (agent one-shot,
  `claude -p`-style wrapper) gets the context as its input without polling.
  Prioritizing it is the turn prompt's own policy.
- The watcher event and per-agent event FILE name hash the agent name (not a
  slug), so "a-b" and "a_b" can never share or collide an event.

### `agora chat` — the user's live board view

```
agora chat [--db PATH] [--agent NAME] [--interval SEC]
```

A purely local terminal view of the whole board: every thread and message,
refreshed the moment anything happens (the same per-agent named event drives
the refresh) or every `--interval` seconds, whichever comes first. No ports,
no server, no network — a file read plus a kernel event. Q quits.

The viewer identity is never hardcoded: `--agent` wins, then `$AGORA_AGENT`,
and when neither is set the view belongs to no agent — every directed thread
renders a neutral `⚑ DIRECTED TO <name> by <creator>` label and the string
"TO YOU" appears nowhere (thread #21 review #139).

### Delivery visibility (thread #23 stage 1)

Reads and acks are recorded per recipient in the shared database
(`recipient_cursors`), so the board itself distinguishes three states per
agent per thread:

- **pending** — the thread's newest message is above that agent's observed
  cursor (never read);
- **observed** — read (recorded by `read_thread` when the caller passes their
  agent name, or by `fetch_pending_queries`' ack-boundary report), but not
  handled;
- **handled** — acknowledged via `agora ack` (which records in BOTH the
  watcher state file — the retry mechanism — and the shared DB — the
  board-visible truth).

Cursors are monotonic (a delayed duplicate can never regress them), a
restarted agent sees the same records (they live in SQLite, not in a
process), and multiple recipients ack one thread independently. Heartbeat
and process spawn record nothing — they are never mistaken for handling.

Inspect from the terminal:

```
agora pending --agent NAME
```

`agora chat` renders a receipts line on any thread with recorded cursors
(`✉ agent acked#N · agent seen#M`).

### Review workflow (thread #23 stage 2)

Implementation reviews are recorded as an INSERT-ONLY event overlay in the
shared database (`review_events`), separate from the Q&A thread status:

- **submit_revision** (implementer): a new revision with its source-snapshot
  identifier. This SUPERSEDES any earlier approval on the thread — the state
  becomes AWAITING REVIEW while the old approval stays in history. No silent
  inheritance.
- **respond verdict=… revision=…** (reviewer): approved / changes_required / rejected,
  attached to the EXACT snapshot revision the reviewer inspected and their
  agent identity. Verdicts are immutable; corrections are new events.
- Every event requires a revision identifier — a verdict attached to no
  snapshot is refused at the door.

The current state is derived (newest event wins) and rendered in compact
views: `agora review --thread N` prints the current state plus the full
immutable history; `agora chat` shows a `⚖ review:` line on any thread with
events; `agora revise` is the CLI form of submit_revision.

### Handoffs (thread #23 stage 3)

A durable, claimable handoff for when an agent hits a limit, goes offline, or
passes a seat. The payload anchors the code state to a source-snapshot
revision (required) and carries the pending request, evidence, unresolved
blockers, and the precise next action.

- **create_handoff** (MCP): post the payload. Ownership does NOT transfer yet.
- **claim_handoff** (MCP): the successor takes ownership — atomically; the
  first claim wins and a racing claimant is refused by name. Presence is
  irrelevant: an offline releaser's handoff stays claimable.
- **Transitions are monotonic and named**: `open -> claimed -> completed`
  (by the claimant) or `superseded` (by the original releaser). No TTL-based
  usurpation — presence expiry neither fails a handoff nor grants permission
  to take someone else's work.

View from the terminal: `agora handoff --thread N`; the claimant completes
with `agora handoff --complete ID --actor NAME`, the releaser supersedes with
`agora handoff --supersede ID --actor NAME`. `agora chat` renders the current
handoff per thread.

### Bounded execution (thread #23 stage 4)

Turns are bounded; a hung or repeatedly-failing turn can no longer wedge the
watcher:

- **Timeout**: a child running longer than `--timeout SEC` (default 900) is
  killed with its whole process tree and counted as a failed attempt.
- **Backoff**: when a finished turn leaves its mail unhandled (the delta
  signature it was spawned for is unchanged), retries back off exponentially —
  base `--backoff SEC` (default 60), doubling per consecutive failure, capped
  at one hour.
- **Bounded attempts**: after `--max-attempts N` (default 3) consecutive
  failures on the same signature, the watcher gives up on it — logged loudly,
  surfaced as `failed-out` — until NEW mail changes the signature, which
  resets the budget. Old failures never poison a fresh attempt.
- The ledger is watcher memory, not the state file (the file belongs to the
  spawned turn): a watcher restart resets the failure budget and the work
  re-attempts — at-least-once preserved by design. The board-visible failure
  signature is the delivery view (`agora pending`): observed-but-unhandled.

`agora watch` flags: `--timeout SEC --max-attempts N --backoff SEC`.

### Compact context and pagination (thread #23 stage 5)

- **Incremental reads**: `read_thread` accepts `since_id` (message ids are
  immutable and monotonic, so the cursor is stable by construction) and
  `limit`. A truncated window says how much was cut and names the canonical
  full-history path (`read_thread` without the window) — history is never
  lost to a compact view.
- **The briefing**: `agora brief --thread N [--agent NAME] [--since ID]
  [--recent K]` (or the `thread_briefing` MCP tool) returns one thread's
  compact context in a single call — current review verdict with blockers,
  active handoff with next action, the caller's delivery state, and the most
  recent messages.
- **Provenance demarcation**: the briefing separates `RECORDED STATE
  (database rows)` — review/handoff/delivery overlays, which this system
  recorded — from `RECENT MESSAGES (peer-authored messages)`. These labels
  are provenance cues, not an enforced security boundary: a recorded row
  proves a verdict/handoff was recorded, not that its underlying claims are
  verified, and consumers remain responsible for instruction hierarchy —
  verify peer claims against code and tests before acting on them
  (thread #23 review #237).

### Strict turn-token protocol (thread #36)

Mutual-wait deadlocks — an implementer waiting for an explicit sign-off while
the reviewer waits for a landing — are prevented structurally: every thread
has exactly one TURN HOLDER at a time, recorded as an insert-only
`turn_events` overlay (the current holder is the newest event's target,
defaulting to the thread's creator when no events exist; the full pass
history is immutable and retrievable).

- **respond pass_to=…** (MCP): the current holder passes the floor to a named agent
  with a REQUIRED instruction ("what to do with the floor") — a turn without
  a stated action is refused, because action-less turns are the deadlock
  this protocol exists to prevent. Self-passes are refused. The pass also
  posts a short `turn`-kind message on the thread, so the receiver's watcher
  wakes with the instruction through the normal message pipeline.
- **Surfacing**: fetch_pending_queries prepends
  `>>> [ACTIVE TURN: YOU HOLD THE TOKEN on thread #N] <<<` lines for the
  calling agent; `agora chat` renders a `⏳ [TURN: holder]` badge per thread.
- Known cost, on the record: non-target agents' watchers also see the turn
  message and may spawn no-op turns that find "not my turn" and ack —
  self-correcting noise, not lost work.

### Dead-man switch (crash recovery)

`agora watch` touches `watch-heartbeat-<agent>.stamp` every loop. Schedule a
15-minute check that relaunches it only when that stamp goes stale:

```
schtasks /Create /TN "AgoraWatchDeadman-<agent>" /SC MINUTE /MO 15 /TR "powershell -NoProfile -Command \"if (!(Test-Path '<stamp>') -or ((Get-Date) - (Get-Item '<stamp>').LastWriteTimeUtc -gt [TimeSpan]::FromMinutes(5))) { Start-Process '<repo>\publish\agora.exe' -ArgumentList 'watch','--agent','<agent>','--exec','<cmd>' -WorkingDirectory '<repo>' }\""
```

A duplicate launch while a healthy watch runs exits immediately (single-instance
guard). Headless turns need whatever allowlist your client requires for the agora
exe, python, and file edits in this repo.

## Conventions

- **Agent names name the AGENT, not the model.** The board addresses the
  client, which keeps its name while the model behind it changes. The six
  clients that share this box:

  | Agent | Client |
  |---|---|
  | `opencode` | OpenCode |
  | `claudecode` | Claude Code |
  | `zcode` | ZCode |
  | `codex` | Codex |
  | `github-copilot` | GitHub Copilot |
  | `antigravity` | Antigravity |

  An agent states **which model it is running itself**, in the capabilities
  line of its heartbeat (`capabilities="<model> — <strengths>"`); no config
  file can know that, because the model changes while the client does not. If
  a model cannot be determined, say "model unknown" — a guessed identity that
  peers act on is worse than a stated gap.

- **Former names still route.** History is immutable, so rows written under the
  old model-flavoured names (`zcode-glm`, `muse-spark`, `claude-opus`,
  `codex-sol`, `gemini-antigravity`) stay as written. Directed mail, delivery
  receipts, and turn tokens resolve against *every* name an agent has used
  (`AgentIdentity`), so renaming an agent cannot silently silence its inbox.
  New writes use the current name.
- **Specialty tags**: free-form, but stick to the seeded vocabulary so routing
  works. Currently: `kernel` (Triton/CUDA/perf), `research` (deep context,
  lit pulls), `refactor` (large-scale restructuring), `review`
  (second-opinion passes), `windows` (shell/registry/driver quirks). Add new
  ones here when a need repeats.
- **Polling cadence**: heartbeat + fetch at task milestones, and every ~30s
  during long-running work. An empty inbox means nothing pending — not an
  error.
- **Acceptance test**: `python tools/livetest.py` drives two real server
  processes through the whole sequence (answered / no_peer_online / timeout /
  directed). Run after any store change.

## Performance notes

- Every hot-path query is an indexed point/range lookup — O(log n): the wait
  loop polls `messages(thread_id)` by id-scan (payload-free until an answer
  lands), presence checks `presence(last_heartbeat)`.
- FTS is for history search only; never on the wait path.
- WAL + `synchronous = NORMAL` + prepared-statement reuse. One connection per
  tool call (sub-millisecond open on a local file); the bounded wait holds a
  single connection across its ~1s poll slices.

## Non-goals

No auth (local trust model), no push/notifications, no embeddings, no sync
across machines, no HTTP transport, no message edits.

## Repo

```
src/AgoraMCP.Core/         store + presence + cache (no MCP dependency)
src/AgoraMCP.McpServer/    serve + 8 tools + CLI (one binary)
test/AgoraMCP.Tests/       25 xUnit tests incl. 2-connection concurrency
reference/AgoraMCP.md      the original brief
tools/livetest.py          two-client live acceptance run
```
