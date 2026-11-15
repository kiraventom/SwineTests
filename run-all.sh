#!/bin/bash
# Прогон всех конфигураций тестов. Только для локального стенда: база пересоздаётся.
set -u
# SDK может жить в домашнем каталоге (в образе контейнера его нет)
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH"
cd "$(dirname "$0")"

run() {
  local name="$1"; shift
  echo "=== $name"
  dotnet run -c Debug --no-build -- "$@" > "results/$name.log" 2>&1
  echo "=== $name exit=$?"
}

run duels-1     --days 200 --db /tmp/swinetests-duels1.db
run duels-2     --days 200 --db /tmp/swinetests-duels2.db
run control     --days 200 --db /tmp/swinetests-control.db --duel-chance 0
run feeding     --days 200 --no-slaughter --duel-chance 0 --db /tmp/swinetests-feeding.db
