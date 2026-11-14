"""Name the reward from the icon on the banner card, by multi-scale template matching.

Reference images come from the icon folder (transparent PNG/WebP, named after the item)
and from icon_folder/learned, where the tracker saves icons of rewards it had no image for.
"""
import re
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

IMAGE_TYPES = {".png", ".webp", ".jpg", ".jpeg"}
CARD = 80  # banner card is matched at this size (px)
SCALES = np.linspace(0.5, 0.95, 10)  # icon size relative to the card
ACCEPT = 0.6  # template score needed to trust a match
MARGIN = 0.08  # ...and its lead over the next-best item
_DEFAULT_GOLD = np.array([40, 190, 245], np.float32)  # BGR, used when the card colour can't be measured


def name_from_file(path: Path) -> str:
    """Lucky_Locator_-_Item_-_Fortnite.webp -> Lucky Locator; keeps capitals like A$AP."""
    words = re.sub(r"[_\-]+", " ", path.stem).split()
    words = [w for w in words if w.lower() not in ("item", "fortnite")]
    return " ".join(w.capitalize() if w.islower() else w for w in words)


def safe_filename(name: str) -> str:
    return re.sub(r'[\\/:*?"<>|]+', " ", name).strip() or "unnamed"


def card_crop(roi, anchor):
    """The item card above the "YOU FOUND" line."""
    h = anchor.y1 - anchor.y0
    y0, y1 = anchor.y0 - 6.5 * h, anchor.y0 - 0.5 * h
    x0, x1 = anchor.cx - 3 * h, anchor.cx + 3 * h
    y0, y1, x0, x1 = (max(0, int(round(v))) for v in (y0, y1, x0, x1))
    return roi[y0:y1, x0:x1]


@dataclass
class IconMatch:
    name: str
    score: float
    runner_up: float

    @property
    def confident(self) -> bool:
        return self.score >= ACCEPT and self.score - self.runner_up >= MARGIN


def _card_gold(card):
    hsv = cv2.cvtColor(card, cv2.COLOR_BGR2HSV)
    gold = card[(hsv[..., 0] >= 15) & (hsv[..., 0] <= 35) & (hsv[..., 1] > 100) & (hsv[..., 2] > 150)]
    return np.median(gold, axis=0).astype(np.float32) if len(gold) > 20 else _DEFAULT_GOLD


class IconLibrary:
    def __init__(self, folder: Path):
        self.folder = folder
        self.learned_dir = folder / "learned"
        self.reload()

    def reload(self):
        # name -> list of templates; each template is a list of (bgr float32, alpha float32 HxWx1) per scale
        self.refs: dict[str, list] = {}
        self.paths: dict[str, Path] = {}  # first image file for each name (the overlay shows it)
        if self.folder.is_dir():
            for path in sorted(self.folder.iterdir()):
                self._load(path, name_from_file(path))
        if self.learned_dir.is_dir():
            for path in sorted(self.learned_dir.iterdir()):
                self._load(path, path.stem)

    def _load(self, path: Path, name: str):
        if path.suffix.lower() not in IMAGE_TYPES or not path.is_file():
            return
        img = cv2.imdecode(np.fromfile(str(path), np.uint8), cv2.IMREAD_UNCHANGED)
        if img is None:
            return
        if img.ndim == 2:
            img = cv2.cvtColor(img, cv2.COLOR_GRAY2BGRA)
        elif img.shape[2] == 3:
            img = cv2.cvtColor(img, cv2.COLOR_BGR2BGRA)
        scaled = []
        for s in SCALES:
            side = max(8, int(CARD * s))
            h, w = img.shape[:2]
            f = side / max(h, w)
            t = cv2.resize(img, (max(4, int(w * f)), max(4, int(h * f))), interpolation=cv2.INTER_AREA)
            scaled.append((t[..., :3].astype(np.float32), t[..., 3:4].astype(np.float32) / 255))
        self.refs.setdefault(name, []).append(scaled)
        self.paths.setdefault(name, path)

    def path_for(self, name: str) -> str | None:
        path = self.paths.get(name)
        return str(path) if path else None

    def identify(self, card) -> IconMatch | None:
        if not self.refs or card.shape[0] < 16 or card.shape[1] < 16:
            return None
        card = cv2.resize(card, (CARD, CARD), interpolation=cv2.INTER_AREA)
        gold = _card_gold(card)
        scores = {}
        for name, templates in self.refs.items():
            best = -1.0
            for scaled in templates:
                for bgr, alpha in scaled:
                    t = (bgr * alpha + gold * (1 - alpha)).astype(np.uint8)
                    best = max(best, float(cv2.matchTemplate(card, t, cv2.TM_CCOEFF_NORMED).max()))
            scores[name] = best
        ranked = sorted(scores.items(), key=lambda kv: kv[1], reverse=True)
        runner_up = ranked[1][1] if len(ranked) > 1 else 0.0
        return IconMatch(ranked[0][0], ranked[0][1], runner_up)

    def learn(self, name: str, card) -> Path:
        """Save the centre of a banner card as the reference icon for `name`."""
        self.learned_dir.mkdir(parents=True, exist_ok=True)
        h, w = card.shape[:2]
        inner = card[int(h * 0.15) : int(h * 0.85), int(w * 0.15) : int(w * 0.85)]
        path = self.learned_dir / f"{safe_filename(name)}.png"
        cv2.imencode(".png", inner)[1].tofile(str(path))
        self.reload()
        return path

    def unknown_name(self, label: str) -> str:
        n = 1
        while f"Unknown {label} {n}" in self.refs:
            n += 1
        return f"Unknown {label} {n}"
