"""Строит графики по дневным рядам реплея (results/series-*.jsonl).

Каждая политика согласия усредняется по трём прогонам. Красная вертикальная линия —
день, с которого включены дуэли (по умолчанию 30 из 90).
"""
import glob
import json
import os
import statistics as st
from collections import defaultdict

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt

RESULTS = os.path.join(os.path.dirname(__file__), "results")
OUT = "/home/user/out"
DUELS_FROM = 30

POLICIES = [
    ("none", "без дуэлей", "#555555", "-"),
    ("c0", "0% согласие (всегда отказ)", "#d62728", "-"),
    ("c33", "33% согласие", "#ff7f0e", "-"),
    ("c66", "66% согласие", "#2ca02c", "-"),
    ("c100", "100% согласие (всегда бой)", "#1f77b4", "-"),
]


def load():
    """{policy: [series, series, ...]}"""
    data = defaultdict(list)
    for path in sorted(glob.glob(os.path.join(RESULTS, "series-*.jsonl"))):
        name = os.path.basename(path)[len("series-"):-len(".jsonl")]
        policy = name.rsplit("-", 1)[0]
        rows = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
        data[policy].append(rows)
    return data


def mean_series(series_list, key):
    """Средний по прогонам ряд: список (день, значение)."""
    by_day = defaultdict(list)
    for rows in series_list:
        for row in rows:
            value = row["total"] if key == "total" else row["weights"][key]
            by_day[row["day"]].append(value)
    return [d for d in sorted(by_day)], [st.mean(by_day[d]) for d in sorted(by_day)]


def draw_duel_marker(ax):
    ax.axvline(DUELS_FROM, color="#d62728", linestyle="--", linewidth=1.5, alpha=0.8)
    ax.annotate("дуэли включены", xy=(DUELS_FROM, 0.5), xycoords=("data", "axes fraction"),
                xytext=(4, 0), textcoords="offset points", color="#d62728", fontsize=9, rotation=90, va="center")


def chart_total(data):
    fig, ax = plt.subplots(figsize=(11, 6))
    for policy, label, color, style in POLICIES:
        if policy not in data:
            continue
        days, values = mean_series(data[policy], "total")
        ax.plot(days, values, color=color, linestyle=style, linewidth=2, label=label)

    draw_duel_marker(ax)
    ax.set_xlabel("день симуляции")
    ax.set_ylabel("суммарный вес 7 свинов, кг")
    ax.set_title("Суммарный вес группы: что делают дуэли (включены с 30-го дня)")
    ax.grid(alpha=0.3)
    ax.legend(loc="lower right")
    fig.tight_layout()
    path = os.path.join(OUT, "duels-v2-total.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


def chart_per_swine(data):
    owners = list(next(iter(data.values()))[0][0]["weights"].keys())
    fig, axes = plt.subplots(3, 3, figsize=(14, 10), sharex=True)
    axes = axes.flatten()

    for i, owner in enumerate(owners):
        ax = axes[i]
        for policy, label, color, style in POLICIES:
            if policy not in data or policy in ("c33", "c66"):
                continue
            days, values = mean_series(data[policy], owner)
            ax.plot(days, values, color=color, linewidth=1.8, label=label if i == 0 else None)

        draw_duel_marker(ax)
        ax.set_title(owner, fontsize=11)
        ax.grid(alpha=0.3)

    for j in range(len(owners), len(axes)):
        axes[j].axis("off")

    fig.suptitle("Вес каждого свина: без дуэлей / всегда отказ / всегда бой", fontsize=14)
    fig.supxlabel("день симуляции")
    fig.supylabel("вес, кг")
    axes[0].legend(loc="upper left", fontsize=8)
    fig.tight_layout()
    path = os.path.join(OUT, "duels-v2-per-swine.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


def chart_delta(data):
    """Отклонение суммарного веса от прогона без дуэлей."""
    base_days, base = mean_series(data["none"], "total")
    fig, ax = plt.subplots(figsize=(11, 5))

    for policy, label, color, style in POLICIES:
        if policy == "none" or policy not in data:
            continue
        days, values = mean_series(data[policy], "total")
        ax.plot(days, [v - b for v, b in zip(values, base)], color=color, linewidth=2, label=label)

    draw_duel_marker(ax)
    ax.axhline(0, color="#555555", linewidth=1)
    ax.set_xlabel("день симуляции")
    ax.set_ylabel("суммарный вес минус «без дуэлей», кг")
    ax.set_title("Отклонение от режима без дуэлей")
    ax.grid(alpha=0.3)
    ax.legend(loc="upper left")
    fig.tight_layout()
    path = os.path.join(OUT, "duels-v2-delta.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    return path


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    data = load()
    if not data:
        raise SystemExit("нет рядов: сначала прогони replay-matrix.sh")

    for path in (chart_total(data), chart_per_swine(data), chart_delta(data)):
        print(path)
