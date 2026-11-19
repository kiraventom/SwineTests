#!/bin/bash
# Порог траты: 0.5 (половина лидера) режет разгон ровно там, где идёт борьба.
# Проверяем 0.75 и 0.9 — трата начинается только у самой вершины.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55 --growth sqrt --mod-decay 0.004"

matrix() {
  local prefix="$1"; local share="$2"; local runs="$3"
  : > "results/$prefix.log"
  for run in $(seq 1 "$runs"); do
    ( REPLAY_SERIES="$prefix-$run" dotnet run -c Debug --no-build -- $COMMON \
        --elite-share "$share" --db "/tmp/$prefix-$run.db" > "results/$prefix-$run.log" 2>&1
      echo "s$share-$run exit=$?" ) >> "results/$prefix.log" 2>&1 &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
  wait
  echo DONE >> "results/$prefix.log"
}

matrix l23-s75 0.75 8
matrix l23-s90 0.9 8
echo ALL_DONE
