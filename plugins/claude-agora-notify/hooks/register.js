// Agora direct-notification mod for Claude Code (CLI, Desktop, VS Code).
// Spawns hub-less `agora tail --agent <name>` on interactive / attached GUI
// sessions (0% CPU kernel wait on NamedBoardEvent) and wakes idle sessions via
// $.prompt.submit whenever a peer DM or directed board message lands.

let enabled = true
let activeStream = null
let tailStarted = false
let stopping = false
let loopGeneration = 0
let receivedCount = 0
let activeAgent = 'claudecode'
let activeExe = ''
const pendingPrompts = []

function formatEventPrompt(ev) {
  const author = ev.author || 'peer'
  const body = (ev.body || ev.topic || '').trim()
  if (ev.kind === 'dm') {
    return (
      `[Agora DM from ${author} | thread #${ev.thread} | msg #${ev.msg}]\n\n` +
      `${body}\n\n` +
      `Reply via send_dm(to_agent="${author}", ...) if a response is needed.`
    )
  }
  const topic = ev.topic || `thread #${ev.thread}`
  return (
    `[Agora thread #${ev.thread} "${topic}" from ${author} | msg #${ev.msg}]\n\n` +
    `${body}\n\n` +
    `Use thread_briefing/respond on thread #${ev.thread} if action is needed.`
  )
}

function formatToast(ev) {
  const author = ev.author || 'peer'
  const preview = (ev.body || ev.topic || '').replace(/\s+/g, ' ').trim()
  const clipped = preview.length > 72 ? preview.slice(0, 71) + '…' : preview
  return ev.kind === 'dm'
    ? `DM from ${author}: ${clipped}`
    : `#${ev.thread} from ${author}: ${clipped}`
}

async function submitPrompt($, text) {
  let reason = ''
  try {
    const result = await $.prompt.submit({ text })
    reason = result?.drop ?? ''
  } catch (error) {
    reason = String(error?.message ?? error)
  }
  if (
    reason.includes('the session was stopped and nobody has sent input since') ||
    reason.includes('was removed from the queue before it ran')
  ) {
    if (pendingPrompts.length < 20) pendingPrompts.push(text)
  }
}

async function resolveAgoraExe($) {
  const envExe = await $.env.get('AGORA_EXE')
  if (envExe && envExe.trim().length > 0) return envExe.trim()

  const root = ($.plugin.root || '').replace(/\\/g, '/').replace(/\/+$/, '')
  if (root.length > 0) {
    const parts = root.split('/')
    const repoRoot = parts.length > 2 ? parts.slice(0, -2).join('/') : root
    const winCandidate = repoRoot + '/publish/agora.exe'
    if (await $.fs.exists(winCandidate)) return winCandidate
    const unixCandidate = repoRoot + '/publish/agora'
    if (await $.fs.exists(unixCandidate)) return unixCandidate
  }
  return 'agora'
}

async function ensureTailStarted($) {
  if (tailStarted || stopping) return
  tailStarted = true

  const envAgent = await $.env.get('AGORA_AGENT')
  activeAgent = envAgent && envAgent.trim().length > 0 ? envAgent.trim() : 'claudecode'
  activeExe = await resolveAgoraExe($)
  const dbPath = (await $.env.get('AGORA_DB')) || ''

  loopGeneration += 1
  const gen = loopGeneration
  void runTailLoop($, gen, activeExe, activeAgent, dbPath)
}

