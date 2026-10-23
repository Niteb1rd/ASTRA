# IRIS

IRIS – Companion App for Star Citizen (formerly ASTRA). First feature: **Social Overlay**.

Windows overlay that brackets the nameplates of players you care about, in colors you choose,
and summarizes their orgs in a side panel. See the requirements doc for the full specification.

## Status

Phase 2: the overlay shell. IRIS runs as a tray app with a Social Overlay config window, and now has the overlay window itself (brackets and the right-hand panel), driven by test data only: there is no screen capture or OCR yet, so the real overlay stays empty until Phase 3 feeds it detections. The Settings tab has an Overlay section and a "Show test overlay (15 s)" button to try it. See "Phase 2: the overlay" below.

- `src/Iris.Core`: config file (TOML) read, write and validate; color and bracket rules; right-panel org line (increment 1).
- `src/Iris.Core/Rsi`: reads robertsspaceindustries.com. Pure parsers for the player organizations page, the org page and the
  org members API; an HTTP client that returns Found / NotFound / Failed (never exceptions), allows one request per second
  app-wide (slower while RSI asks us to wait, see below), only talks to robertsspaceindustries.com, and logs every request (URL, time, outcome).
- `src/Iris.Core/Cache`: SQLite lookup cache (players, their orgs, orgs and their members, "no such citizen" notes), with the
  source of every entry, a 30 or 7 day refresh interval, "Refresh Cache" (list entries only), the hotkey lookup and the
  org-list-member lookup.
- `src/Iris.Core/Detection`: the exclusion list (hand-editable text file plus shipped defaults), the nameplate name filter, and the
  tracker that adds names seen on two separate nameplates at once (increment 3).
- `src/Iris.Core/Storage`: `DataPaths` (the `Data` folder and the files in it) and `SocialListStore` (loads the list file; saves it
  crash-safely through `.tmp`, keeping one `.bak`). `src/Iris.Core/Config/RowValidator.cs` checks a typed handle or SID for the
  config window: format, duplicates, cache first, then one RSI request (increment 4).
- `src/Iris.Core/Settings`: `AppSettings` and `AppSettingsToml` (`Data\settings.toml`). `src/Iris.Core/Config` also holds
  `SocialConfigMerger`, `ConfigImporter` (the three import modes, export) and `HostedConfigClient` (increment 5).
