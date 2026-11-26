#!/bin/bash
# Догоняем до 15 прогонов d66 и 10 базы (5 уже посчитаны в l8).
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.1 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l8-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l8-$name.db" "$@" > "results/l8-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l8-more.log
for run in 6 7 8 9 10 11 12 13 14 15; do
  run_one "d66-$run" --consent 0.66 &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l8-more.log 2>&1
for run in 6 7 8 9 10; do
  run_one "base-$run" --duel-chance 0 &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l8-more.log 2>&1
wait
echo DONE >> results/l8-more.log
