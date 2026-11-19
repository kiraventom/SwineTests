"""Свод по матрице l16 (witness-бонус 0.5 п.п.): вес Kevish и Ганса на финише 365 дней.

Читает results/l16-*.log, вытаскивает строку RESULT с весами всех свинов,
считает средние, sd и разрыв Kevish-Ганс.
"""
import glob
import os
import re
import statistics as st

RESULTS = os.path.join(os.path.dirname(__file__), "results")
RE = re.compile(r"RESULT consent=[\d.]+ duels=(\d+) accepted=(\d+) declined=(\d+) "
                r"bigDelta=(-?\d+) smallDelta=(-?\d+) totalDelta=(-?\d+) weights=(.+)")


def main(prefix="l16"):
    rows = []
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.log"))):
        text = open(path, encoding="utf-8", errors="replace").read()
        m = RE.search(text)
        if not m:
            continue
        w = dict((k, int(v)) for k, v in (p.split(":") for p in m.group(7).split(",")))
        rows.append(dict(name=os.path.basename(path), duels=int(m.group(1)),
                         accepted=int(m.group(2)), declined=int(m.group(3)), w=w))

    if not rows:
        print("нет данных")
        return

    def series(pig):
        return [r["w"].get(pig, 0) for r in rows]

    kev, hans = series("Kevish"), series("Ганс")
    delta = [k - h for k, h in zip(kev, hans)]

    print(f"прогонов: {len(rows)}")
    print(f"Kevish : mean {st.mean(kev):8.0f}  sd {st.pstdev(kev):7.0f}  min {min(kev):6d}  max {max(kev):6d}")
    print(f"Ганс   : mean {st.mean(hans):8.0f}  sd {st.pstdev(hans):7.0f}  min {min(hans):6d}  max {max(hans):6d}")
    print(f"разрыв : mean {st.mean(delta):+8.0f}  sd {st.pstdev(delta):7.0f}  "
          f"медиана {st.median(delta):+.0f}  сем {st.pstdev(delta) / len(delta) ** 0.5:.0f}")
    ahead = sum(1 for d in delta if d > 0)
    print(f"Kevish впереди: {ahead}/{len(delta)}")
    duels = [r["duels"] for r in rows]
    acc = [r["accepted"] for r in rows]
    print(f"вызовов: {st.mean(duels):.0f}  принято: {st.mean(acc):.1f}  "
          f"боёв за прогон: {st.mean(acc):.1f}")

    pigs = {}
    for r in rows:
        for k, v in r["w"].items():
            pigs.setdefault(k, []).append(v)
    print("\nвсе свины (mean / sd):")
    for k, v in sorted(pigs.items(), key=lambda kv: -st.mean(kv[1])):
        print(f"  {k:12s} {st.mean(v):8.0f} / {st.pstdev(v):6.0f}")


if __name__ == "__main__":
    import sys
    main(sys.argv[1] if len(sys.argv) > 1 else "l16")
