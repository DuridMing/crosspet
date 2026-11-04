#!/usr/bin/env node
// C# shell + WebView2: cold start, CPU and memory per pose, on the real app (MC-39). Windows only.
//   node maid/bench/webbench.js --exe <CrossPet.exe> [--disable-gpu] [--seconds 8] [--startups 3] [--out file.json]
// Uses a temp data dir and a fake Claude config, so real settings are not touched.
// CPU and memory are the sum over the app's process tree. Time spent in DWM and on the GPU is not counted.
'use strict'
const { spawn, spawnSync } = require('node:child_process')
const fs = require('node:fs')
const os = require('node:os')
const path = require('node:path')

const arg = (name, fallback) => {
  const i = process.argv.indexOf('--' + name)
  return i < 0 ? fallback : process.argv[i + 1]
}
const flag = name => process.argv.includes('--' + name)
const EXE = arg('exe')
if (!EXE) { console.error('--exe is required'); process.exit(2) }
const SECONDS = Number(arg('seconds', 8))
const STARTUPS = Number(arg('startups', 3))
const PORT = Number(arg('port', 9334))
const GPU_OFF = flag('disable-gpu')
const POSES = arg('poses', 'idle,writing,reading,proud,sleeping').split(',')
const sleep = ms => new Promise(r => setTimeout(r, ms))

const root = fs.mkdtempSync(path.join(os.tmpdir(), 'crosspet-webbench-'))
const claudeDir = path.join(root, 'claude')
fs.mkdirSync(claudeDir)
fs.writeFileSync(path.join(claudeDir, 'settings.json'), '{}\n')

function launch(dataDir) {
  const browserArgs = [`--remote-debugging-port=${PORT}`, ...(GPU_OFF ? ['--disable-gpu'] : [])].join(' ')
  return spawn(EXE, [], {
    cwd: path.dirname(EXE), stdio: 'ignore',
    env: { ...process.env, CROSSPET_DATA_DIR: dataDir, CLAUDE_CONFIG_DIR: claudeDir, CODEX_HOME: path.join(root, 'codex'),
      WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: browserArgs },
  })
}
async function stopApp(child) {
  spawnSync('taskkill', ['/T', '/F', '/PID', String(child.pid)], { stdio: 'ignore' })
  await sleep(1500)
}

