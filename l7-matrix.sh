#!/bin/bash
# Больше дуэлянтов (Kevish, Ганс + два малыша) и кап страха 0.75^n с полом 0.25.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.1 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30"
DUELISTS="Kevish,Ганс,Anton,Vyacheslove"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l7-$name" dotnet run -c Debug --no-build -- $COMMON --duelists "$DUELISTS" \
    --db "/tmp/l7-$name.db" "$@" > "results/l7-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l7.log
for run in 1 2 3 4 5; do
  run_one "base-$run" --duel-chance 0 &
  run_one "d66-$run" --consent 0.66 &
  wait
done >> results/l7.log 2>&1
echo DONE >> results/l7.log
