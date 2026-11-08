'use strict';

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));

const state = {
  snap: null,
  prev: null,
  prevAt: 0,
  page: 'overview',
  history: [],
  env: { isAdmin: null, tunEnable: false, autostart: false, hasSysProxyTool: false },
  sysProxy: { enabled: false, server: '', pointsToOurs: false },
  logCursor: 0,
  logLines: [],
  rules: [],
  conns: [],
  nodeFilter: '',
  nodeSort: 'delay',
  connView: 'list',
  expandedId: null,
  logFilter: '',
  logLevel: 'all',
  logPaused: false,
  delayStats: {},
  usage: null,
  busy: false,
};

// ---------------------------------------------------------------- 工具
const FONT = '"Segoe UI Variable Text","Segoe UI","Microsoft YaHei UI",sans-serif';

function fmtBytes(n) {
  n = Number(n) || 0;
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return (i === 0 ? n.toFixed(0) : n.toFixed(n < 10 ? 2 : 1)) + ' ' + u[i];
}
const fmtSpeed = (v) => fmtBytes(v) + '/s';
const fmtDelay = (d) => (!d || d <= 0 ? '—' : d + 'ms');
const delayCls = (d) => (!d || d <= 0 ? '' : d < 200 ? 'good' : d < 500 ? 'mid' : 'bad');
const esc = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const hostOf = (u) => { try { return new URL(u).hostname; } catch { return u || ''; } };
/** 配置文件名的可读写法：订阅来的显示域名，本地导入的去掉扩展名 */
const profileLabel = (p) => (p && p.sub && p.sub.url ? hostOf(p.sub.url) : String(p.file || '').replace(/\.ya?ml$/i, ''));
const expireText = (expire) => {
  if (!expire) return '';
  const days = Math.ceil((expire * 1000 - Date.now()) / 86400000);
  if (days < 0) return '已过期';
  if (days === 0) return '今天到期';
  return days + ' 天后到期';
};

const ago = (ts) => {
  if (!ts) return '从未更新';
  const m = Math.round((Date.now() - ts) / 60000);
  if (m < 1) return '刚刚更新';
  if (m < 60) return m + ' 分钟前';
  const h = Math.round(m / 60);
  return h < 48 ? h + ' 小时前' : Math.round(h / 24) + ' 天前';
};


/** 开关外观由 state 驱动：同时写 checked 和 .on，避免 :checked 与真实状态脱节 */
function setSwitch(sel, on, disabled) {
  const input = $(sel);
  if (!input) return;
  input.checked = !!on;
  const track = input.nextElementSibling;
  if (track) track.classList.toggle('on', !!on);
  if (disabled !== undefined) input.disabled = !!disabled;
}

let toastTimer = null;
function toast(msg, kind) {
  const el = $('#toast');
  el.textContent = msg;
  el.className = 'toast show' + (kind ? ' ' + kind : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.className = 'toast'; }, 2600);
}

async function api(method, url, body) {
  const opt = { method, headers: {} };
  if (body !== undefined) {
    opt.headers['Content-Type'] = 'application/json';
    opt.body = JSON.stringify(body);
  }
  try {
    const r = await fetch(url, opt);
    let d = null;
    try { d = await r.json(); } catch { d = null; }
    return d || { ok: false, msg: 'HTTP ' + r.status };
  } catch {
    return { ok: false, msg: '面板无响应' };
  }
}

