#!/bin/bash
# Свип коэффициента суб-линейного роста: чем он выше, тем сильнее убийца.
# Дуэли v3 с инициативой атакующего, по 10 прогонов на точку.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55 --growth sqrt"

matrix() {
  local prefix="$1"; local k="$2"
  : > "results/$prefix.log"
  for run in $(seq 1 10); do
    ( REPLAY_SERIES="$prefix-$run" dotnet run -c Debug --no-build -- $COMMON \
        --growth-k "$k" --db "/tmp/$prefix-$run.db" > "results/$prefix-$run.log" 2>&1
      echo "k$k-$run exit=$?" ) >> "results/$prefix.log" 2>&1 &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
  wait
  echo DONE >> "results/$prefix.log"
}

matrix l20-k8 0.008
matrix l20-k10 0.010
matrix l20-k12 0.012
echo ALL_DONE
