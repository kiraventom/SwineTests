#!/bin/bash
# Финальное подтверждение утверждённых чисел: k=0.03, phi=1.5, порог 0.75.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55"
: > results/l27.log
for run in $(seq 1 20); do
  ( REPLAY_SERIES="l27-final-$run" dotnet run -c Debug --no-build -- $COMMON \
      --db "/tmp/l27-$run.db" > "results/l27-final-$run.log" 2>&1
    echo "run-$run exit=$?" ) >> results/l27.log 2>&1 &
  [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
done
wait
echo DONE >> results/l27.log
