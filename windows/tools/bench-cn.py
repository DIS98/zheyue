import subprocess, os, statistics, json

SITES = [
 ("百度","https://www.baidu.com"),("腾讯","https://www.qq.com"),("淘宝","https://www.taobao.com"),
 ("京东","https://www.jd.com"),("B站","https://www.bilibili.com"),("知乎","https://www.zhihu.com"),
 ("微博","https://weibo.com"),("网易","https://www.163.com"),("CSDN","https://www.csdn.net"),
 ("政府网","https://www.gov.cn"),("12306","https://www.12306.cn"),
 ("阿里镜像","https://mirrors.aliyun.com"),("腾讯镜像","https://mirrors.cloud.tencent.com"),
 ("阿里云","https://www.aliyun.com"),("华为云","https://www.huaweicloud.com"),
]
FMT = "%{http_code},%{time_namelookup},%{time_appconnect},%{time_starttransfer},%{time_total}"
env = {k:v for k,v in os.environ.items() if k.lower() not in ("http_proxy","https_proxy","all_proxy")}

def one(url, proxy):
    cmd = ["curl","-s","-o","/dev/null","--ssl-no-revoke","-4","--max-time","12","-w",FMT]
    cmd += ["--noproxy","*"] if proxy is None else ["-x",proxy]
    cmd.append(url)
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=15, env=env).stdout.strip()
        p = out.split(",")
        if len(p) != 5: return None
        code = p[0]
        if code in ("000",""): return None
        return dict(code=code, dns=float(p[1])*1000, tls=float(p[2])*1000, ttfb=float(p[3])*1000, total=float(p[4])*1000)
    except Exception:
        return None

def bench(proxy, rounds=3):
    res = {}
    for name,url in SITES:
        vals=[]
        for _ in range(rounds):
            r = one(url, proxy)
            if r: vals.append(r)
        res[name] = vals
    return res

print("测试中：直连组 …", flush=True)
d = bench(None)
print("测试中：代理组 …", flush=True)
p = bench("http://127.0.0.1:7897")

rows=[]
for name,url in SITES:
    dv = d.get(name,[]); pv = p.get(name,[])
    if not dv and not pv:
        rows.append((name,"—","—","—","—","—","全失败")); continue
    def med(vals,k): return statistics.median([v[k] for v in vals]) if vals else None
    dm, pm = med(dv,"total"), med(pv,"total")
    dtls, ptls = med(dv,"tls"), med(pv,"tls")
    dttfb, pttfb = med(dv,"ttfb"), med(pv,"ttfb")
    code = (pv[0]["code"] if pv else "—")
    diff = f"{(pm-dm)/dm*100:+.0f}%" if (dm and pm) else "—"
    rows.append((name, f"{dm:.0f}" if dm else "FAIL", f"{pm:.0f}" if pm else "FAIL",
                 f"{dtls:.0f}" if dtls else "—", f"{ptls:.0f}" if ptls else "—", diff, code))

print("\n| 站点 | 直连总耗时 ms | 经代理总耗时 ms | 直连TLS ms | 代理TLS ms | 差异 | HTTP |")
print("|---|---|---|---|---|---|---|")
for r in rows: print("| " + " | ".join(str(x) for x in r) + " |")

dt=[v["total"] for vals in d.values() for v in vals]
pt=[v["total"] for vals in p.values() for v in vals]
print(f"\n直连中位 {statistics.median(dt):.0f} ms ｜ 代理中位 {statistics.median(pt):.0f} ms")
json.dump({"direct":d,"proxy":p}, open("./","w"), ensure_ascii=False)
