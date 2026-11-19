#!/bin/bash
# Уточнение по 20 прогонов на точку: порог 0.75 (баланс) против 0.9 (динамика).
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55 --growth sqrt --mod-decay 0.004"

matrix() {
  local prefix="$1"; local share="$2"
  : > "results/$prefix.log"
  for run in $(seq 1 20); do
    ( REPLAY_SERIES="$prefix-$run" dotnet run -c Debug --no-build -- $COMMON \
        --elite-share "$share" --db "/tmp/$prefix-$run.db" > "results/$prefix-$run.log" 2>&1
      echo "s$share-$run exit=$?" ) >> "results/$prefix.log" 2>&1 &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
  wait
  echo DONE >> "results/$prefix.log"
}

matrix l24-s75 0.75
matrix l24-s90 0.9
echo ALL_DONE
