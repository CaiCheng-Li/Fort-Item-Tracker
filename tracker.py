"""Fort Item Tracker: logs Fortnite chest rewards with timestamps per session.

  python tracker.py                    track while Fortnite runs (one session per game launch)
  python tracker.py --test samples/*   check detection on screenshots
  python tracker.py --always           track a whole display without waiting for Fortnite
  python tracker.py --sessions         list saved sessions
  python tracker.py --resume [ID]      continue the last (or a given) session, keeping its timer
"""
import argparse
import glob
import signal
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))

from fort_item_tracker.app import LiveTracker, run_test  # noqa: E402
from fort_item_tracker.config import load_config  # noqa: E402
from fort_item_tracker.session import LootLog, fmt_clock, fmt_duration  # noqa: E402


def _interrupt(*_):
    raise KeyboardInterrupt


def list_sessions(cfg) -> int:
    folder = Path(cfg.folder)
    saved = LootLog(folder if folder.is_absolute() else ROOT / folder).saved_sessions()
    if not saved:
        print("No saved sessions yet.")
        return 0
    print(f"{'ID':17} {'started':22} {'last saved':22} {'timer':>8} {'items':>5}  status")
    for st in saved:
        status = "ended" if st.get("ended") else "open (not ended cleanly)"
        print(f"{st['id']:17} {fmt_clock(st['started']):22} {fmt_clock(st['saved_at']):22} "
              f"{fmt_duration(st['elapsed']):>8} {len(st.get('items', [])):>5}  {status}")
    print("\nContinue one with: tracker.py --resume ID   (or just --resume for the most recent)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Logs Fortnite 'YOU FOUND' rewards with timestamps per session.")
    ap.add_argument("--config", type=Path, default=ROOT / "config.toml")
    ap.add_argument("--test", nargs="+", metavar="IMAGE", help="run detection on screenshots and exit")
    ap.add_argument("--verbose", action="store_true", help="with --test, print every OCR line")
    ap.add_argument("--always", action="store_true", help="track a display without waiting for Fortnite")
    ap.add_argument("--display", type=int, default=0, help="display number for --always (default: primary)")
    ap.add_argument("--debug", action="store_true", help="save frames that pass the pre-check to logs/debug")
    ap.add_argument("--resume", nargs="?", const="latest", metavar="ID",
                    help="continue a saved session (default: the most recent) instead of starting a new one")
    ap.add_argument("--sessions", action="store_true", help="list saved sessions and exit")
    ap.add_argument("--overlay", action="store_true", help="show the timer overlay (or set enabled in config.toml)")
    args = ap.parse_args()

    # Closing the console window or Ctrl+Break: stop like Ctrl+C, so the session still gets saved.
    if hasattr(signal, "SIGBREAK"):
        signal.signal(signal.SIGBREAK, _interrupt)

    cfg = load_config(args.config)
    if args.test:
        paths = [Path(p) for pattern in args.test for p in (glob.glob(pattern) or [pattern])]
        return run_test(cfg, ROOT, paths, args.verbose)
    if args.sessions:
        return list_sessions(cfg)
    LiveTracker(cfg, ROOT, always=args.always, display=args.display, debug=args.debug, resume=args.resume,
                overlay=args.overlay or cfg.overlay_enabled).run()
    return 0


if __name__ == "__main__":
    sys.exit(main())
