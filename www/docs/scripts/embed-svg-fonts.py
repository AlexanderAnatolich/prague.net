#!/usr/bin/env python3
"""Embed the Excalifont handwriting font into exported Excalidraw SVGs.

Excalidraw's SVG export only *names* the font ("Excalifont, Virgil, …"); a browser
that loads the file through <img> cannot fetch external fonts, so the text falls
back to Comic Sans / Chalkboard. This script inlines the Latin subset of Excalifont
(www/docs/fonts/Excalifont-Regular-latin.woff2, ~25 KB) as a data: URI @font-face in
every SVG of the given directory. It is run on the built site, never on the sources.

Usage: embed-svg-fonts.py <dir-with-svgs> [font.woff2]
"""

from __future__ import annotations

import base64
import re
import sys
from pathlib import Path

MARKER = "<!-- excalifont-embedded -->"


def main() -> int:
	if len(sys.argv) < 2:
		print(__doc__, file=sys.stderr)
		return 2
	svg_dir = Path(sys.argv[1])
	font = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).resolve().parent.parent / "fonts" / "Excalifont-Regular-latin.woff2"
	if not font.is_file():
		print(f"font not found: {font}", file=sys.stderr)
		return 1
	b64 = base64.b64encode(font.read_bytes()).decode("ascii")
	style = (
		f"{MARKER}<defs><style>@font-face{{font-family:Excalifont;font-style:normal;font-weight:400;"
		f"src:url(data:font/woff2;base64,{b64}) format('woff2');}}</style></defs>"
	)
	done = 0
	for svg in sorted(svg_dir.glob("*.svg")):
		text = svg.read_text(encoding="utf-8")
		if MARKER in text or "Excalifont" not in text:
			continue
		m = re.search(r"<svg\b[^>]*>", text)
		if not m:
			print(f"skip (no <svg> tag): {svg}", file=sys.stderr)
			continue
		svg.write_text(text[: m.end()] + style + text[m.end():], encoding="utf-8")
		done += 1
	print(f"embed-svg-fonts: embedded Excalifont into {done} file(s) under {svg_dir}")
	return 0


if __name__ == "__main__":
	sys.exit(main())
