#!/bin/bash
# 365 дней, киллер, ставка 10%, забои останавливаются на 30-й день (как в базе) —
# иначе убийцы режут себя сами и сравнение нечестное.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"
mkdir -p results

run_one() {
  local name="$1"; shift
  REPLAY_SERIES="l4-$name" dotnet run -c Debug --no-build -- --replay --days 365 --killer --stake 0.1 \
    --stop-slaughter 30 --db "/tmp/l4-$name.db" "$@" > "results/l4-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/l4.log
for run in 1 2 3; do
  run_one "base-$run" --duel-chance 0 &
  run_one "witness-feed-yes-$run" --consent 0.66 --chance witness --debuff feed-steal &
  wait
  run_one "witness-depr-yes-$run" --consent 0.66 --chance witness --debuff depreciate &
  run_one "age-depr-yes-$run" --consent 0.66 --chance age --debuff depreciate &
  wait
  run_one "achiev-depr-no-$run" --consent 0.66 --chance achievements --debuff depreciate --no-glory &
  wait
done >> results/l4.log 2>&1
echo DONE >> results/l4.log
