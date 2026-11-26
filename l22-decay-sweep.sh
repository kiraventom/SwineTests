#!/bin/bash
# Свип доли траты множителя: чем больше доля, тем ниже садится вершина.
# 0.0001 — почти без траты (контроль), 0.004 — вершина ~1.4, 0.02 — жёстко.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55 --growth sqrt --elite-share 0.5"

matrix() {
  local prefix="$1"; local decay="$2"; local runs="$3"
  : > "results/$prefix.log"
  for run in $(seq 1 "$runs"); do
    ( REPLAY_SERIES="$prefix-$run" dotnet run -c Debug --no-build -- $COMMON \
        --mod-decay "$decay" --db "/tmp/$prefix-$run.db" > "results/$prefix-$run.log" 2>&1
      echo "d$decay-$run exit=$?" ) >> "results/$prefix.log" 2>&1 &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
  wait
  echo DONE >> "results/$prefix.log"
}

matrix l22-d4 0.004 8
matrix l22-d10 0.01 8
matrix l22-d20 0.02 8
echo ALL_DONE
