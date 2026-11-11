'use strict';
/**
 * Clash Deck - 本地代理内核控制台
 * 零依赖：仅用 Node 内置模块。给 mihomo/clash 内核套一层自己的 Web 控制台。
 */

const http = require('http');
const fs = require('fs');
const path = require('path');
const net = require('net');
const https = require('https');
const crypto = require('crypto');
const os = require('os');
const { spawn } = require('child_process');

const ROOT = __dirname;
const DATA = path.join(ROOT, 'data');
const PROFILES = path.join(DATA, 'profiles');
const RUNTIME = path.join(DATA, 'runtime');
const LOG_DIR = path.join(DATA, 'logs');
const PUBLIC = path.join(ROOT, 'public');
const CONFIG_FILE = path.join(DATA, 'config.json');
const SUBS_FILE = path.join(DATA, 'subscriptions.json');
const HISTORY_FILE = path.join(DATA, 'history.jsonl');

/** 内置默认测速地址：返回 204 空响应，够小够快 */
const DEFAULT_TEST_URL = 'https://www.gstatic.com/generate_204';

/**
 * 解析订阅返回的 subscription-userinfo。
 * 形如 upload=62; download=4989; total=137438953472; expire=1803472119
 * 注意：这是"拉订阅那一刻"的数值，不是实时用量。
 */
function parseUserinfo(raw) {
  if (!raw) return null;
  const o = {};
  for (const part of String(raw).split(';')) {
    const idx = part.indexOf('=');
    if (idx < 0) continue;
    const k = part.slice(0, idx).trim();
    const v = Number(part.slice(idx + 1).trim());
    if (k && Number.isFinite(v)) o[k] = v;
  }
  if (!o.total || o.total <= 0) return null;
  const used = (o.upload || 0) + (o.download || 0);
  return {
    upload: o.upload || 0,
    download: o.download || 0,
    used,
    total: o.total,
    remain: Math.max(0, o.total - used),
    usedPct: Math.min(100, Math.round((used / o.total) * 1000) / 10),
    expire: o.expire || 0,
  };
}

function subQuota(meta) {
  if (!meta) return null;
  return parseUserinfo(meta.userinfo);
}

/** 测速地址归一化：补协议、去掉首尾空白；非法返回 null */
function normalizeTestUrl(raw) {
  if (!raw) return null;
  let s = String(raw).trim();
  if (!s) return null;
  if (!/^https?:\/\//i.test(s)) s = 'https://' + s;
  try {
    const u = new URL(s);
    if (!u.hostname) return null;
    return u.toString();
  } catch {
    return null;
  }
}

const DEFAULT_SETTINGS = {
  corePath: path.join(__dirname, 'core', 'verge-mihomo.exe'),
  geoSourceDir: path.join(__dirname, 'core', 'resources'),
  sysProxyPath: path.join(__dirname, 'tools', 'sysproxy.exe'),
  webPort: 8787,
  mixedPort: 7897,
  socksPort: 7898,
  httpPort: 7899,
  controllerPort: 9097,
  secret: 'clash-deck-local',
  allowLan: false,
  tunEnable: false,
  activeProfile: '',
  autoStartCore: false,
  autoStartProxy: false,
  autoDelayMinutes: 0,
  subIntervalHours: 24,
  testUrl: DEFAULT_TEST_URL,
  delayTimeout: 5000,
  enhancedRules: true,   // 用本地规则集替换订阅自带的规则（关掉则完全沿用订阅）
  // 带宽测速：都必须经代理跑，否则测的是你家宽带不是代理链路
  speedTestUrl: 'https://speed.cloudflare.com/__down?bytes={bytes}',
  speedUploadUrl: 'https://speed.cloudflare.com/__up',
  speedTestBytes: 5242880,
  logToFile: true,    // 日志落盘
  logConn: true,      // 记录连接日志（含访问域名，属于隐私，可关）
  // 日志文件保留天数：7 / 30 / 0（0 = 永不删除）。
  // 以前固定 7 天，排障想回溯一个月时无从下手；这个值给用户选，写入前过白名单。
  logKeepDays: 7,
  proxyMode: 'smart', // smart=兜底走代理；whitelist=兜底直连，只有列出的走代理
};

for (const d of [DATA, PROFILES, RUNTIME]) fs.mkdirSync(d, { recursive: true });

/** 启动早期出错时记一笔：此时 settings 还没赋值，不能用 pushLog */
function noteStartupError(msg) {
  try {
    fs.mkdirSync(DATA, { recursive: true });
    fs.appendFileSync(path.join(DATA, 'startup-error.log'),
      new Date().toISOString() + ' ' + msg + '\n', 'utf8');
  } catch { /* 连错误都记不下，只能放弃 */ }
}

// 配置损坏时留下的证据。以前只写进 startup-error.log，而没人读那个文件，
// 于是"设置全丢了"这件事永远传不到用户眼前。现在记在内存里，由快照/启动失败提示带出去。
let configRecovery = null;

/** 五个端口键。读侧（loadSettings）与写侧（/api/settings）共用这一份，避免两处清单漂开 */
const PORT_KEYS = ['webPort', 'mixedPort', 'socksPort', 'httpPort', 'controllerPort'];

/**
 * 端口的中文名。界面（`ProfilesView.xaml`）只暴露三个输入框：
 * 面板端口 / 混合端口 / 控制端口。`socksPort` / `httpPort` 只存在于 config.json，
 * 界面上**没有**那两栏 —— 所以报错文案必须区分"用户能改的"和"后台固定的"，
 * 否则会出现"请修改 SOCKS 端口"，而用户去设置页根本找不到这个框
 * （从"看不懂"变成"找不到"，问题没解决）。
 */
const PORT_LABELS = {
  webPort: '面板端口',
  mixedPort: '混合端口',
  socksPort: 'SOCKS 端口',
  httpPort: 'HTTP 端口',
  controllerPort: '控制端口',
};
const portLabel = (k) => PORT_LABELS[k] || k;

/**
 * 端口撞车文案。按"这次请求里带了哪个"判断谁是用户在改的：
 *   两个都能改 → 平铺说"都用了 X"
 *   只有一个是用户改的 → 它做主语，另一个说成"后台固定使用的"
 *   两个都是后台的（只可能来自手改 config.json）→ 指到配置文件
 */
function portConflictMsg(a, b, value, body) {
  const aEditable = body[a] !== undefined;
  const bEditable = body[b] !== undefined;
  const la = `「${portLabel(a)}」`;
  const lb = `「${portLabel(b)}」`;
  if (aEditable && bEditable) return `${la}和${lb}都用了 ${value}，请改掉其中一个`;
  if (aEditable) return `${la}${value} 与后台固定使用的${lb}冲突，请换一个`;
  if (bEditable) return `${lb}${value} 与后台固定使用的${la}冲突，请换一个`;
  return `后台的${la}与${lb}冲突（${value}），请手工修改 data\\config.json`;
}

/**
 * 读侧端口校正的结果：`{ key, value, used }`，只报**第一个**非法项。
 *
 * 以前只有写接口校验端口，读侧裸奔。config.json 被手改坏之后的实际后果（都实测过）：
 *   `"abc"`  → `listen("abc")` 抛未捕获异常 + 栈，没有 [[CD_FATAL]]，界面上只有"启动即退出"
 *   `70000`  → 未捕获 RangeError，同上
 *   `0`/`null` → 绑到随机端口，日志打印 `http://127.0.0.1:0`，主程序探 8787 永远失败
 * —— 三种都表现为"后端莫名其妙起不来"，而真实原因是一个数字。
 *
 * 只报第一个是刻意的：一次让用户改一个端口，比一次弹四条"XX 不合法"更有用；
 * 也正好对应单对象契约（数组会诱导后来者往里 push 多个）。
 */
let portFallback = null;

function loadSettings() {
  let s;
  try {
    const raw = JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8'));
    s = { ...DEFAULT_SETTINGS, ...raw };
  } catch (e) {
    // 以前这里静默回退默认值：用户只会发现"设置怎么全没了"，且无法追溯。
    // 现在把坏文件留成 .bad-<时间戳>，并写 startup-error.log。
    // 注意区分两种情形：文件不存在 = 全新安装（正常，不能吓唬人）；
    // 文件在、但读不出来 = 真的损坏（必须让用户知道）。
    let existed = false;
    try { existed = fs.existsSync(CONFIG_FILE); } catch { /* 探测失败按不存在处理 */ }
    let backup = '';
    try {
      if (existed) {
        const bad = CONFIG_FILE + '.bad-' + Date.now();
        fs.renameSync(CONFIG_FILE, bad);
        backup = path.basename(bad);
        noteStartupError('settings 解析失败，已备份为 ' + backup + '：' + (e && e.message));
      }
    } catch { /* 备份失败也要继续启动 */ }
    if (existed) {
      // 连备份都没成功（文件被锁等）时也要留痕，只是没有文件名可报。
      configRecovery = { backup, reason: (e && e.message) || 'parse error' };
    }
    s = { ...DEFAULT_SETTINGS };
  }
  // 端口：读侧就校正。非法值回落默认并记进 portFallback（由 /api/env、/api/snapshot 带出去），
  // 而不是让它在 listen 或 config.yaml 里炸。
  for (const k of PORT_KEYS) {
    const v = validPort(s[k]);
    if (v === null) {
      if (!portFallback) portFallback = { key: k, value: s[k], used: DEFAULT_SETTINGS[k] };
      s[k] = DEFAULT_SETTINGS[k];
    } else {
      s[k] = v;
    }
  }
  // 随安装目录走的路径：配置里可能是旧机器/旧目录的绝对路径，
  // 文件不存在时回退到本程序自带的 tools\，否则换目录安装必坏。
  // 顺手规范化：只接受"绝对路径 + 文件存在"，相对路径一律当非法 ——
  // 相对路径是按进程 cwd 解析的，而 cwd 不可控（服务被谁拉起就变成谁的目录）。
  // 规范化之后，壳侧 `File.Exists(Paths.SystemProxyTool)` 就是唯一且可靠的"有没有工具"判据，
  // 不必再去快照里取（失联态下快照不可信，那样会误禁用开关）。
  if (typeof s.sysProxyPath !== 'string' || !s.sysProxyPath
      || !path.isAbsolute(s.sysProxyPath) || !fs.existsSync(s.sysProxyPath)) {
    const local = path.join(__dirname, 'tools', 'sysproxy.exe');
    if (fs.existsSync(local)) s.sysProxyPath = local;
  }
  // 内核同理：随包自带的 core\verge-mihomo.exe 优先兜底，
  // 这样换机器、或用户删掉原来那份 Clash Verge，程序依然能启动。
  if (!s.corePath || !fs.existsSync(s.corePath)) {
    const local = path.join(__dirname, 'core', 'verge-mihomo.exe');
    if (fs.existsSync(local)) s.corePath = local;
  }
  if (!s.geoSourceDir || !fs.existsSync(s.geoSourceDir)) {
    const local = path.join(__dirname, 'core', 'resources');
    if (fs.existsSync(local)) s.geoSourceDir = local;
  }
  return s;
}
function saveSettings(s) {
  // 必须原子写：直接覆盖时一旦进程被杀/断电，文件会截断成半截 JSON，
  // 下次启动 loadSettings 解析失败就会静默回落默认值 —— 用户的端口、订阅、
  // 分流模式、日志开关会一起消失，且没有任何提示。
  atomicWrite(CONFIG_FILE, JSON.stringify(s, null, 2));
}
let settings = loadSettings();
// 老配置里没有/写坏了测速地址，就回落默认值
settings.testUrl = normalizeTestUrl(settings.testUrl) || DEFAULT_TEST_URL;
if (typeof settings.logToFile !== 'boolean') settings.logToFile = true;
if (typeof settings.logConn !== 'boolean') settings.logConn = true;
// 0 是合法值（永不删除），所以不能写成 `!settings.logKeepDays` —— 那会把"永不"改回 7 天
if (!Number.isFinite(settings.logKeepDays) || settings.logKeepDays < 0) settings.logKeepDays = 7;
if (settings.proxyMode !== 'whitelist') settings.proxyMode = 'smart';
if (!Number(settings.delayTimeout) || settings.delayTimeout < 1000) settings.delayTimeout = 5000;

/**
 * 一次性命令：--set-web-port <n>
 *
 * 面板端口被占时后端根本起不来，API 这条路是断的，所以主程序没法走接口改端口。
 * 这个命令把逻辑留在后端：DEFAULT_SETTINGS 的全集只有这里一份，
 * 主程序不需要复制一份默认配置来重建 config.json（复制就一定会漂移）。
 *
 * 配置本来损坏时：loadSettings 已把坏文件改名成 .bad-<时间戳>，这里写出来的是
 * 一份按默认值重建的新配置 —— 所以回执里要带 configBackup，让主程序说清楚
 * "你的旧配置没能读出来，已备份成 xxx，现在是默认设置"。
 */
const setPortIdx = process.argv.indexOf('--set-web-port');
if (setPortIdx >= 0) {
  const want = Number(process.argv[setPortIdx + 1]);
  const others = ['mixedPort', 'socksPort', 'httpPort', 'controllerPort'];
  let err = '';
  if (!Number.isInteger(want) || want < 1 || want > 65535) err = '端口必须是 1-65535 之间的整数';
  else if (others.some((k) => settings[k] === want)) err = `端口 ${want} 已被本程序的其他用途占用`;
  if (err) {
    console.error('[[CD_PORT_SET]]' + JSON.stringify({ ok: false, msg: err }));
    process.exit(2);
  }
  settings.webPort = want;
  saveSettings(settings);
  console.error('[[CD_PORT_SET]]' + JSON.stringify({
    ok: true,
    webPort: want,
    configBackup: configRecovery ? configRecovery.backup : '',
    configRebuilt: !!configRecovery,
  }));
  process.exit(0);
}

// ---------------------------------------------------------------- 订阅元数据

function readSubs() {
  try { return JSON.parse(fs.readFileSync(SUBS_FILE, 'utf8')); } catch { return {}; }
}
function writeSubs(s) {
  // 同 saveSettings：订阅元数据损坏等于丢订阅，必须原子写
  atomicWrite(SUBS_FILE, JSON.stringify(s, null, 2));
}

// ---------------------------------------------------------------- 串行闸门
// 内核启停、运行时配置生成、订阅回写 三件事都会碰 data/runtime/config.yaml 和进程状态。
// 并发跑会出「文件写到一半被内核读」「新进程抢旧进程端口」这类看着像"配置冲突"的故障，
// 所以统一排队：同一时刻只允许一个动作改运行时状态。
let _gate = Promise.resolve();
function withGate(fn) {
  const run = _gate.then(fn, fn);
  _gate = run.then(() => {}, () => {});
  return run;
}

/** 先写临时文件再改名，避免内核读到半截 YAML */
function atomicWrite(file, text) {
  const tmp = file + '.tmp';
  fs.writeFileSync(tmp, text, 'utf8');
  fs.renameSync(tmp, file);
}

async function waitPortFree(port, timeoutMs) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeoutMs) {
    if (!(await portBusy(port))) return true;
    await sleep(120);
  }
  return false;
}

/**
 * 机场按 User-Agent 发不同内容：认得的客户端给完整订阅，不认的返回"空壳"
 * （只有规则没有节点，组里还引用着不存在的 自动选择/故障转移）。
 * 实测同一个地址：clash-verge → 20 个节点；clash-deck/1.0 → 0 个节点。
 * 所以这里按"主流白名单优先"的顺序逐个试，谁先给出真节点就用谁。
 */
const SUB_USER_AGENTS = [
  'clash-verge/v2.4.2',
  'ClashMetaForAndroid/2.11.1.Meta',
  'mihomo/1.19.32',
  'clash-deck/1.0',
];