// ---------------------------------------------------------------- 主题
const THEME_KEY = 'clashdeck-theme';
function applyTheme(t) {
  document.documentElement.dataset.theme = t;
  $('#themeLabel').textContent = t === 'dark' ? '深色' : '浅色';
  $('#themeIcon').innerHTML = t === 'dark'
    ? '<path d="M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z"/>'
    : '<circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4"/>';
  localStorage.setItem(THEME_KEY, t);
  if (state.history.length) drawChart();
}
applyTheme(new URLSearchParams(location.search).get('theme')
  || localStorage.getItem(THEME_KEY)
  || (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'));
$('#btnTheme').addEventListener('click', () => {
  applyTheme(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');
});

// ---------------------------------------------------------------- 导航
$$('.nav button').forEach((b) => {
  b.addEventListener('click', () => {
    state.page = b.dataset.page;
    $$('.nav button').forEach((x) => x.classList.toggle('active', x === b));
    $$('.page').forEach((p) => p.classList.toggle('active', p.id === 'page-' + state.page));
    refreshPage();
  });
});

function refreshPage() {
  if (state.page === 'overview') { renderOverview(); drawChart(); }
  if (state.page === 'proxies') renderGroups($('#allGroups'), true, 0);
  if (state.page === 'conns') loadConns();
  if (state.page === 'rules') { loadRules(); loadRuleProviders(); }
  if (state.page === 'profiles') { renderProfiles(); fillSettings(); fillMergeOptions(); }
  if (state.page === 'logs') pollLogs();
}

// ---------------------------------------------------------------- 主轮询
async function tick() {
  const s = await api('GET', '/api/snapshot');
  if (!s || !s.core) {
    $('#pipCore').className = 'pip';
    $('#coreState').textContent = '面板未连接';
    return;
  }
  const now = Date.now();
  if (state.prev && state.prevAt) {
    const dt = Math.max((now - state.prevAt) / 1000, .2);
    s.traffic.up = Math.max(0, (s.traffic.upTotal - state.prev.traffic.upTotal) / dt);
    s.traffic.down = Math.max(0, (s.traffic.downTotal - state.prev.traffic.downTotal) / dt);
  }
  state.prev = s;
  state.prevAt = now;
  state.snap = s;

  if (!state.helloed) {
    state.helloed = true;
    api('POST', '/api/client-hello', { ua: navigator.userAgent });
  }

  renderTop(s);
  if (state.page === 'overview') renderOverview();
  if (state.page === 'proxies') renderGroups($('#allGroups'), true, 0);
  if (state.page === 'conns') loadConns();
  if (state.page === 'profiles') { renderProfiles(); fillSettings(); fillMergeOptions(); }
  if (state.page === 'logs') pollLogs();
}

function renderTop(s) {
  const on = s.core.running;
  $('#pipCore').className = 'pip' + (state.busy ? ' busy' : on ? ' on' : '');
  $('#coreState').textContent = state.busy ? '正在切换…' : on ? '内核运行中' : '内核未启动';
  $('#coreMeta').textContent = on
    ? `v${s.core.version || '?'} · ${s.core.uptime}s · ${s.traffic.conns} 连接`
    : (s.core.lastExit ? '上次异常退出' : '等待启动');

  const btn = $('#btnToggle');
  btn.textContent = state.busy ? '请稍候…' : on ? '停止' : '启动';
  btn.className = 'btn ' + (on ? 'stop' : 'primary');
  btn.disabled = state.busy;

  $('#upSpeed').textContent = fmtSpeed(s.traffic.up || 0);
  $('#downSpeed').textContent = fmtSpeed(s.traffic.down || 0);
  $('#connCount').textContent = s.traffic.conns || 0;
  $('#navConns').textContent = s.traffic.conns || 0;
  $('#navGroups').textContent = s.groups.length || 0;

  const sel = $('#profileSel');
  const cur = s.settings.activeProfile;
  const html = s.profiles.length
    ? s.profiles.map((p) => `<option value="${esc(p.file)}"${p.file === cur ? ' selected' : ''}>${esc(profileLabel(p))}</option>`).join('')
    : '<option value="">（无配置）</option>';
  if (sel.dataset.html !== html) { sel.innerHTML = html; sel.dataset.html = html; }

  $$('#modeSeg button').forEach((b) => b.classList.toggle('active', b.dataset.mode === s.mode));

  setSwitch('#swSysProxy', state.sysProxy.enabled, !s.core.running || !s.env.hasSysProxyTool);
  $('#sysProxyHint').textContent = state.sysProxy.enabled
    ? (state.sysProxy.pointsToOurs ? '已指向本面板 · ' + state.sysProxy.server : '已开启，指向 ' + (state.sysProxy.server || '未知'))
    : '未开启';
  setSwitch('#swTun', s.env.tunEnable);
  $('#tunHint').textContent = state.env.isAdmin === false ? '当前非管理员，无法启用' : '开启后需重启内核';
  setSwitch('#swAutostart', s.env.autostart);
  $('#adminHint').textContent = state.env.isAdmin === null ? '' : state.env.isAdmin ? '管理员权限' : '普通权限';
}

// ---------------------------------------------------------------- 概览
function renderOverview() {
  const s = state.snap;
  if (!s) return;
  $('#tDown').textContent = fmtSpeed(s.traffic.down || 0);
  $('#tUp').textContent = fmtSpeed(s.traffic.up || 0);
  $('#tDownTotal').textContent = '累计 ' + fmtBytes(s.traffic.downTotal);
  $('#tUpTotal').textContent = '累计 ' + fmtBytes(s.traffic.upTotal);

  $('#ovCore').innerHTML = [
    ['状态', s.core.running ? '运行中' : '已停止'],
    ['PID', s.core.pid || '—'],
    ['版本', s.core.version ? 'v' + s.core.version : '—'],
    ['模式', { rule: '规则', global: '全局', direct: '直连' }[s.mode] || '—'],
  ].map(([k, v]) => `<span class="k">${k}</span><span class="v">${esc(v)}</span>`).join('');

  const curP = s.profiles.find((p) => p.file === s.settings.activeProfile);
  const q = curP && curP.quota;
  $('#ovProfile').innerHTML = [
    ['文件', (s.settings.activeProfile || '未选择').replace(/\.ya?ml$/i, '')],
    ['剩余流量', q ? `${fmtBytes(q.remain)} / ${fmtBytes(q.total)}` : '—'],
    ['到期', q && q.expire ? expireText(q.expire) : '—'],
    ['节点数', (s.groups[0] ? s.groups[0].all.length : 0) + ' 个'],
  ].map(([k, v]) => `<span class="k">${k}</span><span class="v">${esc(v)}</span>`).join('');

  renderGroups($('#ovGroups'), false, 3);
  pullUsage();
}

function renderGroups(host, withTest, limit) {
  if (!host) return;
  const s = state.snap;
  if (!s || !s.core.running) {
    host.innerHTML = `<div class="empty"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><circle cx="12" cy="12" r="9"/><path d="M12 8v5M12 16h.01"/></svg>内核未运行，启动后这里会显示策略组</div>`;
    return;
  }
  const groups = limit ? s.groups.slice(0, limit) : s.groups;
  if (!groups.length) { host.innerHTML = '<div class="empty">没有策略组</div>'; return; }

  // 过滤与排序（只影响显示，不动内核里的真实顺序）
  const kws = String(state.nodeFilter || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  const sortMode = state.nodeSort || 'delay';
  const delayOf = (g, n) => ((g.delays && g.delays[n]) || (n === g.now ? g.delayOfNow : 0)) || 0;
  const pickNodes = (g) => {
    let arr = g.all.filter((n) => !kws.length || kws.every((k) => n.toLowerCase().includes(k)));
    if (sortMode === 'delay') arr = arr.slice().sort((a, b) => (delayOf(g, a) || 1e9) - (delayOf(g, b) || 1e9));
    else if (sortMode === 'delay-desc') arr = arr.slice().sort((a, b) => delayOf(g, b) - delayOf(g, a));
    else if (sortMode === 'name') arr = arr.slice().sort((a, b) => a.localeCompare(b, 'zh'));
    return arr;
  };
  const shown = groups.map(pickNodes).reduce((a, b) => a + b.length, 0);
  const total = groups.reduce((a, g) => a + g.all.length, 0);
  const tip = $('#proxyTip');
  if (tip) tip.textContent = kws.length ? `筛出 ${shown} / ${total} 个节点` : `共 ${total} 个节点`;

  host.innerHTML = groups.map((g) => {
    const nodes = pickNodes(g).map((n) => {
      const sel = n === g.now;
      const d = (g.delays && g.delays[n]) || (sel ? g.delayOfNow : 0);
      const st = state.delayStats && state.delayStats[n];
      const tip = st
        ? `最近 ${st.samples.length} 次：${st.samples.map((x) => x || '×').join(' / ')} ms · 平均 ${st.avg}ms · 丢包 ${st.lossRate}% · 点一下立刻重测`
        : '点一下单独测这个节点的延迟';
      const loss = st && st.lossRate > 0 ? `<span class="loss" title="丢包 ${st.lossRate}%">${st.lossRate}%</span>` : '';
      return `<div class="node${sel ? ' sel' : ''}" data-group="${esc(g.name)}" data-node="${esc(n)}">
        <span class="dot"></span>
        <span class="nm">${esc(n)}</span>
        ${loss}
        <span class="chip t ${delayCls(d)}" data-tn="${esc(n)}" title="${esc(tip)}">${fmtDelay(d)}</span>
      </div>`;
    }).join('');
    return `<div class="card group">
      <div class="card-h">
        <span class="gbadge${g.type === 'Selector' ? ' sel' : ''}">${esc(g.type)}</span>
        <h3>${esc(g.name)}</h3>
        <div class="grow"></div>
        <span class="now-node" title="${esc(g.now)}">${esc(g.now || '—')}</span>
        ${withTest ? `<button class="btn sm" data-test="${esc(g.name)}" title="测速地址：${esc(state.env.testUrl || '默认')}">测速</button>` : ''}
      </div>
      <div class="nodes">${nodes || '<div class="skel">没有匹配的节点</div>'}</div>
    </div>`;
  }).join('');

  $$('.node', host).forEach((el) => {
    el.addEventListener('click', async (ev) => {
      // 点延迟标签 = 只测这一个节点，不切换
      const chip = ev.target.closest('.chip.t');
      if (chip) {
        ev.stopPropagation();
        if (chip.dataset.busy) return;
        chip.dataset.busy = '1';
        const backup = chip.textContent;
        chip.textContent = '…';
        const r = await api('GET', '/api/proxies/delay?name=' + encodeURIComponent(chip.dataset.tn) + '&timeout=4000');
        chip.dataset.busy = '';
        if (r.ok && r.delay) {
          chip.textContent = r.delay + 'ms';
          chip.className = 'chip t ' + (r.delay < 200 ? 'good' : r.delay < 500 ? 'mid' : 'bad');
          toast(`${chip.dataset.tn}：${r.delay}ms`, 'ok');
        } else {
          chip.textContent = '超时';
          chip.className = 'chip t bad';
          toast(`${chip.dataset.tn}：测速失败`, 'bad');
        }
        void backup;
        return;
      }
      const r = await api('POST', '/api/proxies/select', { group: el.dataset.group, name: el.dataset.node });
      toast(r.ok ? '已切换到 ' + el.dataset.node : (r.msg || '切换失败'), r.ok ? 'ok' : 'bad');
      tick();
    });
  });
  $$('button[data-test]', host).forEach((btn) => {
    btn.addEventListener('click', async () => {
      btn.disabled = true; btn.textContent = '测速中';
      const r = await api('GET', '/api/proxies/delay?group=' + encodeURIComponent(btn.dataset.test));
      btn.disabled = false; btn.textContent = '测速';
      if (r.ok) toast(`${btn.dataset.test}：${r.results.filter((x) => x.delay).length}/${r.results.length} 可用`, 'ok');
      else toast(r.msg || '测速失败', 'bad');
      tick();
    });
  });
}

// ---------------------------------------------------------------- 延迟统计 / 流量统计
async function pullDelayStats() {
  const r = await api('GET', '/api/delay-history');
  if (r.ok && r.all) state.delayStats = r.all;
}

async function pullUsage() {
  const r = await api('GET', '/api/usage?days=30');
  if (!r.ok) return;
  state.usage = r;
  const fmtRow = (lab, o) => `<span class="k">${lab}</span><span class="v">↓ ${fmtBytes(o.down)} · ↑ ${fmtBytes(o.up)}</span>`;
  $('#ovUsage').innerHTML = [
    fmtRow('今日', r.today),
    fmtRow('本月', r.month),
    `<span class="k">采样</span><span class="v">${r.samples} 分钟</span>`,
  ].join('');
  const days = r.days.slice(-7);
  const max = Math.max(1, ...days.map((d) => d.down + d.up));
  $('#usageTip').textContent = days.length ? '最近 ' + days.length + ' 天' : '暂无数据';
  $('#usageBars').innerHTML = days.length
    ? days.map((d) => {
      const h = Math.max(4, Math.round(((d.down + d.up) / max) * 46));
      return `<div class="ub" title="${d.day}：↓ ${fmtBytes(d.down)} · ↑ ${fmtBytes(d.up)}">
        <i style="height:${h}px"></i><em>${d.day.slice(5)}</em></div>`;
    }).join('')
    : '<div class="skel" style="padding:6px 0">跑一会儿就有数据（每分钟记一笔）</div>';
}

// ---------------------------------------------------------------- 图表
function drawChart() {
  const cv = $('#chart');
  if (!cv) return;
  const dpr = window.devicePixelRatio || 1;
  const w = cv.clientWidth || 600;
  const h = 132;
  if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(h * dpr)) {
    cv.width = Math.round(w * dpr);
    cv.height = Math.round(h * dpr);
  }
  const ctx = cv.getContext('2d');
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, w, h);

  const pad = { l: 52, r: 12, t: 10, b: 20 };
  const iw = w - pad.l - pad.r;
  const ih = h - pad.t - pad.b;

  const cs = getComputedStyle(document.documentElement);
  const borderC = (cs.getPropertyValue('--border') || '#eee').trim();
  const text3 = (cs.getPropertyValue('--text-3') || '#999').trim();

  ctx.strokeStyle = borderC;
  ctx.lineWidth = 1;
  for (let i = 0; i <= 3; i++) {
    const y = pad.t + (ih / 3) * i;
    ctx.beginPath(); ctx.moveTo(pad.l, y); ctx.lineTo(w - pad.r, y); ctx.stroke();
  }

  const data = state.history;
  if (!data.length) {
    ctx.fillStyle = text3;
    ctx.font = '12px ' + FONT;
    ctx.fillText('启动内核后开始采样', pad.l + 6, pad.t + ih / 2);
    return;
  }

  // 时间轴
  ctx.fillStyle = text3;
  ctx.font = '10px ' + FONT;
  ctx.textAlign = 'left';
  ctx.fillText('-10 分钟', pad.l, h - 5);
  ctx.textAlign = 'right';
  ctx.fillText('现在', w - pad.r, h - 5);
  ctx.textAlign = 'center';
  ctx.fillText('-5 分钟', pad.l + iw / 2, h - 5);
  ctx.textAlign = 'left';

  const maxV = Math.max(1024, ...data.map((d) => Math.max(d.down, d.up))) * 1.18;
  const X = (i) => pad.l + (data.length === 1 ? iw / 2 : (i / (data.length - 1)) * iw);
  const Y = (v) => pad.t + ih - (v / maxV) * ih;

  ctx.font = '10px ' + FONT;
  ctx.textAlign = 'right';
  ctx.fillStyle = getComputedStyle(document.documentElement).getPropertyValue('--text-3') || '#999';
  for (let i = 0; i <= 3; i++) {
    ctx.fillText(fmtBytes(maxV * (1 - i / 3)) + '/s', pad.l - 8, pad.t + (ih / 3) * i + 3.5);
  }
  ctx.textAlign = 'left';

  const series = [
    { key: 'down', stroke: '#2f6df6', from: 'rgba(47,109,246,.28)', to: 'rgba(47,109,246,0)' },
    { key: 'up', stroke: '#f0a73d', from: 'rgba(240,167,61,.22)', to: 'rgba(240,167,61,0)' },
  ];
  for (const s of series) {
    const grad = ctx.createLinearGradient(0, pad.t, 0, pad.t + ih);
    grad.addColorStop(0, s.from);
    grad.addColorStop(1, s.to);
    ctx.beginPath();
    data.forEach((d, i) => (i ? ctx.lineTo(X(i), Y(d[s.key])) : ctx.moveTo(X(i), Y(d[s.key]))));
    ctx.strokeStyle = s.stroke;
    ctx.lineWidth = 1.8;
    ctx.lineJoin = 'round';
    ctx.stroke();
    ctx.lineTo(X(data.length - 1), pad.t + ih);
    ctx.lineTo(X(0), pad.t + ih);
    ctx.closePath();
    ctx.fillStyle = grad;
    ctx.fill();
  }

  // 悬停辅助线
  if (state.hover != null && state.hover < data.length) {
    const i = state.hover;
    const x = X(i);
    ctx.strokeStyle = (cs.getPropertyValue('--border-strong') || '#ccc').trim();
    ctx.setLineDash([3, 3]);
    ctx.beginPath(); ctx.moveTo(x, pad.t); ctx.lineTo(x, pad.t + ih); ctx.stroke();
    ctx.setLineDash([]);
    for (const s of series) {
      ctx.beginPath();
      ctx.arc(x, Y(data[i][s.key]), 3, 0, Math.PI * 2);
      ctx.fillStyle = s.stroke;
      ctx.fill();
    }
  }

  if (data.length < 12) {
    ctx.fillStyle = text3;
    ctx.font = '11px ' + FONT;
    ctx.textAlign = 'right';
    ctx.fillText('采样中…', w - pad.r - 4, pad.t + 12);
    ctx.textAlign = 'left';
  }
}

const chartEl = $('#chart');
chartEl.addEventListener('mousemove', (e) => {
  const rect = chartEl.getBoundingClientRect();
  const padL = 52, padR = 12;
  const x = e.clientX - rect.left;
  const iw = rect.width - padL - padR;
  const ratio = Math.min(1, Math.max(0, (x - padL) / iw));
  const n = state.history.length;
  if (!n) return;
  state.hover = Math.round(ratio * (n - 1));
  drawChart();
  const d = state.history[state.hover];
  const tip = $('#chartTip');
  const t = new Date(d.t);
  tip.textContent = `${String(t.getHours()).padStart(2, '0')}:${String(t.getMinutes()).padStart(2, '0')}:${String(t.getSeconds()).padStart(2, '0')}  ↓${fmtBytes(d.down)}/s  ↑${fmtBytes(d.up)}/s`;
  tip.style.left = Math.min(rect.width - 100, Math.max(100, x)) + 'px';
  tip.style.top = (e.clientY - rect.top) + 'px';
  tip.style.opacity = 1;
});
chartEl.addEventListener('mouseleave', () => {
  state.hover = null;
  $('#chartTip').style.opacity = 0;
  drawChart();
});
window.addEventListener('resize', () => { if (state.page === 'overview') drawChart(); });

async function pullHistory() {
  const r = await api('GET', '/api/history?seconds=600');
  if (!r.ok) return;
  state.history = r.samples || [];
  if (state.page === 'overview') drawChart();
}

// ---------------------------------------------------------------- 连接
const procName = (p) => (p || '').split(/[\\/]/).pop() || '未知进程';

function connRow(c) {
  const open = state.expandedId === c.id;
  const rows = [`<tr class="conn-row${open ? ' open' : ''}" data-id="${esc(c.id)}">
    <td class="ell" title="${esc(c.host)}">${esc(c.host || '—')}</td>
    <td class="ell" title="${esc((c.chains || []).join(' ← '))}">${esc((c.chains || []).join(' ← '))}</td>
    <td>${esc(c.rule || '')}</td>
    <td class="num">${fmtBytes(c.upload)}</td>
    <td class="num">${fmtBytes(c.download)}</td>
    <td class="ell" title="${esc(c.process)}">${esc(procName(c.process))}</td>
  </tr>`];
  if (open) {
    rows.push(`<tr class="conn-detail"><td colspan="6">
      <div class="detail-grid">
        <span>目标</span><span class="mono">${esc(c.host)} → ${esc(c.dest || '')}</span>
        <span>来源</span><span class="mono">${esc(c.source || '—')}</span>
        <span>类型</span><span>${esc(c.net || '')}${c.type ? ' / ' + esc(c.type) : ''}</span>
        <span>规则</span><span>${esc(c.rule || '')}${c.rulePayload ? '（' + esc(c.rulePayload) + '）' : ''}</span>
        <span>链路</span><span>${esc((c.chains || []).join(' ← ') || '—')}</span>
        <span>流量</span><span>↑ ${fmtBytes(c.upload)} · ↓ ${fmtBytes(c.download)}</span>
        <span>持续</span><span>${c.duration}s</span>
        <span>进程</span><span class="mono ell" title="${esc(c.process)}">${esc(c.process || '—')}</span>
      </div>
      <div style="margin-top:10px"><button class="btn sm danger" data-close="${esc(c.id)}">断开这一条</button></div>
    </td></tr>`);
  }
  return rows.join('');
}

function renderConnTable(list, kw) {
  const body = $('#connBody');
  const head = $('#connHead');
  if (state.connView === 'process') {
    head.innerHTML = '<tr><th>进程</th><th class="num">连接数</th><th class="num">上传</th><th class="num">下载</th><th>主要线路</th><th>规则</th></tr>';
    const m = new Map();
    for (const c of list) {
      const k = procName(c.process);
      const o = m.get(k) || { n: 0, up: 0, down: 0, chains: new Set(), rules: new Set() };
      o.n++; o.up += c.upload; o.down += c.download;
      (c.chains || []).forEach((x) => o.chains.add(x));
      if (c.rule) o.rules.add(c.rule);
      m.set(k, o);
    }
    const rows = [...m.entries()].sort((a, b) => (b[1].down + b[1].up) - (a[1].down + a[1].up));
    body.innerHTML = rows.map(([k, o]) => `<tr>
      <td class="ell" title="${esc(k)}">${esc(k)}</td>
      <td class="num">${o.n}</td>
      <td class="num">${fmtBytes(o.up)}</td>
      <td class="num">${fmtBytes(o.down)}</td>
      <td class="ell">${esc([...o.chains].join(' ← ') || '—')}</td>
      <td class="ell">${esc([...o.rules].join(' / ') || '—')}</td>
    </tr>`).join('') || '<tr><td colspan="6" class="skel">没有活动连接</td></tr>';
    return;
  }

  head.innerHTML = '<tr><th>主机</th><th>链路</th><th>规则</th><th class="num">上传</th><th class="num">下载</th><th>进程</th></tr>';
  if (!list.length) {
    body.innerHTML = `<tr><td colspan="6" class="skel">${kw ? '没有匹配的连接' : '当前没有活动连接'}</td></tr>`;
    return;
  }
  body.innerHTML = list.map(connRow).join('');

  $$('.conn-row', body).forEach((tr) => {
    tr.addEventListener('click', (ev) => {
      if (ev.target.closest('button')) return;
      state.expandedId = state.expandedId === tr.dataset.id ? null : tr.dataset.id;
      loadConns();
    });
  });
  $$('button[data-close]', body).forEach((b) => {
    b.addEventListener('click', async (ev) => {
      ev.stopPropagation();
      const r = await api('DELETE', '/api/connections/' + encodeURIComponent(b.dataset.close));
      toast(r.ok ? '已断开这一条' : (r.msg || '断开失败'), r.ok ? 'ok' : 'bad');
      state.expandedId = null;
      loadConns();
    });
  });
}

async function loadConns() {
  const r = await api('GET', '/api/connections');
  const body = $('#connBody');
  if (!r.ok) { body.innerHTML = '<tr><td colspan="6" class="skel">控制接口不可用</td></tr>'; return; }
  state.conns = r.list;
  const kw = ($('#connSearch').value || '').trim().toLowerCase();
  const list = kw
    ? r.list.filter((c) => (c.host + ' ' + c.process + ' ' + (c.chains || []).join(' ') + ' ' + c.rule).toLowerCase().includes(kw))
    : r.list;
  $('#connTip').textContent = `共 ${r.total} 条${kw ? `，匹配 ${list.length}` : ''}`;
  if (!state.expandedId && new URLSearchParams(location.search).get('expand') && list.length) {
    state.expandedId = list[0].id;
  }
  renderConnTable(list, kw);
}

// ---------------------------------------------------------------- 规则
async function loadRules() {
  const r = await api('GET', '/api/rules?sort=' + $('#ruleSort').value);
  const body = $('#ruleBody');
  if (!r.ok) { body.innerHTML = '<tr><td colspan="6" class="skel">控制接口不可用</td></tr>'; return; }
  state.rules = r.rules;
  const kw = ($('#ruleSearch').value || '').trim().toLowerCase();
  const rows = kw
    ? state.rules.filter((x) => (x.payload + ' ' + x.proxy + ' ' + x.type).toLowerCase().includes(kw))
    : state.rules;
  $('#ruleTip').textContent = `共 ${r.count} 条 · 累计命中 ${r.totalHit} 次${kw ? ` · 匹配 ${rows.length}` : ''}`;
  if (!rows.length) { body.innerHTML = '<tr><td colspan="6" class="skel">没有匹配的规则</td></tr>'; return; }
  body.innerHTML = rows.map((x, i) => {
    const pct = r.maxHit ? Math.max(1.5, (x.hit / r.maxHit) * 100) : 0;
    return `<tr>
      <td class="num" style="color:var(--text-3)">${i + 1}</td>
      <td><span class="gbadge">${esc(x.type)}</span></td>
      <td class="ell mono" title="${esc(x.payload)}">${esc(x.payload)}</td>
      <td>${esc(x.proxy)}</td>
      <td>${x.hit ? `<div class="bar"><i style="width:${pct}%"></i></div>` : '<span style="color:var(--text-3)">—</span>'}</td>
      <td class="num">${x.hit || 0}</td>
    </tr>`;
  }).join('');
}

// ---------------------------------------------------------------- 日志
function renderLogs() {
  const box = $('#logBox');
  const kws = String(state.logFilter || '').trim().toLowerCase().split(/\s+/).filter(Boolean);
  const lv = state.logLevel || 'all';
  const rows = state.logLines.filter((l) => {
    if (lv === 'sys' && l.stream !== 'sys') return false;
    if (lv === 'info' && !/level=info/.test(l.line)) return false;
    if (lv === 'warn' && !/level=(warn|error)/.test(l.line)) return false;
    if (kws.length) {
      const t = l.line.toLowerCase();
      if (!kws.every((k) => t.includes(k))) return false;
    }
    return true;
  });
  box.innerHTML = rows.map((l) => {
    const cls = l.stream === 'err' || /level=error/.test(l.line) ? 'err'
      : l.stream === 'sys' ? 'sys'
        : /level=warn/.test(l.line) ? 'warn' : '';
    return `<span class="${cls}"><span class="t">${l.t}</span>  ${esc(l.line)}</span>`;
  }).join('\n') || '<span class="t">（没有匹配的日志）</span>';
  if (!state.logPaused) box.scrollTop = box.scrollHeight;
}

async function pollLogs() {
  const r = await api('GET', '/api/logs?since=' + state.logCursor);
  if (!r.ok) return;
  if (r.total < state.logCursor) { state.logCursor = 0; state.logLines = []; }
  if (r.lines && r.lines.length) {
    state.logLines = state.logLines.concat(r.lines).slice(-1500);
    state.logCursor = r.total;
    if (!state.logPaused) renderLogs();
  }
}

// ---------------------------------------------------------------- 配置页
function renderProfiles() {
  const s = state.snap;
  if (!s) return;
  const cur = s.settings.activeProfile;
  const host = $('#profileList');
  $('#profileTip').textContent = `${s.profiles.length} 份配置`;
  if (!s.profiles.length) {
    host.innerHTML = `<div class="empty"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5"><path d="M14 3v5h5"/><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z"/></svg>还没有配置，从下面导入一份</div>`;
    return;
  }
  host.innerHTML = s.profiles.map((p) => {
    const sub = p.sub;
    const line = sub
      ? `<span class="${sub.lastStatus === 'fail' ? 'bad' : 'ok'}">${sub.lastStatus === 'fail' ? '更新失败' : hostOf(sub.url)}</span>
         <span>${ago(sub.lastUpdate)}</span>
         <span>${sub.intervalHours ? '每 ' + sub.intervalHours + 'h' : '手动更新'}</span>`
      : `<span>本地导入</span>`;
    return `<div class="pitem${p.file === cur ? ' act' : ''}">
      <div class="meta">
        <div class="nm">${esc(profileLabel(p))}</div>
        <div class="ln">${profileLabel(p) === p.file.replace(/\.ya?ml$/i, '') ? '' : `<span class="mono">${esc(p.file)}</span><span>·</span>`}${fmtBytes(p.size)}<span>·</span>${line}</div>
        ${p.quota ? `<div class="quota">
          <div class="bar"><i style="width:${p.quota.usedPct}%"></i></div>
          <div class="ql"><span>已用 ${fmtBytes(p.quota.used)} / ${fmtBytes(p.quota.total)}</span><span>剩余 <b>${fmtBytes(p.quota.remain)}</b></span>${p.quota.expire ? `<span>${expireText(p.quota.expire)}</span>` : ''}</div>
        </div>` : ''}
      </div>
      ${p.file === cur ? '<span class="tag">使用中</span>' : `<button class="btn sm" data-use="${esc(p.file)}">激活</button>`}
      ${sub ? `<button class="btn sm ghost" data-update="${esc(p.file)}">更新</button>` : ''}
      <button class="btn sm danger" data-del="${esc(p.file)}">删除</button>
    </div>`;
  }).join('');

  $$('button[data-use]', host).forEach((b) => b.addEventListener('click', async () => {
    const r = await api('POST', '/api/profiles/activate', { file: b.dataset.use });
    toast(r.msg || '已切换', r.ok ? 'ok' : 'bad');
    tick();
  }));
  $$('button[data-update]', host).forEach((b) => b.addEventListener('click', async () => {
    b.disabled = true; b.textContent = '更新中';
    const r = await api('POST', '/api/subscriptions/update', { file: b.dataset.update });
    toast(r.msg || '完成', r.ok ? 'ok' : 'bad');
    b.disabled = false; b.textContent = '更新';
    tick();
  }));
  $$('button[data-del]', host).forEach((b) => b.addEventListener('click', async () => {
    if (!confirm('删除 ' + b.dataset.del + '？')) return;
    const r = await api('DELETE', '/api/profiles?file=' + encodeURIComponent(b.dataset.del));
    toast(r.msg || '已删除', r.ok ? 'ok' : 'bad');
    tick();
  }));
}

function fillSettings() {
  const s = state.snap;
  if (!s) return;
  const ae = document.activeElement;
  const editing = !!(ae && ae.closest && ae.closest('#page-profiles'));
  if (!editing) {
    $('#setCore').value = s.settings.corePath;
    $('#setMixed').value = s.settings.mixedPort;
    $('#setCtrl').value = s.settings.controllerPort;
    $('#setWeb').value = s.settings.webPort;
    $('#subInterval').value = String(s.env.subIntervalHours || 0);
    $('#setTestUrl').value = s.env.testUrl || '';
    $('#setDelayTimeout').value = s.env.delayTimeout || 5000;
    $('#setAutoDelay').value = String(s.env.autoDelayMinutes || 0);
  }
  $('#testUrlSaved').textContent = '当前：' + (s.env.testUrl || '默认');
  setSwitch('#swAutoCore', s.env.autoStartCore);
  setSwitch('#swAutoProxy', s.env.autoStartProxy);
}

// ---------------------------------------------------------------- 交互
$('#btnToggle').addEventListener('click', async () => {
  const on = state.snap && state.snap.core.running;
  state.busy = true;
  renderTop(state.snap);
  const r = await api('POST', on ? '/api/core/stop' : '/api/core/start');
  state.busy = false;
  toast(r.msg || (r.ok ? '完成' : '失败'), r.ok ? 'ok' : 'bad');
  await tick();
  await loadSysProxy();
});

$('#btnRestart').addEventListener('click', async () => {
  state.busy = true; renderTop(state.snap);
  const r = await api('POST', '/api/core/restart');
  state.busy = false;
  toast(r.msg || '已重启', r.ok ? 'ok' : 'bad');
  tick();
});

$$('#modeSeg button').forEach((b) => b.addEventListener('click', async () => {
  const r = await api('POST', '/api/mode', { mode: b.dataset.mode });
  toast(r.ok ? '已切换到' + b.textContent + '模式' : (r.msg || '切换失败'), r.ok ? 'ok' : 'bad');
  tick();
}));

$('#profileSel').addEventListener('change', async (e) => {
  if (!e.target.value) return;
  const r = await api('POST', '/api/profiles/activate', { file: e.target.value });
  toast(r.msg || '已切换', r.ok ? 'ok' : 'bad');
  tick();
});

$('#swSysProxy').addEventListener('change', async (e) => {
  const want = e.target.checked;
  const r = await api('POST', '/api/systemproxy', { enable: want });
  toast(r.msg || (r.ok ? '已切换' : '失败'), r.ok ? 'ok' : 'bad');
  if (!r.ok) e.target.checked = !want;
  await loadSysProxy();
});

$('#swTun').addEventListener('change', async (e) => {
  const want = e.target.checked;
  if (want && state.env.isAdmin === false) {
    if (!confirm('TUN 需要管理员权限，当前不是。仍要保存吗？（重启内核后会失败）')) { e.target.checked = false; return; }
  }
  const r = await api('POST', '/api/settings', { tunEnable: want });
  toast(r.msg || '已保存', r.ok ? 'ok' : 'bad');
  if (state.snap && state.snap.core.running && confirm('需要重启内核才能生效，现在重启？')) {
    state.busy = true; renderTop(state.snap);
    const rr = await api('POST', '/api/core/restart');
    state.busy = false;
    toast(rr.msg || '已重启', rr.ok ? 'ok' : 'bad');
  }
  tick();
});

$('#swAutostart').addEventListener('change', async (e) => {
  const r = await api('POST', '/api/autostart', { enable: e.target.checked });
  toast(r.msg || '已设置', r.ok ? 'ok' : 'bad');
  if (!r.ok) e.target.checked = !e.target.checked;
  tick();
});

$('#nodeSearch').addEventListener('input', (e) => {
  state.nodeFilter = e.target.value;
  if (state.page === 'proxies') renderGroups($('#allGroups'), true, 0);
});
$('#nodeSort').addEventListener('change', (e) => {
  state.nodeSort = e.target.value;
  if (state.page === 'proxies') renderGroups($('#allGroups'), true, 0);
});

$('#setAutoDelay').addEventListener('change', async (e) => {
  const r = await api('POST', '/api/settings', { autoDelayMinutes: Number(e.target.value) });
  toast(r.msg || '已保存', r.ok ? 'ok' : 'bad');
  loadEnv(); tick();
});
$('#swAutoCore').addEventListener('change', async (e) => {
  const r = await api('POST', '/api/settings', { autoStartCore: e.target.checked });
  toast(r.msg || '已保存', r.ok ? 'ok' : 'bad');
  if (!r.ok) e.target.checked = !e.target.checked;
  loadEnv(); tick();
});
$('#swAutoProxy').addEventListener('change', async (e) => {
  const r = await api('POST', '/api/settings', { autoStartProxy: e.target.checked });
  toast(r.msg || '已保存', r.ok ? 'ok' : 'bad');
  if (!r.ok) e.target.checked = !e.target.checked;
  loadEnv(); tick();
});

$('#connSearch').addEventListener('input', loadConns);
$('#ruleSearch').addEventListener('input', loadRules);
$('#ruleSort').addEventListener('change', loadRules);
$('#btnRefreshRules').addEventListener('click', loadRules);

$('#btnCloseAll').addEventListener('click', closeAll);
async function closeAll() {
  if (!confirm('断开全部活动连接？')) return;
  const r = await api('DELETE', '/api/connections');
  toast(r.ok ? '已断开' : (r.msg || '失败'), r.ok ? 'ok' : 'bad');
}

$('#btnQuickDelay').addEventListener('click', async () => {
  const s = state.snap;
  if (!s || !s.groups.length) return toast('没有可测速的策略组', 'bad');
  const g = s.groups.find((x) => x.type !== 'Selector') || s.groups[0];
  toast('正在测试 ' + g.name + ' …');
  const r = await api('GET', '/api/proxies/delay?group=' + encodeURIComponent(g.name));
  if (!r.ok) return toast(r.msg || '测速失败', 'bad');
  toast(`${g.name}：${r.results.filter((x) => x.delay).length}/${r.results.length} 可用`, 'ok');
  tick();
});

$('#btnFetch').addEventListener('click', async () => {
  const url = $('#subUrl').value.trim();
  if (!url) return toast('先填订阅地址', 'bad');
  toast('拉取中…');
  const r = await api('POST', '/api/profiles/fetch', {
    name: $('#subName').value.trim() || 'sub',
    url,
    intervalHours: Number($('#subInterval').value || 24),
  });
  toast(r.msg || (r.ok ? '已拉取' : '失败'), r.ok ? 'ok' : 'bad');
  if (r.ok) { $('#subUrl').value = ''; $('#subName').value = ''; }
  tick();
});

$('#btnSaveYaml').addEventListener('click', async () => {
  const text = $('#yamlText').value;
  if (!text.trim()) return toast('YAML 为空', 'bad');
  const r = await api('POST', '/api/profiles', { name: $('#yamlName').value.trim() || 'pasted', content: text });
  toast(r.msg || '已保存', r.ok ? 'ok' : 'bad');
  if (r.ok) { $('#yamlText').value = ''; $('#yamlName').value = ''; }
  tick();
});

$('#btnSaveSettings').addEventListener('click', async () => {
  const r = await api('POST', '/api/settings', {
    corePath: $('#setCore').value.trim(),
    mixedPort: Number($('#setMixed').value),
    controllerPort: Number($('#setCtrl').value),
    webPort: Number($('#setWeb').value),
  });
  toast(r.msg || '已保存', r.ok ? 'ok' : 'bad');
});

async function saveTestUrl(url, timeout) {
  const body = {};
  if (url !== undefined) body.testUrl = url;
  if (timeout !== undefined) body.delayTimeout = timeout;
  const r = await api('POST', '/api/settings', body);
  toast(r.msg || (r.ok ? '已保存' : '保存失败'), r.ok ? 'ok' : 'bad');
  if (r.ok) { await loadEnv(); tick(); }
  return r.ok;
}

$('#btnSaveTest').addEventListener('click', () => {
  const url = $('#setTestUrl').value.trim();
  const t = Number($('#setDelayTimeout').value);
  if (!url) return toast('测速地址不能为空', 'bad');
  saveTestUrl(url, t);
});

$$('button[data-preset]').forEach((b) => b.addEventListener('click', async () => {
  $('#setTestUrl').value = b.dataset.preset;
  await saveTestUrl(b.dataset.preset, Number($('#setDelayTimeout').value) || undefined);
}));

$('#setTestUrl').addEventListener('keydown', (e) => {
  if (e.key === 'Enter') $('#btnSaveTest').click();
});

$('#btnPauseLog').addEventListener('click', () => {
  state.logPaused = !state.logPaused;
  $('#btnPauseLog').textContent = state.logPaused ? '继续滚动' : '暂停滚动';
  if (!state.logPaused) renderLogs();
});
$('#logSearch').addEventListener('input', (e) => { state.logFilter = e.target.value; renderLogs(); });
$('#logLevel').addEventListener('change', (e) => { state.logLevel = e.target.value; renderLogs(); });

$$('#connView button').forEach((b) => b.addEventListener('click', () => {
  state.connView = b.dataset.view;
  $$('#connView button').forEach((x) => x.classList.toggle('active', x === b));
  state.expandedId = null;
  loadConns();
}));

// ---------------- 规则预判 ----------------
async function runDiagnose() {
  const host = $('#diagHost').value.trim();
  const box = $('#diagResult');
  if (!host) return toast('先填个域名', 'bad');
  box.innerHTML = '<div class="skel">正在让内核真实连一次 ' + esc(host) + ' …</div>';
  const r = await api('POST', '/api/diagnose', { host });
  if (!r.ok) {
    box.innerHTML = `<div class="diag bad"><b>没拿到结果</b><span>${esc(r.msg || '未知原因')}</span></div>`;
    return;
  }
  box.innerHTML = `<div class="diag ok">
    <div class="dl"><span>域名</span><b>${esc(r.host)}</b></div>
    <div class="dl"><span>命中规则</span><b>${esc(r.ruleType || r.rule || '—')}${r.rulePayload ? `<em>${esc(r.rulePayload)}</em>` : ''}</b></div>
    <div class="dl"><span>最终节点</span><b>${esc(r.proxy || '—')}</b></div>
    <div class="dl"><span>链路</span><span>${esc((r.chains || []).join(' ← ') || '—')}</span></div>
    <div class="dl"><span>去向</span><span class="mono">${esc(r.dest || '')}</span></div>
    <div class="dl"><span>耗时</span><span>${r.ms}ms · 来源 ${r.source === 'connections' ? '连接记录' : '内核日志'}</span></div>
  </div>`;
}
$('#btnDiagnose').addEventListener('click', runDiagnose);
$('#diagHost').addEventListener('keydown', (e) => { if (e.key === 'Enter') runDiagnose(); });

// ---------------- 拖拽导入 YAML ----------------
let dragDepth = 0;
document.addEventListener('dragenter', (e) => {
  if (!e.dataTransfer || !Array.from(e.dataTransfer.types || []).includes('Files')) return;
  e.preventDefault();
  dragDepth++;
  document.body.classList.add('dragging');
});
document.addEventListener('dragover', (e) => {
  if (e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files')) e.preventDefault();
});
document.addEventListener('dragleave', () => {
  dragDepth = Math.max(0, dragDepth - 1);
  if (!dragDepth) document.body.classList.remove('dragging');
});
document.addEventListener('drop', async (e) => {
  if (!e.dataTransfer || !e.dataTransfer.files || !e.dataTransfer.files.length) return;
  e.preventDefault();
  dragDepth = 0;
  document.body.classList.remove('dragging');
  const files = Array.from(e.dataTransfer.files).filter((f) => /\.(ya?ml|txt)$/i.test(f.name));
  if (!files.length) return toast('只能拖 .yaml / .yml / .txt 配置文件', 'bad');
  for (const f of files) {
    const text = await f.text();
    if (!/proxies\s*:|proxy-providers\s*:/m.test(text)) {
      toast(f.name + ' 里没有 proxies，已跳过', 'bad');
      continue;
    }
    const r = await api('POST', '/api/profiles', { name: f.name.replace(/\.(ya?ml|txt)$/i, ''), content: text });
    toast(r.ok ? '已导入 ' + f.name : (r.msg || '导入失败'), r.ok ? 'ok' : 'bad');
  }
  tick();
});

$('#btnClearLog').addEventListener('click', () => {
  state.logLines = [];
  renderLogs();
});

// ---------------------------------------------------------------- 备份 / 合并 / 规则集
async function exportBackup() {
  const r = await api('GET', '/api/backup');
  if (!r.ok || !r.bundle) return toast('导出失败', 'bad');
  const blob = new Blob([JSON.stringify(r.bundle, null, 2)], { type: 'application/json' });
  const a = document.createElement('a');
  const d = new Date();
  const stamp = `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}-${String(d.getHours()).padStart(2, '0')}${String(d.getMinutes()).padStart(2, '0')}`;
  a.href = URL.createObjectURL(blob);
  a.download = `clash-deck-backup-${stamp}.json`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 4000);
  toast(`已导出（${r.bundle.profiles.length} 份配置）`, 'ok');
}

async function importBackup(file) {
  const text = await file.text();
  let bundle = null;
  try { bundle = JSON.parse(text); } catch { return toast('不是合法的备份文件', 'bad'); }
  if (bundle.bundle) bundle = bundle.bundle;
  if (!bundle.profiles) return toast('备份文件里没有配置', 'bad');
  const names = bundle.profiles.map((p) => p.file).join('、');
  const overwrite = confirm(`备份里有 ${bundle.profiles.length} 份配置：\n${names}\n\n点「确定」= 覆盖同名配置；点「取消」= 只补缺失的`);
  const r = await api('POST', '/api/restore', { bundle, overwrite });
  toast(r.msg || (r.ok ? '已导入' : '导入失败'), r.ok ? 'ok' : 'bad');
  tick();
}

function fillMergeOptions() {
  const s = state.snap;
  if (!s) return;
  const subs = s.profiles.filter((p) => p.sub);
  const base = $('#mergeBase');
  const curBase = base.value;
  base.innerHTML = s.profiles.map((p) => `<option value="${esc(p.file)}"${p.file === curBase ? ' selected' : ''}>${esc(profileLabel(p))}</option>`).join('');
  if (!s.profiles.some((p) => p.file === curBase) && s.settings.activeProfile) base.value = s.settings.activeProfile;
  const extras = $('#mergeExtras');
  extras.innerHTML = subs.length
    ? subs.map((p) => `<label class="chk"><input type="checkbox" value="${esc(p.file)}" /><span>${esc(profileLabel(p))}</span></label>`).join('')
    : '<span class="muted" style="font-size:11.5px">没有订阅导入的配置，无法合并</span>';
  $('#mergeTip').textContent = subs.length ? `${subs.length} 份订阅可用` : '';
}

async function doMerge() {
  const base = $('#mergeBase').value;
  const extras = [...document.querySelectorAll('#mergeExtras input:checked')].map((x) => x.value);
  if (!base) return toast('先选基础配置', 'bad');
  if (!extras.length) return toast('至少勾选一份订阅', 'bad');
  const box = $('#mergeResult');
  box.innerHTML = '<div class="skel">正在生成并用内核校验（约 1-3 秒）…</div>';
  const r = await api('POST', '/api/merge', { base, extras, name: $('#mergeName').value.trim() || undefined });
  if (r.ok) {
    box.innerHTML = `<div class="diag ok">
      <div class="dl"><span>生成</span><b>${esc(r.file)}</b></div>
      <div class="dl"><span>合并的 provider</span><b>${esc((r.providers || []).join('、'))}</b></div>
      <div class="dl"><span>内核校验</span><b>通过</b></div>
      ${r.note ? `<div class="dl"><span>提示</span><span>${esc(r.note)}</span></div>` : ''}
    </div>`;
    toast('已生成 ' + r.file, 'ok');
  } else {
    box.innerHTML = `<div class="diag bad"><b>没有生成</b><span>${esc(r.msg)}</span>${r.detail ? `<pre class="diag-detail">${esc(r.detail)}</pre>` : ''}</div>`;
    toast('合并失败：' + r.msg, 'bad');
  }
  tick();
}

async function loadRuleProviders() {
  const body = $('#rpBody');
  const r = await api('GET', '/api/providers/rules');
  if (!r.ok) { body.innerHTML = '<tr><td colspan="5" class="skel">内核未运行或接口不可用</td></tr>'; return; }
  $('#rpTip').textContent = `共 ${r.count} 个`;
  if (!r.count) { body.innerHTML = '<tr><td colspan="5" class="skel">当前配置没有 rule-providers</td></tr>'; return; }
  body.innerHTML = r.providers.map((p) => `<tr>
    <td>${esc(p.name)}</td>
    <td><span class="gbadge">${esc(p.behavior || p.format || '')}</span></td>
    <td class="num">${p.ruleCount}</td>
    <td class="mono">${esc((p.updatedAt || '').replace('T', ' ').slice(0, 19))}</td>
    <td><button class="btn sm" data-rp="${esc(p.name)}">更新</button></td>
  </tr>`).join('');
  $$('button[data-rp]', body).forEach((b) => b.addEventListener('click', async () => {
    b.disabled = true; b.textContent = '更新中';
    const rr = await api('POST', '/api/providers/rules/update', { name: b.dataset.rp });
    toast(rr.msg || '完成', rr.ok ? 'ok' : 'bad');
    b.disabled = false; b.textContent = '更新';
    loadRuleProviders();
  }));
}

$('#btnBackup').addEventListener('click', exportBackup);
$('#btnRestorePick').addEventListener('click', () => $('#fileRestore').click());
$('#fileRestore').addEventListener('change', async (e) => {
  const f = e.target.files && e.target.files[0];
  if (f) await importBackup(f);
  e.target.value = '';
});
$('#btnMerge').addEventListener('click', doMerge);
$('#btnRefreshProviders').addEventListener('click', loadRuleProviders);
$('#btnUpdateAllProviders').addEventListener('click', async () => {
  if (!confirm('更新全部规则集？会重新下载所有 rule-provider。')) return;
  const btn = $('#btnUpdateAllProviders');
  btn.disabled = true; btn.textContent = '更新中…';
  const r = await api('POST', '/api/providers/rules/update', { all: true });
  toast(r.msg || '完成', r.ok ? 'ok' : 'bad');
  btn.disabled = false; btn.textContent = '全部更新';
  loadRuleProviders();
});

// ---------------------------------------------------------------- 系统代理状态
async function loadSysProxy() {
  const r = await api('GET', '/api/systemproxy');
  state.sysProxy = r.ok ? r : { enabled: false, server: '', pointsToOurs: false };
  if (state.snap) renderTop(state.snap);
}
async function loadEnv() {
  const r = await api('GET', '/api/env');
  if (r.ok) state.env = r;
}

// ---------------------------------------------------------------- 启动
(async () => {
  await loadEnv();
  await loadSysProxy();
  await tick();
  await pullHistory();
})();
setInterval(tick, 2000);
setInterval(pullHistory, 3000);
setInterval(loadSysProxy, 12000);
setInterval(pullDelayStats, 10000);
setInterval(() => { if (state.page === 'overview') pullUsage(); }, 60000);
setInterval(loadEnv, 30000);

// 支持 ?page=proxies 直接打开某页、?diag=host 自动预判（截图/调试用）
const qs = new URLSearchParams(location.search);
const startPage = qs.get('page');
if (startPage) {
  const btn = document.querySelector(`.nav button[data-page="${startPage}"]`);
  if (btn) btn.click();
}
if (qs.get('diag')) {
  $('#diagHost').value = qs.get('diag');
  runDiagnose();
}
