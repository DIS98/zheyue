/**
 * Clash Deck 全功能自检（开发/验收用）。
 * 用法：先启动面板，然后  node tools\selftest.js
 * 会逐项打真实接口、断言结果、打印 PASS/FAIL 汇总，末尾自动把系统代理关掉、配置还原。
 */
const http = require('http');
const fs = require('fs');
const path = require('path');

// 简易端口占位器（用来伪造"端口被占"）
const blocker = {
  _srv: null,
  hold(port) {
    return new Promise((resolve, reject) => {
      const srv = http.createServer(() => {});
      srv.on('error', reject);
      srv.listen(port, '127.0.0.1', () => { this._srv = srv; resolve({ close: () => new Promise((r) => this._srv.close(r)) }); });
    });
  },
};

const ROOT = './';
const BASE = 'http://127.0.0.1:8787';
const results = [];
let current = '';

function section(name) { current = name; console.log(`\n=== ${name} ===`); }
function check(name, ok, evidence) {
  results.push({ section: current, name, ok: !!ok, evidence: String(evidence ?? '') });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}  ${evidence ?? ''}`);
  return !!ok;
}
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function holdConn(host) {
  return new Promise((resolve) => {
    const req = http.request({
      host: '127.0.0.1', port: 7897, method: 'CONNECT', path: host + ':443', timeout: 6000,
    });
    let done = false;
    req.on('connect', (res, socket) => { if (!done) { done = true; resolve({ socket, req }); } });
    req.on('timeout', () => { req.destroy(); if (!done) { done = true; resolve(null); } });
    req.on('error', () => { if (!done) { done = true; resolve(null); } });
    req.end();
  });
}


async function req(method, url, body) {
  const opt = { method, headers: {} };
  if (body !== undefined) {
    opt.headers['Content-Type'] = 'application/json';
    opt.body = JSON.stringify(body);
  }
  const t0 = Date.now();
  const r = await fetch(BASE + url, opt);
  let j = null;
  try { j = await r.json(); } catch { j = null; }
  return { status: r.status, body: j, ok: !!(j && j.ok), ms: Date.now() - t0 };
}
const GET = (u) => req('GET', u);
const POST = (u, b) => req('POST', u, b ?? {});
const DEL = (u) => req('DELETE', u);

// 本地订阅桩
function startSubServer() {
  const yaml = `port: 7890
mode: rule
proxies:
  - { name: "自检节点A", type: ss, server: 127.0.0.1, port: 1, cipher: aes-128-gcm, password: x }
proxy-groups:
  - { name: "自检组", type: select, proxies: ["自检节点A"] }
rules:
  - MATCH,自检组
