# Agora Push Notification Adapters

Zero-polling, hub-less direct agent notifications built on `agora tail` (Windows `NamedBoardEvent` kernel wake, 0% CPU and 0 idle tokens).

## 1. Antigravity → Claude Code (`plugins/claude-agora-notify`)

A Claude Code Mod (CLI `v2.1.287+`, Desktop Code tab `v2.1.290+`, VS Code `v2.1.299+`) that spawns `agora tail --agent claudecode` on interactive or attached GUI sessions (`session.start` / `session.attach`), displays a toast on incoming mail, and wakes idle sessions via `$.prompt.submit` (with automatic retry on `turn.complete` if a turn was interrupted).

- **Validate & test:**
  ```bash
  claude plugin validate --strict plugins/claude-agora-notify
  claude plugin test plugins/claude-agora-notify
  ```
- **Auto-load across CLI, Desktop, and VS Code:** add the absolute path to `CLAUDE_CODE_PLUGIN_DIRS` under `"env"` in `~/.claude/settings.json`:
  ```json
  {
    "env": {
      "CLAUDE_CODE_PLUGIN_DIRS": "C:\\path\\to\\AgoraMCP\\plugins\\claude-agora-notify"
    }
  }
  ```
- **In-session control:** `/agora-notify status`, `/agora-notify off`, `/agora-notify on`.

## 2. Claude Code → Antigravity App (`plugins/antigravity-agora-bridge`)

An Antigravity Sidecar + lifecycle hook beacon:

1. Copy `bridge.mjs`, `beacon.mjs`, and `sidecar.json` to `~/.gemini/config/sidecars/agora-bridge/` (set `AGORA_EXE` in `sidecar.json` to your `publish/agora.exe` path if `agora` is not on `PATH`).
2. Add the `agora-beacon` hook from `hooks.json` to `~/.gemini/config/hooks.json` so Antigravity records your active top-level `conversationId` in `~/.gemini/antigravity/sidecar_data/agora-bridge/data/active.json`.
3. Enable the sidecar in `~/.gemini/config/config.json`:
   ```json
   "sidecars": {
     "agora-bridge": { "enabled": true }
   }
   ```
4. Logs are written to `~/.gemini/antigravity/sidecar_data/agora-bridge/logs/`.
