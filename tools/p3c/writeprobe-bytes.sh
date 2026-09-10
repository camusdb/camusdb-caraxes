#!/usr/bin/env bash
# Bytes-per-op summary for a write-probe arm (features 9bd74955 / 6f0e0fb7).
# Usage: tools/p3c/writeprobe-bytes.sh <writeprobe-dir> <run-dir>
#  - host device MB/s over the measured window (run-dir/host-io.csv) / AchievedOpsPerSec -> KB/op
#  - per-container /proc/1/io write_bytes over the same window (writeprobe-dir/io.csv)
#  - Raft-log shard CF from the last RocksDB LOG stats dump: WAL ingest, flush, per-level Write/Moved, W-Amp, stalls
set -u
wp=$1; run=$2
python3 - "$wp" "$run" <<'PY'
import sys,csv,re,os,glob,json
from datetime import datetime,timezone,timedelta
wp,run=sys.argv[1],sys.argv[2]
meta=json.load(open(os.path.join(run,'artifacts/run/run-meta.json')))
summ=json.load(open(os.path.join(run,'artifacts/run/summary.json')))
def ts(s): return datetime.fromisoformat(s.replace('Z','+00:00')[:26]+'+00:00') if len(s)>27 else datetime.fromisoformat(s.replace('Z','+00:00'))
t0=ts(meta['measureStartUtc']); t1=t0+timedelta(seconds=meta['measureSeconds'])
ops=summ['AchievedOpsPerSec']; secs=summ['MeasuredSeconds']
print(f"ops/s {ops:,.0f} over {secs}s (failed {summ.get('Failed')}), window {t0:%H:%M:%S}-{t1:%H:%M:%S}Z")
hio=[r for r in csv.DictReader(open(os.path.join(run,'host-io.csv'))) if t0<=ts(r['ts'])<=t1]
if hio:
    w=sum(float(r['w_mb_s']) for r in hio)/len(hio); fs=sorted(float(r['fsync_ms']) for r in hio)
    print(f"HOST device: {w:.1f} MB/s = {w*1e6/ops/1024:.2f} KB/op ({len(hio)} samples; fsync p50 {fs[len(fs)//2]:.2f} ms, first-min mean {sum(float(r['fsync_ms']) for r in hio[:6])/max(1,len(hio[:6])):.2f}, last-min {sum(float(r['fsync_ms']) for r in hio[-6:])/max(1,len(hio[-6:])):.2f})")
rows=[r for r in csv.DictReader(open(os.path.join(wp,'io.csv'))) if t0<=ts(r['ts'])<=t1]
tot=0
by={}
for r in rows: by.setdefault(r['container'],[]).append(r)
for c,rs in sorted(by.items()):
    d=int(rs[-1]['write_bytes'])-int(rs[0]['write_bytes']); span=(ts(rs[-1]['ts'])-ts(rs[0]['ts'])).total_seconds(); tot+=d
    print(f"  {c}: process write {d/1e9:.2f} GB / {span:.0f}s = {d/span/1e6:.1f} MB/s; end footprint kv {rs[-1]['kv_mib']} MiB wal {rs[-1]['wal_mib']} MiB")
print(f"  process total: {tot/1e6/secs:.1f} MB/s = {tot/(ops*secs)/1024:.2f} KB/op")
for f in sorted(glob.glob(os.path.join(wp,'rocksdb-LOG-wal-camus*.txt'))):
    t=open(f,errors='ignore').read()
    cw=re.findall(r'Cumulative writes: (\S+) writes, (\S+) keys,.*?ingest: (\S+ \S+)',t)
    stalls=t.count('Stalling writes'); moved=len(re.findall(r'Moved #\d+ to level',t)); comp=len(re.findall(r'compaction_finished',t)); flushes=len(re.findall(r'"event": "flush_finished"',t))
    wbm=len(re.findall(r'"flush_reason": "Write Buffer Manager"',t))
    blocks=re.split(r'\*\* Compaction Stats \[(\w+)\] \*\*',t)
    last={}
    for i in range(1,len(blocks)-1,2):
        if '\n  L0 ' in blocks[i+1] or '\n  L6 ' in blocks[i+1]: last[blocks[i]]=blocks[i+1]
    print(os.path.basename(f), f"WAL ingest {cw[-1][2] if cw else '?'} ({cw[-1][1] if cw else '?'} keys), flushes {flushes} (WBM-forced {wbm}), compactions {comp}, trivial moves {moved}, stalls {stalls}")
    for cf,b in last.items():
        if not cf.startswith('shard'): continue
        m=re.search(r'\n Sum\s+(\S+)\s+(\S+ \S+)\s+\S+\s+(\S+)\s+\S+\s+\S+\s+(\S+)\s+\S+\s+\S+\s+(\S+)\s+(\S+)',b)
        if not m or m.group(4)=='0.0': continue
        fl=re.search(r'Flush\(GB\): cumulative (\S+),',b)
        levels=re.findall(r'\n  (L\d)\s+(\S+)\s+(\S+ \S+)\s+\S+\s+(\S+)\s+\S+\s+\S+\s+(\S+)\s+\S+\s+\S+\s+(\S+)\s+(\S+)',b)
        lv=' '.join(f"{l}[files {fi} size {sz} write {w} moved {mv} wamp {wa}]" for l,fi,sz,rd,w,mv,wa in levels)
        print(f"   [{cf}] flush {fl.group(1) if fl else '?'} GB; Sum: files {m.group(1)} size {m.group(2)} read {m.group(3)} write {m.group(4)} GB moved {m.group(5)} W-Amp {m.group(6)}; {lv}")
PY
