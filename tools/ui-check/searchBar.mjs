/**
 * 搜索栏交互回归（真实鼠标事件，走 Chrome DevTools Protocol）。
 *
 * 覆盖重点是「点红点弹卡片 / 点灰点切模式不该把搜索区整块收起」这条链路 ——
 * 它曾经因为 `focusout` 里用了 `queueMicrotask` 而失效（详见 README
 * 「搜索模式圆点与焦点处理」）。
 *
 * 用法：
 *   pnpm dev:web                                   # 先把前端跑起来
 *   node tools/ui-check/searchBar.mjs              # 复用已在 9333 上的 Chrome
 *   node tools/ui-check/searchBar.mjs --chrome "C:\Program Files\Google\Chrome\Application\chrome.exe"
 *                                                  # 自己拉起无头 Chrome，跑完关掉
 *
 * 参数：--url <页面地址>  --cdp <http://127.0.0.1:9333>  --chrome <浏览器 exe>
 * 只用 Node 内置能力（fetch / WebSocket / child_process），不需要装依赖。
 */
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const args = process.argv.slice(2);
const argOf = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 && args[index + 1] ? args[index + 1] : fallback;
};

const PAGE = argOf('--url', 'http://127.0.0.1:5173/#/search');
const CDP = argOf('--cdp', 'http://127.0.0.1:9333');
const CHROME = argOf('--chrome', '');

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

let chrome;
let profile;
let failures = 0;

function check(name, condition, detail) {
  if (condition) {
    console.log(`✔ ${name}`);
    return;
  }
  failures += 1;
  console.log(`✖ ${name}`);
  if (detail !== undefined) console.log(`  实际状态：${JSON.stringify(detail)}`);
}

async function waitForCdp() {
  for (let i = 0; i < 40; i += 1) {
    try {
      const res = await fetch(`${CDP}/json/version`);
      if (res.ok) return;
    } catch {
      // 还没起来
    }
    await sleep(250);
  }
  throw new Error(`连不上 CDP：${CDP}（加 --chrome 让它自己开浏览器，或手动开一个带 --remote-debugging-port 的 Chrome）`);
}

async function openPage() {
  const targets = await (await fetch(`${CDP}/json/list`)).json();
  const target = targets.find((entry) => entry.type === 'page');
  if (!target) throw new Error('CDP 里没有可用的页面');

  const ws = new WebSocket(target.webSocketDebuggerUrl);
  const pending = new Map();
  let id = 0;
  ws.addEventListener('message', (event) => {
    const message = JSON.parse(event.data);
    const waiter = message.id && pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) waiter.reject(new Error(JSON.stringify(message.error)));
    else waiter.resolve(message.result);
  });
  await new Promise((resolve, reject) => {
    ws.addEventListener('open', resolve, { once: true });
    ws.addEventListener('error', reject, { once: true });
  });

  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const mid = ++id;
    pending.set(mid, { resolve, reject });
    ws.send(JSON.stringify({ id: mid, method, params }));
  });

  const evaluate = async (expression) => {
    const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };

  await send('Page.enable');
  await send('Runtime.enable');
  await send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 720, deviceScaleFactor: 1, mobile: false });
  await send('Page.navigate', { url: `${PAGE}?ui-check=${Date.now()}` });
  for (let i = 0; i < 60; i += 1) {
    if (await evaluate(`!!document.querySelector('.search-control')`)) break;
    await sleep(200);
  }
  await sleep(500);

  const state = () => evaluate(`(() => {
    const form = document.querySelector('.search-control');
    const input = document.querySelector('.search-capsule input');
    return {
      expanded: form.classList.contains('show-controls'),
      picker: !!document.querySelector('.mode-picker'),
      mode: input ? (input.className.includes('link-field') ? 'link' : 'song') : null,
      inputFocused: document.activeElement === input,
    };
  })()`);

  const box = (selector, index = 0) => evaluate(`(() => {
    const el = document.querySelectorAll('${selector}')[${index}];
    if (!el) return null;
    const rect = el.getBoundingClientRect();
    return { cx: rect.left + rect.width / 2, cy: rect.top + rect.height / 2, left: rect.left };
  })()`);

  const dotBoxes = () => evaluate(`[...document.querySelectorAll('.mode-button')]
    .map((el) => { const r = el.getBoundingClientRect(); return { cx: r.left + r.width / 2, cy: r.top + r.height / 2 }; })`);

  const clickAt = async (x, y, hold = 0) => {
    await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'none', buttons: 0 });
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount: 1 });
    if (hold) await sleep(hold);
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', buttons: 0, clickCount: 1 });
    await sleep(300);
    return state();
  };

  return { state, box, dotBoxes, clickAt, evaluate, close: () => ws.close() };
}

