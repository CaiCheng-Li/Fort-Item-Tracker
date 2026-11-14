"""Live tracking loop and the screenshot test mode."""
import msvcrt
import time
from datetime import datetime
from pathlib import Path

import cv2
import numpy as np

from . import winutil
from .capture import DxcamCapture, MssCapture, make_capture
from .config import Config
from .detector import BannerReader, BannerTracker, LootEvent, Namer, gate_score
from .icons import IconLibrary
from .hotkeys import Hotkeys
from .ocr import WindowsOcr
from .overlay import Overlay, OverlayItem, OverlayState
from .session import LootLog, ProcessWatcher, Session, fmt_clock, fmt_duration, fmt_time

AUTOSAVE_S = 15.0  # seconds between automatic saves of the running session


def roi_rect(window, roi) -> tuple[int, int, int, int]:
    l, t, r, b = window
    w, h = r - l, b - t
    return (l + int(w * roi[0]), t + int(h * roi[1]), l + int(w * roi[2]), t + int(h * roi[3]))


def crop_roi(img, roi):
    h, w = img.shape[:2]
    return img[int(h * roi[1]) : int(h * roi[3]), int(w * roi[0]) : int(w * roi[2])]


def make_icons(cfg: Config, root: Path) -> IconLibrary:
    folder = Path(cfg.icon_folder)
    icons = IconLibrary(folder if folder.is_absolute() else root / folder)
    if not icons.refs:
        print(f"icons: no reference images in {icons.folder}; rewards will be named from the banner text")
    return icons


def tally_items(items: list[dict], limit: int) -> list[tuple[str, int, str]]:
    """(name, count, last found_at) per item, the most recently found item first."""
    tally: dict[str, list] = {}
    for i in items:
        entry = tally.pop(i["item"], [0, ""])  # re-inserting keeps dict order = order of latest find
        entry[0] += 1
        entry[1] = i["found_at"]
        tally[i["item"]] = entry
    latest = list(tally.items())[::-1]
    return [(name, count, found) for name, (count, found) in latest[: max(0, limit)]]


def describe(ev: LootEvent) -> str:
    text = ev.item
    if ev.banner_text and ev.banner_text != ev.item:
        text += f" (banner: {ev.banner_text})"
    elif ev.icon_score is None and ev.score is None and ev.reads:
        text += " (not in items.txt)"
    if ev.learned:
        text += f"  [new icon saved: {Path(ev.learned).name}]"
    return text


