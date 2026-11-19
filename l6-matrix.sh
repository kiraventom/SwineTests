#!/bin/bash
# Страх вместо обесценивания: порция труса уходит трём активным свинам.
# Дуэлянты — Kevish и Ганс. Сравниваем базу, умеренный отказ (66%) и жёсткий (10%).
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.1 --stop-slaughter 30 --duelists Kevish,Ганс --chance witness --debuff feed-steal --duels-from 30"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l6-$name" dotnet run -c Debug --no-build -- $COMMON --db "/tmp/l6-$name.db" "$@" \
    > "results/l6-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l6.log
for run in 1 2 3; do
  run_one "base-$run" --duel-chance 0 &
  run_one "consent66-$run" --consent 0.66 &
  wait
  run_one "consent10-$run" --consent 0.10 &
  wait
done >> results/l6.log 2>&1
echo DONE >> results/l6.log
