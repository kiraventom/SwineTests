"""Проверка калибровки «забил X — вырос до X».

Читает лог реплея, вытаскивает строки
    Feed multiplier M for swine [ID] at W kg, fuel now F
и для каждого свина ищет эпизоды выгорания: запас падал до нуля.
В отчёте: максимум запаса перед выгоранием (сколько накопил, X),
вес в момент, когда запас кончился, и отношение вес/X.

Запуск: python3 analyze-calibration.py l26-1
"""
import glob
import os
import re
import statistics as st
import sys

RESULTS = os.path.join(os.path.dirname(__file__), "results")
LINE = re.compile(r"Feed multiplier ([\d.]+) for swine \[(\d+)\] at (\d+) kg, fuel now ([\d.]+)")


def load(prefix):
    per_swine = {}
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}*.log"))):
        for line in open(path, encoding="utf-8", errors="replace"):
            m = LINE.search(line)
            if not m:
                continue
            swine = m.group(2)
            per_swine.setdefault(swine, []).append((float(m.group(4)), int(m.group(3))))
    return per_swine


def episodes(series):
    """Эпизоды: (накопленный максимум, вес в момент обнуления запаса)."""
    out = []
    peak = 0.0
    burning = False
    for fuel, weight in series:
        if fuel > 0:
            peak = max(peak, fuel)
            burning = True
        elif burning:
            out.append((peak, weight))
            peak = 0.0
            burning = False
    return out


def main(prefix):
    data = load(prefix)
    if not data:
        print(f"нет данных по {prefix}")
        return

    print(f"{prefix}: свинов в логе {len(data)}\n")
    print(f"{'свин':>6} {'накопил X':>10} {'вес на выгорании':>17} {'вес/X':>7}")
    ratios = []
    for swine, series in data.items():
        for peak, weight in episodes(series):
            if peak < 100:
                continue
            ratios.append(weight / peak)
            print(f"{swine:>6} {peak:>10.0f} {weight:>17d} {weight / peak:>7.2f}")

    if ratios:
        print(f"\nэпизодов: {len(ratios)}; медиана вес/X = {st.median(ratios):.2f}; "
              f"среднее {st.mean(ratios):.2f}; диапазон {min(ratios):.2f}–{max(ratios):.2f}")
    else:
        print("эпизодов выгорания не найдено")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "l26-1")
