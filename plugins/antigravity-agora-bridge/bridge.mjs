// Agora DM / directed-mail bridge sidecar for Antigravity App (2.0).
// Runs `agora tail --agent antigravity` (0% CPU NamedBoardEvent wait) and
// delivers incoming peer messages into the most recently active top-level
// Antigravity conversation via `agentapi send-message`.

import { execFileSync, spawn } from 'node:child_process'
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { homedir } from 'node:os'
import { join } from 'node:path'

const AGORA_EXE = (process.env.AGORA_EXE || 'agora').trim()
const AGORA_AGENT = (process.env.AGORA_AGENT || 'antigravity').trim()
const AGORA_DB = (process.env.AGORA_DB || '').trim()
const DATA_DIR =
  process.env.ANTIGRAVITY_EXECUTABLE_DATA_DIR ||
  join(homedir(), '.gemini', 'antigravity', 'sidecar_data', 'agora-bridge', 'data')
const ACTIVE_FILE = join(DATA_DIR, 'active.json')
const BRAIN_DIR = join(homedir(), '.gemini', 'antigravity', 'brain')

function log(msg) {
  const stamp = new Date().toISOString()
  process.stderr.write(`[agora-bridge ${stamp}] ${msg}\n`)
}

function findFallbackConversationId() {
  if (!existsSync(BRAIN_DIR)) return null
  const candidates = []
  let dirs
  try {
    dirs = readdirSync(BRAIN_DIR, { withFileTypes: true })
  } catch {
    return null
  }
  for (const d of dirs) {
    if (!d.isDirectory()) continue
    const transcript = join(BRAIN_DIR, d.name, '.system_generated', 'logs', 'transcript.jsonl')
    if (!existsSync(transcript)) continue
    try {
      const st = statSync(transcript)
      candidates.push({ id: d.name, transcript, mtimeMs: st.mtimeMs })
    } catch {
      // Ignore transient stat failures.
    }
  }
  candidates.sort((a, b) => b.mtimeMs - a.mtimeMs)

  // Pick the newest conversation that actually has a USER_EXPLICIT step
  // (skips subagents, which only have SYSTEM_MESSAGE + PLANNER_RESPONSE).
  for (const c of candidates.slice(0, 15)) {
    try {
      const content = readFileSync(c.transcript, 'utf-8')
      if (content.includes('"USER_EXPLICIT"') || content.includes('"USER_INPUT"')) {
        return c.id
      }
    } catch {
      // Ignore unreadable file.
    }
  }
  return candidates[0]?.id ?? null
}

function resolveTargetConversationId() {
  if (existsSync(ACTIVE_FILE)) {
    try {
      const data = JSON.parse(readFileSync(ACTIVE_FILE, 'utf-8'))
      if (data && typeof data.conversationId === 'string' && data.conversationId.trim().length > 0) {
        return data.conversationId.trim()
      }
    } catch {
      // Fall through to transcript scan.
    }
  }
  return findFallbackConversationId()
}

function formatMessage(ev) {
  const author = ev.author || 'peer'
  const body = (ev.body || ev.topic || '').trim()
  if (ev.kind === 'dm') {
    return {
      title: `Agora DM from ${author}`,
      content:
        `[Agora DM from ${author} | thread #${ev.thread} | msg #${ev.msg}]\n\n` +
        `${body}\n\n` +
        `Reply via send_dm(to_agent="${author}", ...) if a response is needed.`,
    }
  }
  const topic = ev.topic || `thread #${ev.thread}`
  return {
    title: `Agora #${ev.thread} from ${author}`,
    content:
      `[Agora thread #${ev.thread} "${topic}" from ${author} | msg #${ev.msg}]\n\n` +
      `${body}\n\n` +
      `Use thread_briefing/respond on thread #${ev.thread} if action is needed.`,
  }
}

