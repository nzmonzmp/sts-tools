"""Icône du serveur Discord : un graphique de suivi (barres + courbe montante). Usage : make_icon.py <sortie.png>"""
import math
import sys

from PIL import Image, ImageDraw

S = 2048
img = Image.new("RGBA", (S, S))
px = img.load()
for y in range(S):
    for x in range(S):
        d = min(1, math.hypot(x - S * .5, y - S * .4) / (S * .75))
        c0, c1 = (58, 36, 92), (16, 11, 28)
        px[x, y] = tuple(int(c0[i] + (c1[i] - c0[i]) * d) for i in range(3)) + (255,)
d = ImageDraw.Draw(img)

base = S * .76
# Barres discrètes
for cx, h in [(.27, .20), (.42, .30), (.58, .26), (.73, .42)]:
    w = S * .10
    d.rounded_rectangle([cx * S - w / 2, base - h * S, cx * S + w / 2, base], radius=S * .02, fill=(104, 78, 150))
# Courbe de suivi montante, avec ses points
pts = [(S * x, S * y) for x, y in [(.22, .58), (.40, .46), (.58, .52), (.78, .26)]]
d.line(pts, fill=(240, 190, 80), width=int(S * .045), joint="curve")
for x, y in pts:
    r = S * .045
    d.ellipse([x - r, y - r, x + r, y + r], fill=(252, 220, 130))
# Axe
d.rounded_rectangle([S * .17, base, S * .83, base + S * .03], radius=S * .015, fill=(150, 125, 190))

img.resize((512, 512), Image.LANCZOS).save(sys.argv[1])
