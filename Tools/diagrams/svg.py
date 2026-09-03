"""Tiny SVG drawing kit for the architecture diagrams in Content.Server/AiAgent/README.md.

Hand-placed boxes and arrows, one palette, one font stack. The point is that the output is a
plain .svg file GitHub renders inline, readable on light and dark backgrounds (the canvas paints
its own white background) and editable by re-running gen.py rather than by hand.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from html import escape

FONT = "-apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, 'Noto Sans', sans-serif"
MONO = "ui-monospace, SFMono-Regular, Menlo, Consolas, 'Liberation Mono', monospace"

INK = "#1f2328"
MUTED = "#59636e"
LINE = "#6e7781"
BORDER = "#8c959f"
LANE_BORDER = "#d0d7de"

TINTS = {
    "blue": "#eaf2fb",
    "green": "#eaf6ec",
    "warm": "#fbf3e6",
    "purple": "#f3eefa",
    "grey": "#f1f3f5",
    "red": "#fbeaea",
    "teal": "#e6f5f5",
}
ACCENTS = {
    "blue": "#0969da",
    "green": "#1a7f37",
    "warm": "#bc4c00",
    "purple": "#8250df",
    "grey": "#57606a",
    "red": "#cf222e",
    "teal": "#1b7c83",
}


def _w(text: str, size: float, bold: bool = False) -> float:
    """Rough text width for layout checks; 0.56em per glyph, a bit more for bold."""
    return len(text) * size * (0.58 if bold else 0.53)


@dataclass
class Node:
    x: float
    y: float
    w: float
    h: float
    lines: list[str]

    @property
    def cx(self):
        return self.x + self.w / 2

    @property
    def cy(self):
        return self.y + self.h / 2

    def top(self, dx=0):
        return (self.cx + dx, self.y)

    def bottom(self, dx=0):
        return (self.cx + dx, self.y + self.h)

    def left(self, dy=0):
        return (self.x, self.cy + dy)

    def right(self, dy=0):
        return (self.x + self.w, self.cy + dy)


@dataclass
class Svg:
    w: int
    h: int
    parts: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)

    # ----------------------------------------------------------------- primitives
    def lane(self, x, y, w, h, title, tint="grey", subtitle=None):
        fill = TINTS[tint]
        acc = ACCENTS[tint]
        self.parts.append(
            f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="12" fill="{fill}" '
            f'stroke="{LANE_BORDER}" stroke-width="1"/>'
        )
        sub = (f'<tspan dx="12" font-size="12" font-weight="400" fill="{MUTED}" '
               f'letter-spacing="0">{escape(subtitle)}</tspan>') if subtitle else ""
        self.parts.append(
            f'<text x="{x + 14}" y="{y + 22}" font-family="{FONT}" font-size="13" font-weight="700" '
            f'fill="{acc}" letter-spacing="0.4">{escape(title)}{sub}</text>'
        )

    def node(self, x, y, w, h, *lines, accent=None, mono_from=1, fill="#ffffff", dashed=False):
        """A box. First line bold; the rest muted. Lines from `mono_from` may use `code`."""
        stroke = ACCENTS[accent] if accent else BORDER
        dash = ' stroke-dasharray="5 4"' if dashed else ""
        self.parts.append(
            f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="8" fill="{fill}" '
            f'stroke="{stroke}" stroke-width="{1.6 if accent else 1.2}"{dash}/>'
        )
        if accent:
            self.parts.append(
                f'<rect x="{x}" y="{y}" width="5" height="{h}" rx="2.5" fill="{stroke}"/>'
            )
        n = len(lines)
        line_h = 16
        total = 17 + (n - 1) * line_h
        y0 = y + (h - total) / 2 + 13
        for i, line in enumerate(lines):
            bold = i == 0
            size = 13 if bold else 12
            fill_t = INK if bold else MUTED
            weight = ' font-weight="700"' if bold else ""
            est = _w(line, size, bold)
            if est > w - 14:
                self.warnings.append(f"line may overflow ({est:.0f} > {w - 14}): {line!r}")
            self.parts.append(
                f'<text x="{x + w / 2}" y="{y0 + i * line_h}" text-anchor="middle" '
                f'font-family="{FONT}" font-size="{size}"{weight} fill="{fill_t}">'
                f"{self._rich(line)}</text>"
            )
        return Node(x, y, w, h, list(lines))

    def _rich(self, line: str) -> str:
        """Backticks are allowed in source text for readability; they are dropped on output so
        every renderer lays the line out identically."""
        return escape(line.replace("`", ""))

    def text(self, x, y, s, size=12, color=MUTED, anchor="start", bold=False, mono=False):
        weight = ' font-weight="700"' if bold else ""
        fam = MONO if mono else FONT
        self.parts.append(
            f'<text x="{x}" y="{y}" text-anchor="{anchor}" font-family="{fam}" font-size="{size}"'
            f'{weight} fill="{color}">{self._rich(s)}</text>'
        )

    def arrow(self, *pts, label=None, dashed=False, color=LINE, label_at=0.5, label_dy=-6,
              head=True, tail=False, label_anchor="middle", label_dx=0):
        d = "M " + " L ".join(f"{px:.1f} {py:.1f}" for px, py in pts)
        dash = ' stroke-dasharray="6 4"' if dashed else ""
        mk = ' marker-end="url(#head)"' if head else ""
        mk += ' marker-start="url(#tailhead)"' if tail else ""
        self.parts.append(
            f'<path d="{d}" fill="none" stroke="{color}" stroke-width="1.6"{dash}{mk}/>'
        )
        if label:
            # place the label on the segment that contains the requested fraction of total length
            segs = list(zip(pts, pts[1:]))
            lens = [((b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2) ** 0.5 for a, b in segs]
            total = sum(lens) or 1
            target = label_at * total
            acc = 0
            for (a, b), ln in zip(segs, lens):
                if acc + ln >= target:
                    t = (target - acc) / ln if ln else 0
                    lx = a[0] + (b[0] - a[0]) * t + label_dx
                    ly = a[1] + (b[1] - a[1]) * t + label_dy
                    break
                acc += ln
            common = (f'x="{lx:.1f}" y="{ly:.1f}" text-anchor="{label_anchor}" '
                      f'font-family="{FONT}" font-size="11.5"')
            self.parts.append(
                f'<text {common} fill="none" stroke="#ffffff" stroke-width="5" '
                f'stroke-linejoin="round">{self._rich(label)}</text>'
            )
            self.parts.append(f'<text {common} fill="{MUTED}">{self._rich(label)}</text>')

    def note(self, x, y, w, lines, size=11.5):
        """Free-standing explanatory text, left-aligned, no box."""
        for i, line in enumerate(lines):
            self.parts.append(
                f'<text x="{x}" y="{y + i * (size + 4)}" font-family="{FONT}" font-size="{size}" '
                f'fill="{MUTED}">{self._rich(line)}</text>'
            )

    # ----------------------------------------------------------------- output
    def render(self) -> str:
        defs = (
            '<defs>'
            '<marker id="head" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" '
            f'orient="auto-start-reverse"><path d="M 0 0 L 10 5 L 0 10 z" fill="{LINE}"/></marker>'
            '<marker id="tailhead" viewBox="0 0 10 10" refX="1" refY="5" markerWidth="8" markerHeight="8" '
            f'orient="auto-start-reverse"><path d="M 10 0 L 0 5 L 10 10 z" fill="{LINE}"/></marker>'
            '</defs>'
        )
        body = "\n".join(self.parts)
        return (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{self.w}" height="{self.h}" '
            f'viewBox="0 0 {self.w} {self.h}" font-family="{FONT}">\n{defs}\n'
            f'<rect width="{self.w}" height="{self.h}" rx="14" fill="#ffffff"/>\n{body}\n</svg>\n'
        )

    def save(self, path):
        with open(path, "w", encoding="utf-8") as f:
            f.write(self.render())
        for w in self.warnings:
            print(f"  warn {path}: {w}")
