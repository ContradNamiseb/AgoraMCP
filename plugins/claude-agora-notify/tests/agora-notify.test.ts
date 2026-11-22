import { expect, mock, test } from 'claude-code/testing'

function deferred<T = void>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((r) => {
    resolve = r
  })
  return { promise, resolve }
}

test('incoming split-chunk DM triggers toast and $.prompt.submit', async ($, on) => {
  mock.env(on, { AGORA_AGENT: 'claudecode', AGORA_EXE: 'C:/fake/agora.exe', AGORA_DB: '' })
  mock.clock(on)
  on('session.start', () => ({ cwd: '/work' }))
  on('session.end', () => ({ sessionId: 's1' }))
  on('command.register', () => ({ value: undefined }))

  const toasts: string[] = []
  const submitted: string[] = []
  const gotSubmit = deferred()
  on('ui.toast', (_$, e) => {
    toasts.push(e.text)
    return { value: undefined }
  })
  on('prompt.submit', (_$, e) => {
    submitted.push(e.text)
    gotSubmit.resolve()
    return { text: e.text }
  })

  const payload = JSON.stringify({
    seq: 42,
    kind: 'dm',
    thread: 7,
    msg: 42,
    author: 'antigravity',
    topic: 'DM antigravity ↔ claudecode',
    directed_to: 'claudecode',
    at: '2026-11-22T09:00:00Z',
    body: 'please check PR #2508',
  })

  let spawnedArgv: readonly string[] = []
  const holdOpen = deferred()
  on('process.spawn', async function* (_$, e) {
    spawnedArgv = e.argv
    // Split one JSON line across two stdout chunks to verify line buffering.
    yield { stream: 'stdout', text: payload.slice(0, 30) }
    yield { stream: 'stdout', text: payload.slice(30) + '\n' }
    await holdOpen.promise
    return { value: { code: 0, signal: null } }
  })

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await gotSubmit.promise
  holdOpen.resolve()
  await $.session.end({ reason: 'other' })

  expect(spawnedArgv).toEqual(['C:/fake/agora.exe', 'tail', '--agent', 'claudecode'])
  expect(toasts).toEqual(['DM from antigravity: please check PR #2508'])
  expect(submitted).toHaveLength(1)
  expect(submitted[0]).toContain('please check PR #2508')
  expect(submitted[0]).toContain('send_dm(to_agent="antigravity"')
})

test('interrupted prompt retries on turn.complete', async ($, on) => {
  mock.env(on, { AGORA_AGENT: 'claudecode', AGORA_EXE: 'C:/fake/agora.exe', AGORA_DB: '' })
  mock.clock(on)
  on('session.start', () => ({ cwd: '/work' }))
  on('session.end', () => ({ sessionId: 's1' }))
  on('command.register', () => ({ value: undefined }))
  on('ui.toast', () => ({ value: undefined }))
  on('turn.complete', () => ({ text: '' }))

  let attempts = 0
  const firstAttempt = deferred()
  const secondAttempt = deferred()
  on('prompt.submit', (_$, e) => {
    attempts += 1
    if (attempts === 1) {
      firstAttempt.resolve()
      return { drop: 'the session was stopped and nobody has sent input since' }
    }
    secondAttempt.resolve()
    return { text: e.text }
  })

  const payload = JSON.stringify({
    seq: 99,
    kind: 'dm',
    thread: 7,
    msg: 99,
    author: 'antigravity',
    topic: 'DM',
    directed_to: 'claudecode',
    at: '2026-11-22T09:05:00Z',
    body: 'retry me',
  })

  const holdOpen = deferred()
  on('process.spawn', async function* () {
    yield { stream: 'stdout', text: payload + '\n' }
    await holdOpen.promise
    return { value: { code: 0, signal: null } }
  })

  await $.session.start({ surface: 'terminal', isInteractive: true, cwd: '/work' })
  await firstAttempt.promise
  // Let the mod's submitPrompt() receive the { drop } return and enqueue it.
  await new Promise((r) => setTimeout(r, 20))
  expect(attempts).toBe(1)

  await $.turn.complete({ turnId: 't1', answer: 'ok', durationMs: 10, isAborted: false, usage: null })
  await secondAttempt.promise
  holdOpen.resolve()
  await $.session.end({ reason: 'other' })
  expect(attempts).toBe(2)
})

test('non-interactive -p session does not spawn agora tail, while Desktop session.attach does', async ($, on) => {
  mock.env(on, { AGORA_AGENT: 'claudecode', AGORA_EXE: 'C:/fake/agora.exe', AGORA_DB: '' })
  mock.clock(on)
  on('session.start', () => ({ cwd: '/work' }))
  on('session.attach', (_$, e) => ({ clientId: e.clientId }))
  on('session.end', () => ({ sessionId: 's2' }))
  on('session.surfaces', () => ({ value: [] }))
  on('command.register', () => ({ value: undefined }))
  on('ui.toast', () => ({ value: undefined }))

  const submitted: string[] = []
  const gotSubmit = deferred()
  on('prompt.submit', (_$, e) => {
    submitted.push(e.text)
    gotSubmit.resolve()
    return { text: e.text }
  })

  const payload = JSON.stringify({
    seq: 105,
    kind: 'dm',
    thread: 61,
    msg: 105,
    author: 'antigravity',
    topic: 'DM',
    directed_to: 'claudecode',
    at: '2026-11-22T10:50:00Z',
    body: 'desktop wake test',
  })

  let spawned = false
  const holdOpen = deferred()
  on('process.spawn', async function* () {
    spawned = true
    yield { stream: 'stdout', text: payload + '\n' }
    await holdOpen.promise
    return { value: { code: 0, signal: null } }
  })

  await $.session.start({ surface: null, isInteractive: false, cwd: '/work' })
  expect(spawned).toBe(false)

  await $.session.attach({ surface: 'desktop', clientId: 'desktop:default' })
  await gotSubmit.promise
  holdOpen.resolve()
  await $.session.end({ reason: 'other' })

  expect(spawned).toBe(true)
  expect(submitted).toHaveLength(1)
  expect(submitted[0]).toContain('desktop wake test')
})

test('/agora-notify toggles paused and active state', async ($, on) => {
  mock.env(on, { AGORA_AGENT: 'claudecode', AGORA_EXE: 'C:/fake/agora.exe', AGORA_DB: '' })
  mock.clock(on)
  on('session.start', () => ({ cwd: '/work' }))
  on('session.end', () => ({ sessionId: 's3' }))
  on('session.surfaces', () => ({ value: [] }))
  on('command.register', () => ({ value: undefined }))
  const holdOpen = deferred()
  on('process.spawn', async function* () {
    await holdOpen.promise
    return { value: { code: 0, signal: null } }
  })

  await $.session.start({ surface: null, isInteractive: false, cwd: '/work' })

  const off = await $.command.run({ command: 'agora-notify', args: 'off' })
  expect(off.text).toContain('paused')
  const status = await $.command.run({ command: 'agora-notify', args: 'status' })
  expect(status.text).toContain('paused')
  const backOn = await $.command.run({ command: 'agora-notify', args: 'on' })
  expect(backOn.text).toContain('active')
  holdOpen.resolve()
  await $.session.end({ reason: 'other' })
})
