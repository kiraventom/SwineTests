#!/bin/bash
# Все свины дерутся. Отвага: капы +5/-5, затухание -1 в сутки.
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

: > results/l8.log
for run in 1 2 3 4 5; do
  run_one "base-$run" --duel-chance 0 &
  run_one "d66-$run" --consent 0.66 &
  wait
done >> results/l8.log 2>&1
echo DONE >> results/l8.log
