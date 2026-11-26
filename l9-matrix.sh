#!/bin/bash
# Контекстные решения: свин соглашается, если шанс победы >= его порога
# (или если он в долге по отваге). Три характера: 30% азартный, 50% расчётливый, 70% осторожный.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.1 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l9-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l9-$name.db" "$@" > "results/l9-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l9.log
for run in 1 2 3 4 5 6 7 8 9 10; do
  run_one "p30-$run" --consent 0.30 &
  run_one "p70-$run" --consent 0.70 &
  wait
  run_one "p50-$run" --consent 0.50 &
  run_one "base-$run" --duel-chance 0 &
  wait
done >> results/l9.log 2>&1
echo DONE >> results/l9.log
