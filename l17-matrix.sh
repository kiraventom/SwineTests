#!/bin/bash
# Дуэли v3: шанс сжат к 50%, ставка 5% от лёгкого, слава как валюта,
# отказ = только -1 славы, лимит «вызов на беседу в сутки».
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l17-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l17-$name.db" "$@" > "results/l17-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l17.log
for run in $(seq 1 20); do
  run_one "v3-$run" &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l17.log 2>&1
wait
echo DONE >> results/l17.log
