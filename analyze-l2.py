"""365 дней с синтетическим убийцей «Кровавый Ганс» (кормит часто, забивает регулярно).

Смотрим три вещи:
1. Кто впереди: бережливый Kevish или эффективный убийца Ганс.
2. Не душат ли дуэли слабого убийцу (Вячесlove) сильнее, чем база без дуэлей.
3. Как меняется суммарный вес группы.
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
WEAK_KILLER = "Вячесlove"
REAL_KILLER = "Ганс"


def load(prefix):
    results, series = [], {}
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.log"))):
        name = os.path.basename(path)[len(prefix) + 1:-len(".log")]
        if name.startswith("runner"):
            continue
        text = open(path, encoding="utf-8", errors="replace").read()
        m = re.search(r"RESULT consent=[\d.]+ duels=(\d+) accepted=(\d+) declined=(\d+) "
                      r"bigDelta=(-?\d+) smallDelta=(-?\d+) totalDelta=(-?\d+) weights=(.+)", text)
        if not m:
            continue
        weights = dict((k, int(v)) for k, v in (p.split(":") for p in m.group(7).split(",")))
        results.append(dict(name=name, duels=int(m.group(1)), total=int(m.group(6)), weights=weights))

    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.jsonl"))):
        name = os.path.basename(path)[len(prefix) + 1:-len(".jsonl")]
        series[name] = [json.loads(line) for line in open(path, encoding="utf-8") if line.strip()]
    return results, series


def group(results):
    """Схлопывает прогоны одной конфигурации."""
    by_config = defaultdict(list)
    for row in results:
        config = row["name"].rsplit("-", 1)[0]
        by_config[config].append(row)

    out = {}
    for config, rows in by_config.items():
        out[config] = dict(
            runs=len(rows), duels=st.mean(r["duels"] for r in rows), total=st.mean(r["total"] for r in rows),
            saver=st.mean(r["weights"].get(SAVER, 0) for r in rows),
            weak=st.mean(r["weights"].get(WEAK_KILLER, 0) for r in rows),
            killer=st.mean(r["weights"].get(REAL_KILLER, 0) for r in rows),
            saver_sd=st.pstdev([r["weights"].get(SAVER, 0) for r in rows]) if len(rows) > 1 else 0,
        )
    return out


def main():
    results, series = load("l4")
    grouped = group(results)
    base = {k: v for k, v in grouped.items() if k.startswith("base")}
    base_avg = dict(
        saver=st.mean(v["saver"] for v in base.values()),
        weak=st.mean(v["weak"] for v in base.values()),
        killer=st.mean(v["killer"] for v in base.values()),
        total=st.mean(v["total"] for v in base.values()),
    ) if base else None

    print(f"{'конфигурация':18} {'прогонов':>8} {'вызовов':>8} {'Kevish':>9} {'Ганс':>8} {'разрыв':>8} {'Вячесlove':>10} {'всего':>7}")
    for config in sorted(grouped):
        if config.startswith("base"):
            continue
        g = grouped[config]
        print(f"{config:18} {g['runs']:>8} {g['duels']:>8.0f} {g['saver']:>9.0f} {g['killer']:>8.0f} "
              f"{g['saver'] - g['killer']:>+8.0f} {g['weak']:>10.0f} {g['total']:>7.0f}")

    if base_avg:
        print(f"\n{'без дуэлей':18} {'':>8} {0:>8} {base_avg['saver']:>9.0f} {base_avg['killer']:>8.0f} "
              f"{base_avg['saver'] - base_avg['killer']:>+8.0f} {base_avg['weak']:>10.0f} {base_avg['total']:>7.0f}")

        print("\nудушение дуэлями (вес при дуэлях минус вес без дуэлей):")
        for config in sorted(grouped):
            if config.startswith("base"):
                continue
            g = grouped[config]
            print(f"  {config:18} Kevish {g['saver'] - base_avg['saver']:+8.0f}   "
                  f"Ганс {g['killer'] - base_avg['killer']:+8.0f}   "
                  f"Вячесlove {g['weak'] - base_avg['weak']:+8.0f}")

    # графики
    fig, ax = plt.subplots(figsize=(12, 6))
    if "base-1" in series:
        days = [r["day"] for r in series["base-1"]]
        for name, color, style in ((SAVER, "#1f77b4", "-"), (REAL_KILLER, "#d62728", "-"), (WEAK_KILLER, "#777777", ":")):
            values = [r["weights"].get(name, 0) for r in series["base-1"]]
            ax.plot(days, values, color=color, linestyle=style, linewidth=2, label=f"{name} (без дуэлей)")

    best = "achiev-depr-no-1"
    if best in series:
        days = [r["day"] for r in series[best]]
        for name, color, style in ((SAVER, "#1f77b4", "--"), (REAL_KILLER, "#d62728", "--"), (WEAK_KILLER, "#777777", "-.")):
            values = [r["weights"].get(name, 0) for r in series[best]]
            ax.plot(days, values, color=color, linestyle=style, linewidth=1.6, alpha=0.9,
                    label=f"{name} (achiev/depreciate/без славы)")

    ax.set_xlabel("день")
    ax.set_ylabel("вес, кг")
    ax.set_title("365 дней: бережливый, настоящий убийца и слабый убийца — с дуэлями и без")
    ax.grid(alpha=0.3)
    ax.legend(fontsize=8)
    fig.tight_layout()
    path = os.path.join(OUT, "l4-weights.png")
    fig.savefig(path, dpi=120)
    plt.close(fig)
    print("\n" + path)


if __name__ == "__main__":
    main()
