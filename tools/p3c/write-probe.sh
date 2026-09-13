#!/usr/bin/env bash
# Write-attribution probe driver: runs bank-rebase-cand-p1-w128-writeprobe.yml once and samples each node's
# I/O totals, data footprint and RocksDB LOG files into runs/writeprobe-<tag>/ (feature cfe716da part 2, step 1).
set -u
cd ~/camusdb-caraxes || exit 1
tag=${TAG:-w1}; out=~/camusdb-caraxes/runs/writeprobe-$tag; mkdir -p "$out"
# SCENARIO/CLUSTER select a probe variant (the waltune-* arms); defaults keep the original probe.
scenario=${SCENARIO:-bank-rebase-cand-p1-w128-writeprobe}; cluster=${CLUSTER:-bankwriteprobe}
build_flag=""; [ "${BUILD:-0}" = "1" ] || build_flag="--skip-build"
log=$out/driver.log
sampler() {
  echo "ts,container,write_bytes,cancelled_write_bytes,kv_mib,wal_mib,logs_mib,wal_log_files_mib,wal_sst_mib" > "$out/io.csv"
  while :; do
    for n in 1 2 3; do
      c="$cluster-camus$n"
      io=$(docker exec "$c" cat /proc/1/io 2>/dev/null) || continue
      wb=$(echo "$io" | awk '/^write_bytes/{print $2}'); cwb=$(echo "$io" | awk '/^cancelled_write_bytes/{print $2}')
      d=$(docker exec "$c" sh -c 'du -sm /data/kv /data/wal 2>/dev/null | cut -f1 | tr "\n" ","; du -sm /data --exclude=/data/kv --exclude=/data/wal 2>/dev/null | cut -f1')
      # RocksDB write-ahead .log files vs .sst files under the Raft-log DB: the k175 arms showed /data/wal at 2.5-3.5 GB
      # against 0.2-0.9 GB of live SST, so the split is recorded to tell pinned WAL logs from retained tables.
      w=$(docker exec "$c" sh -c 'l=$(find /data/wal -name "*.log" -printf "%s\n" 2>/dev/null | awk "{s+=\$1} END{printf \"%d\", s/1048576}"); t=$(find /data/wal -name "*.sst" -printf "%s\n" 2>/dev/null | awk "{s+=\$1} END{printf \"%d\", s/1048576}"); echo "$l,$t"')
      echo "$(date -u +%Y-%m-%dT%H:%M:%SZ),$c,$wb,$cwb,$d,$w" >> "$out/io.csv"
      for db in kv wal; do docker exec "$c" sh -c "cat /data/$db/*/LOG 2>/dev/null" > "$out/rocksdb-LOG-$db-camus$n.txt.tmp" 2>/dev/null && mv "$out/rocksdb-LOG-$db-camus$n.txt.tmp" "$out/rocksdb-LOG-$db-camus$n.txt"; done
    done
    sleep 10
  done
}
echo "$(date -Is) === START writeprobe ===" >> "$log"
sampler & spid=$!
dotnet run --project Caraxes -c Release -- run --scenario "scenarios/$scenario.yml" --tag "$tag" $build_flag >> "$log" 2>&1
echo "$(date -Is) === END writeprobe exit=$? ===" >> "$log"
kill "$spid" 2>/dev/null; wait "$spid" 2>/dev/null
echo "$(date -Is) === WRITEPROBE COMPLETE ===" >> "$log"
