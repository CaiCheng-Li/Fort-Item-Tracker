"""Fortnite process watching, the per-session master timer, saved sessions and the CSV logs."""
import csv
import json
import os
import time
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path

import psutil

from .detector import LootEvent


def fmt_duration(seconds: float) -> str:
    s = int(max(0, seconds))
    return f"{s // 3600:02d}:{s % 3600 // 60:02d}:{s % 60:02d}"


def fmt_time(wall: float) -> str:
    """12-hour clock time, e.g. 8:16:01 PM."""
    return datetime.fromtimestamp(wall).strftime("%I:%M:%S %p").lstrip("0")


def fmt_clock(wall: float) -> str:
    """Date and 12-hour time, e.g. 2026-11-13 8:16:01 PM (Excel reads this as a date-time)."""
    return f"{datetime.fromtimestamp(wall):%Y-%m-%d} {fmt_time(wall)}"


class ProcessWatcher:
    def __init__(self, names):
        self.names = {n.lower() for n in names}
        self.pids: set[int] = set()

    def poll(self) -> set[int]:
        alive = {p for p in self.pids if psutil.pid_exists(p)}
        if alive:
            self.pids = alive
            return alive
        self.pids = {
            p.pid for p in psutil.process_iter(["name"]) if (p.info["name"] or "").lower() in self.names
        }
        return self.pids


@dataclass
class Session:
    id: str
    wall_start: float  # when the session was first started
    mono_start: float  # time.monotonic() when this run of it started
    prior: float = 0.0  # master-timer seconds from earlier runs (a resumed session)
    items: list[dict] = field(default_factory=list)  # {"item", "found_at", "t"} with t = master timer
    runs: list[list[float]] = field(default_factory=list)  # [start, end] wall times of each run

    def at(self, mono: float) -> float:
        """Master timer value at a time.monotonic() moment of this run."""
        return self.prior + mono - self.mono_start

    def elapsed(self) -> float:
        return self.at(time.monotonic())

    def summary(self) -> str:
        counts = Counter(i["item"] for i in self.items)
        return "; ".join(f"{name} x{n}" for name, n in counts.most_common())


class LootLog:
    LOOT_HEADER = [
        "session_id", "found_at", "session_time", "item", "banner_text", "ocr_text", "match_score", "icon_score", "reads"
    ]
    SESSION_HEADER = ["session_id", "start", "end", "duration", "items_found", "summary"]

    def __init__(self, folder: Path):
        self.loot_csv = folder / "loot.csv"
        self.sessions_csv = folder / "sessions.csv"
        self.saved_dir = folder / "sessions"
        self.saved_dir.mkdir(parents=True, exist_ok=True)

    # ---- CSV ----------------------------------------------------------------------------

    @staticmethod
    def _keep_if_old_format(path: Path, header: list[str]):
        if path.exists() and path.stat().st_size:
            with path.open(encoding="utf-8-sig", newline="") as f:
                old_header = next(csv.reader(f), [])
            if old_header != header:  # columns changed: keep the old file aside
                path.rename(path.with_name(f"{path.stem}_old_{int(time.time())}{path.suffix}"))

    def _append(self, path: Path, header: list[str], row: list):
        self._keep_if_old_format(path, header)
        new = not path.exists() or path.stat().st_size == 0
        with path.open("a", newline="", encoding="utf-8-sig") as f:
            w = csv.writer(f)
            if new:
                w.writerow(header)
            w.writerow(row)

    def _upsert_session_row(self, session: Session):
        """One row per session in sessions.csv, replaced when a resumed session ends again."""
        path = self.sessions_csv
        self._keep_if_old_format(path, self.SESSION_HEADER)
        rows = []
        if path.exists():
            with path.open(encoding="utf-8-sig", newline="") as f:
                rows = [r for r in list(csv.reader(f))[1:] if r and r[0] != session.id]
        end = session.runs[-1][1] if session.runs else time.time()
        rows.append([
            session.id,
            fmt_clock(session.wall_start),
            fmt_clock(end),
            fmt_duration(session.elapsed()),
            len(session.items),
            session.summary(),
        ])
        tmp = path.with_suffix(".tmp")
        with tmp.open("w", newline="", encoding="utf-8-sig") as f:
            w = csv.writer(f)
            w.writerow(self.SESSION_HEADER)
            w.writerows(rows)
        os.replace(tmp, path)

    # ---- saved session state ------------------------------------------------------------

    def _state_path(self, session_id: str) -> Path:
        return self.saved_dir / f"{session_id}.json"

    def save(self, session: Session, ended: bool = False):
        """Write the session (timer, items, runs) to logs/sessions/<id>.json."""
        now = time.time()
        if session.runs:
            session.runs[-1][1] = now
        state = {
            "id": session.id,
            "started": session.wall_start,
            "elapsed": round(session.elapsed(), 1),
            "ended": ended,
            "saved_at": now,
            "runs": session.runs,
            "items": session.items,
        }
        path = self._state_path(session.id)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(state, indent=1), encoding="utf-8")
        os.replace(tmp, path)  # atomic, so a crash never leaves a half-written file

    def saved_sessions(self) -> list[dict]:
        """Saved sessions, most recently saved first."""
        out = []
        for path in self.saved_dir.glob("*.json"):
            try:
                out.append(json.loads(path.read_text(encoding="utf-8")))
            except (OSError, ValueError):
                continue
        return sorted(out, key=lambda s: s.get("saved_at", 0), reverse=True)

    # ---- session lifecycle --------------------------------------------------------------

    def start_session(self) -> Session:
        wall = time.time()
        base = sid = datetime.fromtimestamp(wall).strftime("%Y%m%d-%H%M%S")
        n = 2
        while self._state_path(sid).exists():  # two sessions in the same second
            sid, n = f"{base}-{n}", n + 1
        s = Session(sid, wall, time.monotonic())
        s.runs.append([wall, wall])
        self.save(s)
        return s

    def resume_session(self, session_id: str | None = None) -> Session:
        """Continue a saved session: its master timer carries on from where it stopped."""
        saved = self.saved_sessions()
        if session_id:
            saved = [s for s in saved if s["id"] == session_id]
        if not saved:
            raise LookupError(f"no saved session {session_id}" if session_id else "no saved sessions")
        st = saved[0]
        wall = time.time()
        s = Session(st["id"], st["started"], time.monotonic(), float(st["elapsed"]), st.get("items", []),
                    st.get("runs", []))
        s.runs.append([wall, wall])
        self.save(s)
        return s

    def add(self, session: Session, ev: LootEvent):
        t = session.at(ev.mono_time)
        session.items.append({"item": ev.item, "found_at": fmt_clock(ev.wall_time), "t": round(t, 1)})
        self._append(
            self.loot_csv,
            self.LOOT_HEADER,
            [
                session.id,
                fmt_clock(ev.wall_time),
                fmt_duration(t),
                ev.item,
                ev.banner_text,
                ev.ocr_text,
                "" if ev.score is None else ev.score,
                "" if ev.icon_score is None else ev.icon_score,
                ev.reads,
            ],
        )
        self.save(session)

    def end_session(self, session: Session) -> str:
        self.save(session, ended=True)
        self._upsert_session_row(session)
        return session.summary()