class LiveTracker:
    def __init__(self, cfg: Config, root: Path, always: bool = False, display: int = 0, debug: bool = False,
                 resume: str | None = None, overlay: bool = False):
        winutil.enable_dpi_awareness()
        self.cfg = cfg
        out = Path(cfg.folder)
        out = out if out.is_absolute() else root / out
        self.log = LootLog(out)
        self.ocr = WindowsOcr(cfg.ocr_language)
        self.icons = make_icons(cfg, root)
        self.reader = BannerReader(self.ocr, cfg.anchor, self.icons)
        self.namer = Namer(root / "items.txt", cfg.snap_score)
        self.tracker = BannerTracker(self.namer, cfg.banner_gap_s, self.icons, cfg.learn_icons, cfg.generic_names)
        self.capture = make_capture(cfg.backend)
        self.watcher = ProcessWatcher(cfg.process_names)
        self.always = always
        self.display = self._pick_display(display) if always else None
        self.debug_dir = out / "debug" if debug else None
        if self.debug_dir:
            self.debug_dir.mkdir(parents=True, exist_ok=True)
        self.session: Session | None = None
        self.hwnd = None
        self.missing_since: float | None = None
        self.capture_failures = 0
        self.last_error = ""
        self.debug_saved = 0
        self.last_debug = 0.0
        self.status_len = 0
        self.resume = resume  # session id, "latest", or None
        self.next_autosave = 0.0
        self.hold_new = False  # set by E: don't start a new session until the game restarts (or N)
        if resume:  # fail now, not when Fortnite starts
            self._find_resumable(resume)
        self.overlay = (
            Overlay(cfg.overlay_scale, cfg.overlay_opacity, cfg.overlay_items, cfg.overlay_show_names)
            if overlay else None
        )
        self.hotkeys = Hotkeys({"reset": cfg.hotkey_reset})

    @staticmethod
    def _pick_display(number: int):
        monitors = winutil.list_monitors()
        if number:
            for device, rect, _ in monitors:
                if device.upper().endswith(f"DISPLAY{number}"):
                    return device, rect
            raise SystemExit(f"No display {number}. Found: {', '.join(d for d, _, _ in monitors)}")
        return next((d, r) for d, r, primary in monitors if primary)

    # ---- output -------------------------------------------------------------------------

    def _print(self, text: str):
        print("\r" + " " * self.status_len + "\r" + text, flush=True)
        self.status_len = 0

    def _status(self, state: str):
        if self.session is None and self.hold_new:
            text = "Session ended. N = new session now, otherwise one starts with the next game"
        elif self.session is None:
            text = "Waiting for Fortnite to start..."
        else:
            s = self.session
            text = f"Session {fmt_duration(s.elapsed())} | {len(s.items)} found"
            if s.items:
                last = s.items[-1]
                text += f" | last: {last['item']} ({fmt_duration(s.elapsed() - last['t'])} ago)"
            if state:
                text += f" | {state}"
        print("\r" + text.ljust(self.status_len), end="", flush=True)
        self.status_len = len(text)

    def _log_event(self, ev: LootEvent):
        self.log.add(self.session, ev)
        clock = fmt_time(ev.wall_time).rjust(11)
        self._print(f"  {clock}  +{fmt_duration(self.session.at(ev.mono_time))}  {describe(ev)}")
        self._push_overlay()

    def _overlay_origin(self) -> tuple[int, int] | None:
        """Top-left corner of the game window (or the display with --always), None to hide."""
        if self.always:
            rect = self.display[1]
        elif winutil.is_window(self.hwnd) and not winutil.is_minimized(self.hwnd):
            rect = winutil.client_rect(self.hwnd)
        else:
            return None
        return (rect[0], rect[1]) if rect else None

    def _push_overlay(self):
        if self.overlay is None:
            return
        s, origin = self.session, self._overlay_origin()
        if s is None or origin is None:
            self.overlay.update(OverlayState())
            return
        items = tuple(
            OverlayItem(name, found_at.split(" ", 1)[-1], self.icons.path_for(name), count)
            for name, count, found_at in tally_items(s.items, self.cfg.overlay_items)
        )
        self.overlay.update(OverlayState(
            True, origin[0] + self.cfg.overlay_x, origin[1] + self.cfg.overlay_y, s.prior, s.mono_start,
            s.items[-1]["t"] if s.items else None, items,
        ))

    # ---- session ------------------------------------------------------------------------

    def _find_resumable(self, resume: str) -> str:
        saved = self.log.saved_sessions()
        if resume != "latest":
            saved = [st for st in saved if st["id"] == resume]
        if not saved:
            raise SystemExit(f"No saved session {resume!r}. See: tracker.py --sessions")
        return saved[0]["id"]

    def _start_session(self):
        self.namer.reload()
        self.icons.reload()
        self.hold_new = False
        if self.resume:
            self.session = self.log.resume_session(self._find_resumable(self.resume))
            self.resume = None  # later sessions in this run are new ones
            s = self.session
            self._print(f"Session {s.id} resumed at {fmt_duration(s.prior)} with {len(s.items)} item(s)")
        else:
            self.session = self.log.start_session()
            self._print(f"Session {self.session.id} started {fmt_clock(time.time())}")
        self.next_autosave = time.monotonic() + AUTOSAVE_S
        self._push_overlay()

    def _save_session(self, announce: bool = False):
        if self.session is None:
            if announce:
                self._print("No session running, nothing to save.")
            return
        self.log.save(self.session)
        self.next_autosave = time.monotonic() + AUTOSAVE_S
        if announce:
            self._print(f"Session {self.session.id} saved at {fmt_duration(self.session.elapsed())}. "
                        f"Continue it later with: tracker.py --resume {self.session.id}")

    def _handle_keys(self):
        """Console keys: S = save now, E = end the session, R (or N) = reset. Plus the global hotkeys."""
        for name in self.hotkeys.pressed():
            if name == "reset":
                self._reset()
        while msvcrt.kbhit():
            key = msvcrt.getwch().lower()
            if key == "s":
                self._save_session(announce=True)
            elif key == "e" and self.session is not None:
                self._end_session()
                self.hold_new = True
            elif key in ("r", "n"):
                self._reset()

    def _reset(self):
        """Timer and counts back to zero: the current session is saved and ended, a new one starts."""
        if self.session is not None:
            self._print("Reset: saving the current session and starting a new one.")
        self._end_session()
        self._start_session()

    def _end_session(self):
        if self.session is None:
            return
        for ev in self.tracker.flush():
            self._log_event(ev)
        summary = self.log.end_session(self.session)
        s = self.session
        self._print(f"Session {s.id} ended at {fmt_duration(s.elapsed())}: {len(s.items)} item(s). {summary}")
        self._print(f"  Saved. Continue it later with: tracker.py --resume {s.id}")
        self.session = None
        self.hwnd = None
        self._push_overlay()

    def _check_process(self, mono: float):
        if self.always:
            if self.session is None and not self.hold_new:
                self._start_session()
            return
        pids = self.watcher.poll()
        if pids:
            self.missing_since = None
            self.hwnd = winutil.find_main_window(pids)
            if self.session is None and not self.hold_new:
                self._start_session()
        elif self.session is None:
            self.hold_new = False  # game closed: the next launch starts a new session
        else:
            if self.missing_since is None:
                self.missing_since = mono
            elif mono - self.missing_since >= self.cfg.end_grace_s:
                self._end_session()
        self._push_overlay()  # follows the game window if it moves, hides while minimised

    # ---- capture ------------------------------------------------------------------------

    def _target(self):
        """(device, monitor rect, capture rect), or a string saying why capture is paused."""
        if self.always:
            device, monitor = self.display
            window = monitor
        else:
            if not winutil.is_window(self.hwnd):
                return "no Fortnite window"
            if winutil.is_minimized(self.hwnd):
                return "paused: Fortnite minimized"
            if self.cfg.only_when_focused and not winutil.is_foreground(self.hwnd):
                return "paused: Fortnite not focused"
            window = winutil.client_rect(self.hwnd)
            if window is None:
                return "no Fortnite window"
            device, monitor = winutil.monitor_for_window(self.hwnd)
        l, t, r, b = roi_rect(window, self.cfg.roi)
        region = (max(l, monitor[0]), max(t, monitor[1]), min(r, monitor[2]), min(b, monitor[3]))
        if region[2] - region[0] < 50 or region[3] - region[1] < 50:
            return "paused: Fortnite window off-screen"
        return device, monitor, region

    def _grab(self, device, monitor, region):
        try:
            frame = self.capture.grab(device, monitor, region)
            self.capture_failures = 0
            return frame
        except Exception as e:
            self.capture_failures += 1
            if isinstance(self.capture, DxcamCapture):
                self.capture.close()
                if self.capture_failures >= 3:
                    self._print(f"dxcam keeps failing ({e}); switching to mss")
                    self.capture = MssCapture()
            elif str(e) != self.last_error:
                self._print(f"capture error: {e}")
            self.last_error = str(e)
            return None

    def _save_debug(self, roi, score: float, lines):
        now = time.monotonic()
        if self.debug_saved >= 1000 or now - self.last_debug < 0.5:
            return
        self.last_debug = now
        self.debug_saved += 1
        stamp = datetime.now().strftime("%H%M%S_%f")[:-3]
        cv2.imwrite(str(self.debug_dir / f"{stamp}_{score:.4f}.png"), roi)
        with (self.debug_dir / "ocr.txt").open("a", encoding="utf-8") as f:
            f.write(f"{stamp} gate={score:.4f} lines={[ln.text for ln in lines]}\n")

    # ---- main loop ----------------------------------------------------------------------

    def run(self):
        interval = 1.0 / max(0.5, self.cfg.poll_hz)
        where = f"display {self.display[0]}" if self.always else "the Fortnite window"
        print(f"Capture: {self.capture.name}, {self.cfg.poll_hz:g} checks/s on {where}")
        print(f"Logging to {self.log.loot_csv.parent}")
        if self.resume:
            print(f"Will resume session {self._find_resumable(self.resume)} when tracking starts.")
        if self.overlay:
            print("Overlay: on (top-left of the game window)")
            self.overlay.start()
        self.hotkeys.start()
        for err in self.hotkeys.errors:
            print(f"Hotkey not available: {err}")
        if "reset" in self.hotkeys.registered:
            print(f"Reset hotkey (works in game): {self.hotkeys.registered['reset'].upper()}")
        print("Keys: S = save, R = reset timer and counts, E = end session, Ctrl+C = stop (session is saved).\n")
        next_process_check = 0.0
        next_status = 0.0
        try:
            while True:
                mono = time.monotonic()
                self._handle_keys()
                if self.session is not None and mono >= self.next_autosave:
                    self._save_session()
                if mono >= next_process_check:
                    next_process_check = mono + 2.0
                    self._check_process(mono)
                state = ""
                if self.session is not None:
                    read = None
                    target = self._target()
                    if isinstance(target, str):
                        state = target
                    else:
                        roi = self._grab(*target)
                        if roi is not None:
                            score = gate_score(roi, self.cfg.gate_hue)
                            if score >= self.cfg.gate_threshold:
                                read = self.reader.read(roi)
                                if self.debug_dir:
                                    self._save_debug(roi, score, read.lines if read else self.ocr.read(roi))
                    for ev in self.tracker.update(time.time(), mono, read):
                        self._log_event(ev)
                if mono >= next_status:
                    next_status = mono + 1.0
                    self._status(state)
                wait = interval if self.session is not None and not state else 0.5
                time.sleep(max(0.0, wait - (time.monotonic() - mono)))
        except KeyboardInterrupt:
            pass
        finally:
            self._end_session()
            if self.overlay:
                self.overlay.stop()
            self.hotkeys.stop()
            self.capture.close()
            self.ocr.close()
            self._print("Stopped.")


