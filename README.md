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
| (status) | Session timer, items found and the last few finds, with their icons |
| Save session now | Writes the session to disk now (it also saves every 15 s) |
| Pause session | Stops the session timer and tracking until you resume it (also **Ctrl+Shift+P**, in game too) |
| Reset timer and counts | Ends and saves the session and starts a new one at 00:00:00 (also **Ctrl+Shift+R**, in game too) |
| End session | Ends it; a new one starts with the next game launch |
| Resume a saved session | Continues an earlier session with its timer and items |
| Show overlay | The timer panel over the game (saved to `config.toml`) |
| Pin overlay in place | Untick it to move the overlay (the same as clicking its pin, see Overlay) |
| Reset overlay position | Moves the overlay back to its default spot, top left of the game |
| Start with Windows | Starts the tracker when you sign in |
| Activity log | What the old console window showed: sessions, finds, problems, colour-coded, under the live session timer. Also a double-click on the icon |
| Test screenshots... | Runs detection on banner screenshots (see Checking and tuning) |
| Save a problem report | Puts a zip on your desktop to send to whoever helps you (see If something looks wrong) |
| Open logs folder, Open icons folder, Edit settings, Edit item names | Shortcuts to the files below |
| Restart | Restarts the tracker so settings changes apply; the session carries on |

The tray icon sparkles briefly each time something is found. The activity log shows each find as it
happens, in gold:

```
Session 20261113-201503 started 2026-11-13 8:15:03 PM
Watching Fortnite at 2560x1440, full screen, on DISPLAY1 (2560x1440, 100% scaling, HDR off). Window capture is working.
   8:16:01 PM  +00:00:58  Extraction Accelerator (banner: Sprite Gizmo, icon 0.88)
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

### Pausing a session

Press **Ctrl+Shift+P** anywhere, including in game, or use Pause session in the tray menu, to take a
break without ending the session. The master timer stops, nothing is tracked, and the overlay shows
**PAUSED** with the timer held. Press it again, or use Resume paused session, and the timer carries on
from where it stopped, with the same session and items. A paused session isn't ended when Fortnite
closes, so you can quit the game and come back to it later. Each stretch between pauses is a separate
run in the saved session, and `sessions.csv` counts only the time it wasn't paused. Change or turn off
the hotkey with `pause` under `[hotkey]` in `config.toml`.

## If something looks wrong

1. **Check it sees the game.** Open the Activity log (double-click the tray icon). Soon after Fortnite
   starts it shows a line like `Watching Fortnite at 2560x1440, full screen, on DISPLAY1 (...). Window
   capture is working.` No such line means the tracker can't see the game yet.
2. **Read the warnings.** Problems show up as yellow `Warning:` lines that say what to change. The
   usual one: window capture shows black while the game is on screen, so the tracker captures the
   screen instead (it then misses banners while a window covers the game) and tries the window again
   later. Playing in **Windowed Fullscreen** (Window Mode in Fortnite's video settings) usually fixes it.
3. **Check the names.** Each find shows how well the icon matched: `Extraction Accelerator (banner:
   Sprite Gizmo, icon 0.88)`. A gizmo whose icon it can't place is logged as `Sprite Gizmo (icon unsure,
   closest: Portable Extractor 0.55)`, in yellow, instead of a guess. Every find's item card is saved in
   `logs\cards\`, so you can see what it was.
4. **Send a report.** Still wrong? Pick **Save a problem report** in the tray menu. It saves a zip to your
   desktop with the activity log, `loot.csv`, settings, learned icons, the last few sessions' item cards,
   a list of the icon images and whether Windows can read them, and a description of your displays
   (size, scaling, HDR) and graphics card. It holds no full screenshots, and your Windows user folder
   is written as `%USERPROFILE%`. Send that file.

## Overlay

A panel in the top-left corner of the game shows the session timer. After your first find
it adds a **Since last item** timer and one row per item found this session: its icon, how many
you've found (`×3`), and when you last found one. The most recently found item is at the top, so a
repeat find moves its row back up and bumps the count:

```
 ✦ SESSION
 00:17:02
 SINCE LAST ITEM  00:00:21
 ─────────────────────
 [icon] ×3  8:31:44 PM
 [icon] ×1  8:24:10 PM
