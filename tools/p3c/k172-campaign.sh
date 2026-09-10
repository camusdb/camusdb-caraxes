#!/usr/bin/env bash
# Kahuna 1.7.2 / Kommander 1.5.7 campaign: build the image once via the write probe (w3), then the
# post-completion hold trio (0/2/4 ms) on tmpfs. Logs: runs/writeprobe-w3/driver.log, runs/pipeline-ab-driver-h1.log.
set -u
cd ~/camusdb-caraxes || exit 1
TAG=w3 BUILD=1 tools/p3c/write-probe.sh
TAG=h1 BUILD=0 ARMS="0 2 4" SCENARIO_PREFIX=hold-h CLUSTER_PREFIX=bankholdh tools/p3c/pipeline-ab.sh
echo "$(date -Is) === K172 CAMPAIGN COMPLETE ===" >> ~/camusdb-caraxes/runs/k172-campaign.log
