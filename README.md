# YouTubeSync – Jellyfin Plugin

A minimal Jellyfin plugin that integrates YouTube channels and playlists into your library
via **yt-dlp**, without pre-downloading any content.

## How it works

1. **Sync task** – runs on a schedule (default every 6 h) and creates one sub-folder per
   configured source inside your library path.  Each folder contains:
   - `tvshow.nfo` (for channels / series playlists) or `movie.nfo` (for movie-mode playlists)
   - `<VideoTitle>.strm` – points to the built-in resolver endpoint
   - `<VideoTitle>.nfo` – episode metadata from yt-dlp's flat-playlist output

2. **Simple playback** – the plugin asks `yt-dlp` for a playable YouTube URL, caches it briefly,
   and hands it to Jellyfin. This is the lightest setup and works without `ffmpeg`.

3. **Enhanced playback (optional)** – when enabled, the plugin first tries to create a cleaner,
   disk-backed local HLS stream using `ffmpeg` and higher-quality YouTube inputs. If that cannot
   start for any reason, playback automatically falls back to Simple playback.

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
| Fallback playback preference | `Compatibility first` | Used by Simple playback and as a fallback when Enhanced playback is unavailable |
| Max videos per source | `200` | Maximum number of videos to sync per channel or playlist (0 = unlimited) |
| Playback mode | `Simple mode` | `Simple mode` works without ffmpeg. `Enhanced mode` creates a local ffmpeg-backed stream first and falls back automatically if needed |
| ffmpeg path | `ffmpeg` | Path to the ffmpeg binary used by Enhanced mode |
| Video encoder | `Software` | Encoder used by Enhanced mode (`Software`, `Intel Quick Sync`, `NVIDIA NVENC`, `VAAPI`, `AMD AMF`) |
| Stop unused enhanced streams after | `2` min | Idle timeout before an Enhanced mode stream is stopped and deleted |
| Concurrent enhanced streams | `2` | Maximum number of Enhanced mode playbacks before new requests fall back to Simple mode |

### Fallback playback preference

| Target | Behavior |
|---|---|
| Compatibility first | Best default for mixed clients. Prefers simpler streams that are more likely to play cleanly everywhere. |
| Balanced | Aims for a good middle ground between compatibility and quality. |
| Highest single-stream quality | Asks YouTube for the best single playable stream it can provide. Highest quality, lowest predictability. |

### Playback mode guidance

- Use Simple mode if you want the lightest setup and do not want to depend on ffmpeg.
- Use Enhanced mode if you want sharper, more consistent playback on devices that benefit from a locally generated stream.
- Enhanced mode is best-effort. If ffmpeg or the selected encoder cannot start, playback automatically falls back to Simple mode.
- The first Enhanced mode profile normalizes playback to a local HLS stream using H.264 video and AAC audio for broad compatibility.

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

- Broad compatibility mode is intentionally conservative and may cap many videos at 720p to keep playback stable across more Jellyfin clients.
- Progressive H.264/AAC streams are typically available only up to 720 p on YouTube; 1080p
   progressive is rare, so 1080p and higher targets often resolve to manifest-based playback URLs instead.
- Managed transcoding currently ships as one universal 1080p HLS profile. It is intentionally narrow to keep the default lightweight path untouched and to make failure fall back cleanly.
- No cookie support – age-restricted or member-only videos will not resolve.