function sendViaAgentApi(conversationId, title, content) {
  // Prefer ANTIGRAVITY_AGENTAPI_EXE directly (no shell!) to avoid Windows .bat
  // ENOENT / cmd.exe quoting issues — matches Antigravity's own sidecar_sdk.
  const exe = process.env.ANTIGRAVITY_AGENTAPI_EXE
  const file = exe || 'agentapi'
  const args = exe
    ? ['agentapi', 'send-message', '--title', title, conversationId, content]
    : ['send-message', '--title', title, conversationId, content]
  return execFileSync(file, args, {
    encoding: 'utf-8',
    timeout: 30_000,
    env: process.env,
  })
}

function deliverEvent(ev) {
  const conversationId = resolveTargetConversationId()
  if (!conversationId) {
    log(`no active Antigravity conversation found for msg #${ev.msg} from ${ev.author}; skipping.`)
    return
  }
  const { title, content } = formatMessage(ev)
  try {
    const out = sendViaAgentApi(conversationId, title, content)
    log(`delivered msg #${ev.msg} (${ev.kind} from ${ev.author}) -> ${conversationId} ${out.trim()}`)
  } catch (err) {
    const fallback = findFallbackConversationId()
    if (fallback && fallback !== conversationId) {
      log(`send-message to ${conversationId} failed (${err.message}); retrying fallback ${fallback}`)
      try {
        const out = sendViaAgentApi(fallback, title, content)
        log(`delivered msg #${ev.msg} -> fallback ${fallback} ${out.trim()}`)
        return
      } catch (err2) {
        log(`fallback send-message to ${fallback} failed: ${err2.message}`)
        return
      }
    }
    log(`send-message to ${conversationId} failed: ${err.message}`)
  }
}

let stopping = false
let child = null
let backoffMs = 2000

function shutdown() {
  stopping = true
  if (child) {
    try {
      child.kill()
    } catch {
      // Already exited.
    }
  }
  process.exit(0)
}

process.on('SIGINT', shutdown)
process.on('SIGTERM', shutdown)

function startTail() {
  if (stopping) return
  const argv = ['tail', '--agent', AGORA_AGENT]
  if (AGORA_DB.length > 0) argv.push('--db', AGORA_DB)
  if (process.env.AGORA_SINCE) argv.push('--since', process.env.AGORA_SINCE)
  if (process.env.AGORA_ONCE === '1') argv.push('--once')

  log(`starting: ${AGORA_EXE} ${argv.join(' ')}`)
  const proc = spawn(AGORA_EXE, argv, {
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
  })
  child = proc

  let buffer = ''
  proc.stdout.setEncoding('utf-8')
  proc.stdout.on('data', (chunk) => {
    backoffMs = 2000
    buffer += chunk
    let nl = buffer.indexOf('\n')
    while (nl >= 0) {
      const line = buffer.slice(0, nl).trim()
      buffer = buffer.slice(nl + 1)
      nl = buffer.indexOf('\n')
      if (line.length === 0) continue
      try {
        const ev = JSON.parse(line)
        deliverEvent(ev)
      } catch (err) {
        log(`bad JSON line from agora tail: ${err.message}`)
      }
    }
  })

  proc.stderr.setEncoding('utf-8')
  proc.stderr.on('data', (chunk) => {
    const trimmed = chunk.trim()
    if (trimmed.length > 0) log(`agora tail stderr: ${trimmed}`)
  })

  proc.on('error', (err) => {
    log(`failed to spawn ${AGORA_EXE}: ${err.message}`)
  })

  proc.on('exit', (code, signal) => {
    child = null
    if (stopping || process.env.AGORA_ONCE === '1') {
      process.exit(code ?? 0)
      return
    }
    log(`agora tail exited (code=${code}, signal=${signal}); restarting in ${backoffMs}ms`)
    const delay = backoffMs
    backoffMs = Math.min(backoffMs * 2, 30_000)
    setTimeout(startTail, delay)
  })
}

startTail()
