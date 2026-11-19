"""Динамика по дневным рядам: смены лидера и скорость возврата после забоя.

Читает results/<prefix>-*.jsonl (по одному дню на строку, веса всех свинов) и отвечает
на вопрос, ради которого затевалась трата множителя:
  1) меняется ли лидер за год, или наверху вечный король;
  2) за сколько дней свин возвращается в топ-3 после забоя (вес 1-2 кг).

Запуск: python3 analyze-dynamics.py l22-d4
"""
import glob
import json
import os
import statistics as st
import sys

RESULTS = os.path.join(os.path.dirname(__file__), "results")


def load(prefix):
    runs = []
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.jsonl"))):
        days = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
        if days:
            runs.append((os.path.basename(path), days))
    return runs


def leader_changes(days):
    leaders = [max(d["weights"], key=lambda k: d["weights"][k]) for d in days]
    return sum(1 for a, b in zip(leaders, leaders[1:]) if a != b), sorted(set(leaders))


def comeback_days(days):
    """Для каждого свина: сколько дней от забоя (вес упал до <= 2) до возврата в топ-3."""
    names = list(days[0]["weights"])
    result = []
    for name in names:
        dropped_at = None
        for i, d in enumerate(days):
            w = d["weights"]
            weight = w.get(name, 0)
            top3 = sorted(w.values(), reverse=True)[:3]
            if weight <= 2:
                dropped_at = i
                continue
            if dropped_at is not None and weight >= top3[-1]:
                result.append(i - dropped_at)
                dropped_at = None
    return result


def main(prefix):
    runs = load(prefix)
    if not runs:
        print(f"нет рядов по {prefix}")
        return

    changes, comebacks, leader_sets = [], [], []
    for _, days in runs:
        ch, leaders = leader_changes(days)
        changes.append(ch)
        leader_sets.append(leaders)
        comebacks.extend(comeback_days(days))

    print(f"{prefix}: {len(runs)} прогонов")
    print(f"  смен лидера за год: медиана {st.median(changes):.0f}, среднее {st.mean(changes):.1f}, "
          f"мин {min(changes)}, макс {max(changes)}")
    everyone = sorted({l for s in leader_sets for l in s})
    print(f"  кто вообще был лидером: {', '.join(everyone)}")
    if comebacks:
        print(f"  возвратов в топ-3 после забоя: {len(comebacks)}, "
              f"медиана {st.median(comebacks):.0f} дней (мин {min(comebacks)}, макс {max(comebacks)})")
    else:
        print("  возвратов в топ-3 после забоя: 0")


if __name__ == "__main__":
    for p in (sys.argv[1:] or ["l22-d4"]):
        main(p)
