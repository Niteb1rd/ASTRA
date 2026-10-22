# IRIS

IRIS – Companion App for Star Citizen (formerly ASTRA). First feature: **Social Overlay**.

Windows overlay that brackets the nameplates of players you care about, in colors you choose,
and summarizes their orgs in a side panel. See the requirements doc for the full specification.

## Status

Phase 1, increment 5: the Settings tab. IRIS runs as a tray app with a Social Overlay config window (no overlay or OCR yet). The window now has app settings, import and export of your lists, and an optional hosted config URL.

- `src/Iris.Core`: config file (TOML) read, write and validate; color and bracket rules; right-panel org line (increment 1).
- `src/Iris.Core/Rsi`: reads robertsspaceindustries.com. Pure parsers for the player organizations page, the org page and the
  org members API; an HTTP client that returns Found / NotFound / Failed (never exceptions), allows one request every 3 seconds
  app-wide, only talks to robertsspaceindustries.com, and logs every request (URL, time, outcome).
- `src/Iris.Core/Cache`: SQLite lookup cache (players, their orgs, orgs and their members, "no such citizen" notes), with the
  source of every entry, a 30 or 7 day refresh interval, "Update database now" (list entries only), the hotkey lookup and the
  org-list-member lookup.
- `src/Iris.Core/Detection`: the exclusion list (hand-editable text file plus shipped defaults), the nameplate name filter, and the
  tracker that adds names seen on two separate nameplates at once (increment 3).
- `src/Iris.Core/Storage`: `DataPaths` (the `Data` folder and the files in it) and `SocialListStore` (loads the list file; saves it
  crash-safely through `.tmp`, keeping one `.bak`). `src/Iris.Core/Config/RowValidator.cs` checks a typed handle or SID for the
  config window: format, duplicates, cache first, then one RSI request (increment 4).
- `src/Iris.Core/Settings`: `AppSettings` and `AppSettingsToml` (`Data\settings.toml`). `src/Iris.Core/Config` also holds
  `SocialConfigMerger`, `ConfigImporter` (the three import modes, export) and `HostedConfigClient` (increment 5).
- `src/Iris.App`: the Windows app (WPF): tray icon, and the config window with the Players, Orgs, Org-Attribute and Settings tabs.
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
```

Unknown keys are noted in the log and ignored. A file that is not valid TOML is not changed until you save; until then the defaults
apply and IRIS tells you; a background change (such as the remembered hosted version) is then kept in memory only, and the file is
written when you press Save. A hash that is not 64 hex characters is cleared with a note. Turning the log file off stops writing lines to it; the in-memory log keeps working, and files older than
30 days are still deleted at startup.

**Update database now** (tray menu and Settings tab, same action) first looks at the hosted config URL, if one is set, and then
refetches your saved players and orgs from RSI.

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
  The usual `.bak` of the previous version is kept. A current file with errors cannot be merged (choose Backup or Replace).
- **Replace existing config**: the imported config overwrites the list file (the usual `.bak` keeps the previous version).

With no list file yet, the import just asks for confirmation and writes the file. Afterwards the window shows the new rows, every
name is checked against RSI, and the status bar says what was added.

### Hosted config URL

Put the address of your own config file (for example a raw file in your own repository or a web space) into **Hosted config URL**.
Rules IRIS follows when it fetches it: `https://` only, no user name or password in the address; one GET, 15 seconds, at most 1 MB;
redirects are followed by hand only within the same site, same port (up to 3), a redirect to another site is refused with a message;
every request URL, refused ones too, is logged without any user name or password; a failed fetch is never remembered.

When you press **Update database now** and the hosted file differs from the last one you applied or declined (compared by SHA-256),
IRIS shows the same import choices with the heading "Your hosted config has changed". If you import it, choose Cancel, or the file
has problems, that version is remembered and you are not asked again until the file changes. If the import could not happen (your
changes could not be saved, the list file could not be written, another import was running) the version is not remembered and it is
offered again next time. If the fetch fails, the result message says so and the RSI refresh still runs. **Import from URL** does the same fetch on demand and, when you import, remembers the address and the version.

### Export

**Export...** writes the lists as last saved to a file (default name `iris-config.toml`). If the window has unsaved list changes you
are asked whether to save the lists first (Yes; settings edits stay unsaved), export the saved version (No) or stop (Cancel). A file that
cannot be written (for example a read-only USB stick) is reported in the status bar and a message. The file contains only the lists, never settings.

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
