#!/bin/bash
# Возраст свинов из прода + witness-бонус. Политика: ratio 0.4, порог 70%.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.25 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30 --consent 0.70 --min-weight-ratio 0.4"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l15-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l15-$name.db" "$@" > "results/l15-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l15.log
for run in $(seq 1 20); do
  run_one "age-$run" &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l15.log 2>&1
wait
echo DONE >> results/l15.log
