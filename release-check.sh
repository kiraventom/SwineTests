#!/bin/bash
# Релизная проверка: сценарии + сезоны на дефолтах бота + сводка медиан.
# Запуск: ./release-check.sh [число сезонов, по умолчанию 20]
set -u
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
export DOTNET_ROOT="$HOME/.dotnet"
cd "$(dirname "$0")"

RUNS="${1:-20}"
PREFIX="release"
# Флаги только про модель прогона: ни один не переопределяет настройки бота,
# чтобы мерить ровно то, что поедет в прод (см. шапку лога «Баланс»)
COMMON="--replay --days 365 --killer --stake 0.05 --stop-slaughter 30 --duels-from 30 --consent 0.55"

mkdir -p results
echo "=== сборка"
dotnet build -c Debug 2>&1 | tail -3 || exit 1

echo "=== сценарии и короткая симуляция"
dotnet run -c Debug --no-build -- --days 3 --db "/tmp/$PREFIX-scenarios.db" > "results/$PREFIX-scenarios.log" 2>&1
SCEN=$?
grep -E "ALL CHECKS PASSED|FAILURES" "results/$PREFIX-scenarios.log"

echo "=== сезоны: $RUNS по 365 дней"
: > "results/$PREFIX.runner.log"
for run in $(seq 1 "$RUNS"); do
  (
    start=$SECONDS
    dotnet run -c Debug --no-build -- $COMMON --db "/tmp/$PREFIX-$run.db" > "results/$PREFIX-$run.log" 2>&1
    echo "$PREFIX-$run exit=$? elapsed=$((SECONDS-start))s" >> "results/$PREFIX.runner.log"
  ) &
  while [ "$(jobs -rp | wc -l)" -ge 2 ]; do sleep 1; done
done
wait

echo "=== итог"
python3 analyze-season.py "$PREFIX"
FAILED=$(grep -vc "exit=0" "results/$PREFIX.runner.log" || true)
echo
[ "$SCEN" -eq 0 ] && [ "$FAILED" -eq 0 ] && echo "RELEASE CHECK OK" || echo "RELEASE CHECK FAILED (сценарии exit=$SCEN, прогонов с ошибкой: $FAILED)"
