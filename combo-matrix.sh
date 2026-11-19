#!/bin/bash
# 12 комбинаций: источник шанса (3) x дебафф (2) x слава (2), по 3 прогона.
# Согласие 66%, забои включены (иначе дебафф "обесценивание" не проверить).
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="combo-$name" dotnet run -c Debug --no-build -- --replay --days 90 --consent 0.66 \
    --db "/tmp/combo-$name.db" "$@" > "results/combo-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/combo.log
for run in 1 2 3; do
  for chance in age witness achievements; do
    for debuff in feed-steal depreciate; do
      for glory in yes no; do
        local_glory=""
        [ "$glory" = "no" ] && local_glory="--no-glory"
        run_one "$chance-$debuff-$glory-$run" --chance "$chance" --debuff "$debuff" $local_glory &
        [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
      done
    done
  done
  wait
done >> results/combo.log 2>&1
echo DONE >> results/combo.log
