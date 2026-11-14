"""Find the "YOU FOUND <item>" banner, read the item name, and turn frames into one event per banner."""
import re
import string
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path

import cv2
import numpy as np
from rapidfuzz import fuzz, process

from .icons import IconLibrary, IconMatch, card_crop
from .ocr import Line, WindowsOcr

GATE_HEIGHT = 120  # the pre-check runs on the search area scaled to this height


def letters(text: str) -> str:
    return re.sub(r"[^A-Z0-9]", "", text.upper())


def gate_score(roi, hue=(22, 34)) -> float:
    """Share of bright gold-yellow pixels (the banner frame and item name).

    Costs ~0.1 ms. Banners score 0.02+, scenery around 0.0005, so OCR only runs on
    frames that probably contain one. Hue is OpenCV's 0-180 scale.
    """
    scale = min(1.0, GATE_HEIGHT / roi.shape[0])
    small = cv2.resize(roi, None, fx=scale, fy=scale, interpolation=cv2.INTER_AREA)
    hsv = cv2.cvtColor(small, cv2.COLOR_BGR2HSV)
    mask = cv2.inRange(hsv, (hue[0], 100, 200), (hue[1], 255, 255))
    return float(np.count_nonzero(mask)) / mask.size


@dataclass
class BannerRead:
    name: str | None  # raw OCR text of the item name, if it was readable
    lines: list[Line]
    icon: IconMatch | None = None  # best match for the icon on the banner card
    card: np.ndarray | None = None  # the card image, kept so an unknown icon can be learned


class BannerReader:
    def __init__(self, ocr: WindowsOcr, anchor: str = "YOU FOUND", icons: IconLibrary | None = None):
        self.ocr = ocr
        self.anchor = letters(anchor)
        self.icons = icons

    def _anchor_score(self, text: str) -> float:
        key = letters(text)
        if len(key) < 4:
            return 0
        if len(key) <= len(self.anchor) + 2:
            return fuzz.ratio(self.anchor, key)
        return fuzz.partial_ratio(self.anchor, key)

    def read(self, roi) -> BannerRead | None:
        """None when no banner is visible."""
        lines = self.ocr.read(roi)
        scored = [(self._anchor_score(ln.text), ln) for ln in lines]
        scored = [s for s in scored if s[0] >= 75]
        if not scored:
            return None
        anchor = max(scored, key=lambda s: s[0])[1]
        h = anchor.y1 - anchor.y0
        w = anchor.x1 - anchor.x0
        below = sorted(
            (
                ln
                for ln in lines
                if ln is not anchor
                and anchor.y1 - 0.3 * h <= ln.y0 <= anchor.y1 + 2.5 * h
                and abs(ln.cx - anchor.cx) <= 0.6 * max(w, ln.x1 - ln.x0)
            ),
            key=lambda ln: ln.y0,
        )
        name = " ".join(ln.text for ln in below).strip()
        card = card_crop(roi, anchor)
        icon = self.icons.identify(card) if self.icons else None
        return BannerRead(name if len(letters(name)) >= 2 else None, lines, icon, card)


def clean_name(raw: str) -> str:
    text = re.sub(r"[^A-Za-z0-9 '&.:-]", " ", raw)
    return string.capwords(re.sub(r"\s+", " ", text).strip())


class Namer:
    """Snaps OCR text to a known item name from items.txt when it is close enough."""

    def __init__(self, items_file: Path, snap_score: int = 80):
        self.items_file = items_file
        self.snap_score = snap_score
        self.known: list[str] = []
        self.reload()

    def reload(self):
        if self.items_file.exists():
            lines = self.items_file.read_text(encoding="utf-8").splitlines()
            self.known = [ln.strip() for ln in lines if ln.strip() and not ln.lstrip().startswith("#")]

    def resolve(self, raw: str) -> tuple[str, int | None]:
        """(display name, match score); score is None when the name isn't in items.txt."""
        if self.known:
            hit = process.extractOne(raw, self.known, scorer=fuzz.ratio, processor=letters)
            if hit and hit[1] >= self.snap_score:
                return hit[0], int(hit[1])
        return clean_name(raw), None


