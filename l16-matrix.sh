#!/bin/bash
# l15 + witness-бонус 2.0 -> 0.5 п.п. (кап 30 оставлен). Политика: ratio 0.4, порог 70%.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.25 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30 --consent 0.70 --min-weight-ratio 0.4"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l16-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l16-$name.db" "$@" > "results/l16-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l16.log
for run in $(seq 1 20); do
  run_one "w05-$run" &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l16.log 2>&1
wait
echo DONE >> results/l16.log
