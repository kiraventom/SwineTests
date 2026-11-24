"""Графики по медианам прогонов: как /history, но медиана по 20 сезонам.

Панели:
1. Kevish vs Ганс — старые числа (0.03 / 1.5 / наследник 100%) против новых (0.02 / 2.0 / 40%).
2. Все свины на новых числах (медиана).
3. Финальный разрыв Kevish−Ганс по каждому прогону: было и стало.
"""
import glob
import json
import os
import statistics as st

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt

OUT = "/home/user/out/swinebot-multiplier-medians.png"
STEP = 3


def load(prefix):
    runs = []
    for path in sorted(glob.glob(f"results/{prefix}-*.jsonl")):
        runs.append([json.loads(line) for line in open(path, encoding="utf-8") if line.strip()])
    if not runs:
        raise SystemExit(f"нет данных по {prefix}")

    days = min(len(r) for r in runs)
    names = list(runs[0][0]["weights"].keys())
    xs = list(range(0, days, STEP))
    med = {n: [st.median(r[d]["weights"].get(n, 0) for r in runs) for d in xs] for n in names}
    gaps = [r[-1]["weights"].get("Kevish", 0) - r[-1]["weights"].get("Ганс", 0) for r in runs]
    return xs, med, gaps, len(runs)


old_x, old_med, old_gaps, old_n = load("l30-final")
new_x, new_med, new_gaps, new_n = load("l33-final")

fig = plt.figure(figsize=(13, 8.5), dpi=140)
fig.patch.set_facecolor("#ffffff")
gs = fig.add_gridspec(2, 2, width_ratios=[1.35, 1], hspace=0.32, wspace=0.22)

# --- 1. дуэль бережливого и убийцы ---
ax = fig.add_subplot(gs[:, 0])
ax.plot(old_x, old_med["Kevish"], color="#4a90d9", ls="--", lw=1.6,
        label="Kevish — старые числа")
ax.plot(old_x, old_med["Ганс"], color="#e8833a", ls="--", lw=1.6,
        label="Ганс — старые числа")
ax.plot(new_x, new_med["Kevish"], color="#4a90d9", lw=2.4, label="Kevish — новые числа")
ax.plot(new_x, new_med["Ганс"], color="#e8833a", lw=2.4, label="Ганс — новые числа")
ax.set_title("Медиана по 20 прогонам: бережливый против убийцы", fontsize=12, pad=10)
ax.set_xlabel("день года")
ax.set_ylabel("вес, кг")
ax.legend(fontsize=9, frameon=False, loc="upper left")
ax.grid(alpha=0.25, lw=0.6)
for side in ("top", "right"):
    ax.spines[side].set_visible(False)

# --- 2. все свины на новых числах ---
ax2 = fig.add_subplot(gs[0, 1])
# цвета закреплены за свинами, чтобы панели читались одинаково
FIXED = {"Kevish": "#4a90d9", "Ганс": "#e8833a"}
MUTED = ["#7b6fd0", "#3f9e8f", "#8a8f98", "#b45fa8", "#c9a227", "#5aa05a"]
muted = iter(MUTED)
for name, series in sorted(new_med.items(), key=lambda kv: -kv[1][-1]):
    color = FIXED.get(name) or next(muted)
    ax2.plot(new_x, series, lw=2.0 if name in FIXED else 1.1, color=color, label=name)
ax2.set_title("Все свины, новые числа (медиана)", fontsize=11, pad=8)
ax2.set_xlabel("день года")
ax2.set_ylabel("вес, кг")
ax2.legend(fontsize=7.5, frameon=False, ncol=2)
ax2.grid(alpha=0.25, lw=0.6)
for side in ("top", "right"):
    ax2.spines[side].set_visible(False)

# --- 3. финальный разрыв по прогонам ---
ax3 = fig.add_subplot(gs[1, 1])
ax3.scatter(old_gaps, [1] * len(old_gaps), color="#e8833a", alpha=0.55, s=28, label="старые")
ax3.scatter(new_gaps, [0] * len(new_gaps), color="#4a90d9", alpha=0.55, s=28, label="новые")
ax3.axvline(st.median(old_gaps), color="#e8833a", lw=1.8, ls="--")
ax3.axvline(st.median(new_gaps), color="#4a90d9", lw=1.8, ls="--")
ax3.axvline(0, color="#444444", lw=1)
ax3.set_yticks([1, 0])
ax3.set_yticklabels(["старые", "новые"], fontsize=9)
ax3.set_ylim(-0.6, 1.6)
ax3.set_xlabel("финальный разрыв Kevish − Ганс, кг")
ax3.set_title(f"Разрыв по прогонам: медиана {st.median(old_gaps):+.0f} → {st.median(new_gaps):+.0f}", fontsize=11, pad=8)
ax3.grid(alpha=0.25, axis="x", lw=0.6)
for side in ("top", "right", "left"):
    ax3.spines[side].set_visible(False)

fig.text(0.01, 0.015,
         f"Медиана по {old_n} и {new_n} прогонам по 365 дней; шаг {STEP} дня. "
         "Старые: k=0.03, φ=1.5, наследник 100%. Новые: k=0.02, φ=2.0, наследник 40%.",
         fontsize=8.5, color="#555555")

os.makedirs("/home/user/out", exist_ok=True)
fig.savefig(OUT, bbox_inches="tight", facecolor=fig.get_facecolor())
print("сохранено:", OUT)
print(f"медианы разрыва: {st.median(old_gaps):+.0f} -> {st.median(new_gaps):+.0f}")
