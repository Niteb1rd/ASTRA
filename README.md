# IRIS

IRIS – Companion App for Star Citizen. First feature: **Social Overlay**.

Windows overlay that brackets the nameplates of players you care about, in colors you choose,
and summarizes their orgs in a side panel. See the requirements doc for the full specification.

## Status

Phase 3, step 3 makes the overlay live: DXGI screen capture of the game's scan region, name detection 5 times a second, template tracking of the plates between detections (on every new frame of the game, at most 30 to 90 times a second, or every frame with "Max") and brackets drawn at the monitor's refresh rate (see "Phase 3, step 3: live detection" below). Phase 3, step 2 added the nameplate detector and a replay tool; step 1 the nameplate letter recognizer. Phase 2: the overlay shell. IRIS runs as a tray app with a Social Overlay config window and the overlay window itself (brackets and the right-hand panel). The Settings tab has an Overlay section (including the tracking tickrate) and a "Show test overlay (15 s)" button to try it. See "Phase 2: the overlay" below.

- `src/Iris.Core`: config file (TOML) read, write and validate; color and bracket rules; right-panel org line (increment 1).
- `src/Iris.Core/Rsi`: reads robertsspaceindustries.com. Pure parsers for the player organizations page, the org page and the
  org members API; an HTTP client that returns Found / NotFound / Failed (never exceptions), allows one request per second
  app-wide (slower while RSI asks us to wait, see below), only talks to robertsspaceindustries.com, and logs every request (URL, time, outcome).
- `src/Iris.Core/Cache`: SQLite lookup cache (players, their orgs, orgs and their members, "no such citizen" notes), with the
  source of every entry, a 30 or 7 day refresh interval, "Refresh Cache" (list entries only), the hotkey lookup and the
  org-list-member lookup.
- `src/Iris.Core/Detection`: the exclusion list (hand-editable text file plus shipped defaults), the nameplate name filter, and the
  tracker that adds names seen on two separate nameplates at once (increment 3).
- `src/Iris.Core/Live`: the live pipeline without threads or windows: `IFrameSource`, `ScanGeometry`, `LiveDetectionEngine`, `LiveSnapshotBuilder`, `LivePredictor`, `PlateTemplateTracker`, `PlateMasker`, `PlateSeedBuilder`, `DetectionMailbox`, `OrgMemberLookupQueue`, `LiveStats` (Phase 3, step 3).
- `src/Iris.Core/Storage`: `DataPaths` (the `Data` folder and the files in it) and `SocialListStore` (loads the list file; saves it
  crash-safely through `.tmp`, keeping one `.bak`). `src/Iris.Core/Config/RowValidator.cs` checks a typed handle or SID for the
  config window: format, duplicates, cache first, then one RSI request (increment 4).
- `src/Iris.Core/Settings`: `AppSettings` and `AppSettingsToml` (`Data\settings.toml`). `src/Iris.Core/Config` also holds
  `SocialConfigMerger`, `ConfigImporter` (the three import modes, export) and `HostedConfigClient` (increment 5).
