# ASTRA

Star Citizen helper overlay. First feature: **Social Overlay**.

Windows overlay that brackets the nameplates of players you care about, in colors you choose,
and summarizes their orgs in a side panel. See the requirements doc for the full specification.

## Status

Phase 1, increment 1: core logic only (no windows yet).

- `src/Astra.Core`: config file (TOML) read, write and validate; color and bracket rules; right-panel org line.
- `tests/Astra.Core.Tests`: automated tests for all of the above.

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
A good result ends with a line like `Passed!  - Failed: 0, Passed: 16`.

## License

GNU General Public License v3.0. See `LICENSE`.

## Disclaimer

Unofficial fan tool. Not affiliated with or endorsed by Cloud Imperium Games.
Star Citizen® is a registered trademark of Cloud Imperium Rights LLC.
