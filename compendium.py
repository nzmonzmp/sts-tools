import json, glob, os
from collections import Counter

path = os.path.expanduser("~/.local/share/SlayTheSpire2/steam/*/profile1/saves/history/*.run")
w, l = Counter(), Counter()
for f in glob.glob(path):
    r = json.load(open(f, encoding="utf-8"))
    if r.get("ascension") != 10 or len(r.get("players", [])) != 1:
        continue
    char = r["players"][0].get("character", "?")
    (w if r.get("win") else l)[char] += 1
for c in sorted(set(w) | set(l)):
    print(f"{c}: {w[c]}W {l[c]}L ({w[c]/(w[c]+l[c]):.0%})")