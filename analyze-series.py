"""Массы по дневным рядам: средняя, медиана, пик и дни в топ-3.

Финальный вес — плохая метрика для свина, который регулярно забивается: он
показывает, где цикл случайно оказался в последний день. Поэтому смотрим ряд.

Запуск: python3 analyze-series.py l28-base l28-s70
"""
import glob
import json
import os
import statistics as st
import sys

RESULTS = os.path.join(os.path.dirname(__file__), "results")


def load(prefix):
    masses = {}
    top1 = {}
    top3 = {}
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.jsonl"))):
        for line in open(path, encoding="utf-8"):
            if not line.strip():
                continue
            day = json.loads(line)
            weights = day["weights"]
            if not weights:
                continue
            ranked = sorted(weights.items(), key=lambda kv: -kv[1])
            for name, weight in weights.items():
                masses.setdefault(name, []).append(weight)
                top1.setdefault(name, []).append(1 if ranked[0][0] == name else 0)
                top3.setdefault(name, []).append(1 if weight >= ranked[min(2, len(ranked) - 1)][1] else 0)
    return masses, top1, top3


def quantile(values, p):
    ordered = sorted(values)
    return ordered[int(p * (len(ordered) - 1))]


def main(prefix):
    masses, top1, top3 = load(prefix)
    if not masses:
        print(f"нет рядов по {prefix}")
        return

    print(f"\n=== {prefix} ===")
    print(f"{'свин':12} {'среднее':>9} {'медиана':>9} {'пик':>8} {'90%':>8} {'дней топ-1':>11} {'дней топ-3':>11}")
    for name, values in sorted(masses.items(), key=lambda kv: -st.mean(kv[1])):
        print(f"{name:12} {st.mean(values):9.0f} {st.median(values):9.0f} {max(values):8.0f} "
              f"{quantile(values, 0.9):8.0f} {100 * st.mean(top1[name]):10.0f}% {100 * st.mean(top3[name]):10.0f}%")


if __name__ == "__main__":
    for prefix in (sys.argv[1:] or ["l28-base"]):
        main(prefix)
