#!/bin/bash
# 365 дней, синтетический убийца «Кровавый Ганс», ставка 10%.
# 2 прогона без дуэлей (Ганс забивает месяц и фиксирует множитель) + 4 конфигурации x 5.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l2-$name" dotnet run -c Debug --no-build -- --replay --days 365 --killer --stake 0.1 \
    --db "/tmp/l2-$name.db" "$@" > "results/l2-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l2.log
for run in 1 2; do
  run_one "base-$run" --duel-chance 0 --stop-slaughter 30 &
  run_one "base2-$run" --duel-chance 0 &
  wait
done >> results/l2.log 2>&1

declare -a configs=(
  "witness-feed-yes|--chance witness --debuff feed-steal"
  "witness-depr-yes|--chance witness --debuff depreciate"
  "age-depr-yes|--chance age --debuff depreciate"
  "achiev-depr-no|--chance achievements --debuff depreciate --no-glory"
)
for run in 1 2 3 4 5; do
  for cfg in "${configs[@]}"; do
    name="${cfg%%|*}"; flags="${cfg#*|}"
    run_one "$name-$run" --consent 0.66 $flags &
    [ "$(jobs -rp | wc -l)" -ge 2 ] && wait -n
  done
done >> results/l2.log 2>&1
wait
echo DONE >> results/l2.log
