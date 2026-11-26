#!/bin/bash
# 12 комбинаций на 365 дней: как каждая влияет на разрыв Kevish vs Вячесlove.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="long-$name" dotnet run -c Debug --no-build -- --replay --days 365 --consent 0.66 \
    --db "/tmp/long-$name.db" "$@" > "results/long-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/long.log
for chance in age witness achievements; do
  for debuff in feed-steal depreciate; do
    for glory in yes no; do
      g=""; [ "$glory" = "no" ] && g="--no-glory"
      run_one "$chance-$debuff-$glory" --chance "$chance" --debuff "$debuff" $g &
      [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
    done
  done
done
wait
echo DONE >> results/long.log