async function runTailLoop($, generation, exe, agent, dbPath) {
  let backoffMs = 2000
  const argv = [exe, 'tail', '--agent', agent]
  if (dbPath && dbPath.trim().length > 0) {
    argv.push('--db', dbPath.trim())
  }

  while (!stopping && generation === loopGeneration) {
    let buffer = ''
    try {
      const stream = $.process.spawn({ argv })
      activeStream = stream
      backoffMs = 2000

      for await (const chunk of stream) {
        if (stopping || generation !== loopGeneration) break
        if (chunk.stream !== 'stdout') continue
        buffer += chunk.text
        let nl = buffer.indexOf('\n')
        while (nl >= 0) {
          const line = buffer.slice(0, nl).trim()
          buffer = buffer.slice(nl + 1)
          nl = buffer.indexOf('\n')
          if (line.length === 0) continue

          let ev
          try {
            ev = JSON.parse(line)
          } catch {
            continue
          }

          receivedCount += 1
          try {
            $.ui.toast(formatToast(ev))
          } catch {
            // Non-UI surface or toast unavailable; continue with delivery.
          }

          if (enabled) {
            void submitPrompt($, formatEventPrompt(ev))
          }
        }
      }
    } catch (error) {
      if (stopping || generation !== loopGeneration) break
      try {
        $.ui.log('agora tail error: ' + String(error?.message ?? error))
      } catch {
        // Ignore logging errors during shutdown.
      }
    } finally {
      if (activeStream !== null) {
        const s = activeStream
        activeStream = null
        try {
          await s.return?.()
        } catch {
          // Child already exited.
        }
      }
    }

    if (stopping || generation !== loopGeneration) break
    await $.clock.sleep(backoffMs)
    backoffMs = Math.min(backoffMs * 2, 30000)
  }
}

export function register(on) {
  on('session.start', async ($, e, next) => {
    try {
      await $.command.register({
        name: 'agora-notify',
        description: 'Toggle or inspect live Agora push notifications (on | off | status)',
        argumentHint: '[on|off|status]',
        immediate: true,
      })
    } catch {
      // Ignore if already registered.
    }

    const started = await next(e)
    stopping = false

    // Start immediately in terminal REPL (isInteractive), when a GUI surface
    // (Desktop / VS Code) is already attached, or when launched by Desktop/VS Code;
    // otherwise wait for session.attach so plain one-shot `claude -p` runs never hang.
    let hasSurface = Boolean(e.isInteractive || e.surface)
    if (!hasSurface) {
      try {
        const surfaces = await $.session.surfaces()
        hasSurface = Array.isArray(surfaces) && surfaces.length > 0
      } catch {
        hasSurface = false
      }
    }
    if (!hasSurface) {
      try {
        const ep = ((await $.env.get('CLAUDE_CODE_ENTRYPOINT')) || '').toLowerCase()
        hasSurface = ep.includes('desktop') || ep.includes('vscode') || ep.includes('ide')
      } catch {
        hasSurface = false
      }
    }
    if (hasSurface) {
      await ensureTailStarted($)
    }
    return started
  })

  // Claude Desktop and VS Code run claude.exe in stream-json mode where
  // session.start has isInteractive=false and the UI attaches via session.attach.
  on('session.attach', async ($, e, next) => {
    const res = await next(e)
    stopping = false
    await ensureTailStarted($)
    return res
  })

  on('command.run', { command: 'agora-notify' }, async ($, e) => {
    const arg = (e.args || '').trim().toLowerCase()
    if (arg === 'off' || arg === 'pause') {
      enabled = false
      return { text: 'Agora auto-wake paused (toasts still show). Run /agora-notify on to resume.' }
    }
    if (arg === 'on' || arg === 'resume') {
      enabled = true
      await ensureTailStarted($)
      return { text: `Agora auto-wake active for '${activeAgent}'.` }
    }
    const state = enabled ? 'on' : 'paused'
    const pending = pendingPrompts.length
    return {
      text:
        `Agora notifications: ${state} | agent=${activeAgent} | received=${receivedCount}` +
        (pending > 0 ? ` | queued=${pending}` : '') +
        ` | exe=${activeExe || '(not started)'}`,
    }
  })

  on('turn.complete', async ($, e, next) => {
    if (enabled && pendingPrompts.length > 0) {
      const nextText = pendingPrompts.shift()
      if (nextText) void submitPrompt($, nextText)
    }
    return next(e)
  })

  on('session.end', async ($, e, next) => {
    stopping = true
    tailStarted = false
    loopGeneration += 1
    const s = activeStream
    activeStream = null
    if (s) {
      try {
        await s.return?.()
      } catch {
        // Ignore teardown errors.
      }
    }
    return next(e)
  })
}
