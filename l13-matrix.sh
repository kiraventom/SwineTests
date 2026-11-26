#!/bin/bash
# Ставка считается от веса ЗАЩИЩАЮЩЕГОСЯ (25%), 20 прогонов.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.25 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30 --consent 0.50"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l13-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l13-$name.db" "$@" > "results/l13-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l13.log
for run in $(seq 1 20); do
  run_one "def25-$run" &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l13.log 2>&1
wait
echo DONE >> results/l13.log
