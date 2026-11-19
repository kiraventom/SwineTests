"""Диагностика убийцы по матрице: разбор summary-таблицы из логов.

Для каждого прогона достаём строки сводки (start/final/delta/feeds/throws/duels W/L/decl/slaugh)
и смотрим, чем Ганс отличается от Kevish: сколько кормёжек, сколько отказов, сколько боёв.
"""
import glob
import os
import re
import statistics as st

RESULTS = os.path.join(os.path.dirname(__file__), "results")
ROW = re.compile(r"^(\S+)\s+(\d+)\s+(\d+)\s+(-?\d+)\s+(\d+)\s+(\d+)\s+(\d+)/(\d+)\s+(\d+)\s+(\d+)\s*$")


def load(prefix):
    runs = []
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-*.log"))):
        rows = {}
        for line in open(path, encoding="utf-8", errors="replace"):
            m = ROW.match(line.rstrip("\r\n"))
            if m:
                rows[m.group(1)] = dict(start=int(m.group(2)), final=int(m.group(3)),
                                        delta=int(m.group(4)), feeds=int(m.group(5)),
                                        throws=int(m.group(6)), wins=int(m.group(7)),
                                        losses=int(m.group(8)), decl=int(m.group(9)),
                                        slaugh=int(m.group(10)))
        if rows:
            runs.append((os.path.basename(path), rows))
    return runs


def main():
    for prefix in ("l15", "l16"):
        runs = load(prefix)
        if not runs:
            continue
        print(f"\n=== {prefix} ({len(runs)} прогонов) ===")
        for who in ("Kevish", "Ганс"):
            for key, label in (("feeds", "кормёжек"), ("throws", "блевот"),
                               ("wins", "побед"), ("losses", "поражений"),
                               ("decl", "отказов"), ("delta", "прирост")):
                vals = [r[who][key] for _, r in runs if who in r]
                print(f"  {who:6s} {label:10s} {st.mean(vals):8.1f} ± {st.pstdev(vals):7.1f}")
            print()

        # связь: отказы Ганса -> его итоговый вес
        pairs = [(r["Ганс"]["decl"], r["Ганс"]["final"]) for _, r in runs if "Ганс" in r]
        print("  Ганс: отказы -> финальный вес")
        for d, f in sorted(pairs):
            print(f"    {d:4d} отказов -> {f:6d} кг")


if __name__ == "__main__":
    main()
