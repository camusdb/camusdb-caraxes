import csv,sys,collections
t0=int(sys.argv[2]); t1=int(sys.argv[3])
snap=collections.defaultdict(dict)
for r in csv.DictReader(open(sys.argv[1])):
    snap[int(r['unix_s'])][(r['node'],r['tid'])]=(r['comm'],float(r['cpu_ns']),float(r['runq_ns']),float(r['slices']))
ts=[t for t in sorted(snap) if t0<=t<=t1]; dt=ts[-1]-ts[0]
g=collections.defaultdict(lambda:[set(),0.0,0.0,0.0])
for a,b in zip(ts,ts[1:]):
    for (n,tid),(comm,c,q,s) in snap[b].items():
        pc,pq,ps=(snap[a][(n,tid)][1:] if (n,tid) in snap[a] else (0,0,0))
        x=g[(n,comm)]; x[0].add(tid); x[1]+=(c-pc); x[2]+=(q-pq); x[3]+=(s-ps)
print(f"window {ts[0]}..{ts[-1]} ({dt}s), summed over {len(ts)-1} 10-s pairs")
for node in ['camus3','camus1','camus2']:
    items=[(k[1],v) for k,v in g.items() if k[0]==node]
    tc=sum(v[1] for _,v in items)/1e9/dt; tq=sum(v[2] for _,v in items)/1e9/dt; tsl=sum(v[3] for _,v in items)/dt
    print(f"{node}: cpu {tc:.2f} cores, runq {tq:.2f} thr, {tsl:.0f} slices/s, runq/slice {1e6*tq/tsl:.1f}us")
    for comm,(tids,c,q,s) in sorted(items,key=lambda kv:-kv[1][1]):
        c/=1e9*dt; q/=1e9*dt; s/=dt
        if c<0.02 and q<0.02: continue
        print(f"   {comm:<18}{len(tids):>5} tids  cpu {c:5.2f}  runq {q:5.2f}  slices/s {s:8.0f}  cpu/slice {1e6*c/max(s,1):7.1f}us  runq/slice {1e6*q/max(s,1):6.1f}us")
