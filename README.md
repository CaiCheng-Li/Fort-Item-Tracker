# Fort Item Tracker

Watches Fortnite for the **YOU FOUND** banner that appears when you open a chest, works out
which reward it shows (from the item's icon, with the banner text as backup), and logs it with a
timestamp. Each time you launch Fortnite, a session starts with a master timer, and the session
closes when the game exits.

It is a small Windows tray app: one `FortItemTracker.exe`, nothing to install. It only reads what the
game window shows, through Windows Graphics Capture (the same API OBS and the Snipping Tool use), and
never touches the game's memory or files.

## Run it

Double-click `FortItemTracker.exe`. It goes to the tray (by the clock) and waits for
`FortniteClient-Win64-Shipping.exe`; start it before or after the game. Keep the exe in this folder:
it reads `config.toml`, `items.txt` and `assets\` from here and writes to `logs\`.

To build the exe yourself, install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and run
`build.bat`. The exe it makes is self-contained, so the PC that runs it doesn't need .NET. It is
134 MB (48 MB zipped): a compressed exe would be 56 MB but unpacks itself into memory and uses over
twice the RAM, which matters more for something that runs all the time.

Right-click the tray icon for the menu:

| Menu item | What it does |
|---|---|
| (status) | Session timer, items found and the last few finds |
| Save session now | Writes the session to disk now (it also saves every 15 s) |
| Reset timer and counts | Ends and saves the session and starts a new one at 00:00:00 (also **Ctrl+Shift+R**, in game too) |
| End session | Ends it; a new one starts with the next game launch |
| Resume a saved session | Continues an earlier session with its timer and items |
| Show overlay | The timer panel over the game (saved to `config.toml`) |
| Start with Windows | Starts the tracker when you sign in |
| Activity log | What the old console window showed: sessions, finds, problems. Also a double-click on the icon |
| Test screenshots... | Runs detection on banner screenshots (see Checking and tuning) |
| Open logs folder, Open icons folder, Edit settings, Edit item names | Shortcuts to the files below |
| Restart | Restarts the tracker so settings changes apply; the session carries on |

The activity log shows each find as it happens:

```
Session 20261113-201503 started 2026-11-13 8:15:03 PM
   8:16:01 PM  +00:00:58  Extraction Accelerator (banner: Sprite Gizmo)
   8:31:44 PM  +00:16:41  Solid Gold Lil Uzi Vert  [new icon saved: Solid Gold Lil Uzi Vert.png]
```

Exiting from the tray menu, or signing out of Windows, ends and saves the session.

### Resetting the timer and counts

Press **Ctrl+Shift+R** anywhere, including in game, or use Reset in the tray menu. The master timer and
the item counts go back to zero (the overlay drops back to just the timer). Nothing is lost: the
current session is saved and ended, and a new one starts at 00:00:00. The old one stays in `loot.csv`
and `sessions.csv` and can be continued from Resume a saved session. Change or turn off the hotkey
under `[hotkey]` in `config.toml`; if another program already uses the combination, the activity log
says so and the menu item still works.

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

Turn it on with Show overlay in the tray menu, `enabled = true` under `[overlay]` in `config.toml`, or
for one run with `FortItemTracker.exe --overlay`. It sits on top of the game in Windowed Fullscreen,
lets clicks pass through to the game, never takes focus, and hides while Fortnite is minimised or no
session is running. A resumed session shows its earlier finds and carries on its timers. Items without
an icon (nothing in `assets\` yet) show their name instead.

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
15 seconds, from the menu, and when it ends, so even a crash or power cut loses at most 15 seconds.

To carry on a session later with its timer and items intact, pick it under Resume a saved session.
If the game is running it switches right away; otherwise it resumes when Fortnite starts. The master
timer picks up where it stopped (time between runs isn't counted), new items are logged under the
same session ID with timer values that continue on, and the session keeps a single row in
`sessions.csv` with the combined duration and item count. A session that wasn't ended cleanly (the
tracker crashed or was killed) is marked `(not ended cleanly)` and resumes the same way. Sessions
saved by the earlier Python version resume too.

From a command prompt: `FortItemTracker.exe --resume` continues the most recent session,
`--resume 20261113-201503` a specific one, and `--sessions` lists them.

Play in **Windowed Fullscreen** (or Windowed). Exclusive fullscreen can't be captured reliably.

Detection keeps running when Fortnite isn't the focused window, and because it captures the game
window itself, also while another window covers it. It pauses while Fortnite is minimized.

## What gets logged

Both files are in `logs\` and open in Excel (saved sessions are in `logs\sessions\`, see above;
the activity log is `logs\tracker.log`).

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
   after the banner text, and later banners are named by that icon. The activity log shows
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
Tool), put them in a `samples\` folder (it's kept out of git because screenshots show player names),
and pick them with Test screenshots... in the tray menu. The activity log shows, for each one, the
reward it found, the icon score and the next-best score, the banner text and every line OCR read.
The same from a command prompt (add `| more` so the prompt waits for the output):

```
FortItemTracker.exe --test samples\*.webp --verbose | more
```

If a banner is missed in game, start it with `--debug`: every frame that passes the pre-check is saved
to `logs\debug\` along with what OCR read, so you can see which stage failed.

Settings are in `config.toml` (Restart from the tray menu after changing them):

| Setting | When to change it |
|---|---|
| `roi` | The banner sits outside the search area (e.g. a different HUD scale). Fractions of the game window. |
| `source` | `auto` captures the Fortnite window and switches to capturing the screen if the window comes out black. Set `monitor` to always capture the screen. |
| `gate_threshold` | A test shows a banner screenshot scoring below it: lower it. False OCR runs are cheap, missed banners aren't. |
| `gate_hue` | A reward banner comes in a colour other than gold. Widen the range, or the pre-check will skip it. |
| `poll_hz` | Lower to 2 for even less CPU. The banner stays up ~2-3 s, so 2 checks/s still catches it. |
| `anchor`, `ocr_language` | Your game isn't in English. |
| `only_when_focused` | Set `true` to pause capture whenever Fortnite isn't the active window. |
| `learn_icons` | Set `false` to stop saving icons of unrecognised rewards. |
| `generic_names` | Another banner text turns out to be shared by several items, like "Sprite Gizmo". |

`--always` tracks a whole display without waiting for Fortnite, which is useful for testing against a
recording played full screen (`--display 2` picks another monitor).

## How it works

1. **Capture.** Windows Graphics Capture records the Fortnite window. Frames stay on the graphics
   card: 4 times a second the banner search area of the newest one is copied out and halved there
   until it is just over 120 px tall, and only that small copy comes back to memory. On Windows 11
   24H2 and later the capture itself is limited to 8 frames a second, and Windows 11 doesn't draw its
   yellow capture border around the game. If the window comes out black for 15 s, the tracker
   captures the screen where the game is instead and says so in the activity log.
2. **Pre-check.** Scale the small copy to 120 px tall and measure the share of bright gold-yellow
   pixels (the banner's frame and item name). Banners score 0.02 or more, normal gameplay around
   0.0005-0.003, and the threshold is 0.005. Gold things in view (an open chest) can pass too, which
   only costs an extra read.
3. **Read the banner.** Only when the pre-check passes: fetch the full-size search area from the
   graphics card, run the built-in Windows OCR, find the line
   that reads "YOU FOUND" (fuzzy, so "VOU FOUND" counts), and take the text below it. The card above
   that line is cut out, scaled to 80 px, and matched against every reference image laid on the card's
   gold at ten sizes (normalised template matching). A match counts when it scores at least 0.6 and
   leads the next-best image by 0.08. The real Extraction Accelerator banners score 0.85 with the
   runner-up at 0.59. A read takes about 50 ms.
4. **One event per banner.** Reads are collected while the banner is up; the icon (or, failing that,
   the text) is decided by vote. A banner counts once, and the event is written when it leaves the
   screen (the first check where the pre-check fails). Back-to-back banners, even for the same item,
   disappear between each other, so each is logged separately. If something else gold keeps the
   pre-check passing, the banner counts as gone after 1 s without a "YOU FOUND" read.
5. **Sessions.** While Fortnite runs, the tracker checks every 2 s whether it has exited; while
   waiting for it, it looks for it every 5 s. The session ends 10 s after the game exits, and is
   saved to disk throughout so it can be resumed.

Measured on this PC for a minute each, with Fortnite at 2560x1440 and the overlay off:

| | CPU (share of one core) | Memory (Task Manager) |
|---|---|---|
| Fortnite running, no banner | 1.3% | 38 MB |
| Waiting for Fortnite | 0.1% | 11 MB |
| Python version, for comparison (1920x1080 stand-in game) | 6.6-9.6% | 52 MB |

Reading a banner (OCR and icon matching) takes about 40 ms and happens a few times a second only
while a banner is on screen. Capture pauses while Fortnite is minimized.

## Limits

- A gap between back-to-back banners shorter than one check (0.25 s at `poll_hz = 4`) can be missed,
  merging the two into one. Raise `poll_hz` if that happens.
- When capture falls back to the screen (`source = "monitor"`, or `auto` after the game window came
  out black), a window covering the middle of the game hides the banner from the tracker.
- The overlay can't show over Fortnite in exclusive fullscreen (use Windowed Fullscreen, which the
  tracker needs anyway).
- On Windows 10, Windows draws a yellow border around the captured window and doesn't let apps turn
  it off.
- Icon matching was checked on the two real Extraction Accelerator banners and on test banners made
  by pasting each asset image onto them. Your first real banner of each other gizmo is the true test;
  if one is misidentified, save a screenshot to `samples\` and test it.
- The pre-check is tuned on two gold banners. If other rewards use a different banner
  colour, add a screenshot to `samples\`, test it, and adjust `gate_hue` if it's skipped.

## Files

```
FortItemTracker.exe   the app (built by build.bat, not in git)
build.bat             builds FortItemTracker.exe into this folder
config.toml           settings
items.txt             known reward names (used when the icon isn't recognised)
src/
  Program.cs          entry point, command-line options
  TrayApp.cs          tray icon and menu
  Tracker.cs          main loop: process watching, capture, sessions
  WgcCapture.cs       Windows Graphics Capture and the Direct3D copy of the search area
  Banner.cs           pre-check, banner parsing, one-event-per-banner logic
  Icons.cs            reward identification from the banner icon, icon learning
  Ocr.cs              Windows OCR, fuzzy text matching
  Sessions.cs         master timer, saved sessions, CSV logs
  Overlay.cs          on-screen timer overlay (click-through layered window)
  Hotkeys.cs          system-wide hotkeys (Ctrl+Shift+R reset)
  LogWindow.cs        activity log window
  TestMode.cs         screenshot tests
  Imaging.cs          image loading (PNG/JPEG/WebP), resizing, HSV
  Config.cs, Toml.cs  config.toml
  Native.cs           Win32 calls
assets/               reward images the banner icon is matched against
  learned/            icons saved automatically from banners (not in git)
samples/              your screenshots for testing (not in git)
logs/                 loot.csv, sessions.csv, saved sessions, tracker.log (not in git)
```

## License

The code is under the MIT License (see `LICENSE`). The item images in `assets\` are Fortnite artwork
owned by Epic Games and are not covered by it; Fort Item Tracker is not affiliated with Epic Games.
