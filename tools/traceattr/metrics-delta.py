import re,sys,os
def load(p):
    d={}
    for l in open(p):
        if l.startswith('#') or not l.strip(): continue
        m=re.match(r'^([^\s{]+)(\{[^}]*\})?\s+([-+0-9.eE]+|NaN|\+Inf)',l)
        if not m: continue
        try: d[(m.group(1),m.group(2) or '')]=float(m.group(3))
        except: pass
    return d
a,b=load(sys.argv[1]),load(sys.argv[2]); secs=float(sys.argv[3]); pat=re.compile(sys.argv[4])
for k in sorted(b):
    if not pat.search(k[0]+k[1]): continue
    if k[0].endswith('_bucket'): continue
    dv=b[k]-a.get(k,0)
    if dv==0: continue
    print(f"{k[0]}{k[1][:140]}  delta={dv:.4g}  rate={dv/secs:.4g}/s")
