# Fort Item Tracker

Watches your screen for the **YOU FOUND** banner that appears when you open a chest, works out
which reward it shows (from the item's icon, with the banner text as backup), and logs it with a
timestamp. Each time you launch Fortnite, a session starts with a master timer, and the session
closes when the game exits.

It only reads screen pixels, the same way OBS or Discord screen share does. It never touches the
game's memory or files.

## Run it

Double-click `run.bat`, or from this folder:

```
.venv\Scripts\python.exe tracker.py
```

Start it before or after Fortnite; it waits for `FortniteClient-Win64-Shipping.exe` and starts a
session automatically. While it runs, the console shows the session timer and each item as it's found:

```
Session 20261113-201503 started 2026-11-13 8:15:03 PM
   8:16:01 PM  +00:00:58  Extraction Accelerator (banner: Sprite Gizmo)
   8:31:44 PM  +00:16:41  Solid Gold Lil Uzi Vert  [new icon saved: Solid Gold Lil Uzi Vert.png]
Session 00:17:02 | 2 found | last: Solid Gold Lil Uzi Vert (00:00:21 ago)
```

Keys while it runs: **S** saves the session now, **R** resets the timer and counts, **E** ends the
session (a new one starts with the next game launch). Stop with Ctrl+C or close the window; the
session is saved either way.

### Resetting the timer and counts

Press **Ctrl+Shift+R** anywhere, including in game, or **R** in the tracker's window. The master
timer and the item counts go back to zero (the overlay drops back to just the timer) without
restarting anything. Nothing is lost: the current session is saved and ended, and a new session
starts at 00:00:00. The old one stays in `loot.csv` and `sessions.csv` and can still be continued with
`--resume <its id>`. Change or turn off the hotkey under `[hotkey]` in `config.toml`; if another
program already uses the combination, the tracker says so at startup and **R** still works.

## Overlay

An optional panel in the top-left corner of the game shows the session timer. After your first find
it adds a **Since last item** timer and one row per item found this session: its icon, how many
you've found (`x3`), and when you last found one. The most recently found item is at the top, so a
repeat find moves its row back up and bumps the count:

```
 00:17:02
 Since last item  00:00:21
 [icon] x3  8:31:44 PM
 [icon] x1  8:24:10 PM
```

Turn it on with `enabled = true` under `[overlay]` in `config.toml`, or for one run with
`run.bat --overlay` (also works with `resume.bat --overlay`). It sits on top of the game in Windowed
Fullscreen, lets clicks pass through to the game, never takes focus, and hides while Fortnite is
minimised or no session is running. A resumed session shows its earlier finds and carries on its
timers. Items without an icon (nothing in `assets\` yet) show their name instead.

| `[overlay]` setting | What it does |
|---|---|
| `x`, `y` | Offset from the game window's top-left corner in pixels (default 16, 16). Keep it out of the middle of the screen, where it would cover the banner. |
| `scale` | Size; `1.5` is 50% bigger. |
| `opacity` | Panel background opacity, 0.2-1.0. |
| `items` | How many different items to list (most recently found first); `0` shows only the timers. |
| `show_names` | `true` also writes each item's name next to its icon. |

## Saving and resuming sessions

Every session is saved to `logs\sessions\<session id>.json`: its master timer, every item with the
timer value it was found at, and the start and end of each run. It is saved after every item, every
15 seconds, when you press S, and when it ends, so even a crash or power cut loses at most 15 seconds.

To carry on a session later with its timer and items intact, double-click `resume.bat`, or:

```
.venv\Scripts\python.exe tracker.py --sessions             list saved sessions
.venv\Scripts\python.exe tracker.py --resume               continue the most recent one
.venv\Scripts\python.exe tracker.py --resume 20261113-201503   continue a specific one
```

The master timer picks up where it stopped (time between runs isn't counted), new items are logged
under the same session ID with timer values that continue on, and the session keeps a single row in
`sessions.csv` with the combined duration and item count. Only the first session of that run is
resumed; later game launches start new sessions as usual. A session that wasn't ended cleanly (the
tracker crashed or was killed) is listed as `open (not ended cleanly)` and resumes the same way.

Play in **Windowed Fullscreen** (or Windowed). Exclusive fullscreen can't be captured reliably.

Detection keeps running when Fortnite isn't the focused window, for example while you use another
monitor. It reads what is on screen where the game is, so it only misses banners while another
window covers the middle of the game or Fortnite is minimized.

## What gets logged

Both files are in `logs\` and open in Excel (saved sessions are in `logs\sessions\`, see above).

- `loot.csv`: one row per banner, with `session_id, found_at, session_time, item, banner_text, ocr_text,
  match_score, icon_score, reads`. `session_time` is the master timer when the banner appeared.
  `item` is the reward. `banner_text` is what the banner said (e.g. `Sprite Gizmo`). `icon_score`
  (0-1) is set when the item was named by its icon; `match_score` (0-100) is how well the text matched
  `items.txt`, blank if it isn't listed.
- `sessions.csv`: one row per session, with start, end, duration (master timer), item count and a
  summary such as `Lucky Locator x3; Extraction Accelerator x1`. A resumed session's row is updated
  in place.

If the columns change in an update, the old `loot.csv` is renamed to `loot_old_<number>.csv` and kept.

## How rewards are named

1. **By icon.** The icon on the banner's gold card is compared with every image in `assets\` and
   `assets\learned\`. A confident match names the reward after the image file, whatever the banner
   text says. This is how the five gizmos are told apart: every gizmo banner just says SPRITE GIZMO.
2. **By text.** When no image matches, the banner text is used. Text close to a name in `items.txt`
   is logged with that spelling (so `SPRITE-GIZMO` becomes `Sprite Gizmo`); anything else is logged as
   read and marked `(not in items.txt)`.
3. **Learning.** When a reward has no matching image, its icon is saved to `assets\learned\`, named
   after the banner text, and later banners are named by that icon. The console shows
   `[new icon saved: ...]`. A gizmo whose icon isn't known is saved as `Unknown Sprite Gizmo 1.png`
   (and logged as that) because its banner text doesn't say which one it is: **rename the file** to the
   real name, and it is used from the next session on. Renaming any image renames that reward in
   future logs, so it also fixes misspelled names.

`assets\` currently holds the five gizmos and the Pirate Token. The weekly gold cosmetics, Pirate Shop
Access and the week 3-5 rewards get learned the first time you find them. To add an image yourself,
put a picture of the item (a transparent PNG/WebP works best) in `assets\` and name it after the item.
Underscores, dashes and the words "Item" and "Fortnite" in the file name are ignored, so
`Lucky_Locator_-_Item_-_Fortnite.webp` is "Lucky Locator".

## Checking and tuning

Test detection on screenshots. Take full-screen screenshots of the banner (Win+Shift+S or the Snipping
Tool) and put them in a `samples\` folder; it's kept out of git because screenshots show player names.

```
.venv\Scripts\python.exe tracker.py --test samples\*.webp samples\*.png --verbose
```

For each screenshot it prints the reward it found, the icon score and the next-best score, and
the banner text. If a banner is missed in game, run with `--debug`: every frame that passes the
pre-check is saved to `logs\debug\` along with what OCR read, so you can see which stage failed.

Settings are in `config.toml`:

| Setting | When to change it |
|---|---|
| `roi` | The banner sits outside the search area (e.g. a different HUD scale). Fractions of the game window. |
| `gate_threshold` | `--test` shows a banner screenshot scoring below it: lower it. False OCR runs are cheap, missed banners aren't. |
| `gate_hue` | A reward banner comes in a colour other than gold. Widen the range, or the pre-check will skip it. |
| `poll_hz` | Lower to 2 for even less CPU. The banner stays up ~2-3 s, so 2 checks/s still catches it. |
| `anchor`, `ocr_language` | Your game isn't in English. |
| `only_when_focused` | Set `true` to pause capture whenever Fortnite isn't the active window. |
| `learn_icons` | Set `false` to stop saving icons of unrecognised rewards. |
| `generic_names` | Another banner text turns out to be shared by several items, like "Sprite Gizmo". |

`--always` tracks a display without waiting for Fortnite, which is useful for testing against a
recording played full screen (`--display 2` picks another monitor).

## How it works

1. **Capture.** 4 times a second, grab only the banner search area of the Fortnite window with DXGI
   desktop duplication (`dxcam`, ~2 ms). `mss` is the fallback.
2. **Pre-check.** Shrink the crop to 120 px tall and measure the share of bright gold-yellow pixels
   (the banner's frame and item name). About 0.1 ms. Banners score 0.02 or more, normal gameplay
   around 0.0005-0.003, and the threshold is 0.005. Gold things in view (an open chest) can pass too,
   which only costs an extra read.
3. **Read the banner.** Only when the pre-check passes: run the built-in Windows OCR (~15 ms), find
   the line that reads "YOU FOUND" (fuzzy, so "VOU FOUND" counts), and take the text below it. The card
   above that line is cut out, scaled to 80 px, and matched against every reference image laid on the
   card's gold at ten sizes (normalised template matching, ~15 ms). A match counts when it scores at
   least 0.6 and leads the next-best image by 0.08. The real Extraction Accelerator banners score 0.85
   with the runner-up at 0.59.
4. **One event per banner.** Reads are collected while the banner is up; the icon (or, failing that,
   the text) is decided by vote. A banner counts once, and the event is written when it leaves the
   screen (the first check where the pre-check fails). Back-to-back banners, even for the same item,
   disappear between each other, so each is logged separately. If something else gold keeps the
   pre-check passing, the banner counts as gone after 1 s without a "YOU FOUND" read.
5. **Sessions.** The Fortnite process is checked every 2 s. The session ends 10 s after it exits,
   and is saved to disk throughout so it can be resumed.

Measured on this PC with Fortnite running: about 3-6% of one CPU core (under 0.5% of the whole CPU)
and 90-100 MB of RAM, with or without the overlay (it redraws once a second, ~8 ms). Capture pauses
while Fortnite is minimized.

## Limits

- A gap between back-to-back banners shorter than one check (0.25 s at `poll_hz = 4`) can be missed,
  merging the two into one. Raise `poll_hz` if that happens.
- A window covering the middle of the game hides the banner from the tracker.
- The overlay can't show over Fortnite in exclusive fullscreen (use Windowed Fullscreen, which the
  tracker needs anyway).
- Icon matching was checked on the two real Extraction Accelerator banners and on test banners made
  by pasting each asset image onto them. Your first real banner of each other gizmo is the true test;
  if one is misidentified, save a screenshot to `samples\` and run `--test`.
- The pre-check is tuned on two gold banners. If other rewards use a different banner
  colour, add a screenshot to `samples\`, run `--test`, and adjust `gate_hue` if it's skipped.

## Files

```
tracker.py            entry point (live mode, --test)
config.toml           settings
items.txt             known reward names (used when the icon isn't recognised)
run.bat               one-click start, sets up .venv on first run
resume.bat            one-click resume of the most recent session
fort_item_tracker/
  app.py              main loop and test mode
  capture.py          dxcam / mss screen grabs
  detector.py         pre-check, banner parsing, one-event-per-banner logic
  icons.py            reward identification from the banner icon, icon learning
  hotkeys.py          system-wide hotkeys (Ctrl+Shift+R reset)
  ocr.py              Windows OCR wrapper
  overlay.py          on-screen timer overlay (click-through layered window)
  session.py          process watcher, master timer, saved sessions, CSV logs
  winutil.py          finding the Fortnite window and its monitor
assets/               reward images the banner icon is matched against
  learned/            icons saved automatically from banners (not in git)
samples/              your screenshots for --test (not in git)
logs/                 loot.csv, sessions.csv, saved sessions (not in git)
```
