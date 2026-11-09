#!/usr/bin/env python3
"""Regenerates the app icons and iOS splash screens from the SVG sources (needs rsvg-convert).

Run from the repo root: python3 tools/branding/generate.py
Writes to src/Dialed.Web/wwwroot/icons and prints the <link> tags for index.html (kept in sync by PwaTests).
"""
import subprocess, pathlib, tempfile

root = pathlib.Path(__file__).resolve().parents[2]
out = root / "src/Dialed.Web/wwwroot/icons"
src = pathlib.Path(__file__).parent
splash_dir = out / "splash"
splash_dir.mkdir(parents=True, exist_ok=True)

LIGHT_BG, DARK_BG = "#F4ECE0", "#1E1713"

def render(svg, target, w, h=None):
    subprocess.run(["rsvg-convert", "-w", str(w), "-h", str(h or w), "-o", str(target), str(svg)], check=True)

render(src / "icon.svg", out / "icon-192.png", 192)
render(src / "icon.svg", out / "icon-512.png", 512)
render(src / "icon-maskable.svg", out / "icon-maskable-512.png", 512)
render(src / "icon-maskable.svg", out / "apple-touch-icon.png", 180)  # iOS rounds the corners itself
render(src / "icon.svg", root / "src/Dialed.Web/wwwroot/favicon.png", 64)

# (css width, css height, device pixel ratio): portrait iPhone and iPad sizes still in use
DEVICES = [
    (440, 956, 3), (402, 874, 3), (430, 932, 3), (393, 852, 3), (390, 844, 3), (375, 812, 3), (414, 896, 3), (414, 896, 2),
    (375, 667, 2), (414, 736, 3),
    (810, 1080, 2), (820, 1180, 2), (834, 1194, 2), (1032, 1376, 2), (1024, 1366, 2), (834, 1112, 2), (744, 1133, 2),
]

tags = []
for w, h, ratio in DEVICES:
    pw, ph = w * ratio, h * ratio
    for mode, bg, mark in (("light", LIGHT_BG, "icon.svg"), ("dark", DARK_BG, "icon.svg")):
        size = round(min(pw, ph) * 0.28)
        svg = f'''<svg xmlns="http://www.w3.org/2000/svg" width="{pw}" height="{ph}" viewBox="0 0 {pw} {ph}">
  <rect width="{pw}" height="{ph}" fill="{bg}"/>
  <svg x="{(pw - size) / 2}" y="{(ph - size) / 2 - ph * 0.04}" width="{size}" height="{size}" viewBox="0 0 512 512">
    <clipPath id="r"><rect width="512" height="512" rx="112"/></clipPath>
    <g clip-path="url(#r)">{(src / mark).read_text().split("</rect>")[0].split("?>")[-1].split("<svg")[1].split(">", 1)[1].rsplit("</svg>", 1)[0]}</g>
  </svg>
</svg>'''
        with tempfile.NamedTemporaryFile("w", suffix=".svg", delete=False) as f:
            f.write(svg)
        name = f"splash-{mode}-{pw}x{ph}.png"
        render(f.name, splash_dir / name, pw, ph)
        pathlib.Path(f.name).unlink()
        scheme = f"(prefers-color-scheme: {mode})"
        tags.append(f'    <link rel="apple-touch-startup-image" href="icons/splash/{name}" media="(device-width: {w}px) and (device-height: {h}px) and (-webkit-device-pixel-ratio: {ratio}) and (orientation: portrait) and {scheme}" />')

print("\n".join(tags))
