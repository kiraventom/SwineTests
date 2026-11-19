"""Метрики v3 по матрице реплеев (DUELS-V3-DESIGN.md §8).

Читает results/<prefix>-*.log, разбирает summary-таблицу и строку RESULT,
считает частоту боёв, долю отказов и апсетов, массы лидера и убийцы.
sd разрыва тоже печатается — для сравнения с прошлыми прогонами, но решение
принимается по медиане и долям, а не по sd.
"""
import glob
import os
import re
import statistics as st
import sys

RESULTS = os.path.join(os.path.dirname(__file__), "results")

ROW = re.compile(r"^(\S+)\s+(\d+)\s+(\d+)\s+(-?\d+)\s+(\d+)\s+(\d+)\s+(\d+)/(\d+)\s+(\d+)\s+(-?\d+)\s+(\d+)\s*$")
RESULT = re.compile(r"RESULT consent=[\d.]+ duels=(\d+) accepted=(\d+) declined=(\d+) upsets=(\d+) "
                    r"glory=(-?\d+) bigDelta=(-?\d+) smallDelta=(-?\d+) totalDelta=(-?\d+) weights=(.+)")
GOLDEN = ("Kevish", "Ганс")


def load(prefix):
    runs = []
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.log"))):
        text = open(path, encoding="utf-8", errors="replace").read()
        m = RESULT.search(text)
        if not m:
            continue
        rows = {}
        for line in text.splitlines():
            r = ROW.match(line.strip())
            if r:
                rows[r.group(1)] = dict(start=int(r.group(2)), final=int(r.group(3)),
                                        delta=int(r.group(4)), feeds=int(r.group(5)),
                                        throws=int(r.group(6)), wins=int(r.group(7)),
                                        losses=int(r.group(8)), decl=int(r.group(9)),
                                        glory=int(r.group(10)), slaugh=int(r.group(11)))
        runs.append(dict(name=os.path.basename(path),
                         duels=int(m.group(1)), accepted=int(m.group(2)), declined=int(m.group(3)),
                         upsets=int(m.group(4)), glory=int(m.group(5)), rows=rows))
    return runs


def pct(a, b):
    return f"{100 * a / b:.0f}%" if b else "—"


def main(prefix):
    runs = load(prefix)
    if not runs:
        print(f"нет данных по {prefix}")
        return

    n = len(runs)
    print(f"=== {prefix}: {n} прогонов по году ===\n")

    fights = [r["accepted"] for r in runs]
    declines = [r["declined"] for r in runs]
    upsets = [r["upsets"] for r in runs]
    glory = [r["glory"] for r in runs]

    print(f"боёв за год:      {st.mean(fights):6.1f}  (мин {min(fights)}, макс {max(fights)})")
    print(f"отказов за год:   {st.mean(declines):6.1f}  ({pct(st.mean(declines), st.mean(fights) + st.mean(declines))} вызовов)")
    print(f"апсетов за год:   {st.mean(upsets):6.1f}  ({pct(st.mean(upsets), st.mean(fights))} боёв)")
    print(f"сумма славы:      {st.mean(glory):6.1f}  (мин {min(glory)}, макс {max(glory)})")

    kev = [r["rows"]["Kevish"]["final"] for r in runs if "Kevish" in r["rows"]]
    han = [r["rows"]["Ганс"]["final"] for r in runs if "Ганс" in r["rows"]]
    gap = [k - h for k, h in zip(kev, han)]

    print(f"\nKevish:  медиана {st.median(kev):7.0f}  среднее {st.mean(kev):7.0f}  мин {min(kev)}  макс {max(kev)}")
    print(f"Ганс:    медиана {st.median(han):7.0f}  среднее {st.mean(han):7.0f}  мин {min(han)}  макс {max(han)}")
    print(f"разрыв:  медиана {st.median(gap):+7.0f}  среднее {st.mean(gap):+7.0f}  sd {st.pstdev(gap):6.0f}")
    print(f"Kevish впереди: {sum(1 for g in gap if g > 0)}/{n}")
    print(f"убийца ниже 10 000: {sum(1 for h in han if h < 10000)}/{n}")
    print(f"кто-то обнулился (<500 кг): {sum(1 for r in runs for v in r['rows'].values() if v['final'] < 500)} случаев")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "l17")