def run_test(cfg: Config, root: Path, paths: list[Path], verbose: bool = False) -> int:
    """Run the detector on screenshots (whole image = game window)."""
    ocr = WindowsOcr(cfg.ocr_language)
    reader = BannerReader(ocr, cfg.anchor, make_icons(cfg, root))
    namer = Namer(root / "items.txt", cfg.snap_score)
    for path in paths:
        img = cv2.imdecode(np.fromfile(str(path), np.uint8), cv2.IMREAD_COLOR)
        if img is None:
            print(f"{path}: can't read image")
            continue
        roi = crop_roi(img, cfg.roi)
        t0 = time.perf_counter()
        score = gate_score(roi, cfg.gate_hue)
        t1 = time.perf_counter()
        read = reader.read(roi)
        t2 = time.perf_counter()
        gate = "pass" if score >= cfg.gate_threshold else "below threshold"
        print(f"{path.name}: pre-check {score:.4f} ({gate}, {(t1 - t0) * 1000:.2f} ms), read {(t2 - t1) * 1000:.1f} ms")
        if read is None:
            print("   no banner")
        else:
            if read.icon and read.icon.confident:
                print(f"   FOUND: {read.icon.name}  (icon {read.icon.score:.2f}, next best {read.icon.runner_up:.2f})")
            elif read.icon:
                print(f"   icon not recognised (best {read.icon.name} {read.icon.score:.2f}, next {read.icon.runner_up:.2f})")
            if read.name is None:
                print("   banner text unreadable")
            else:
                item, match = namer.resolve(read.name)
                known = f"matches items.txt ({match})" if match is not None else "not in items.txt"
                print(f"   banner text: {item}  [OCR '{read.name}', {known}]")
        if verbose:
            for ln in read.lines if read else ocr.read(roi):
                print(f"      ocr line: {ln.text!r} at ({ln.x0:.0f},{ln.y0:.0f})-({ln.x1:.0f},{ln.y1:.0f})")
    ocr.close()
    return 0
