import re, glob, statistics as st, json

rows = []
for f in sorted(glob.glob('results/replay-*.log')):
    name = f.split('replay-')[1][:-4]
    txt = open(f, encoding='utf-8', errors='replace').read()
    m = re.search(r'RESULT consent=([\d.]+) duels=(\d+) accepted=(\d+) declined=(\d+) bigDelta=(-?\d+) smallDelta=(-?\d+) totalDelta=(-?\d+) weights=(.+)', txt)
    if not m:
        print("нет RESULT:", f)
        continue
    weights = dict((k, int(v)) for k, v in (p.split(':') for p in m.group(8).split(',')))
    rows.append(dict(name=name, consent=float(m.group(1)), duels=int(m.group(2)), acc=int(m.group(3)),
                     dec=int(m.group(4)), big=int(m.group(5)), small=int(m.group(6)), total=int(m.group(7)), w=weights))

def fmt(v, s):
    return "%+.0f ± %.0f" % (v, s)

print("режим      вызовов принято отказов   толстяки d        малыши d        всего d")
for c in sorted(set(r['consent'] for r in rows)):
    grp = [r for r in rows if r['consent'] == c]
    label = "без дуэлей" if all(x['duels'] == 0 for x in grp) else "%.0f%%" % (c * 100)
    mean = lambda k: st.mean(x[k] for x in grp)
    dev = lambda k: st.pstdev([x[k] for x in grp])
    print("%-11s %7.0f %7.0f %7.0f   %-14s %-14s %+.0f" % (
        label, mean('duels'), mean('acc'), mean('dec'),
        fmt(mean('big'), dev('big')), fmt(mean('small'), dev('small')), mean('total')))

print()
cs = sorted(set(r['consent'] for r in rows))
labels = []
for c in cs:
    grp = [r for r in rows if r['consent'] == c]
    labels.append("без дуэлей" if all(x['duels'] == 0 for x in grp) else "%.0f%%" % (c * 100))

owners = list(rows[0]['w'].keys())
print("%-12s" % "владелец" + "".join("%12s" % l for l in labels))
for o in owners:
    print("%-12s" % o + "".join("%12.0f" % st.mean([r['w'][o] for r in rows if r['consent'] == c]) for c in cs))

json.dump(rows, open('results/matrix.json', 'w'), ensure_ascii=False, indent=1)
