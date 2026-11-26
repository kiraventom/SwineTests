#!/bin/bash
# Подтверждающий прогон: дуэли v3 + инициатива + суб-линейный рост с k = 0.010.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55 --growth sqrt"

: > results/l21.log
for run in $(seq 1 20); do
  ( REPLAY_SERIES="l21-final-$run" dotnet run -c Debug --no-build -- $COMMON \
      --db "/tmp/l21-$run.db" > "results/l21-final-$run.log" 2>&1
    echo "final-$run exit=$?" ) >> results/l21.log 2>&1 &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done
wait
echo DONE >> results/l21.log