async function run() {
  if (CHROME) {
    if (!existsSync(CHROME)) throw new Error(`找不到浏览器：${CHROME}`);
    profile = mkdtempSync(join(tmpdir(), 'musicplayer-ui-check-'));
    const port = new URL(CDP).port || '9333';
    chrome = spawn(CHROME, [
      '--headless=new',
      `--remote-debugging-port=${port}`,
      `--user-data-dir=${profile}`,
      '--window-size=1280,720',
      '--no-first-run',
      '--no-default-browser-check',
      '--no-proxy-server',
      'about:blank',
    ], { stdio: 'ignore', windowsHide: true });
  }

  await waitForCdp();
  const page = await openPage();

  const capsule = await page.box('.search-capsule');
  const inputPoint = { x: capsule.cx - 120, y: capsule.cy };
  const outside = { x: 240, y: 520 };

  let state = await page.clickAt(inputPoint.x, inputPoint.y);
  check('点输入框：展开并聚焦', state.expanded && state.inputFocused, state);

  let dots = await page.dotBoxes();
  state = await page.clickAt(dots[1].cx, dots[1].cy);
  check('点未选中的灰点：切到链接模式且保持展开', state.mode === 'link' && state.expanded, state);
  check('切完模式焦点回到输入框', state.inputFocused, state);

  dots = await page.dotBoxes();
  state = await page.clickAt(dots[1].cx, dots[1].cy, 150);
  check('点当前模式的红点（按住 150ms 再松）：弹出选模式卡片', state.picker && state.expanded, state);

  state = await page.clickAt(outside.x, outside.y);
  check('卡片打开时点页面：只收起卡片，搜索栏保持展开', state.expanded && !state.picker, state);

  state = await page.clickAt(outside.x, outside.y);
  check('再点页面：收起搜索栏', !state.expanded, state);

  state = await page.clickAt(inputPoint.x, inputPoint.y);
  check('重新点输入框：再次展开', state.expanded, state);

  const row = await page.box('.mode-switch');
  state = await page.clickAt(row.left + 40, row.cy);
  check('点模式圆点那一行的空白：保持展开', state.expanded, state);

  const caption = await page.box('.mode-caption');
  state = await page.clickAt(caption.cx, caption.cy);
  check('点模式说明行：保持展开', state.expanded, state);

  state = await page.clickAt(outside.x, outside.y);
  check('点页面空白：收起', !state.expanded, state);

  const trace = await page.evaluate(`(() => {
    const api = window.__uiTrace;
    if (!api) return null;
    api.on();
    return api.state();
  })()`);
  check('诊断模式可以用 __uiTrace 打开', trace !== null && trace.enabled === true, trace);

  page.close();
}

async function stopChrome() {
  if (chrome) {
    const exited = new Promise((resolve) => chrome.once('exit', resolve));
    chrome.kill();
    await Promise.race([exited, sleep(3000)]);
  }
  if (profile) {
    // Windows 上浏览器刚退出时 profile 目录还可能被占用：删不掉就算了
    try {
      rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
    } catch {
      // 忽略
    }
  }
}

async function main() {
  try {
    await run();
  } catch (error) {
    failures += 1;
    console.error(`✖ 运行失败：${error instanceof Error ? error.message : error}`);
  }
  await stopChrome();
  console.log(failures === 0 ? '\n全部通过' : `\n失败 ${failures} 项`);
  process.exit(failures === 0 ? 0 : 1);
}

void main();
