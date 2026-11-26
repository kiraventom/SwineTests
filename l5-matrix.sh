#!/bin/bash
# 365 дней, дуэлятся только Kevish и Ганс, witness + обесценивание через отвагу.
# Сравниваем: без дуэлей / душить с 31-го дня / душить с 121-го дня.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.1 --stop-slaughter 30 --duelists Kevish,Ганс --chance witness --debuff depreciate"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l5-$name" dotnet run -c Debug --no-build -- $COMMON --db "/tmp/l5-$name.db" "$@" \
    > "results/l5-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l5.log
for run in 1 2 3; do
  run_one "base-$run" --duel-chance 0 &
  run_one "early-$run" --consent 0.66 --duels-from 30 &
  wait
  run_one "late-$run" --consent 0.66 --duels-from 120 &
  wait
done >> results/l5.log 2>&1
for run in 4 5; do
  run_one "early-$run" --consent 0.66 --duels-from 30 &
  run_one "late-$run" --consent 0.66 --duels-from 120 &
  wait
done >> results/l5.log 2>&1
echo DONE >> results/l5.log
