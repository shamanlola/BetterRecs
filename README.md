# BetterRecs — Smarter Recommendations for Jellyfin

[![Build Plugin](https://github.com/shamanlola/BetterRecs/actions/workflows/build.yml/badge.svg)](https://github.com/shamanlola/BetterRecs/actions/workflows/build.yml)
[![Latest Release](https://img.shields.io/github/v/release/shamanlola/BetterRecs)](https://github.com/shamanlola/BetterRecs/releases/latest)
[![License](https://img.shields.io/github/license/shamanlola/BetterRecs)](LICENSE)

BetterRecs replaces Jellyfin's built-in **Similar Items** with a multi-dimensional weighted scoring engine that matches on genres, tags, community rating, parental rating, release year, and cast/crew. It also exposes a **`/BetterRecs/Recommendations`** API that serves personalised *"Because you watched …"* rows per user.

When the [Home Screen Sections (HSS)](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) plugin is installed, BetterRecs automatically registers a blended **"Recommended for You"** row on the Jellyfin home screen, combining matches from several recently-watched titles.

> **Requires:** Jellyfin 10.11 · .NET 9

---

## Features

- Drop-in replacement for the built-in Similar Items algorithm
- Multi-dimensional weighted scoring (genres, tags, ratings, year, cast/crew)
- Per-user personalised *"Because you watched …"* rows via REST API
- Optional blended **"Recommended for You"** home-screen row via HSS integration, rebuilt on every refresh
- Fully configurable through the Jellyfin dashboard

---

## Installation

### Option 1 — Plugin Repository (recommended)

1. In the Jellyfin dashboard go to **Administration → Plugins → Repositories**.
2. Add the following repository URL:
   ```
   https://raw.githubusercontent.com/shamanlola/BetterRecs/main/manifest.json
   ```
3. Go to **Catalogue**, find **BetterRecs**, and click **Install**.
4. Restart Jellyfin when prompted.

### Option 2 — Manual install from GitHub Releases

1. Download the latest `BetterRecs_x.x.x.x.zip` from the [Releases page](https://github.com/shamanlola/BetterRecs/releases/latest).
2. Extract the zip and copy the resulting `BetterRecs/` folder into your Jellyfin plugins directory:

   | Install type | Plugins directory |
   |---|---|
   | Linux (native) | `~/.local/share/jellyfin/plugins/` or `/var/lib/jellyfin/plugins/` |
   | Docker / Unraid | `<jellyfin appdata>/plugins/` (e.g. `/config/plugins/` inside the container) |
   | Windows | `%APPDATA%\Jellyfin\plugins\` |
   | macOS | `~/.local/share/jellyfin/plugins/` |

3. Restart the Jellyfin server.
4. Open **Dashboard → Plugins** to confirm BetterRecs loaded, then configure it.

   Check the server log for: `BetterRecs vX.X.X.X loaded` to confirm the plugin is active.

---

## Configuration

After installation, go to **Dashboard → Plugins → BetterRecs** to configure:

- Enable/disable the plugin
- Adjust similarity weights (genres, tags, rating, year, cast/crew)
- Configure the home-screen **"Recommended for You"** row: title, number of items, and how many recently-watched titles are blended into it
- Toggle **Regenerate on each refresh** so the row re-picks its sources every time the home screen loads
- Enable watched-item recommendations

---

## Home Screen Sections (HSS) Integration

If the [Home Screen Sections](https://github.com/IAmParadox27/jellyfin-plugin-home-sections) plugin is also installed, BetterRecs automatically registers a single blended **"Recommended for You"** row on the home screen. The row combines the top matches from several titles you recently watched, merged and interleaved into one list. Its contents are rebuilt on every request, so it refreshes along with the home screen (with **Regenerate on each refresh** enabled, it re-picks its source titles each time).

BetterRecs registers the row with HSS via the plugin's supported reflection interface — there is no hard dependency, so BetterRecs loads fine whether or not HSS is present. Enable the section in HSS's home-screen settings after restarting Jellyfin.

> **Note:** Like all HSS sections, this row only appears in the Jellyfin **Web UI** (and apps built on it). Native clients such as Swiftfin and Infuse won't show it — but the enhanced Similar Items still work everywhere.

---

## API

BetterRecs exposes a REST endpoint for building custom clients or integrations:

```
GET /BetterRecs/Recommendations
```

Returns personalised *"Because you watched …"* sections for the current user. See the Swagger UI (`/api-docs/swagger`) on your Jellyfin server for the full schema.

---

## Creating a Release

Push a version tag to trigger the release workflow — it will build the plugin, create a GitHub Release, and attach the zip and checksums automatically:

```bash
git tag v2.1.0
git push origin v2.1.0
```

The release appears under the [Releases](https://github.com/shamanlola/BetterRecs/releases) tab once the workflow finishes.

---

## Build from Source

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

```bash
# Clone the repository
git clone https://github.com/shamanlola/BetterRecs.git
cd BetterRecs

# Build and package (outputs dist/BetterRecs/ and BetterRecs_x.x.x.x.zip)
./build.sh

# Build and install directly to a local Jellyfin instance
./build.sh --install
```

Or build with `dotnet` directly:

```bash
dotnet build Jellyfin.Plugin.BetterRecs.csproj -c Release
```

---

## License

Distributed under the [GPLv3 License](LICENSE).
