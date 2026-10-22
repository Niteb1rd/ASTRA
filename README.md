# ASTRA

Star Citizen helper overlay. First feature: **Social Overlay**.

Windows overlay that brackets the nameplates of players you care about, in colors you choose,
and summarizes their orgs in a side panel. See the requirements doc for the full specification.

## Status

Phase 1, increment 4: the first window. ASTRA now runs as a tray app with a Social Overlay config window (no overlay or OCR yet).

- `src/Astra.Core`: config file (TOML) read, write and validate; color and bracket rules; right-panel org line (increment 1).
- `src/Astra.Core/Rsi`: reads robertsspaceindustries.com. Pure parsers for the player organizations page, the org page and the
  org members API; an HTTP client that returns Found / NotFound / Failed (never exceptions), allows one request every 3 seconds
  app-wide, only talks to robertsspaceindustries.com, and logs every request (URL, time, outcome).
- `src/Astra.Core/Cache`: SQLite lookup cache (players, their orgs, orgs and their members, "no such citizen" notes), with the
  source of every entry, a 30 or 7 day refresh interval, "Update database now" (list entries only), the hotkey lookup and the
  org-list-member lookup.
- `src/Astra.Core/Detection`: the exclusion list (hand-editable text file plus shipped defaults), the nameplate name filter, and the
  tracker that adds names seen on two separate nameplates at once (increment 3).
- `src/Astra.Core/Storage`: `DataPaths` (the `Data` folder and the files in it) and `SocialListStore` (loads the list file; saves it
  crash-safely through `.tmp`, keeping one `.bak`). `src/Astra.Core/Config/RowValidator.cs` checks a typed handle or SID for the
  config window: format, duplicates, cache first, then one RSI request (increment 4).
- `src/Astra.App`: the Windows app (WPF): tray icon, and the config window with the Players, Orgs and Org-Attribute tabs
  (the Settings tab is a placeholder). It has no automated tests yet; everything testable lives in `Astra.Core`.
- `tests/Astra.Core.Tests`: automated tests for all of the above. Tests that need real RSI pages are skipped until you capture them.
- `tools/Astra.CaptureFixtures`: saves a few real RSI pages for those tests.

The page parsers were written without access to RSI's real pages. Their CSS selectors and label texts are guesses, collected as
named constants at the top of each parser. Capture the fixtures and run the tests to find out which guesses are right.

## Player handles and the exclusion list

A player handle is 4 to 25 characters: letters, digits, `-` and `_` only. Anything else read from a nameplate is ignored
(and a player list entry that breaks the rule is rejected). The limits are fixed in the program (`RsiNames.MinHandleLength` and
`MaxHandleLength`).

The exclusion list is a plain text file, `exclusions.txt`: names that appear on screen but are not players (stars, planets, stations...).
One name per line, upper/lower case does not matter, blank lines are ignored, and a line starting with `#` is a comment.
You can edit it by hand while ASTRA runs; changes are picked up automatically. The first time ASTRA runs it creates the file
from the shipped list, `src/Astra.Core/Detection/exclusions.default.txt`. In the app it lives in the `Data` folder (see "Run the app").

- A nameplate whose name is on the list is never matched or looked up, except when the name is also on your own player list
  (your player list wins). Org-list members and hotkey lookups follow the exclusion list with no such exception.
- ASTRA also learns: a player cannot be in two places at once, so a name seen on two separate nameplates in 3 different frames
  (counted across sessions) is added to the end of the file, after a comment line `# auto-added <date> UTC: <reason>`.
  Delete those two lines if the name was added by mistake, or put the name on your player list.
  ASTRA only ever appends to the file; it never rewrites your own lines.
- The frame counts are kept in the lookup cache database (cache schema version 2; an older cache file is upgraded automatically).

## Build and test (Windows)

One-time setup: install the .NET 8 SDK.

```
winget install Microsoft.DotNet.SDK.8
```

Then, from this folder:

```
dotnet test tests\Astra.Core.Tests
```

The first run downloads the packages it needs, which takes about a minute.
Without captured RSI pages, a good result looks like `Passed!  - Failed: 0, Passed: N, Skipped: 6`.
The 6 skipped tests are the ones that need real RSI pages (see below).

## Run the app (Windows)

```
dotnet build src\Astra.App
dotnet run --project src\Astra.App
```

ASTRA starts in the system tray (bottom right, maybe under the `^` arrow). Double-click the icon, or right-click it and choose
"Settings...", to edit your lists. Only one copy can run at a time. Closing the window keeps ASTRA running; use "Quit" in the tray menu.

Everything ASTRA stores lives in a `Data` folder next to `ASTRA.exe` (when run from the build output:
`src\Astra.App\bin\Debug\net8.0-windows\Data`): `social-overlay.toml` (your lists, plus `.bak` of the previous save),
`cache.db` (what ASTRA learned from RSI), `exclusions.txt` and `logs\` (used by a later update).
Delete the folder to start fresh. `.gitignore` ignores every `Data/` folder (it is your personal data) and `bin/`/`obj/`;
it must not ignore the source of `src\Astra.App`, and it does not.

## Capture real RSI pages (needed for the parser tests)

Run once, from this folder (it makes about 6 requests to robertsspaceindustries.com, 3 seconds apart):

```
dotnet run --project tools\Astra.CaptureFixtures
```

It saves the pages into `tests\Astra.Core.Tests\Fixtures\rsi\`. Those files are copies of RSI's pages, so they are
git-ignored and stay on your PC. Then run the tests again:

```
dotnet test tests\Astra.Core.Tests
```

This time the 6 fixture tests run instead of being skipped. If one fails, the failure message says which page and which
expectation; send it back along with the failing page so the parser's selectors can be fixed.

## License

GNU General Public License v3.0. See `LICENSE`.

## Disclaimer

Unofficial fan tool. Not affiliated with or endorsed by Cloud Imperium Games.
Star Citizen® is a registered trademark of Cloud Imperium Rights LLC.
