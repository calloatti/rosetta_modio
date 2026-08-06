# AGENTS.md

## Project Overview

**Rosetta Modio Generator** is a .NET 10 console application (`net10.0`, x64) that crawls mod.io for Timberborn mods, incrementally downloads them, extracts their `manifest.json` files, and exports tab-separated (.txt) datasets for the Rosetta ecosystem. No external NuGet packages are used — only the .NET SDK.

The pipeline runs in 4 phases orchestrated from `Main`:
1. **Verify config** — check for required local credential files.
2. **Crawl** — query the mod.io API (`tags=mod`, incremental via `date_updated-min`), flag changed/new mods for download.
3. **Download** — concurrent worker queue downloads zips and extracts only `manifest.json`.
4. **Export** — parse manifests and write three TSV output files.

## Commands

```sh
dotnet restore              # Restore dependencies
dotnet build                # Build
dotnet run --configuration Release        # Standard incremental run
dotnet run --configuration Release -- -clean   # Wipe local data_modio/ first
```

There is no test suite in this repository.

## Architecture / Key Files

| File | Responsibility |
| :--- | :--- |
| `Source/Program.cs` | Entry point (`Program.Main`), orchestrates the 4 phases, handles `-clean` flag. |
| `Source/LogService.cs` | Static logger: writes to console and `rosetta_modio.log` (deleted at startup). |
| `Source/ModioCrawlerService.cs` | mod.io API crawl. Incremental `date_updated-min` filter, server-side `tags=mod` filter, 2 GB file-size cap, HTTP 429 handling with `Retry-After`/backoff. Defines `CachedModEntry` model. |
| `Source/ModioDownloaderService.cs` | 4-worker concurrent downloader with retry/backoff. Extracts only `manifest.json` entries, enforces 2 GB limit, marks cache `Downloaded=true`. |
| `Source/ModioManifestProcessorService.cs` | Reads `manifest.json` + `.rosetta_cache.json` per folder, writes the three TSV exports. |
| `.github/workflows/rosetta_modio.yml` | Daily (00:00 UTC) GitHub Action: injects secrets, runs generator, commits refreshed exports. |

## Data Flow & Conventions

- **Local cache:** each mod lives in `data_modio/<modId>/` under the build output (`bin/Release/net10.0/data_modio/`) with a `.rosetta_cache.json` file (model `CachedModEntry`: `FileId`, `DateUpdated`, `Downloaded`, `Title`, `Creator`, `DownloadUrl`).
- The `Downloaded` flag drives incremental state: the crawler skips mods whose cached `FileId` matches and `Downloaded=true`.
- **Runtime config:** required local files in the executable directory — `modio_gameid.txt`, `modio_apikey.txt`, `modio_userid.txt`. Missing/empty config exits with code 1.
- **Output datasets** (TSV, written to build output, committed from there):
  - `rosetta_modio.txt` — master metadata export.
  - `rosetta_duplicates_modio.txt` — internal Mod ID conflicts across mod.io items.
  - `rosetta_invalid_packages_modio.txt` — items illegally declaring multiple internal Mod IDs.
- **Commits:** the GitHub Action copies `rosetta*.txt` into the top-level `data/` directory and commits only `data/rosetta*.txt`. `data_modio/` is a local cache inside `bin/` and is never committed.

## Critical Warnings

- **Never commit or log credentials.** `modio_gameid.txt`, `modio_apikey.txt`, `modio_userid.txt` are gitignored and must remain private.
- Always keep API keys redacted when logging URLs — see `SanitizeUrl` in `ModioCrawlerService.cs`.
- In CI, secrets (`MODIO_GAMEID`, `MODIO_APIKEY`, `MODIO_USERID`) are injected as temp files and removed in a `finally`-style step (`if: always()`).

## Code Style

- Block namespaces (not file-scoped), 2-space indentation, `using` directives at top.
- Classes/services PascalCase; private fields camelCase (`_camelCase`).
- All logging goes through `LogService.Log()` with bracketed category prefixes, e.g. `[Network]`, `[Rate Limit]`, `[Warning]`, `[Fatal Error]`.
- Hard limits are `const` (e.g., `MaxAllowedSizeBytes = 2L * 1024 * 1024 * 1024`).
- Models are plain classes with auto-properties and default values (`= ""`, `= new()`).
- JSON parsing uses `System.Text.Json` with try/catch fallbacks; manifests tolerate comments and trailing commas.