/** 数一份 Clash 配置里有多少节点：>0 有节点；-1 走 proxy-providers（数量未知但可用）；0 是空壳 */
function countProfileNodes(text) {
  // 注意：这里一律用 [ \t] 而不是 \s —— \s 会吞掉换行，把下一行内容当成冒号后的值，
  // 导致"有节点的配置"被判成空壳（这个坑实测踩过一次）。
  const pm = text.match(/^proxies[ \t]*:[ \t]*(.*)$/m);
  if (pm) {
    const inline = pm[1].trim();
    if (inline.startsWith('[')) {
      const n = (inline.match(/\{/g) || []).length;
      if (n > 0) return n;
    } else if (inline === '') {
      const rest = text.slice(pm.index + pm[0].length);
      const block = rest.split(/^proxy-groups[ \t]*:/m)[0];
      const n = (block.match(/^[ \t]*-[ \t]*(\{|name[ \t]*:)/gm) || []).length;
      if (n > 0) return n;
    }
  }
  const pv = text.search(/^proxy-providers[ \t]*:/m);
  if (pv >= 0) {
    const rest = text.slice(pv).split(/\r?\n/).slice(1).join('\n').split(/^[a-z][a-z0-9-]*[ \t]*:/m)[0];
    if (/^[ \t]{2,}\S+[ \t]*:/m.test(rest)) return -1;
  }
  return 0;
}

/** 依次换 UA 拉订阅，第一个"真有节点"的结果胜出 */
async function fetchProfileText(url) {
  const tried = [];
  let lastText = null;
  for (const ua of SUB_USER_AGENTS) {
    let r;
    try {
      r = await fetch(url, { headers: { 'User-Agent': ua }, signal: AbortSignal.timeout(25000) });
    } catch (e) {
      tried.push(`${ua} → 网络错误`); continue;
    }
    if (!r.ok) { tried.push(`${ua} → HTTP ${r.status}`); continue; }
    const text = await r.text();
    const nodes = countProfileNodes(text);
    if (nodes !== 0) {
      return { ok: true, text, ua, nodes, userinfo: r.headers.get('subscription-userinfo') || '' };
    }
    lastText = text;
    tried.push(`${ua} → 无节点`);
  }
  return {
    ok: false,
    text: lastText,
    tried,
    msg: '订阅里没有任何节点（已换 4 种客户端标识重试）。多半是订阅链接失效、套餐到期或流量用尽，请去机场后台重新复制 Clash 订阅地址。',
  };
}

/** 把内核的 fatal 日志翻成人话，而不是把英文原始日志甩给用户 */
function humanizeCoreError(detail) {
  const d = String(detail || '');
  const g = d.match(/proxy group\[\d+\]:\s*(.+?):\s*'(.+?)' not found/);
  if (g) {
    return `这份订阅的配置是残缺的：策略组「${g[1].trim()}」里引用了「${g[2]}」，但订阅里既没有这个组、也没有任何节点。`
      + '通常是订阅链接失效、套餐到期或流量用尽；请回机场后台重新复制 Clash 订阅地址后再更新一次。';
  }
  const t = d.match(/unsupport proxy type:[ \t]*([^\s"']+)/i);
  if (t) return `内核不认识这个协议类型「${t[1]}」，跳过该节点即可（多半是订阅里混了本内核版本不支持的协议）。`;
  if (/can't download MMDB/i.test(d)) return '缺少地理数据库（geo 文件）且联网下载失败。检查网络，或重新安装一次让安装包自带的 geo 生效。';
  if (/address already in use|bind: /i.test(d)) return '端口被别的程序占用（常见是本机还开着 Clash Verge），先退出它再启动。';
  const tail = d.split(/\r?\n/).filter(Boolean).slice(-1)[0] || d;
  return '配置校验没通过：' + tail;
}

async function updateSubscriptionInner(file) {
  const subs = readSubs();
  const meta = subs[file];
  if (!meta || !meta.url) return { ok: false, msg: '这份配置没有订阅地址（不是订阅导入的）' };
  const target = path.join(PROFILES, file);
  try {
    const got = await fetchProfileText(meta.url);
    if (!got.ok) throw new Error(got.msg);
    const text = got.text;
    const old = fs.existsSync(target) ? fs.readFileSync(target) : null;
    if (old) fs.writeFileSync(target + '.bak', old);
    atomicWrite(target, text);
    meta.lastUpdate = Date.now();
    meta.lastStatus = 'ok';
    const nodeTxt = got.nodes > 0 ? `${got.nodes} 个节点` : '用 provider 提供节点';
    meta.lastMsg = `${(text.length / 1024).toFixed(1)} KB · ${nodeTxt}`;
    meta.lastUa = got.ua;
    meta.userinfo = got.userinfo || meta.userinfo || '';
    writeSubs(subs);
    pushLog('sys', `订阅更新成功 ${file}（${meta.lastMsg}，UA=${got.ua}）`);
    if (settings.activeProfile === file) await reloadRuntimeInner();
    return { ok: true, msg: `已更新 ${file}（${meta.lastMsg}）`, meta };
  } catch (e) {
    meta.lastStatus = 'fail';
    meta.lastMsg = e.message;
    meta.lastTry = Date.now();
    meta.failCount = (Number(meta.failCount) || 0) + 1;
    writeSubs(subs);
    pushLog('sys', `订阅更新失败 ${file}: ${e.message}`);
    return { ok: false, msg: '更新失败：' + e.message };
  }
}

/** 订阅更新要跟内核启停排队，否则刚拉下来的配置会跟启动中的内核抢 config.yaml */
function updateSubscription(file) {
  return withGate(() => updateSubscriptionInner(file));
}

/** 每 10 分钟检查一次，到期才真正拉取 */
function subScheduler() {
  const subs = readSubs();
  const now = Date.now();
  for (const [file, meta] of Object.entries(subs)) {
    if (!meta.url) continue;
    const hours = Number(meta.intervalHours || settings.subIntervalHours || 0);
    if (!hours || hours <= 0) continue;
    const due = !meta.lastUpdate || now - meta.lastUpdate >= hours * 3600 * 1000;
    if (!due) continue;
    // 失败退避：订阅地址失效时以前每 10 分钟重试一次，日志里一直刷失败。
    // 按 1/2/4/8/16 分钟递增，最慢 16 分钟一次。
    const failCount = Math.min(Number(meta.failCount) || 0, 4);
    const backoff = (failCount ? Math.pow(2, failCount - 1) : 0) * 60 * 1000;
    if (backoff && meta.lastTry && now - meta.lastTry < backoff) continue;
    updateSubscription(file);
  }
}
setInterval(subScheduler, 10 * 60 * 1000);
setTimeout(subScheduler, 60 * 1000);

// ---------------------------------------------------------------- 定时测速
// 只看"用户最关心的那组"（第一个非 Selector 组），避免一次把几十个节点全打一遍。
let lastAutoDelay = 0;
async function autoDelayTick() {
  const minutes = Number(settings.autoDelayMinutes || 0);
  if (!minutes || !coreRunning()) return;
  if (Date.now() - lastAutoDelay < minutes * 60 * 1000) return;
  lastAutoDelay = Date.now();
  const snap = await snapshot();
  const g = snap.groups.find((x) => x.type !== 'Selector') || snap.groups[0];
  if (!g) return;
  const target = settings.testUrl || DEFAULT_TEST_URL;
  const timeout = String(settings.delayTimeout || 5000);
  await Promise.all(g.all.slice(0, 60).map((n) =>
    apiJson('GET', `/proxies/${encodeURIComponent(n)}/delay?timeout=${timeout}&url=${encodeURIComponent(target)}`)
  ));
  pushLog('sys', `定时测速完成（${g.name}，${g.all.length} 个节点）`);
}
setInterval(autoDelayTick, 60 * 1000);

// ---------------------------------------------------------------- 系统代理

function runExe(exe, args, timeoutMs) {
  return new Promise((resolve) => {
    let out = '';
    let err = '';
    let done = false;
    const p = spawn(exe, args, { windowsHide: true });
    const timer = setTimeout(() => {
      if (!done) { try { p.kill(); } catch {} }
    }, timeoutMs || 8000);
    p.stdout.on('data', (b) => { out += b.toString('utf8'); });
    p.stderr.on('data', (b) => { err += b.toString('utf8'); });
    p.on('error', (e) => { done = true; clearTimeout(timer); resolve({ ok: false, code: -1, out, err: e.message }); });
    p.on('exit', (code) => {
      done = true; clearTimeout(timer);
      resolve({ ok: code === 0, code, out: out.trim(), err: err.trim() });
    });
  });
}

async function sysProxyStatus() {
  if (!fs.existsSync(settings.sysProxyPath)) {
    return { ok: false, msg: '系统代理工具未编译：' + settings.sysProxyPath };
  }
  const r = await runExe(settings.sysProxyPath, ['status']);
  if (!r.ok) return { ok: false, msg: r.err || '读取失败' };
  try {
    const st = JSON.parse(r.out);
    st.ok = true;
    st.pointsToOurs = st.enabled && st.server === `127.0.0.1:${settings.mixedPort}`;
    return st;
  } catch { return { ok: false, msg: '返回格式异常: ' + r.out }; }
}

async function sysProxySet(enable) {
  if (!fs.existsSync(settings.sysProxyPath)) {
    // reason 是给程序看的（界面按它决定"禁用开关 + 指路 Windows 设置"），msg 是给人看的。
    return { ok: false, reason: 'tool-missing', msg: '系统代理工具未编译：' + settings.sysProxyPath };
  }
  const want = `127.0.0.1:${settings.mixedPort}`;
  const r = await runExe(settings.sysProxyPath, enable ? ['on', want] : ['off']);
  // exe 改完会把 StatusJson 打到 stdout（`on`/`off` 分支都以它收尾），那就是"回显"。
  // 用它判断比再 spawn 一次 status 便宜得多（status 是 2 秒级的冷启动）。
  let echo = null;
  try { echo = r.out ? JSON.parse(r.out) : null; } catch { /* 不是 JSON 就当没有回显 */ }
  _sysProxyCache = { at: 0, value: null };   // 状态刚变过，缓存的旧值立刻作废
  pushLog('sys', `系统代理 ${enable ? '开' : '关'} → ${r.ok ? '成功' : '失败: ' + (r.err || r.code)}`);
  let recorded = false;
  if (r.ok && enable) {
    // A5：只记录**回显确认过**的串。
    // 判据是 exe 回显的 server 等于我们要求的那个 —— 不是"我们发了命令"。
    // 这个值唯一的消费者是退出路径（后端已死时判断"注册表里那个残留是不是我们写的"），
    // 所以它必须代表"真的写进去了"，否则退出时会拿着一个从未生效的串去关别人的代理。
    let confirmed = !!(echo && echo.enabled === true && echo.server === want);
    if (!confirmed) {
      const st = await sysProxyStatus();   // stdout 被吞（被安全软件拦等）时用 status 复核一次
      confirmed = !!(st.ok && st.enabled && st.server === want);
    }
    if (confirmed) {
      settings.lastProxyServer = { server: want, host: os.hostname(), at: Date.now() };
      try {
        saveSettings(settings);
        recorded = true;
      } catch (e) {
        pushLog('sys', '记录 lastProxyServer 失败：' + (e && e.message));
      }
    } else {
      pushLog('sys', '系统代理已开，但回显与请求不一致，本次不记录 lastProxyServer'
        + '（退出时的残留判据会落到保守分支）');
    }
  }
  return {
    ok: r.ok,
    reason: r.ok ? '' : 'exec-failed',
    msg: r.ok ? (enable ? '系统代理已开启' : '系统代理已关闭') : (r.err || '操作失败'),
    recorded,
  };
}

// ---------------------------------------------------------------- 权限探测

let _isAdmin = null;
async function isAdmin() {
  if (_isAdmin !== null) return _isAdmin;
  // net session 需要管理员；非管理员返回非 0
  const r = await runExe('net', ['session'], 4000);
  _isAdmin = r.code === 0;
  return _isAdmin;
}

// ---------------------------------------------------------------- 开机自启

const STARTUP_DIR = path.join(process.env.APPDATA || '', 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup');
const AUTOSTART_VBS = path.join(STARTUP_DIR, 'ClashDeck.vbs');

// 快照每 2 秒读一次这个值（一天 4.3 万次文件系统调用），加个短缓存：
// 用户在启动文件夹里手动增删，5 秒内也能反映出来。
let _autostartCache = null;
let _autostartAt = 0;

/** 开机自启的目标：主程序本体（以前指向 start.cmd，而那个文件从来不存在） */
function autostartTargetPath() {
  return path.join(ROOT, 'ClashDeck.exe');
}

/** 从 vbs 文本里取出启动目标；取不到返回 ''（老版本写的 start.cmd 就取不到） */
function autostartTargetIn(vbsText) {
  const m = /["']([^"'\r\n]+\.exe)["']/i.exec(String(vbsText || ''));
  return m ? m[1] : '';
}

function autostartEnabled() {
  if (_autostartCache !== null && Date.now() - _autostartAt < 5000) return _autostartCache;
  _autostartCache = fs.existsSync(AUTOSTART_VBS);
  // 文件在 ≠ 开机真能起来：老版本指向 start.cmd（从来不存在），用户也可能挪了安装目录。
  // 那种情况下开关显示"已开启"是假状态，用户下次开机才发现没起来。所以读一次 vbs、校验目标存在。
  // 有 5 秒缓存，快照每 2 秒读一次也不会变成每 2 秒一次磁盘读。
  if (_autostartCache) {
    try {
      const t = autostartTargetIn(fs.readFileSync(AUTOSTART_VBS, 'utf8'));
      if (!t || !fs.existsSync(t)) _autostartCache = false;
    } catch { _autostartCache = false; }
  }
  _autostartAt = Date.now();
  return _autostartCache;
}

function autostartSet(enable) {
  _autostartCache = null;   // 立刻失效，别让界面显示 5 秒前的旧状态
  if (enable) {
    const exe = autostartTargetPath();
    // 先校验再写：目标不存在时写出来的启动项永远起不来，不如当场拒绝
    if (!fs.existsSync(exe)) {
      return { ok: false, msg: '找不到主程序，无法设置开机自启：' + exe, path: AUTOSTART_VBS };
    }
    const vbs = [
      'Set sh = CreateObject("WScript.Shell")',
      `sh.Run """${exe}"" silent", 0, False`,
      '',
    ].join('\r\n');
    try {
      fs.mkdirSync(STARTUP_DIR, { recursive: true });
      fs.writeFileSync(AUTOSTART_VBS, vbs, 'utf8');
    } catch (e) {
      return { ok: false, msg: '写入启动项失败：' + (e && e.message), path: AUTOSTART_VBS };
    }
    // 落盘后再校验一次（写入可能被安全软件拦截）：vbs 真在 + 它指向的目标真在。
    // 任一不满足就删掉刚写的文件 —— 宁可"没开成"并告诉用户，也不留一个不会启动的启动项。
    const written = autostartTargetIn(vbs);
    if (!fs.existsSync(AUTOSTART_VBS) || !written || !fs.existsSync(written)) {
      try { fs.unlinkSync(AUTOSTART_VBS); } catch { /* 已经没了 */ }
      return { ok: false, msg: '启动项写入后校验失败，已回滚', path: AUTOSTART_VBS };
    }
    return { ok: true, msg: '已加入开机自启（Startup 文件夹，静默启动）', path: AUTOSTART_VBS };
  }
  try { fs.unlinkSync(AUTOSTART_VBS); } catch { /* 不存在就算了 */ }
  return { ok: true, msg: '已取消开机自启', path: AUTOSTART_VBS };
}

// ---------------------------------------------------------------- 流量统计（落盘）
// 每 60 秒把这一分钟的增量写进 data/usage.jsonl，重启不丢。
// 内核重启后 uploadTotal/downloadTotal 会归零，所以用"变小就重置基线"的方式处理跳变。

const USAGE_FILE = path.join(DATA, 'usage.jsonl');
let usageBase = null;   // { up, down }
let usageLastAt = 0;

let _usageBusy = false;

function appendUsage() {
  if (_usageBusy || !coreRunning()) { if (!coreRunning()) usageBase = null; return; }
  _usageBusy = true;
  apiJson('GET', '/connections').then((r) => {
    if (!r.ok || !r.data) return;
    const up = r.data.uploadTotal || 0;
    const down = r.data.downloadTotal || 0;
    if (!usageBase || up < usageBase.up || down < usageBase.down) {
      usageBase = { up, down };   // 首次或内核重启，先记基线不写增量
      return;
    }
    const dUp = up - usageBase.up;
    const dDown = down - usageBase.down;
    usageBase = { up, down };
    if (dUp <= 0 && dDown <= 0) return;
    try {
      fs.appendFileSync(USAGE_FILE, JSON.stringify({ t: Date.now(), up: dUp, down: dDown }) + '\n', 'utf8');
    } catch (e) { pushLog('sys', '写流量统计失败: ' + e.message); }
    usageLastAt = Date.now();
    // 这个文件是只增不减的流水，长期跑会越来越大（readUsage 每次还要全量读）。
    // 每天最多裁一次，只保留最近 180 天，且文件够小就完全跳过。
    if (Date.now() - _usageTrimAt > 86400000) {
      _usageTrimAt = Date.now();
      trimUsage();
    }
  }).catch((e) => {
    pushLog('sys', '流量统计采集失败：' + (e && e.message ? e.message : e));
  }).finally(() => { _usageBusy = false; });
}

let _usageTrimAt = 0;

/** 裁剪流量流水：小于 512KB 不动，超过则丢掉 180 天前的记录 */
function trimUsage() {
  try {
    if (!fs.existsSync(USAGE_FILE)) return;
    if (fs.statSync(USAGE_FILE).size < 512 * 1024) return;
    const since = Date.now() - 180 * 86400000;
    const lines = fs.readFileSync(USAGE_FILE, 'utf8').split('\n');
    const kept = [];
    let dropped = 0;
    for (const line of lines) {
      if (!line.trim()) continue;
      try {
        if (JSON.parse(line).t >= since) kept.push(line); else dropped++;
      } catch { dropped++; }
    }
    if (!dropped) return;
    atomicWrite(USAGE_FILE, kept.join('\n') + (kept.length ? '\n' : ''));
    pushLog('sys', `流量流水已裁剪：保留最近 180 天，丢弃 ${dropped} 条旧记录`);
  } catch { /* 裁剪失败不影响主流程 */ }
}
setInterval(appendUsage, 60 * 1000);

const dayKey = (ts) => {
  const d = new Date(ts);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
};

function readUsage(days) {
  const byDay = {};
  let total = 0;
  try {
    const lines = fs.readFileSync(USAGE_FILE, 'utf8').split('\n');
    const since = Date.now() - days * 86400000;
    for (const line of lines) {
      if (!line.trim()) continue;
      let o;
      try { o = JSON.parse(line); } catch { continue; }
      if (!o.t || o.t < since) continue;
      const k = dayKey(o.t);
      const d = byDay[k] || (byDay[k] = { day: k, up: 0, down: 0 });
      d.up += o.up || 0;
      d.down += o.down || 0;
      total++;
    }
  } catch { /* 还没文件 */ }
  const list = Object.values(byDay).sort((a, b) => a.day.localeCompare(b.day));
  const today = dayKey(Date.now());
  const month = today.slice(0, 7);
  const sum = (pred) => list.filter(pred).reduce((a, b) => ({ up: a.up + b.up, down: a.down + b.down }), { up: 0, down: 0 });
  return {
    days: list,
    today: sum((d) => d.day === today),
    month: sum((d) => d.day.startsWith(month)),
    samples: total,
    file: USAGE_FILE,
  };
}

// ---------------------------------------------------------------- 节点延迟历史
// 每次测速记一条，用来算成功率（丢包）与趋势。只在内存里，重启即清空。
const delayHist = new Map();   // node -> [{t, delay}]
const DELAY_KEEP = 20;

function recordDelay(node, delay) {
  if (!node) return;
  const arr = delayHist.get(node) || [];
  arr.push({ t: Date.now(), delay: Number(delay) || 0 });
  while (arr.length > DELAY_KEEP) arr.shift();
  delayHist.set(node, arr);
}

function delayStats(node) {
  const arr = delayHist.get(node) || [];
  if (!arr.length) return null;
  const ok = arr.filter((x) => x.delay > 0);
  const sum = ok.reduce((a, b) => a + b.delay, 0);
  return {
    samples: arr.map((x) => x.delay),
    ok: ok.length,
    fail: arr.length - ok.length,
    lossRate: Math.round(((arr.length - ok.length) / arr.length) * 100),
    avg: ok.length ? Math.round(sum / ok.length) : 0,
    min: ok.length ? Math.min(...ok.map((x) => x.delay)) : 0,
    max: ok.length ? Math.max(...ok.map((x) => x.delay)) : 0,
  };
}

// ---------------------------------------------------------------- 流量历史

const history = { samples: [], max: 1800 };
let _tickBusy = false;

/**
 * 每 2 秒采一次累计流量，算成速率画曲线。
 *
 * 这里原来有两个毛病（静态看是"标志泄漏会永久卡死"，实测发现是另一种）：
 *   ① historyTick 里**没有** `_tickBusy` 守卫，但它的 finally 无条件把
 *      `_historyBusy` 置 false —— 也就是说注释写着"上一次没回来也不叠加"，
 *      实际内核一旦挂起，每 2 秒还是会再发一个请求，最多堆到内核恢复为止。
 *   ② appendUsage（每分钟写一次流量流水）和它**共用了同一个标志**。
 *      两个不同频率、不同用途的定时器抢一个布尔量，结果是 appendUsage
 *      会被随机跳过（丢一分钟统计），而它的 `_historyBusy = true` 又只能
 *      靠 historyTick 的 finally 才被复位 —— 谁也没在管自己。
 * 现在拆成 `_tickBusy` / `_usageBusy` 两个，各自 finally 复位，互不干扰。
 */
function historyTick() {
  if (_tickBusy || !coreRunning()) return;
  _tickBusy = true;
  apiJson('GET', '/connections').then((r) => {
    if (!r.ok || !r.data) return;
    const s = {
      t: Date.now(),
      up: r.data.uploadTotal || 0,
      down: r.data.downloadTotal || 0,
      n: (r.data.connections || []).length,
    };
    history.samples.push(s);
    if (history.samples.length > history.max) history.samples.shift();
  }).catch(() => { /* 采不到就等下一轮 */ })
    .finally(() => { _tickBusy = false; });
}
setInterval(historyTick, 2000);

// ---------------------------------------------------------------- 内核进程

/**
 * 内存环形缓冲容量。
 *
 * 客户端是"只留最近 N 行、一直跟到底"的窗口模型，所以这里必须比页面窗口大，
 * 否则用户切回来时中间那段已经回绕丢了。600 只够 2 分钟的连接日志，
 * 一次内核启动 + 刷几个页面就填满，界面上看就是"日志停了"。
 * 3000 行 ≈ 600 KB 常驻，换掉一个反复出现的困惑，很划算。
 */
const LOG_RING_LIMIT = 3000;

const core = {
  proc: null,
  pid: null,
  startedAt: 0,
  lastExit: null,
  log: [],          // {seq, t, stream, line}
  logLimit: LOG_RING_LIMIT,
};

/**
 * 日志的单调序号（B0）。
 *
 * 以前 `/api/logs?since=N` 的 `since` 是**缓冲数组下标**，`total` 是 `core.log.length`，
 * 到 600 就饱和、永不再增。客户端拿 `total` 当游标，吃到 600 就再也拿不到新行 ——
 * 日志页静默冻结（实测必现，见 `.sandbox/h1/logcursor.js`）。
 *
 * 现在每条日志带一个只增不减的序号，游标用它：
 *   - 回绕 `shift()` 不会让序号回退；
 *   - 后端重启时序号从 0 重来，客户端拿旧序号来问会得到 `reset:true`（而不是"丢了几十亿行"）。
 */
let logSeq = 0;

/**
 * 落盘前脱敏：订阅链接的查询串、token/key/secret 参数、长哈希一律打码。
 * 连接日志里的域名保留原样（排障要用），但绝不能让凭据进日志文件。
 */
function fmtBytes(n) {
  n = Number(n) || 0;
  if (n < 1024) return n + 'B';
  if (n < 1048576) return (n / 1024).toFixed(1) + 'KB';
  if (n < 1073741824) return (n / 1048576).toFixed(1) + 'MB';
  return (n / 1073741824).toFixed(2) + 'GB';
}

function redactLine(s) {
  return String(s)
    .replace(/\b(https?:\/\/[^\s?#]+)\?[^\s]*/gi, '$1?<已隐藏>')
    .replace(/\b(token|key|secret|password|passwd|auth|sign|sig)=([^&\s]+)/gi, '$1=<已隐藏>')
    .replace(/\b[a-f0-9]{32,}\b/gi, '<哈希已隐藏>');
}

function logFilePath() {
  const d = new Date();
  const day = d.getFullYear() + String(d.getMonth() + 1).padStart(2, '0') + String(d.getDate()).padStart(2, '0');
  return path.join(LOG_DIR, 'clashdeck-' + day + '.log');
}

/** 只负责"写一行"：内存 + 落盘，不做任何解析，避免递归 */
/**
 * 日志文件常驻写流。
 *
 * 以前是每行 mkdirSync + appendFileSync：内核启动时一次几十行、连接日志持续输出，
 * 每行都是同步 IO（打开→写→关闭），期间 HTTP 请求（含 2 秒一次的 snapshot）全被阻塞。
 * 改成常驻流之后写入进缓冲区，事件循环不再被磁盘拖住。
 * 代价：进程被强杀时最后几行可能丢 —— 退出路径上会显式 closeLogStream() 兜一下。
 */
let _logStream = null;
let _logStreamPath = '';

function logWriter() {
  const target = logFilePath();          // 按天切分，跨天时这里会变
  if (_logStream && _logStreamPath === target) return _logStream;
  if (_logStream) { try { _logStream.end(); } catch { /* 旧流关不掉也换新的 */ } }
  try {
    fs.mkdirSync(LOG_DIR, { recursive: true });
    _logStream = fs.createWriteStream(target, { flags: 'a' });
    _logStream.on('error', () => { _logStream = null; });
    _logStreamPath = target;
  } catch {
    _logStream = null;
  }
  return _logStream;
}

/** 退出前把缓冲刷掉，否则最后几行日志会丢 */
function closeLogStream() {
  if (!_logStream) return;
  try { _logStream.end(); } catch { /* 尽力而为 */ }
  _logStream = null;
}

function writeLogLine(stream, line, stamp, full) {
  core.log.push({ seq: ++logSeq, t: stamp, stream, line });
  if (core.log.length > core.logLimit) core.log.shift();
  if (settings.logToFile !== false) {
    const w = logWriter();
    if (w) {
      try { w.write(full + ' [' + stream + '] ' + redactLine(line) + '\n'); } catch { /* 写不进去不影响主流程 */ }
    }
  }
}

/**
 * 内核输出里连接行形如：--> www.google.com:443 match RuleSet(google) using 节点[SS]
 * 从这里解析比轮询 /connections 可靠：后者只列"活跃"连接，短连接在 3 秒间隔里就关掉了。
 */
const CONN_LINE_RE = /-->\s+(\S+?):(\d+)\s+match\s+(\S+)\s+using\s+(.+?)\s*$/;

function pushLog(stream, text) {
  // 本地时间：toISOString 是 UTC，日志里出现比本地早 8 小时的时间戳会让人以为出了时区 bug
  const now = new Date();
  const stamp = [now.getHours(), now.getMinutes(), now.getSeconds()]
    .map((n) => String(n).padStart(2, '0')).join(':');
  const full = now.getFullYear() + '-' + String(now.getMonth() + 1).padStart(2, '0') + '-'
    + String(now.getDate()).padStart(2, '0') + ' ' + stamp;
  for (const rawLine of String(text).split(/\r?\n/)) {
    const line = rawLine.trimEnd();
    if (!line.trim()) continue;
    writeLogLine(stream, line, stamp, full);
    // 内核输出里带连接信息 → 单独再落一条可读的连接记录
    if (stream === 'out' && connLogEnabled && settings.logConn !== false) {
      const m = CONN_LINE_RE.exec(line);
      if (m) {
        const host = m[1];
        // 登记去重键，让 8 秒补采不要再记一遍同一条
        rememberConn('h:' + host + ':' + m[2]);
        const rule = m[3];
        const chain = m[4].replace(/\[.*?\]$/, '').trim();
        writeLogLine('conn', host + ':' + m[2] + '  ' + rule + '  → ' + chain, stamp, full);
      }
    }
  }
}

let connLogEnabled = true;   // 运行时状态，由 settings.logConn 决定

/**
 * 只保留最近 N 天的日志文件，避免无限增长。
 * `logKeepDays = 0` 是"永不删除"——用户明确要留档时，清理逻辑必须真的停手，
 * 而不是把 0 当成"没设置"再回落 7 天（那等于用户的选项被静默否决）。
 */
function pruneLogs(keepDays) {
  const days = keepDays === undefined ? settings.logKeepDays : keepDays;
  if (!(Number(days) > 0)) return 0;
  try {
    const files = fs.readdirSync(LOG_DIR).filter((f) => /^clashdeck-\d{8}\.log$/.test(f));
    const cut = Date.now() - days * 86400000;
    let removed = 0;
    for (const f of files) {
      const m = /clashdeck-(\d{4})(\d{2})(\d{2})\.log/.exec(f);
      if (!m) continue;
      const t = new Date(+m[1], +m[2] - 1, +m[3]).getTime();
      if (t < cut) { fs.unlinkSync(path.join(LOG_DIR, f)); removed++; }
    }
    if (removed) console.log('[logs] 清理过期日志 ' + removed + ' 个（保留 ' + days + ' 天）');
    return removed;
  } catch { return 0; /* 目录不存在等 */ }
}

// ---------------------------------------------------------------- 连接日志
/** 记录"谁、什么时候、访问了什么、命中哪条规则、走了哪个节点" */
const connSeen = new Set();
/** 去重键上限。超过就按插入顺序淘汰最旧的，不整批清空。 */
const CONN_SEEN_MAX = 20000;
let connLogTimer = null;

/**
 * 记一个去重键，返回"之前没见过"。
 *
 * 以前是"满了就 clear"（`if (size > 20000) clear()`）：清空那一刻，
 * 还在跑的长连接会被 8 秒补采重新记一遍，日志里就冒出成对的重复行。
 * 换成 FIFO 淘汰最旧的键，容量一样有界，但不会整批失忆。
 * 复杂度 O(1)：Set 的迭代顺序就是插入顺序，取头删头即可。
 */
function rememberConn(key) {
  if (connSeen.has(key)) return false;
  connSeen.add(key);
  if (connSeen.size > CONN_SEEN_MAX) {
    const oldest = connSeen.values().next().value;
    if (oldest !== undefined) connSeen.delete(oldest);
  }
  return true;
}

async function pollConnLog() {
  if (!coreRunning()) return;
  const r = await apiJson('GET', '/connections');
  if (!r.ok || !r.data) return;
  for (const c of (r.data.connections || [])) {
    const id = c.id || (c.metadata && c.metadata.sourceIP + ':' + c.metadata.sourcePort);
    if (!id || !rememberConn(id)) continue;
    const m = c.metadata || {};
    const host = m.host || (m.destinationIP ? m.destinationIP : '');
    if (!host) continue;
    // 同一条连接可能既被内核日志解析记一次、又被这里轮询记一次（两套格式不同，看着像两条）。
    // 用 host:端口 再挡一道，避免日志里出现成对重复。
    if (connLogEnabled && settings.logConn !== false) {
      if (!rememberConn('h:' + host + ':' + (m.destinationPort || ''))) continue;
    }
    const port = m.destinationPort ? ':' + m.destinationPort : '';
    const proc = m.process || '';
    const rule = c.rule || '';
    const chain = (c.chains || []).slice().reverse().join(' ← ');
    const up = c.upload || 0;
    const down = c.download || 0;
    const net = (m.network || 'tcp').toUpperCase();
    pushLog('conn', host + port + (proc ? '  [' + proc + ']' : '')
      + '  ' + net + (rule ? '  ' + rule : '') + (chain ? '  → ' + chain : '')
      + '  ↑' + fmtBytes(up) + ' ↓' + fmtBytes(down));
  }
}

function startConnLog() {
  // 日志驱动已经覆盖了绝大多数情况；轮询只用来兜住"内核日志里没打出来的长连接"
  connLogEnabled = true;
  if (connLogTimer) return;
  connLogTimer = setInterval(() => { pollConnLog().catch(() => {}); }, 8000);
}

function stopConnLog() {
  if (connLogTimer) { clearInterval(connLogTimer); connLogTimer = null; }
  connLogEnabled = false;
  connSeen.clear();
}

function ensureGeoAssets() {
  const files = ['geoip.dat', 'geosite.dat', 'Country.mmdb', 'ASN.mmdb'];
  // geo 数据来源：设置目录 > 内核同目录 > 内核同目录\resources（Clash Verge 就放这）
  const base = path.dirname(settings.corePath || '');
  const candidates = [settings.geoSourceDir, base, path.join(base, 'resources')].filter(Boolean);
  const pick = (f) => {
    for (const dir of candidates) {
      const p = path.join(dir, f);
      if (fs.existsSync(p)) return p;
    }
    return null;
  };
  for (const f of files) {
    const dst = path.join(RUNTIME, f);
    const src = pick(f);
    if (!src) continue;

    if (fs.existsSync(dst)) {
      // 旧版本是"复制"过来的，安装目录里等于存了两份（合计约 40MB）。
      // 这里把独立副本原地换成硬链接；文件被内核占用或跨分区时保持原样，下次再试。
      try {
        const ds = fs.statSync(dst);
        const ss = fs.statSync(src);
        if (ds.size === ss.size && ds.ino !== ss.ino) {
          fs.unlinkSync(dst);
          try { fs.linkSync(src, dst); }
          catch { fs.copyFileSync(src, dst); }
          pushLog('sys', `geo ${f} 已改为硬链接（与内核目录共用一份，省磁盘）`);
        }
      } catch { /* 占用中/不支持，保持原样 */ }
      continue;
    }

    // 优先硬链接：同分区时不占额外空间；跨分区或不支持时回退为复制。
    try {
      fs.linkSync(src, dst);
    } catch {
      try { fs.copyFileSync(src, dst); } catch (e) { pushLog('sys', `geo 复制失败 ${f}: ${e.message}`); }
    }
  }
}

function coreRunning() {
  return !!(core.proc && core.proc.pid && core.proc.exitCode === null);
}

function portBusy(port) {
  return new Promise((resolve) => {
    const s = net.connect({ host: '127.0.0.1', port, timeout: 400 }, () => {
      s.destroy(); resolve(true);
    });
    s.on('timeout', () => { s.destroy(); resolve(false); });
    s.on('error', () => resolve(false));
  });
}

/** 规则集可能是 mrs（二进制）也可能是 list/yaml（文本），扩展名都认 */
const RULE_ASSET_RE = /\.(mrs|list|yaml|yml|txt)$/i;

// 内核启动时会按 rule-providers 里的相对路径去 ./ruleset 找规则文件。
// 找不到就现场联网下载 —— 而此时网络正等着内核起来，于是表现为"启动卡住/规则不全"。
// 把本机已有的规则集搬进运行时目录，启动就变成纯本地动作。
//
// 随包分发的 rules\ 必须排在第一位：它是安装包里唯一"一定存在且离线可用"的那份。
// 2.6.15 之前这里只扫 core\ruleset 与 %APPDATA% 下的第三方客户端目录 ——
// 关掉增强分流（sub 自带 11 个 http provider）时，随包的 cn.mrs / cn-ip.mrs
// 一个都用不上，只能开机现下 jsdelivr；直连被掐就变成 ruleCount=0，
// 界面上就是"分流规则有 5/11 个没加载"。
function findRulesetDirs() {
  const dirs = [];
  const push = (d) => {
    try {
      if (d && fs.existsSync(d) && fs.statSync(d).isDirectory() && !dirs.includes(d)) dirs.push(d);
    } catch { /* 忽略权限问题 */ }
  };
  push(path.join(ROOT, 'rules'));
  push(path.join(RUNTIME, 'rules'));
  push(path.join(settings.geoSourceDir || '', 'ruleset'));
  const base = path.dirname(settings.corePath || '');
  push(path.join(base, 'ruleset'));
  push(path.join(base, 'resources', 'ruleset'));
  try {
    for (const name of fs.readdirSync(process.env.APPDATA || '')) {
      if (!/clash|verge|mihomo/i.test(name)) continue;
      push(path.join(process.env.APPDATA, name, 'ruleset'));
    }
  } catch { /* appdata 读不到就算了 */ }
  return dirs;
}

/**
 * 解析运行时配置里的 rule-providers 段落，得到 `[{name, path, url, type, format, behavior}]`。
 *
 * 不用 YAML 库：这里只认后端自己生成的缩进格式（2 空格名字、4 空格键），
 * 引一个 YAML 依赖只为读 11 行块不值得。
 * `path` 一律解析成绝对路径 —— 内核是 `-d RUNTIME` 启动的，相对路径基准就是 RUNTIME，
 * 拼接基准搞错就会"以为文件在、其实在别处"。
 */
/**
 * 逐行穿一遍 rule-providers 段落，由回调决定这一行保留、替换还是删掉。
 *
 * 抽出来的理由不是"看起来整齐"：这段 YAML 有三处要读（列清单、补 proxy、清理悬空引用），
 * 各写一套"哪里算块开始、什么算一个键"的状态机，改一处忘一处就是行为分叉。
 * 回调签名 `(name, key, value, line)`，返回 undefined 保留 / string 替换 / null 删除。
 */
function mapProviderBlock(text, fn) {
  const out = [];
  let inBlock = false;
  let name = null;
  for (const line of String(text).split(/\r?\n/)) {
    if (/^rule-providers\s*:/.test(line)) { inBlock = true; name = null; out.push(line); continue; }
    if (!inBlock) { out.push(line); continue; }
    if (!line.trim()) { out.push(line); continue; }      // 块内空行不算结束
    if (!/^\s/.test(line)) { inBlock = false; out.push(line); continue; }

    const nm = /^ {2}([A-Za-z0-9_@.\-]+)\s*:\s*$/.exec(line);
    if (nm) { name = nm[1]; out.push(line); continue; }

    const kv = /^ {4}([A-Za-z][\w-]*)\s*:\s*(.*?)\s*$/.exec(line);
    if (kv && name) {
      const r = fn(name, kv[1], kv[2].replace(/^['"]|['"]$/g, ''), line);
      if (r === null) continue;
      out.push(typeof r === 'string' ? r : line);
      continue;
    }
    out.push(line);
  }
  return out.join('\n');
}

function providerSlots(text) {
  const byName = new Map();
  mapProviderBlock(text, (name, key, value) => {
    if (!byName.has(name)) byName.set(name, { name });
    byName.get(name)[key] = value;
  });
  const out = [...byName.values()];
  for (const s of out) if (s.path) s.path = path.resolve(RUNTIME, s.path);
  return out;
}

/**
 * 同一个规则集的"同族文件名"。
 *
 * 随包用的是 `microsoft-cn.mrs` / `steam-cn.mrs`，订阅 provider 写的是
 * `microsoft@cn` / `steam@cn` —— 名字对不上，文件明明在本机也算"没有"。
 * 这里把 `@` 与 `-` 互转、并允许扩展名互换，把这些只是写法不同的情况接住。
 */
function sameFamilyKeys(baseName) {
  const keys = new Set();
  // 只认同一个扩展名：.mrs 是二进制、.list 是文本，把 list 塞进 mrs 路径内核会解析失败
  const m = /^(.*?)(\.[A-Za-z0-9]+)$/.exec(baseName);
  const stem = m ? m[1] : baseName;
  const ext = m ? m[2].toLowerCase() : '';
  const stems = new Set([stem, stem.replace(/@/g, '-'), stem.replace(/-/g, '@')]);
  for (const x of stems) keys.add((x + ext).toLowerCase());
  return keys;
}

/** 把本机已有的规则集按文件原名搬进 data\runtime\ruleset，缺哪个补哪个 */
function ensureRulesetAssets() {
  const dirs = findRulesetDirs();
  if (!dirs.length) return 0;
  const dst = path.join(RUNTIME, 'ruleset');
  fs.mkdirSync(dst, { recursive: true });
  let copied = 0;
  for (const dir of dirs) {
    let files = [];
    try { files = fs.readdirSync(dir); } catch { continue; }
    for (const f of files) {
      const to = path.join(dst, f);
      if (fs.existsSync(to)) continue;
      try { fs.copyFileSync(path.join(dir, f), to); copied++; } catch { /* 单个失败不阻塞 */ }
    }
  }
  if (copied) pushLog('sys', `补齐规则集 ${copied} 个文件（内核启动不必再联网下载）`);
  return copied;
}

/**
 * 按"当前配置真正要哪些规则集"逐项补齐，名字不同也认。
 *
 * ensureRulesetAssets 只会照抄文件名，订阅里的 `microsoft@cn` 与随包的 `microsoft-cn`
 * 在它眼里是两个不同的东西。这一步以 provider 的 path 为准去反向找本地文件，
 * 找到就硬链接过去 —— 于是内核看到文件已存在，整段下载动作被跳过。
 */
function fillProviderAssets() {
  let text = '';
  try { text = fs.readFileSync(path.join(RUNTIME, 'config.yaml'), 'utf8'); } catch { return 0; }
  const slots = providerSlots(text);
  if (!slots.length) return 0;

  const index = new Map();
  for (const dir of findRulesetDirs()) {
    let files = [];
    try { files = fs.readdirSync(dir); } catch { continue; }
    for (const f of files) {
      const k = f.toLowerCase();
      if (!RULE_ASSET_RE.test(k) || index.has(k)) continue;
      index.set(k, path.join(dir, f));
    }
  }

  let filled = 0;
  for (const s of slots) {
    if (!s.path) continue;
    let have = 0;
    try { have = fs.statSync(s.path).size; } catch { have = 0; }
    if (have > 0) continue;
    let src = null;
    for (const k of sameFamilyKeys(path.basename(s.path))) {
      if (index.has(k)) { src = index.get(k); break; }
    }
    if (!src) continue;
    try { fs.mkdirSync(path.dirname(s.path), { recursive: true }); } catch { /* 已存在 */ }
    // 必须复制、不能硬链接：http provider 会往这个路径回写下载结果，
    // 硬链接会让内核把随包的 rules\ 那份一起改掉（同 inode）
    try { fs.copyFileSync(src, s.path); } catch { continue; }
    filled++;
  }
  if (filled) pushLog('sys', `本地补齐规则集 ${filled} 个（按 provider 名字对齐，免联网）`);
  return filled;
}

/**
 * 给指定 provider 补一个 `proxy: <代理组>` 字段。
 *
 * 这是 mihomo 原生支持的规则集下载通道：provider 默认走 DIRECT，
 * 而缺的这几个源（jsdelivr / raw.githubusercontent）**恰恰在严格模式下就是直连**，
 * 于是"规则空 → 走 DIRECT → 下载失败 → 规则还是空"完美闭环。
 * 指定 proxy 之后由内核自己经节点去抓，绕过它当前的空规则表，是最短的一条路。
 */
function addProviderProxy(text, names, group) {
  const wanted = new Set(names);
  // 先扫一遍谁已经有 proxy：只看 type 就插的话，重复调用会插出第二行
  const has = new Set();
  mapProviderBlock(text, (name, key) => { if (key === 'proxy') has.add(name); });

  const done = new Set();      // 一个 provider 只动一次
  let touched = 0;
  const out = mapProviderBlock(text, (name, key, value, line) => {
    if (!wanted.has(name) || done.has(name)) return undefined;
    if (key === 'proxy') { done.add(name); touched++; return '    proxy: ' + group; }
    if (key === 'type' && !has.has(name)) { done.add(name); touched++; return line + '\n    proxy: ' + group; }
    return undefined;
  });
  return { text: out, touched };
}

/**
 * 补不齐的规则集：补 `proxy:` 字段 + 重载配置，让内核经节点再抓一次。
 *
 * 调用点是非阻塞的：启动不该被网络拖住，界面上会先看到「N/M 没加载」，
 * 几秒后自己变成已加载 —— 比卡在启动中诚实。
 */
async function repairProviders(emptyNames) {
  if (!emptyNames || !emptyNames.length) return 0;
  const cfgPath = path.join(RUNTIME, 'config.yaml');
  let text = '';
  try { text = fs.readFileSync(cfgPath, 'utf8'); } catch { return 0; }
  const group = pickMainGroup(text);
  if (!group) {
    pushLog('err', '规则集补不齐，而且这份配置里找不到可选代理组，没法经代理重试');
    return 0;
  }
  const patched = addProviderProxy(text, emptyNames, group);
  if (!patched.touched) return 0;

  try { atomicWrite(cfgPath, patched.text); } catch { /* 写不进就等下次启动 */ }
  pushLog('sys', `已让 ${patched.touched} 个规则集改走「${group}」重新抓取`);
  // 30 秒：重载会重建全部规则集，8 秒那次实测就超时了（超时 ≠ 没生效）
  const rl = await apiJson('PUT', '/configs', { path: cfgPath }, 30000);
  if (!rl.ok) pushLog('sys', '重载配置未确认成功：' + (rl.msg || '未知原因') + '（内核可能仍在应用）');
  await sleep(1200);   // 等内核把这几个 provider 拉完，下面紧接着重查才有意义
  return patched.touched;
}

/**
 * 规则集有没有真的加载进来。
 *
 * `mihomo -t` **不校验** rule-provider 的文件是否存在（实测：把 data\runtime\rules\ 整个移走，
 * `-t` 仍然 exit=0 "test is successful"）。于是会出现最难查的一种状态：
 * 内核起来了、节点有延迟、面板显示"已就绪"，但 15 个规则集的 ruleCount 全是 0，
 * 分流规则一条都不生效 —— 严格模式下所有网站走 MATCH→DIRECT（用户结论是"严格模式打不开外网"），
 * 智能模式下 15 万分流规则全废。
 *
 * 所以内核就绪后主动查一次，把空的那几个点名出来。
 *
 * 形状 `{ ok, total, empty[], checkedAt }` —— `total` 是必须的：
 * 只说"有 3 个没加载"而不知道总共几个，用户无从判断严重程度（3/3 还是 3/15）。
 * 三种状态由调用方按这个分支：
 *   total === 0        → 查不到任何规则集（连 /providers/rules 都是空的，无法判断）
 *   ok === true        → 全部加载
 *   否则               → empty.length 个没加载，共 total 个
 */
let rulesOk = null;
let lastRulesWarn = '';   // 上一次已经报过的"没加载"集合，避免重复刷日志

async function checkRulesOk() {
  // 控制口比 rule-provider 先就绪 —— 内核先监听 external-controller，再异步灌规则集，
  // 两者之间有一段空窗。TUN 模式下空窗被拉长（建虚拟网卡、改默认路由、DNS 接管），
  // 于是"就绪即查"会读到 ruleCount 全为 0，把"正在加载"错报成"没加载"。
  // 实测证据（2.6.12 用户日志 2026-11-06）：01:15:22 报 15/15 未加载，
  // 01:15:23 同一个内核已经 match RuleSet(cd-cn) 分流成功 —— 报的是假警。
  // 00:05:38 那次更直接读到 7/15，等于当场拍到"边加载边查"。
  // 所以这里等它追上来：最多 7 次、约 2.4 秒，拿到非零就收工，只有仍然全空才算真失败。
  let providers = {};
  let names = [];
  for (let i = 0; i < 7; i++) {
    if (!coreRunning()) break;
    const r = await apiJson('GET', '/providers/rules');
    providers = (r.ok && r.data && r.data.providers) || {};
    names = Object.keys(providers);
    const pending = names.filter((n) => !(Number(providers[n] && providers[n].ruleCount) > 0));
    if (names.length && !pending.length) break;
    if (i < 6) await sleep(400);
  }
  if (!names.length) {
    rulesOk = { ok: false, total: 0, empty: [], checkedAt: Date.now() };
    pushLog('err', '查不到任何规则集（/providers/rules 为空）：分流规则可能完全没加载');
    return rulesOk;
  }
  const empty = names.filter((n) => !(Number(providers[n] && providers[n].ruleCount) > 0));
  rulesOk = { ok: empty.length === 0, total: names.length, empty, checkedAt: Date.now() };
  // 同一批东西只报一次：快照每 30 秒兜底重查一次，无脑 pushLog 会把日志刷成刷屏
  const sig = empty.length ? empty.join('|') + '@' + names.length : '';
  if (sig && sig !== lastRulesWarn) {
    lastRulesWarn = sig;
    const head = empty.slice(0, 5).join(', ') + (empty.length > 5 ? ' …' : '');
    pushLog('err', `分流规则集未加载：${empty.length}/${names.length} 个 provider 的规则数为 0`
      + `（${head}）。此时这些规则一条都不生效，严格模式下会全部直连。`
      + '原因多半是这几个规则集的下载源连不上；概览页那条告警可以直接点「重试加载」，'
      + '也可以关掉「增强分流」改用随包的本地规则集。');
  } else if (!sig) {
    lastRulesWarn = '';
  }
  return rulesOk;
}

/**
 * 补完规则集之后要"等它拉完"再看结果。
 *
 * 内核重载配置是异步的：PUT /configs 返回 204 只代表配置收下了，
 * provider 还在后台下。查一次就下结论会把"正在下"写成"还是没加载"，
 * 而那个结论会被缓存住，直到下一次快照兜底重查（30 秒）才纠正。
 */
async function recheckRulesUntilOk(tries = 8, gapMs = 2000) {
  for (let i = 0; i < tries; i++) {
    await checkRulesOk();
    if (rulesOk && rulesOk.ok) return true;
    await sleep(gapMs);
  }
  return false;
}

async function startCoreInner() {
  if (coreRunning()) {
    // 已经在跑就别直接回 ok:true 了事：进程在、控制口可能早就不响应了。
    // 探一次 /version，把"进程在不在"和"能不能用"分开说 —— 只报前者会显示成绿色已启动，
    // 之后每一步操作都失败。
    const v = await apiJson('GET', '/version');
    return {
      ok: true,
      ready: !!v.ok,
      msg: v.ok ? '内核已在运行' : '内核进程还在，但控制接口未响应（功能暂不可用）',
    };
  }
  if (!settings.activeProfile) return { ok: false, msg: '还没有激活的配置，请先在「配置」里导入并激活一个 YAML' };
  if (!fs.existsSync(settings.corePath)) return { ok: false, msg: `内核不存在: ${settings.corePath}` };

  if (settings.tunEnable && !(await isAdmin())) {
    pushLog('sys', '警告：TUN 已开启但当前不是管理员，内核很可能因无法创建虚拟网卡而失败；请用 start-admin.cmd 启动面板');
  }

  // 上一个内核刚被杀时端口还没释放，这里等它，而不是直接报"被占用"
  for (const p of [settings.mixedPort, settings.controllerPort]) {
    if (await portBusy(p) && !(await waitPortFree(p, 3000))) {
      return { ok: false, msg: `端口 ${p} 被别的程序占着（Clash Verge 在跑？），请先退出它` };
    }
  }

  const cfgPath = buildRuntimeConfig();
  if (!cfgPath.ok) return cfgPath;
  // 这份配置就是要被内核加载的那份（下面 spawn 用的就是同一个文件）→ 记为"已生效值"
  commitApplied(cfgPath.applied);

  ensureGeoAssets();
  ensureRulesetAssets();
  fillProviderAssets();   // 订阅 provider 与随包文件同名不同写法时，这一步才认得出来
  const localRules = ensureLocalRules();
  if (localRules.present > 0) {
    if (localRules.created > 0) {
      pushLog('sys', `分流规则集就位 ${localRules.present} 个（本地 mrs，无需联网）`);
    }
  } else if (settings.enhancedRules !== false) {
    // 返回 0 不再静默：要么 rules\ 整个没随包发出来，要么 hardlink/copy 全被权限挡住。
    // 这两种情况内核都会"起得来但规则全空"，而 -t 校验不出来，只能靠这里留痕。
    // 关掉增强分流时本地规则本来就不参与，所以那种情况不吵。
    pushLog('err', '分流规则集一个都没就位（rules\\ 缺失或链接失败）：内核能起来，但分流规则会全部失效');
  }
  // 启动前先离线校验一次。实测 `mihomo -t` 要 1.9 秒，是启动链上最大的一笔开销，
  // 所以按配置内容做指纹：同一份配置只在第一次（或它改动后）才真跑校验，
  // 之后启动直接放行。指纹落在 data/config.ok，重启软件也不重复校验。
  try {
    const raw = fs.readFileSync(path.join(RUNTIME, 'config.yaml'), 'utf8');
    const fp = crypto.createHash('sha1').update(raw).digest('hex');
    let prevFp = '';
    try { prevFp = fs.readFileSync(path.join(DATA, 'config.ok'), 'utf8').trim(); } catch { /* 首次 */ }
    if (prevFp !== fp) {
      const chk = await validateConfigText(raw);
      if (!chk.ok) {
        pushLog('sys', '配置校验未通过：' + String(chk.detail).replace(/\s+/g, ' ').slice(0, 400));
        return { ok: false, msg: humanizeCoreError(chk.detail) };
      }
      try { atomicWrite(path.join(DATA, 'config.ok'), fp); } catch { /* 记不住就下次再校验 */ }
      pushLog('sys', '配置已校验（首次或配置有改动）');
    }
  } catch (e) {
    pushLog('sys', '配置预校验本身出错，继续启动：' + (e && e.message));
  }

  pushLog('sys', `启动内核 ${settings.corePath}`);
  const proc = spawn(settings.corePath, ['-d', RUNTIME, '-f', path.join(RUNTIME, 'config.yaml')], {
    cwd: RUNTIME,
    windowsHide: true,
  });
  core.proc = proc;
  core.pid = proc.pid;
  core.startedAt = Date.now();
  core.lastExit = null;
  proc.stdout.on('data', (b) => pushLog('out', b.toString('utf8')));
  proc.stderr.on('data', (b) => pushLog('err', b.toString('utf8')));
  proc.on('exit', (code, sig) => {
    core.lastExit = { code, sig, at: Date.now() };
    pushLog('sys', `内核退出 code=${code} sig=${sig}`);
    core.proc = null;
    core.pid = null;
    // 内核没了，规则集状态也就没有意义了 —— 留着上一轮的 rulesOk 会让界面
    // 在"未运行"时还显示"15/15 已加载"，那是个过期的事实。
    rulesOk = null;
  });

  // 等待控制端口就绪
  for (let i = 0; i < 40; i++) {
    await sleep(200);
    if (!coreRunning()) break;
    if (await portBusy(settings.controllerPort)) {
      const v = await apiJson('GET', '/version');
      if (v.ok) {
        startConnLog();   // 内核就绪才开始采集连接，否则轮询全是空
        // 连接日志这句不能无条件说"已开启"：真正算不算由 settings.logConn 决定，
        // 日志正文写"已开启"而开关是关的，等于当众否定用户刚关掉的隐私开关。
        if (settings.logConn !== false) {
          pushLog('sys', '连接日志已开启（内核输出实时解析，另有每 8 秒补采长连接）');
        } else {
          pushLog('sys', '连接日志已关闭（不记录访问域名，仅保留内核运行日志）');
        }
        await checkRulesOk();   // 规则集是否真的加载进来了（-t 查不出来）
        // 还有空的就借代理补一次。故意不 await：启动不该被网络拖住，
        // 补完立刻重查，快照下一轮就会把 "N/M 没加载" 换成已加载。
        if (rulesOk && !rulesOk.ok && rulesOk.empty.length) {
          repairProviders(rulesOk.empty)
            .then((n) => (n ? recheckRulesUntilOk() : false))
            .catch(() => { /* 补不上就保留告警，界面已经如实说了 */ });
        }
        return {
          ok: true,
          ready: true,
          msg: `内核已就绪 v${String((v.data && v.data.version) || '?').replace(/^v/i, '')}`,
        };
      }
    }
  }
  if (!coreRunning()) {
    const tail = core.log.slice(-8).map((l) => l.line).join(' | ');
    return { ok: false, ready: false, msg: `内核启动失败：${tail || '无输出'}` };
  }
  return { ok: true, ready: false, msg: '内核进程已起，控制接口未响应（可能仍在初始化）' };
}

let suppressAutoProxyOff = false;

async function stopCoreInner() {
  stopConnLog();
  if (!coreRunning()) return { ok: true, msg: '内核未运行' };
  pushLog('sys', '停止内核');
  const proc = core.proc;
  try {
    proc.kill();
  } catch (e) {
    return { ok: false, msg: e.message };
  }

  // 必须等进程真的退出、端口真的释放，否则紧接着的启动会撞车
  const t0 = Date.now();
  while (coreRunning() && Date.now() - t0 < 6000) await sleep(120);
  if (coreRunning()) {
    try { proc.kill('SIGKILL'); } catch { /* 已经没了 */ }
    await sleep(300);
  }
  await waitPortFree(settings.mixedPort, 3000);

  // 安全网：内核已停，系统代理还指着它 → 整机断网，必须一并关掉
  if (!suppressAutoProxyOff) {
    const st = await sysProxyStatus();
    if (st.ok && st.pointsToOurs) {
      pushLog('sys', '内核已停，自动关闭指向它的系统代理');
      await sysProxySet(false);
    }
  }
  return { ok: true, msg: '内核已停止' };
}

// 对外只暴露排过队的版本
function startCore() { return withGate(startCoreInner); }
function stopCore() { return withGate(stopCoreInner); }

async function restartCore() {
  return withGate(async () => {
    const before = await sysProxyStatus();
    suppressAutoProxyOff = true;
    await stopCoreInner();
    const r = await startCoreInner();
    suppressAutoProxyOff = false;
    if (before.ok && before.pointsToOurs && r.ok) await sysProxySet(true);
    return r;
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- 配置生成

const CONTROLLED_KEYS = new Set([
  'mixed-port', 'port', 'socks-port', 'external-controller', 'secret',
  'external-controller-cors', 'external-controller-pipe', 'allow-lan', 'bind-address', 'tun',
]);

function stripTopLevelKeys(text, keys) {
  const lines = text.split(/\r?\n/);
  const out = [];
  let skip = false;
  for (const line of lines) {
    const m = /^([A-Za-z0-9_.-]+)\s*:/.exec(line);
    if (m) {
      skip = keys.has(m[1]);
      if (!skip) out.push(line);
      continue;
    }
    if (skip) continue;
    out.push(line);
  }
  return out.join('\n');
}

// ---------------------------------------------------------------- 带宽测速
/**
 * 通过本机代理跑 curl 测下行/上行。
 * 刻意不用 Node 自己发请求：走代理要手写 CONNECT 隧道，而系统自带的 curl 已经把这活儿干好了。
 * 结果只返回给前端显示，**不写任何文件、不进流量流水**；上传用的临时块用完立即删除。
 */
async function speedTestRun(mode, bytes) {
  if (!coreRunning()) return { ok: false, msg: '内核没在运行，先启动内核再测速' };
  const proxy = `http://127.0.0.1:${settings.mixedPort}`;
  const want = Math.max(1024 * 1024, Math.min(Number(bytes) || settings.speedTestBytes || 10485760, 100 * 1024 * 1024));
  let tmpFile = null;
  const t0 = Date.now();
  try {
    if (mode === 'up') {
      const url = String(settings.speedUploadUrl || '').trim();
      if (!/^https?:\/\//i.test(url)) return { ok: false, msg: '上传测速地址无效' };
      tmpFile = path.join(process.env.TEMP || '.', `cd-speed-${Date.now()}.bin`);
      fs.writeFileSync(tmpFile, Buffer.alloc(want, 0x61));
      const r = await runExe('curl', ['-x', proxy, '-o', 'NUL', '-sS', '-m', '60',
        '-w', '%{speed_upload}|%{time_total}|%{size_upload}', '--data-binary', '@' + tmpFile, url], 70000);
      if (!r.ok) return { ok: false, msg: '上传测速失败：' + (r.err || ('curl 退出码 ' + r.code)) };
      return parseSpeedOut(r.out, 'up', Date.now() - t0);
    }

    const raw = String(settings.speedTestUrl || '').trim();
    if (!/^https?:\/\//i.test(raw)) return { ok: false, msg: '下载测速地址无效' };
    const url = raw.includes('{bytes}') ? raw.replace(/\{bytes\}/g, String(want)) : raw;
    const r = await runExe('curl', ['-x', proxy, '-o', 'NUL', '-sS', '-m', '45',
      '-w', '%{speed_download}|%{time_total}|%{size_download}', url], 55000);
    // 超时（curl 28）时其实也测到了数据，按已下载量给结果比直接报错有用
    const parsed = parseSpeedOut(r.out, 'down', Date.now() - t0);
    if (parsed.ok) {
      if (!r.ok) parsed.note = '没跑满计划的数据量（网络较慢），数值按实际传输量计算';
      return parsed;
    }
    return { ok: false, msg: '下载测速失败：' + (r.err || ('curl 退出码 ' + r.code)) + '。可以换个节点，或在设置里改测速地址' };
  } catch (e) {
    return { ok: false, msg: '测速异常：' + (e && e.message ? e.message : e) };
  } finally {
    // 无论成败都不留临时文件
    if (tmpFile) { try { fs.unlinkSync(tmpFile); } catch { /* 已被删 */ } }
  }
}

function parseSpeedOut(out, mode, elapsed) {
  // curl -w 输出：速度|总耗时|字节数
  const m = String(out || '').trim().match(/([\d.]+)\|([\d.]+)\|([\d.]+)/);
  if (!m) return { ok: false, msg: '没能解析测速结果' };
  const bytesPerSec = Number(m[1]);
  const secs = Number(m[2]) || elapsed / 1000;
  const bytes = Number(m[3]);
  if (!(bytesPerSec > 0) || !(bytes > 0)) return { ok: false, msg: '测速没拿到有效数据（连接可能被中断）' };
  return {
    ok: true,
    mode,
    bytes: Math.round(bytes),
    seconds: Number(secs.toFixed(2)),
    MBps: Number((bytesPerSec / 1048576).toFixed(2)),
    Mbps: Number(((bytesPerSec * 8) / 1e6).toFixed(1)),
  };
}

// ---------------------------------------------------------------- 在线更新
/**
 * 应用内更新：查云端 releases → 下载 → SHA256 校验 → 静默安装 → 自动重启。
 * 之所以敢做静默安装，是因为安装器本来就带 `--silent --dir`（见 src/Setup/Program.cs）。
 * 设计参考 Tauri updater 的思路（静态 manifest + 校验 + 调用安装器 + 重启），
 * 但我们不用公钥签名，改用云端版本表里的 SHA256 —— 下载源是我们自己的 HTTPS 域名，
 * 威胁模型是"传输损坏/装错文件"，不是"中间人伪造"。自用场景这个强度够。
 */
const UPDATE_HOST = 'https://updates.example.com';
// 安装包与元数据同回分享页：实测 73 MB / 9.1 MB/s，且 2.6.12 老客户端本来就拼本站路径。
// 外部对象存储与 CloudRun 通道已停用，避免维护两套 host 与两套 ticket 规则。
const UPDATE_DL_HOST = 'https://updates.example.com';
const UPDATE_ACCESS_KEY = 'wbpk_tbRQpRFOwXug4cJuumUo1G_qV1aJZEUE1iSBhxV1KpYkoM1tg6mTCYg';
const UPDATE_SHARE = '<REDACTED>';
const UPDATE_DIR = path.join(DATA, 'update');
const CORE_EXE_NAME = 'verge-mihomo.exe';
const UPDATE_STATE = path.join(UPDATE_DIR, 'state.json');

/** 当前软件的发布版本（打包时写进 version.txt，与安装包文件名同源） */
function localVersion() {
  try {
    return fs.readFileSync(path.join(ROOT, 'version.txt'), 'utf8').trim();
  } catch { return ''; }
}

/** 语义化版本比较：a>b 返回 1，相等 0，a<b 返回 -1 */
function cmpVersion(a, b) {
  const pa = String(a || '').split(/[.+-]/).map((x) => parseInt(x, 10) || 0);
  const pb = String(b || '').split(/[.+-]/).map((x) => parseInt(x, 10) || 0);
  for (let i = 0; i < Math.max(pa.length, pb.length, 3); i++) {
    const x = pa[i] || 0;
    const y = pb[i] || 0;
    if (x !== y) return x > y ? 1 : -1;
  }
  return 0;
}

async function checkUpdate() {
  const current = localVersion();
  if (!current) return { ok: false, msg: '读不到当前版本号（version.txt 缺失），无法判断是否需要更新' };
  let list;
  try {
    const r = await fetch(`${UPDATE_HOST}/.cloud/database/rest/releases`
      + '?select=version,file_name,size_bytes,sha256,highlights&is_current=eq.true&limit=1', {
      headers: { Origin: UPDATE_HOST, 'x-wb-webapp-access-key': UPDATE_ACCESS_KEY },
    });
    if (!r.ok) return { ok: false, msg: '连不上更新服务器（HTTP ' + r.status + '）' };
    list = await r.json();
  } catch (e) {
    return { ok: false, msg: '检查更新失败：' + (e && e.message ? e.message : e) };
  }
  if (!Array.isArray(list) || !list.length) return { ok: false, msg: '更新服务器上没有版本记录' };

  const latest = list[0];
  const newer = cmpVersion(latest.version, current) > 0;
  return {
    ok: true,
    current,
    latest: latest.version,
    hasUpdate: newer,
    // 云端比本地旧时也要说出来，避免"检查更新永远没反应"让人以为坏了
    behind: cmpVersion(latest.version, current) < 0,
    notes: Array.isArray(latest.highlights) ? latest.highlights : [],
    size: Number(latest.size_bytes) || 0,
    sha256: String(latest.sha256 || ''),
    url: `${UPDATE_DL_HOST}/${latest.file_name}?ticket=${UPDATE_SHARE}`,
    fileName: latest.file_name,
  };
}

/**
 * 下载，带断点续传：已经存在的 .part 用 Range 从断点接着下。
 * 70MB 下到一半断了从前再来很折磨人，这是唯一值得为它加代码的地方。
 */
function downloadFile(url, dst, onProgress) {
  return new Promise((resolve, reject) => {
    let have = 0;
    try { have = fs.statSync(dst).size; } catch { have = 0; }

    const go = (u, depth, offset) => {
      if (depth > 5) return reject(new Error('重定向次数过多'));
      const headers = { 'User-Agent': 'clash-deck-updater/1' };
      if (offset > 0) headers.Range = 'bytes=' + offset + '-';

      https.get(u, { headers }, (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume();
          return go(new URL(res.headers.location, u).toString(), depth + 1, offset);
        }
        // 206 = 服务器接受了续传；200 = 不支持 Range，只能从头写
        const resume = res.statusCode === 206;
        if (res.statusCode !== 200 && res.statusCode !== 206) {
          res.resume();
          return reject(new Error('HTTP ' + res.statusCode));
        }
        const totalHead = Number(res.headers['content-length'] || 0);
        const total = resume ? totalHead + offset : totalHead;
        let got = resume ? offset : 0;
        const ws = fs.createWriteStream(dst, resume ? { flags: 'a' } : { flags: 'w' });
        res.on('data', (c) => {
          got += c.length;
          if (onProgress) onProgress(got, total);
        });
        res.on('error', reject);
        ws.on('error', reject);
        ws.on('finish', () => resolve({ bytes: got, total, resumed: resume && offset > 0 }));
        res.pipe(ws);
      }).on('error', reject);
    };
    go(url, 0, have);
  });
}

function sha256File(p) {
  return new Promise((resolve, reject) => {
    const h = crypto.createHash('sha256');
    fs.createReadStream(p)
      .on('data', (d) => h.update(d))
      .on('end', () => resolve(h.digest('hex')))
      .on('error', reject);
  });
}

function readUpdateState() {
  try { return JSON.parse(fs.readFileSync(UPDATE_STATE, 'utf8')) || {}; } catch { return {}; }
}

/** 下载并校验，成功才落 state；失败一律清掉临时文件，不留半成品 */
async function downloadUpdate() {
  const c = await checkUpdate();
  if (!c.ok) return c;
  if (!c.hasUpdate) return { ok: false, msg: '已经是最新版本 ' + c.current };
  if (!c.sha256) return { ok: false, msg: '云端这条版本记录没有校验值，为安全起见不更新' };

  fs.mkdirSync(UPDATE_DIR, { recursive: true });
  const dst = path.join(UPDATE_DIR, c.fileName);
  const part = dst + '.part';
  try {
    // 保留 .part：下载函数会据此续传
    const r = await downloadFile(c.url, part, (got, total) => {
      updateProgress = { got, total, pct: total ? Math.round(got * 100 / total) : 0 };
    });
    const digest = await sha256File(part);
    if (digest.toLowerCase() !== c.sha256.toLowerCase()) {
      fs.unlinkSync(part);
      return { ok: false, msg: `下载的文件校验不通过（期望 ${c.sha256.slice(0, 12)}… 实际 ${digest.slice(0, 12)}…），已丢弃` };
    }
    if (fs.existsSync(dst)) fs.unlinkSync(dst);
    fs.renameSync(part, dst);
    atomicWrite(UPDATE_STATE, JSON.stringify({ file: dst, version: c.latest, sha256: digest, at: Date.now() }, null, 2));
    return { ok: true, file: dst, version: c.latest, bytes: r.bytes, msg: '已下载并校验通过' };
  } catch (e) {
    try { if (fs.existsSync(part)) fs.unlinkSync(part); } catch { /* 尽力清理 */ }
    return { ok: false, msg: '下载失败：' + (e && e.message ? e.message : e) };
  } finally {
    updateProgress = null;
  }
}

let updateProgress = null;

/**
 * 应用更新：写一个延迟批处理 → 主进程退出 → 安装器静默覆盖 → 重新拉起。
 * 必须延迟：安装包要覆盖正在运行的 exe，进程没退干净时文件是锁的。
 */
function applyUpdate() {
  const st = readUpdateState();
  if (!st.file || !fs.existsSync(st.file)) return { ok: false, msg: '更新包不存在，请先下载' };
  const exe = path.join(ROOT, 'ClashDeck.exe');
  const cmdFile = path.join(UPDATE_DIR, 'apply.cmd');
  // 关键：用 start /wait 等安装器真正结束，并检查退出码。
  // 以前是"启动安装器 → 立刻重启"，安装失败（文件被占用）时照样拉起旧版，
  // 现象是"自动重启了、版本没变、没有任何提示" —— 静默失败。
  const logFile = path.join(UPDATE_DIR, 'apply.log');
  const script = [
    '@echo off',
    'chcp 65001 >nul',
    'timeout /t 3 /nobreak >nul',
    'taskkill /F /IM ClashDeck.exe >nul 2>&1',
    'taskkill /F /IM ' + CORE_EXE_NAME + ' >nul 2>&1',
    'timeout /t 2 /nobreak >nul',
    'start /wait "" "' + st.file + '" --silent --dir "' + ROOT + '"',
    'if errorlevel 1 (',
    '  echo %date% %time% install failed code=%errorlevel% >> "' + logFile + '"',
    ') else (',
    '  echo %date% %time% install ok version=' + st.version + ' >> "' + logFile + '"',
    '  del "' + st.file + '" >nul 2>&1',
    ')',
    'start "" "' + exe + '"',
    'del "%~f0"',
  ].join('\r\n');
  try {
    fs.writeFileSync(cmdFile, script, 'utf8');
  } catch (e) {
    return { ok: false, msg: '写更新脚本失败：' + e.message };
  }
  try {
    spawn('cmd.exe', ['/c', 'start', '/min', cmdFile], {
      detached: true, stdio: 'ignore', windowsHide: true,
    }).unref();
  } catch (e) {
    return { ok: false, msg: '启动更新脚本失败：' + e.message };
  }
  pushLog('sys', `开始更新到 ${st.version}，即将重启`);
  return { ok: true, msg: '正在重启并安装 ' + st.version + '，稍等几秒会自动打开' };
}

// ---------------------------------------------------------------- 自定义分流规则
/**
 * 公开规则库不可能覆盖所有站点，所以留一个用户自己的口子：
 * 指定某个域名"走代理"或"直连"，插在内置规则之前（优先级高于 ads/国内直连），
 * 用户说什么就是什么。存 data/custom-rules.json，改完重启内核生效。
 */
const CUSTOM_RULES_FILE = path.join(DATA, 'custom-rules.json');
const CUSTOM_TYPES = ['DOMAIN', 'DOMAIN-SUFFIX', 'DOMAIN-KEYWORD'];

function readCustomRules() {
  try {
    const a = JSON.parse(fs.readFileSync(CUSTOM_RULES_FILE, 'utf8'));
    if (!Array.isArray(a)) return [];
    return a.filter((r) => r && r.value && (r.action === 'proxy' || r.action === 'direct'))
      .map((r) => ({
        type: CUSTOM_TYPES.includes(String(r.type || '').toUpperCase()) ? String(r.type).toUpperCase() : 'DOMAIN-SUFFIX',
        value: String(r.value),
        action: r.action,
        at: Number(r.at) || 0,
      }));
  } catch { return []; }
}

function writeCustomRules(list) {
  atomicWrite(CUSTOM_RULES_FILE, JSON.stringify(list, null, 2));
}

// ---------------------------------------------------------------- 增强分流规则
/**
 * 内置一套本地规则集（mrs，随安装包分发，不依赖 GitHub 可达性）。
 * 订阅自带的规则通常很薄（常见只有 10 来个规则集，且部分引用 jsdelivr 在国内根本拉不动），
 * 这里用 MetaCubeX 的 geosite/geoip 数据重建一层分流，覆盖 15 万条。
 */
// Telegram 官方公布的服务器网段（core.telegram.org/resources/cidr.txt）。
// Telegram 客户端连的是 **IP 地址**，不做域名解析 —— 域名规则（cd-telegram）管不到它。
// 严格模式下这些 IP 会落到 MATCH → DIRECT → 被墙，表现就是"开了严格模式 Telegram 断网"。
const TELEGRAM_CIDR = [
  '91.108.56.0/22', '91.108.4.0/22', '91.108.8.0/22', '91.108.16.0/22',
  '91.108.12.0/22', '149.154.160.0/20', '91.105.192.0/23', '91.108.20.0/22',
  '185.76.151.0/24',
  '2001:b28:f23d::/48', '2001:b28:f23f::/48', '2001:67c:4e8::/48',
  '2001:b28:f23c::/48', '2a0a:f280::/32',
];

/** Telegram IP 段 → 规则行（IPv6 用 IP-CIDR6）；no-resolve 避免对域名目标触发解析 */
function telegramCidrLines(group) {
  return TELEGRAM_CIDR.map((c) =>
    `  - IP-CIDR${c.includes(':') ? '6' : ''},${c},${group},no-resolve`);
}

const LOCAL_RULES = [
  'private', 'ads', 'cn', 'cn-ip', 'apple-cn', 'microsoft-cn', 'steam-cn',
  'google', 'openai', 'telegram', 'twitter', 'github', 'netflix', 'youtube',
  'geolocation-not-cn',
];

/**
 * 把安装目录里的 rules/ 硬链接到运行目录（省磁盘，同分区零额外占用）。
 *
 * 返回 `{ created, present }` —— 分开报是必要的：以前只返回"这次新建了几个"，
 * 于是第二次启动恒为 0，调用方就按"一个都没就位"记一条 err。
 * 文件明明在、规则也在跑，日志里却天天报错，这种假警报比没日志更坏事。
 */
function ensureLocalRules() {
  const srcDir = path.join(ROOT, 'rules');
  const dstDir = path.join(RUNTIME, 'rules');
  if (!fs.existsSync(srcDir)) return { created: 0, present: 0 };
  try { fs.mkdirSync(dstDir, { recursive: true }); } catch { /* 已存在 */ }
  let created = 0;
  let names = [];
  try { names = fs.readdirSync(srcDir); } catch { return { created: 0, present: 0 }; }
  for (const f of names) {
    if (!f.endsWith('.mrs')) continue;
    const src = path.join(srcDir, f);
    const dst = path.join(dstDir, f);
    if (fs.existsSync(dst)) continue;
    try { fs.linkSync(src, dst); }
    catch { try { fs.copyFileSync(src, dst); } catch { continue; } }
    created++;
  }
  // 以"运行目录里现在有几个"为准，而不是"这次干了多少活"
  let present = 0;
  try { present = fs.readdirSync(dstDir).filter((f) => f.endsWith('.mrs')).length; } catch { present = created; }
  return { created, present };
}

/** 找出订阅里的"主组"：第一个 type: select 的组，规则的分流目标就是它 */
function pickMainGroup(text) {
  const inline = text.match(/^[ \t]*-[ \t]*\{[ \t]*name[ \t]*:[ \t]*(['"]?)([^,'"}\n]+?)\1[ \t]*,[^\n]*type[ \t]*:[ \t]*select/m);
  if (inline) return inline[2].trim();
  const block = text.match(/^[ \t]*-[ \t]*name[ \t]*:[ \t]*(['"]?)([^'"\n]+?)\1[ \t]*\r?\n[ \t]*type[ \t]*:[ \t]*select/m);
  if (block) return block[2].trim();
  return null;
}

/**
 * 自定义规则 → YAML 行。两种规则来源共用一份生成逻辑，
 * 否则「增强开」和「增强关」两条路径会各写一套，迟早不一致。
 */
function customRuleLines(custom, group) {
  return (custom || []).map((r) =>
    `  - ${r.type},${r.value},${r.action === 'direct' ? 'DIRECT' : group}`);
}

/**
 * 把若干规则行插到订阅 rules: 段的**最前面** —— 也就是优先级最高。
 * 用于「关闭增强分流」的情况：此时规则来自订阅，但用户自己设过的条目
 * 不该因为一个开关就静默失效（界面还留着规则，实际不起作用，属于界面在说谎）。
 */
function injectRuleLines(text, lines) {
  if (!lines.length) return text;
  const arr = text.split(/\r?\n/);
  // 只处理块式 rules:（冒号后为空或注释）。内联数组 forms 极少见，遇到就不动。
  const i = arr.findIndex((l) => /^rules\s*:\s*(#.*)?$/.test(l));
  if (i < 0) return text;
  arr.splice(i + 1, 0,
    '  # —— 用户自定义规则（最高优先级；关闭增强分流后依然生效）——',
    ...lines);
  return arr.join('\n');
}

/** 生成规则块：顺序即优先级，改动前先想清楚谁该在谁前面 */
function enhancedRulesBlock(group, custom) {
  const prov = LOCAL_RULES.map((n) => {
    const behavior = n === 'cn-ip' ? 'ipcidr' : 'domain';
    return `  cd-${n}: {type: file, behavior: ${behavior}, format: mrs, path: ./rules/${n}.mrs}`;
  }).join('\n');

  // 两种模式的**代理名单完全相同**，区别只在最后的兜底走哪儿。
  //
  // geolocation-not-cn 的 behavior 是 domain —— 它是「非中国服务的域名集合」
  // （MetaCubeX geolocation-!cn，压缩后约 190KB，覆盖数万个国外站点），不是 IP 段。
  // 所以它正是严格模式需要的那份"国外域名大名单"：
  //   严格 = 名单内走代理（覆盖所有已知国外站）+ 名单外直连（真正没见过的站才直连）
  //   智能 = 同一份名单走代理 + 名单外也走代理（兜底代理）
  const strict = settings.proxyMode === 'whitelist';
  const toProxy = [
    'openai', 'telegram', 'twitter', 'google', 'github', 'youtube', 'netflix',
    'geolocation-not-cn',   // 兜住所有没被上面几个专项规则覆盖的国外域名
  ];
  const toDirect = ['apple-cn', 'microsoft-cn', 'steam-cn', 'cn'];

  // 用户自定义规则：排在 private 之后、其余一切之前 —— 想覆盖哪条就覆盖哪条
  const customLines = customRuleLines(custom, group);

  return [
    'rule-providers:',
    prov,
    'rules:',
    // ① 私有地址最先直连，避免内网/本机被代理
    '  - RULE-SET,cd-private,DIRECT',
    // ② 用户自定义（优先级高于广告与国内直连）
    ...(customLines.length ? ['  # —— 自定义规则 ——', ...customLines] : []),
    // ③ 广告在代理之前拒绝，否则白烧机场流量
    '  - RULE-SET,cd-ads,REJECT',
    // ④ 明确要代理的服务必须排在 cn 之前：google/openai 都有域名混在国内列表里
    ...toProxy.map((n) => `  - RULE-SET,cd-${n},${group}`),
    // ⑤ 国内服务直连
    ...toDirect.map((n) => `  - RULE-SET,cd-${n},DIRECT`),
    '  - RULE-SET,cd-cn-ip,DIRECT',
    // ④.5 Telegram 官方 IP 段 → 代理
    //      放在 cn-ip 之后（国内 IP 先判直连，不影响），放在私有网段之前。
    //      只列 Telegram 自己的服务器网段，不是"所有国外 IP"，白名单语义不变。
    ...telegramCidrLines(group),
    // ⑤ 私有网段兜底（private 规则集只含域名，不含网段）
    '  - IP-CIDR,127.0.0.0/8,DIRECT,no-resolve',
    '  - IP-CIDR,10.0.0.0/8,DIRECT,no-resolve',
    '  - IP-CIDR,172.16.0.0/12,DIRECT,no-resolve',
    '  - IP-CIDR,192.168.0.0/16,DIRECT,no-resolve',
    '  - IP-CIDR,100.64.0.0/10,DIRECT,no-resolve',
    '  - IP-CIDR,169.254.0.0/16,DIRECT,no-resolve',
    '  - IP-CIDR6,::1/128,DIRECT,no-resolve',
    '  - IP-CIDR6,fc00::/7,DIRECT,no-resolve',
    '  - IP-CIDR6,fe80::/10,DIRECT,no-resolve',
    // ⑥ 兜底：这是全局隐私取向的分水岭。
    //    smart     = 漏网之鱼走代理（能上，但所有流量都过机场）
    //    whitelist = 漏网之鱼走直连（没点名的站根本不出门，代价是陌生外网站可能打不开）
    `  - MATCH,${strict ? 'DIRECT' : group}`,
  ].join('\n');
}

/** 删掉某个顶层键及其所有子行（用于清理会引用已删规则集的配置段） */
function dropYamlSection(text, key) {
  const lines = text.split(/\r?\n/);
  const out = [];
  let skipping = false;
  let indent = 0;
  for (const line of lines) {
    const m = /^([ \t]*)([A-Za-z0-9_.!@-]+)[ \t]*:/.exec(line);
    if (!skipping && m && m[2] === key) {
      skipping = true;
      indent = m[1].length;
      continue;
    }
    if (skipping) {
      if (!line.trim()) continue;
      const cur = /^([ \t]*)/.exec(line)[1].length;
      if (cur > indent) continue;
      skipping = false;
    }
    out.push(line);
  }
  return out.join('\n');
}

/**
 * 机场会在订阅里塞"信息节点"：`剩余流量：116 GB`、`套餐到期：2027-02-24`、`官网：xxx`。
 * 它们是真协议（Vless/SS），能被测通、能出延迟数字，于是会和真节点一起进 url-test 组。
 *
 * 平时不会被选中——延迟比真节点高。但真节点一旦全部超时（机场抖动、维护、被墙），
 * URLTest 就会挑中信息节点，此时"自动选择"显示有延迟、实际流量全走进一个只回固定页面的
 * 假节点 —— 用户看到的是"代理开着但什么都打不开"。
 *
 * 订阅自带一个 exclude-filter，但通常只排除 `[Home]` 之类的自家标记，管不到这些。
 * 所以这里给所有**自动选路**的组（url-test / fallback / load-balance）补上关键词；
 * select 组不动——那里面的信息节点是用户手动点开看流量用的，删了反而少个功能。
 *
 * 两个约束（都是实测踩出来的）：
 *   ① 必须 `^` 锚定。信息节点的名字**一定**以这些词开头（`剩余流量：116GB`、`官网：xxx`），
 *      而不锚定会误伤正常节点：`HK-Reset-Pool-01`、`JP-Traffic-East`、`US-Expire-Node`
 *      都曾被整条剔除，实测一组 6 个节点只剩 1 个。
 *   ② 不要 `Reset` / `Expire` 这类泛用词。它们在英文机场的**正常**节点名里当动词/名词用
 *      （Pool-Reset、Expire-Node），而信息节点在中文语境里不会叫这个。
 */
const INFO_NODE_FILTER = '^(剩余流量|距离下次重置|套餐到期|官网|到期时间|官网地址|Expire|Traffic)';

/** 取一个组块里的 proxies 名单（行内 `[a, b]` 与块式 `- a` 都支持）；没写 proxies 返回 null */
function groupProxyNames(lines, from, to) {
  for (let i = from; i < to; i++) {
    const m = /^\s*proxies\s*:\s*(.*)$/.exec(lines[i]);
    if (!m) continue;
    const inline = m[1].trim();
    const out = [];
    if (inline.startsWith('[')) {
      for (const part of inline.replace(/^\[|\]\s*$/g, '').split(',')) {
        const v = part.trim().replace(/^["']|["']$/g, '').trim();
        if (v) out.push(v);
      }
      return out;
    }
    const baseIndent = /^(\s*)/.exec(lines[i])[1].length;
    for (let k = i + 1; k < to; k++) {
      const l = lines[k];
      if (!l.trim()) continue;
      if (/^(\s*)/.exec(l)[1].length <= baseIndent) break;
      const mm = /^\s*-\s*(.+)$/.exec(l);
      if (mm) {
        const v = mm[1].trim().replace(/^["']|["']$/g, '').trim();
        if (v) out.push(v);
      }
    }
    return out;
  }
  return null;
}

/** 组里有没有 `use:`（provider 引用）——那种组的节点不在本文件里，数不出可用节点数 */
function groupUsesProvider(lines, from, to) {
  for (let i = from; i < to; i++) if (/^\s*use\s*:/.test(lines[i])) return true;
  return false;
}

function hardenAutoGroups(body) {
  const AUTO_TYPES = new Set(['url-test', 'fallback', 'load-balance']);
  const lines = body.split('\n');
  const start = lines.findIndex((l) => /^proxy-groups\s*:/.test(l));
  if (start < 0) return body;

  // 结束位置：proxy-groups 之后第一个不缩进的非空行
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i++) {
    const l = lines[i];
    if (l.trim() && !/^\s/.test(l) && !/^-/.test(l)) { end = i; break; }
  }

  // 收集每个组的 [起, 止) 区间与 type 行位置
  const groups = [];
  let cur = null;
  for (let i = start + 1; i < end; i++) {
    const l = lines[i];
    if (/^\s*-\s*name\s*:/.test(l)) {
      if (cur) { cur.to = i; groups.push(cur); }
      cur = { from: i, to: end, typeLine: -1, type: '', filterLine: -1, indent: 2 };
    } else if (cur) {
      const m = /^(\s*)type\s*:\s*(\S+)/.exec(l);
      if (m) { cur.type = m[2].toLowerCase(); cur.typeLine = i; cur.indent = m[1].length; }
      if (/^\s*exclude-filter\s*:/.test(l)) cur.filterLine = i;
    }
  }
  if (cur) { cur.to = end; groups.push(cur); }

  let patched = 0;
  let emptied = 0;
  let uncountable = 0;
  // 从后往前改，避免插入行导致前面的下标失效
  for (const g of groups.reverse()) {
    if (!AUTO_TYPES.has(g.type)) continue;
    const pad = ' '.repeat(g.indent);
    let nextFilter;
    if (g.filterLine >= 0) {
      const l = lines[g.filterLine];
      const m = /^(\s*exclude-filter\s*:\s*)(.*)$/.exec(l);
      const val = (m ? m[2] : '').trim();
      if (val.includes('剩余流量')) continue;      // 已经加过，别重复
      // 原值可能带引号，去掉引号再拼。
      // 拼完**不能加双引号**：订阅里常见 `\[Home\]` 这种写法，
      // YAML 双引号串里 `\[` 是非法转义，内核会直接报 unknown escape character。
      // 未加引号的标量里 `|` 和 `\` 都是普通字符，正则可原样写。
      const unq = val.replace(/^["']|["']$/g, '').trim();
      nextFilter = (unq ? unq + '|' : '') + INFO_NODE_FILTER;
    } else if (g.typeLine >= 0) {
      nextFilter = INFO_NODE_FILTER;
    } else {
      continue;
    }

    // 加之前先数一遍：这个 filter 会不会把这组里的节点全摘光。
    // 全摘光的后果不是"少几个节点"——mihomo 会让该组退化成内置 COMPATIBLE（等于直连），
    // 面板上"自动选择"还亮着、还能选出名字，实际流量绕过代理直接出去（真实 IP 暴露）。
    const names = groupProxyNames(lines, g.from, g.to);
    if (names === null) {
      // 组里只有 use:（provider 引用），节点不在本文件里，数不出来 → 不冒险，保持原样
      if (groupUsesProvider(lines, g.from, g.to)) { uncountable++; continue; }
    } else {
      let re = null;
      try { re = new RegExp(nextFilter); } catch { re = null; }
      if (re && names.every((n) => re.test(n))) { emptied++; continue; }
    }

    if (g.filterLine >= 0) lines[g.filterLine] = pad + 'exclude-filter: ' + nextFilter;
    else lines.splice(g.typeLine + 1, 0, pad + 'exclude-filter: ' + nextFilter);
    patched++;
  }

  if (patched > 0) {
    pushLog('sys', `已让 ${patched} 个自动选路组跳过机场的信息节点（剩余流量/套餐到期这类）`);
  }
  if (emptied > 0) {
    pushLog('sys', `有 ${emptied} 个自动选路组的节点名全部命中信息节点关键词，`
      + '为避免整组失效（会退化成直连）已跳过不写过滤');
  }
  if (uncountable > 0) {
    pushLog('sys', `有 ${uncountable} 个自动选路组用 provider 提供节点，无法预判过滤后的节点数，已跳过（信息节点可能仍被自动选中）`);
  }
  return lines.join('\n');
}

/**
 * 生成后的配置里，兜底是不是被**显式**写成 DIRECT。
 *
 * 这是 `strictApplied` 的唯一真值来源 —— 不从"代码走了哪个分支"推，而是看最后写出来的那行。
 * 因为"我们以为走了严格分支"和"文件里真是 DIRECT"是两件事（增强开启但认不出主组时，
 * 代码会静默退回订阅自带规则，那儿根本没有严格模式处理）。
 */
function fallbackIsDirect(text) {
  const hits = String(text).match(/^[ \t]*-[ \t]*MATCH[ \t]*,[ \t]*(\S+)[ \t]*$/gm);
  if (!hits || !hits.length) return false;   // 一条 MATCH 都没有 = 没写成
  return /MATCH[ \t]*,[ \t]*DIRECT[ \t]*$/i.test(hits[hits.length - 1]);
}

/** 最近一次**真正生效**的运行时配置里的关键值（供 pendingRestart 比对） */
let appliedTo = null;

function commitApplied(applied) {
  if (applied) appliedTo = applied;
}

/**
 * 哪些设置改了但还没生效。
 *
 * 判据的右边必须是"**内核实际加载的那份 config.yaml 里的值**"，不是"最后一次启动时的 settings"：
 * 热加载也会让改动生效，只记启动那一刻的话，热加载之后徽章还会挂着（假徽章）。
 *
 * 内核没跑时恒为空数组 —— 没有"跑着的是旧值"这回事，停机状态下挂一堆"待重启"只会误导。
 */
function computePendingRestart() {
  if (!coreRunning() || !appliedTo) return [];
  const out = [];
  if (settings.proxyMode !== appliedTo.proxyMode) out.push('proxyMode');
  if ((settings.enhancedRules !== false) !== appliedTo.enhancedRules) out.push('enhancedRules');
  if (!!settings.tunEnable !== appliedTo.tunEnable) out.push('tunEnable');
  // 面板端口要重启的是**应用**，不是内核；判据是"配置值 vs 真实监听值"
  if (Number(settings.webPort) !== Number(listenPort())) out.push('webPort');
  return out;
}

function buildRuntimeConfig() {
  if (!settings.activeProfile) return { ok: false, msg: '未指定激活配置' };
  const src = path.join(PROFILES, settings.activeProfile);
  if (!fs.existsSync(src)) return { ok: false, msg: `配置文件不存在: ${settings.activeProfile}` };
  const raw = fs.readFileSync(src, 'utf8');
  if (!/^\s*proxies\s*:/m.test(raw) && !/^\s*proxy-providers\s*:/m.test(raw)) {
    return { ok: false, msg: '这份 YAML 里没有 proxies / proxy-providers，看起来不是完整配置' };
  }
  let body = stripTopLevelKeys(raw, CONTROLLED_KEYS);
  // 主组只算一次：增强开/关两条分支都要用，分开算会出现"两条分支结论不一致"
  const mainGroup = pickMainGroup(raw);

  // 增强分流：把订阅自带的规则换成本地规则集。
  // 订阅规则普遍很薄（本例只有 11 个规则集），且不少远程源在国内根本拉不动；
  // 本地这套覆盖 15 万条，离线可用。找不到主组就退回订阅原规则，不硬来。
  if (settings.enhancedRules !== false) {
    const g = mainGroup;
    if (g) {
      let stripped = stripTopLevelKeys(raw, new Set([...CONTROLLED_KEYS, 'rules', 'rule-providers']));
      // dns.nameserver-policy 里通常写着 rule-set:<订阅自己的规则集名>，
      // 规则集被我们换掉后这些引用会变成悬空引用（内核直接 fatal），所以整段移除。
      stripped = dropYamlSection(stripped, 'nameserver-policy');
      body = stripped.trimEnd() + '\n' + enhancedRulesBlock(g, readCustomRules());
    } else {
      pushLog('sys', '增强规则已开启，但这份配置里找不到 select 组，本次沿用订阅自带规则');
    }
  } else {
    // 关闭增强分流：规则完全用订阅自带的那套。这里要做两件事，
    // 否则开关一关，界面上还留着的东西就会静默不生效。
    const custom = readCustomRules();
    const g = mainGroup;

    // ① 自定义规则：插到订阅 rules 最前面（优先级最高）。
    //    "走代理"的条目需要组名，订阅里找不到可选组时只能跳过并明说。
    if (custom.length) {
      const usable = custom.filter((r) => r.action === 'direct' || !!g);
      const skipped = custom.length - usable.length;
      if (usable.length) {
        body = injectRuleLines(body, customRuleLines(usable, g || 'DIRECT'));
        pushLog('sys', `已把 ${usable.length} 条自定义规则插到订阅规则最前面（关闭增强分流时依然生效）`);
      }
      if (skipped) pushLog('sys', `有 ${skipped} 条"走代理"的自定义规则未生效：这份订阅里找不到可选代理组`);
    }

    // ② 严格模式：兜底改直连。
    //    关闭增强分流时 proxyMode 本来完全不参与 ——
    //    但"严格"的本质就是"没点名的站不走代理"，不改这里，用户切了严格也不生效。
    //    只动终端规则那一行，订阅的其余规则原样保留。
    if (settings.proxyMode === 'whitelist') {
      const before = body;
      // Telegram 客户端按 IP 直连，订阅规则里没有它的网段 → 兜底改直连后它会断。
      // 所以把 Telegram 网段插在 MATCH 之前，只影响它自己。
      const tg = g ? telegramCidrLines(g) : [];
      body = body.replace(/^(\s*-\s*MATCH,).*$/gm, () =>
        (tg.length ? tg.join('\n') + '\n  - MATCH,DIRECT' : '  - MATCH,DIRECT'));
      if (tg.length) pushLog('sys', `严格模式：已在兜底前插入 ${tg.length} 条 Telegram 网段规则（客户端按 IP 连接，域名规则管不到）`);
      if (body !== before) {
        pushLog('sys', '严格模式：已把订阅自带规则的兜底改为直连（未命中的域名不再经过机场）');
      } else {
        pushLog('sys', '严格模式：这份订阅配置里没有 MATCH 兜底行，无法强制直连');
      }
    }
  }

  // 规则定完之后、拼 header 之前处理代理组：把机场的信息节点从自动选路组里摘掉。
  // 放在这里是因为 body 此时已是最终形态，后面不会再被改动。
  body = hardenAutoGroups(body);

  const header = [
    '# generated by clash-deck',
    `mixed-port: ${settings.mixedPort}`,
    `socks-port: ${settings.socksPort}`,
    `port: ${settings.httpPort}`,
    `allow-lan: ${settings.allowLan ? 'true' : 'false'}`,
    `external-controller: 127.0.0.1:${settings.controllerPort}`,
    `secret: "${settings.secret}"`,
    'external-controller-cors:',
    '  allow-private-network: true',
    '  allow-origins:',
    `  - http://127.0.0.1:${settings.webPort}`,
    `  - http://localhost:${settings.webPort}`,
    'tun:',
    `  enable: ${settings.tunEnable ? 'true' : 'false'}`,
    '  stack: mixed',
    '  auto-route: true',
    '  auto-detect-interface: true',
  ].join('\n');
  const cfgPath = path.join(RUNTIME, 'config.yaml');
  const content = header + '\n' + body;
  let prev = null;
  try { prev = fs.readFileSync(cfgPath, 'utf8'); } catch { /* 首次生成 */ }
  if (prev !== content) atomicWrite(cfgPath, content);

  // A8：严格模式到底有没有生效，看写出来的那行兜底，不看走了哪个分支。
  // 智能模式下不存在"兜底该不该直连"这个问题 → null（不适用），
  // 给 true/false 都会被界面误当成"已生效/未生效"来渲染。
  let strictApplied = null;
  let strictNote = '';
  if (settings.proxyMode === 'whitelist') {
    strictApplied = fallbackIsDirect(content);
    if (!strictApplied) {
      strictNote = mainGroup ? 'no-match-rule' : 'no-main-group';
    }
  }
  return {
    ok: true,
    msg: cfgPath,
    strictApplied,
    strictNote,
    applied: {
      proxyMode: settings.proxyMode,
      enhancedRules: settings.enhancedRules !== false,
      tunEnable: !!settings.tunEnable,
      strictApplied,
      strictNote,
      at: Date.now(),
    },
  };
}

/** 让运行中的内核热加载当前配置（不重启进程） */
async function reloadRuntimeInner() {
  const built = buildRuntimeConfig();
  if (!built.ok) return built;
  const carry = { strictApplied: built.strictApplied, strictNote: built.strictNote, pendingRestart: [] };
  if (!coreRunning()) {
    // 没内核在跑 → 文件写好了，等下次启动生效；此时没有"跑着的是旧值"，所以不是 pending
    commitApplied(built.applied);
    return { ok: true, msg: '内核未运行，配置已写入，下次启动生效', ...carry };
  }
  const r = await apiJson('PUT', '/configs?force=true', { path: path.join(RUNTIME, 'config.yaml') });
  if (r.ok) {
    commitApplied(built.applied);   // 热加载成功才算"已生效"，否则徽章会提前消失
    return { ok: true, msg: '配置已热加载', ...carry, pendingRestart: computePendingRestart() };
  }
  return {
    ok: false,
    msg: '热加载失败：' + (r.msg || '未知原因'),
    ...carry,
    pendingRestart: computePendingRestart(),   // 失败时旧值仍在跑 → 该挂的徽章要挂出来
  };
}

function reloadRuntime() { return withGate(reloadRuntimeInner); }

// ---------------------------------------------------------------- 规则预判
// 不做"猜规则"：直接让内核真实建一条连接，然后从 /connections 或内核日志里读它实际命中了什么。
// 这样 GEOSITE / RULE-SET 这些本地算不出来的规则也能给准确答案。

function connectViaProxy(host, port, timeoutMs) {
  return new Promise((resolve) => {
    const req = http.request({
      host: '127.0.0.1',
      port: settings.mixedPort,
      method: 'CONNECT',
      path: `${host}:${port}`,
      timeout: timeoutMs || 5000,
      headers: { Host: `${host}:${port}` },
    });
    let settled = false;
    const done = (r) => { if (!settled) { settled = true; resolve(r); } };
    req.on('connect', (res, socket) => {
      setTimeout(() => {
        try { socket.destroy(); } catch { /* 已断 */ }
        done({ ok: true, status: res.statusCode });
      }, 280);
    });
    req.on('timeout', () => { req.destroy(); done({ ok: false, msg: '连接超时（节点可能不通）' }); });
    req.on('error', (e) => done({ ok: false, msg: e.code === 'ECONNREFUSED' ? '内核端口未监听' : e.message }));
    req.end();
  });
}

async function diagnoseHost(rawHost) {
  const host = String(rawHost || '').trim().replace(/^https?:\/\//i, '').split('/')[0].split(':')[0];
  if (!host || !/^[a-zA-Z0-9._-]+$/.test(host)) return { ok: false, msg: '域名格式不对' };
  if (!coreRunning()) return { ok: false, msg: '内核没运行，先启动内核' };

  const t0 = Date.now();
  const conn = await connectViaProxy(host, 443);

  const safe = host.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const reLog = new RegExp('-->\\s+' + safe + ':(\\d+)\\s+match\\s+(\\S+)\\s+using\\s+(.+)$');

  let connHit = null;
  let logHit = null;

  // 两条路同时找：connections 给链路，日志给带参数的规则名（如 RuleSet(google)）
  for (let i = 0; i < 10; i++) {
    await sleep(150);
    if (!connHit) {
      const r = await apiJson('GET', '/connections');
      const list = (r.ok && r.data && r.data.connections) || [];
      connHit = list.find((c) => ((c.metadata && c.metadata.host) || '').toLowerCase() === host.toLowerCase()) || null;
    }
    if (!logHit) {
      for (let k = core.log.length - 1; k >= 0 && k > core.log.length - 200; k--) {
        const m = reLog.exec(core.log[k].line);
        if (m) { logHit = { port: m[1], rule: m[2], chain: m[3] }; break; }
      }
    }
    if (connHit && logHit) break;
  }

  const splitRule = (raw) => {
    const m = /^([A-Za-z0-9_-]+)\((.+)\)$/.exec(raw || '');
    return m ? { type: m[1], payload: m[2] } : { type: raw || '', payload: '' };
  };

  if (connHit || logHit) {
    const ruleStr = (logHit && logHit.rule) || (connHit && connHit.rule) || '';
    const { type, payload } = splitRule(ruleStr);
    return {
      ok: true,
      host,
      ms: Date.now() - t0,
      source: connHit ? 'connections' : 'log',
      ruleType: type,
      rulePayload: payload || (connHit && connHit.rulePayload) || '',
      rule: ruleStr,
      chains: (connHit && connHit.chains) || (logHit ? [logHit.chain.split('[')[0].trim()] : []),
      proxy: ((connHit && connHit.chains) || [])[0] || (logHit ? logHit.chain.split('[')[0].trim() : ''),
      dest: (() => {
        const m = connHit && connHit.metadata;
        if (m && m.destinationIP) return `${m.destinationIP}:${m.destinationPort || ''}`;
        return host + ':' + ((m && m.destinationPort) || (logHit && logHit.port) || 443);
      })(),
    };
  }

  return {
    ok: false,
    host,
    ms: Date.now() - t0,
    msg: conn.ok ? '连接已建立但没抓到规则（目标可能瞬间断开，换个大一点的站点再试）' : (conn.msg || '没拿到规则'),
  };
}

// ---------------------------------------------------------------- 多订阅合并
// 做法是 mihomo 的标准组合方式：基础配置提供规则和组结构，补充订阅以 proxy-provider 身份
// 被各策略组 use 引用。生成后必须过 mihomo -t 才算成功 —— 生成一份起不来的配置比不生成更糟。

function insertUseIntoGroups(text, providerNames) {
  const lines = text.split(/\r?\n/);
  const start = lines.findIndex((l) => /^proxy-groups\s*:/.test(l));
  if (start < 0) return { ok: false, msg: '基础配置里没有 proxy-groups' };
  let end = lines.length;
  for (let i = start + 1; i < lines.length; i++) {
    if (/^[A-Za-z0-9_.-]+\s*:/.test(lines[i])) { end = i; break; }
  }

  // 组项的缩进以 section 里第一个 "- " 为准；只有同缩进的才是组，
  // 否则 proxies 列表里的 "- 节点名" 会被误当成组（这是第一版合并校验失败的原因）。
  let itemIndent = -1;
  for (let i = start + 1; i < end; i++) {
    const m = /^(\s*)-\s+/.exec(lines[i]);
    if (m) { itemIndent = m[1].length; break; }
  }
  if (itemIndent < 0) return { ok: false, msg: 'proxy-groups 是空的，无法合并' };

  const items = [];
  for (let i = start + 1; i < end; i++) {
    const m = /^(\s*)-\s+/.exec(lines[i]);
    if (m && m[1].length === itemIndent) items.push({ idx: i });
  }

  let skippedInline = 0;
  for (let k = items.length - 1; k >= 0; k--) {
    const it = items[k];
    if (lines[it.idx].includes('{')) { skippedInline++; continue; }   // 内联 map 写法跳过
    const nextStart = k + 1 < items.length ? items[k + 1].idx : end;
    const pad = ' '.repeat(itemIndent + 2);
    const block = [pad + 'use:'].concat(providerNames.map((n) => pad + '  - ' + n));
    lines.splice(nextStart, 0, ...block);
  }
  return { ok: true, text: lines.join('\n'), skippedInline };
}

function addProxyProviders(text, providers) {
  const block = providers.flatMap((p) => ([
    `  ${p.name}:`,
    '    type: http',
    `    url: "${p.url}"`,
    `    path: ./providers/${p.name}.yaml`,
    '    interval: 3600',
  ]));
  const lines = text.split(/\r?\n/);
  const idx = lines.findIndex((l) => /^proxy-providers\s*:/.test(l));
  if (idx >= 0) {
    lines.splice(idx + 1, 0, ...block);
    return lines.join('\n');
  }
  return text.trimEnd() + '\n\nproxy-providers:' + '\n' + block.join('\n') + '\n';
}

async function validateConfigText(text) {
  const tmp = path.join(RUNTIME, '.validate');
  fs.mkdirSync(tmp, { recursive: true });
  const file = path.join(tmp, 'config.yaml');
  fs.writeFileSync(file, text, 'utf8');
  ensureGeoAssets();
  ensureRulesetAssets();
  const r = await runExe(settings.corePath, ['-t', '-d', RUNTIME, '-f', file], 25000);
  return { ok: r.ok, detail: ((r.out || '') + '\n' + (r.err || '')).trim().split(/\r?\n/).slice(-6).join('\n') };
}

async function mergeProfiles(baseFile, extraFiles, outName) {
  const base = safeName(baseFile || '');
  if (!base || !fs.existsSync(path.join(PROFILES, base))) return { ok: false, msg: '先选一份基础配置（它提供规则和组结构）' };
  const extras = (extraFiles || []).map(safeName).filter((f) => f && f !== base);
  if (!extras.length) return { ok: false, msg: '至少再选一份要合进来的订阅' };

  const subs = readSubs();
  const providers = [];
  const skipped = [];
  for (const f of extras) {
    const meta = subs[f];
    if (!meta || !meta.url) { skipped.push(f + '（没有订阅地址）'); continue; }
    providers.push({ name: safeName(f.replace(/\.ya?ml$/i, '')), url: meta.url, file: f });
  }
  if (!providers.length) return { ok: false, msg: '选中的配置都不是订阅导入的，无法作为 provider 合并' };

  let text = fs.readFileSync(path.join(PROFILES, base), 'utf8');
  text = addProxyProviders(text, providers);
  const ins = insertUseIntoGroups(text, providers.map((p) => p.name));
  if (!ins.ok) return { ok: false, msg: ins.msg };
  text = ins.text;

  const v = await validateConfigText(text);
  if (!v.ok) {
    pushLog('sys', '合并配置校验失败：' + v.detail.replace(/\n/g, ' | ').slice(0, 300));
    return { ok: false, msg: '生成的配置没通过内核校验，已放弃（不会写进配置列表）', detail: v.detail };
  }

  const name = safeName(outName || ('合并-' + base.replace(/\.ya?ml$/i, '')));
  const file = (name.endsWith('.yaml') ? name : name + '.yaml');
  atomicWrite(path.join(PROFILES, file), text);
  pushLog('sys', `已生成合并配置 ${file}（基础 ${base} + ${providers.length} 个订阅）`);
  return {
    ok: true,
    file,
    msg: `已生成 ${file}，内核校验通过`,
    providers: providers.map((p) => p.name),
    skipped,
    note: ins.skippedInline ? `有 ${ins.skippedInline} 个内联写法的组未插入 use` : '',
  };
}

// ---------------------------------------------------------------- 控制接口客户端

function apiJson(method, urlPath, body, timeoutMs) {
  return new Promise((resolve) => {
    const payload = body === undefined ? null : Buffer.from(JSON.stringify(body), 'utf8');
    const req = http.request({
      host: '127.0.0.1',
      port: settings.controllerPort,
      path: urlPath,
      method,
      // 默认 8 秒够所有读接口；PUT /configs 要重建 11 个规则集，必须单独放宽
      timeout: timeoutMs || 8000,
      headers: {
        Authorization: 'Bearer ' + settings.secret,
        'Content-Type': 'application/json',
        ...(payload ? { 'Content-Length': payload.length } : {}),
      },
    }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString('utf8');
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch { data = text; }
        if (res.statusCode >= 200 && res.statusCode < 300) resolve({ ok: true, data, status: res.statusCode });
        else resolve({ ok: false, status: res.statusCode, msg: (data && (data.message || data.error)) || text || ('HTTP ' + res.statusCode) });
      });
    });
    req.on('timeout', () => { req.destroy(); resolve({ ok: false, msg: '控制接口超时' }); });
    req.on('error', (e) => resolve({ ok: false, msg: e.code === 'ECONNREFUSED' ? '控制接口未监听（内核没起来？）' : e.message }));
    if (payload) req.write(payload);
    req.end();
  });
}

// ---------------------------------------------------------------- 辅助

function readProfiles() {
  const list = [];
  const subs = readSubs();
  for (const f of fs.readdirSync(PROFILES)) {
    if (!/\.ya?ml$/i.test(f)) continue;
    const st = fs.statSync(path.join(PROFILES, f));
    const meta = subs[f] || null;
    list.push({ file: f, size: st.size, mtime: st.mtimeMs, sub: meta, quota: subQuota(meta) });
  }
  list.sort((a, b) => b.mtime - a.mtime);
  return list;
}

function safeName(name) {
  return String(name || '').replace(/[^\w.\-\u4e00-\u9fa5]/g, '_').slice(0, 80) || 'profile';
}

/**
 * 把"配置文件名"变成绝对路径，并且拒绝一切越界。
 *
 * settings.activeProfile 来自配置文件、也可能来自 /api/config/activate 的输入。
 * 以前直接 path.join(PROFILES, activeProfile) 就读写 —— 若这个值被写成
 * `../../../Windows/System32/drivers/etc/hosts`，/api/config/raw 的 GET 会读出
 * 任意文件、POST 会覆盖任意文件。虽然只有本机可调，但"路径拼接不做校验"是
 * 一旦将来加了远程控制就变成提权的口子，没有理由留着。
 *
 * 判据：只允许单层文件名（不含分隔符）且必须以 .yaml/.yml 结尾，最终路径必须
 * 真的落在 PROFILES 里面（用 path.relative 判断，而不是 startsWith 前缀匹配 ——
 * 后者对 `profiles-evil` 这种同前缀兄弟目录无效）。
 */
function profilePath(name) {
  const base = String(name || '');
  if (!base || base !== path.basename(base)) return null;
  if (!/\.ya?ml$/i.test(base)) return null;
  const full = path.resolve(PROFILES, base);
  const rel = path.relative(PROFILES, full);
  if (!rel || rel.startsWith('..') || path.isAbsolute(rel)) return null;
  return full;
}

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
};

function sendJson(res, code, obj) {
  const buf = Buffer.from(JSON.stringify(obj), 'utf8');
  res.writeHead(code, { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': buf.length, 'Cache-Control': 'no-store' });
  res.end(buf);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (c) => {
      size += c.length;
      if (size > 12 * 1024 * 1024) { reject(new Error('请求体过大')); req.destroy(); return; }
      chunks.push(c);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

/**
 * 取请求体并解析成对象。解析失败返回 {}，调用方不需要自己 try。
 *
 * 以前 20 多处都是裸 `JSON.parse(await readBody(req) || '{}')`：一个畸形请求体会抛到
 * 最外层 catch，返回 500「接口异常」。界面把 500 当成"后端挂了"，弹出"后端未响应"，
 * 而真实原因只是某个字段传错。现在统一成 400 + 明确文案。
 */
function readJsonBody(req) {
  return readBody(req).then((text) => {
    if (!text || !text.trim()) return { ok: true, body: {} };
    try {
      const v = JSON.parse(text);
      if (v === null || typeof v !== 'object' || Array.isArray(v)) {
        return { ok: false, msg: '请求体必须是 JSON 对象' };
      }
      return { ok: true, body: v };
    } catch {
      return { ok: false, msg: '请求体不是合法 JSON' };
    }
  }, (e) => ({ ok: false, msg: e && e.message ? e.message : '读取请求体失败' }));
}

/**
 * 端口合法性。以前 webPort/mixedPort/socksPort/httpPort/controllerPort 五个字段
 * 一个校验都没有，写进去什么就是什么：
 *   - 68787（>65535）→ config.yaml 里 `mixed-port: 68787`，内核直接拒绝启动
 *   - 字符串 "7897x" → YAML 里变成裸字符串，同样是启动失败
 * 而界面上只显示"内核未运行"，用户完全看不出是端口写错了。
 */
function validPort(v) {
  // 只接受 number 或纯数字字符串。以前用 Number(v) 一刀切，会放过两种值：
  //   `true` → Number(true) === 1、`[7897]` → Number([7897]) === 7897
  // 它们会让 `mixed-port: true` 这种废配置写进 config.yaml，内核直接起不来。
  if (typeof v !== 'number' && typeof v !== 'string') return null;
  const n = Number(v);
  if (!Number.isInteger(n) || n < 1 || n > 65535) return null;
  return n;
}

/**
 * 写接口的来源校验。
 *
 * 侧车只监听 127.0.0.1，但这不等于"只有本机能调"——用户浏览器里的**任意网页**
 * 都能向 http://127.0.0.1:8787 发请求。带 JSON 的跨域请求会被预检挡住，
 * 但 `Content-Type: text/plain` 属于"简单请求"，不触发预检，而服务端以前
 * 不检查 Content-Type 就直接 JSON.parse —— 于是第三方页面可以用一行 fetch
 * 关掉内核、改订阅、开系统代理（CSRF）。
 *
 * 判据（2.6.8 修正过一次，别改回去）：
 *   ① 带了 Origin 就必须是本机面板自己的源 —— 浏览器发起的跨站请求一定带 Origin，
 *      这条挡住网页对 `/api/core/start` 这类**无 body** 的调用；
 *   ② 只有**真的带了 body** 时才要求 application/json。2.6.7 把这条写成"所有写请求都必须
 *      application/json"，结果 `POST /api/core/start`（我们自己的界面传 body=null，
 *      于是不发 Content-Type）被自己的闸挡了 —— 点"启动"直接弹"写接口只接受 application/json"。
 *      无 body 的请求本来也没有可解析的载荷，真正需要防的是"用 text/plain 夹带 JSON"。
 *   ③ 我们自己的界面（WPF HttpClient）不发 Origin，因此走 ① 直接放行。
 */
/**
 * 当前**实际监听**的面板端口。
 *
 * 不能用 `settings.webPort` —— 它是"下次启动用"的值：用户在设置里改端口的那一刻它就变了，
 * 而服务还在旧端口上跑。拿它拼白名单的后果是：保存成功之后，同一个页面发起的**每一个**写请求
 * 都被 403（连"改回去"也是写请求），只能重启后端才能恢复。
 */
function listenPort() {
  try {
    const a = server && server.address();
    if (a && typeof a === 'object' && a.port) return a.port;
  } catch { /* 还没 listen 或已关闭 */ }
  return settings.webPort;
}

function checkWriteOrigin(req) {
  const origin = req.headers.origin;
  if (origin) {
    const port = listenPort();
    const allowed = [
      `http://127.0.0.1:${port}`,
      `http://localhost:${port}`,
    ];
    if (!allowed.includes(origin)) return { ok: false, msg: '来源不被允许：' + origin };
  }

  // 只有"带了 body"才校验类型：Content-Length > 0，或分块传输
  const len = Number(req.headers['content-length'] || 0);
  const chunked = !!req.headers['transfer-encoding'];
  const hasBody = len > 0 || chunked;

  if (hasBody) {
    const ct = String(req.headers['content-type'] || '').toLowerCase();
    if (!ct.startsWith('application/json')) {
      return { ok: false, msg: '写接口只接受 application/json' };
    }
  }
  return { ok: true };
}

/**
 * 系统代理状态的缓存。
 *
 * `sysproxy.exe` 是 .NET Framework 单文件程序，冷启动实测 2 秒级；而快照每 2 秒拉一次。
 * 如果在 buildEnv 里每次都真去 spawn 它，等于每 2 秒起一个进程 —— 不可接受。
 * 所以：缓存 15 秒，且**只有明确要求刷新的调用方**（/api/env、/api/systemproxy、设置之后）
 * 才允许触发 spawn；/api/snapshot 只读缓存，读不到就给 null（界面渲染"未知"，不猜）。
 */
let _sysProxyCache = { at: 0, value: null };

async function sysProxyEnv(fresh) {
  const hasTool = fs.existsSync(settings.sysProxyPath);
  if (!hasTool) {
    // 缺口没有工具时**读也读不到**：enabled 必须是 null（"未知"），不能是 false（"没开"）。
    // 回 false 会让界面把"读不到"伪装成"确认关闭"，用户就没救了。
    return { enabled: null, pointsToOurs: false, server: '', pac: '', hasTool: false };
  }
  const stale = !_sysProxyCache.value || Date.now() - _sysProxyCache.at > 15000;
  if (fresh || stale) _sysProxyCache = { at: Date.now(), value: await sysProxyStatus() };
  const st = _sysProxyCache.value || {};
  return {
    // 工具在、但状态读失败（进程被拦/被锁）时仍是 null：宁可说"未知"，也不编一个"没开"。
    enabled: st.ok ? !!st.enabled : null,
    pointsToOurs: !!st.pointsToOurs,
    server: String(st.server || ''),
    pac: String(st.pac || ''),
    hasTool: true,
  };
}

/**
 * `/api/env` 与 `/api/snapshot.env` 共用的环境块。
 *
 * 必须同源：壳用 `/api/env` 的 `app` 判断"对面是不是我们的后端"，又用 `/api/snapshot`
 * 渲染状态。两处各拼一次，迟早出现"快照说可信、env 说不是我"这种自相矛盾。
 * `freshProxy` 只给"需要真实状态"的调用方开（见 sysProxyEnv 的注释）。
 */
async function buildEnv(opts) {
  const env = {
    app: 'clash-deck',   // 身份校验：壳的 PingAsync 靠它确认对面是自己人，避免 attach 到别人的后端
    isAdmin: await isAdmin(),
    tunEnable: settings.tunEnable,
    enhancedRules: settings.enhancedRules !== false,
    // 界面是读快照渲染的，所以这里必须有——只加在 /api/env 里不够，
    // 那会变成"后端已切换成功、界面每秒被空值刷回旧状态"。
    proxyMode: settings.proxyMode,
    logConn: settings.logConn !== false,
    logToFile: settings.logToFile !== false,
    // 0 = 永不删除。界面用这个值回填下拉框，缺了就会每次打开都退回默认 7 天
    logKeepDays: settings.logKeepDays,
    hasSysProxyTool: fs.existsSync(settings.sysProxyPath),
    sysProxy: await sysProxyEnv(!!(opts && opts.freshProxy)),
    testUrl: settings.testUrl,
    delayTimeout: settings.delayTimeout,
    coreRunning: coreRunning(),
    // ── 严格模式到底有没有生效（A8）──
    // 取自"最近一次真正生效的配置"（appliedTo），不是现算一遍：
    // 现算会得到"如果现在重启会怎样"，而用户问的是"现在跑的是什么"。
    // smart 模式下 strictApplied 是 null（不适用）—— 智能模式不存在"兜底该不该直连"，
    // 给 true/false 都会被界面当"已生效/未生效"渲染，那是两句假话选一句。
    strictApplied: appliedTo ? appliedTo.strictApplied : null,
    strictNote: appliedTo ? appliedTo.strictNote : '',
    // 哪些设置改了还没生效。内核没跑时恒为 []（见 computePendingRestart）
    pendingRestart: computePendingRestart(),
    // 规则集加载情况（A7）。内核没跑 / 还没查过时是 null，界面据此"没有就不显示"。
    // 正常时字段存在且 ok:true —— 界面要用它显示"分流规则 15/15 已加载"这类正向信息。
    rulesOk,
  };
  // 端口配置被回落过才有这个键。没有就不出现（不是空对象/空串）——
  // 界面按"字段在不在"决定要不要提示，避免重演 configRecovered 那种"空串常驻"。
  if (portFallback) {
    env.portFallback = { key: portFallback.key, value: portFallback.value, used: portFallback.used };
  }
  return env;
}

async function snapshot() {
  const running = coreRunning();
  const pack = {
    core: {
      running,
      pid: core.pid,
      uptime: running ? Math.round((Date.now() - core.startedAt) / 1000) : 0,
      lastExit: core.lastExit,
      version: null,
      connected: false,
    },
    settings: {
      mixedPort: settings.mixedPort,
      controllerPort: settings.controllerPort,
      corePath: settings.corePath,
      activeProfile: settings.activeProfile,
      webPort: settings.webPort,
    },
    profiles: readProfiles(),
    env: {
      ...(await buildEnv({ freshProxy: false })),
      subIntervalHours: settings.subIntervalHours,
      autostart: autostartEnabled(),
      speedTestUrl: settings.speedTestUrl,
      speedUploadUrl: settings.speedUploadUrl,
      speedTestBytes: settings.speedTestBytes,
      autoStartCore: settings.autoStartCore,
      autoStartProxy: settings.autoStartProxy,
      autoDelayMinutes: settings.autoDelayMinutes,
      // 配置文件在启动时读不出来、已被改名成 .bad-<时间戳> 时带出文件名，否则空串。
      // 界面用它提示一次「你的设置没能读出来」—— 以前这件事只写进 startup-error.log，
      // 没人会去翻那个文件，用户只会莫名其妙发现端口和订阅全变回默认值。
      configRecovered: configRecovery ? (configRecovery.backup || '(备份失败)') : '',
    },
    mode: null,
    traffic: { up: 0, down: 0, upTotal: 0, downTotal: 0, conns: 0 },
    groups: [],
    rulesCount: null,
  };
  if (!running) return pack;

  const [ver, cfg, conns, proxies] = await Promise.all([
    apiJson('GET', '/version'),
    apiJson('GET', '/configs'),
    apiJson('GET', '/connections'),
    apiJson('GET', '/proxies'),
  ]);

  if (ver.ok && ver.data) { pack.core.connected = true; pack.core.version = String(ver.data.version || (ver.data.meta ? 'meta' : '')).replace(/^v/i, ''); }
  if (cfg.ok && cfg.data) {
    pack.mode = cfg.data.mode || null;
    if (typeof cfg.data['mixed-port'] === 'number') pack.settings.mixedPort = cfg.data['mixed-port'];
  }
  if (conns.ok && conns.data && typeof conns.data === 'object') {
    pack.traffic.upTotal = conns.data.uploadTotal || 0;
    pack.traffic.downTotal = conns.data.downloadTotal || 0;
    pack.traffic.conns = Array.isArray(conns.data.connections) ? conns.data.connections.length : 0;
  }
  if (proxies.ok && proxies.data && proxies.data.proxies) {
    const map = proxies.data.proxies;
    const delayMap = {};
    for (const [name, p] of Object.entries(map)) {
      if (!p) continue;
      const hist = Array.isArray(p.history) && p.history.length ? p.history[p.history.length - 1] : null;
      if (hist && hist.delay) delayMap[name] = hist.delay;
    }
    for (const [name, p] of Object.entries(map)) {
      if (!p || !Array.isArray(p.all)) continue;
      if (p.type === 'Selector' || p.type === 'URLTest' || p.type === 'Fallback' || p.type === 'LoadBalance' || p.type === 'Relay') {
        const hist = Array.isArray(p.history) && p.history.length ? p.history[p.history.length - 1] : null;
        pack.groups.push({
          name,
          type: p.type,
          now: p.now || '',
          all: p.all,
          delays: delayMap,
          delayOfNow: hist ? hist.delay : (delayMap[p.now] || null),
        });
      }
    }
    pack.groups.sort((a, b) => (a.type === 'Selector' ? -1 : 1) - (b.type === 'Selector' ? -1 : 1));
  }
  return pack;
}

// ---------------------------------------------------------------- 路由

async function handleApi(req, res, url) {
  const p = url.pathname;
  const q = url.searchParams;

  // 写操作先过来源闸，再进业务。放在最前面，避免任何分支漏检。
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    const chk = checkWriteOrigin(req);
    if (!chk.ok) return sendJson(res, 403, { ok: false, msg: chk.msg });
  }

  if (p === '/api/snapshot' && req.method === 'GET') {
    // 规则集还在缺着就顺手再查一次（30 秒最多一次），不阻塞本次响应。
    // 修补是异步的：补完那一刻内核还在拉，只有下一轮才看得到结果。
    if (coreRunning() && rulesOk && !rulesOk.ok && Date.now() - rulesOk.checkedAt > 30000) {
      rulesOk.checkedAt = Date.now();          // 先占位，避免两轮快照叠着查
      checkRulesOk().catch(() => { /* 查不到就等下一轮 */ });
    }
    return sendJson(res, 200, await snapshot());
  }

  if (p === '/api/core/start' && req.method === 'POST') {
    const r = await startCore();
    return sendJson(res, r.ok ? 200 : 500, r);
  }
  if (p === '/api/core/stop' && req.method === 'POST') {
    return sendJson(res, 200, await stopCore());
  }
  if (p === '/api/core/restart' && req.method === 'POST') {
    const r = await restartCore();
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/client-hello' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const ua = String(body.ua || '').slice(0, 120);
    const m = /Edg\/([\d.]+)/.exec(ua) || /Chrome\/([\d.]+)/.exec(ua);
    pushLog('sys', `界面已就绪${m ? '（Chromium ' + m[1] + '）' : ''}`);
    return sendJson(res, 200, { ok: true });
  }

  if (p === '/api/systemproxy' && req.method === 'GET') {
    // 读一次**真**状态，并顺手刷新缓存。
    // 只返回不刷缓存会出一个很难查的坑：界面渲染读的是 snapshot.env.sysProxy（15 秒缓存），
    // 而"重试"按钮打的就是这个接口 —— 不刷缓存的话，用户点完重试，状态其实已经读到了，
    // 但界面最多还要等 15 秒才变，于是他会认为"重试没用"，再点几次还是没用。
    const st = await sysProxyStatus();
    if (st && st.ok) _sysProxyCache = { at: Date.now(), value: st };
    return sendJson(res, 200, st);
  }
  if (p === '/api/systemproxy' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    if (body.enable && !coreRunning()) {
      return sendJson(res, 400, { ok: false, msg: '内核没在运行，开了系统代理会直接断网' });
    }
    const r = await sysProxySet(!!body.enable);
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/env' && req.method === 'GET') {
    // 与 /api/snapshot.env 同一个来源（buildEnv）。这里允许刷系统代理状态：
    // 壳的 PingAsync / 设置页打开时才走这条路，频率低，值得拿真实值。
    return sendJson(res, 200, { ok: true, ...(await buildEnv({ freshProxy: true })) });
  }

  if (p === '/api/history' && req.method === 'GET') {
    const span = Number(q.get('seconds') || 1800);
    const since = Date.now() - span * 1000;
    const pts = history.samples.filter((s) => s.t >= since);
    return sendJson(res, 200, {
      ok: true,
      count: pts.length,
      samples: pts.map((s, i) => {
        const prev = i > 0 ? pts[i - 1] : pts[0];
        const dt = Math.max((s.t - prev.t) / 1000, 0.5);
        return { t: s.t, up: Math.max(0, (s.up - prev.up) / dt), down: Math.max(0, (s.down - prev.down) / dt), n: s.n };
      }),
    });
  }

  if (p === '/api/autostart' && req.method === 'GET') {
    return sendJson(res, 200, { ok: true, enabled: autostartEnabled(), path: AUTOSTART_VBS });
  }
  if (p === '/api/autostart' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const r = autostartSet(!!body.enable);
    pushLog('sys', r.msg);
    return sendJson(res, 200, r);
  }

  if (p === '/api/subscriptions' && req.method === 'GET') {
    return sendJson(res, 200, { ok: true, subs: readSubs() });
  }
  if (p === '/api/subscriptions/update' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const r = await updateSubscription(body.file);
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/settings' && req.method === 'GET') {
    const { secret, ...rest } = settings;
    return sendJson(res, 200, { ...rest, hasSecret: !!secret });
  }
  if (p === '/api/log' && req.method === 'POST') {
    // 界面把关键动作写进日志，排查"点了没反应"这类问题时不用靠猜
    const body = (await readJsonBody(req)).body || {};
    if (body.text) pushLog('sys', '[UI] ' + String(body.text).slice(0, 200));
    return sendJson(res, 200, { ok: true });
  }

  if (p === '/api/settings' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};

    // ① 端口：五个都必须落在 1–65535 的整数区间。
    //    这是"界面说保存成功、内核却起不来"最常见的原因，必须在写入前挡住。
    //    清单用模块级 PORT_KEYS（和 loadSettings 共用），别再在这里写第二份。
    for (const k of PORT_KEYS) {
      if (body[k] === undefined) continue;
      const v = validPort(body[k]);
      if (v === null) {
        return sendJson(res, 400, {
          ok: false,
          // 报中文端口名，不报字段名：界面弹的是这一句原文（以前是「端口 mixedPort=70000 不合法」）
          msg: `端口「${portLabel(k)}」${JSON.stringify(body[k])} 不合法，必须是 1–65535 的整数`,
        });
      }
      body[k] = v;
    }
    // 五个端口不能撞。撞了内核会以"address already in use"退出，同样只显示"未运行"。
    const chosen = {};
    for (const k of PORT_KEYS) {
      const v = body[k] !== undefined ? body[k] : settings[k];
      if (chosen[v]) {
        return sendJson(res, 400, { ok: false, msg: portConflictMsg(chosen[v], k, v, body) });
      }
      chosen[v] = k;
    }

    // ② 内核路径与 geo 目录：给的是路径就要求它真的存在，否则启动必然失败。
    if (body.corePath !== undefined && !fs.existsSync(String(body.corePath))) {
      return sendJson(res, 400, { ok: false, msg: '内核路径不存在：' + body.corePath });
    }
    if (body.geoSourceDir !== undefined && !fs.existsSync(String(body.geoSourceDir))) {
      return sendJson(res, 400, { ok: false, msg: 'geo 数据目录不存在：' + body.geoSourceDir });
    }

    // ③ allowLan 只接受布尔值。以前字符串 "false" 会被当成真值写进 config.yaml 的
    //    `allow-lan: true`，在公共网络里把混合端口开到局域网上，而且没有任何提示。
    if (body.allowLan !== undefined) {
      if (typeof body.allowLan !== 'boolean') {
        return sendJson(res, 400, { ok: false, msg: 'allowLan 只接受 true / false' });
      }
      if (body.allowLan && !settings.allowLan) {
        pushLog('sys', '已开启「允许局域网」：混合端口将对同网段设备开放，且没有认证，'
          + '公共网络下建议用完就关');
      }
    }

    for (const k of ['corePath', 'webPort', 'mixedPort', 'socksPort', 'httpPort', 'controllerPort', 'allowLan', 'geoSourceDir', 'subIntervalHours']) {
      if (body[k] !== undefined) settings[k] = body[k];
    }
    if (typeof body.tunEnable === 'boolean') settings.tunEnable = body.tunEnable;
    if (typeof body.enhancedRules === 'boolean') settings.enhancedRules = body.enhancedRules;
    if (typeof body.autoStartCore === 'boolean') settings.autoStartCore = body.autoStartCore;
    if (typeof body.autoStartProxy === 'boolean') settings.autoStartProxy = body.autoStartProxy;
    if (body.autoDelayMinutes !== undefined) {
      const m = Number(body.autoDelayMinutes);
      if (!(m >= 0 && m <= 1440)) return sendJson(res, 400, { ok: false, msg: '定时测速间隔需在 0–1440 分钟' });
      settings.autoDelayMinutes = Math.round(m);
    }
    if (typeof body.secret === 'string' && body.secret.trim()) settings.secret = body.secret.trim();
    if (body.testUrl !== undefined) {
      const u = normalizeTestUrl(body.testUrl);
      if (!u) return sendJson(res, 400, { ok: false, msg: '测速地址不合法，需要形如 https://example.com/204' });
      settings.testUrl = u;
    }
    if (body.delayTimeout !== undefined) {
      const t = Number(body.delayTimeout);
      if (!(t >= 1000 && t <= 30000)) return sendJson(res, 400, { ok: false, msg: '测速超时需在 1000–30000 毫秒之间' });
      settings.delayTimeout = Math.round(t);
    }
    // —— 带宽测速设置 ——
    if (body.speedTestUrl !== undefined) {
      const u = String(body.speedTestUrl || '').trim();
      if (!/^https?:\/\//i.test(u)) return sendJson(res, 400, { ok: false, msg: '下载测速地址必须是 http(s)，可含 {bytes} 占位符' });
      settings.speedTestUrl = u;
    }
    if (body.speedUploadUrl !== undefined) {
      const u = String(body.speedUploadUrl || '').trim();
      if (!/^https?:\/\//i.test(u)) return sendJson(res, 400, { ok: false, msg: '上传测速地址必须是 http(s)' });
      settings.speedUploadUrl = u;
    }
    if (body.speedTestBytes !== undefined) {
      const b = Number(body.speedTestBytes);
      if (!(b >= 1048576 && b <= 104857600)) return sendJson(res, 400, { ok: false, msg: '测速数据量需在 1–100 MB 之间' });
      settings.speedTestBytes = Math.round(b);
    }
    if (body.proxyMode === 'smart' || body.proxyMode === 'whitelist') {
      if (settings.proxyMode !== body.proxyMode) {
        pushLog('sys', `分流模式 → ${body.proxyMode === 'whitelist' ? '严格（兜底直连）' : '智能（兜底代理）'}`
          + '，重启内核后生效');
      }
      settings.proxyMode = body.proxyMode;
    }
    if (typeof body.logConn === 'boolean') {
      settings.logConn = body.logConn;
      connLogEnabled = body.logConn;
    }
    if (typeof body.logToFile === 'boolean') settings.logToFile = body.logToFile;
    if (body.logKeepDays !== undefined) {
      const d = Number(body.logKeepDays);
      // 只收 0 / 7 / 30：选项由界面给，后端不放开任意值，免得写进一个 99999 天等于没清
      if (![0, 7, 30].includes(d)) {
        return sendJson(res, 400, { ok: false, msg: '日志保留只能是 7 天、30 天或永不删除' });
      }
      settings.logKeepDays = d;
      // 立刻执行一次：用户改成 7 天就是想把旧文件清掉，等到下次启动才生效等于没改
      const removed = pruneLogs(d);
      pushLog('sys', d === 0
        ? '日志保留策略：永不删除（只受磁盘剩余空间限制）'
        : `日志保留策略：最近 ${d} 天${removed ? `（已清理 ${removed} 个旧文件）` : ''}`);
    }
    if (body.subIntervalHours !== undefined) {
      const h = Number(body.subIntervalHours);
      if (!(h >= 0 && h <= 168)) return sendJson(res, 400, { ok: false, msg: '订阅更新间隔需在 0–168 小时之间（0 = 不自动更新）' });
      settings.subIntervalHours = Math.round(h);
      // 订阅元数据里的 intervalHours 优先级更高，这里一并改写，
      // 否则"设置成 24 小时"会被旧值悄悄覆盖（这正是之前一直显示 6 小时的原因）。
      const subs = readSubs();
      let touched = 0;
      for (const f of Object.keys(subs)) {
        if (!subs[f] || !subs[f].url) continue;
        subs[f].intervalHours = settings.subIntervalHours;
        touched++;
      }
      if (touched) writeSubs(subs);
    }
    saveSettings(settings);
    // 用户主动保存过一次，磁盘上只剩合法值了 → 收掉"端口配置被回落"的提示。
    // 不清的话会重演 configRecovery 的老问题：提示挂满整场会话，而事实早已改变。
    portFallback = null;
    // 保存不改运行时（proxyMode / enhancedRules / tunEnable 要重启内核才生效），
    // 所以这里带出的是"当前实际在跑的那份配置"的状态，界面据此立刻挂徽章。
    return sendJson(res, 200, {
      ok: true,
      msg: '设置已保存（内核运行中需重启生效）',
      strictApplied: appliedTo ? appliedTo.strictApplied : null,
      strictNote: appliedTo ? appliedTo.strictNote : '',
      pendingRestart: computePendingRestart(),
    });
  }

  if (p === '/api/profiles' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const file = safeName(body.name || 'profile') + '.yaml';
    atomicWrite(path.join(PROFILES, file), body.content || '');
    return sendJson(res, 200, { ok: true, msg: '已保存 ' + file, file });
  }

  if (p === '/api/profiles/fetch' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    if (!/^https?:\/\//i.test(body.url || '')) return sendJson(res, 400, { ok: false, msg: '订阅地址必须是 http(s)' });
    try {
      const r = await fetch(body.url, { headers: { 'User-Agent': 'clash-deck/1.0' } });
      if (!r.ok) return sendJson(res, 400, { ok: false, msg: '订阅拉取失败 HTTP ' + r.status });
      const text = await r.text();
      const file = safeName(body.name || new URL(body.url).hostname) + '.yaml';
      atomicWrite(path.join(PROFILES, file), text);
      const remain = r.headers.get('subscription-userinfo') || '';
      const subs = readSubs();
      subs[file] = {
        url: body.url,
        intervalHours: Number(body.intervalHours || settings.subIntervalHours || 24),
        lastUpdate: Date.now(),
        lastStatus: 'ok',
        lastMsg: `${(text.length / 1024).toFixed(1)} KB`,
        userinfo: remain,
      };
      writeSubs(subs);
      return sendJson(res, 200, { ok: true, msg: '已下载 ' + file, file, remain, userinfo: remain });
    } catch (e) {
      return sendJson(res, 500, { ok: false, msg: '订阅拉取异常：' + e.message });
    }
  }

  if (p === '/api/profiles/activate' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const file = safeName(body.file);
    const src = profilePath(file);
    if (!src || !fs.existsSync(src)) return sendJson(res, 404, { ok: false, msg: '配置不存在' });
    settings.activeProfile = file;
    saveSettings(settings);
    const r = await reloadRuntime();
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/profiles' && req.method === 'DELETE') {
    const file = safeName(q.get('file'));
    if (!profilePath(file)) return sendJson(res, 400, { ok: false, msg: '文件名不合法' });
    if (file === settings.activeProfile) return sendJson(res, 400, { ok: false, msg: '不能删除正在使用的配置' });
    try {
      fs.unlinkSync(path.join(PROFILES, file));
      const subs = readSubs();
      if (subs[file]) { delete subs[file]; writeSubs(subs); }
      try { fs.unlinkSync(path.join(PROFILES, file + '.bak')); } catch { /* 没备份 */ }
      return sendJson(res, 200, { ok: true, msg: '已删除' });
    } catch (e) { return sendJson(res, 500, { ok: false, msg: e.message }); }
  }

  if (p === '/api/proxies/select' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const r = await apiJson('PUT', '/proxies/' + encodeURIComponent(body.group), { name: body.name });
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/logs/export' && req.method === 'GET') {
    // 导出一份可直接发给别人排障的日志（已脱敏）
    try {
      const wanted = q.get('stream');   // all | core | conn | sys
      const lines = core.log
        .filter((x) => !wanted || wanted === 'all' || x.stream === wanted)
        .map((x) => x.t + ' [' + x.stream + '] ' + redactLine(x.line));
      return sendJson(res, 200, {
        ok: true,
        count: lines.length,
        text: lines.join('\n'),
        dir: LOG_DIR,
        note: '凭据类内容（订阅查询串、token/key/secret、长哈希）已按规则打码',
      });
    } catch (e) {
      return sendJson(res, 500, { ok: false, msg: '导出失败：' + e.message });
    }
  }

  if (p === '/api/update/check' && req.method === 'GET') {
    return sendJson(res, 200, await checkUpdate());
  }
  if (p === '/api/update/auto' && req.method === 'GET') {
    return sendJson(res, 200, { ok: true, info: autoUpdateInfo });
  }
  if (p === '/api/update/progress' && req.method === 'GET') {
    return sendJson(res, 200, { ok: true, progress: updateProgress });
  }
  if (p === '/api/update/download' && req.method === 'POST') {
    return sendJson(res, 200, await downloadUpdate());
  }
  if (p === '/api/update/apply' && req.method === 'POST') {
    return sendJson(res, 200, applyUpdate());
  }

  if (p === '/api/speedtest' && req.method === 'GET') {
    const mode = (q.get('mode') || 'down').toLowerCase() === 'up' ? 'up' : 'down';
    return sendJson(res, 200, await speedTestRun(mode, q.get('bytes')));
  }

  if (p === '/api/proxies/delay' && req.method === 'GET') {
    const group = q.get('group');
    const name = q.get('name');
    const timeout = q.get('timeout') || String(settings.delayTimeout || 5000);
    // 测速地址：请求参数优先（临时测一次）> 设置里的值 > 内置默认
    const target = normalizeTestUrl(q.get('url')) || settings.testUrl || DEFAULT_TEST_URL;
    if (name) {
      const r = await apiJson('GET', `/proxies/${encodeURIComponent(name)}/delay?timeout=${timeout}&url=${encodeURIComponent(target)}`);
      const delay = r.ok ? (r.data && r.data.delay) : 0;
      recordDelay(name, delay);
      return sendJson(res, 200, { ok: r.ok, name, delay: delay || null, url: target, msg: r.msg });
    }
    if (!group) return sendJson(res, 400, { ok: false, msg: '缺少 group 或 name' });
    const snap = await snapshot();
    const g = snap.groups.find((x) => x.name === group);
    if (!g) return sendJson(res, 404, { ok: false, msg: '分组不存在' });
    const results = await Promise.all(g.all.slice(0, 60).map(async (n) => {
      const r = await apiJson('GET', `/proxies/${encodeURIComponent(n)}/delay?timeout=${timeout}&url=${encodeURIComponent(target)}`);
      const delay = r.ok ? (r.data && r.data.delay) : 0;
      recordDelay(n, delay);
      return { name: n, delay: delay || null };
    }));
    return sendJson(res, 200, { ok: true, url: target, results });
  }

  if (p === '/api/mode' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    if (!['rule', 'global', 'direct'].includes(body.mode)) return sendJson(res, 400, { ok: false, msg: '非法模式' });
    const r = await apiJson('PATCH', '/configs', { mode: body.mode });
    // 以前直接把内核响应透传，成功时没有 msg，界面日志里就是 "tray mode rule -> "
    // （空一段），排障时看不出到底成没成。
    const label = { rule: '规则', global: '全局', direct: '直连' }[body.mode] || body.mode;
    return sendJson(res, r.ok ? 200 : 500, {
      ok: r.ok,
      msg: r.ok ? `代理模式已切换为「${label}」` : (r.msg || '切换代理模式失败'),
    });
  }

  if (p === '/api/connections' && req.method === 'GET') {
    const r = await apiJson('GET', '/connections');
    if (!r.ok) return sendJson(res, 500, r);
    const list = (r.data.connections || []).map((c) => {
      const m = c.metadata || {};
      return {
        id: c.id,
        host: m.host || m.destinationIP || '',
        dest: `${m.destinationIP || m.host || ''}:${m.destinationPort || ''}`,
        source: `${m.sourceIP || ''}:${m.sourcePort || ''}`,
        net: m.network || '',
        type: m.type || '',
        process: m.processPath || '',
        chains: c.chains || [],
        rule: c.rule || '',
        rulePayload: c.rulePayload || '',
        upload: c.upload || 0,
        download: c.download || 0,
        start: c.start || '',
        duration: c.start ? Math.max(0, Math.round((Date.now() - new Date(c.start).getTime()) / 1000)) : 0,
      };
    });
    list.sort((a, b) => (b.download + b.upload) - (a.download + a.upload));
    return sendJson(res, 200, { ok: true, total: list.length, list: list.slice(0, 200) });
  }
  if (p === '/api/usage' && req.method === 'GET') {
    const days = Math.min(365, Math.max(1, Number(q.get('days') || 30)));
    return sendJson(res, 200, { ok: true, ...readUsage(days) });
  }

  if (p === '/api/delay-history' && req.method === 'GET') {
    const name = q.get('name');
    if (!name) {
      const all = {};
      for (const k of delayHist.keys()) all[k] = delayStats(k);
      return sendJson(res, 200, { ok: true, all });
    }
    return sendJson(res, 200, { ok: true, name, ...(delayStats(name) || { empty: true }) });
  }

  if (p === '/api/backup' && req.method === 'GET') {
    const subs = readSubs();
    const profiles = readProfiles().map((x) => ({
      file: x.file,
      content: fs.readFileSync(path.join(PROFILES, x.file), 'utf8'),
    }));
    return sendJson(res, 200, {
      ok: true,
      bundle: {
        app: 'clash-deck',
        v: 1,
        at: Date.now(),
        settings: {
          testUrl: settings.testUrl,
          delayTimeout: settings.delayTimeout,
          subIntervalHours: settings.subIntervalHours,
          autoStartCore: settings.autoStartCore,
          autoStartProxy: settings.autoStartProxy,
          autoDelayMinutes: settings.autoDelayMinutes,
          tunEnable: settings.tunEnable,
          allowLan: settings.allowLan,
        },
        profiles,
        subs,
      },
    });
  }

  if (p === '/api/restore' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const bundle = body.bundle || body;
    if (!bundle || !Array.isArray(bundle.profiles)) return sendJson(res, 400, { ok: false, msg: '备份包格式不对（缺 profiles）' });
    let written = 0, skipped = 0;
    for (const pf of bundle.profiles) {
      const file = safeName(pf.file);
      const target = path.join(PROFILES, file);
      if (fs.existsSync(target) && !body.overwrite) { skipped++; continue; }
      atomicWrite(target, String(pf.content || ''));
      written++;
    }
    if (bundle.subs && typeof bundle.subs === 'object') {
      const subs = readSubs();
      for (const [k, v] of Object.entries(bundle.subs)) {
        const file = safeName(k);
        if (!fs.existsSync(path.join(PROFILES, file))) continue;
        subs[file] = v;
      }
      writeSubs(subs);
    }
    if (bundle.settings && typeof bundle.settings === 'object') {
      for (const k of ['testUrl', 'delayTimeout', 'subIntervalHours', 'autoDelayMinutes']) {
        if (bundle.settings[k] !== undefined) settings[k] = bundle.settings[k];
      }
      for (const k of ['autoStartCore', 'autoStartProxy', 'tunEnable', 'allowLan']) {
        if (typeof bundle.settings[k] === 'boolean') settings[k] = bundle.settings[k];
      }
      settings.testUrl = normalizeTestUrl(settings.testUrl) || DEFAULT_TEST_URL;
      saveSettings(settings);
    }
    pushLog('sys', `恢复备份：写入 ${written} 份配置，跳过 ${skipped} 份已存在`);
    return sendJson(res, 200, { ok: true, msg: `已恢复 ${written} 份配置${skipped ? `，跳过 ${skipped} 份已存在的` : ''}`, written, skipped });
  }

  if (p === '/api/merge' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const r = await mergeProfiles(body.base, body.extras || [], body.name);
    return sendJson(res, r.ok ? 200 : 400, r);
  }

  if (p === '/api/providers/rules' && req.method === 'GET') {
    const r = await apiJson('GET', '/providers/rules');
    if (!r.ok) return sendJson(res, 500, r);
    const map = (r.data && r.data.providers) || {};
    const list = Object.entries(map).map(([name, v]) => ({
      name,
      behavior: v.behavior || '',
      ruleCount: v.ruleCount || 0,
      updatedAt: v.updatedAt || '',
      format: v.format || '',
      vehicle: v.vehicleType || '',
    }));
    return sendJson(res, 200, { ok: true, count: list.length, providers: list });
  }

  /**
   * 手动重试"没加载的规则集"。
   *
   * 启动时那次修复是后台跑的，网络刚好不通就会失败；用户在概览的告警条上点一下，
   * 就按"本地 → 代理"两条路重来一次，不用重启内核。
   */
  if (p === '/api/rules/repair' && req.method === 'POST') {
    if (!coreRunning()) return sendJson(res, 400, { ok: false, msg: '内核没在运行，先启动内核' });
    await checkRulesOk();
    if (rulesOk && !rulesOk.ok && rulesOk.empty.length) {
      const fixed = await repairProviders(rulesOk.empty);
      // 这里等用户点完要看到结果，试的次数收一点：5 轮约 20 秒，客户端超时是 45 秒
      if (fixed) await recheckRulesUntilOk(5, 1500);
      if (!fixed) {
        return sendJson(res, 200, {
          ok: false,
          msg: `还有 ${rulesOk.empty.length} 个规则集补不下来（直连与代理都不通）：${rulesOk.empty.join(', ')}`,
          rulesOk,
        });
      }
    }
    return sendJson(res, 200, { ok: !!(rulesOk && rulesOk.ok), rulesOk });
  }

  if (p === '/api/providers/rules/update' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    if (body.all) {
      const r = await apiJson('GET', '/providers/rules');
      const names = Object.keys((r.ok && r.data && r.data.providers) || {});
      const out = [];
      for (const n of names) {
        const up = await apiJson('PUT', '/providers/rules/' + encodeURIComponent(n));
        out.push({ name: n, ok: up.ok, msg: up.msg });
      }
      pushLog('sys', `规则集全部更新：${out.filter((x) => x.ok).length}/${out.length} 成功`);
      return sendJson(res, 200, { ok: true, msg: `已更新 ${out.filter((x) => x.ok).length}/${out.length} 个规则集`, results: out });
    }
    if (!body.name) return sendJson(res, 400, { ok: false, msg: '缺少规则集名' });
    const up = await apiJson('PUT', '/providers/rules/' + encodeURIComponent(body.name));
    pushLog('sys', `规则集更新 ${body.name}：${up.ok ? '成功' : '失败'}`);
    return sendJson(res, up.ok ? 200 : 500, { ok: up.ok, msg: up.ok ? '已更新 ' + body.name : (up.msg || '更新失败') });
  }

  if (p === '/api/diagnose' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    const r = await diagnoseHost(body.host);
    pushLog('sys', `规则预判 ${body.host} → ${r.ok ? (r.rule + ' / ' + (r.proxy || '')) : (r.msg || '失败')}`);
    return sendJson(res, r.ok ? 200 : 400, r);
  }

  if (p.startsWith('/api/connections/') && req.method === 'DELETE') {
    const id = decodeURIComponent(p.slice('/api/connections/'.length));
    const r = await apiJson('DELETE', '/connections/' + encodeURIComponent(id));
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/connections' && req.method === 'DELETE') {
    const r = await apiJson('DELETE', '/connections');
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  if (p === '/api/custom-rules') {
    if (req.method === 'GET') return sendJson(res, 200, { ok: true, rules: readCustomRules() });
    if (req.method === 'POST') {
      const body = (await readJsonBody(req)).body || {};
      let value = String(body.value || '').trim().toLowerCase()
        .replace(/^https?:\/\//i, '').split('/')[0].replace(/^\*?\./, '');
      if (!value || !/^[a-z0-9._-]+$/.test(value)) {
        return sendJson(res, 400, { ok: false, msg: '域名格式不对，只要域名本身，例如 example.com' });
      }
      const type = CUSTOM_TYPES.includes(String(body.type || '').toUpperCase())
        ? String(body.type).toUpperCase() : 'DOMAIN-SUFFIX';
      const action = body.action === 'direct' ? 'direct' : 'proxy';
      const list = readCustomRules();
      const dup = list.findIndex((r) => r.value === value);
      if (dup >= 0) list[dup] = { type, value, action, at: Date.now() };
      else list.push({ type, value, action, at: Date.now() });
      writeCustomRules(list);
      pushLog('sys', `自定义规则：${value} → ${action === 'direct' ? '直连' : '走代理'}`);
      return sendJson(res, 200, { ok: true, rules: list, msg: '已保存，重启内核后生效' });
    }
    if (req.method === 'DELETE') {
      const value = String(q.get('value') || '').trim().toLowerCase();
      if (!value) return sendJson(res, 400, { ok: false, msg: '缺少 value' });
      const list = readCustomRules().filter((r) => r.value !== value);
      writeCustomRules(list);
      return sendJson(res, 200, { ok: true, rules: list });
    }
    return sendJson(res, 405, { ok: false, msg: '不支持的方法' });
  }

  if (p === '/api/rules' && req.method === 'GET') {
    const r = await apiJson('GET', '/rules');
    if (!r.ok) return sendJson(res, 500, r);
    const raw = r.data.rules || [];
    const rows = raw.map((x) => {
      const ex = x.extra || {};
      return {
        type: x.type,
        payload: x.payload,
        proxy: x.proxy,
        hit: Number(ex.hitCount || 0),
        miss: Number(ex.missCount || 0),
        size: x.size,
      };
    });
    const totalHit = rows.reduce((a, b) => a + b.hit, 0);
    const maxHit = rows.reduce((a, b) => Math.max(a, b.hit), 0);
    const sort = q.get('sort') || 'hit';
    const sorted = sort === 'order' ? rows : rows.slice().sort((a, b) => b.hit - a.hit);
    return sendJson(res, 200, {
      ok: true,
      count: rows.length,
      totalHit,
      maxHit,
      sort,
      rules: sorted.slice(0, 500),
    });
  }

  if (p === '/api/logs' && req.method === 'GET') {
    const rawSince = Number(q.get('since') || 0);
    const since = Number.isFinite(rawSince) && rawSince > 0 ? Math.floor(rawSince) : 0;

    // 老协议（不带 v=2）：`since` 仍按"缓冲下标"解释，返回形状与 2.6.12 逐字一致。
    // 保留它不是懒得改，而是为了让**已发布的客户端**在新后端上不会变更糟：
    // 它们把 total 当游标（会停在 600，既有冻结行为），但如果服务端把 600 当序号解释，
    // 它们每轮都会收到同样那 600 行并反复追加 —— 那比冻结更糟。
    if (q.get('v') !== '2') {
      return sendJson(res, 200, { ok: true, lines: core.log.slice(since), total: core.log.length });
    }

    // 新协议：since 是单调序号（见 logSeq 的注释）
    const last = logSeq;                  // 已分配出去的最大序号
    const count = core.log.length;
    const oldest = count ? core.log[0].seq : last + 1;
    const reset = since > last;           // 序号空间回退 → 只可能是后端重启过
    let dropped = 0;
    let from = since + 1;
    if (since === 0) {
      // 首次请求 / reset 之后：客户端手里什么都没有，从最老的开始，**不报丢行**
      from = oldest;
    } else if (!reset && from < oldest) {
      // 这段区间已随 600 行回绕被永久丢弃（不可恢复），如实说出来
      dropped = oldest - from;
      from = oldest;
    }
    const lines = reset ? core.log.slice() : core.log.filter((l) => l.seq >= from);
    return sendJson(res, 200, {
      ok: true,
      lines,
      next: last,        // 客户端无条件采纳：下次请求把它当 since
      count,             // 当前缓冲行数（空态判据用它，不要用 next）
      dropped,           // 本次因回绕而永久丢失的行数
      reset,             // true = 后端重启过 / 游标无法对应 → 客户端清空重铺
      total: count,      // 【deprecated】= count，仅为老客户端不炸而保留
    });
  }

  if (p === '/api/config/raw' && req.method === 'GET') {
    if (!settings.activeProfile) return sendJson(res, 404, { ok: false, msg: '未激活配置' });
    const src = profilePath(settings.activeProfile);
    if (!src) return sendJson(res, 400, { ok: false, msg: '激活配置的文件名不合法' });
    const text = fs.readFileSync(src, 'utf8');
    return sendJson(res, 200, { ok: true, file: settings.activeProfile, text });
  }
  if (p === '/api/config/raw' && req.method === 'POST') {
    const body = (await readJsonBody(req)).body || {};
    if (!settings.activeProfile) return sendJson(res, 400, { ok: false, msg: '未激活配置' });
    const dst = profilePath(settings.activeProfile);
    if (!dst) return sendJson(res, 400, { ok: false, msg: '激活配置的文件名不合法' });
    if (typeof body.text !== 'string') return sendJson(res, 400, { ok: false, msg: '缺少 text 字段' });
    atomicWrite(dst, body.text);
    const r = await reloadRuntime();
    return sendJson(res, r.ok ? 200 : 500, r);
  }

  return sendJson(res, 404, { ok: false, msg: '未知接口 ' + p });
}

const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  try {
    if (url.pathname.startsWith('/api/')) return await handleApi(req, res, url);

    let rel = url.pathname === '/' ? '/index.html' : url.pathname;
    // 用 resolve + relative 判断越界，替代原来的 `startsWith(PUBLIC)`。
    // startsWith 是纯前缀匹配：PUBLIC 是 `...\public` 时，`...\public-x\secret`
    // 也满足它以 `...\public` 开头，于是同级兄弟目录被当成面板资源读出去了。
    const file = path.resolve(PUBLIC, path.normalize(rel).replace(/^([/\\])+/, ''));
    const relToPublic = path.relative(PUBLIC, file);
    if (relToPublic.startsWith('..') || path.isAbsolute(relToPublic)) {
      res.writeHead(403, { 'Content-Type': 'text/plain; charset=utf-8' }).end('forbidden');
      return;
    }
    fs.readFile(file, (err, buf) => {
      if (err) { res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' }).end('404'); return; }
      res.writeHead(200, { 'Content-Type': MIME[path.extname(file).toLowerCase()] || 'application/octet-stream' });
      res.end(buf);
    });
  } catch (e) {
    sendJson(res, 500, { ok: false, msg: e.message });
  }
});

/** 端口是否可绑（只测本机回环，与 server.listen 的范围一致）。 */
function isPortFree(port) {
  return new Promise((resolve) => {
    const probe = net.createServer();
    probe.once('error', () => resolve(false));
    probe.once('listening', () => probe.close(() => resolve(true)));
    probe.listen(port, '127.0.0.1');
  });
}

server.on('error', async (e) => {
  if (e && e.code === 'EADDRINUSE') {
    // 以前这里 exit(0)（成功退出）+ 只打 console.log：
    // ① 主程序无法区分"正常退出"和"端口被占"，只会显示"后端启动即退出"
    // ② 日志文件里一个字都没有，用户和我都查不到原因
    const busy = settings.webPort;
    // 用户实际可能自己另配过 mixedPort 等，别把候选挑成"服务起来内核又撞端口"。
    const taken = new Set([settings.mixedPort, settings.socksPort, settings.httpPort, settings.controllerPort]
      .filter((p) => Number(p) > 0));
    const free = [];
    for (let p = busy + 1; p <= busy + 24 && free.length < 3; p++) {
      if (p > 65535) break;
      if (taken.has(p)) continue;
      if (await isPortFree(p)) free.push(p);
    }

    let msg = `面板端口 ${busy} 已被占用，后端无法启动。` +
      '常见原因：已有一个实例在运行，或该端口被其它程序占用。';
    // 配置损坏时，端口其实是"默认值"而不是用户设的那个 —— 不合并进提示的话，
    // 用户会以为自己在设置里改过端口，越查越乱。
    if (configRecovery) {
      msg += configRecovery.backup
        ? `\n另外：配置文件无法解析（${configRecovery.reason}），已备份为 ${configRecovery.backup}，` +
          '本次是按默认设置启动的，上面这个端口号是默认值，不是你原来设的那个。'
        : `\n另外：配置文件无法解析（${configRecovery.reason}），且备份失败，本次按默认设置启动。`;
    }
    console.error(msg);
    noteStartupError(msg);
    // 结构化回执：主程序靠它渲染"端口被占 + 可换端口"的界面，不必去解析中文文案。
    // 前缀是约定，改动要同步 Backend.cs 里的解析。
    console.error('[[CD_FATAL]]' + JSON.stringify({
      code: 'EADDRINUSE',
      port: busy,
      free,
      configBackup: configRecovery ? configRecovery.backup : '',
      configReason: configRecovery ? configRecovery.reason : '',
    }));
    process.exit(1);
  }
  console.error(e);
  noteStartupError('服务监听失败：' + (e && e.message));
  process.exit(1);
});

/**
 * 启动自愈：上一次退出如果是"被强杀 / 断电 / 兜底 Environment.Exit 抢跑"，
 * 系统代理会残留成"指向本机混合端口"，而内核已经不在 ——
 * 浏览器每个请求都打到死端口，表现是"网站打不开、转圈很久"，但命令行 ping 一切正常。
 * 启动时检查一次并关掉它。
 */
async function healStaleProxy(reason) {
  try {
    if (coreRunning()) return;
    const st = await sysProxyStatus();
    if (!st.ok || !st.pointsToOurs) return;
    const r = await sysProxySet(false);
    pushLog('sys', `检测到上次退出残留的系统代理（${reason}），已自动关闭：${r.msg || ''}`);
  } catch (e) {
    pushLog('sys', '清理残留系统代理失败：' + (e && e.message ? e.message : e));
  }
}

pruneLogs();

// 只在启动时清一次不够：面板开着跑一整周（托盘常驻就是这么用的），
// 过期文件会一直堆到下次重启才消失。每 6 小时补一次，代价是一次 readdir。
setInterval(() => pruneLogs(), 6 * 3600 * 1000).unref?.();

/**
 * 更新包只在"下载完 → 安装前"这一小段时间有用。
 * 装完不删、或者下载后没装，就会一直躺在 data\update（一个 73MB）。
 * 启动时清掉超过 24 小时的包，state.json 与日志留着（体积可忽略）。
 */
function pruneUpdateDir() {
  try {
    if (!fs.existsSync(UPDATE_DIR)) return;
    const now = Date.now();
    let freed = 0;
    for (const f of fs.readdirSync(UPDATE_DIR)) {
      if (f === 'state.json' || f.endsWith('.log') || f.endsWith('.cmd')) continue;
      const fp = path.join(UPDATE_DIR, f);
      try {
        const st = fs.statSync(fp);
        if (!st.isFile() || now - st.mtimeMs < 24 * 3600 * 1000) continue;
        freed += st.size;
        fs.unlinkSync(fp);
      } catch { /* 单个文件删不掉就跳过 */ }
    }
    if (freed > 0) pushLog('sys', `已清理过期的更新包（释放 ${fmtBytes(freed)}）`);
  } catch { /* 清理失败不影响启动 */ }
}
pruneUpdateDir();

/**
 * 启动后静默检查一次更新：不弹窗、不阻塞启动。
 * 结果放内存，界面进「概览」时直接读，省得每次都要点一下才知道有没有新版。
 */
let autoUpdateInfo = null;
setTimeout(async () => {
  try {
    const r = await checkUpdate();
    if (r.ok && r.hasUpdate) {
      autoUpdateInfo = r;
      pushLog('sys', `发现新版本 v${r.latest}（当前 v${r.current}），可在「概览 → 版本更新」里升级`);
    } else if (r.ok) {
      pushLog('sys', `已是最新版本 v${r.current}`);
    }
  } catch { /* 静默失败，不影响使用 */ }
}, 8000);

server.listen(settings.webPort, '127.0.0.1', () => {
  const url = `http://127.0.0.1:${settings.webPort}`;
  try { fs.writeFileSync(path.join(DATA, 'server.pid'), String(process.pid), 'utf8'); } catch {}
  console.log(`Clash Deck: ${url}`);
  console.log(`core binary: ${settings.corePath}`);
  // 端口回落过的，把修正值写回磁盘。
  // 必须放在 listen 成功之后：8787 万一也被占，我们会走 EADDRINUSE 退出，
  // 那时不该已经把"未经验证的 8787"写进用户的 config.json（那会伪造出一个原值）。
  // 写回之后 portFallback 仍在内存里留一代，让用户这次能看到"你填的是 abc，已改用 8787"。
  if (portFallback) {
    try {
      saveSettings(settings);
      pushLog('sys', `端口配置已修正并写回 config.json：${portFallback.key} 原值 `
        + `${JSON.stringify(portFallback.value)} → ${portFallback.used}`);
    } catch (e) {
      pushLog('sys', '端口修正值写回失败（不影响本次启动）：' + (e && e.message));
    }
    noteStartupError('端口配置非法，已回落：' + portFallback.key + '='
      + JSON.stringify(portFallback.value) + ' → ' + portFallback.used);
  }
  if (!NO_BROWSER) {
    try {
      spawn('cmd', ['/c', 'start', '', url], { windowsHide: true, detached: true, stdio: 'ignore' }).unref();
    } catch { /* 打开浏览器失败不影响服务 */ }
  }
  if (settings.autoStartCore) {
    pushLog('sys', '已开启"启动时自动运行内核"，正在拉起内核');
    startCore().then(async (r) => {
      pushLog('sys', '自动启动内核：' + (r.msg || ''));
      if (r.ok && settings.autoStartProxy) {
        const p = await sysProxySet(true);
        pushLog('sys', '自动开启系统代理：' + (p.msg || ''));
      } else if (!r.ok) {
        await healStaleProxy('内核自动启动失败');
      }
    });
  } else {
    healStaleProxy('本次未自动启动内核');
  }
});

async function shutdown() {
  try { await stopCore(); } catch { /* 退出路上不再纠错 */ }
  try { fs.unlinkSync(path.join(DATA, 'server.pid')); } catch { /* 没这个文件 */ }
  process.exit(0);
}
process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);
process.on('exit', () => { try { fs.unlinkSync(path.join(DATA, 'server.pid')); } catch { /* 已删 */ } });

// ---------------------------------------------------------------- 启动参数
// 用命令行而不是环境变量：Windows 上 HTTPS_PROXY / https_proxy 同时存在时，
// .NET Framework 的 ProcessStartInfo.EnvironmentVariables 会因字典键冲突直接抛异常。
const ARGS = process.argv.slice(2);
const argOf = (n) => { const i = ARGS.indexOf(n); return i >= 0 && i + 1 < ARGS.length ? ARGS[i + 1] : null; };
const NO_BROWSER = process.env.CLASH_DECK_NO_BROWSER === '1' || ARGS.includes('--no-browser');

// ---------------------------------------------------------------- 父进程看门狗
// 主程序被"任务管理器 → 结束任务"强杀时，本进程会变成孤儿，
// 并且锁住 runtime\node.exe，导致之后卸载报"对路径 node.exe 的访问被拒绝"。
// 所以父进程一消失就自己退出（连续两次确认，避免误判）。
const PARENT_PID = Number(process.env.CLASH_DECK_PARENT_PID || argOf('--parent-pid') || 0);
if (PARENT_PID > 0) {
  let miss = 0;
  const timer = setInterval(async () => {
    let alive = true;
    try { process.kill(PARENT_PID, 0); } catch { alive = false; }
    if (alive) { miss = 0; return; }
    if (++miss < 2) return;
    clearInterval(timer);
    pushLog('sys', '父进程已退出，后端随之关闭');
    try { await stopCore(); } catch { /* 尽力而为 */ }
    try { closeLogStream(); } catch { /* 尽力而为 */ }
    setTimeout(() => process.exit(0), 200);
  }, 2000);
  if (timer.unref) timer.unref();
}
