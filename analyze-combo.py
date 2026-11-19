"""Анализ 12 комбинаций (источник шанса × дебафф × слава).

Главная метрика: разрыв между «бережливым» (Kevish, не забивает) и «убийцей»
(Вячесlove, 10 забоёв и множитель кормления 3.4). Плюс суммарный вес группы.
"""
import glob
import json
import os
import re
import statistics as st
from collections import defaultdict

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

RESULTS = os.path.join(os.path.dirname(__file__), "results")
OUT = "/home/user/out"

SAVER = "Kevish"       # бережливый: 1 забой, множитель 1.14
KILLER = "Вячесlove"   # убийца: 10 забоев, множитель 3.39


def load_results():
    rows = []
    for path in sorted(glob.glob(os.path.join(RESULTS, "combo-*.log"))):
        name = os.path.basename(path)[len("combo-"):-len(".log")]
        if name.startswith("runner"):
            continue
        text = open(path, encoding="utf-8", errors="replace").read()
        m = re.search(r"RESULT consent=([\d.]+) duels=(\d+) accepted=(\d+) declined=(\d+) "
                      r"bigDelta=(-?\d+) smallDelta=(-?\d+) totalDelta=(-?\d+) weights=(.+)", text)
        if not m:
            continue
        weights = dict((k, int(v)) for k, v in (p.split(":") for p in m.group(8).split(",")))

        parts = name.rsplit("-", 1)
        config = parts[0]
        rows.append(dict(
            name=name, config=config, run=parts[1],
            duels=int(m.group(2)), accepted=int(m.group(3)), declined=int(m.group(4)),
            big=int(m.group(5)), small=int(m.group(6)), total=int(m.group(7)),
            weights=weights, gap=weights.get(SAVER, 0) - weights.get(KILLER, 0),
        ))
    return rows


def load_series():
    data = defaultdict(list)
    for path in sorted(glob.glob(os.path.join(RESULTS, "combo-*.jsonl"))):
        name = os.path.basename(path)[len("combo-"):-len(".jsonl")]
        config = name.rsplit("-", 1)[0]
        data[config].append([json.loads(line) for line in open(path, encoding="utf-8") if line.strip()])
    return data


def print_table(rows):
    configs = sorted(set(r["config"] for r in rows))
    print(f"{'конфигурация':34} {'вызовов':>8} {'прин':>5} {'отк':>5} {'Kevish':>7} {'Вячесlove':>10} {'разрыв':>8} {'всего':>7}")
    for config in configs:
        grp = [r for r in rows if r["config"] == config]
        mean = lambda k: st.mean(x[k] for x in grp)
        saver = st.mean(x["weights"][SAVER] for x in grp)
        killer = st.mean(x["weights"][KILLER] for x in grp)
        print(f"{config:34} {mean('duels'):>8.0f} {mean('accepted'):>5.0f} {mean('declined'):>5.0f} "
              f"{saver:>7.0f} {killer:>10.0f} {mean('gap'):>+8.0f} {mean('total'):>7.0f}")

    print()
    best = max(configs, key=lambda c: st.mean(r["gap"] for r in rows if r["config"] == c))
    worst = min(configs, key=lambda c: st.mean(r["gap"] for r in rows if r["config"] == c))
    print(f"лучше всего для бережливого: {best} (разрыв {st.mean(r['gap'] for r in rows if r['config'] == best):+.0f} кг)")
    print(f"хуже всего:                  {worst} (разрыв {st.mean(r['gap'] for r in rows if r['config'] == worst):+.0f} кг)")


def chart_gap(series):
    fig, ax = plt.subplots(figsize=(12, 6))
    markers = {"feed-steal": "-", "depreciate": "--"}
    colors = {"age": "#1f77b4", "witness": "#2ca02c", "achievements": "#9467bd"}

    for config in sorted(series):
        chance, debuff, glory = config.split("-")[0], config.split("-")[1], config.split("-")[2]
        by_day = defaultdict(list)
        for rows in series[config]:
            for row in rows:
                by_day[row["day"]].append(row["weights"][SAVER] - row["weights"][KILLER])

        days = sorted(by_day)
        values = [st.mean(by_day[d]) for d in days]
        label = f"{chance} / {debuff} / слава:{glory}"
        ax.plot(days, values, color=colors.get(chance, "#333333"), linestyle=markers.get(debuff, "-"),
                linewidth=1.6, alpha=0.85, label=label)

    ax.axhline(0, color="#000000", linewidth=1)
    ax.set_xlabel("день симуляции")
    ax.set_ylabel("вес Kevish минус вес Вячесlove, кг")
    ax.set_title("Кто впереди: бережливый (Kevish) или убийца (Вячесlove). Выше нуля — бережливый")
    ax.grid(alpha=0.3)
    ax.legend(fontsize=7, ncol=2, loc="lower left")
    fig.tight_layout()
    path = os.path.join(OUT, "combo-gap.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


def chart_bars(rows):
    configs = sorted(set(r["config"] for r in rows))
    gaps = [st.mean(r["gap"] for r in rows if r["config"] == c) for c in configs]
    errors = [st.pstdev([r["gap"] for r in rows if r["config"] == c]) if len([r for r in rows if r["config"] == c]) > 1 else 0 for c in configs]

    fig, ax = plt.subplots(figsize=(12, 5))
    colors = ["#2ca02c" if g > 0 else "#d62728" for g in gaps]
    ax.bar(range(len(configs)), gaps, yerr=errors, color=colors, alpha=0.85, capsize=3)
    ax.set_xticks(range(len(configs)))
    ax.set_xticklabels(configs, rotation=45, ha="right", fontsize=8)
    ax.axhline(0, color="#000000", linewidth=1)
    ax.set_ylabel("разрыв на 90-й день, кг")
    ax.set_title("Итоговый разрыв «бережливый минус убийца» по конфигурациям (зелёный — бережливый впереди)")
    ax.grid(alpha=0.3, axis="y")
    fig.tight_layout()
    path = os.path.join(OUT, "combo-bars.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


if __name__ == "__main__":
    rows = load_results()
    if not rows:
        raise SystemExit("нет результатов combo-*")
    print_table(rows)
    print()
    print(chart_bars(rows))
    series = load_series()
    if series:
        print(chart_gap(series))