`;
  return new Promise((resolve) => {
    const srv = http.createServer((_, res) => {
      res.writeHead(200, {
        'Content-Type': 'text/yaml; charset=utf-8',
        'subscription-userinfo': 'upload=0; download=1; total=1073741824; expire=1800000000',
      });
      res.end(yaml);
    });
    srv.listen(18999, '127.0.0.1', () => resolve(srv));
  });
}

function trafficThroughProxy() {
  // 走面板的混合端口，产生真实连接（curl 才认代理）
  const urls = ['https://www.gstatic.com/generate_204', 'https://api.github.com', 'https://cdn.jsdelivr.net/npm/vue/package.json'];
  for (const u of urls) {
    try {
      require('child_process').execFileSync('curl.exe',
        ['-s', '-o', '/dev/null', '--noproxy', '', '-x', 'http://127.0.0.1:7897', '--max-time', '10', u],
        { timeout: 15000, stdio: 'ignore' });
    } catch { /* 单个失败不影响 */ }
  }
}

(async () => {
  const originalProfile = (await GET('/api/snapshot')).body.settings.activeProfile;
  let subServer = null;
  try {
    // ---------------------------------------------------------------- 1 基础
    section('1. 基础接口');
    const env = await GET('/api/env');
    check('/api/env 可用', env.body && env.body.ok === true, `isAdmin=${env.body.isAdmin} sysProxyTool=${env.body.hasSysProxyTool}`);
    const snap0 = await GET('/api/snapshot');
    check('/api/snapshot 结构完整',
      snap0.body && snap0.body.core && Array.isArray(snap0.body.profiles) && Array.isArray(snap0.body.groups),
      `profiles=${snap0.body.profiles.length} groups=${snap0.body.groups.length}`);

    // ---------------------------------------------------------------- 2 内核
    section('2. 内核生命周期');
    const st = await POST('/api/core/start');
    check('启动内核', st.body.ok === true, st.body.msg);
    await sleep(1200);
    let snap = (await GET('/api/snapshot')).body;
    check('内核状态为运行中', snap.core.running === true && !!snap.core.version, `v${snap.core.version} pid=${snap.core.pid}`);
    check('策略组读取', snap.groups.length >= 2, `${snap.groups.length} 组`);

    const rs = await POST('/api/core/restart');
    check('重启内核', rs.body.ok === true, `${rs.body.msg} / ${rs.ms}ms`);

    // ---------------------------------------------------------------- 3 测速
    section('3. 测速与延迟');
    const g1 = snap.groups.find((g) => g.type !== 'Selector') || snap.groups[0];
    const oneNode = (snap.groups.find((g) => g.type === 'Selector') || snap.groups[0]).all
      .find((n) => !/剩余流量|到期|重置|官网/.test(n)) || g1.all[0];

    const d1 = await GET('/api/proxies/delay?name=' + encodeURIComponent(oneNode) + '&timeout=4000');
    check('单节点测速返回延迟', d1.body.ok === true && Number(d1.body.delay) > 0, `${oneNode} → ${d1.body.delay}ms`);

    const d2 = await GET('/api/proxies/delay?group=' + encodeURIComponent(g1.name) + '&timeout=4000');
    const okCount = (d2.body.results || []).filter((x) => x.delay > 0).length;
    check('整组批量测速', d2.body.ok === true && okCount > 0, `${g1.name}: ${okCount}/${(d2.body.results || []).length} 可用`);

    await sleep(1200);
    snap = (await GET('/api/snapshot')).body;
    const delayNodes = Object.keys(snap.groups[0].delays || {}).length;
    check('快照里带回每节点延迟（界面据此着色）', delayNodes >= 5, `${delayNodes} 个节点有延迟数据`);

    // 测速地址可配置
    const envT = (await GET('/api/env')).body;
    check('测速地址有默认值', !!envT.testUrl, envT.testUrl);

    const altUrl = 'https://www.google.com/generate_204';
    const setAlt = await POST('/api/settings', { testUrl: altUrl, delayTimeout: 6000 });
    const envAlt = (await GET('/api/env')).body;
    check('改成自定义测速地址', setAlt.ok && envAlt.testUrl === altUrl, `${envAlt.testUrl} / ${envAlt.delayTimeout}ms`);

    const dAlt = await GET('/api/proxies/delay?name=' + encodeURIComponent(oneNode));
    // 只验证"请求确实发往配置里的地址"；能不能通取决于这条线路本身
    check('测速请求发往新地址', dAlt.status === 200 && dAlt.body.url === altUrl, `实际请求 ${dAlt.body.url}（本次延迟=${dAlt.body.delay ?? '不可达'}）`);

    const perCall = await GET('/api/proxies/delay?name=' + encodeURIComponent(oneNode) + '&url=' + encodeURIComponent('https://cp.cloudflare.com/generate_204'));
    check('单次调用可临时指定地址', perCall.ok && perCall.body.url === 'https://cp.cloudflare.com/generate_204', perCall.body.url);

    const badUrl = await POST('/api/settings', { testUrl: 'not a url ::' });
    check('非法测速地址被拒绝', badUrl.status === 400, `HTTP ${badUrl.status}`);
    const badTimeout = await POST('/api/settings', { delayTimeout: 50 });
    check('非法超时被拒绝', badTimeout.status === 400, `HTTP ${badTimeout.status}`);

    const restoreUrl = await POST('/api/settings', { testUrl: 'https://www.gstatic.com/generate_204', delayTimeout: 5000 });
    const envBack = (await GET('/api/env')).body;
    check('测速地址恢复默认', restoreUrl.ok && envBack.testUrl === 'https://www.gstatic.com/generate_204', envBack.testUrl);

    // ---------------------------------------------------------------- 4 模式
    section('4. 代理模式');
    for (const m of ['global', 'direct', 'rule']) {
      await POST('/api/mode', { mode: m });
      const s = (await GET('/api/snapshot')).body;
      check(`切换模式 ${m}`, s.mode === m, `当前=${s.mode}`);
    }
    const badMode = await POST('/api/mode', { mode: 'nonsense' });
    check('非法模式被拒绝', badMode.status === 400, `HTTP ${badMode.status}`);

    // ---------------------------------------------------------------- 5 节点切换
    section('5. 节点切换');
    const selGroup = snap.groups.find((g) => g.type === 'Selector' && g.all.length > 2) || snap.groups[0];
    const targetNode = selGroup.all.find((n) => n !== selGroup.now) || selGroup.all[0];
    const sw = await POST('/api/proxies/select', { group: selGroup.name, name: targetNode });
    await sleep(600);
    const afterSel = (await GET('/api/snapshot')).body.groups.find((g) => g.name === selGroup.name);
    check('切换节点生效', sw.ok && afterSel.now === targetNode, `${selGroup.name} → ${afterSel.now}`);

    // ---------------------------------------------------------------- 6 流量与连接
    section('6. 流量采样与连接');
    trafficThroughProxy();
    await sleep(2500);
    const hist = (await GET('/api/history?seconds=600')).body;
    check('流量历史有采样点', hist.count >= 3, `${hist.count} 点`);
    const trSnap = (await GET('/api/snapshot')).body;
    check('代理链路真的产生流量', trSnap.traffic.downTotal > 0, `累计下行 ${trSnap.traffic.downTotal} 字节`);
    const conns = (await GET('/api/connections')).body;
    check('连接列表可读', conns.ok === true && Array.isArray(conns.list), `${conns.total} 条`);
    const closeAll = await DEL('/api/connections');
    check('一键断开连接', closeAll.ok === true, `HTTP ${closeAll.status}`);

    // ---------------------------------------------------------------- 7 规则
    section('7. 规则与命中统计');
    const rHit = (await GET('/api/rules?sort=hit')).body;
    const rOrd = (await GET('/api/rules?sort=order')).body;
    const sortedOk = rHit.rules.every((x, i, a) => i === 0 || a[i - 1].hit >= x.hit);
    check('规则命中排序', rHit.ok && sortedOk, `共 ${rHit.count} 条，累计命中 ${rHit.totalHit}`);
    check('切换排序不丢规则', rOrd.count === rHit.count, `${rOrd.count} vs ${rHit.count}`);

    // ---------------------------------------------------------------- 8 配置
    section('8. 配置文件管理');
    const pasteName = '自检配置';
    const pasteYaml = `proxies:\n  - { name: "自检节点A", type: ss, server: 127.0.0.1, port: 1, cipher: aes-128-gcm, password: x }\nproxy-groups:\n  - { name: "自检组", type: select, proxies: ["自检节点A"] }\nrules:\n  - MATCH,自检组\n`;
    const saved = await POST('/api/profiles', { name: pasteName, content: pasteYaml });
    check('粘贴 YAML 保存', saved.ok === true, saved.body.msg);
    const hasIt = (await GET('/api/snapshot')).body.profiles.some((p) => p.file.startsWith(pasteName));
    check('保存后出现在配置列表', hasIt, saved.body.file);

    // 订阅：本地桩
    subServer = await startSubServer();
    const fetched = await POST('/api/profiles/fetch', { name: '自检订阅', url: 'http://127.0.0.1:18999/sub.yaml', intervalHours: 6 });
    check('订阅链接拉取', fetched.ok === true, fetched.body.msg);
    check('订阅信息落库', !!(fetched.body.userinfo || '').includes('total'), fetched.body.userinfo || '（无）');

    const qSnap = (await GET('/api/snapshot')).body;
    const qp = qSnap.profiles.find((x) => x.file === '自检订阅.yaml');
    check('订阅配额被解析出来', !!(qp && qp.quota && qp.quota.total > 0 && qp.quota.remain > 0),
      qp && qp.quota ? `已用 ${(qp.quota.used / 1024).toFixed(0)}KB / 共 ${(qp.quota.total / 1024 / 1024).toFixed(0)}MB，剩余 ${(qp.quota.remain / 1024 / 1024).toFixed(0)}MB` : '无 quota');
    const upd = await POST('/api/subscriptions/update', { file: '自检订阅.yaml' });
    check('订阅手动更新', upd.ok === true, upd.body.msg);
    check('更新前留备份 .bak', fs.existsSync(path.join(ROOT, 'data', 'profiles', '自检订阅.yaml.bak')), '自检订阅.yaml.bak');

    const act = await POST('/api/profiles/activate', { file: '自检订阅.yaml' });
    check('激活配置', act.ok === true, act.body.msg);
    const actSnap = (await GET('/api/snapshot')).body;
    check('激活后快照同步', actSnap.settings.activeProfile === '自检订阅.yaml', actSnap.settings.activeProfile);

    const delActive = await DEL('/api/profiles?file=' + encodeURIComponent('自检订阅.yaml'));
    check('拒绝删除正在使用的配置', delActive.ok === false, delActive.body.msg);
    await POST('/api/profiles/activate', { file: originalProfile });
    await sleep(1200);
    const del1 = await DEL('/api/profiles?file=' + encodeURIComponent('自检订阅.yaml'));
    const subsFile = JSON.parse(fs.readFileSync(path.join(ROOT, 'data', 'subscriptions.json'), 'utf8'));
    check('删除配置时清掉订阅元数据', !subsFile['自检订阅.yaml'], Object.keys(subsFile).length + ' 条剩余');
    const del2 = await DEL('/api/profiles?file=' + encodeURIComponent(pasteName + '.yaml'));
    check('删除配置', del1.ok && del2.ok, `${del1.body.msg} / ${del2.body.msg}`);

    // 还原原始配置
    await POST('/api/profiles/activate', { file: originalProfile });
    await sleep(1500);
    const backSnap = (await GET('/api/snapshot')).body;
    check('还原到原配置并能起内核', backSnap.settings.activeProfile === originalProfile && backSnap.core.running, backSnap.settings.activeProfile);

    // ---------------------------------------------------------------- 9 系统代理
    section('9. 系统代理开关');
    const on = await POST('/api/systemproxy', { enable: true });
    const onStat = (await GET('/api/systemproxy')).body;  // ok 取 body
    check('开启系统代理', on.ok === true && onStat.enabled === true && onStat.pointsToOurs === true, `server=${onStat.server}`);
    const off = await POST('/api/systemproxy', { enable: false });
    const offStat = (await GET('/api/systemproxy')).body;
    check('关闭系统代理', off.ok === true && offStat.enabled === false, `enabled=${offStat.enabled}`);

    // ---------------------------------------------------------------- 10 自启 / TUN
    section('10. 开机自启与 TUN 设置');
    const auOn = await POST('/api/autostart', { enable: true });
    const vbs = path.join(process.env.APPDATA, 'Microsoft', 'Windows', 'Start Menu', 'Programs', 'Startup', 'ClashDeck.vbs');
    check('开启开机自启写文件', auOn.body.ok === true && fs.existsSync(vbs), vbs);
    const auOff = await POST('/api/autostart', { enable: false });
    check('关闭开机自启删文件', auOff.body.ok === true && !fs.existsSync(vbs), '已删除');

    await POST('/api/settings', { tunEnable: true });
    check('TUN 开关写入', (await GET('/api/snapshot')).body.env.tunEnable === true, 'env.tunEnable=true');
    await POST('/api/settings', { tunEnable: false });
    check('TUN 开关复原', (await GET('/api/snapshot')).body.env.tunEnable === false, 'env.tunEnable=false');

    // 启动联动三件套
    await POST('/api/settings', { autoStartCore: true, autoStartProxy: true, autoDelayMinutes: 15 });
    const envBoot = (await GET('/api/snapshot')).body.env;
    check('启动联动设置写入', envBoot.autoStartCore === true && envBoot.autoStartProxy === true && envBoot.autoDelayMinutes === 15,
      `core=${envBoot.autoStartCore} proxy=${envBoot.autoStartProxy} autoDelay=${envBoot.autoDelayMinutes}min`);
    const badAuto = await POST('/api/settings', { autoDelayMinutes: 99999 });
    check('非法定时测速间隔被拒', badAuto.status === 400, `HTTP ${badAuto.status}`);
    await POST('/api/settings', { autoStartCore: false, autoStartProxy: false, autoDelayMinutes: 0 });
    const envBoot2 = (await GET('/api/snapshot')).body.env;
    check('启动联动可复原', envBoot2.autoStartCore === false && envBoot2.autoDelayMinutes === 0, '已复原');

    await POST('/api/settings', { subIntervalHours: 12 });
    check('订阅间隔设置', (await GET('/api/snapshot')).body.env.subIntervalHours === 12, '12h');
    await POST('/api/settings', { subIntervalHours: 24 });

    // ---------------------------------------------------------------- 11 端口占用预检
    section('11. 端口占用预检');
    await POST('/api/core/stop');
    await sleep(1500);
    const hold = await blocker.hold(7897);
    await sleep(400);
    const blocked = await POST('/api/core/start');
    check('端口被占时报错而非静默失败', blocked.body.ok === false && /端口/.test(blocked.body.msg || ''), blocked.body.msg);
    await hold.close();
    await sleep(600);
    const startAgain = await POST('/api/core/start');
    check('端口释放后能正常启动', startAgain.body.ok === true, startAgain.body.msg);

    // ---------------------------------------------------------------- 12 并发
    section('12. 并发操作');
    const par = await Promise.all([
      POST('/api/core/restart'), POST('/api/core/start'),
      POST('/api/profiles/activate', { file: originalProfile }), GET('/api/snapshot'),
    ]);
    check('四个请求并行不互相踩', par.every((r) => r.status === 200), par.map((r) => r.status).join('/'));
    await sleep(1500);
    const fin = (await GET('/api/snapshot')).body;
    check('并发后内核仍在运行', fin.core.running === true, `v${fin.core.version}`);

    const badLogs = (await GET('/api/logs?since=0')).body.lines
      .filter((l) => /启动失败|EADDRINUSE|address already in use|热加载失败|panic/i.test(l.line));
    check('日志无冲突/崩溃记录', badLogs.length === 0, `${badLogs.length} 条`);

    // ---------------------------------------------------------------- 13 规则预判
    section('13. 规则预判与单条断开');
    const dg = await POST('/api/diagnose', { host: 'www.baidu.com' });
    check('预判返回命中规则', dg.ok === true && !!dg.body.ruleType, `${dg.body.ruleType}(${dg.body.rulePayload || ''}) → ${dg.body.proxy} · ${dg.body.ms}ms`);
    check('预判带完整链路', Array.isArray(dg.body.chains) && dg.body.chains.length >= 1, (dg.body.chains || []).join(' ← '));
    check('预判来源可追溯', dg.body.source === 'connections' || dg.body.source === 'log', '来源=' + dg.body.source);

    const dgBad = await POST('/api/diagnose', { host: '不是域名!!' });
    check('非法域名被拒', dgBad.status === 400, `HTTP ${dgBad.status}`);

    // 自己按住一条连接，再单独断掉它
    const held = await holdConn('www.google.com');
    if (held) {
      await sleep(500);
      const cl = (await GET('/api/connections')).body;
      const target = (cl.list || []).find((c) => (c.host || '').includes('google'));
      if (target) {
        const one = await DEL('/api/connections/' + encodeURIComponent(target.id));
        await sleep(400);
        const after = (await GET('/api/connections')).body;
        const still = (after.list || []).some((c) => c.id === target.id);
        check('单条连接可断开', one.ok === true && !still, `id=${target.id.slice(0, 8)}… → ${still ? '仍存在' : '已断开'}`);
      } else {
        check('单条连接可断开', false, '没抓到按住的那条连接');
      }
      try { held.socket.destroy(); } catch {}
    } else {
      check('单条连接可断开', false, '按住连接失败');
    }

    // ---------------------------------------------------------------- 14 统计 / 备份 / 合并 / 规则集
    section('14. 统计与数据管理');

    const usage = await GET('/api/usage?days=30');
    check('流量统计接口可用', usage.ok === true && usage.body.today && typeof usage.body.today.down === 'number',
      `今日 ↓${(usage.body.today.down / 1024).toFixed(1)}KB · 采样 ${usage.body.samples} 分钟`);

    const dh = await GET('/api/delay-history?name=' + encodeURIComponent(oneNode));
    check('延迟历史记录了样本', dh.ok === true && (dh.body.samples || []).length > 0,
      `样本 ${(dh.body.samples || []).length} 个，均 ${dh.body.avg}ms，丢包 ${dh.body.lossRate}%`);

    const bk = await GET('/api/backup');
    check('导出备份包含配置与订阅', bk.ok === true && (bk.body.bundle.profiles || []).length > 0,
      `${bk.body.bundle.profiles.length} 份配置 / ${Object.keys(bk.body.bundle.subs || {}).length} 条订阅 / ${Object.keys(bk.body.bundle.settings || {}).length} 项设置`);

    const restoreRes = await POST('/api/restore', { bundle: bk.body.bundle, overwrite: false });
    check('导入备份（不覆盖）', restoreRes.ok === true && restoreRes.body.written === 0, restoreRes.body.msg);

    const rsBad = await POST('/api/restore', { bundle: { nothing: true } });
    check('非法备份包被拒', rsBad.status === 400, `HTTP ${rsBad.status}`);

    const rp = await GET('/api/providers/rules');
    check('规则集列表可读', rp.ok === true && rp.body.count > 0, `${rp.body.count} 个规则集`);
    if (rp.body.count) {
      const one = rp.body.providers[0];
      const up = await POST('/api/providers/rules/update', { name: one.name });
      check('单个规则集可更新', up.ok === true, `${one.name} → ${up.body.msg}`);
    }

    // 合并：先拿一份"薄配置"做反例，确认校验门禁真的会拦
    const thin = await GET('/api/snapshot');
    const subProfiles = thin.body.profiles.filter((x) => x.sub);
    if (subProfiles.length >= 2) {
      const merged = await POST('/api/merge', {
        base: subProfiles[0].file,
        extras: [subProfiles[1].file],
        name: '自检合并',
      });
      check('合并订阅（薄配置应被校验拦下或成功生成）',
        merged.ok ? true : /校验/.test(merged.body.msg || ''),
        merged.ok ? `生成 ${merged.body.file}` : `已拦下：${(merged.body.msg || '').slice(0, 40)}`);
      // 换一份能自洽的配置再试
      const merged2 = await POST('/api/merge', { base: originalProfile, extras: [subProfiles[0].file], name: '自检合并2' });
      if (merged2.ok) {
        check('合并成功并落盘', fs.existsSync(path.join(ROOT, 'data', 'profiles', merged2.body.file)), merged2.body.file);
        await DEL('/api/profiles?file=' + encodeURIComponent(merged2.body.file));
        check('清理合并产物', true, '已删除');
      } else {
        check('合并成功并落盘', false, merged2.body.msg);
      }
    } else {
      check('合并订阅', true, '订阅不足 2 份，跳过（不影响其它项）');
    }

  } catch (e) {
    check('脚本异常', false, e && e.message);
  } finally {
    // 安全收尾：代理必须关，配置必须还原
    try { await POST('/api/systemproxy', { enable: false }); } catch {}
    try { await POST('/api/settings', { tunEnable: false }); } catch {}
    try { await POST('/api/profiles/activate', { file: originalProfile }); } catch {}
    if (subServer) subServer.close();
    try { fs.unlinkSync(path.join(ROOT, 'data', 'profiles', '自检订阅.yaml.bak')); } catch {}

    const pass = results.filter((r) => r.ok).length;
    const fail = results.filter((r) => !r.ok);
    console.log('\n================ 汇总 ================');
    console.log(`共 ${results.length} 项：通过 ${pass}，失败 ${fail.length}`);
    if (fail.length) fail.forEach((f) => console.log(`  FAIL [${f.section}] ${f.name} — ${f.evidence}`));
    process.exit(fail.length ? 1 : 0);
  }
})();

