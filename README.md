# YouTubeSync – Jellyfin Plugin

A minimal Jellyfin plugin that integrates YouTube channels and playlists into your library
via **yt-dlp**, without pre-downloading any content.

## How it works

1. **Sync task** – runs on a schedule (default every 6 h) and creates one sub-folder per
   configured source inside your library path.  Each folder contains:
   - `tvshow.nfo` (for channels / series playlists) or `movie.nfo` (for movie-mode playlists)
   - `<VideoTitle>.strm` – points to the built-in resolver endpoint
   - `<VideoTitle>.nfo` – episode metadata from yt-dlp's flat-playlist output

2. **Resolver endpoint** – `GET /YouTubeSync/resolve/{videoId}`  
   Calls `yt-dlp -J`, selects the best **progressive MP4 / H.264 / AAC ≤1080p** stream,
   caches the CDN URL for a configurable number of minutes, and returns an **HTTP 302**
   redirect.  If only DASH streams are available, it returns **503** (DASH proxy is not
   supported in v1).

## Requirements

| Dependency | Notes |
|---|---|
| Jellyfin | 10.11.6 |
| yt-dlp | must be on PATH inside the container (or configure full path in plugin settings) |
| .NET SDK | 9.0 (build only) |

## Building

```bash
dotnet publish Jellyfin.Plugin.YouTubeSync/Jellyfin.Plugin.YouTubeSync.csproj \
  -c Release \
  --no-self-contained \
  -o publish/
```

The output folder will contain `Jellyfin.Plugin.YouTubeSync.dll`.

## Manual deployment

```bash
PLUGIN_DIR="/config/plugins/YouTubeSync"
mkdir -p "$PLUGIN_DIR"
cp publish/Jellyfin.Plugin.YouTubeSync.dll "$PLUGIN_DIR/"
cp Jellyfin.Plugin.YouTubeSync/meta.json   "$PLUGIN_DIR/"
```

Restart Jellyfin.  The plugin will appear under **Dashboard → Plugins**.

## Adding as a Jellyfin plugin repository

The `manifest.json` at the root of this repository is automatically updated on every tagged
release by the included GitHub Actions workflow.

Add the following URL in Jellyfin under
**Dashboard → Plugins → Repositories → +**:

```
https://raw.githubusercontent.com/kingschnulli/jellyfin-youtube-plugin/main/manifest.json
```

You can then install / update the plugin directly from the Jellyfin UI.

## Automated releases (CI)

Push a version tag to trigger a release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

The workflow (`.github/workflows/release.yml`) will:
1. Build and publish the plugin.
2. Package `Jellyfin.Plugin.YouTubeSync.dll` + `meta.json` into a ZIP.
3. Create a GitHub Release with the ZIP attached.
4. Update `manifest.json` with the new version entry and push it back to `main`.

## Configuration

Open **Dashboard → Plugins → YouTubeSync → Settings** after installation.  All settings are
managed through the UI — no manual file editing is required.

### General settings

| Setting | Default | Description |
|---|---|---|
| yt-dlp executable path | `yt-dlp` | Path to the yt-dlp binary (must be on PATH or provide the full path) |
| Library base path | `/media/youtube` | Root folder inside a Jellyfin library where .strm/.nfo files are written |
| Jellyfin base URL | `http://localhost:8096` | Externally accessible Jellyfin URL written into `.strm` resolver links — **set this to your public URL** when clients access Jellyfin remotely |
| CDN URL cache duration | `5` min | How long a resolved CDN URL is cached in memory before being re-fetched |
| Max videos per source | `200` | Maximum number of videos to sync per channel or playlist (0 = unlimited) |

### Adding a source (channel or playlist)

Click **+ Add Source** on the settings page.  Each source requires:

| Field | Description |
|---|---|
| Channel / Playlist ID | YouTube channel ID (e.g. `UCxxxxxxxxxxxxxxxxxxxxxx`) or playlist ID (e.g. `PLxxxxxxxxxxxxxxxxxxxxxx`) |
| Display name | Used as the folder name inside your Jellyfin library |
| Source type | `Channel` or `Playlist` |
| Library mode | `Series` — videos appear as TV-show episodes; `Movies` — each video appears as an individual film |
| Description | Optional text written into the source `.nfo` file |

Click **Save** after adding or modifying sources.

## Adjusting for other Jellyfin versions

The plugin targets **`targetAbi: 10.11.6.0`**.  To run on a different version:

1. Change the `<PackageReference>` versions in `Jellyfin.Plugin.YouTubeSync.csproj`
   to match your Jellyfin version.
2. Update `"targetAbi"` in `Jellyfin.Plugin.YouTubeSync/meta.json`.
3. Rebuild and redeploy.

## Known limitations (v1)

- DASH-only videos (no progressive stream) cannot be played – the resolver returns 503.
- Progressive H.264/AAC streams are typically available only up to 720 p on YouTube;
  1080p progressive is rare but will be selected when present.
- No cookie support – age-restricted or member-only videos will not resolve.
- No DASH proxy / mux – split audio+video streams are intentionally rejected.

