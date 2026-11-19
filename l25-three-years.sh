#!/bin/bash
# Три года на точку: проверяем, что с тратой множитель НАСЫЩАЕТСЯ, а без неё — ползёт вверх.
# Порог 0.75 веса лидера, доля траты 0.004 за кормёжку против контроля без траты.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 1095 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55 --growth sqrt --elite-share 0.75"

matrix() {
  local prefix="$1"; local decay="$2"
  : > "results/$prefix.log"
  for run in $(seq 1 5); do
    ( REPLAY_SERIES="$prefix-$run" dotnet run -c Debug --no-build -- $COMMON \
        --mod-decay "$decay" --db "/tmp/$prefix-$run.db" > "results/$prefix-$run.log" 2>&1
      echo "decay$decay-$run exit=$?" ) >> "results/$prefix.log" 2>&1 &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
  wait
  echo DONE >> "results/$prefix.log"
}

matrix l25-3y 0.004
matrix l25-3y-none 0
echo ALL_DONE