- `src/Iris.Core/Overlay`: the overlay model and its math, without any windowing code: `OverlayModel` (brackets and panel entries), `OverlayModelBuilder`
  (detections + cached orgs to model), `PanelLayout` (panel rectangles), `GameWindowRules` (the borderless decision), `StarCitizenDisplaySettings` (the game's WindowMode setting), `OverlayPalette`, `TestOverlayBuilder` (Phase 2).
- `src/Iris.App`: the Windows app (WPF): tray icon, the config window with the Players, Orgs, Org-Attribute and Settings tabs, the overlay window
  (`Views/OverlayWindow`, `Views/OverlayPainter`, `Services/LayeredWindow`), the game window tracker and the overlay controller, DXGI capture and the live detection service (`Services`).
  It has no automated tests yet; everything testable lives in `Iris.Core`.
- `tests/Iris.Core.Tests`: automated tests for all of the above. Tests that need real RSI pages are skipped until you capture them.
- `tools/Iris.CaptureFixtures`: saves a few real RSI pages for those tests.
- `tools/Iris.Replay`: developer tool that runs the nameplate detector over a video or a folder of frames and measures recall (Phase 3, step 2).

The page parsers were written without access to RSI's real pages. Their CSS selectors and label texts are guesses, collected as
named constants at the top of each parser. Capture the fixtures and run the tests to find out which guesses are right.

## Player handles and the exclusion list

A player handle is 4 to 64 characters: letters, digits, `-` and `_` only. Anything else read from a nameplate is ignored (and a player list entry that breaks the rule is rejected).
A name read from a nameplate may in addition not start or end with `-` (detection rule 6 below: such a read is an animation stage or a misread); a listed or RSI handle may, because older accounts might have one. The limits are fixed in the program (`RsiNames.MinHandleLength` and
`MaxHandleLength`).

The exclusion list is a plain text file, `exclusions.txt`: names that appear on screen but are not players (stars, planets, stations...).
One name per line, upper/lower case does not matter, blank lines are ignored, and a line starting with `#` is a comment.
You can edit it by hand while IRIS runs; changes are picked up automatically. The first time IRIS runs it creates the file
from the shipped list, `src/Iris.Core/Detection/exclusions.default.txt`. In the app it lives in the `Data` folder (see "Run the app").

- A nameplate whose name is on the list is never matched or looked up, except when the name is also on your own player list
  (your player list wins). Org-list members and hotkey lookups follow the exclusion list with no such exception.
- IRIS no longer writes to this file by itself: it only holds your own entries and the shipped defaults. Duplicates are handled for
  the running session only (see "Duplicate names" below).

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
written, and read only once per IRIS run (the first time the game is in front). Star Citizen removes the WindowMode line when Windowed is chosen, so a
valid file without it counts as Windowed. A mode changed inside the game is picked up the next time IRIS starts. The log says which file
is used and the mode read ("Star Citizen display mode: Fullscreen"). The setting alone decides; the window's shape is not looked at. If the file cannot be
found, read or understood, IRIS tries once more a second later; if that fails too, it logs "Failed to open attributes.xml, assuming Borderless mode" and
treats the game as Borderless. If the game is in front but not borderless, the overlay stays off and IRIS shows a small pop-up with a single **Close** button, naming the actual mode when it is known:
"Star Citizen is in Fullscreen mode, so the IRIS overlay is turned off. Switch Display Mode to Borderless Windowed in the game's graphics settings." (or "Windowed mode"; when the mode
is unknown: "Star Citizen is not in borderless windowed mode, so the IRIS overlay is turned off. Switch to Borderless in the game's graphics settings."). It is shown at most once per run of IRIS (not once per game session),
never while Disable Overlay is on, and not while another IRIS pop-up is open. IRIS looks at every 250 ms check, not only when the window state changes, so a game that was already running in a non-borderless mode when IRIS started gets the notice too, and a skipped moment is tried again at the next check. Like IRIS's other pop-ups it does not take the focus from the game: it waits in the taskbar, and there is no tray balloon. It is not a question that IRIS waits for: the tray's Settings and Quit work while it is open (Quit closes it).
**Disable Overlay** (tray) hides it, see "Disable Overlay, Disable Info Panel and hotkeys" below. The overlay window is created the first time it is needed and afterwards only hidden. While the game is not in front, the only work IRIS does is
a check of the foreground window every 250 ms.

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

## Phase 3, step 1: the nameplate letter recognizer

Phase 3 reads real nameplates from the screen. Step 1 is the reader itself, without screen capture yet: it is given an image and the box of one
nameplate line and returns the name. It lives in `src/Iris.Core/Detection/Letters/`:

- `LetterModel`: the trained classifier (a small neural network: 388 numbers describing one letter in, a log probability for each of the 43 characters
  `A-Z 0-9 _ -` and, for distance lines, `. k m` and the brackets `[ ]` of the speed readout out). Its weights are the file `letters-model.bin`, embedded in `Iris.Core` as the resource `Iris.Core.Detection.Letters.letters-model.bin`. The file contains numbers only; it was trained from the developer's own
  gameplay clips with a separate local training kit that is not part of this repository.
- `NameplateFeatures`: the image side. Crop to a 4x text mask (local contrast on the brightest colour channel, so cyan, white, red and green text all work,
  with an automatic contrast threshold), the capital band, cuts at blank columns, and the 388-number letter description.
- `NameplateReader`: reads a line. Pieces are cut at blank columns; a piece too wide for one letter (letters touching) is cut by a dynamic-programming search for
  the cut set whose letters the model is surest about. Input is an OpenCV `Mat` (BGR) or a pixel buffer (`byte[]`, width, height, stride) plus the name box.
  Output is the text and a score (sum of the letters' log probabilities; closer to 0 is surer).

It is a port of a Python prototype, step for step (same rounding and comparison rules), so that both give the same result. The tests in
`tests/Iris.Core.Tests` (`LetterModelTests`, `NameplateReaderTests`) check this against **synthetic** fixtures in `tests/Iris.Core.Tests/Fixtures/letters/`:
text drawn by a script onto generated noisy coloured backgrounds, with the Python results for every stage (mask, capital band, cuts, letter vectors, final
text) and 200 feature vectors with the model's log probabilities. The model must agree within 1e-4, the mask with at least 99.5 % of the pixels of the overlap (OpenCV builds
can round differently at the edges, so a size difference of 1 row or column is tolerated), and the final text must be identical on every line. These fixtures check that the C# code gives the same results as the Python reference (parity); they do not measure how accurately real nameplates are read. No game footage, screenshots or player names are in the repository.
The tests that need OpenCV's native library are skipped on systems other than Windows where it is not available (the tests project carries the Windows x64 one); on Windows they are never skipped, so a broken native library fails them.

### OpenCvSharp4 (new dependency)

The image operations use [OpenCvSharp4](https://github.com/shimat/opencvsharp) (a .NET wrapper of OpenCV, Apache-2.0 licence): `OpenCvSharp4` in `Iris.Core`,
and the native library through `OpenCvSharp4.runtime.win` in `Iris.App` and the tests. The native library is large: expect the package download to be tens of
megabytes (the Windows x64 build) and `IRIS.exe`'s folder to grow by a similar amount. The first `dotnet test` or build downloads it.

### The letter model file

`letters-model.bin`, little endian:

| Part | Type | Meaning |
| --- | --- | --- |
| magic | 8 bytes | `IRISLM01` |
| version | uint32 | 1 |
| charset | uint32 byte length N, then N bytes UTF-8 | the characters, in output order: output unit i is `charset[i]` (`-.0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ[]_km`, 43 characters: the letters of names, plus the decimal point and the lower case `k` and `m` of distance lines, and the square brackets `[` and `]` of the speed readout) |
| input size | uint32 | 388 (a 16x24 picture of the letter plus width, height, top and bottom offset relative to the capital height, each times 4) |
| layer count | uint32 | L (currently 2: 388 to 256 to 43) |
| each layer | uint32 rows (inputs), uint32 cols (outputs), float32 weights row-major (`weight[i][j]` connects input i to output j), float32 biases (cols values) | |

ReLU follows every layer except the last; softmax follows the last. The reader clips each probability to at least 1e-6 and takes its logarithm.
`LetterModel.Load` rejects files with a wrong magic, version or shape, or with missing or extra bytes.

## Phase 3, step 2: the nameplate detector (v2), the tracker and the replay tool

The detector finds nameplates in a whole frame and reads them with the letter model of step 1 (`src/Iris.Core/Detection/NameplateDetector.cs`).
It does not use Tesseract or any other OCR: IRIS does not ship one. Version 2 is built around a **distance anchor**: false nameplates are worse than
missed ones, so a text line is only a nameplate when it has the distance line the game prints under every real player nameplate.

### Detector rules (v2)

1. **Cheap pass.** Colour mask (top-hat 11x11 per colour channel, then the cyan/white, red and green rules), tall blobs (over 22 px) dropped, text lines by dilation 7x1 and
   size/fill filters. Default scan region x 15-85 %, y 10-70 % of the frame (`NameplateDetectorSettings`; the replay tool takes `--region`).
2. **Distance anchor.** A line is a distance only if it reads as a number from 0 to 1000, a period, **exactly one digit**, then lower case `m` or `km`, and nothing after it
   (`DistanceLineRule`); one word gap after the unit may be followed by the `[` of a speed readout, which ends the line for the rule (see "Speed readout" below). A distance without a decimal is ignored. The line is read with the letter model restricted to `0-9 . k m` and the digit look-alikes below; the read must be sure
   (mean score and the gap to the model's free best), and no gap inside the line may be wider than a word gap. Unsure end pieces (specks next to the line) are trimmed first.
   Nothing may touch the line, and 3 line-box heights (`ClearLines`; the height of the line's bounding box, which is about one character width) must be clear on both sides, small specks tolerated
   (the one exception is the crowd exception, rule 4).
3. **Name.** The line directly above the anchor (the gap between the two lines is at most 1.4 line-box heights; the lines are about equally high), a **single word** of 4 to 64 characters `A-Z 0-9 - _` (not starting or ending with `-`, rule 6), read with the score per letter at least -0.20.
   It must be centred over the distance line (the game centres a name over its distance line; with a speed readout over the whole line, speed group included), left-aligned pairs are rejected (the left edges equal within 2 px while the widths differ by 8 px or more:
   HUD label columns such as `R-ALT` over `VSI`), and 3 line-box heights must be clear on both sides.
4. **Isolation: strict, crowd and loose pairs.** A pair whose lines are both isolated is a *strict* pair. A pair whose lines fail only the isolation test is a *loose* pair: it may only continue a track
   that already is a nameplate, never start one (hysteresis). **Crowd exception:** nameplates drift next to each other, so ink beside a line does not count against it when ALL of that ink lies inside the boxes of
   other text lines that are themselves part of another candidate pair (a line over a line). Such a *crowd* pair is treated as strict, but only when every one of those neighbouring pairs also reads as a
   real nameplate (a valid distance anchor under one word of name characters; `FrameEvaluator`); a ring, a ship, a HUD arc, a panel, or text that is not part of a valid nameplate beside the line still
   rejects it (it stays loose, or is rejected). `NameplateDetectorSettings.CrowdException` (default on) switches the exception off.
5. **Contact codes.** A name of the exact shape `XX-XXXX-XX` (2 characters, hyphen, 4 characters, hyphen, 2 characters, each character a letter or a digit) is ignored unless it is on the player list (`ContactCode`, `NameFilter`). New RSI handles can't contain hyphens, so the two hyphens make the shape unambiguous and fast to check. A code read without its hyphens, or with a hyphen misread, is not ignored (it gets the No Match bracket).
6. **Names read from the screen do not start or end with `-`** (the handles RSI hands out today do not; `_` may start and end a name, `QUARTZ_` is valid). A read such as `-PIL0TFISH` is an animation stage or a misread, not a name: the pair is still a nameplate (it has its distance anchor), but the read is no vote (`NameShape.HasValidEdges`, used by the detector and the tracker only). A hyphen inside a name is fine (`CINDER-404`). The rule is **not** part of `RsiNames.IsValidHandle` (RSI page parsing, member lists, the player list, handles you type): an older RSI account might have a handle with a hyphen at an edge, so those only need 4 to 64 characters of `A-Z a-z 0-9 - _`.
7. **Line width.** The cheap pass accepts text lines up to `NameplateDetector.MaxLineWidth` = **832 px at 1440** (scaled with the frame height; it was 400): a 64 character name must fit. A letter advances 10.1 px on average at 1440 (2801 reads of labeled names of 6 or more letters: median 10.0, 95th percentile 11.0, widest 11.7, the same at every line height), so 64 letters are about 650 px, 750 px with only wide letters; 832 = 64 x 13 leaves 11 % over the widest advance seen. The reader's work limits (`MaxPieceCaps` = 8 capital heights, `MaxSplitSpans` = 1500) apply per run of touching ink, not per line, so a long word is not rejected by them; the time per line grows linearly with its length.

There is no stacked rule any more (v1 accepted a plate above a confirmed one without its own distance).
Noise protection: a run of ink wider than 8 capital heights, or a cut search of more than 1500 candidate spans, is not cut: that run reads as nothing and scores -50.
**Resolution.** The pixel constants are tuned for a frame 1440 px high and scaled by `frame height / 1440` (rounded, at least 1); the letter reader itself works from the capital height of the line.

### Look-alike lists (`LookAlikes`)

Two lists, in one documented class, used for different things:

| List | Groups | Used for |
| --- | --- | --- |
| **Efficiency** | `0 O Q D`, `1 I 7`, `5 S`, `8 B`, `6 G`, `2 Z` | only the digits of the distance line: a letter the model sees where a digit must be counts as that digit |
| **Accuracy** | none (the list is empty) | nothing: **names match exactly**, ignoring only upper/lower case (handles are case-insensitive on the RSI site) |

Wherever two names are compared (the player list, the exclusion list, the colour lookup, the replay tool's labels) the characters must be the same: `N1TEBIRD` is not `Nitebird`, `UNKN0WN` is not
the excluded `UNKNOWN`, and a confirmed `PIL0TFISH` is a wrong name, not a found `PILOTFISH`. A wrong name is worse than a missed one (it is looked up on the RSI site and shown on screen).
`NameMatcher` (a developer tool for scoring reads against labels) is exact in the same way; it used to use a weighted edit distance with seven confusable groups, then only 0/O and 1/I.

**There is no character-swap retry of a lookup.** A name that RSI does not know is remembered by the negative cache (`not_found`, 30 days, `CacheSettings.NotFoundRetention`), so a name read the same way
again is not asked again. A different read is a different name and may be asked. A misread such as a `0` for an `O` is fixed with better training data (more samples of that letter in the training kit), not with tolerance.

### Nameplate tracker (`NameplateTracker`, no WPF)

A name is not trusted from one frame. The tracker (`src/Iris.Core/Detection/NameplateTracker.cs`) follows each candidate pair from frame to frame:

- **Association** of a candidate with a track needs about the same line height and width, and either a centre within 3 line heights (`MoveLines`) of the track's last position or within a gate around the
  position the track is *expected* at: its last position plus its velocity (from its last two positions, at most `MaxSpeedDtMs` = 500 ms apart) times the time since it was seen. That gate is 3 line heights plus `SpeedGain` (1.0)
  times the distance the track is expected to have moved, at most `MaxGateLines` (10) line heights, so a fast mover is followed. Matches are made nearest (to the expected position) first, and one candidate gets
  one track and one track one candidate. **Identical read:** a strict candidate that no gate holds is read at once; when it reads the same name as a `Nameplate` track seen within `IdenticalMs` (500 ms) whose name
  line has its size, it continues that track however far it jumped (a track is claimed once per frame, the most recently seen and then the nearest first). Otherwise it starts a new track.
- **Sticky verdict** per track: `Nameplate` (it passed the distance anchor) or `NotNameplate`. A `NotNameplate` track is not read again every frame, only every 3 s (`RecheckMs`) or when its box size changes
  notably (width by 15 %, height by 2 px or 15 %). A `Nameplate` track that fails 3 evaluations in a row becomes `NotNameplate`.
- **Voting.** Every evaluation that reads the name surely enough (and that does not start or end with `-`) is a vote. A name is **confirmed** after being read identically `ConfirmReads` times (default **3**),
  more often than any rival, and the identical reads must span at least `ConfirmSpanMs` (default **300 ms**): the game types or scrambles a name when a plate appears (`ORBI`, `STELLARK`, `UNKN0WN3`), and a stage of that
  animation can be read twice in a row; a real name stays. "Delay over wrong." Before the first confirmation only the votes of the last 4 x `ConfirmSpanMs` (1.2 s; `PreConfirmationWindowMs`) count, older ones are
  dropped (a confirmation needs reads that span `ConfirmSpanMs`, so older votes cannot be part of one, and a plate that never confirms must not keep every vote it ever got; with `ConfirmSpanMs` 0 there is no cap).
- **Stable box.** The identical reads that confirm a name must also have the same name-line **width**, within `StableWidthPx` (default **3 px** on a frame 1440 px high, scaled by `frame height / 1440`; pass the
  frame height to `Update`): while a plate types its name the box grows by a letter (about 9 px) per stage. Why 3 px: on the 13 clips, for the labeled names a track read at least twice, the widths of those reads
  spread (max minus min) by 0 or 1 px in 92 % of the cases, by 2 px in 4 % and by 3 px or more in 4 % (a plate that moves toward the camera changes size a little: `TITANIUM` read at 69, 73 and 72 px in three frames,
  which 2 px rejected). 3 px is a third of a letter.
- **Corrections.** A confirmed name is never withdrawn, but it is **corrected**: once a name is confirmed only the votes of the last `CorrectionWindowMs` (default 4 s) count, and when another name meets the
  confirmation rule there (enough identical reads at one width, spread over `ConfirmSpanMs`) and has more votes in that window than ANY other name has (the confirmed name included), `ConfirmedName` switches to it. A confirmed track whose
  latest read disagrees with its name is read at every frame (not once a second) until it settles; an evaluation that finds the nameplate but gives no vote (too unsure, or a `-` at an edge) ends that state. The change is exposed: `TrackerUpdate.Corrected` lists the tracks that switched in this frame (they are in
  `Confirmed` too, so a panel that shows `ConfirmedName` follows), `NameplateTrack.Corrections` lists every switch (`From`, `To`, frame, ms), `IsDisputed` says that the latest read disagrees (it is cleared again by a nameplate evaluation without a vote). A tie changes nothing.
  The replay tool counts them (`corr`, and `corrections` / `corrected` in `tracks.csv`). On the clips corrections happen when a plate is reused for another player (`CINDER-404` to `ICE_HARRIER`) or after an animation stage was confirmed.
- The confirmed name is exposed with the frame of the first read and of the confirmation. Tracks expire after 1 s unseen. A confirmed track is read again once a second.

### Replay tool (developer tool)

`tools/Iris.Replay` runs the detector and the tracker over a recording and reports what it finds. It is for tuning and checking on your own footage; your footage, labels and results stay
outside the repository (the tool refuses to write into a folder that is inside a git repository).

    dotnet run --project tools/Iris.Replay -- <input> [--labels FILE] [--out FOLDER] [--annotate] [--every N] [--model FILE] [--region L,R,T,B]
                                              [--fps N] [--image-fps N] [--confirm-reads N] [--confirm-span MS] [--track]

- `<input>`: a video file, a folder of images, a folder with one sub folder (or video) per clip, or one image. Videos are opened with OpenCV's `VideoCapture`; the
  `OpenCvSharp4.runtime.win` package may not include an FFmpeg backend for every codec. If a video does not open, cut it into frames
  (`ffmpeg -i clip.mp4 -vf fps=10 frames\%05d.jpg`) and give the folder. Frames of a clip run through the tracker in order; `--image-fps` (default 10) is the rate that the numbers in image file names stand for (it was `--fps` before `--fps` became the sampling rate).
- `--labels`: `clips.txt` (`clip: NAME1, NAME2`) runs the tracker and prints, **per clip**:
  - `found`: labeled names a track confirmed, with exactly the labeled spelling (no look-alike tolerance: a `0` for an `O` is a wrong name);
  - `delay`: the median, over the found names, from the first read of the name to its confirmation (frames and ms);
  - `wrong`: names **confirmed** that are no label, no excluded name and no contact code, listed under the table (the target is 0); `wrongEnd`: the same for the name each track ended with, after corrections;
  - `corr`: confirmed names that were corrected (a track switched to another name);
  - `junk`, `anim`, `unread`, `ignrd`: nameplate tracks whose best read is none of the known names, a stage or near miss of a known name (the typing animation), nothing readable, or an excluded name / contact code;
  - `ms med`, `ms p95`: milliseconds per frame (cheap pass, reads and tracker).

  `screenshots.csv` (`file,expected_players,notes`, names separated by `;`) reads every image once, without the tracker, and prints names read (exactly) and wrong reads per image.
- `--out`: default `%LOCALAPPDATA%\Iris\replay`. It gets `detections.csv` (`source,frame,track,verdict,confirmed,name,score,x,y,w,h,distance,...,match`: one row per evaluation that read a name, with the track,
  the track's verdict and its confirmed name so far) and `tracks.csv` (one row per track: first frame, evaluations, best name, confirmed names, number of corrections and the corrections as `FROM>TO`, all reads with counts).
  With `--annotate` it also gets PNGs in `annotated\<clip>\`: **confirmed tracks green, nameplates not yet confirmed yellow, rejected pairs red**.
- `--confirm-reads N` and `--confirm-span MS` override the tracker defaults (3 and 300).
- `--every N` uses every Nth frame (the tracker's clock follows the frame times). A video that does not open does not stop the run: it is reported as skipped.
- `--fps N` samples the frames **by time**: a frame is kept when its timestamp has advanced at least 1000/N ms since the last kept one (the first frame is always kept; default: every frame; applied after `--every`).
  A video of 60 or 120 fps is read much more often than the app will read (**5 times a second, planned**), and many reads a second let a short-lived misread reach three identical reads inside 300 ms, so a run over every frame is a
  stress test, and **`--fps 5` mirrors the planned app rate**. Use it for the numbers that matter for the app; frames that are skipped are not converted to images (a video is still decoded in full).
  **Frame numbers are native video frames** (or the numbers in image file names) also when `--fps` samples: the `frame` column of `detections.csv`, `first_frame` of `tracks.csv` and `delay fr` of the table are not counts of the sampled frames.
  With `--fps 5` on a 60 fps video three reads are at least 24 frames apart, so `delay fr` is 24 or more where `delay ms` says 400; compare `delay ms` with the app. `--fps` is **ignored for single images** (a `screenshots.csv` run, or an image given as the input):
  every image is read once, and the tool says so on stderr.

### Detector v2 results on the developer's own clips

Python reference (`detector_ref.py` plus `tracker_ref.py`) on all 5718 frames of 13 clips, 60 labeled names (the labels say who appears in a clip, not in which frames). "v2-fixed" is the current code: crowd exception,
tracker with prediction and identical-read association, `AMMUNITON` on the exclusion list:

| | v1 (replay-v1) | v2, tracker K=3 over 300 ms | v2-fixed | v2-fixed + speed readout (model v3) |
| --- | --- | --- | --- | --- |
| labeled names found | 60/60 | 51/60 | 55/60 | 55/60 |
| detections (v1) / false detections | 50191 / 27424 | n/a (tracks are what counts) | n/a | n/a |
| wrong names confirmed | n/a | 1 (a stable misread: `AMMUNITON`) | 0 | 0 |
| junk nameplate tracks | n/a | 39 (plus 195 stages of the typing animation, 42 unread, 293 excluded or contact code) | 28 (plus 180 animation stages, 38 unread, 94 excluded or contact code) | 29 (plus 189 animation stages, 40 unread, 94 excluded or contact code) |
| nameplate tracks | n/a | 1323 | 574 | 592 |
| median delay to the confirmed name | n/a | 3 frames (0.3 s at 10 fps) | 3 frames | 3 frames |

The 5 labeled names that are still not confirmed: three contact codes (`AB-1234-CD`, `EF-5678-GH`, `JK-9012-LM`: they are single line plates the app ignores by name anyway) and `ZANTHOR` and `PIXEL4821`
(a crowded clip start in front of a station: `ZANTHOR` has one clean read in the whole clip, `PIXEL4821` three clean reads in frames 1 to 3 that span only 200 ms, less than the 300 ms rule asks for).
The two NPC names `VIXEN` and `WARLORD` are now found, but they are on the exclusion list, so the app ignores them.

### Tracker tuning: exact names, stable box, corrections, hyphen rule, 64 characters

Why: a run over every native frame (60 to 120 fps, median 10.7 ms per frame) confirmed 8 wrong names (`UNKNOWK_`, `UNKEOWN`, `UNKNO`, `SECURI`, `FN-84`, `H-LVORIN`, `MARGU1SPELICAM`, `-PIL0TFISH`):
the app will read 5 times a second (planned), a replay of every frame reads 12 to 24 times as often, and a short-lived misread reaches three identical reads inside 300 ms. Use `--fps 5` to mirror the planned rate.
Python reference, 13 clips (5718 frames at 10 fps, 60 labeled names), names compared EXACTLY. **Before** = the branch before this work (look-alikes 0/O and 1/I counted as found, line filter 400 px, names up to 25
characters, no edge rule, a rival that catches up withdraws the confirmation); **after** = this work. The 5 fps rows take every second frame (two phases, A and B), which is what the planned 5 reads a second see.

| 13 clips | before 10 fps | after 10 fps | before 5 fps (A / B) | after 5 fps (A / B) |
| --- | --- | --- | --- | --- |
| labeled names confirmed (exact) | 55/60 | 56/60 | 52 / 52 | 52 / 52 |
| wrong names confirmed (ever) | 1 (`PIL0TFISH`) | 1 (`PIL0TFISH`) | 1 / 1 | 1 / 1 |
| corrections (a confirmed name replaced by another) | 0 | 4 | 0 / 0 | 1 / 0 |
| junk tracks (plus animation stages, unread) | 30 (199, 40) | 28 (193, 48) | 15 (126, 31) / 20 (131, 29) | 15 (120, 37) / 18 (129, 33) |
| median delay from the first read to the confirmation | 300 ms | 300 ms | 400 ms | 400 ms |

The 10 fps run is also the dense stress check (every frame the training kit has). The 4 names still missing at 10 fps: `HV-5192-DB`, `BN-3816-RZ`, `HALVORIN`, `OTTER2231`. The 4 corrections are plates that were reused for
another player (`KT-7301-MN` to `PL-2468-WS`, `CINDER-404` to `ICE_HARRIER`, `T4PIR_220` to `ICE_HARRIER`, ...): the panel now follows. The line filter and name length changes did not raise the junk tracks (28 against 30).

**Native 60 fps** (`name1`, `name3`, `name11`, `name12`, `name13`, all frames of the videos, 34 labeled names):

| 5 clips, 60 fps | before, every frame | after, every frame | before, every 12th frame (5 fps, A / B) | after, every 12th frame (5 fps, A / B) |
| --- | --- | --- | --- | --- |
| labeled names confirmed | 30/34 | 31/34 | 29 / 28 | 29 / 28 |
| wrong names confirmed (ever) / still shown at the end | 7 / 6 | 7 / 4 | 1 / 2 and 1 / 1 | 1 / 2 and 1 / 1 |
| corrections | 0 | 5 | 0 | 1 / 2 |

At 5 fps the only wrong name is `PIL0TFISH` (below). Every frame still confirms 4 wrong names that are never corrected, and the causes are not in the tracker:
- `PIL0TFISH` (`name3`, 5 tracks): the letter model reads the `O` of `PILOTFISH` as `0`, stably. It was hidden while 0/O counted as a look-alike. A training data problem (more samples of `O` and `0` in this font).
- `UNKAOWN` (`name11`): the glitch animation of the excluded word `UNKNOWN` dwells 300 ms on one scrambled stage (8 identical reads at one width); the plate is gone before the real word is read often enough to correct it.
- `FN-84` (`name11`): a typing stage of `FN-8420-TC` that stays 350 ms; the plate is lost before the next stages (`FN-842`) can correct it.
- `MARGU1SPELICAM` (`name13`, a 21 px high close plate of `MARQUISPELICAN`): a stable misread of several letters.
`ConfirmSpanMs` 500 would remove two of them at the price of 2 of 56 names at 10 fps (700: 4; 1000: 9); it is left at 300 because the app will read 5 times a second (planned). `ICE_` and `KEST` (typing stages) were confirmed and then corrected to `ICE_HARRIER` / `KESTREL9`.
Corrections win: a stage that is confirmed wrongly is replaced as soon as the plate shows the real name often enough.

Times per frame depend on the machine (v1 median was 19.9 ms per frame in C#).
Single screenshots without the tracker find fewer names (hard cases: red hostile targets, bright moons, stacked plates, plates without a distance line): 5 of the 17 labeled names are read in one frame
by `detect()` (`evaluate_screenshots_v2.py`), with no wrong name. The letter model v2 reads held-out clips' letters at 98.1 %, the 14 labeled screenshot crops exactly 11/14, and held-out distance lines exactly 98.4 % (the anchor rule passes 98.7 %).

**Speed readout.** A targeted nameplate prints its closing speed after its distance: `9.0km [-29m/s]`, `4.4km [268m/s]`, `15.6km [1km/s]` (lower case `m/s` or `km/s`, a space before `[`,
a minus sign for a closing speed). The distance anchor accepts it: after the unit, **one word gap and a `[`** may follow, and everything from the bracket on is ignored (not read, not scored, not validated;
the speed itself is not used). The line is judged on the pieces before the gap (`DistanceLineRule`, `HasSpeedGroup`). The bracket is recognized by the letter model (model v3 has the two characters `[` and `]`):
the piece after the gap must be read as `[` when the model may choose among **all** of its characters, with a log probability of at least `BracketMinScore` (-0.7). The distance part is still read with the
distance alphabet only, so no digit can be read as a bracket. Rejected, as before: a speed first (`VELOCITY 225M/S (1.12KM)`, `209M/S (59.06KM)`: upper case and parentheses, the part before the gap is not a distance),
a speed group without a distance before it, a gap that is not followed by `[` (a minus sign, text), a second gap before the bracket.

- **Centering.** The game centres the name over the WHOLE line, speed group included (measured on the 13 clips: the centre of the whole line is within 0.5 px of the name centre in every frame where
  the name is complete; the distance part alone is tens of pixels off). The pairing therefore uses the full extent of the line.
- **Joined or split.** Usually the 7 px line joiner puts the whole line into one box. When the gap after the unit is wider (`SpeedJoinGap`, up to 1.5 line heights) and the speed group is a box of its own,
  the box to the right (same row, similar height, at most `SpeedJoinMaxLines` = 8 line heights wide) is joined to the distance box as one extra candidate line (`SpeedUnions`); it can only be the lower line of a pair.
- **Clearance.** The 3 line heights of free space on both sides apply to the whole line: after the `]`, not after the unit.
- **Model v3.** `[` and `]` were added to model v2 by training only two new output units on v2's hidden layer (training kit: `extend_model.py`; samples from Tesseract-labelled speed lines, 68 `[` and 58 `]`).
  Everything else the model knows is unchanged (a full retrain from scratch re-draws every weight; on the clips it gave one stable misread that was then confirmed as a wrong name, `UNKAOWN`, and 12 more junk tracks, none of it related to brackets).
  Held-out clips (`name3`, `name7`): letter accuracy 98.1 % (as v2), 67 of 67 held-out brackets recognized (fitted on the other clips only), 1 of 12 514 other held-out letters read as a bracket; screenshot exam 11/14 as before.
- **Effect on the clips** (Python reference): the labeled names found stay 55/60 and no wrong name is confirmed; `ORBITAL_WREN` (a targeted plate) is confirmed at frame 45 instead of 480, `KESTREL9` has 38 instead of 11 frames with a name
  vote (27 of them on speed lines), `ICE_HARRIER` 190 instead of 123. The price is 18 more nameplate tracks (18 more animation stages, unread tracks and junk tracks in total: the name types in while a plate is targeted) and 1 more junk track.

**Parity fixtures.** `tests/Iris.Core.Tests/Fixtures/detector/` has six synthetic frames (fake nameplates in the game's colours with made-up names, plus decoys: label columns, single lines, speed lines,
a crowded plate, bars, stars; the fifth frame holds nameplates side by side, a plate beside a HUD word pair and a plate beside a ring; the sixth has speed readouts `x.ykm [-nnm/s]`, joined and split into two boxes, and their decoys) and `detector.json` with the stages (boxes, strict, crowd and loose pairs,
evaluations, detections) of the Python reference. `NameplateDetectorTests` compare the C# detector with it.
They were made by a script of the local training kit; like the letter fixtures they check agreement with the reference, not accuracy on real footage.

## Phase 3, step 3: live detection

The overlay now shows REAL detections: IRIS captures the game's screen, reads the names of the nameplates (**detect**), follows each plate from picture to picture (**track**) and draws brackets and the panel from that.
(Step 2, the detector, is described above; this is the third step of Phase 3.)

**Detect, then track.** Reading names (OCR) is slow and happens 5 times a second; a plate moves much faster than that. So the two jobs are separate:
- **Detect** (5 a second, `LiveSettings.DetectionRateHz`): the detector and the name tracker read the scan region. A name is confirmed after 3 identical reads over at least 300 ms. Every plate a detection saw gives the tracker
  a **template** (the colour-masked text of the plate's NAME ROWS only, cut from the very picture that was read; the distance line is left out, its digits change all the time), the plate's position and its velocity.
- **Track** (on every NEW frame of the game, at most `nameplate_tracking_fps` a second: 30 to 90, default 60, or "max" for every frame; see "Setting `nameplate_tracking_fps`" below): for each plate the tracker looks for its template in a small patch of the screen around the **predicted** position (last tracked position plus velocity times the time since).
  The text mask is made with the same top-hat colour rules as the detector's, per plate colour class (cyan/white, red, green; else the detector's combined mask). `Cv2.MatchTemplate` (`TM_CCORR_NORMED`) gives a score; **0.65 or more
  (`PlateTemplateTracker.DefaultMatchThreshold`) means found**: the position is the match (to a fraction of a pixel by a parabola through the peak) and the velocity is the smoothed movement over the last 2 to 3 found ticks (at most 2000 px/s at 1440, scaled).
  A lower score keeps the prediction. A plate **not found for 300 ms** is hidden until a detection finds it again. Tracking never reads or changes a name; **a detection always wins**: it replaces position, template and velocity.
  The nameplate has the same pixel size at any distance, so there is no scale search. At most **24 plates** are matched (the lowest track ids first); any others only move along their velocity.
- **The search patch** is the template's box grown on every side by `max(16, 2000 px/s x t)` pixels at 1440 high, scaled by window height / 1440, clipped to the window, where t is the larger of the time since the last tracked frame (about 67 px at 30 tracked frames a second, 22 px at 90) and the AGE of the position the prediction starts from (a detection's picture, a run of misses), at most 500 ms.
  A detection that arrives after the tracking has found the plate in a NEWER picture keeps the tracked position and velocity (it brings the name, colour, template and box size); one that agrees with the tracked box within 2 px (scaled) keeps the tracked box too, so a still bracket does not hop. Plates without a template count as seen when their detection arrives.
- **What does not change:** names, colours, confirmation, the exclusion and duplicate rules and the panel all still come from the detection (`LiveDetectionEngine`, `LiveSnapshotBuilder`).

**Capture: DXGI Desktop Duplication** (`src/Iris.App/Services/DxgiFrameSource.cs`, behind `IFrameSource` in `Iris.Core/Live`).
- The monitor that holds the game window is duplicated (Windows 8 and later; the game must be borderless, which the overlay already requires). The picture stays on the GPU; only small rectangles are read back: for every tracked frame the
  tracker's **search patches**, and at most every 200 ms also the **scan region** (default x 15 to 85 %, y 10 to 70 % of the game window, plus the little context the detector wants), all out of **one acquired frame** (`CopyHeldFrame`), each into
  its own staging texture (the scan region has a dedicated one, the patches a stable pool) (sized up to a multiple of 64 px so that a patch that changes by a few pixels does not make a new texture) and its own reusable BGR `Mat`. The rest of the screen is never read back.
- **Event driven.** The loop waits in `AcquireNextFrame` (up to 50 ms) for the screen's next NEW picture; there is no timer. A frame counts as new when `AccumulatedFrames` or `LastPresentTime` is not 0 (a wake for the mouse pointer alone is not a frame).
  The frame's time is its `LastPresentTime` (QueryPerformanceCounter ticks) converted to the shared stopwatch clock's milliseconds (`FrameClock`: `(ticks - origin) x 1000 / Stopwatch.Frequency`, the origin taken when the clock started); when it is 0 or not plausible (more than 2 ms later
  than the acquisition, more than 1 s earlier) the clock right after the acquire is used. A frame is **tracked** by a running schedule that meets the cap on average (`TrackingSchedule`: a frame counts when it is not earlier than the next allowed time minus 1 ms of slack, and the next allowed time then moves one period on, never more than one period behind the frame; no cap with Max); any other new frame is released at once and the loop waits again.
  So a game at 144 frames a second with a cap of 60 is tracked on every third frame, and a game at 50 frames a second on every frame. The scan region is copied on a tracked frame, at most every 200 ms and only when the detection thread is free, as before.
- **Still screen.** A wait that times out is not an error: every 50 ms the brackets stay where they are (the plates that were found stay seen) and the previous scan picture is read again when a detection is due. Access lost (mode change, secure desktop,
  another full-screen program), device removed, a changed monitor or an unexpected pixel format release device, duplication and staging textures; the next acquire sets up again (after at most 2 s following a failed setup).
- `DuplicateOutput` always delivers 8 bit B8G8R8A8 (R8G8B8A8 is accepted as well; any other format is reported in the log as unsupported). That holds with HDR on, too: Windows converts the picture, so HDR does not stop
  the capture, but the converted colors can lower the detection quality. If detection is poor, try SDR for the game's monitor.
- New packages: `Vortice.DXGI` 3.6.2 and `Vortice.Direct3D11` 3.6.2 (MIT), in `Iris.App` only. `Iris.Core` still has no Windows or Direct3D code.

**IRIS's own overlay and its own capture.** The brackets and panel colours would be read as screen content (and the brackets would break the detector's clearance check), so IRIS keeps its windows out of its own pictures:
- **Default (setting off): excluded by Windows.** Every overlay window is set to "exclude from capture" (`SetWindowDisplayAffinity` with `WDA_EXCLUDEFROMCAPTURE`, Windows 10 version 2004 or later). Side effect: **screen recorders, OBS, Discord screen share and
  screenshots do not show the overlay either.**
- **"Show overlay in screenshots and recordings"** (Settings > Overlay; `overlay_visible_in_capture = true`, default `false`): ticking the box shows a pop-up (one **Close** button): "Recording software must be set to capture desktop to record the IRIS overlay. If set to capture Star Citizen, the overlay will not be captured."
  (only when you tick it, not when the window loads the saved value; the box stays ticked). The windows use `WDA_NONE`, so recordings and screenshots **do** show the brackets and the panel,
  and **IRIS masks its own drawings in its own captures** instead: the panel and test windows' rectangles are filled black and the outlines of the brackets (stroke plus outline plus 4 px) of the **newest published bracket set and of every set published within 100 ms before the captured frame** are painted
  black (the screen may still show an earlier frame than the newest one; `CaptureExclusion` keeps a thread-safe, time-stamped `PublishedHistory`, and the sets are read right after the frame is acquired). The masks apply to every grabbed region, the tracking patches included. The setting applies to all overlay windows
  at once when you press Save (no restart).
- The same masking is the fallback when Windows refuses the call or the Windows build is older than 19041 (before version 2004, where the call would turn the window into a black box): the log says so. For the brackets window only the outlines are painted
  (its rectangle also holds nameplates).

**Threads and data flow.**
- **Capture and tracking thread** ("IRIS tracking"): the ONLY owner of the screen duplication. For every tracked new frame (see "Event driven" above): take a finished detection if there is one (it wins), plan the patches, grab patches (and, at most every 200 ms, the scan
  region) from that frame, hand the scan picture to the detection thread, match the templates, publish the snapshot. If the detection thread is still busy with the last picture, that slot is skipped (counted in the log): **OCR never blocks tracking**.
- **Detection thread** ("IRIS detection", below normal priority): takes the scan picture from a one-slot mailbox (`DetectionMailbox`: the capture thread copies into the mailbox's buffer only when the thread is idle), runs `LiveDetectionEngine.Process`,
  and posts the `DetectionResult` (snapshot plus the seeds: template, position, velocity of each plate it saw) through a one-slot `DetectionResultSlot`. A result of an earlier run (the game left the front meanwhile) is dropped by its epoch.
- **UI thread (the renderer):** see below. **Lookup task:** `OrgMemberLookupQueue`, one RSI request at a time through the rate-limited client.
- screen -> capture thread -> (copy) mailbox -> detection thread -> (immutable result) slot -> capture thread -> (immutable `LiveSnapshot`) `LiveSnapshotHub` -> UI thread. Nothing else is shared except immutable objects and `LiveStats` (its own lock).
- Detection runs only while the overlay controller says Ready: Star Citizen is the window in front, it is borderless (`attributes.xml`), and the overlay is not disabled. Otherwise nothing is captured or read (the loop only wakes to look at the gate),
  the tracks are forgotten, and after 5 s of idleness the screen duplication is released. **Disable Overlay** (button or hotkey) stops capture and OCR entirely. The test overlay does not start detection.

**Render at monitor refresh.** The brackets window hooks `CompositionTarget.Rendering` while it is shown (and unhooks when it hides): once per frame WPF composes, i.e. at the monitor's refresh rate, it moves each bracket from its last TRACKED position
along its tracked velocity to the expected display time (now plus one refresh period of the display, 16.7 ms when unknown), **at most 50 ms ahead** (`LiveSettings.RenderExtrapolationMs`; still at most 3 box heights, and nothing under 10 px/s), and **redraws only when a bracket moved by a whole pixel**. So the brackets glide at 60, 144 or 240 Hz
while the plates are tracked on up to 30 to 90 (or every) new frames a second, and a stalled tracker never lets them run away (they stop after 50 ms and are hidden after 600 ms without any snapshot). The panel window is redrawn only when its entries or the settings change.
**The refresh period is the one of the GAME's monitor**, not the primary display's: `MonitorFromWindow(game, MONITOR_DEFAULTTONEAREST)`, `GetMonitorInfoW` (MONITORINFOEX: the device name), `EnumDisplaySettingsW(device, ENUM_CURRENT_SETTINGS)` and its `dmDisplayFrequency`, cached per monitor for 5 s; 16.7 ms when it is not known or not plausible (0 and 1 mean "hardware default").
The UI thread is told only about structural changes (brackets appeared or all went, a new panel); movement is found by the render hook itself. A transparent window costs Windows work in proportion to its area, so the live overlay is **two small windows**:
a brackets window sized to the union of the brackets (plus a 64 px margin so small movements do not resize it) and a panel window sized to the panel. The test overlay still uses one window of the whole size.

**Atomic window updates.** The overlay windows are not WPF windows but raw Win32 layered windows (`LayeredWindow`, window styles layered, transparent, tool window, no activate, topmost; never activated, clicks go through). A WPF window is moved and resized by Windows on one step and repainted on another: on a refresh where the window's
rectangle changed (a bracket appeared or went) but the old picture was still shown, every bracket was drawn shifted by the move distance for one frame (measured in a 60 fps recording: all brackets together, up to 692 px, for exactly one frame). Now every update is ONE call, `UpdateLayeredWindow` (`ULW_ALPHA`, `AC_SRC_OVER`, `AC_SRC_ALPHA`), which sets position, size and pixels together:
the same drawing code draws into a `DrawingVisual`, which is rendered at 96 dpi into a `RenderTargetBitmap` of the window's size rounded up to a multiple of 128 pixels (premultiplied BGRA, the unused part cleared), copied into a 32 bit top-down DIB section of the same bucket size (both kept until the bucket changes) that sits in a memory DC; the real size goes to `UpdateLayeredWindow`. An update that fails is tried again at the next present or frame, and after three failures in a row the window is hidden rather than left frozen on the screen.
Display affinity, the capture exclusion and masks (published from the model that was actually shown), hide and show, topmost re-assertion and the whole-pixel redraw skip are as before. Because no overlay window is a WPF window any more, one 1 x 1 pixel transparent click-through WPF window (`RenderKeepAlive`) stays at the game window's corner while the brackets hook is active, so that WPF keeps rendering frames for `CompositionTarget.Rendering`.

**Bracket geometry.** The brackets have ONE fixed size: that of a one-row plate at 1440 (a 41 px high box: bracket height 41 x 1.24, width 0.24 x height, stroke max(2, height x 0.06), dark outline stroke x 0.5), scaled by the game window's height / 1440. They are
**never taller for a multi-row plate**; they are centered vertically on the WHOLE plate box (name rows and distance line) and stand at least **10 px x (height / 1440)** from the text (the widest row; the rows are centered) to the nearest drawn pixel,
half the stroke and the outline included (`BracketMetrics.For(plateBox, gameWindowHeight)`).

**Setting `nameplate_tracking_fps`** in `settings.toml`: **default 60**; a whole number from **30 to 90 in steps of 5**, or the text **`"max"`** (any case). The slider "Nameplate Tracking Tickrate" in Settings > Overlay has the same stops, 30 to 90 and a last one labelled **Max**.
- a number above 90 (older files allowed up to 120) means Max; a number below 30 becomes 30, with a warning in the log; anything else (other text, a fraction, a number that is not on a step of 5) gives the default 60 and a warning;
- Max tracks every new frame of the game, with no cap other than the frames themselves (the game's frame rate, which can be above the monitor's refresh rate); a number is a cap: a new frame is tracked when at least 1000 / number ms (less 1 ms) have passed since the last tracked one.
It is how often each plate is matched against the screen. A higher rate follows fast turns more closely and costs a little more processor and graphics work; the brackets are drawn at the monitor's refresh rate whatever it is.

**The overlay model from tracks** (`LiveSnapshotBuilder`). Brackets for CONFIRMED nameplate tracks only (a name read identically 3 times over at least 300 ms), coloured by the usual rules: `{ }` player list, `[ ]` org list,
`< >` org attribute and No Match. A correction of the confirmed name switches colour and shape on the next detection. The panel entries come from `OverlayModelBuilder` ("Orgs: not looked up" until the cache has the player).
An expired track is gone; a plate the tracker has not found for 300 ms is hidden until a detection finds it, and everything is hidden when nothing at all was published for 600 ms. Names the exclusion list or the contact-code rule ignore get no bracket (the player list wins over both); the
session duplicate rule is fed every detection (see "Duplicate names"). Players who are cached members of an org on the org list but have no cached page are looked up in the background, one at a time, through the
rate-limited RSI client (`OrgMemberLookupQueue`, `CacheRefresher.LookupOrgListMemberAsync`), so they get the org's colour as soon as the page is read.

**Logging.** One line when detection starts, one when it stops, and once a minute while it runs: the reads (count, average and 95th percentile read time, skipped slots, reads without a picture, confirmed tracks) and the tracking
(`tracking: N ticks, average X ms, p95 Y ms, Z % of M matches found`), ending with the frame rates seen in that minute: `game frames ~143/s, tracked ~60/s (cap 60)` (or `no cap` for Max). The game's frame rate is measured from the timestamps of the new frames, `(frames - 1) / (last - first)`.
Right after that line the brackets window adds `Overlay render: N frames in 60 s, R redraws, average X ms per frame` (one line a minute: its statistics are kept while the window hides and shows again, and the line is written only then; it used to be written whenever the brackets window hid, which happens every few seconds in play). Nothing per frame.

**CPU expectations** (estimates, not measured on real hardware yet): a detection of the scan region at 1440p is tens of milliseconds on one core, 5 times a second, on a below normal priority thread. A tracking tick matches one small patch per plate (the plate's name text plus the search margin) and should be well under a millisecond per plate. Look at the minute lines in the log (`average` and `p95` of both) for the real numbers on your machine. Use the replay tool's `--track` on your own clips for the tracking error and the share of frames with a bracket.

Tests: `LiveTrackingTests` (geometry, prediction and the 50 ms render cap, bracket metrics, window fit, stats, snapshot from tracks), `TemplateTrackingTests` (template tracking on synthetic pictures: follow, lose after 300 ms, a detection wins, the cap, colour classes;
the mailbox, the result slot, the published history, the statistics), `LiveEngineTests` (settings, org member queue, the whole pipeline with a fake frame source over the detector fixtures), `TrackSimulationTests` (the replay tool's `--track`), `FrameTimingTests` (the tracking cap, the QPC-to-clock conversion, the game frame rate of the minute line). The window code (`LayeredWindow`, `OverlayWindow`, `DxgiFrameSource`, the event driven loop) is in the app and has no automated tests; check it on a Windows PC with a running game: the minute lines should say `game frames ~` close to the game's frame rate, and the brackets must not jump when one appears or goes.

## Multi-row names, plate colour, duplicate names and the O/0 check

**Multi-row names.** The game wraps a long handle over up to 3 centered rows above the distance line. The detector joins them (a port of
`stack_rows` of the Python reference): rows above the name line within 0.8 line heights of the row below, a pitch of 1.05 to 1.8 line heights,
a similar height, full width (11 line heights or more for every row above the last), centered like the distance line, one word each; the name is the rows
joined, 4 to 64 characters. A fourth row, or a name-like row that is not centered, is a blocker: the plate is still a nameplate but gives no name. A junk line (not one word) above a name is
ignored, the name line is read alone, exactly as before. A single-row plate behaves as it always did. A detection exposes `Rows` (every row box) and `PlateBox` (rows plus distance line).
Fixtures: `tests/Iris.Core.Tests/Fixtures/plates/` (synthetic frames from `make_plate_fixtures.py`, expected values from the Python reference).

**Plate colour.** The ink pixels of all rows are measured (median colour of the brighter half) and classified as `WHITE` (including light cyan), `BLUE`, `GREEN` or `RED`
exactly as `plate_color` / `classify_color` of the reference (`PlateColors`); it is on the detection and on the track. At least 30 ink pixels are needed, otherwise there is no colour.

**Duplicate names.** A player cannot be in two places at once, so one name read on two separate plates means one of the reads is wrong. In order, for every name:
1. a name on the player list, or a member of an org on the org list, is **always shown and never excluded** (org attribute rules such as No Org or Redacted do not count);
2. otherwise only WHITE plates count: when the same name is read on 2 or more non-overlapping white plates by both tracks in the same update, within 3 updates,
   it gets a **session exclusion**: kept in memory, it survives Reset (which happens when the game leaves the front or the overlay is disabled; there is no hotkey) and is gone when IRIS quits;
3. a session-excluded name gets no bracket, no panel entry and no RSI lookup; the log says once per name `Session exclusion: NAME (seen on 2+ white nameplates)`.
Nothing is written to `exclusions.txt` any more (`ExclusionList` keeps user and default entries only).

**O/0 check.** The letter model cannot tell `O` from `0` reliably, so the reader measures the glyph on the screen pixels (names only, never the distance line): the glyph is enlarged 8x, thresholded, and the ratio of the
outer width to height and of the hole width to height decide with a two-feature Gaussian rule whether it is a letter `O` or a zero (`O0Rule`, constants from `model/o0-rule.json`; a verdict needs 95 % probability, otherwise the model's read stays).
`NameplateDetectorSettings.SubPixelO0Rule` is **on by default, on purpose**; the Python reference flag `o0_rule` defaults to off only so that old results can be reproduced. Setting it to false switches the check off. `O0RuleTests` use synthetic glyphs; the test `O0LocalParityTests` compares with real crops from your machine and only runs when the environment variable
`IRIS_O0_FIXTURES` points to the folder (those crops are not in the repository).

## Build and test (Windows)

One-time setup: install the .NET 8 SDK.

```
winget install Microsoft.DotNet.SDK.8
```

Then, from this folder (`IRIS.sln` holds all five projects):

```
dotnet build IRIS.sln
dotnet test IRIS.sln
```

(`dotnet test tests\Iris.Core.Tests` runs only the tests project.)

The first run downloads the packages it needs, which takes about a minute.
Without captured RSI pages, a good result looks like `Passed!  - Failed: 0, Passed: N, Skipped: 7` (the 6 tests that need real RSI pages, see below, plus the local O/0 parity test while `IRIS_O0_FIXTURES` is unset).

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
nameplate_tracking_fps = 60    # at most how often each plate is matched against the screen between name reads: 30 to 90 in steps of 5, or "max" (every new frame); default 60
overlay_visible_in_capture = false   # true = the overlay shows in screenshots and recordings (IRIS masks it in its own captures)
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