```

It moves rather than jumps: it fades in when the game appears, the digits roll over each second, and
a find sends a sweep of light across the panel, sparkles out of the item's icon and pops its count,
while the other rows slide down to make room (or let a repeat find's row slide back to the top).

It is on by default. Turn it off with Show overlay in the tray menu or `enabled = false` under
`[overlay]` in `config.toml` (`FortItemTracker.exe --overlay` then shows it for one run). It sits on
top of the game in Windowed Fullscreen, lets clicks pass through to the game, never takes focus, and
hides while Fortnite is minimised or no session is running. While a session is paused its label reads
PAUSED and the timers stand still. A resumed session shows its earlier finds
and carries on its timers. Items without an icon (nothing in `assets\` yet) show their name instead.

### Moving it

Point at the overlay (in the lobby, the map, or any time the game shows a mouse cursor) and a pin
appears in its top-right corner. Click the pin to unpin the overlay: it glows gold and you can drag it
anywhere over the game. Near an edge it snaps to a small gap from it. Click the pin again to pin it
there (or untick Pin overlay in place in the tray menu). It also pins itself when you go back to
playing and the game hides the cursor.

Only the pin catches clicks while the overlay is pinned; everything else goes to the game. Unpinned, the
panel itself catches clicks so you can drag it.

The spot is saved to `config.toml` as `anchor`, `x` and `y`, measured from the game window's nearest
corner, so the overlay stays in that corner if the window size changes, and grows away from it as
items are added: in a bottom corner it grows upwards.

To put it back where it started (16, 16 px from the top-left corner), click the circular arrow left of
the pin while the overlay is unpinned, or use Reset overlay position in the tray menu. The overlay glides back and the default spot is saved to `config.toml`. This works even
while the overlay is turned off, so it appears in the default spot when you turn it back on.

| `[overlay]` setting | What it does |
|---|---|
| `anchor` | The game window corner the overlay is placed from: `top-left` (default), `top-right`, `bottom-left` or `bottom-right`. Dragging the overlay sets it. |
| `x`, `y` | Distance from that corner in pixels (default 16, 16). Keep the overlay out of the middle of the screen, where it would cover the banner. |
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

The CSV files are in `logs\` and open in Excel (saved sessions are in `logs\sessions\`, see above;
the activity log is `logs\tracker.log`).

- `loot.csv`: one row per banner, with `session_id, found_at, session_time, item, banner_text, ocr_text,
  match_score, icon_score, reads`. `session_time` is the master timer when the banner appeared.
  `item` is the reward. `banner_text` is what the banner said (e.g. `Sprite Gizmo`). `icon_score`
  (0-1) is set when the item was named by its icon; `match_score` (0-100) is how well the text matched
  `items.txt`, blank if it isn't listed.
- `sessions.csv`: one row per session, with start, end, duration (master timer), item count and a
  summary such as `Lucky Locator x3; Extraction Accelerator x1`. A resumed session's row is updated
  in place.
- `cards\<session id>\`: the middle of each find's item card as a PNG named by time and item, e.g.
  `201601 Extraction Accelerator.png`, to check a name later. It is cut the same way as a learned icon,
  so copying one into `assets\learned\` (named after the item) teaches the tracker that icon.

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
   `[new icon saved: ...]`. A learned icon is cut from your own screen, so it also looks a bit like
   other items' cards there: in tests a different gizmo scored up to 0.68 against one, the same item
   0.97 or more. So a learned icon needs 0.85 to count (a shipped image needs 0.6). Renaming any image
   renames that reward in future logs, so it also fixes misspelled names.
4. **Shared banner text.** Every gizmo banner says SPRITE GIZMO (`generic_names` in `config.toml`), so
   a gizmo icon that matches no image is not learned: it is almost always a known gizmo that looks
   different on your screen, and a learned copy would then be used for the other gizmos too. It is
   logged as `Sprite Gizmo (icon unsure, closest: Portable Extractor 0.55)`. If a new gizmo comes out,
   copy its card from `logs\cards\` into `assets\learned\` and name the file after the gizmo.
   Version 2.1.0 still learned these as `Unknown Sprite Gizmo 1.png` and so on: rename such a file to
   the gizmo it shows (check `logs\cards\`), or delete it.

`assets\` currently holds the five gizmos and the Pirate Token. The weekly gold cosmetics, Pirate Shop
Access and the week 3-5 rewards get learned the first time you find them. To add an image yourself,
put a picture of the item (a transparent PNG works best) in `assets\` and name it after the item.
Underscores, dashes and the words "Item" and "Fortnite" in the file name are ignored, so
`Lucky_Locator_-_Item_-_Fortnite.png` is "Lucky Locator". JPEG works too. WebP only works when
Windows has the "WebP Image Extensions" app, which some Windows installs leave out; the activity log
warns about any image it can't read, and those items then can't be named by their icon.

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
| `source` | `auto` captures the Fortnite window, and the screen while the window comes out black but the screen shows the game. Set `monitor` to always capture the screen. |
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
   yellow capture border around the game. If the window comes out black for 15 s while Fortnite is in
   front, the tracker looks at the screen there once: if that is black too (a loading screen, a fade),
   nothing is wrong. If it shows the game, window capture isn't working, so the tracker captures the
   screen instead, says so in the activity log, and tries the window again after 1 minute, then 2, 4,
   8 and every 16 minutes, going back to it as soon as it works.
2. **Pre-check.** Scale the small copy to 120 px tall and measure the share of bright gold-yellow
   pixels (the banner's frame and item name). Banners score 0.02 or more, normal gameplay around
   0.0005-0.003, and the threshold is 0.005. Gold things in view (an open chest) can pass too, which
   only costs an extra read.
3. **Read the banner.** Only when the pre-check passes: fetch the full-size search area from the
   graphics card, run the built-in Windows OCR, find the line
   that reads "YOU FOUND" (fuzzy, so "VOU FOUND" counts), and take the text below it. Windows OCR
   sometimes misses that line at one size and reads it at another, and can't read text much under
   12 px, so after a miss the next check reads the area at 80%, then 60%, 150% and 200% size (each
   only while that gives OCR 240-1000 px of height), and keeps the size that works. The gold card
   above the line is found from its own pixels, so its box fits any resolution, HUD scale or text size
   (if no gold card is found, the box is sized from the text). It is cut out, scaled to 80 px, and
   matched against every reference image laid on the card's gold at ten sizes (normalised template
   matching). A match counts when it scores at least 0.6 (0.85 for a learned icon) and leads the
   next-best image by 0.08. The real Extraction Accelerator banners score 0.83-0.87 with the runner-up
   at 0.57-0.60, and the same with the text drawn at half or 1.3 times its size, the screenshot at
   0.5-2 times its resolution, or both. A read takes about 55 ms.
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
- When capture falls back to the screen (`source = "monitor"`, or `auto` while window capture shows
  black), a window covering the middle of the game hides the banner from the tracker.
- A gizmo that looks different on your screen than in `assets\` (a colour setting or capture method)
  is logged as `Sprite Gizmo (icon unsure)` rather than by name. Its card in
  `logs\cards\` shows which it was; a problem report helps tune the matching.
- The overlay can't show over Fortnite in exclusive fullscreen (use Windowed Fullscreen, which the
  tracker needs anyway).
- On Windows 10, Windows draws a yellow border around the captured window and doesn't let apps turn
  it off.
- Icon matching was checked on two Extraction Accelerator screenshots, and in play on one PC
  (2560x1440, window capture), where all five gizmos matched at 0.78-0.92. Other setups can score
  lower; if one is misidentified, save a screenshot to `samples\` and test it.
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
  Overlay.cs          on-screen timer overlay (layered window: click-through except its pin, draggable)
  Hotkeys.cs          system-wide hotkeys (Ctrl+Shift+R reset, Ctrl+Shift+P pause)
  LogWindow.cs        activity log window
  Theme.cs            colours, shapes and the tray menu's look
  Motion.cs           springs and easing for the animations
  TestMode.cs         screenshot tests
  Diagnose.cs         the setup line (game size, display, scaling, HDR) and the problem report
  Imaging.cs          image loading (Windows codecs), resizing, HSV
  Config.cs, Toml.cs  config.toml
  Native.cs           Win32 calls
assets/               reward images the banner icon is matched against
  learned/            icons saved automatically from banners (not in git)
samples/              your screenshots for testing (not in git)
logs/                 loot.csv, sessions.csv, saved sessions, item cards, tracker.log (not in git)
```

## License

The code is under the MIT License (see `LICENSE`). The item images in `assets\` are Fortnite artwork
owned by Epic Games and are not covered by it; Fort Item Tracker is not affiliated with Epic Games.
