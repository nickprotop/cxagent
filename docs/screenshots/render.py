#!/usr/bin/env python3
"""ANSI (from `tmux capture-pane -e`) -> PNG, colours exact.

Matches the existing docs/screenshots: dark ground, DejaVu Sans Mono, one cell per
character so the glyph metrics are the renderer's rather than a terminal's.
"""
import re, sys
from PIL import Image, ImageDraw, ImageFont

FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf"
SIZE = 15
PAD  = 12
BG   = (13, 17, 23)
FG   = (201, 209, 217)

# xterm-256 palette
def palette():
    p = [(0,0,0),(205,49,49),(13,188,121),(229,229,16),(36,114,200),(188,63,188),(17,168,205),(229,229,229),
         (102,102,102),(241,76,76),(35,209,139),(245,245,67),(59,142,234),(214,112,214),(41,184,219),(255,255,255)]
    lv = [0,95,135,175,215,255]
    for r in lv:
        for g in lv:
            for b in lv: p.append((r,g,b))
    for i in range(24):
        v = 8 + i*10; p.append((v,v,v))
    return p
PAL = palette()

CSI = re.compile(r'\x1b\[([0-9;]*)m')

def cells(line):
    """(char, fg, bg, bold) per column."""
    out, fg, bg, bold, i = [], FG, None, False, 0
    for m in CSI.finditer(line):
        for ch in line[i:m.start()]:
            out.append((ch, fg, bg, bold))
        i = m.end()
        codes = [int(c) for c in m.group(1).split(';') if c != ''] or [0]
        j = 0
        while j < len(codes):
            c = codes[j]
            if c == 0: fg, bg, bold = FG, None, False
            elif c == 1: bold = True
            elif c == 22: bold = False
            elif 30 <= c <= 37: fg = PAL[c-30]
            elif 90 <= c <= 97: fg = PAL[c-90+8]
            elif 40 <= c <= 47: bg = PAL[c-40]
            elif 100 <= c <= 107: bg = PAL[c-100+8]
            elif c == 39: fg = FG
            elif c == 49: bg = None
            elif c in (38, 48) and j+1 < len(codes):
                target = 'fg' if c == 38 else 'bg'
                if codes[j+1] == 5 and j+2 < len(codes):
                    col = PAL[codes[j+2] % 256]; j += 2
                elif codes[j+1] == 2 and j+4 < len(codes):
                    col = (codes[j+2], codes[j+3], codes[j+4]); j += 4
                else: col = FG; j += 1
                if target == 'fg': fg = col
                else: bg = col
            j += 1
    for ch in line[i:]:
        out.append((ch, fg, bg, bold))
    return out

def render(src, dst):
    lines = open(src, encoding='utf-8', errors='replace').read().split('\n')
    while lines and not lines[-1].strip(): lines.pop()

    font = ImageFont.truetype(FONT, SIZE)
    bold = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf", SIZE)
    cw = font.getbbox("M")[2] - font.getbbox("M")[0]
    ch = SIZE + 5

    grid = [cells(l) for l in lines]
    cols = max((len(g) for g in grid), default=80)
    img = Image.new("RGB", (cols*cw + PAD*2, len(grid)*ch + PAD*2), BG)
    d = ImageDraw.Draw(img)

    for y, row in enumerate(grid):
        for x, (c, fg, bgc, b) in enumerate(row):
            px, py = PAD + x*cw, PAD + y*ch
            if bgc: d.rectangle([px, py, px+cw, py+ch], fill=bgc)
            if c != ' ': d.text((px, py), c, font=bold if b else font, fill=fg)

    img.save(dst)
    print(f"{dst}  {img.size[0]}x{img.size[1]}  ({cols} cols x {len(grid)} rows)")

if __name__ == "__main__":
    render(sys.argv[1], sys.argv[2])
