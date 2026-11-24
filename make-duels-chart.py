"""Сезон с дуэлями и без: медианные траектории и разброс разрыва."""
import glob, json, os, statistics as st
import matplotlib; matplotlib.use("Agg")
import matplotlib.pyplot as plt

OUT = "/home/user/out/swinebot-duels-vs-none.png"
CASES = [("l39-noduels", "без дуэлей (0 боёв)", "#8a8f98", 1.6),
         ("l38-max",     "с дуэлями (~84 боя в год)", "#4a90d9", 2.4)]

def med(prefix, name):
    runs = [[json.loads(l) for l in open(f, encoding="utf-8") if l.strip()]
            for f in sorted(glob.glob(f"results/{prefix}-*.jsonl"))]
    days = min(len(r) for r in runs)
    xs = list(range(days))
    return xs, [st.median(r[d]["weights"].get(name, 0) for r in runs) for d in xs]

fig = plt.figure(figsize=(13, 6.5), dpi=140)
gs = fig.add_gridspec(1, 2, width_ratios=[1.7, 1], wspace=0.22)
ax, ax2 = fig.add_subplot(gs[0, 0]), fig.add_subplot(gs[0, 1])

for prefix, label, color, lw in CASES:
    xs, hans = med(prefix, "Ганс")
    ax.plot(xs, hans, color=color, lw=lw, label=f"Ганс — {label}")
xs, kev = med("l38-max", "Kevish")
ax.plot(xs, kev, color="#e8833a", lw=1.3, ls=":", label="Kevish — с дуэлями")
xs, kev2 = med("l39-noduels", "Kevish")
ax.plot(xs, kev2, color="#c9a227", lw=1.3, ls=":", label="Kevish — без дуэлей")
ax.set_title("Медиана по 20 прогонам: дуэли против их отсутствия", fontsize=12, pad=10)
ax.set_xlabel("день года"); ax.set_ylabel("вес, кг")
ax.legend(fontsize=9, frameon=False, loc="upper left")
ax.grid(alpha=0.25, lw=0.6)
for s in ("top", "right"): ax.spines[s].set_visible(False)

for i, (prefix, label, color, _) in enumerate(CASES):
    gaps = []
    for f in sorted(glob.glob(f"results/{prefix}-*.jsonl")):
        days = [json.loads(l) for l in open(f, encoding="utf-8") if l.strip()]
        gaps.append(days[-1]["weights"].get("Kevish", 0) - days[-1]["weights"].get("Ганс", 0))
    y = len(CASES) - i
    ax2.scatter(gaps, [y]*len(gaps), color=color, alpha=0.6, s=26)
    ax2.axvline(st.median(gaps), color=color, lw=1.6, ls="--")
    ax2.annotate(f"медиана {st.median(gaps):+.0f}\nsd {st.pstdev(gaps):.0f}", (st.median(gaps), y+0.25),
                 fontsize=8.5, color=color, ha="center")
ax2.axvline(0, color="#333333", lw=1)
ax2.set_yticks([2, 1]); ax2.set_yticklabels(["без дуэлей", "с дуэлями"], fontsize=9)
ax2.set_ylim(0.5, 2.7)
ax2.set_xlabel("финальный разрыв Kevish − Ганс, кг")
ax2.set_title("Разброс исхода", fontsize=11, pad=8)
ax2.grid(alpha=0.25, axis="x", lw=0.6)
for s in ("top", "right", "left"): ax2.spines[s].set_visible(False)

fig.text(0.01, 0.015, "Медианы по 20 прогонам по 365 дней, конфиг: k=0.02, φ=2.0, порог 0.5, наследник 40%, "
                      "забой не понижает множитель; убийца режет первый месяц.",
         fontsize=8.5, color="#555555")
os.makedirs("/home/user/out", exist_ok=True)
fig.savefig(OUT, bbox_inches="tight", facecolor="white")
print("сохранено:", OUT)
