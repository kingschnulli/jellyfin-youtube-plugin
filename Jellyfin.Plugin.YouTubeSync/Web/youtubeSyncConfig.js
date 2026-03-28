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

    /* ── sources list rendering ──────────────────────────── */

    function renderSources() {
        const list = view.querySelector('#sourcesList');
        if (sources.length === 0) {
            list.innerHTML = '<p class="fieldDescription">No sources configured. Click <strong>+ Add Source</strong> to add your first channel or playlist.</p>';
            return;
        }

        list.innerHTML = sources.map(function (s, i) {
            return '<div class="listItem listItem-border" style="display:flex;align-items:center;padding:.75em 1em;gap:1em;">'
                + '<div style="flex:1;min-width:0;">'
                + '<div style="font-weight:600;">' + escapeHtml(s.Name || s.Id) + '</div>'
                + '<div class="fieldDescription" style="margin:0;">'
                + escapeHtml(s.Type) + ' &bull; ' + escapeHtml(s.Mode) + ' &bull; ID:&nbsp;' + escapeHtml(s.Id)
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

        view.querySelector('#editSourceTitle').textContent = index >= 0 ? 'Edit Source' : 'Add Source';
        view.querySelector('#editSourceId').value = s.Id;
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
            view.querySelector('#MaxVideosPerSource').value = config.MaxVideosPerSource != null ? config.MaxVideosPerSource : 200;
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
            MaxVideosPerSource: parseInt(view.querySelector('#MaxVideosPerSource').value, 10) || 0,
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

    view.querySelector('#saveSourceBtn').addEventListener('click', function () {
        const id = view.querySelector('#editSourceId').value.trim();
        const name = view.querySelector('#editSourceName').value.trim();

        if (!id) {
            Dashboard.processErrorResponse({ statusText: 'Channel / Playlist ID is required.' });
            return;
        }
        if (!name) {
            Dashboard.processErrorResponse({ statusText: 'Display name is required.' });
            return;
        }

        const src = {
            Id: id,
            Name: name,
            Type: view.querySelector('#editSourceType').value,
            Mode: view.querySelector('#editSourceMode').value,
            Description: view.querySelector('#editSourceDescription').value.trim()
        };

        if (editIndex >= 0) {
            sources[editIndex] = src;
        } else {
            sources.push(src);
        }

        closeEditForm();
        renderSources();
    });

    view.querySelector('#cancelSourceBtn').addEventListener('click', closeEditForm);
}
