// Antigravity SessionStart / PreInvocation hook beacon.
// Records the most recently active top-level Antigravity App conversationId so
// the agora-bridge sidecar injects incoming Agora DMs into it.
// Always outputs {} so the turn proceeds untouched.

import { mkdirSync, renameSync, writeFileSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'

const ACTIVE_FILE =
  process.env.AGORA_ANTIGRAVITY_ACTIVE_FILE ||
  join(homedir(), '.gemini', 'antigravity', 'sidecar_data', 'agora-bridge', 'data', 'active.json')

let raw = ''
process.stdin.setEncoding('utf-8')
process.stdin.on('data', (chunk) => {
  raw += chunk
})
process.stdin.on('end', () => {
  try {
    const payload = JSON.parse(raw || '{}')
    const convId = typeof payload.conversationId === 'string' ? payload.conversationId.trim() : ''
    const transcript = typeof payload.transcriptPath === 'string' ? payload.transcriptPath : ''
    const lastUserInput = typeof payload.lastUserInput === 'string' ? payload.lastUserInput.trim() : ''

    // Skip non-App surfaces (IDE/CLI) and background subagents (no lastUserInput).
    const isOtherSurface =
      transcript.includes('antigravity-ide') || transcript.includes('antigravity-cli')
    if (convId.length > 0 && !isOtherSurface && lastUserInput.length > 0) {
      mkdirSync(dirname(ACTIVE_FILE), { recursive: true })
      const tmp = `${ACTIVE_FILE}.${process.pid}.tmp`
      writeFileSync(
        tmp,
        JSON.stringify({
          conversationId: convId,
          cwd: payload.cwd || '',
          hookEventName: payload.hookEventName || '',
          updatedAt: new Date().toISOString(),
        }),
        'utf-8'
      )
      renameSync(tmp, ACTIVE_FILE)
    }
  } catch {
    // Fail soft: never disrupt a conversation turn.
  }
  process.stdout.write('{}')
})