- `src/Iris.Core/Overlay`: the overlay model and its math, without any windowing code: `OverlayModel` (brackets and panel entries), `OverlayModelBuilder`
  (detections + cached orgs to model), `PanelLayout` (panel rectangles), `GameWindowRules` (the borderless decision), `StarCitizenDisplaySettings` (the game's WindowMode setting), `OverlayPalette`, `TestOverlayBuilder` (Phase 2).
- `src/Iris.App`: the Windows app (WPF): tray icon, the config window with the Players, Orgs, Org-Attribute and Settings tabs, the overlay window
  (`Views/OverlayWindow`, `Views/OverlayCanvas`), the game window tracker and the overlay controller (`Services`).
  It has no automated tests yet; everything testable lives in `Iris.Core`.
- `tests/Iris.Core.Tests`: automated tests for all of the above. Tests that need real RSI pages are skipped until you capture them.
- `tools/Iris.CaptureFixtures`: saves a few real RSI pages for those tests.

The page parsers were written without access to RSI's real pages. Their CSS selectors and label texts are guesses, collected as
named constants at the top of each parser. Capture the fixtures and run the tests to find out which guesses are right.

## Player handles and the exclusion list

A player handle is 4 to 25 characters: letters, digits, `-` and `_` only. Anything else read from a nameplate is ignored
(and a player list entry that breaks the rule is rejected). The limits are fixed in the program (`RsiNames.MinHandleLength` and
`MaxHandleLength`).

The exclusion list is a plain text file, `exclusions.txt`: names that appear on screen but are not players (stars, planets, stations...).
One name per line, upper/lower case does not matter, blank lines are ignored, and a line starting with `#` is a comment.
You can edit it by hand while IRIS runs; changes are picked up automatically. The first time IRIS runs it creates the file
from the shipped list, `src/Iris.Core/Detection/exclusions.default.txt`. In the app it lives in the `Data` folder (see "Run the app").

- A nameplate whose name is on the list is never matched or looked up, except when the name is also on your own player list
  (your player list wins). Org-list members and hotkey lookups follow the exclusion list with no such exception.
- IRIS also learns: a player cannot be in two places at once, so a name seen on two separate nameplates in 3 different frames
  (counted across sessions) is added to the end of the file, after a comment line `# auto-added <date> UTC: <reason>`.
  Delete those two lines if the name was added by mistake, or put the name on your player list.
  IRIS only ever appends to the file; it never rewrites your own lines.
- The frame counts are kept in the lookup cache database (cache schema version 2; an older cache file is upgraded automatically).

## Colors, brackets and the No Match rule

A detected player gets a color in this order: the **player list** (brackets `{ }`), then the **org list** (`[ ]`, lowest priority number wins),
then the **org-attribute rules** (`< >`, lowest priority number wins; ties go to the rule listed first).

The last org-attribute rule is permanent: **No Match**. A detected player who matches nothing above gets its color, also with the `< >` brackets,
so every detected nameplate is bracketed and you can see which players the overlay found. The right panel then shows the orgs line as usual with no org
marked as the reason. Rules of this kind in the config file:

```
[[org_rules]]
kind = "no_match"
priority = 10
color = "Blue"
```

- Every list has exactly one. A file without it gets the default (Blue) when it is loaded, silently; a file with more than one keeps the
  first and gives a note. It is always the last rule, and its priority is always 10 (the other rules use 1 to 9, so it can never win against a
  real match); another priority in the file is replaced by 10 with a note. It has no value.
- In the **Org-Attribute** tab it is the last row: it cannot be removed, its Type ("No Match"), Value ("Player does not meet any other rule") and
  Priority (10) are greyed out, and only its Color can be changed (it is part of Save like any other change). "+ Add rule" adds new rows above it,
  and the Type drop-down of the other rows does not offer "No Match".
- Import: **Merge** keeps your No Match color; **Replace** and **Backup** take the imported one (or the default if the imported file has none).
  The row counts in the import summary and in "New config loaded" do not count the No Match rule.

## Phase 2: the overlay

IRIS now has the overlay window. It is driven by **test data only**; Phase 3 will feed the same model with real detections from screen capture and OCR.

**What it draws** (over the game window, nothing else is covered, mouse clicks go straight through it to the game, and it never takes the keyboard focus):

- **Brackets** around each nameplate, in the player's color, with a thin dark outline so they show on dark and bright scenes:
  `{ }` player list, `[ ]` org list, `< >` org-attribute match and No Match.
- **The right-hand panel**: one rounded box per bracketed player, filled with the bracket color at 50% opacity. Line 1 is the handle, line 2 the orgs
  line (the same text rules as before: deciding org first, main org with `*`, at most 40 characters). **Every** bracketed player is listed, No Match
  players too. Orgs come from the cache; a player the cache knows nothing about shows `Orgs: not looked up`.
  Order: player list, org list, org attribute, No Match; players of the same kind stay in the order they were first seen.
- If the entries do not fit, the panel stops before the bottom 10% of the screen and its last line says `+N more`.

**When it shows.** Star Citizen must be the window in front and must run in **borderless windowed mode**: IRIS only overlays a window that exactly
covers its monitor and is not exclusive Fullscreen, because only then do window and screen coordinates line up. In the game, set
*Graphics > Display Mode* to **Borderless Windowed**. IRIS reads the game's own setting: the `WindowMode` line of the running game's `attributes.xml`
(`<Attr name="WindowMode" value="1"/>`; 0 = Windowed, 1 = Borderless Windowed, 2 = Fullscreen). Exclusive Fullscreen looks exactly like a borderless window from
the outside (no title bar, size of the monitor), which is why the window alone cannot tell them apart. The file is found relative to the running `StarCitizen.exe`
(`<install>\Bin64\StarCitizen.exe` gives `<install>\user\client\0\Profiles\default\attributes.xml`, or else the most recently changed
`<install>\user\client\*\Profiles\default\attributes.xml`), so LIVE, PTU and EPTU installs side by side each use their own. It is only ever read, never
written, and re-read when its time stamp changes (looked at once per second; the game may only write it when the graphics settings are applied). The log says which file
is used and each mode change ("Star Citizen display mode: Fullscreen"). If the file cannot be found or read, IRIS falls back to the window check (no title bar or frame and exactly the
size of the monitor). If the game is in front but not borderless, the overlay stays off and IRIS shows a small pop-up with a single **Close** button, naming the actual mode when it is known:
"Star Citizen is in Fullscreen mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings." (or "Windowed mode"; when the mode
is unknown: "Star Citizen is not in borderless windowed mode, so the IRIS overlay is turned off. Switch to Borderless in the game's graphics settings."). It is shown at most once per run of IRIS (not once per game session),
never while Disable Overlay is on, and not while another IRIS pop-up is open. IRIS looks at every 250 ms check, not only when the window state changes, so a game that was already running in a non-borderless mode when IRIS started gets the notice too, and a skipped moment is tried again at the next check. Like IRIS's other pop-ups it does not take the focus from the game: it waits in the taskbar, and there is no tray balloon. It is not a question that IRIS waits for: the tray's Settings and Quit work while it is open (Quit closes it).
**Disable Overlay** (tray) hides it, see "Disable Overlay, Disable Info Panel and hotkeys" below. The overlay window is created the first time it is needed and afterwards only hidden. While the game is not in front, the only work IRIS does is
a check of the foreground window every 250 ms. State changes (game in front, borderless or not) are written to the log.

**Test overlay.** Settings tab > Overlay > **Show test overlay (15 s)** shows a sample for 15 seconds: up to 3 players from your player list, 2 org-list
examples (an org SID from your list with a made-up handle), 1 org-attribute example (if you have a rule) and 2 No Match examples (`TestPilot01`, `TestPilot02`, "Orgs: not looked up").
It appears over the Star Citizen window if the game is running (even when it is not in front or not borderless), otherwise over the screen the settings window is on. It ignores the
borderless/foreground rules. The button turns into a countdown ("Stop test overlay (12 s)"); press it again to stop. The sizes and gaps use the values currently shown in the
Settings tab, saved or not, and follow them while you move the sliders, so you can tune and preview before pressing Save. Whether the panel is drawn at all follows the saved
*Disable Info Panel* state (the tray check mark and its hotkey).

**Overlay settings** (Settings tab, in `settings.toml` as the `panel_*` keys above):

| Setting | Default | Range | Meaning |
| --- | --- | --- | --- |
| Disable Info Panel (tray / hotkey only) | off | on/off | Hides the right panel; the brackets stay. Stored as `panel_enabled` (true = panel shown). Not a Settings tab control |
| Panel width | 16 % | 8 to 40 | Of the game window width |
| Text size | 18 px | 10 to 40 | Pixels at 1440p; scales linearly with the window height (27 px at 2160p) |
| Right gap | 10 % | 0 to 40 | Panel right edge = width minus this share of the width |
| Top gap | 15 % | 0 to 60 | Top of the panel, of the height |

IRIS is per-monitor DPI aware (`app.manifest`, PerMonitorV2), so the overlay lines up with the game window in real pixels on every monitor and at any Windows scaling.

### Disable Overlay, Disable Info Panel and hotkeys

The tray menu has two check-mark items, and the same two switches exist as hotkeys (set in the Settings tab's Hotkeys section). There are no check boxes for them in the Settings
tab. They always agree: the tray and the hotkeys change the same saved setting.

- **Disable Overlay** (`overlay_disabled`): hides everything **and stops all screen reading (OCR)**. Only the 250 ms "is Star Citizen in front?" check keeps running.
  The future capture loop must check `AppServices.IsOverlayDisabled` before each capture (event: `OverlayDisabledChanged`). Kept across restarts.
  The test overlay ignores this switch.
- **Disable Info Panel** (`panel_enabled = false`): hides only the right panel; the brackets stay. The test overlay follows the saved setting (the tray check mark).
- A tray click or a hotkey saves just that one setting at once. If the settings window is open with other unsaved edits, they are kept.

**Hotkeys** (Settings tab, top section, `hotkey_toggle_overlay` / `hotkey_toggle_panel`, empty by default): *Toggle Overlay* and *Toggle Info Panel*. No box has the focus when the window or the tab opens. **Click** a box (or Tab to it and press Enter or Space) to start recording, then
press the hotkey; Esc cancels (Esc itself cannot be assigned), **Clear** removes it. Merely focusing a box does not start recording. A single key without any modifier is allowed (for example `F9` or `O`), because the key press
always reaches the game as well; combine it with Ctrl, Alt, Shift or Win if you prefer. The modifiers must match exactly: a plain `O` hotkey does not fire while Ctrl is held. While a box is recording, hotkeys do not fire.
An empty box shows "(none)". Written as text such as `Ctrl+Shift+O` or `F9`; keys: A-Z, 0-9, F1-F24,
Num0-Num9, Space, Tab, Enter, Backspace, Insert, Delete, Home, End, PageUp, PageDown, arrows.

Hotkeys are global and work while the game is in front. IRIS reads them with a low-level keyboard hook rather than by claiming the combination, so **the key press
always reaches Star Citizen (or whatever program is in front) as well as IRIS**: IRIS never swallows a key, and it does not check whether another program uses the same
combination. A hotkey fires once when its key goes down with exactly its modifiers held (holding the key does not repeat it) and is active again after the key is released.
Key presses sent by other programs count too. The hook runs on its own background thread (so a busy settings window can never delay keystrokes or get the hook removed by Windows),
is only installed while at least one hotkey is set, and is removed when IRIS quits. If Windows refuses the hook, the log says so and the next time settings are applied (for example when you press Save) IRIS tries again. The only thing IRIS checks is that
the two hotkeys are not the same combination: the boxes then show a red message, the log has a line and neither is active.

When a hotkey is pressed and Star Citizen is in front and borderless, the overlay shows a short confirmation ("Overlay off", "Overlay on", "Info panel off", "Info panel on")
for about 2 seconds just above the top of the panel, white text with a dark edge on a dark translucent box. This also works while the overlay is disabled (only that text is drawn).
A new press replaces a confirmation that is still showing. In any other case, including while the test overlay is running, the result is a tray balloon instead.
Clicking the tray items shows no on-screen text.

### Credits

The overlay text uses the font **Quantico** (Bold), licensed under the SIL Open Font License 1.1. The font file (`Quantico-Bold.ttf`) and its license text
(`OFL.txt`) are in the repository, in `src\Iris.App\Assets\Fonts\`; the font is built into `IRIS.exe` and `OFL.txt` is copied next to it. The project file picks up
`Assets\Fonts\*.ttf` by wildcard, so the build still works if the file is removed; then the overlay uses Segoe UI Bold (as it also does if the font cannot be read).

## Build and test (Windows)

One-time setup: install the .NET 8 SDK.

```
winget install Microsoft.DotNet.SDK.8
```

Then, from this folder:

```
dotnet test tests\Iris.Core.Tests
```

The first run downloads the packages it needs, which takes about a minute.
Without captured RSI pages, a good result looks like `Passed!  - Failed: 0, Passed: N, Skipped: 6`.
The 6 skipped tests are the ones that need real RSI pages (see below).

## Run the app (Windows)

```
dotnet build src\Iris.App
dotnet run --project src\Iris.App
```

IRIS starts in the system tray (bottom right, maybe under the `^` arrow). Double-click the icon, or right-click it and choose
"Settings...", to edit your lists. Only one copy can run at a time. Closing the window keeps IRIS running; use "Quit" in the tray menu.
The tray menu also has "Open log" (today's log file in your text viewer) and "Open config location" (opens the `Data` folder in Explorer).
The window's **Save** button is grey while nothing differs from what is saved; saving does not close the window ("Saved at HH:mm:ss" appears in the status bar). Save is also off while an import, export or save is running.
It stays **on** while `social-overlay.toml` or `settings.toml` has errors, even if you changed nothing: that is how you repair a broken file (Save asks first and keeps a copy of the broken file as `.invalid-<timestamp>`). **Close** (or Escape) closes it and asks first if there are unsaved changes.

Everything IRIS stores lives in a `Data` folder next to `IRIS.exe` (when run from the build output:
`src\Iris.App\bin\Debug\net8.0-windows\Data`): `social-overlay.toml` (your lists, plus `.bak` of the previous save),
`settings.toml` (this PC's app settings, see below), `cache.db` (what IRIS learned from RSI), `exclusions.txt` and `logs\` (one `iris-yyyy-MM-dd.log` per day, kept for 30 days).
Delete the folder to start fresh. `.gitignore` ignores every `Data/` folder (it is your personal data) and `bin/`/`obj/`;
it must not ignore the source of `src\Iris.App`, and it does not.

## Settings, import and export

The window's **Settings** tab holds the settings of this PC. They live in `Data\settings.toml`, a separate file that is never
shared or exported with your lists. Press the window's single **Save** button to save the lists (if you changed them) and the
settings (if you changed them); an invalid hosted URL blocks the save with a message. Settings take effect at once, no restart.

`settings.toml` (all keys are optional; a missing file means the defaults and nothing is written until you save):

```
format_version = 1
cache_ttl_days = 30            # 30 or 7; anything else becomes 30 (with a note in the log)
session_log_enabled = true     # write Data\logs\iris-yyyy-MM-dd.log
check_for_updates = true       # a setting only for now: the GitHub version check arrives in a later version
hosted_config_url = ""         # https:// address of a config file you host yourself
hosted_config_last_hash = ""   # SHA-256 (64 hex characters) of the last hosted file you applied or declined (IRIS fills this in)
hosted_config_auto_check = false              # true: check the hosted address every 24 hours (see "Hosted Config URL")
hosted_config_last_check = "2026-10-22T09:30:00Z"   # optional; when the last automatic check was made (UTC text; IRIS fills this in)
panel_enabled = true           # false = "Disable Info Panel": the right-hand panel is hidden (the brackets stay)
overlay_disabled = false       # true = "Disable Overlay": nothing is drawn and no screen reading happens
hotkey_toggle_overlay = ""     # global hotkey for Disable Overlay, e.g. "Ctrl+Shift+O" or "F9"; empty = none
hotkey_toggle_panel = ""       # global hotkey for Disable Info Panel; empty = none
panel_width_percent = 16       # panel width, % of the game window width; 8 to 40
panel_text_size = 18           # panel text size in pixels at 1440p (scales with the window height); 10 to 40
panel_right_gap_percent = 10   # space right of the panel, % of the width; 0 to 40
panel_top_gap_percent = 15     # space above the panel, % of the height; 0 to 60
```

A hotkey that is not valid (no key, unknown key, more than one key, wrong type) is noted in the log and becomes empty. A `panel_*` value that is out of its range (or not a whole number) is noted in the log and replaced by its default.
`hosted_config_auto_check` is off by default. Switching it on without a valid `hosted_config_url` switches it off again, with a note; a
`hosted_config_last_check` that is not a date and time is noted and counts as "never checked". Unknown keys are noted in the log and ignored. A file that is not valid TOML is not changed until you save; until then the defaults
apply and IRIS tells you; a background change (such as the remembered hosted version) is then kept in memory only, and the file is
written when you press Save. A hash that is not 64 hex characters is cleared with a note. Turning the log file off stops writing lines to it; the in-memory log keeps working, and files older than
30 days are still deleted at startup.

**Refresh Cache (long time)** (Settings tab, at the bottom) refetches everything on your saved lists from RSI, whatever its age. It takes a long
time (about one request per second), the button is off while it runs and the result appears in the status bar. There is no such entry in
the tray menu any more. Loading a config (an import, or a changed file, see below) does not run this: it only fills in what is missing.

### Request rate and backoff

IRIS sends at most one request per second to robertsspaceindustries.com, from one shared limiter. When RSI answers 429, any 5xx error,
or does not answer (timeout, network error), the wait before the next request doubles: 1, 2, 4, 8, 16, 30 seconds (30 is the cap). If RSI sends
a Retry-After (seconds or a date) with a 429 or 503, the longer of the two is used (at most 120 seconds). Every good answer halves the wait
again, back to 1 second (a good answer to a request that was sent before the latest bad answer is ignored: it says nothing about how RSI is doing now). Changes are logged ("RSI is slowing us down: waiting 4 s between requests"), and each request line in the log
shows how long it took, e.g. `-> found [AVOCADO page 12] (0.42 s)`.

### Watching the config files

IRIS looks at `social-overlay.toml`, `settings.toml` and `exclusions.txt` when it starts, when the config window opens, and every 2 seconds
while the window is open (never while it is closed). It first only checks the file's time and size; if either differs it reads the file
(even while an editor has it open) and compares a SHA-256, so a file that was only touched is ignored.

- A changed, valid file is applied and a tray message says so ("New config loaded: 3 players, 2 orgs, 1 rules", "New settings loaded",
  "Exclusion list reloaded (N names)"). If the window is open it shows the new rows or settings at once; **unsaved changes in the window are
  discarded** when that happens. After a new list is loaded, a background "fill missing" refresh fetches only players and orgs that have no
  cache entry or an entry older than the refresh interval.
- A changed file with errors is not applied: IRIS keeps using the previous version, shows a tray message, and the window shows a red
  banner with the first errors (with line numbers when available) and an **Open file** button. Each bad version is reported once. The next Save
  replaces the file (the broken one is kept as `.invalid-<timestamp>`).
- Every time `social-overlay.toml` or `settings.toml` is loaded or saved and is valid, the exact text is also written to
  `social-overlay.lastgood.toml` / `settings.lastgood.toml` in the `Data` folder. If the main file is broken when IRIS starts and a
  last-good copy exists, IRIS uses the copy, says so ("using the last good version (from <date>)") and does not overwrite the main file.
  `exclusions.txt` has no last-good copy.
- If `social-overlay.toml` is deleted while IRIS runs, the tray message and the window's status bar say so; IRIS keeps using the list it has, and
  the next Save (or import) writes the file again.
- The first check when the config window opens runs in the background, so the window appears at once and then shows any edit made while it was closed.
- A broken `settings.toml` is only replaced when you agree (Save asks "Replace it with the settings shown here? A copy of the broken file is kept.");
  settings are written only when they differ from the saved ones or to replace such a broken file. If you decline replacing a broken file
  that you did not edit, edits to the other file (lists or settings) are still saved.

### Import

**Import from file...** and **Import from URL** read a config file (at most 1 MB; the file is only ever read as data).
If it has errors, the first 15 are shown and nothing changes; an archetype or activity rule whose value is not one IRIS knows
counts as an error. Notes (such as unknown keys) are shown and you can continue. Only one import runs at a time.
If the window has unsaved changes you are asked "Save your changes first?" (Yes saves them, then imports; No discards them and
imports; Cancel stops). When importing from a URL, saving first does not store the address you typed: the address is stored only
after the import worked.
You then pick what happens to your current lists, every time:

- **Backup existing config**: your current file is renamed `social-overlay.backup-yyyyMMdd-HHmmss.toml` (same folder), and the
  imported config becomes the list file.
- **Merge config files**: your rows stay first, in their order; imported rows you do not have yet are added after them.
  Duplicates are players with the same handle, orgs with the same SID, and rules with the same kind and value (case does not matter;
  Redacted and No-org rules count by kind alone). On a duplicate your row wins, with its color and priority. Your list name is kept.
  The No Match rule is not merged: the result keeps yours.
  The usual `.bak` of the previous version is kept. A current file with errors cannot be merged (choose Backup or Replace).
- **Replace existing config**: the imported config overwrites the list file (the usual `.bak` keeps the previous version).

With no list file yet, the import just asks for confirmation and writes the file. Afterwards the window shows the new rows, every
name is checked against RSI, and the status bar says what was added.

### Hosted Config URL

Put the address of your own config file (for example a raw file in your own repository or a web space) into **Hosted Config URL**.
Rules IRIS follows when it fetches it: `https://` only, no user name or password in the address; one GET, 15 seconds, at most 1 MB;
redirects are followed by hand only within the same site, same port (up to 3), a redirect to another site is refused with a message;
every request URL, refused ones too, is logged without any user name or password; a failed fetch is never remembered.

The hosted address is contacted when you press **Import from URL**, and, only if you tick the check described below, once a day. If the fetched file is the same version (SHA-256) as the one you last
imported from there, nothing is imported and IRIS says "Your hosted config has not changed." (always as a tray message, and in the status bar while the window is open). Otherwise the usual import choices appear, and after a successful import the address and version are remembered. A failed fetch is never remembered.

#### Checking the hosted config every 24 hours (opt-in)

Beside the **Import from URL** button the Settings tab has the check box **Check URL for updates every 24 hrs**. It is greyed out
while the address box is empty or not a valid `https://` address (clearing the address unticks it); when a valid address is there it is enabled,
and stays unticked until you tick it. It is saved with the Save button like every other setting.

While IRIS runs (the config window does not have to be open) and the box is ticked, IRIS checks at startup if it has never checked or the last check is 24 hours
or more ago (`hosted_config_last_check`), and then wakes every 10 minutes to see whether 24 hours have passed again. A check downloads the file exactly like
**Import from URL** does (only your address, every request logged) and stores the time afterwards, whether it worked or not; a failure is only written to the log.

- Same version (SHA-256) as the last file you imported: nothing happens.
- Otherwise IRIS asks: "An update to your hosted config is available. Do you want to update now?". **Yes** runs the same import as **Import from URL**
  (error check, notes, unsaved-changes question, Backup/Merge/Replace question, background fill-missing refresh; the version is remembered when it worked).
  **No** does nothing more until the next 24-hour check, which asks again if the file still differs.
- It never runs while an import or another IRIS question is open, nor while IRIS is quitting: that round is skipped (the time is not stored for a skipped
  question) and the next wake tries again. A newly ticked check starts at the next wake (within 10 minutes) or at the next start.

### Pop-ups that appear on their own

A pop-up that IRIS opens without you having just acted in it (the startup "errors were detected" and "problems in your list file" messages,
the 24-hour update question, the import question when no config window is open, an unexpected-error message) is never forced over the program you
are using. If an IRIS window is the active window it is an ordinary dialog. Otherwise (for example the game is in front) it is not on top and does not
take the focus: it waits behind, with its own button in the taskbar, and a tray message with its headline tells you that it is waiting. While the
24-hour update question is open the Settings window cannot be opened and Quit asks you to answer it first. Pop-ups that answer a click in the IRIS window
(save, import, export...) behave as before.

### Export

**Export...** writes the lists as last saved to a file (default name `iris-config.toml`). If the window has unsaved list changes you
are asked whether to save the lists first (Yes; settings edits stay unsaved), export the saved version (No) or stop (Cancel). A file that
cannot be written (for example a read-only USB stick) is reported in the status bar and a message. The file contains only the lists, never settings.
Exporting onto IRIS's own `social-overlay.toml` or `settings.toml` is refused with a message.

## Capture real RSI pages (needed for the parser tests)

Run once, from this folder (it makes about 6 requests to robertsspaceindustries.com, 3 seconds apart):

```
dotnet run --project tools\Iris.CaptureFixtures
```

It saves the pages into `tests\Iris.Core.Tests\Fixtures\rsi\`. Those files are copies of RSI's pages, so they are
git-ignored and stay on your PC. Then run the tests again:

```
dotnet test tests\Iris.Core.Tests
```

This time the 6 fixture tests run instead of being skipped. If one fails, the failure message says which page and which
expectation; send it back along with the failing page so the parser's selectors can be fixed.

## License

GNU General Public License v3.0. See `LICENSE`.

## Disclaimer

Unofficial fan tool. Not affiliated with or endorsed by Cloud Imperium Games.
Star CitizenÂ® is a registered trademark of Cloud Imperium Rights LLC.
