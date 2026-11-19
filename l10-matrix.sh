#!/bin/bash
# Кап шанса: 10/90 и 20/80 при пороге согласия 50%.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.1 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30 --consent 0.50"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l10-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l10-$name.db" "$@" > "results/l10-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l10.log
for run in 1 2 3 4 5 6 7 8 9 10; do
  run_one "c1090-$run" --min-chance 10 --max-chance 90 &
  run_one "c2080-$run" --min-chance 20 --max-chance 80 &
  wait
done >> results/l10.log 2>&1
echo DONE >> results/l10.log
