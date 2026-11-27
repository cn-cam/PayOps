// 生成 GitHub Pages 用的管理后台截图：用本机 Edge/Chrome 的无头模式（CDP 协议，零依赖）登录并逐页截图。
//
//   node scripts/screenshots.mjs                                  # 默认 http://localhost:8090，admin / Admin@123456
//   BASE_URL=http://localhost:5080 node scripts/screenshots.mjs
//
// 截图输出到 docs/assets/screenshots/。运行前先执行 scripts/demo.ps1 准备演示数据。

import { spawn } from 'node:child_process'
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const BASE = (process.env.BASE_URL ?? 'http://localhost:8090').replace(/\/$/, '')
const USERNAME = process.env.ADMIN_USER ?? 'admin'
const PASSWORD = process.env.ADMIN_PASSWORD ?? 'Admin@123456'
const OUT = resolve(dirname(fileURLToPath(import.meta.url)), '../docs/assets/screenshots')
const PORT = 9333
const WIDTH = 1600
const SCALE = 1.5

const browser = [
  process.env.BROWSER,
  'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Microsoft/Edge/Application/msedge.exe',
  'C:/Program Files/Google/Chrome/Application/chrome.exe',
  '/usr/bin/google-chrome',
  '/usr/bin/chromium',
  '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
].find((p) => p && existsSync(p))
if (!browser) throw new Error('找不到 Edge / Chrome，请用环境变量 BROWSER 指定可执行文件路径')

const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

async function api(path, token, body) {
  const res = await fetch(BASE + '/api/admin' + path, {
    method: body ? 'POST' : 'GET',
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  })
  const json = await res.json()
  if (json.code !== 0) throw new Error(`${path}: ${json.message}`)
  return json.data
}

// ---------------------------------------------------------------- 准备：登录并找出要展示的数据
const login = await api('/auth/login', null, { username: USERNAME, password: PASSWORD })
const token = login.token
const channels = await api('/channels/all', token)
const channel = channels.find((c) => c.code === 'official') ?? channels[0]
const subs = (await api('/subscriptions?pageSize=50', token)).items
const sub = subs.find((s) => s.renewCount > 0 && s.status === 1) ?? subs[0]

const clickText = (text) => `(() => { const el = [...document.querySelectorAll('button, a, span, .el-link')].find(e => e.textContent.trim() === ${JSON.stringify(text)}); if (el) el.click(); return !!el })()`
const scrollTo = (pattern) => `(() => { const el = [...document.querySelectorAll('h1,h2,h3,h4,div,span')].find(e => e.children.length === 0 && ${pattern}.test(e.textContent)); if (el) el.scrollIntoView({ block: 'start' }); return !!el })()`

const shots = [
  { name: 'dashboard', path: '/dashboard', height: 1000 },
  { name: 'cluster', path: '/dashboard', height: 620, run: [scrollTo('/在线节点|集群节点|集群/')] },
  channel && { name: 'bindings', path: `/channels/${channel.id}/bindings`, height: 900, run: [clickText('¥100.00'), clickText('开始模拟')], wait: 1500 },
  { name: 'passages', path: '/passages', height: 900 },
  { name: 'orders', path: '/orders', height: 900 },
  { name: 'test-order', path: '/orders/test', height: 900 },
  { name: 'subscriptions', path: '/subscriptions', height: 900 },
  sub && { name: 'subscription-detail', path: '/subscriptions', height: 900, run: [clickText(sub.subNo)], wait: 1500 },
  { name: 'recon', path: '/recon', height: 800 },
  { name: 'recon-diffs', path: '/recon?tab=diffs&status=all', height: 800 },
].filter(Boolean)

// ---------------------------------------------------------------- 启动无头浏览器并连接 CDP
const profile = mkdtempSync(join(tmpdir(), 'payops-shots-'))
const child = spawn(browser, [
  '--headless=new', `--remote-debugging-port=${PORT}`, `--user-data-dir=${profile}`,
  '--hide-scrollbars', '--no-first-run', '--disable-gpu', `--window-size=${WIDTH},900`, 'about:blank',
], { stdio: 'ignore' })

try {
  let target
  for (let i = 0; i < 50 && !target; i++) {
    await sleep(200)
    try {
      const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json()
      target = list.find((t) => t.type === 'page')
    } catch { /* 浏览器尚未就绪 */ }
  }
  if (!target) throw new Error('无头浏览器启动失败')

  const ws = new WebSocket(target.webSocketDebuggerUrl)
  await new Promise((ok, fail) => { ws.onopen = ok; ws.onerror = fail })
  let seq = 0
  const pending = new Map()
  const listeners = new Map()
  ws.onmessage = (ev) => {
    const msg = JSON.parse(ev.data)
    if (msg.id && pending.has(msg.id)) {
      const { ok, fail } = pending.get(msg.id)
      pending.delete(msg.id)
      msg.error ? fail(new Error(msg.error.message)) : ok(msg.result)
    } else if (msg.method && listeners.has(msg.method)) {
      listeners.get(msg.method)(msg.params)
      listeners.delete(msg.method)
    }
  }
  const send = (method, params = {}) => new Promise((ok, fail) => { const id = ++seq; pending.set(id, { ok, fail }); ws.send(JSON.stringify({ id, method, params })) })
  const once = (method) => new Promise((ok) => listeners.set(method, ok))
  const goto = async (url) => { const loaded = once('Page.loadEventFired'); await send('Page.navigate', { url }); await Promise.race([loaded, sleep(15000)]) }
  const evaluate = (expression) => send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true })

  await send('Page.enable')
  await send('Runtime.enable')
  await send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: 900, deviceScaleFactor: SCALE, mobile: false })

  // 先打开登录页以取得同源的 localStorage，再写入登录态
  await goto(BASE + '/login')
  await evaluate(`localStorage.setItem('payops_admin_token', ${JSON.stringify(token)});
    localStorage.setItem('payops_admin_token_expires', String(Date.now() + ${Number(login.expiresIn)} * 1000));
    localStorage.setItem('payops_admin_user', ${JSON.stringify(JSON.stringify(login.user))});`)

  mkdirSync(OUT, { recursive: true })
  for (const shot of shots) {
    await send('Emulation.setDeviceMetricsOverride', { width: WIDTH, height: shot.height, deviceScaleFactor: SCALE, mobile: false })
    await goto(BASE + shot.path)
    await sleep(2000)
    for (const script of shot.run ?? []) {
      const r = await evaluate(script)
      if (r.result?.value === false) console.warn(`  [${shot.name}] 未找到要操作的元素：${script.slice(0, 80)}…`)
      await sleep(500)
    }
    await sleep(shot.wait ?? 600)
    const { data } = await send('Page.captureScreenshot', { format: 'png' })
    writeFileSync(join(OUT, shot.name + '.png'), Buffer.from(data, 'base64'))
    console.log(`  ${shot.name}.png`)
  }
  ws.close()
} finally {
  child.kill()
  await sleep(500)
  try { rmSync(profile, { recursive: true, force: true }) } catch { /* 浏览器仍在释放文件，忽略 */ }
}
console.log('截图已输出到 ' + OUT)
