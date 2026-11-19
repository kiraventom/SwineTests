"""365-дневные прогоны: база без дуэлей + 12 комбинаций.

Показывает, как каждая комбинация влияет на разрыв «бережливый (Kevish) минус
убийца (Вячесlove)» и на суммарный вес группы.
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

SAVER = "Kevish"
KILLER = "Вячесlove"


def load_series(prefix):
    data = {}
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.jsonl"))):
        name = os.path.basename(path)[len(prefix) + 1:-len(".jsonl")]
        if name.startswith("runner"):
            continue
        data[name] = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    return data


def load_results(prefix):
    rows = []
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.log"))):
        text = open(path, encoding="utf-8", errors="replace").read()
        m = re.search(r"RESULT consent=[\d.]+ duels=(\d+) accepted=(\d+) declined=(\d+) "
                      r"bigDelta=(-?\d+) smallDelta=(-?\d+) totalDelta=(-?\d+) weights=(.+)", text)
        if not m:
            continue
        name = os.path.basename(path)[len(prefix) + 1:-len(".log")]
        weights = dict((k, int(v)) for k, v in (p.split(":") for p in m.group(7).split(",")))
        rows.append(dict(name=name, duels=int(m.group(1)), accepted=int(m.group(2)), declined=int(m.group(3)),
                         total=int(m.group(6)), weights=weights, gap=weights[SAVER] - weights[KILLER]))
    return rows


def chart(data, base_name):
    fig, ax = plt.subplots(figsize=(13, 7))
    colors = {"age": "#1f77b4", "witness": "#2ca02c", "achievements": "#9467bd"}
    styles = {"feed-steal": "--", "depreciate": ":"}

    if base_name in data:
        days = [r["day"] for r in data[base_name]]
        values = [r["weights"][SAVER] - r["weights"][KILLER] for r in data[base_name]]
        ax.plot(days, values, color="#000000", linewidth=3, label="без дуэлей (забои только до 30-го дня)")

    for name, rows in sorted(data.items()):
        if name == base_name:
            continue
        parts = name.split("-")
        if len(parts) < 3:
            continue
        chance, debuff, glory = parts[0], parts[1], parts[2]
        days = [r["day"] for r in rows]
        values = [r["weights"][SAVER] - r["weights"][KILLER] for r in rows]
        ax.plot(days, values, color=colors.get(chance, "#777777"), linestyle=styles.get(debuff, "-"),
                linewidth=1.4, alpha=0.8, label=f"{chance} / {debuff} / слава:{glory}")

    ax.axhline(0, color="#333333", linewidth=1)
    ax.axvline(30, color="#d62728", linestyle="--", linewidth=1, alpha=0.7)
    ax.set_xlabel("день симуляции")
    ax.set_ylabel("разрыв: Kevish минус Вячесlove, кг")
    ax.set_title("365 дней: как дуэльные механики влияют на разрыв «бережливый vs убийца»")
    ax.grid(alpha=0.3)
    ax.legend(fontsize=7, ncol=2, loc="upper left")
    fig.tight_layout()
    path = os.path.join(OUT, "long-gap.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


def chart_bars(rows, base_rows):
    rows = sorted(rows, key=lambda r: -r["gap"])
    labels = [r["name"] for r in rows]
    gaps = [r["gap"] for r in rows]
    base_gap = st.mean(r["gap"] for r in base_rows) if base_rows else None

    fig, ax = plt.subplots(figsize=(12, 5))
    ax.bar(range(len(rows)), gaps, color="#2ca02c", alpha=0.8)
    if base_gap is not None:
        ax.axhline(base_gap, color="#000000", linewidth=2, linestyle="--",
                   label=f"без дуэлей: {base_gap:+.0f} кг")
        ax.legend()
    ax.set_xticks(range(len(rows)))
    ax.set_xticklabels(labels, rotation=45, ha="right", fontsize=8)
    ax.set_ylabel("разрыв на 365-й день, кг")
    ax.set_title("Итоговый разрыв «бережливый минус убийца»: чем выше, тем сильнее бережливый")
    ax.grid(alpha=0.3, axis="y")
    fig.tight_layout()
    path = os.path.join(OUT, "long-bars.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


if __name__ == "__main__":
    base = load_series("base")
    long_data = load_series("long")

    base_rows = load_results("base")
    rows = load_results("long")

    print(f"{'конфигурация':30} {'вызовов':>8} {'Kevish':>8} {'Вячесlove':>10} {'разрыв':>8} {'всего':>7}")
    for r in sorted(rows, key=lambda r: -r["gap"]):
        print(f"{r['name']:30} {r['duels']:>8} {r['weights'][SAVER]:>8} {r['weights'][KILLER]:>10} "
              f"{r['gap']:>+8} {r['total']:>7}")

    if base_rows:
        b = base_rows[0]
        print(f"\n{'без дуэлей':30} {0:>8} {b['weights'][SAVER]:>8} {b['weights'][KILLER]:>10} {b['gap']:>+8} {b['total']:>7}")

    print()
    print(chart({**base, **long_data}, "base-365"))
    print(chart_bars(rows, base_rows))
