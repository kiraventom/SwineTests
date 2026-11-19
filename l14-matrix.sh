#!/bin/bash
# Новая политика: отказ от мелюзги (ratio < 0.5), согласие при шансе >= 70%.
# Отвага ±3, страх = 0.75^n без пола, каждый провал поесть сжигает 1 долга.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.25 --stop-slaughter 30 --chance witness --debuff feed-steal --duels-from 30 --consent 0.70 --min-weight-ratio 0.5"

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l14-$name" dotnet run -c Debug --no-build -- $COMMON \
    --db "/tmp/l14-$name.db" "$@" > "results/l14-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l14.log
for run in $(seq 1 20); do
  run_one "smart-$run" &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done >> results/l14.log 2>&1
wait
echo DONE >> results/l14.log
