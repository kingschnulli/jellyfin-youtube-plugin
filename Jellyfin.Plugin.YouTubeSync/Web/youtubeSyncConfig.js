/* Must match Plugin.Id in Plugin.cs */
const pluginUniqueId = '55a3502b-b6b2-4a3c-93d7-f3c4e7b1e0d5';

export default function (view) {
    let sources = [];
    let editIndex = -1;

    /* ── helpers ─────────────────────────────────────────── */

    function escapeHtml(str) {
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    /** Detect whether a YouTube URL refers to a playlist or a channel. */
    function detectSourceType(url) {
        if (!url) return 'Channel';
        return /[?&]list=/.test(url) ? 'Playlist' : 'Channel';
    }

    /* ── sources list rendering ──────────────────────────── */

    function renderSources() {
        const list = view.querySelector('#sourcesList');
        if (sources.length === 0) {
            list.innerHTML = '<p class="fieldDescription">No channels or playlists configured yet. Click <strong>+ Add Channel or Playlist</strong> to add your first one.</p>';
            return;
        }

        list.innerHTML = sources.map(function (s, i) {
            const contentType = s.Type === 'Playlist' ? 'Playlist' : 'Channel';
            const appearance = s.Mode === 'Movies' ? 'Separate movies' : 'Episodes in a series';
            return '<div class="listItem listItem-border" style="display:flex;align-items:center;padding:.75em 1em;gap:1em;">'
                + '<div style="flex:1;min-width:0;">'
                + '<div style="font-weight:600;">' + escapeHtml(s.Name || s.Id) + '</div>'
                + '<div class="fieldDescription" style="margin:0;">'
                + escapeHtml(contentType) + ' &bull; ' + escapeHtml(appearance) + ' &bull; '
                + escapeHtml(s.Id)
                + '</div>'
                + (s.Description
                    ? '<div class="fieldDescription" style="margin:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">'
                      + escapeHtml(s.Description) + '</div>'
                    : '')
                + '</div>'
                + '<button is="emby-button" type="button" class="raised" data-editidx="' + i + '">Edit</button>'
                + '<button is="emby-button" type="button" class="raised" data-delidx="' + i + '">Delete</button>'
                + '</div>';
        }).join('');

        list.querySelectorAll('[data-editidx]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                openEditForm(parseInt(this.dataset.editidx, 10));
            });
        });

        list.querySelectorAll('[data-delidx]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                sources.splice(parseInt(this.dataset.delidx, 10), 1);
                renderSources();
            });
        });
    }

    /* ── inline add / edit form ──────────────────────────── */

    function openEditForm(index) {
        editIndex = index;
        const s = index >= 0
            ? sources[index]
            : { Id: '', Name: '', Type: 'Channel', Mode: 'Series', Description: '' };

        view.querySelector('#editSourceTitle').textContent = index >= 0 ? 'Edit Channel or Playlist' : 'Add Channel or Playlist';
        view.querySelector('#editSourceUrl').value = s.Id;
        view.querySelector('#editSourceName').value = s.Name;
        view.querySelector('#editSourceType').value = s.Type;
        view.querySelector('#editSourceMode').value = s.Mode;
        view.querySelector('#editSourceDescription').value = s.Description;

        view.querySelector('#sourceEditSection').style.removeProperty('display');
        view.querySelector('#sourceEditSection').scrollIntoView({ behavior: 'smooth' });
    }

    function closeEditForm() {
        view.querySelector('#sourceEditSection').style.display = 'none';
        editIndex = -1;
    }

    /* ── load / save configuration ───────────────────────── */

    view.addEventListener('viewshow', function () {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
            view.querySelector('#YtDlpPath').value = config.YtDlpPath || 'yt-dlp';
            view.querySelector('#LibraryBasePath').value = config.LibraryBasePath || '/media/youtube';
            view.querySelector('#JellyfinBaseUrl').value = config.JellyfinBaseUrl || 'http://localhost:8096';
            view.querySelector('#CacheMinutes').value = config.CacheMinutes != null ? config.CacheMinutes : 5;
            view.querySelector('#PlaybackTarget').value = config.PlaybackTarget || 'BroadCompatibility720p';
            view.querySelector('#VideoRetentionDays').value = config.VideoRetentionDays != null ? config.VideoRetentionDays : 300;
            view.querySelector('#AllowManagedTranscoding').value = String(config.AllowManagedTranscoding === true);
            view.querySelector('#FfmpegPath').value = config.FfmpegPath || 'ffmpeg';
            view.querySelector('#ManagedTranscodeHardwareMode').value = config.ManagedTranscodeHardwareMode || 'None';
            view.querySelector('#ManagedTranscodeSessionIdleMinutes').value = config.ManagedTranscodeSessionIdleMinutes != null ? config.ManagedTranscodeSessionIdleMinutes : 2;
            view.querySelector('#MaxConcurrentManagedTranscodes').value = config.MaxConcurrentManagedTranscodes != null ? config.MaxConcurrentManagedTranscodes : 2;
            sources = config.Sources || [];
            renderSources();
            Dashboard.hideLoadingMsg();
        }).catch(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.processErrorResponse({ statusText: 'Failed to load plugin configuration.' });
        });
    });

    view.querySelector('#saveBtn').addEventListener('click', function () {
        const config = {
            YtDlpPath: view.querySelector('#YtDlpPath').value.trim(),
            LibraryBasePath: view.querySelector('#LibraryBasePath').value.trim(),
            JellyfinBaseUrl: view.querySelector('#JellyfinBaseUrl').value.trim(),
            CacheMinutes: parseInt(view.querySelector('#CacheMinutes').value, 10) || 5,
            PlaybackTarget: view.querySelector('#PlaybackTarget').value,
            VideoRetentionDays: parseInt(view.querySelector('#VideoRetentionDays').value, 10) || 0,
            AllowManagedTranscoding: view.querySelector('#AllowManagedTranscoding').value === 'true',
            FfmpegPath: view.querySelector('#FfmpegPath').value.trim(),
            ManagedTranscodeHardwareMode: view.querySelector('#ManagedTranscodeHardwareMode').value,
            ManagedTranscodeSessionIdleMinutes: parseInt(view.querySelector('#ManagedTranscodeSessionIdleMinutes').value, 10) || 2,
            MaxConcurrentManagedTranscodes: parseInt(view.querySelector('#MaxConcurrentManagedTranscodes').value, 10) || 2,
            Sources: sources
        };

        Dashboard.showLoadingMsg();
        ApiClient.updatePluginConfiguration(pluginUniqueId, config).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
        }).catch(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.processErrorResponse({ statusText: 'Failed to save plugin configuration.' });
        });
    });

    /* ── source add / edit events ────────────────────────── */

    view.querySelector('#addSourceBtn').addEventListener('click', function () {
        openEditForm(-1);
    });

    // Auto-detect source type when the URL field changes.
    view.querySelector('#editSourceUrl').addEventListener('input', function () {
        const detected = detectSourceType(this.value.trim());
        view.querySelector('#editSourceType').value = detected;
    });

    view.querySelector('#saveSourceBtn').addEventListener('click', function () {
        const id = view.querySelector('#editSourceUrl').value.trim();

        if (!id) {
            Dashboard.processErrorResponse({ statusText: 'A channel or playlist link is required.' });
            return;
        }

        function commitSource(name, description) {
            const src = {
                Id: id,
                Name: name,
                Type: view.querySelector('#editSourceType').value,
                Mode: view.querySelector('#editSourceMode').value,
                Description: description
            };

            if (editIndex >= 0) {
                sources[editIndex] = src;
            } else {
                sources.push(src);
            }

            closeEditForm();
            renderSources();
        }

        const name = view.querySelector('#editSourceName').value.trim();
        const description = view.querySelector('#editSourceDescription').value.trim();

        // If the user left the name blank, fetch it from YouTube before saving.
        if (!name) {
            Dashboard.showLoadingMsg();
            fetch(ApiClient.getUrl('/YouTubeSync/source-info') + '?url=' + encodeURIComponent(id), {
                headers: { 'X-Emby-Token': ApiClient.accessToken() }
            }).then(function (resp) {
                if (!resp.ok) throw new Error('HTTP ' + resp.status);
                return resp.json();
            }).then(function (info) {
                Dashboard.hideLoadingMsg();
                if (info.Type) {
                    view.querySelector('#editSourceType').value = info.Type;
                }

                commitSource(info.Title || id, info.Description || description);
            }).catch(function () {
                Dashboard.hideLoadingMsg();
                // Inform the user and fall back to saving with the URL as the name.
                Dashboard.alert({ message: 'Could not fetch metadata from YouTube. The link will be used as the name for now, and you can rename it later.' });
                commitSource(id, description);
            });
        } else {
            commitSource(name, description);
        }
    });

    view.querySelector('#cancelSourceBtn').addEventListener('click', closeEditForm);
}
