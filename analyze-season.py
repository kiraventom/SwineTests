"""Сводка сезонов: медианы весов, разрыв «бережливый — убийца», разброс, апсеты.

Запуск: python3 analyze-season.py release
Читает results/<prefix>-*.log (строки RESULT и сводку боя), печатает медианы.
"""
import glob
import os
import re
import statistics as st
import sys

RESULTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), "results")
RESULT = re.compile(r"^RESULT .*weights=(\S+)", re.M)
FIGHTS = re.compile(r"^боёв (\d+), апсетов (\d+) \((\d+)%\)", re.M)
CALLS = re.compile(r"^попыток вызова (\d+), из них запросов создано (\d+), отбито лимитами (\d+)", re.M)


def load(prefix):
    rows = []
    for path in sorted(glob.glob(os.path.join(RESULTS, f"{prefix}-[0-9]*.log"))):
        text = open(path, encoding="utf-8", errors="replace").read()
        m = RESULT.search(text)
        if not m:
            continue
        weights = {k: int(v) for k, v in (kv.split(":") for kv in m.group(1).split(","))}
        fights, calls = FIGHTS.search(text), CALLS.search(text)
        rows.append(dict(
            weights=weights,
            fights=int(fights.group(1)) if fights else 0,
            upsets=int(fights.group(3)) if fights else 0,
            calls=int(calls.group(1)) if calls else 0,
            created=int(calls.group(2)) if calls else 0,
            rejected=int(calls.group(3)) if calls else 0,
            ok="ALL UPDATES HANDLED" in text,
        ))
    return rows


def main(prefix):
    rows = load(prefix)
    if not rows:
        print(f"{prefix}: прогонов с RESULT нет")
        return

    kevish = [r["weights"].get("Kevish", 0) for r in rows]
    hans = [r["weights"].get("Ганс", 0) for r in rows]
    gaps = [h - k for k, h in zip(kevish, hans)]

    print(f"{len(rows)} прогонов")
    print(f"Kevish: медиана {st.median(kevish):>7.0f}  min {min(kevish):>6}  max {max(kevish):>6}")
    print(f"Ганс:   медиана {st.median(hans):>7.0f}  min {min(hans):>6}  max {max(hans):>6}")
    print(f"разрыв (Ганс − Kevish): медиана {st.median(gaps):>+7.0f}  sd {st.pstdev(gaps):>6.0f}  "
          f"min {min(gaps):+}  max {max(gaps):+}")
    print(f"Kevish впереди: {sum(1 for g in gaps if g < 0)}/{len(rows)};  "
          f"убийца ниже 10к: {sum(1 for h in hans if h < 10000)}/{len(rows)};  "
          f"обнуления: {sum(1 for r in rows for v in r['weights'].values() if v < 100)}")
    print(f"боёв {st.median([r['fights'] for r in rows]):.0f}, апсетов "
          f"{st.median([r['upsets'] for r in rows]):.0f}%, "
          f"попыток {st.median([r['calls'] for r in rows]):.0f} "
          f"(создано {st.median([r['created'] for r in rows]):.0f}, "
          f"отбито {st.median([r['rejected'] for r in rows]):.0f})")
    print(f"все прогоны без сбоев: {all(r['ok'] for r in rows)}")
    print("медианные веса: " + ", ".join(
        f"{name}={st.median([r['weights'].get(name, 0) for r in rows]):.0f}" for name in rows[0]["weights"]))


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "release")