async function connect(timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs
  let target
  while (Date.now() < deadline && !target) {
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json`)).json()
      target = list.find(t => t.type === 'page' && t.url.includes('index.html'))
    } catch {}
    if (!target) await sleep(50)
  }
  if (!target) throw new Error('pet page not found')
  const ws = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
  let id = 0
  const pending = new Map()
  ws.onmessage = e => {
    const m = JSON.parse(e.data)
    if (pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id) }
  }
  const evaluate = expr => new Promise((res, rej) => {
    const n = ++id
    pending.set(n, m => {
      const r = m.result
      if (m.error || r.exceptionDetails) rej(new Error(JSON.stringify(m.error || r.exceptionDetails)))
      else res(r.result.value)
    })
    ws.send(JSON.stringify({ id: n, method: 'Runtime.evaluate', params: { expression: expr, returnByValue: true } }))
  })
  return { evaluate, close: () => ws.close() }
}

async function timeStartup(dataDir) {
  const t0 = performance.now()
  const child = launch(dataDir)
  const c = await connect()
  while (!(await c.evaluate(`typeof manifest !== 'undefined' && !!manifest && !!pose`).catch(() => false))) await sleep(20)
  const ms = performance.now() - t0
  c.close()
  await stopApp(child)
  return Math.round(ms)
}

// One query gives CPU time and memory for every candidate. The tree is walked from the app's pid,
// so other programs' WebView2 processes are excluded.
function sampleTree(rootPid) {
  const ps = `Get-CimInstance Win32_Process -Filter "Name='CrossPet.exe' OR Name='msedgewebview2.exe'" | ` +
    `Select-Object Name,ProcessId,ParentProcessId,KernelModeTime,UserModeTime,WorkingSetSize,PrivatePageCount | ConvertTo-Json -Compress`
  const r = spawnSync('powershell.exe', ['-NoProfile', '-Command', ps], { encoding: 'utf8' })
  const rows = [].concat(JSON.parse(r.stdout || '[]'))
  const members = new Set([rootPid])
  for (let changed = true; changed;) {
    changed = false
    for (const p of rows) if (!members.has(p.ProcessId) && members.has(p.ParentProcessId)) { members.add(p.ProcessId); changed = true }
  }
  const tree = rows.filter(p => members.has(p.ProcessId))
  return {
    at: performance.now(),
    cpu: new Map(tree.map(p => [p.ProcessId, (Number(p.KernelModeTime) + Number(p.UserModeTime)) / 1e4])),   // ms
    shell: tree.find(p => p.ProcessId === rootPid),
    privateMb: tree.reduce((s, p) => s + Number(p.PrivatePageCount), 0) / 1e6,
    workingSetMb: tree.reduce((s, p) => s + Number(p.WorkingSetSize), 0) / 1e6,
    processes: tree.length,
  }
}
function cpuPercent(a, b, only) {
  let used = 0
  for (const [pid, ms] of b.cpu) if (only === undefined || pid === only) used += ms - (a.cpu.get(pid) || 0)
  return used / (b.at - a.at) * 100
}

// Whole-machine load during a sample, so a noisy reading can be told apart from a real cost.
function systemTimes() {
  let idle = 0, total = 0
  for (const { times } of os.cpus()) { idle += times.idle; total += times.user + times.nice + times.sys + times.idle + times.irq }
  return { idle, total }
}

async function main() {
  const coldDir = path.join(root, 'cold')
  const firstRunMs = await timeStartup(coldDir)
  const warmMs = []
  for (let i = 0; i < STARTUPS; i++) warmMs.push(await timeStartup(coldDir))

  const child = launch(coldDir)
  const c = await connect()
  while (!(await c.evaluate(`typeof manifest !== 'undefined' && !!manifest && !!pose`).catch(() => false))) await sleep(50)
  await sleep(6000)
  // Same switch the developer console uses: stops idle behaviours and greetings from changing the pose mid-sample.
  await c.evaluate(`devActive = true; say(''); 0`)
  const poses = {}
  for (const p of POSES) {
    // noteRest: setPose does nothing when the pose is already showing, so arm the rest timer explicitly.
    await c.evaluate(`clearTimeout(revertTimer); clearTimeout(pending); setPose(${JSON.stringify(p)}, true); noteRest(pose); 0`)
    await sleep(1500)   // let the transition finish
    // Idle and sleeping only slow their effects down after a delay (0 and 5 fps): sample the settled state.
    for (let i = 0; i < 300 && await c.evaluate(`RESTING.has(pose) && !fxResting`); i++) await sleep(100)
    if (await c.evaluate(`RESTING.has(pose) && !fxResting`)) throw new Error(`pose ${p} never settled`)
    // The app pauses every animation while it is concealed (a fullscreen program, a locked screen):
    // numbers taken then look great and mean nothing, so refuse them.
    const assertVisible = async () => {
      if (await c.evaluate('pageHidden')) throw new Error('the pet is concealed (fullscreen program or locked screen); close it and measure again')
    }
    await assertVisible()
    const a = sampleTree(child.pid), sysA = systemTimes()
    await sleep(SECONDS * 1000)
    const b = sampleTree(child.pid), sysB = systemTimes()
    await assertVisible()
    const shown = await c.evaluate('pose')
    if (shown !== p) throw new Error(`pose changed during sample: wanted ${p}, got ${shown}`)
    poses[p] = { cpu_pct_of_one_core: Number(cpuPercent(a, b).toFixed(1)), shell_cpu_pct: Number(cpuPercent(a, b, child.pid).toFixed(1)),
      fx_fps: await c.evaluate('fxFps'), system_busy_pct: Number((100 * (1 - (sysB.idle - sysA.idle) / (sysB.total - sysA.total))).toFixed(1)),
      private_mb: Math.round(b.privateMb),
      working_set_mb: Math.round(b.workingSetMb), processes: b.processes }
  }
  c.close()
  await stopApp(child)

  const report = { date: new Date().toISOString(), cpu: os.cpus()[0]?.model, cores: os.cpus().length, gpu: GPU_OFF ? 'disabled' : 'default',
    seconds_per_pose: SECONDS, first_run_ms: firstRunMs, warm_start_ms: warmMs, poses }
  const text = JSON.stringify(report, null, 1)
  console.log(text)
  const out = arg('out')
  if (out) { fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true }); fs.writeFileSync(out, text) }
}

main().catch(e => { console.error(e); spawnSync('taskkill', ['/F', '/IM', 'CrossPet.exe'], { stdio: 'ignore' }); process.exitCode = 1 })
  .finally(() => { try { fs.rmSync(root, { recursive: true, force: true }) } catch {} })
