import subprocess, os, statistics, threading
env={k:v for k,v in os.environ.items() if k.lower() not in ("http_proxy","https_proxy","all_proxy")}
SITES=[("Google","https://www.google.com"),("YouTube","https://www.youtube.com"),
 ("GitHub","https://github.com"),("X","https://x.com"),("Wikipedia","https://en.wikipedia.org"),
 ("Cloudflare","https://www.cloudflare.com"),("Microsoft","https://www.microsoft.com"),
 ("Apple","https://www.apple.com"),("OpenAI","https://openai.com"),("Reddit","https://www.reddit.com"),
 ("Netflix","https://www.netflix.com"),("Steam","https://store.steampowered.com"),
 ("npm","https://registry.npmjs.org"),("PyPI","https://pypi.org"),("Telegram","https://telegram.org")]
FMT="%{http_code},%{time_namelookup},%{time_connect},%{time_appconnect},%{time_starttransfer},%{time_total}"
lock=threading.Lock()

def probe(url,proxy,timeout):
    cmd=["curl","-s","-o","NUL","--ssl-no-revoke","-4","--max-time",str(timeout),"-w",FMT]
    cmd += ["--noproxy","*"] if proxy is None else ["-x",proxy]
    cmd.append(url)
    try:
        o=subprocess.run(cmd,capture_output=True,text=True,timeout=timeout+4,env=env).stdout.strip().split(",")
        if len(o)!=6: return None
        if o[0] in ("000",""): return None
        return dict(code=o[0],dns=float(o[1])*1000,conn=float(o[2])*1000,tls=float(o[3])*1000,
                    ttfb=float(o[4])*1000,total=float(o[5])*1000)
    except Exception: return None

def run(proxy,timeout,rounds,label):
    out={}
    def work(item):
        name,url=item; vals=[]
        for _ in range(rounds):
            r=probe(url,proxy,timeout)
            if r: vals.append(r)
        with lock: out[name]=vals
    ths=[threading.Thread(target=work,args=(s,)) for s in SITES]
    for t in ths: t.start()
    for t in ths: t.join()
    return out

print("国外 · 直连组（4s 超时快速失败）…",flush=True)
D=run(None,4,2,"d")
print("国外 · 经代理组 …",flush=True)
P=run("http://127.0.0.1:7897",12,2,"p")

print("\n| 站点 | 直连 | 经代理 | 代理 TTFB | 代理 TLS |")
print("|---|---|---|---|---|")
ok_d=ok_p=0
for name,_ in SITES:
    dv=D.get(name,[]); pv=P.get(name,[])
    if dv: ok_d+=1
    if pv: ok_p+=1
    ds=f"{statistics.median([v['total'] for v in dv]):.0f} ms" if dv else "超时"
    ps=f"{statistics.median([v['total'] for v in pv]):.0f} ms" if pv else "失败"
    pt=f"{statistics.median([v['ttfb'] for v in pv]):.0f}" if pv else "—"
    ptl=f"{statistics.median([v['tls'] for v in pv]):.0f}" if pv else "—"
    print(f"| {name} | {ds} | {ps} | {pt} | {ptl} |")
print(f"\n直连可达 {ok_d}/15 ｜ 代理可达 {ok_p}/15")
if ok_p:
    allp=[v['total'] for vals in P.values() for v in vals]
    print(f"经代理总耗时中位 {statistics.median(allp):.0f} ms")
