#!/bin/bash
# Усиление потери веса в дуэли: ставка 25% и 40% от веса проигравшего.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
BASE="--replay --days 365 --killer --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30 --consent 0.50"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l11-$name" dotnet run -c Debug --no-build -- $BASE \
    --db "/tmp/l11-$name.db" "$@" > "results/l11-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l11.log
for run in 1 2 3 4 5 6 7 8 9 10; do
  run_one "s25-$run" --stake 0.25 &
  run_one "s40-$run" --stake 0.40 &
  wait
done >> results/l11.log 2>&1
echo DONE >> results/l11.log
