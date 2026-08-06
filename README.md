# Rosetta Modio Generator

An automated C# pipeline designed to crawl mod metadata, manifests, and dependency structures from mod.io for Timberborn, generating structured tab-separated dataset files for consumption by the Rosetta ecosystem.

---

## Key Features

- Incremental Crawling: Uses mod.io API timestamp filtering (date_updated-min) to only pull modified or newly published items.
- Decoupled Architecture: Individual mod metadata and download states are stored per-folder in lightweight local .rosetta_cache.json files.
- Server-Side Tag Filtering: Queries mod.io directly for tags=mod to exclude maps and other non-mod content.
- Resilient Downloader: Processes worker queues concurrently with automatic HTTP 429 rate-limiting handling (respecting Retry-After headers and rolling backoffs).
- Automated GitHub Action: Runs daily via GitHub Actions, caching the downloaded manifest directory and committing refreshed export datasets to data/.

---

## Output Datasets

The pipeline outputs three primary tab-separated (.txt) files into the data/ directory:

1. rosetta_modio.txt: Master metadata export containing Published File IDs, Internal Mod IDs, Names, Titles, Versions, Minimum Game Versions, and Required/Optional Dependencies.
2. rosetta_duplicates_modio.txt: Diagnostic analysis flagging cross-item internal Mod ID conflicts.
3. rosetta_invalid_packages_modio.txt: Diagnostic analysis flagging mod.io items that illegally declare multiple internal Mod IDs across subfolders.

---

## Configuration Files

The crawler requires three local text files placed in the executable directory:

| Filename | Description | Example Content |
| :--- | :--- | :--- |
| modio_gameid.txt | Target game ID on mod.io | 3659 |
| modio_apikey.txt | Your mod.io API key
| modio_userid.txt | Your mod.io user ID

Note: These credentials must remain private. Do not commit these text files to source control.

---

## Running Locally

### Prerequisites
- .NET 10.0 SDK

### Setup & Run
1. Create modio_gameid.txt, modio_apikey.txt, and modio_userid.txt in your project root or output directory.
2. Build and run the project:

Standard incremental update:
dotnet run --configuration Release

Clean run (wipes local data_modio directory before starting):
dotnet run --configuration Release -- -clean

---

## Setting Up GitHub Secrets

To allow the automated GitHub Action workflow (.github/workflows/rosetta_modio.yml) to run without exposing your mod.io API credentials publicly, you must store your configuration as GitHub Repository Secrets.

### Step-by-Step Setup:

1. Navigate to your repository on GitHub.
2. Click on the Settings tab located at the top of the repository.
3. In the left sidebar, navigate to Secrets and variables -> Actions.
4. Click the New repository secret button.
5. Create the following three secrets individually:

| Secret Name | Value |
| :--- | :--- |
| MODIO_GAMEID | Your mod.io Game ID | 3659 |
| MODIO_APIKEY | Your private mod.io API key
| MODIO_USERID | Your mod.io User ID

Once created, the GitHub Action workflow will automatically inject these secrets as temporary files in the build environment during execution, run the generator, and safely remove them when finished.