@dataclass
class LootEvent:
    wall_time: float  # time.time() when the banner first appeared
    mono_time: float  # time.monotonic() at the same moment
    item: str  # the reward: from the icon when it is recognised, else from the text
    ocr_text: str
    score: int | None  # how well the text matched items.txt; None if it isn't listed
    reads: int
    banner_text: str = ""  # the banner text as resolved against items.txt
    icon_score: float | None = None  # template score of the recognised icon
    learned: str = ""  # path of a newly saved reference icon, if one was learned


@dataclass
class _Active:
    wall_time: float
    mono_time: float
    last_seen: float
    reads: list[str] = field(default_factory=list)
    icons: list[IconMatch] = field(default_factory=list)
    best_unknown: float = 0.0  # best icon score while the icon looked unknown
    card: np.ndarray | None = None

    def icon_consensus(self) -> str | None:
        votes = Counter(m.name for m in self.icons if m.confident)
        return votes.most_common(1)[0][0] if votes else None


class BannerTracker:
    """Collapses the several frames a banner stays on screen into one LootEvent.

    A banner counts once, from when it is first read until it leaves the screen. Back-to-back
    banners (even of the same item) disappear between each other, and that gap starts a new event.
    """

    def __init__(self, namer: Namer, gap_s: float = 1.0, icons: IconLibrary | None = None,
                 learn: bool = True, generic=()):
        self.namer = namer
        self.gap_s = gap_s
        self.icons = icons
        self.learn = learn and icons is not None
        self.generic = [letters(g) for g in generic]
        self.active: _Active | None = None

    def is_generic(self, name: str) -> bool:
        return any(fuzz.ratio(letters(name), g) >= 80 for g in self.generic)

    def _add(self, wall: float, mono: float, read: BannerRead):
        if self.active is None:
            self.active = _Active(wall, mono, mono)
        a = self.active
        a.last_seen = mono
        if read.name:
            a.reads.append(read.name)
        if read.icon:
            a.icons.append(read.icon)
            if not read.icon.confident and read.name and read.card is not None and read.icon.score >= a.best_unknown:
                a.best_unknown = read.icon.score
                a.card = read.card
        elif read.name and read.card is not None and a.card is None:
            a.card = read.card

    def update(self, wall: float, mono: float, read: BannerRead | None,
               visible: bool | None = None) -> list[LootEvent]:
        """`visible`: False when the pre-check sees no banner at all (it has left the screen),
        True when it passes but OCR may have missed the text, None when the frame wasn't checked."""
        if read is not None:
            self._add(wall, mono, read)
            return []
        if self.active is None:
            return []
        # Gone: the banner's gold left the screen. The timeout covers something else gold
        # (an open chest) keeping the pre-check passing after the banner itself has gone.
        if visible is False or mono - self.active.last_seen > self.gap_s:
            return [self._finish()]
        return []

    def flush(self) -> list[LootEvent]:
        return [self._finish()] if self.active else []

    def _finish(self) -> LootEvent:
        a, self.active = self.active, None
        icon_item = a.icon_consensus()
        icon_score = round(max(m.score for m in a.icons if m.name == icon_item), 2) if icon_item else None
        if not a.reads:
            return LootEvent(a.wall_time, a.mono_time, icon_item or "(unreadable)", "", None, 0, "", icon_score)
        resolved = [(self.namer.resolve(r), r) for r in a.reads]
        votes = Counter(name for (name, _), _ in resolved)
        top = max(votes, key=lambda n: (votes[n], max((s or 0) for (m, s), _ in resolved if m == n)))
        (_, score), raw = next(x for x in resolved if x[0][0] == top)
        item, learned = icon_item or top, ""
        if not icon_item and self.learn and a.card is not None and len(a.icons) >= 2:
            # No frame matched a reference image: save the icon so the next one is named by it.
            item = self.icons.unknown_name(top) if self.is_generic(top) else top
            learned = str(self.icons.learn(item, a.card))
        return LootEvent(a.wall_time, a.mono_time, item, raw, score, len(a.reads), top, icon_score, learned)
