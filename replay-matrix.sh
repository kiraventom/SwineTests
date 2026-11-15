#!/bin/bash
# Матрица прогонов реплея прод-беседы: 3 без дуэлей + 4 режима согласия x 3 прогона.
# Параллельность 3: на стенде 2 CPU, больше — только мешают друг другу.
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd "$(dirname "$0")"
mkdir -p results

run_one() {
  local name="$1"; shift
  dotnet run -c Debug --no-build -- --replay --days 90 --db "/tmp/replay-$name.db" "$@" > "results/replay-$name.log" 2>&1
  echo "$name exit=$?"
}

: > results/matrix.log
for run in 1 2 3; do
  run_one "none-$run" --duel-chance 0 &
  run_one "c100-$run" --consent 1.0 &
  wait
  run_one "c66-$run" --consent 0.66 &
  run_one "c33-$run" --consent 0.33 &
  wait
  run_one "c0-$run" --consent 0.0 &
  wait
done >> results/matrix.log 2>&1
echo DONE >> results/matrix.log
