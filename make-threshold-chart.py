"""Почему при пороге 0.75 не видно затухания, а при 0.5 — видно."""
import glob
import json
import os
import statistics as st

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

OUT = "/home/user/out/swinebot-threshold-compare.png"

CASES = [
    ("l30-final",     "старые числа (k=0.03, φ=1.5, наследник 100%)", "#b0b7c3", 1.4),
    ("l33-final",     "порог 0.75 — налог включается поздно",        "#e8833a", 1.8),
    ("l36-s50",       "порог 0.5 — налог с половины лидера",         "#4a90d9", 2.6),
]


def series(prefix, name):
    runs = []
    for path in sorted(glob.glob(f"results/{prefix}-*.jsonl")):
        runs.append([json.loads(line) for line in open(path, encoding="utf-8") if line.strip()])
    days = min(len(r) for r in runs)
    xs = list(range(days))
    med = [st.median(r[d]["weights"].get(name, 0) for r in runs) for d in xs]
    gaps = [r[-1]["weights"].get("Kevish", 0) - r[-1]["weights"].get("Ганс", 0) for r in runs]
    return xs, med, gaps, len(runs)


fig = plt.figure(figsize=(13, 7), dpi=140)
gs = fig.add_gridspec(1, 2, width_ratios=[1.6, 1], wspace=0.2)
ax = fig.add_subplot(gs[0, 0])
ax2 = fig.add_subplot(gs[0, 1])

for prefix, label, color, lw in CASES:
    xs, med, gaps, n = series(prefix, "Ганс")
    ax.plot(xs, med, color=color, lw=lw, label=f"Ганс: {label}")

xs, med, _, _ = series("l36-s50", "Kevish")
ax.plot(xs, med, color="#4a90d9", lw=1.2, ls=":", label="Kevish: порог 0.5 (для сравнения)")

ax.axvline(219, color="#999999", lw=1, ls="--")
ax.annotate("219-й день: при пороге 0.75\nналог только здесь и включается",
            xy=(219, 9000), xytext=(238, 4200), fontsize=8.5, color="#555555",
            arrowprops=dict(arrowstyle="->", color="#999999", lw=1))
ax.set_title("Ганс, медиана по 20 прогонам: когда начинает работать трата", fontsize=12, pad=10)
ax.set_xlabel("день года")
ax.set_ylabel("вес, кг")
ax.legend(fontsize=8.5, frameon=False, loc="upper left")
ax.grid(alpha=0.25, lw=0.6)
for side in ("top", "right"):
    ax.spines[side].set_visible(False)

for i, (prefix, label, color, _) in enumerate(CASES):
    _, _, gaps, _ = series(prefix, "Ганс")
    short = label.split("—")[0].strip()
    ax2.scatter(gaps, [len(CASES) - i] * len(gaps), color=color, alpha=0.6, s=26)
    ax2.axvline(st.median(gaps), color=color, lw=1.6, ls="--")
ax2.axvline(0, color="#333333", lw=1)
ax2.set_yticks([3, 2, 1])
ax2.set_yticklabels(["старые", "порог 0.75", "порог 0.5"], fontsize=9)
ax2.set_ylim(0.4, 3.6)
ax2.set_xlabel("финальный разрыв Kevish − Ганс, кг")
ax2.set_title("Итог сезона по 20 прогонам", fontsize=11, pad=8)
ax2.grid(alpha=0.25, axis="x", lw=0.6)
for side in ("top", "right", "left"):
    ax2.spines[side].set_visible(False)

fig.text(0.01, 0.015,
         "Медианы по 20 прогонам по 365 дней. Убийца режет первый месяц, дальше живёт с запаса. "
         "Множитель Ганса к концу года: 0.75 → 1.23 (не догорел), 0.5 → 1.00 (догорел).",
         fontsize=8.5, color="#555555")

os.makedirs("/home/user/out", exist_ok=True)
fig.savefig(OUT, bbox_inches="tight", facecolor="white")
print("сохранено:", OUT)
