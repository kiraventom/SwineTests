#!/bin/bash
# Две матрицы подряд, чтобы мерить раздельно:
#   l18 — v3 + инициатива атакующего, рост от забоёв ЛИНЕЙНЫЙ (как было);
#   l19 — то же + суб-линейный рост (корень), решение из DUELS-V3-DESIGN.md §7.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55"

matrix() {
  local prefix="$1"; local growth="$2"
  : > "results/$prefix.log"
  for run in $(seq 1 20); do
    ( REPLAY_SERIES="$prefix-$growth-$run" dotnet run -c Debug --no-build -- $COMMON \
        --growth "$growth" --db "/tmp/$prefix-$run.db" > "results/$prefix-$growth-$run.log" 2>&1
      echo "$growth-$run exit=$?" ) >> "results/$prefix.log" 2>&1 &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
  wait
  echo DONE >> "results/$prefix.log"
}

matrix l18 linear
matrix l19 sqrt
echo ALL_DONE
