(function () {
    'use strict';

    const CONTENT_TYPES = ['TV Show', 'Movie', 'Music Video', 'Music', 'Weather'];
    const CANDIDATE_KINDS = ['Jellyfin Item', 'Collection', 'Filter Query', 'Playlist / List'];
    const SLOT_CANDIDATE_KIND_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3,
        jellyfinItem: 0, JellyfinItem: 0,
        collection: 1, Collection: 1,
        filterQuery: 2, FilterQuery: 2,
        playlist: 3, Playlist: 3
    };
    const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
    const CONTENT_TYPE_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3, 4: 4,
        tvShow: 0, movie: 1, musicVideo: 2, music: 3, weather: 4,
        TvShow: 0, Movie: 1, MusicVideo: 2, Music: 3, Weather: 4
    };
    const ASPECT_RATIO_VALUES = {
        0: 0, 1: 1,
        sixteenNine: 0, fourThree: 1,
        SixteenNine: 0, FourThree: 1
    };
    const BUG_PLACEMENT_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5,
        auto: 0, topLeft: 1, topRight: 2, bottomLeft: 3, bottomRight: 4, none: 5,
        Auto: 0, TopLeft: 1, TopRight: 2, BottomLeft: 3, BottomRight: 4, None: 5
    };

    let channels = [];
    let logoSets = [];
    let selectedChannelId = null;
    let editingChannelId = null;
    let lineupSlots = [];
    let lineupOverrides = [];
    let lineupIsWeather = false;
    let itemTitleCache = {};
    let channelFilter = '';
    let channelOnAir = {};
    let onAirRefreshTimer = null;
    let channelPresets = [];
    let presetNumberingMode = 0;
    let configPage = null;
    let aiSettings = null;
    let aiChannels = [];
    let aiPlayoutTemplates = [];
    let aiPreview = null;
    let weatherDockerStatus = null;
    let playwrightDockerStatus = null;
    let finTvLists = [];
    let listNameCache = {};
    let specialPresentations = [];
    let specialChannelId = null;

    function $(id) {
        if (configPage) {
            return configPage.querySelector('#' + id);
        }

        return document.getElementById(id);
    }

    function q(selector) {
        return configPage ? configPage.querySelector(selector) : document.querySelector(selector);
    }

    function qa(selector) {
        return configPage ? configPage.querySelectorAll(selector) : document.querySelectorAll(selector);
    }

    function resolveUrl(path) {
        const normalized = path.startsWith('/') ? path.slice(1) : path;
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.getUrl === 'function') {
            return ApiClient.getUrl(normalized);
        }
        return '/' + normalized;
    }

    function parseErrorMessage(message) {
        if (!message) {
            return 'Request failed';
        }

        try {
            const parsed = JSON.parse(message);
            const genericTitles = new Set([
                'error processing request',
                'an error occurred while processing your request.',
                'an error occurred while processing your request',
                'bad request',
                'internal server error'
            ]);

            if (parsed.detail) {
                return parsed.detail;
            }

            if (parsed.message) {
                return parsed.message;
            }

            if (parsed.title && !genericTitles.has(String(parsed.title).trim().toLowerCase())) {
                return parsed.title;
            }

            if (parsed.errors) {
                return Object.values(parsed.errors).flat().join(' ');
            }

            if (parsed.title) {
                return parsed.title;
            }
        } catch (ignore) {
            // Keep raw response text.
        }

        return message;
    }

    async function readApiFailure(err) {
        if (err instanceof Response) {
            const text = await err.text();
            throw new Error(parseErrorMessage(text || err.statusText));
        }

        if (err && typeof err.responseText === 'string' && err.responseText) {
            throw new Error(parseErrorMessage(err.responseText));
        }

        const message = (err && err.message) || 'Request failed';
        if (isNetworkError(message)) {
            throw new Error('Jellyfin server is unreachable');
        }

        throw new Error(message);
    }

    function isNetworkError(err) {
        const message = String((err && err.message) || err || '').toLowerCase();
        return message.includes('failed to fetch')
            || message.includes('network error')
            || message.includes('connection refused')
            || message.includes('load failed')
            || message.includes('jellyfin server is unreachable');
    }

    function reportApiError(err, fallbackMessage) {
        if (isNetworkError(err)) {
            stopOnAirPolling();
            toast('Jellyfin server is unreachable. Restart the server and refresh this page.', 'error');
            return;
        }

        toast((err && err.message) || fallbackMessage || 'Request failed', 'error');
    }

    function normalizeApiValue(value) {
        if (Array.isArray(value)) {
            return value.map(normalizeApiValue);
        }

        if (!value || typeof value !== 'object') {
            return value;
        }

        const normalized = {};
        Object.keys(value).forEach((key) => {
            const camelKey = key.length ? key.charAt(0).toLowerCase() + key.slice(1) : key;
            normalized[camelKey] = normalizeApiValue(value[key]);
        });
        return normalized;
    }

    function normalizeApiResponse(value) {
        return value == null ? value : normalizeApiValue(value);
    }

    function parseApiJsonBody(text) {
        if (text == null || text === '') {
            return null;
        }

        if (typeof text === 'object') {
            return text;
        }

        try {
            return JSON.parse(text);
        } catch (err) {
            throw new Error('Invalid JSON response from server');
        }
    }

    function resolveEnumValue(map, value, fallback) {
        if (value == null || value === '') {
            return fallback;
        }

        if (typeof value === 'number' && Number.isFinite(value)) {
            return value;
        }

        const key = String(value);
        if (Object.prototype.hasOwnProperty.call(map, key)) {
            return map[key];
        }

        const parsed = parseInt(key, 10);
        return Number.isFinite(parsed) ? parsed : fallback;
    }

    function contentTypeLabel(value) {
        const index = resolveEnumValue(CONTENT_TYPE_VALUES, value, null);
        return index == null ? String(value) : (CONTENT_TYPES[index] || String(value));
    }

    function normalizeChannel(channel) {
        if (!channel) {
            return channel;
        }

        channel.contentType = resolveEnumValue(CONTENT_TYPE_VALUES, channel.contentType, 0);
        channel.aspectRatio = resolveEnumValue(ASPECT_RATIO_VALUES, channel.aspectRatio, 0);
        channel.bugPlacement = resolveEnumValue(BUG_PLACEMENT_VALUES, channel.bugPlacement, 0);
        return channel;
    }

    function setSelectEnum(selectId, map, value, fallback) {
        const select = $(selectId);
        if (!select) {
            return fallback;
        }

        const resolved = resolveEnumValue(map, value, fallback);
        select.value = String(resolved);
        return resolved;
    }

    function readSelectEnum(selectId, map, fallback) {
        const select = $(selectId);
        if (!select) {
            return fallback;
        }

        return resolveEnumValue(map, select.value, fallback);
    }

    function buildChannelPayload(form) {
        return {
            Number: form.number,
            Name: form.name,
            ContentType: form.contentType,
            AspectRatio: form.aspectRatio,
            ScanlinesEnabled: form.scanlinesEnabled,
            BugPlacement: form.bugPlacement,
            AudioLanguage: form.audioLanguage,
            LogoSetId: form.logoSetId,
            LogoFileName: form.logoFileName,
            WeatherLocationQuery: form.weatherLocationQuery,
            Enabled: form.enabled
        };
    }

    function isEmptyApiResponseError(err) {
        const message = String((err && err.message) || '');
        const status = err && err.status;
        const responseText = err && typeof err.responseText === 'string' ? err.responseText : '';
        return status === 204
            || ((message === 'parsererror' || message.includes('Unexpected end of JSON input'))
                && !responseText);
    }

    function normalizeApiResponseData(data) {
        if (data == null || data === '') {
            return null;
        }

        if (typeof data === 'object') {
            return normalizeApiResponse(data);
        }

        return normalizeApiResponse(parseApiJsonBody(data));
    }

    function ajaxViaApiClient(ajaxOptions) {
        return new Promise((resolve, reject) => {
            let settled = false;
            const finish = (action, value) => {
                if (settled) {
                    return;
                }

                settled = true;
                action(value);
            };

            const handleSuccess = (data, statusCode) => {
                if (statusCode === 204 || data == null || data === '') {
                    finish(resolve, null);
                    return;
                }

                try {
                    finish(resolve, normalizeApiResponseData(data));
                } catch (parseErr) {
                    finish(reject, parseErr);
                }
            };

            ajaxOptions.success = (data, _textStatus, xhr) => {
                handleSuccess(data, xhr && xhr.status);
            };

            ajaxOptions.error = (xhr, textStatus) => {
                if (isEmptyApiResponseError({ status: xhr.status, message: textStatus, responseText: xhr.responseText })) {
                    finish(resolve, null);
                    return;
                }

                void readApiFailure({ responseText: xhr.responseText, message: textStatus, status: xhr.status })
                    .catch((err) => finish(reject, err instanceof Error ? err : new Error(String(err))));
            };

            let ajaxResult;
            try {
                ajaxResult = ApiClient.ajax(ajaxOptions);
            } catch (syncErr) {
                finish(reject, syncErr instanceof Error ? syncErr : new Error(String(syncErr)));
                return;
            }

            if (ajaxResult && typeof ajaxResult.then === 'function') {
                ajaxResult.then((data) => {
                    if (settled) {
                        return;
                    }

                    handleSuccess(data);
                }).catch(async (err) => {
                    if (settled) {
                        return;
                    }

                    try {
                        await readApiFailure(err);
                    } catch (parsedErr) {
                        finish(reject, parsedErr instanceof Error ? parsedErr : new Error(String(parsedErr)));
                    }
                });
            }
        });
    }

    function api(path, options) {
        options = options || {};
        const url = resolveUrl('FinTV/api' + (path.startsWith('/') ? path : '/' + path));
        const method = options.method || 'GET';
        const body = options.body == null
            ? undefined
            : (typeof options.body === 'string' ? options.body : JSON.stringify(options.body));

        if (typeof ApiClient !== 'undefined' && typeof ApiClient.ajax === 'function') {
            const ajaxOptions = {
                url: url,
                type: method,
                dataType: 'text',
                headers: {
                    accept: 'application/json'
                }
            };

            if (body) {
                ajaxOptions.contentType = 'application/json';
                ajaxOptions.data = body;
            }

            return ajaxViaApiClient(ajaxOptions);
        }

        const fetchOptions = {
            method: method,
            credentials: 'same-origin',
            headers: {
                accept: 'application/json'
            }
        };

        if (body) {
            fetchOptions.headers['Content-Type'] = 'application/json';
            fetchOptions.body = body;
        }

        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
            const token = ApiClient.accessToken();
            if (token) {
                fetchOptions.headers['X-Emby-Token'] = token;
            }
        }

        return fetch(url, fetchOptions).then(async (res) => {
            if (!res.ok) {
                const text = await res.text();
                throw new Error(parseErrorMessage(text || res.statusText));
            }

            if (res.status === 204) {
                return null;
            }

            const text = await res.text();
            return normalizeApiResponse(parseApiJsonBody(text));
        }).catch((err) => {
            if (isNetworkError(err)) {
                throw new Error('Jellyfin server is unreachable');
            }

            throw err;
        });
    }

    async function apiForm(path, formData, method) {
        const url = resolveUrl('FinTV/api' + (path.startsWith('/') ? path : '/' + path));
        const httpMethod = method || 'POST';

        if (typeof ApiClient !== 'undefined' && typeof ApiClient.ajax === 'function') {
            return ajaxViaApiClient({
                url: url,
                type: httpMethod,
                data: formData,
                contentType: false,
                processData: false,
                dataType: 'text',
                headers: {
                    accept: 'application/json'
                }
            });
        }

        const headers = {
            accept: 'application/json'
        };
        if (typeof ApiClient !== 'undefined' && typeof ApiClient.accessToken === 'function') {
            const token = ApiClient.accessToken();
            if (token) {
                headers['X-Emby-Token'] = token;
            }
        }

        const fetchOptions = {
            method: httpMethod,
            credentials: 'same-origin',
            headers: headers,
            body: formData
        };

        try {
            const res = await fetch(url, fetchOptions);
            if (!res.ok) {
                const text = await res.text();
                throw new Error(parseErrorMessage(text || res.statusText));
            }

            if (res.status === 204) {
                return null;
            }

            const text = await res.text();
            return normalizeApiResponse(parseApiJsonBody(text));
        } catch (err) {
            if (isNetworkError(err)) {
                throw new Error('Jellyfin server is unreachable');
            }

            throw err;
        }
    }

    function toast(message, type) {
        const container = $('toast-container');
        if (!container) {
            return;
        }

        const el = document.createElement('div');
        el.className = 'toast' + (type ? ' ' + type : '');
        el.textContent = message;
        container.appendChild(el);
        setTimeout(() => el.remove(), 4200);
    }

    function slotTime(index) {
        const h = Math.floor(index / 2);
        const m = index % 2 ? '30' : '00';
        const h12 = ((h + 11) % 12) + 1;
        const ampm = h < 12 ? 'AM' : 'PM';
        return `${h12}:${m} ${ampm}`;
    }

    function formatChannelNumber(number) {
        const value = Math.round(Number(number) * 10) / 10;
        const major = Math.trunc(value);
        const minor = Math.round((value - major) * 10);
        return minor === 0 ? String(major) : `${major}.${minor}`;
    }

    function parseChannelNumber(raw) {
        const text = String(raw).trim();
        if (!/^\d+(\.\d)?$/.test(text)) {
            throw new Error('Channel number must be at least 1 with at most one decimal digit (.0 through .9).');
        }
        const value = Number(text);
        const major = Math.trunc(value);
        const minor = Math.round((value - major) * 10);
        if (value < 1 || minor < 0 || minor > 9) {
            throw new Error('Channel sub-number must be .0 through .9.');
        }
        return value;
    }

    function parseWeatherLocationQuery(value) {
        const location = String(value ?? '').trim();
        if (!location) {
            return null;
        }

        return location;
    }

    function splitWeatherPermalink(permalink) {
        let url;
        try {
            url = new URL(String(permalink ?? '').trim());
        } catch (ignore) {
            throw new Error('Invalid WeatherStar permalink URL.');
        }

        const params = new URLSearchParams(url.search);
        ['latLonQuery', 'latLon', 'txtLocation', 'lat', 'lon', 'kiosk', 'wide'].forEach((key) => params.delete(key));
        const pathname = url.pathname.endsWith('/') && url.pathname.length > 1
            ? url.pathname.slice(0, -1)
            : url.pathname;

        return {
            baseUrl: `${url.origin}${pathname}`,
            query: params.toString()
        };
    }

    function escapeHtml(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function todayIsoDate() {
        return new Date().toISOString().slice(0, 10);
    }

    function openModal(title, bodyHtml, footerHtml) {
        $('modal-title').textContent = title;
        $('modal-body').innerHTML = bodyHtml;
        $('modal-footer').innerHTML = footerHtml || '';
        $('modal-backdrop').classList.remove('hidden');
    }

    function closeModal() {
        $('modal-backdrop').classList.add('hidden');
        $('modal-body').innerHTML = '';
        $('modal-footer').innerHTML = '';
    }

    async function lookupItemTitles(ids) {
        const missing = ids.filter((id) => id && !itemTitleCache[id]);
        if (missing.length === 0) return;
        const results = await api('/catalog/lookup', { method: 'POST', body: JSON.stringify({ ids: missing }) });
        (results || []).forEach((item) => { itemTitleCache[item.id] = item.name; });
    }

    function itemLabel(id) {
        if (!id) return 'Unknown item';
        return itemTitleCache[id] || id;
    }

    function candidateKind(candidate) {
        return resolveEnumValue(SLOT_CANDIDATE_KIND_VALUES, candidate?.kind ?? candidate?.Kind, 0);
    }

    function candidateSummary(candidate) {
        if (!candidate) return 'Empty slot';
        const kind = candidateKind(candidate);
        const itemId = candidate.jellyfinItemId || candidate.JellyfinItemId;
        const collectionName = candidate.collectionName || candidate.CollectionName;
        const filterJson = candidate.filterJson || candidate.FilterJson;
        const finTvListId = candidate.finTvListId || candidate.FinTvListId;

        if (kind === 1 && collectionName) return `Collection: ${collectionName}`;
        if (kind === 2 && filterJson) return 'Filter query';
        if (kind === 3 && finTvListId) return `List: ${listNameCache[finTvListId] || finTvListId}`;
        if (itemId) return itemLabel(itemId);
        return CANDIDATE_KINDS[kind] || 'Candidate';
    }

    function slotIndexFromTime(value) {
        if (!value) return 0;
        const parts = value.split(':');
        const h = parseInt(parts[0], 10) || 0;
        const m = parseInt(parts[1], 10) || 0;
        return Math.min(47, Math.floor((h * 60 + m) / 30));
    }

    function slotTimeInputValue(index) {
        const totalMinutes = index * 30;
        const h = Math.floor(totalMinutes / 60);
        const m = totalMinutes % 60;
        return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}`;
    }

    async function ensureFinTvLists(force) {
        if (!force && finTvLists.length) {
            return finTvLists;
        }

        finTvLists = await api('/lists') || [];
        listNameCache = {};
        finTvLists.forEach((list) => {
            listNameCache[list.id] = list.name;
        });
        return finTvLists;
    }

    async function refreshDashboardStats() {
        try {
            const commercials = await api('/commercials').catch(() => []);
            const enabled = channels.filter((c) => c.enabled).length;
            $('dashboard-stats').innerHTML = [
                `<span class="stat-pill"><strong>${channels.length}</strong> channels</span>`,
                `<span class="stat-pill"><strong>${enabled}</strong> enabled</span>`,
                `<span class="stat-pill"><strong>${(commercials || []).length}</strong> commercials</span>`
            ].join('');
        } catch (e) {
            $('dashboard-stats').innerHTML = '';
        }
    }

    function updateSplitLayout() {
        const layout = q('.split-layout');
        const panel = $('channel-form-panel');
        if (layout && panel) {
            layout.classList.toggle('has-panel', !panel.classList.contains('hidden'));
        }
    }

    function toggleWeatherFields() {
        const contentType = $('ch-content-type');
        const weatherFields = $('weather-fields');
        if (!contentType || !weatherFields) {
            return;
        }

        weatherFields.classList.toggle('hidden', contentType.value !== '4');
    }

    function populateLogoSelectors(channel) {
        const setSelect = $('ch-logo-set');
        setSelect.innerHTML = '<option value="">None</option>' + logoSets.map((s) =>
            `<option value="${s.id}">${escapeHtml(s.name)} (${(s.entries || []).length})</option>`).join('');
        setSelect.value = channel?.logoSetId || '';

        const fileSelect = $('ch-logo-file');
        const set = logoSets.find((s) => s.id === (channel?.logoSetId || setSelect.value));
        fileSelect.innerHTML = '<option value="">Default</option>' + ((set && set.entries) || []).map((e) =>
            `<option value="${escapeHtml(e.fileName)}">${escapeHtml(e.displayName || e.fileName)}</option>`).join('');
        fileSelect.value = channel?.logoFileName || '';
    }

    async function loadLogoSetsForForm() {
        logoSets = await api('/logos/sets') || [];
    }

    async function loadChannelNowPlaying(channelId) {
        const box = $('channel-now-playing');
        if (!channelId) {
            box.classList.add('hidden');
            return;
        }
        try {
            const response = await api(`/channels/${channelId}/now-playing`).catch(() => null);
            const now = response?.item;
            if (now?.title) {
                box.classList.remove('hidden');
                box.innerHTML = `<strong>Now playing</strong>${escapeHtml(now.title)}`;
            } else {
                box.classList.add('hidden');
            }
        } catch {
            box.classList.add('hidden');
        }
    }

    function channelViewerCount(channelId) {
        return channelOnAir[String(channelId).toLowerCase()] || 0;
    }

    function renderChannelStatusBadges(channel) {
        const viewers = channelViewerCount(channel.id);
        const onAir = viewers > 0;
        const viewerLabel = viewers > 1 ? ` (${viewers})` : '';
        return `<div class="status-badges">
            <span class="badge ${channel.enabled ? 'badge-on' : 'badge-off'}">${channel.enabled ? 'On' : 'Off'}</span>
            <span class="badge ${onAir ? 'badge-air' : 'badge-idle'}">${onAir ? `On Air${viewerLabel}` : 'Off Air'}</span>
        </div>`;
    }

    async function loadChannelOnAirStatus() {
        try {
            const data = await api('/channels/on-air');
            const next = {};
            (data?.channels || []).forEach((entry) => {
                const id = entry.channelId || entry.id;
                const count = Number(entry.viewerCount ?? entry.viewers ?? 0);
                if (id && count > 0) {
                    next[String(id).toLowerCase()] = count;
                }
            });
            channelOnAir = next;
        } catch (err) {
            channelOnAir = {};
            if (isNetworkError(err)) {
                stopOnAirPolling();
            }
        }

        renderChannelsList();
    }

    function startOnAirPolling() {
        stopOnAirPolling();
        loadChannelOnAirStatus();
        onAirRefreshTimer = setInterval(loadChannelOnAirStatus, 10000);
    }

    function stopOnAirPolling() {
        if (onAirRefreshTimer) {
            clearInterval(onAirRefreshTimer);
            onAirRefreshTimer = null;
        }
    }

    function filteredChannels() {
        if (!channelFilter) return channels;
        const q = channelFilter.toLowerCase();
        return channels.filter((c) =>
            c.name.toLowerCase().includes(q) ||
            formatChannelNumber(c.number).includes(q) ||
            contentTypeLabel(c.contentType).toLowerCase().includes(q));
    }

    function renderChannelsList() {
        const list = filteredChannels();
        const wrap = $('channels-list');
        if (list.length === 0) {
            wrap.innerHTML = '<div class="empty-state">No channels yet. Click <strong>New Channel</strong> to create one.</div>';
            return;
        }

        wrap.innerHTML = `<table class="data-table">
            <thead><tr><th>#</th><th>Name</th><th>Type</th><th>Status</th><th></th></tr></thead>
            <tbody>${list.map((c) => `<tr data-id="${c.id}" class="${editingChannelId === c.id ? 'selected' : ''}">
                <td><strong>${formatChannelNumber(c.number)}</strong></td>
                <td>${escapeHtml(c.name)}</td>
                <td><span class="badge badge-type">${contentTypeLabel(c.contentType)}</span></td>
                <td>${renderChannelStatusBadges(c)}</td>
                <td class="row-actions">
                    <button type="button" data-edit="${c.id}">Edit</button>
                    <button type="button" data-lineup="${c.id}">Lineup</button>
                    <button type="button" class="btn-danger" data-delete="${c.id}">Delete</button>
                </td>
            </tr>`).join('')}</tbody></table>`;

        wrap.querySelectorAll('[data-edit]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            editChannel(btn.dataset.edit);
        });
        wrap.querySelectorAll('[data-lineup]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            selectedChannelId = btn.dataset.lineup;
            switchTab('lineups');
        });
        wrap.querySelectorAll('[data-delete]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            deleteChannel(btn.dataset.delete);
        });
        wrap.querySelectorAll('tbody tr').forEach((row) => row.onclick = () => editChannel(row.dataset.id));
    }

    async function loadChannels() {
        try {
            channels = (await api('/channels') || []).map(normalizeChannel);
            renderChannelsList();

            const select = $('lineup-channel-select');
            select.innerHTML = channels.map((c) =>
                `<option value="${c.id}">${formatChannelNumber(c.number)} - ${escapeHtml(c.name)}</option>`).join('');
            if (!selectedChannelId && channels[0]) selectedChannelId = channels[0].id;
            select.value = selectedChannelId || '';

            populateSpecialChannelSelect();

            await refreshDashboardStats();
        } catch (err) {
            reportApiError(err, 'Could not load channels.');
        }
    }

    function resetChannelForm() {
        editingChannelId = null;
        const form = $('channel-form');
        if (form) {
            form.reset();
        }

        const audio = $('ch-audio');
        if (audio) {
            audio.value = 'eng';
        }

        const enabled = $('ch-enabled');
        if (enabled) {
            enabled.checked = true;
        }

        const title = $('channel-form-title');
        if (title) {
            title.textContent = 'New Channel';
        }

        const deleteBtn = $('btn-delete-channel');
        if (deleteBtn) {
            deleteBtn.classList.add('hidden');
        }

        const nowPlaying = $('channel-now-playing');
        if (nowPlaying) {
            nowPlaying.classList.add('hidden');
        }

        populateLogoSelectors(null);
        toggleWeatherFields();
    }

    async function openNewChannelForm() {
        if (!syncConfigPage()) {
            return;
        }

        resetChannelForm();
        showChannelForm(true);

        try {
            await loadLogoSetsForForm();
            populateLogoSelectors(null);
        } catch (err) {
            toast(err.message || 'Could not load logo sets.', 'error');
        }
    }

    function showChannelForm(show) {
        const panel = $('channel-form-panel');
        if (!panel) {
            return;
        }

        panel.classList.toggle('hidden', !show);
        updateSplitLayout();
        if (!show) {
            resetChannelForm();
        }
    }

    async function editChannel(id) {
        const c = channels.find((x) => x.id === id);
        if (!c) return;

        try {
            await loadLogoSetsForForm();
        } catch (err) {
            reportApiError(err, 'Could not load logo sets.');
            return;
        }

        editingChannelId = id;
        showChannelForm(true);
        $('channel-form-title').textContent = `Edit ${c.name}`;
        $('btn-delete-channel').classList.remove('hidden');

        $('ch-number').value = c.number;
        $('ch-name').value = c.name;
        setSelectEnum('ch-content-type', CONTENT_TYPE_VALUES, c.contentType, 0);
        setSelectEnum('ch-aspect', ASPECT_RATIO_VALUES, c.aspectRatio, 0);
        $('ch-scanlines').checked = c.scanlinesEnabled;
        setSelectEnum('ch-bug', BUG_PLACEMENT_VALUES, c.bugPlacement, 0);
        $('ch-audio').value = c.audioLanguage || 'eng';
        if ($('ch-weather-location')) {
            $('ch-weather-location').value = c.weatherLocationQuery || '';
        }
        $('ch-enabled').checked = c.enabled;
        populateLogoSelectors(c);
        toggleWeatherFields();
        renderChannelsList();
        loadChannelNowPlaying(c.id);
    }

    async function saveChannel(e) {
        e.preventDefault();
        syncConfigPageFromEvent(e);
        if (!configPage) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        const nameEl = $('ch-name');
        const numberEl = $('ch-number');
        const form = $('channel-form');
        const submitBtn = form ? form.querySelector('button[type="submit"]') : null;
        const originalLabel = submitBtn ? submitBtn.textContent : '';

        if (!nameEl || !numberEl) {
            toast('Channel form is not loaded. Close and reopen the form.', 'error');
            return;
        }

        let number;
        try {
            number = parseChannelNumber(numberEl.value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        let weatherLocationQuery = null;
        try {
            weatherLocationQuery = parseWeatherLocationQuery($('ch-weather-location')?.value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        const payload = buildChannelPayload({
            number,
            name: nameEl.value.trim(),
            contentType: readSelectEnum('ch-content-type', CONTENT_TYPE_VALUES, 0),
            aspectRatio: readSelectEnum('ch-aspect', ASPECT_RATIO_VALUES, 0),
            scanlinesEnabled: !!$('ch-scanlines')?.checked,
            bugPlacement: readSelectEnum('ch-bug', BUG_PLACEMENT_VALUES, 0),
            audioLanguage: $('ch-audio')?.value.trim() || 'eng',
            logoSetId: $('ch-logo-set')?.value ? $('ch-logo-set').value : null,
            logoFileName: $('ch-logo-file')?.value || null,
            weatherLocationQuery,
            enabled: !!$('ch-enabled')?.checked
        });

        if (!payload.Name) {
            toast('Channel name is required.', 'error');
            return;
        }

        try {
            if (submitBtn) {
                submitBtn.disabled = true;
                submitBtn.textContent = 'Saving…';
            }

            if (editingChannelId) {
                await api('/channels/' + editingChannelId, { method: 'PUT', body: JSON.stringify(payload) });
                toast('Channel updated.', 'success');
            } else {
                await api('/channels', { method: 'POST', body: JSON.stringify(payload) });
                toast('Channel created.', 'success');
            }
            showChannelForm(false);
            await loadChannels();
        } catch (err) {
            reportApiError(err, 'Could not save channel.');
        } finally {
            if (submitBtn) {
                submitBtn.disabled = false;
                submitBtn.textContent = originalLabel || 'Save';
            }
        }
    }

    async function deleteChannel(channelId) {
        channelId = channelId || editingChannelId;
        if (!channelId) {
            return;
        }

        const c = channels.find((x) => x.id === channelId);
        if (!c) {
            return;
        }

        if (!confirm(`Delete channel ${formatChannelNumber(c.number)} - ${c.name}?`)) {
            return;
        }

        try {
            await api('/channels/' + channelId, { method: 'DELETE' });
            toast('Channel deleted.', 'success');
            if (editingChannelId === channelId) {
                showChannelForm(false);
            }

            if (selectedChannelId === channelId) {
                selectedChannelId = null;
            }

            await loadChannels();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function collectItemIdsFromSlots(slots) {
        const ids = [];
        (slots || []).forEach((slot) => (slot.candidates || []).forEach((c) => {
            if (c.jellyfinItemId) ids.push(c.jellyfinItemId);
        }));
        await lookupItemTitles(ids);
    }

    async function loadLineups() {
        selectedChannelId = $('lineup-channel-select').value;
        if (!selectedChannelId) return;

        try {
            const data = await api('/lineups/' + selectedChannelId);
            lineupIsWeather = !!(data.isWeather || getLineupChannel()?.contentType === CONTENT_TYPE_VALUES.Weather);
            lineupSlots = (data.lineup && data.lineup.slots) || [];
            lineupOverrides = data.overrides || [];
            if (lineupIsWeather) {
                lineupSlots = [{ slotIndex: 0, spanSlots: 48, candidates: [] }];
            } else if (lineupSlots.length === 0) {
                lineupSlots = Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }));
            }

            updateLineupToolbarState();

            if (lineupIsWeather) {
                renderWeatherLineupGrid();
                renderOverrideList();
                $('lineup-preview-banner').classList.add('hidden');
                await previewLineup(true);
                return;
            }

            await collectItemIdsFromSlots(lineupSlots);
            renderLineupGrid();
            renderOverrideList();
            $('lineup-preview-banner').classList.add('hidden');
            await loadLineupPlayoutStatus();
        } catch (err) {
            reportApiError(err, 'Could not load lineup.');
        }
    }

    async function loadLineupPlayoutStatus() {
        const banner = $('lineup-playout-banner');
        if (!banner) {
            return;
        }

        if (!selectedChannelId || lineupIsWeather) {
            banner.classList.add('hidden');
            return;
        }

        try {
            const h = await api('/lineups/' + selectedChannelId + '/playout-horizon');
            const daysBuilt = Number(h.daysBuilt || 0);
            const targetDays = Number(h.playoutDaysToBuild || 14);

            if (!h.latestScheduledFinishUtc || daysBuilt < 0.5) {
                banner.classList.remove('hidden');
                banner.textContent = 'Live TV guide has no playout for this channel yet. Click Rebuild Playout (or Save Lineup) to populate the EPG.';
                return;
            }

            if (daysBuilt < 1) {
                banner.classList.remove('hidden');
                banner.textContent = `Guide playout ends in about ${Math.max(1, Math.round(daysBuilt * 24))} hours. Rebuild Playout to refresh the full ${targetDays}-day guide.`;
                return;
            }

            banner.classList.add('hidden');
        } catch (_) {
            banner.classList.add('hidden');
        }
    }

    function getLineupChannel() {
        return channels.find((c) => c.id === selectedChannelId);
    }

    function updateLineupToolbarState() {
        const hint = $('lineup-hint');
        const saveBtn = $('btn-save-lineup');
        const addOverrideBtn = $('btn-add-override');
        const overridesSection = $('lineup-overrides-section');
        const weatherBanner = $('lineup-weather-banner');
        const channel = getLineupChannel();

        if (lineupIsWeather) {
            const location = channel?.weatherLocationQuery;
            const coords = location && String(location).trim()
                ? String(location).trim()
                : '50317, Des Moines, IA, USA';

            if (hint) {
                hint.textContent = 'Weather channels use 24 one-hour Local Weather blocks that play back-to-back all day.';
            }

            if (weatherBanner) {
                weatherBanner.classList.remove('hidden');
                weatherBanner.textContent = `Weather channel · Live WeatherStar capture · Location: ${coords}. Edit location on the Channels tab. Configure display settings on the Weather tab.`;
            }

            saveBtn?.classList.add('hidden');
            addOverrideBtn?.classList.add('hidden');
            overridesSection?.classList.add('hidden');
            return;
        }

        if (hint) {
            hint.textContent = 'Click a 30-minute slot to edit candidates. Slots with overrides show a badge on preview.';
        }

        weatherBanner?.classList.add('hidden');
        saveBtn?.classList.remove('hidden');
        addOverrideBtn?.classList.remove('hidden');
        overridesSection?.classList.remove('hidden');
    }

    function renderWeatherLineupGrid() {
        const grid = $('lineup-grid');
        grid.innerHTML = Array.from({ length: 24 }, (_, hour) => {
            const start = slotTime(hour * 2);
            const end = hour < 23 ? slotTime((hour + 1) * 2) : '12:00 AM';
            return `<div class="slot-card has-items weather-slot" data-hour="${hour}" style="--slot-span:2;grid-column:span 2">
                <div class="time">${start} – ${end}</div>
                <div class="summary">Local Weather</div>
                <div class="count">Live WeatherStar · 1 hour</div>
            </div>`;
        }).join('');

        grid.querySelectorAll('.weather-slot').forEach((card) => {
            card.onclick = () =>
                toast('Weather channels use 24 hourly live blocks. Edit coordinates on the Channels tab.', 'info');
        });
    }

    function renderLineupGrid() {
        const grid = $('lineup-grid');
        grid.innerHTML = lineupSlots.sort((a, b) => a.slotIndex - b.slotIndex).map((s) => {
            const count = (s.candidates || []).length;
            const first = count ? candidateSummary(s.candidates[0]) : 'Empty slot';
            const span = Math.max(1, s.spanSlots || 1);
            const spanLabel = span > 1 ? ` · ${span * 30}m` : '';
            return `<div class="slot-card ${count ? 'has-items' : 'empty'}" data-slot="${s.slotIndex}" style="${span > 1 ? '--slot-span:' + span + ';grid-column:span ' + span : ''}">
                <div class="time">${slotTime(s.slotIndex)}${spanLabel}</div>
                <div class="summary">${escapeHtml(first)}</div>
                <div class="count">${count} candidate${count === 1 ? '' : 's'}</div>
            </div>`;
        }).join('');

        grid.querySelectorAll('.slot-card').forEach((card) => {
            card.onclick = () => openSlotEditor(parseInt(card.dataset.slot, 10));
        });
    }

    function renderOverrideList() {
        const el = $('override-list');
        if (!lineupOverrides.length) {
            el.innerHTML = '<div class="empty-state">No override lineups configured.</div>';
            return;
        }

        el.innerHTML = lineupOverrides.map((o) => {
            const when = o.kind === 1 && o.specificDate
                ? o.specificDate
                : (o.dayOfWeek !== undefined && o.dayOfWeek !== null ? DAYS[o.dayOfWeek] : 'Schedule');
            return `<div class="override-card">
                <div>
                    <strong>${escapeHtml(o.name)}</strong>
                    <div class="meta">${when} · ${(o.slots || []).filter((s) => (s.candidates || []).length).length} filled slots</div>
                </div>
                <div class="row-actions">
                    <button type="button" data-delete-override="${o.id}">Delete</button>
                </div>
            </div>`;
        }).join('');

        el.querySelectorAll('[data-delete-override]').forEach((btn) => {
            btn.onclick = () => deleteOverride(btn.dataset.deleteOverride);
        });
    }

    function openSlotEditor(index) {
        if (lineupIsWeather) {
            toast('Weather channels use a live 24/7 feed and do not use lineup candidates.', 'info');
            return;
        }

        const slot = lineupSlots.find((s) => s.slotIndex === index) || { slotIndex: index, candidates: [] };
        slot.candidates = slot.candidates || [];
        const channel = channels.find((c) => c.id === selectedChannelId);

        const body = `
            <p class="hint">Editing ${slotTime(index)} · add multiple weighted candidates for smart rotation.</p>
            <label class="field"><span>Block length (30-min slots)</span>
                <input id="slot-span-slots" type="number" min="1" max="8" class="emby-input" value="${Math.max(1, slot.spanSlots || 1)}"></label>
            <div id="slot-candidates" class="candidate-list">${renderCandidateRows(slot.candidates)}</div>
            <div class="field">
                <span>Add candidate</span>
                <select id="slot-add-kind" class="emby-select">
                    <option value="0">Jellyfin item</option>
                    <option value="1">Collection name</option>
                    <option value="2">Filter JSON</option>
                    <option value="3">FinTV list</option>
                </select>
            </div>
            <div id="slot-add-panel"></div>`;

        openModal(`Slot ${slotTime(index)}`, body, `
            <button type="button" class="emby-button" id="slot-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="slot-save">Save Slot</button>`);

        const panel = document.getElementById('slot-add-panel');
        function renderAddPanel() {
            const kind = parseInt(document.getElementById('slot-add-kind').value, 10);
            if (kind === 0) {
                panel.innerHTML = `
                    <label class="field"><span>Search library</span>
                    <input id="slot-search" type="search" class="emby-input" placeholder="Type at least 2 characters…"></label>
                    <div id="slot-search-results" class="search-results"></div>`;
                let timer;
                document.getElementById('slot-search').oninput = (ev) => {
                    clearTimeout(timer);
                    timer = setTimeout(() => searchLibrary(ev.target.value, channel), 250);
                };
            } else if (kind === 1) {
                panel.innerHTML = `<label class="field"><span>Collection name</span>
                    <input id="slot-collection" class="emby-input"><button type="button" class="emby-button" id="slot-add-collection" style="margin-top:.5rem">Add collection</button></label>`;
                document.getElementById('slot-add-collection').onclick = () => {
                    const name = document.getElementById('slot-collection').value.trim();
                    if (!name) return;
                    slot.candidates.push({ kind: 1, collectionName: name, weight: 1, sortOrder: slot.candidates.length });
                    refreshCandidateList(slot);
                };
            } else if (kind === 2) {
                panel.innerHTML = `<label class="field"><span>Filter JSON</span>
                    <textarea id="slot-filter" class="emby-input" rows="3" placeholder='{"genre":"Comedy"}'></textarea></label>
                    <button type="button" class="emby-button" id="slot-add-filter">Add filter</button>`;
                document.getElementById('slot-add-filter').onclick = () => {
                    const json = document.getElementById('slot-filter').value.trim();
                    if (!json) return;
                    slot.candidates.push({ kind: 2, filterJson: json, weight: 1, sortOrder: slot.candidates.length });
                    refreshCandidateList(slot);
                };
            } else if (kind === 3) {
                ensureFinTvLists().then((lists) => {
                    panel.innerHTML = `<label class="field"><span>FinTV list</span>
                        <select id="slot-list-id" class="emby-select">
                            ${lists.map((l) => `<option value="${l.id}">${escapeHtml(l.name)}</option>`).join('')}
                        </select></label>
                        <button type="button" class="emby-button" id="slot-add-list" style="margin-top:.5rem">Add list</button>`;
                    document.getElementById('slot-add-list').onclick = () => {
                        const listId = document.getElementById('slot-list-id').value;
                        if (!listId) return;
                        slot.candidates.push({ kind: 3, finTvListId: listId, weight: 1, sortOrder: slot.candidates.length });
                        refreshCandidateList(slot);
                    };
                });
            }
        }

        document.getElementById('slot-add-kind').onchange = renderAddPanel;
        renderAddPanel();
        bindCandidateRowActions(slot);

        async function searchLibrary(q, ch) {
            const resultsEl = document.getElementById('slot-search-results');
            if (!q || q.trim().length < 2) {
                resultsEl.innerHTML = '';
                return;
            }
            const params = new URLSearchParams({ q: q.trim(), limit: '20' });
            if (ch) params.set('contentType', ch.contentType);
            const results = await api('/catalog/search?' + params.toString());
            resultsEl.innerHTML = (results || []).map((item) =>
                `<div class="search-result" data-id="${item.id}">
                    <strong>${escapeHtml(item.name)}</strong>
                    <div class="sub">${escapeHtml(item.type)}${item.runtimeMinutes ? ' · ' + item.runtimeMinutes + 'm' : ''}</div>
                </div>`).join('') || '<div class="search-result">No matches</div>';

            resultsEl.querySelectorAll('.search-result[data-id]').forEach((row) => {
                row.onclick = () => {
                    itemTitleCache[row.dataset.id] = row.querySelector('strong').textContent;
                    slot.candidates.push({
                        kind: 0,
                        jellyfinItemId: row.dataset.id,
                        weight: 1,
                        sortOrder: slot.candidates.length
                    });
                    refreshCandidateList(slot);
                    toast('Item added to slot.', 'success');
                };
            });
        }

        document.getElementById('slot-cancel').onclick = closeModal;
        document.getElementById('slot-save').onclick = () => {
            slot.spanSlots = Math.max(1, Math.min(8, parseInt(document.getElementById('slot-span-slots').value, 10) || 1));
            const idx = lineupSlots.findIndex((s) => s.slotIndex === index);
            if (idx >= 0) lineupSlots[idx] = slot;
            else lineupSlots.push(slot);
            closeModal();
            renderLineupGrid();
            toast('Slot updated. Click Save Lineup to persist.', 'success');
        };
    }

    function renderCandidateRows(candidates) {
        if (!candidates.length) return '<div class="hint">No candidates yet.</div>';
        return candidates.map((c, i) => `<div class="candidate-row">
            <div><div class="title">${escapeHtml(candidateSummary(c))}</div>
            <div class="sub">${CANDIDATE_KINDS[candidateKind(c)] || 'Item'} · weight ${c.weight || 1}</div></div>
            <input type="number" min="1" value="${c.weight || 1}" data-weight="${i}" style="width:70px">
            <button type="button" data-remove-candidate="${i}">Remove</button>
        </div>`).join('');
    }

    function refreshCandidateList(currentSlot, containerId = 'slot-candidates') {
        const container = document.getElementById(containerId);
        if (!container) return;
        container.innerHTML = renderCandidateRows(currentSlot.candidates);
        bindCandidateRowActions(currentSlot, containerId);
    }

    function bindCandidateRowActions(currentSlot, containerId = 'slot-candidates') {
        const container = document.getElementById(containerId);
        if (!container) return;

        container.querySelectorAll('[data-remove-candidate]').forEach((btn) => {
            btn.onclick = () => {
                currentSlot.candidates.splice(parseInt(btn.dataset.removeCandidate, 10), 1);
                currentSlot.candidates.forEach((c, i) => { c.sortOrder = i; });
                refreshCandidateList(currentSlot, containerId);
            };
        });
        container.querySelectorAll('[data-weight]').forEach((input) => {
            input.onchange = () => {
                const idx = parseInt(input.dataset.weight, 10);
                currentSlot.candidates[idx].weight = Math.max(1, parseInt(input.value, 10) || 1);
            };
        });
    }

    async function saveLineup() {
        try {
            await api('/lineups/' + selectedChannelId, { method: 'PUT', body: JSON.stringify(lineupSlots) });
            toast('Lineup saved. Playout rebuild started in background.', 'success');
            await loadLineupPlayoutStatus();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function rebuildLineup() {
        const btn = $('btn-rebuild-lineup');
        try {
            if (btn) {
                btn.disabled = true;
            }

            await api('/lineups/' + selectedChannelId + '/rebuild', { method: 'POST' });
            toast('Playout rebuild started in background. Guide status will refresh automatically.', 'success');

            for (let attempt = 0; attempt < 24; attempt++) {
                await new Promise((resolve) => setTimeout(resolve, 5000));
                await loadLineupPlayoutStatus();
                const h = await api('/lineups/' + selectedChannelId + '/playout-horizon');
                if (Number(h.daysBuilt || 0) >= 1) {
                    toast('Playout rebuild finished for this channel.', 'success');
                    return;
                }
            }

            toast('Playout rebuild is still running. Check the guide banner again in a minute.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
            }
        }
    }

    async function previewLineup(silent) {
        const dateVal = $('lineup-preview-date').value || todayIsoDate();
        try {
            const data = await api('/lineups/' + selectedChannelId + '/preview', {
                method: 'POST',
                body: JSON.stringify({ date: dateVal })
            });

            if (data.isWeather) {
                $('lineup-preview-banner').classList.remove('hidden');
                $('lineup-preview-banner').textContent = `Preview for ${data.date}: 24/24 hourly blocks — ${data.title || 'Local Weather'} (live).`;
                return;
            }

            const filled = (data.slots || []).filter((s) => s.candidateCount > 0).length;
            $('lineup-preview-banner').classList.remove('hidden');
            $('lineup-preview-banner').textContent = `Preview for ${data.date}: ${filled}/48 slots have candidates.`;
        } catch (err) {
            if (!silent) {
                toast(err.message, 'error');
            }
        }
    }

    function openOverrideForm() {
        const body = `
            <label class="field"><span>Name</span><input id="ov-name" class="emby-input" placeholder="Friday Movie Night"></label>
            <label class="field"><span>Schedule type</span>
                <select id="ov-kind" class="emby-select">
                    <option value="0">Day of week</option>
                    <option value="1">Specific date</option>
                </select>
            </label>
            <label class="field" id="ov-day-wrap"><span>Day</span>
                <select id="ov-day" class="emby-select">${DAYS.map((d, i) => `<option value="${i}">${d}</option>`).join('')}</select>
            </label>
            <label class="field hidden" id="ov-date-wrap"><span>Date</span><input id="ov-date" type="date" class="emby-input"></label>
            <p class="hint">Override starts with empty slots. Edit them on the lineup grid after saving (future enhancement: dedicated override editor).</p>`;

        openModal('Add Override Lineup', body, `
            <button type="button" class="emby-button" id="ov-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="ov-save">Create Override</button>`);

        const kindEl = document.getElementById('ov-kind');
        kindEl.onchange = () => {
            const specific = kindEl.value === '1';
            document.getElementById('ov-day-wrap').classList.toggle('hidden', specific);
            document.getElementById('ov-date-wrap').classList.toggle('hidden', !specific);
        };

        document.getElementById('ov-cancel').onclick = closeModal;
        document.getElementById('ov-save').onclick = async () => {
            const name = document.getElementById('ov-name').value.trim();
            if (!name) {
                toast('Override name is required.', 'error');
                return;
            }
            const kind = parseInt(kindEl.value, 10);
            const payload = {
                name,
                kind,
                dayOfWeek: kind === 0 ? parseInt(document.getElementById('ov-day').value, 10) : null,
                specificDate: kind === 1 ? document.getElementById('ov-date').value : null,
                slots: Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }))
            };
            try {
                await api('/lineups/' + selectedChannelId + '/overrides', { method: 'POST', body: JSON.stringify(payload) });
                closeModal();
                toast('Override created.', 'success');
                await loadLineups();
            } catch (err) {
                toast(err.message, 'error');
            }
        };
    }

    async function deleteOverride(id) {
        if (!confirm('Delete this override lineup?')) return;
        try {
            await api('/lineups/overrides/' + id, { method: 'DELETE' });
            toast('Override deleted.', 'success');
            await loadLineups();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function parseCommaList(value) {
        return (value || '').split(',').map((part) => part.trim()).filter(Boolean);
    }

    function parseDecadeList(value) {
        return parseCommaList(value)
            .map((part) => parseInt(part, 10))
            .filter((num) => Number.isFinite(num) && num >= 1900);
    }

    const COMMERCIALBRAINZ_DEFAULT_URL = 'https://commercialbrainz.duckdns.org';

    function readBrainzSettingsFromForm() {
        return {
            enabled: !!$('cb-enabled')?.checked,
            baseUrl: $('cb-base-url')?.value?.trim() || COMMERCIALBRAINZ_DEFAULT_URL,
            apiToken: $('cb-api-token')?.value?.trim() || '',
            poolMode: parseInt($('cb-pool-mode')?.value, 10) || 2,
            maxSyncResults: parseInt($('cb-max-sync')?.value, 10) || 500,
            minYear: $('cb-min-year')?.value ? parseInt($('cb-min-year').value, 10) : null,
            maxYear: $('cb-max-year')?.value ? parseInt($('cb-max-year').value, 10) : null,
            decades: parseDecadeList($('cb-decades')?.value),
            brands: parseCommaList($('cb-brands')?.value),
            tags: parseCommaList($('cb-tags')?.value),
            excludeTags: parseCommaList($('cb-exclude-tags')?.value),
            genres: parseCommaList($('cb-genres')?.value),
            networks: parseCommaList($('cb-networks')?.value),
            channelNames: parseCommaList($('cb-channel-names')?.value),
            minAgeLimit: $('cb-min-age')?.value ? parseInt($('cb-min-age').value, 10) : null,
            maxAgeLimit: $('cb-max-age')?.value ? parseInt($('cb-max-age').value, 10) : null,
            allowSpoof: !!$('cb-allow-spoof')?.checked,
            allowFake: !!$('cb-allow-fake')?.checked,
            allowReal: !!$('cb-allow-real')?.checked,
            allowAiEnhanced: !!$('cb-allow-ai')?.checked,
            allowLateNight: !!$('cb-allow-latenight')?.checked,
            allowAdultRated: !!$('cb-allow-adult')?.checked,
            allowBanned: !!$('cb-allow-banned')?.checked
        };
    }

    function applyBrainzSettings(settings) {
        settings = settings || {};
        if ($('cb-enabled')) $('cb-enabled').checked = !!settings.enabled;
        if ($('cb-base-url')) {
            $('cb-base-url').value = settings.baseUrl || COMMERCIALBRAINZ_DEFAULT_URL;
        }
        if ($('cb-api-token')) $('cb-api-token').value = '';
        if ($('cb-pool-mode')) $('cb-pool-mode').value = String(settings.poolMode ?? 2);
        if ($('cb-max-sync')) $('cb-max-sync').value = settings.maxSyncResults || 500;
        if ($('cb-min-year')) $('cb-min-year').value = settings.minYear ?? '';
        if ($('cb-max-year')) $('cb-max-year').value = settings.maxYear ?? '';
        if ($('cb-decades')) $('cb-decades').value = (settings.decades || []).join(', ');
        if ($('cb-brands')) $('cb-brands').value = (settings.brands || []).join(', ');
        if ($('cb-tags')) $('cb-tags').value = (settings.tags || []).join(', ');
        if ($('cb-exclude-tags')) $('cb-exclude-tags').value = (settings.excludeTags || []).join(', ');
        if ($('cb-genres')) $('cb-genres').value = (settings.genres || []).join(', ');
        if ($('cb-networks')) $('cb-networks').value = (settings.networks || []).join(', ');
        if ($('cb-channel-names')) $('cb-channel-names').value = (settings.channelNames || []).join(', ');
        if ($('cb-min-age')) $('cb-min-age').value = settings.minAgeLimit ?? '';
        if ($('cb-max-age')) $('cb-max-age').value = settings.maxAgeLimit ?? '';
        if ($('cb-allow-spoof')) $('cb-allow-spoof').checked = settings.allowSpoof !== false;
        if ($('cb-allow-fake')) $('cb-allow-fake').checked = settings.allowFake !== false;
        if ($('cb-allow-real')) $('cb-allow-real').checked = settings.allowReal !== false;
        if ($('cb-allow-ai')) $('cb-allow-ai').checked = settings.allowAiEnhanced !== false;
        if ($('cb-allow-latenight')) $('cb-allow-latenight').checked = settings.allowLateNight !== false;
        if ($('cb-allow-adult')) $('cb-allow-adult').checked = !!settings.allowAdultRated;
        if ($('cb-allow-banned')) $('cb-allow-banned').checked = !!settings.allowBanned;
        renderBrainzStatus(settings.syncState, settings.hasApiToken);
    }

    function renderBrainzStatus(syncState, hasApiToken) {
        const el = $('brainz-status');
        if (!el) return;
        const state = syncState || {};
        el.textContent = [
            `API token saved: ${hasApiToken ? 'yes' : 'no'}`,
            `Sync running: ${state.isRunning ? 'yes' : 'no'}`,
            `Last matched: ${state.lastMatchedCount ?? 0}`,
            `Last fetched: ${state.lastFetchedCount ?? 0}`,
            `Library count: ${state.libraryCount ?? 0}`,
            state.lastCompletedAt ? `Last sync: ${state.lastCompletedAt}` : 'Last sync: never',
            state.lastError ? `Last error: ${state.lastError}` : ''
        ].filter(Boolean).join('\n');
    }

    function renderBrainzPreview(preview) {
        const el = $('brainz-preview');
        if (!el) return;
        if (!preview) {
            el.innerHTML = '';
            return;
        }
        const samples = preview.samples || preview.Samples || [];
        el.innerHTML = `<div class="preview-banner">${preview.matchedCount ?? preview.MatchedCount ?? 0} matches from ${preview.fetchedCount ?? preview.FetchedCount ?? 0} fetched videos</div>` +
            (samples.length ? `<table class="data-table"><thead><tr><th>Title</th><th>Brand</th><th>Year</th><th>Network</th></tr></thead><tbody>${samples.map((item) => `<tr>
                <td>${escapeHtml(item.title || item.Title || '')}</td>
                <td>${escapeHtml(item.brand || item.Brand || '')}</td>
                <td>${escapeHtml(String(item.year ?? item.Year ?? ''))}</td>
                <td>${escapeHtml(item.network || item.Network || '')}</td>
            </tr>`).join('')}</tbody></table>` : '<div class="empty-state">No preview samples.</div>');
    }

    async function loadBrainzSettings() {
        try {
            const settings = await api('/commercials/brainz/settings');
            applyBrainzSettings(settings);
        } catch (err) {
            applyBrainzSettings(null);
            reportApiError(err, 'Could not load CommercialBrainz settings.');
        }
    }

    async function saveBrainzSettings() {
        if (!syncConfigPage()) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        try {
            const payload = readBrainzSettingsFromForm();
            const saved = await api('/commercials/brainz/settings', {
                method: 'PUT',
                body: JSON.stringify(payload)
            });
            applyBrainzSettings(saved);
            toast('CommercialBrainz filters saved.', 'success');
        } catch (err) {
            reportApiError(err, 'Could not save CommercialBrainz settings.');
        }
    }

    async function previewBrainz() {
        await saveBrainzSettings();
        const preview = await api('/commercials/brainz/preview', { method: 'POST' });
        renderBrainzPreview(preview);
        toast(`Preview: ${preview.matchedCount ?? preview.MatchedCount ?? 0} matches`, 'success');
    }

    async function syncBrainz() {
        await saveBrainzSettings();
        await api('/commercials/brainz/sync', { method: 'POST' });
        toast('CommercialBrainz sync started.', 'success');
        await loadBrainzSettings();
        await loadCommercials();
    }

    async function loadCommercials() {
        try {
            const list = await api('/commercials');
            const status = await api('/commercials/scan-status');
            await loadBrainzSettings();
            $('commercial-status').textContent = status ? JSON.stringify(status, null, 2) : 'No scan running.';

            if (!list || !list.length) {
                $('commercial-list').innerHTML = '<div class="empty-state">No commercials synced yet.</div>';
                return;
            }

            $('commercial-list').innerHTML = `<table class="data-table">
                <thead><tr><th>Source</th><th>Title</th><th>Brand</th><th>Duration</th><th>Year</th><th>Chapters</th></tr></thead>
                <tbody>${list.map((c) => `<tr>
                    <td><span class="badge badge-type">${escapeHtml((c.source === 1 || c.source === 'CommercialBrainz') ? 'Brainz' : 'Jellyfin')}</span></td>
                    <td>${escapeHtml(c.title)}</td>
                    <td>${escapeHtml(c.brand || '')}</td>
                    <td>${Math.round((c.duration && c.duration.totalSeconds) || 0)}s</td>
                    <td>${escapeHtml(String(c.year ?? ''))}</td>
                    <td>${(c.chapters || []).length}</td>
                </tr>`).join('')}</tbody></table>`;
        } catch (err) {
            reportApiError(err, 'Could not load commercials.');
        }
    }

    async function loadLogos() {
        try {
            const sets = await api('/logos/sets') || [];
            logoSets = sets;
            if (!sets.length) {
                $('logo-set-info').innerHTML = '<div class="empty-state">No logo sets yet. Import Binarygeek119 or create a custom set.</div>';
                return;
            }

            $('logo-set-info').innerHTML = sets.map((s) => {
            const files = (s.entries || []).slice(0, 48).map((e) =>
                `<span class="badge badge-type" style="margin:.15rem">${escapeHtml(e.displayName || e.fileName)}</span>`).join('');
            const customBadge = s.isCustom ? '<span class="badge badge-on" style="margin-left:.35rem">Custom</span>' : '';
            const actions = s.isCustom
                ? `<div class="actions" style="margin-top:.65rem">
                        <button type="button" class="emby-button btn-manage-logo-set" data-set-id="${escapeHtml(s.id)}">Manage Logos</button>
                        <button type="button" class="emby-button btn-delete-logo-set" data-set-id="${escapeHtml(s.id)}">Delete Set</button>
                   </div>`
                : '';
            return `<div class="card"><h3>${escapeHtml(s.name)}${customBadge}</h3><p class="hint">${(s.entries || []).length} logos indexed · assign per channel in the Channels editor</p><div>${files || '<span class="hint">No files found</span>'}</div>${actions}</div>`;
        }).join('');

        qa('.btn-manage-logo-set').forEach((btn) => {
            btn.onclick = () => openCustomLogoSetModal(btn.dataset.setId);
        });
        qa('.btn-delete-logo-set').forEach((btn) => {
            btn.onclick = () => deleteCustomLogoSet(btn.dataset.setId);
        });
        } catch (err) {
            reportApiError(err, 'Could not load logo sets.');
        }
    }

    async function repairChannelLogos() {
        try {
            const result = await api('/logos/repair-channels', { method: 'POST' });
            const status = $('logo-repair-status');
            if (status) {
                status.classList.remove('hidden');
                const repaired = result.repaired || result.Repaired || [];
                const missing = result.missing || result.Missing || [];
                status.textContent = [
                    `Repaired ${repaired.length} channel(s).`,
                    repaired.length ? `Updated: ${repaired.join(', ')}` : '',
                    missing.length ? `Still missing artwork: ${missing.join(', ')}` : 'All channels now have logo assignments.'
                ].filter(Boolean).join('\n');
            }
            toast(`Applied logos to ${(result.repaired || result.Repaired || []).length} channel(s).`, 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function openCreateLogoSetModal() {
        openModal(
            'Create Custom Logo Set',
            `<label class="field"><span>Set name</span><input id="new-logo-set-name" type="text" class="emby-input" placeholder="My Channel Logos"></label>
             <p class="hint">After creating the set, you can upload named PNG/JPG/WEBP logos and assign them to channels.</p>`,
            `<button type="button" class="emby-button" id="modal-cancel-logo-set">Cancel</button>
             <button type="button" class="raised button-submit emby-button" id="modal-save-logo-set">Create Set</button>`
        );

        $('modal-cancel-logo-set').onclick = closeModal;
        $('modal-save-logo-set').onclick = () => createCustomLogoSet();
    }

    async function createCustomLogoSet() {
        const name = ($('new-logo-set-name')?.value || '').trim();
        if (!name) {
            toast('Set name is required.', 'error');
            return;
        }

        try {
            const set = await api('/logos/sets/custom', { method: 'POST', body: { name: name } });
            closeModal();
            toast(`Created logo set "${name}".`, 'success');
            await loadLogos();
            if (set && set.id) {
                openCustomLogoSetModal(set.id);
            }
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function deleteCustomLogoSet(setId) {
        const set = logoSets.find((s) => s.id === setId);
        if (!set) {
            return;
        }

        if (!confirm(`Delete custom logo set "${set.name}"? This cannot be undone.`)) {
            return;
        }

        try {
            await api('/logos/sets/' + setId, { method: 'DELETE' });
            toast('Logo set deleted.', 'success');
            await loadLogos();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function openCustomLogoSetModal(setId) {
        const set = await api('/logos/sets/' + setId);
        if (!set) {
            toast('Logo set not found.', 'error');
            return;
        }

        const modal = document.querySelector('#modal-backdrop .modal');
        if (modal) {
            modal.classList.add('modal-wide');
        }

        const rows = (set.entries || []).map((entry) => `
            <div class="logo-entry-row" data-entry-id="${escapeHtml(entry.id)}">
                <label><span>Logo name</span><input type="text" class="emby-input logo-entry-name" value="${escapeHtml(entry.displayName || entry.fileName)}"></label>
                <label><span>File</span><input type="text" class="emby-input" value="${escapeHtml(entry.fileName)}" readonly></label>
                <label><span>Replace image</span><input type="file" class="logo-entry-file" accept="image/png,image/jpeg,image/webp,.png,.jpg,.jpeg,.webp"></label>
                <div class="logo-entry-actions">
                    <button type="button" class="emby-button btn-save-logo-entry">Save Name</button>
                    <button type="button" class="emby-button btn-upload-logo-entry">Upload</button>
                    <button type="button" class="emby-button btn-danger btn-delete-logo-entry">Delete</button>
                </div>
            </div>`).join('');

        openModal(
            `Manage Logos · ${set.name}`,
            `<div class="logo-upload-row">
                <label><span>Logo name</span><input id="new-logo-display-name" type="text" class="emby-input" placeholder="FlashBack TV"></label>
                <label><span>Image file</span><input id="new-logo-file" type="file" accept="image/png,image/jpeg,image/webp,.png,.jpg,.jpeg,.webp"></label>
                <button type="button" class="raised button-submit emby-button" id="btn-add-custom-logo">Add Logo</button>
            </div>
            <div id="custom-logo-entries">${rows || '<div class="empty-state">No logos uploaded yet.</div>'}</div>`,
            `<button type="button" class="emby-button" id="modal-close-logo-manager">Close</button>`
        );

        $('modal-close-logo-manager').onclick = () => {
            if (modal) {
                modal.classList.remove('modal-wide');
            }
            closeModal();
            loadLogos();
        };

        $('btn-add-custom-logo').onclick = () => uploadCustomLogo(setId, {
            displayNameInput: $('new-logo-display-name'),
            fileInput: $('new-logo-file'),
            refresh: () => openCustomLogoSetModal(setId)
        });

        qa('.logo-entry-row').forEach((row) => {
            const entryId = row.dataset.entryId;
            row.querySelector('.btn-save-logo-entry').onclick = () => saveCustomLogoName(setId, entryId, row.querySelector('.logo-entry-name').value, setId);
            row.querySelector('.btn-upload-logo-entry').onclick = () => {
                const fileInput = row.querySelector('.logo-entry-file');
                if (!fileInput.files || !fileInput.files[0]) {
                    toast('Choose a replacement image first.', 'error');
                    return;
                }

                replaceCustomLogo(setId, entryId, row.querySelector('.logo-entry-name').value, fileInput.files[0], setId);
            };
            row.querySelector('.btn-delete-logo-entry').onclick = () => deleteCustomLogoEntry(setId, entryId, setId);
        });
    }

    async function uploadCustomLogo(setId, options) {
        const displayName = (options.displayNameInput?.value || '').trim();
        const file = options.fileInput?.files && options.fileInput.files[0];
        if (!displayName) {
            toast('Logo name is required.', 'error');
            return;
        }

        if (!file) {
            toast('Choose an image file.', 'error');
            return;
        }

        const formData = new FormData();
        formData.append('displayName', displayName);
        formData.append('file', file);

        try {
            await apiForm('/logos/sets/' + setId + '/logos', formData, 'POST');
            toast('Logo uploaded.', 'success');
            if (options.displayNameInput) {
                options.displayNameInput.value = '';
            }
            if (options.fileInput) {
                options.fileInput.value = '';
            }
            if (options.refresh) {
                await options.refresh();
            } else {
                await loadLogos();
            }
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function saveCustomLogoName(setId, entryId, displayName, refreshSetId) {
        try {
            await api('/logos/sets/' + setId + '/entries/' + entryId, {
                method: 'PUT',
                body: { displayName: displayName }
            });
            toast('Logo name saved.', 'success');
            if (refreshSetId) {
                await openCustomLogoSetModal(refreshSetId);
            } else {
                await loadLogos();
            }
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function replaceCustomLogo(setId, entryId, displayName, file, refreshSetId) {
        try {
            await api('/logos/sets/' + setId + '/entries/' + entryId, { method: 'DELETE' });
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        const formData = new FormData();
        formData.append('displayName', displayName);
        formData.append('file', file);

        try {
            await apiForm('/logos/sets/' + setId + '/logos', formData, 'POST');
            toast('Logo replaced.', 'success');
            if (refreshSetId) {
                await openCustomLogoSetModal(refreshSetId);
            } else {
                await loadLogos();
            }
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function deleteCustomLogoEntry(setId, entryId, refreshSetId) {
        if (!confirm('Delete this logo?')) {
            return;
        }

        try {
            await api('/logos/sets/' + setId + '/entries/' + entryId, { method: 'DELETE' });
            toast('Logo deleted.', 'success');
            if (refreshSetId) {
                await openCustomLogoSetModal(refreshSetId);
            } else {
                await loadLogos();
            }
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function loadPresets() {
        try {
            presetNumberingMode = parseInt($('preset-numbering-mode').value, 10) || 0;
            channelPresets = await api('/channels/presets?numberingMode=' + presetNumberingMode) || [];
            renderPresetsList();
        } catch (err) {
            reportApiError(err, 'Could not load presets.');
        }
    }

    function renderPresetsList() {
        const el = $('presets-list');
        if (!channelPresets.length) {
            el.innerHTML = '<div class="empty-state">No presets available.</div>';
            return;
        }

        const altLabel = presetNumberingMode === 1 ? 'Legacy #' : 'Sub #';
        const categories = [...new Set(channelPresets.map((p) => p.category))];
        el.innerHTML = categories.map((category) => {
            const rows = channelPresets.filter((p) => p.category === category);
            return `<h4 style="margin:1rem 0 .5rem">${escapeHtml(category)}</h4>
                <table class="data-table">
                    <thead><tr><th>#</th><th>${altLabel}</th><th>Name</th><th>Description</th><th>Library Tag</th><th>Status</th></tr></thead>
                    <tbody>${rows.map((p) => `<tr>
                        <td><strong>${formatChannelNumber(p.number)}</strong></td>
                        <td>${formatChannelNumber(presetNumberingMode === 1 ? p.legacyNumber : p.subchannelNumber)}</td>
                        <td>${escapeHtml(p.name)}</td>
                        <td>${escapeHtml(p.description)}</td>
                        <td><code>${escapeHtml(p.libraryTag)}</code></td>
                        <td><span class="badge ${p.exists ? 'badge-on' : 'badge-off'}">${p.exists ? 'Exists' : 'Missing'}</span></td>
                    </tr>`).join('')}</tbody>
                </table>`;
        }).join('');
    }

    async function applyPresets() {
        if (!syncConfigPage()) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        const btn = $('btn-apply-presets');
        const resultEl = $('presets-result');
        const numberingEl = $('preset-numbering-mode');
        const updateExistingEl = $('preset-update-existing');
        const originalLabel = btn ? btn.textContent : '';

        if (!numberingEl) {
            toast('Preset controls are not loaded. Switch to the Presets tab and try again.', 'error');
            return;
        }

        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Creating…';
            }

            const updateExisting = !!updateExistingEl?.checked;
            presetNumberingMode = parseInt(numberingEl.value, 10) || 0;
            const result = await api('/channels/presets/apply', {
                method: 'POST',
                body: JSON.stringify({
                    numberingMode: presetNumberingMode,
                    skipExisting: !updateExisting,
                    updateExisting: updateExisting
                })
            });
            const lines = [];
            if (result?.created?.length) {
                lines.push(`Created ${result.created.length}: ${result.created.map((r) => formatChannelNumber(r.number) + ' ' + r.name).join(', ')}`);
            }
            if (result?.updated?.length) {
                lines.push(`Updated ${result.updated.length}: ${result.updated.map((r) => formatChannelNumber(r.number) + ' ' + r.name).join(', ')}`);
            }
            if (result?.skipped?.length) {
                lines.push(`Skipped ${result.skipped.length} existing channel(s).`);
            }
            if (resultEl) {
                resultEl.classList.remove('hidden');
                resultEl.textContent = lines.join('\n') || 'No changes made — all preset channels already exist.';
            }
            toast(lines[0] || 'All preset channels already exist.', lines.length ? 'success' : 'info');
            await loadPresets();
            await loadChannels();
        } catch (err) {
            reportApiError(err, 'Could not apply channel presets.');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Create Missing Channels';
            }
        }
    }

    function applySetupData(data) {
        $('setup-m3u').textContent = data.m3u || '';
        $('setup-epg').textContent = data.epg || '';
        $('setup-steps').innerHTML = (data.instructions || []).map((i) => `<li>${escapeHtml(i)}</li>`).join('');
        if ($('setup-m3u-test')) $('setup-m3u-test').href = data.m3u || '#';
        if ($('setup-epg-test')) $('setup-epg-test').href = data.epg || '#';
        if ($('setup-base-url')) $('setup-base-url').textContent = data.baseUrl || '';
    }

    function updateEbsLibraryFieldVisibility() {
        const source = Number($('ebs-music-source')?.value || $('setup-ebs-music-source')?.value || '1');
        const field = $('ebs-library-field') || $('setup-ebs-library-field');
        if (field) field.style.display = source === 1 ? '' : 'none';
    }

    function updateEbsFieldVisibility() {
        const displayMode = Number($('ebs-display-mode')?.value || '0');
        const slateVariantField = $('ebs-slate-variant-field');
        const slateVariantHint = $('ebs-slate-variant-hint');
        const musicSourceField = $('ebs-music-source')?.closest('.field');
        const musicLibraryField = $('ebs-library-field');
        const audioMode = Number($('ebs-audio-mode')?.value || '0');

        if (slateVariantField) {
            slateVariantField.style.display = displayMode === 0 ? '' : 'none';
        }
        if (slateVariantHint) {
            slateVariantHint.style.display = displayMode === 0 ? '' : 'none';
        }
        if (musicSourceField) {
            musicSourceField.style.display = audioMode !== 0 ? 'none' : '';
        }
        if (musicLibraryField) {
            musicLibraryField.style.display = audioMode !== 0 ? 'none' : '';
        }
        updateEbsLibraryFieldVisibility();
    }

    function populateEbsMusicLibraries(libraries, selectedId, selectedName, selectId) {
        const select = $(selectId || 'ebs-music-library') || $('setup-ebs-music-library');
        if (!select) return;

        const options = (libraries || []).map((lib) =>
            `<option value="${escapeHtml(String(lib.id))}">${escapeHtml(lib.name)}</option>`
        );
        select.innerHTML = options.join('');

        if (selectedId && [...select.options].some((opt) => opt.value === selectedId)) {
            select.value = selectedId;
            return;
        }

        const byName = [...select.options].find((opt) => opt.textContent === selectedName);
        if (byName) {
            select.value = byName.value;
        }
    }

    function renderEbsCustomSlateStatus(customSlates) {
        const usa = customSlates?.usa;
        const international = customSlates?.international;
        const usaEl = $('ebs-usa-status');
        const intlEl = $('ebs-international-status');
        if (usaEl) {
            usaEl.textContent = usa?.fileName
                ? `Custom upload: ${usa.fileName}`
                : 'Using bundled stock slate.';
        }
        if (intlEl) {
            intlEl.textContent = international?.fileName
                ? `Custom upload: ${international.fileName}`
                : 'Using bundled stock slate.';
        }
    }

    const AI_CATALOG_MODES = ['TV only', 'Movies only', 'Both', 'Music videos only'];
    function buildAiApplyPayload() {
        const source = aiPreview?.lineupSlots || aiPreview?.LineupSlots;
        if (!Array.isArray(source) || source.length === 0) {
            throw new Error('No AI lineup to apply. Generate a lineup first.');
        }

        const slots = source.map((slot) => ({
            SlotIndex: slot.slotIndex ?? slot.SlotIndex,
            SpanSlots: Math.max(1, Math.min(8, Number(slot.spanSlots ?? slot.SpanSlots ?? 1))),
            Candidates: (slot.candidates || slot.Candidates || []).map((c, index) => ({
                Kind: resolveEnumValue(SLOT_CANDIDATE_KIND_VALUES, c.kind ?? c.Kind, 0),
                JellyfinItemId: c.jellyfinItemId ?? c.JellyfinItemId ?? null,
                CollectionName: c.collectionName ?? c.CollectionName ?? null,
                FilterJson: c.filterJson ?? c.FilterJson ?? null,
                Weight: Number(c.weight ?? c.Weight ?? 1),
                SortOrder: Number(c.sortOrder ?? c.SortOrder ?? index)
            }))
        }));

        return { Slots: slots, RebuildPlayout: true };
    }

    function updateAiUiState() {
        const enabled = $('ai-enabled') ? !!$('ai-enabled').checked : !!(aiSettings && aiSettings.enabled);
        const note = $('ai-disabled-note');
        note?.classList.toggle('hidden', enabled);
        qa('.ai-action').forEach((el) => { el.disabled = !enabled; });
        qa('.ai-channel-row').forEach((row) => row.classList.toggle('disabled-row', !enabled));
        if ($('btn-ai-generate-all')) $('btn-ai-generate-all').disabled = !enabled;
        if ($('ai-auto-apply-channel-add')) $('ai-auto-apply-channel-add').disabled = !enabled;
        if ($('ai-auto-apply-all-on-save')) $('ai-auto-apply-all-on-save').disabled = !enabled;
    }

    function readAiSettingsFromForm() {
        return {
            enabled: !!$('ai-enabled')?.checked,
            autoApplyOnChannelAdd: !!$('ai-auto-apply-channel-add')?.checked,
            autoApplyToAllChannelsOnSave: !!$('ai-auto-apply-all-on-save')?.checked,
            defaultProvider: Number($('ai-default-provider')?.value || '0'),
            openAiModel: $('ai-openai-model')?.value?.trim() || 'gpt-4o-mini',
            veniceModel: $('ai-venice-model')?.value?.trim() || 'gpt-4o-mini',
            openAiApiKey: $('ai-openai-key')?.value?.trim() || null,
            veniceApiKey: $('ai-venice-key')?.value?.trim() || null
        };
    }

    async function loadAi() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            aiSettings = await api('/ai/settings');
            if ($('ai-enabled')) $('ai-enabled').checked = !!aiSettings.enabled;
            if ($('ai-auto-apply-channel-add')) $('ai-auto-apply-channel-add').checked = !!aiSettings.autoApplyOnChannelAdd;
            if ($('ai-auto-apply-all-on-save')) $('ai-auto-apply-all-on-save').checked = !!aiSettings.autoApplyToAllChannelsOnSave;
            if ($('ai-default-provider')) $('ai-default-provider').value = String(aiSettings.defaultProvider ?? 0);
            if ($('ai-openai-model')) $('ai-openai-model').value = aiSettings.openAiModel || 'gpt-4o-mini';
            if ($('ai-venice-model')) $('ai-venice-model').value = aiSettings.veniceModel || 'gpt-4o-mini';
            if ($('ai-openai-key')) $('ai-openai-key').value = '';
            if ($('ai-venice-key')) $('ai-venice-key').value = '';
            const keyStatus = $('ai-key-status');
            if (keyStatus) {
                keyStatus.textContent = `OpenAI key: ${aiSettings.hasOpenAiApiKey ? aiSettings.openAiApiKeyMasked : 'not set'} · Venice key: ${aiSettings.hasVeniceApiKey ? aiSettings.veniceApiKeyMasked : 'not set'}`;
            }
            aiChannels = await api('/ai/channels');
            aiPlayoutTemplates = await api('/ai/playout-templates');
            renderAiChannels();
            updateAiUiState();
            const job = await api('/ai/generate-all/status');
            renderGenerateAllStatus(job);
            if (job.isRunning) {
                startGenerateAllPolling();
            }
        } catch (err) {
            reportApiError(err, 'Could not load AI settings.');
        }
    }

    let aiGenerateAllPollTimer = null;
    let aiGenerateAllLastCompletedSteps = null;
    let aiGenerateAllIdlePolls = 0;

    function renderGenerateAllStatus(job) {
        const el = $('ai-generate-all-status');
        const cancelBtn = $('btn-ai-cancel-generate-all');
        if (!el || !job) {
            el?.classList.add('hidden');
            cancelBtn?.classList.add('hidden');
            return;
        }

        if (!job.isRunning && !job.completedAt) {
            el.classList.add('hidden');
            cancelBtn?.classList.add('hidden');
            return;
        }

        el.classList.remove('hidden');
        if (job.isRunning) {
            const totalSteps = job.totalSteps || 0;
            const pct = totalSteps ? Math.round((job.completedSteps / totalSteps) * 100) : 0;
            let statusLine =
                `Generate all: day ${job.currentDay}/${job.totalDays || 14} · ${job.currentChannelName || '…'} (all channels per day, then next day) · ` +
                `${job.completedSteps}/${totalSteps || '?'} steps (${pct}%)`;
            if (job.workerActive === false) {
                statusLine += ' · no background worker (stale — click Cancel to reset)';
            } else if (aiGenerateAllIdlePolls >= 6) {
                statusLine += ' · no recent progress (may be waiting on AI — click Cancel to stop)';
            }
            el.textContent = statusLine;
            if ($('btn-ai-generate-all')) {
                $('btn-ai-generate-all').disabled = true;
                $('btn-ai-generate-all').textContent = 'Generating…';
            }
            cancelBtn?.classList.remove('hidden');
            if (cancelBtn) {
                cancelBtn.disabled = false;
            }
            return;
        }

        cancelBtn?.classList.add('hidden');

        if (job.wasCancelled) {
            el.textContent =
                `Generate all cancelled after ${job.lineupsGenerated} lineup(s) and ${job.playoutDaysBuilt} playout day(s).`;
        } else if (job.wasStale) {
            el.textContent =
                `Generate all stopped at ${job.completedSteps}/${job.totalSteps || '?'} steps. ${job.lastError || 'Background task is no longer running.'}`;
        } else {
            let message = `Generate all finished: ${job.lineupsGenerated} lineups, ${job.playoutDaysBuilt} playout days built across ${job.totalChannels} channel(s) and ${job.totalDays} day(s).`;
            if (job.lineupsFailed || job.playoutDaysFailed) {
                message += ` Failures: ${job.lineupsFailed} lineup, ${job.playoutDaysFailed} day.`;
            }
            if (job.lastError) {
                message += ` Last error: ${job.lastError}`;
            }
            el.textContent = message;
        }

        if ($('btn-ai-generate-all')) {
            $('btn-ai-generate-all').disabled = !($('ai-enabled')?.checked);
            $('btn-ai-generate-all').textContent = 'Generate All Channels';
        }
    }

    function stopGenerateAllPolling() {
        if (aiGenerateAllPollTimer) {
            clearTimeout(aiGenerateAllPollTimer);
            aiGenerateAllPollTimer = null;
        }
    }

    function startGenerateAllPolling() {
        if (aiGenerateAllPollTimer) {
            clearTimeout(aiGenerateAllPollTimer);
        }
        aiGenerateAllPollTimer = setTimeout(pollGenerateAllStatus, 3000);
    }

    async function pollGenerateAllStatus() {
        try {
            const job = await api('/ai/generate-all/status');
            if (job.isRunning) {
                if (aiGenerateAllLastCompletedSteps === job.completedSteps) {
                    aiGenerateAllIdlePolls++;
                } else {
                    aiGenerateAllLastCompletedSteps = job.completedSteps;
                    aiGenerateAllIdlePolls = 0;
                }
            } else {
                aiGenerateAllLastCompletedSteps = null;
                aiGenerateAllIdlePolls = 0;
            }

            renderGenerateAllStatus(job);
            if (job.isRunning) {
                startGenerateAllPolling();
                return;
            }

            stopGenerateAllPolling();

            if (job.completedAt) {
                if (job.wasStale) {
                    toast(job.lastError || 'Generate all is no longer running. Status was reset.', 'info');
                } else if (job.wasCancelled) {
                    toast(
                        `Generate all cancelled after ${job.lineupsGenerated} lineup(s) and ${job.playoutDaysBuilt} playout day(s).`,
                        'info'
                    );
                } else {
                    toast(
                        `Generate all finished: ${job.lineupsGenerated} lineups and ${job.playoutDaysBuilt} playout days built.`,
                        job.lineupsFailed || job.playoutDaysFailed ? 'info' : 'success'
                    );
                }
                await loadAi();
            }
        } catch (_) {
            startGenerateAllPolling();
        }
    }

    async function cancelGenerateAll() {
        const cancelBtn = $('btn-ai-cancel-generate-all');
        try {
            if (cancelBtn) {
                cancelBtn.disabled = true;
            }

            const data = await api('/ai/generate-all/cancel', { method: 'POST', body: '{}' });
            if (data.cancelled) {
                toast('Cancel requested. Generate all will stop after the current channel/day step.', 'info');
            } else {
                toast('Generate all is not running. Status reset if it was stale.', 'info');
            }

            renderGenerateAllStatus(data.job);
            if (!data.job?.isRunning) {
                stopGenerateAllPolling();
            } else {
                startGenerateAllPolling();
            }
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (cancelBtn && $('ai-generate-all-status')?.textContent?.includes('Generate all:')) {
                cancelBtn.disabled = false;
            }
        }
    }

    function renderAiChannels() {
        const list = $('ai-channels-list');
        if (!list) return;
        if (!aiChannels.length) {
            list.innerHTML = '<div class="empty-state">No channels available for AI lineup generation.</div>';
            return;
        }

        list.innerHTML = aiChannels.map((ch) => {
            const templateOptions = (aiPlayoutTemplates || []).map((t) =>
                `<option value="${escapeHtml(t.id)}" ${(ch.aiPlayoutTemplateId || 'none') === t.id ? 'selected' : ''}>${escapeHtml(t.name)}</option>`
            ).join('');
            return `
            <div class="ai-channel-row" data-ai-channel="${ch.id}">
                <div class="row-top">
                    <div>
                        <strong>${escapeHtml(ch.number)} · ${escapeHtml(ch.name)}</strong>
                        <div class="meta">${escapeHtml(ch.libraryTag || 'no tag')} · ${ch.filledSlots}/48 slots filled</div>
                    </div>
                    <div class="row-actions">
                        <label class="field" style="margin:0">
                            <span>Content mix</span>
                            <select class="emby-input ai-catalog-mode" data-channel="${ch.id}">
                                <option value="0" ${ch.catalogMode === 0 ? 'selected' : ''}>TV only</option>
                                <option value="1" ${ch.catalogMode === 1 ? 'selected' : ''}>Movies only</option>
                                <option value="2" ${ch.catalogMode === 2 ? 'selected' : ''}>Both</option>
                                <option value="3" ${ch.catalogMode === 3 ? 'selected' : ''}>Music videos only</option>
                            </select>
                        </label>
                        <label class="field" style="margin:0">
                            <span>Playout template</span>
                            <select class="emby-input ai-playout-template ai-template-select" data-channel="${ch.id}">${templateOptions}</select>
                        </label>
                        <button type="button" class="emby-button ai-action ai-save-channel" data-channel="${ch.id}">Save</button>
                        <button type="button" class="emby-button ai-action ai-generate-channel" data-channel="${ch.id}">Generate</button>
                    </div>
                </div>
                ${ch.aiRuleBrief ? `<p class="hint">${escapeHtml(ch.aiRuleBrief)}</p>` : ''}
                <label class="field">
                    <span>Fine-tune prompt</span>
                    <textarea class="emby-input ai-fine-tune" data-channel="${ch.id}" placeholder="Optional extra instructions for this channel">${escapeHtml(ch.aiFineTunePrompt || '')}</textarea>
                </label>
            </div>`;
        }).join('');

        list.querySelectorAll('.ai-save-channel').forEach((btn) => {
            btn.onclick = () => saveAiChannelSettings(btn.dataset.channel);
        });
        list.querySelectorAll('.ai-generate-channel').forEach((btn) => {
            btn.onclick = () => generateAiLineup(btn.dataset.channel);
        });
        updateAiUiState();
    }

    async function saveAiSettings() {
        if (!syncConfigPage()) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        const btn = $('btn-save-ai-settings');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Saving…';
            }

            const form = readAiSettingsFromForm();
            const payload = {
                enabled: form.enabled,
                autoApplyOnChannelAdd: form.autoApplyOnChannelAdd,
                autoApplyToAllChannelsOnSave: form.autoApplyToAllChannelsOnSave,
                defaultProvider: form.defaultProvider,
                openAiModel: form.openAiModel,
                veniceModel: form.veniceModel
            };
            if (form.openAiApiKey) payload.openAiApiKey = form.openAiApiKey;
            if (form.veniceApiKey) payload.veniceApiKey = form.veniceApiKey;
            const response = await api('/ai/settings', { method: 'PUT', body: JSON.stringify(payload) });
            aiSettings = response.settings || response;
            if (response.applyAll?.queued) {
                toast('AI settings saved. Apply-to-all is running in the background.', 'success');
            } else if (response.applyAll) {
                const summary = response.applyAll;
                toast(
                    `AI settings saved. Applied to ${summary.ok} channel(s)` +
                    (summary.failed ? `, ${summary.failed} failed` : '') +
                    (summary.skipped ? `, ${summary.skipped} skipped` : '') +
                    '.',
                    summary.failed ? 'info' : 'success'
                );
            } else {
                toast('AI settings saved.', 'success');
            }
            await loadAi();
        } catch (err) {
            reportApiError(err, 'Could not save AI settings.');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Save AI Settings';
            }
        }
    }

    async function testAiConnection() {
        if (!syncConfigPage()) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        const btn = $('btn-test-ai');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Testing…';
            }
            const form = readAiSettingsFromForm();
            const payload = {
                provider: form.defaultProvider
            };
            if (form.openAiApiKey) payload.openAiApiKey = form.openAiApiKey;
            if (form.veniceApiKey) payload.veniceApiKey = form.veniceApiKey;
            const data = await api('/ai/settings/test', {
                method: 'POST',
                body: JSON.stringify(payload)
            });
            toast(`Connected to ${data.provider}.`, 'success');
        } catch (err) {
            reportApiError(err, 'AI connection test failed.');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Test Connection';
            }
        }
    }

    async function saveAiChannelSettings(channelId) {
        if (!syncConfigPage()) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        const row = q(`[data-ai-channel="${channelId}"]`);
        if (!row) return;
        try {
            await api('/ai/channels/' + channelId + '/fine-tune', {
                method: 'PUT',
                body: JSON.stringify({
                    aiFineTunePrompt: row.querySelector('.ai-fine-tune')?.value || '',
                    catalogMode: Number(row.querySelector('.ai-catalog-mode')?.value || '0'),
                    aiPlayoutTemplateId: row.querySelector('.ai-playout-template')?.value || 'none'
                })
            });
            toast('Channel AI settings saved.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function generateAiLineup(channelId) {
        if (!syncConfigPage()) {
            toast('FinTV admin page is not ready. Close and reopen FinTV settings.', 'error');
            return;
        }

        const row = q(`[data-ai-channel="${channelId}"]`);
        if (row) {
            await saveAiChannelSettings(channelId);
        }
        const btn = row?.querySelector('.ai-generate-channel');
        if (btn) btn.disabled = true;
        try {
            aiPreview = await api('/ai/channels/' + channelId + '/generate', { method: 'POST', body: '{}' });
            renderAiPreview();
            toast('AI lineup generated. Review the preview below.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (btn) btn.disabled = !(aiSettings && aiSettings.enabled);
        }
    }

    function renderAiPreview() {
        const panel = $('ai-preview-panel');
        if (!aiPreview || !panel) return;
        panel.classList.remove('hidden');
        $('ai-preview-title').textContent = `Preview: ${aiPreview.channelName}`;
        const templateLabel = aiPreview.playoutTemplateName && aiPreview.playoutTemplateId !== 'none'
            ? ` · Template: ${aiPreview.playoutTemplateName}`
            : '';
        $('ai-preview-summary').textContent = `AI chose from ${aiPreview.catalogSummary.includedInPrompt} of ${aiPreview.catalogSummary.totalAvailable} tagged items · ${AI_CATALOG_MODES[aiPreview.catalogMode] || 'Mixed'} mode${templateLabel} · builds up to 14 days of playout when applied with rebuild`;
        const grid = $('ai-preview-grid');
        const occupied = new Array(48).fill(false);
        const blocks = (aiPreview.slots || []).filter((s) => (s.title && s.title !== 'Filter fallback') || s.jellyfinItemId);
        let html = '';
        for (let i = 0; i < 48; i++) {
            if (occupied[i]) continue;
            const block = blocks.find((s) => s.slotIndex === i);
            if (!block) {
                html += `<div class="slot-card empty"><div class="time">${slotTime(i)}</div><div class="summary">Open</div></div>`;
                occupied[i] = true;
                continue;
            }
            const span = Math.max(1, block.spanSlots || 1);
            for (let j = i; j < i + span && j < 48; j++) occupied[j] = true;
            const duration = span * 30;
            html += `<div class="slot-card has-items span-block" style="--slot-span:${span};grid-column:span ${span}">
                <div class="time">${slotTime(i)} · ${duration}m</div>
                <div class="summary">${escapeHtml(block.title)}</div>
                <div class="count">${escapeHtml(block.type || '')}${block.runtimeMinutes ? ' · ' + block.runtimeMinutes + 'm' : ''}${block.daypartName ? `<span class="ai-daypart-badge">${escapeHtml(block.daypartName)}</span>` : ''}</div>
            </div>`;
        }
        grid.innerHTML = html;
        updateAiUiState();
    }

    async function applyAiLineup() {
        if (!aiPreview) return;
        try {
            await api('/ai/channels/' + aiPreview.channelId + '/apply', {
                method: 'POST',
                body: JSON.stringify(buildAiApplyPayload())
            });
            toast('Lineup applied and Live TV guide playout rebuilt.', 'success');
            discardAiPreview();
            await loadAi();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function discardAiPreview() {
        aiPreview = null;
        $('ai-preview-panel')?.classList.add('hidden');
        if ($('ai-preview-grid')) $('ai-preview-grid').innerHTML = '';
    }

    async function generateAllAiLineups() {
        if (!confirm('Generate AI lineups for all channels? Each channel gets an AI lineup, then playout is built one day at a time (14 days total). This runs in the background.')) return;
        const btn = $('btn-ai-generate-all');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Queueing…';
            }
            const data = await api('/ai/generate-all', { method: 'POST', body: '{}' });
            if (data.alreadyRunning) {
                toast('Generate all is already running.', 'info');
                renderGenerateAllStatus(data.job);
                startGenerateAllPolling();
                return;
            }
            if (data.queued) {
                toast('Generate all queued. Building one channel and one day at a time.', 'success');
                renderGenerateAllStatus(data.job);
                startGenerateAllPolling();
                return;
            }
            const failed = (data.results || []).filter((r) => !r.ok && !r.skipped);
            const ok = (data.results || []).filter((r) => r.ok).length;
            const fail = failed.length;
            const skipped = (data.results || []).filter((r) => r.skipped).length;
            let message = `Generate all finished: ${ok} succeeded, ${fail} failed${skipped ? `, ${skipped} skipped` : ''}.`;
            if (fail && failed[0]?.error) {
                const sample = failed[0].name ? `${failed[0].name}: ${failed[0].error}` : failed[0].error;
                message += ` First error: ${sample}`;
            }
            toast(message, fail ? 'info' : 'success');
            await loadAi();
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (btn && !$('ai-generate-all-status')?.textContent?.includes('running')) {
                btn.disabled = !($('ai-enabled')?.checked);
                btn.textContent = originalLabel || 'Generate All Channels';
            }
        }
    }

    async function loadEbs() {
        try {
            const settings = await api('/ebs/settings');
            if ($('ebs-display-mode')) $('ebs-display-mode').value = String(settings.ebsDisplayMode ?? 0);
            if ($('ebs-audio-mode')) $('ebs-audio-mode').value = String(settings.ebsAudioMode ?? 0);
            if ($('ebs-slate-variant')) $('ebs-slate-variant').value = String(settings.ebsSlateVariant ?? 0);
            if ($('ebs-music-source')) $('ebs-music-source').value = String(settings.ebsBackgroundMusicSource ?? 1);
            populateEbsMusicLibraries(
                settings.musicLibraries,
                settings.ebsBackgroundMusicLibraryId || '',
                settings.ebsBackgroundMusicLibraryName || 'Background Music',
                'ebs-music-library'
            );
            renderEbsCustomSlateStatus(settings.customSlates);
            updateEbsFieldVisibility();
        } catch (err) {
            reportApiError(err, 'Could not load EBS settings.');
        }
    }

    async function saveEbsSettings() {
        const librarySelect = $('ebs-music-library');
        const selectedOption = librarySelect?.selectedOptions?.[0];
        try {
            await api('/ebs/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    ebsDisplayMode: Number($('ebs-display-mode')?.value || '0'),
                    ebsAudioMode: Number($('ebs-audio-mode')?.value || '0'),
                    ebsSlateVariant: Number($('ebs-slate-variant')?.value || '0'),
                    ebsBackgroundMusicSource: Number($('ebs-music-source')?.value || '1'),
                    ebsBackgroundMusicLibraryId: selectedOption?.value || null,
                    ebsBackgroundMusicLibraryName: selectedOption?.textContent?.trim() || 'Background Music'
                })
            });
            toast('EBS settings saved.', 'success');
            await loadEbs();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function uploadEbsSlate(variant, inputId) {
        const input = $(inputId);
        const file = input?.files?.[0];
        if (!file) {
            toast('Choose a PNG or JPG image first.', 'error');
            return;
        }

        const formData = new FormData();
        formData.append('file', file);
        try {
            const data = await apiForm('/ebs/slates/' + variant, formData, 'POST');
            renderEbsCustomSlateStatus(data.customSlates);
            if (input) input.value = '';
            toast('Custom EBS slate uploaded.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function removeEbsSlate(variant) {
        try {
            const data = await api('/ebs/slates/' + variant, { method: 'DELETE' });
            renderEbsCustomSlateStatus(data.customSlates);
            toast('Custom EBS slate removed.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function renderWeatherDockerStatus(status) {
        weatherDockerStatus = status;
        const renderPanel = (elId, info, usingLocal) => {
            const el = $(elId);
            if (!el || !info) return;

            let stateLine;
            if (!info.dockerAvailable) {
                stateLine = 'Docker not available — install Docker and ensure Jellyfin can run docker';
            } else if (!info.running) {
                stateLine = 'Stopped';
            } else if (info.httpReachable) {
                stateLine = `Running · HTTP reachable at ${info.baseUrl}`;
            } else if (info.statusMessage) {
                stateLine = info.statusMessage;
            } else {
                stateLine = 'Running but HTTP not reachable from Jellyfin — click Stop, then Start';
            }

            const networkLine = info.jellyfinInDocker
                ? (info.sharesJellyfinNetwork
                    ? `Jellyfin in Docker · sharing network namespace${info.jellyfinContainerRef ? ' · ' + info.jellyfinContainerRef : ''}${info.sidecarNetworkParent ? ' · parent ' + info.sidecarNetworkParent : ''}`
                    : 'Jellyfin in Docker · host-published port')
                : 'Jellyfin on host';

            const detailLine = info.running && !info.httpReachable && info.httpListeningInsideSidecar
                ? '<div class="meta">WeatherStar responds inside the container but not from Jellyfin — stale network attachment is likely.</div>'
                : '';

            el.innerHTML = `<div>${escapeHtml(stateLine)}</div><div class="meta">${escapeHtml(info.containerName)} · ${escapeHtml(info.image)} · port ${info.hostPort} · ${escapeHtml(networkLine)}${usingLocal ? ' · active URL' : ''}</div>${detailLine}`;
        };
        renderPanel('ws4kp-docker-status', status?.ws4kp, status?.usingLocalWs4kp);
        renderPanel('ws3kp-docker-status', status?.ws3kp, status?.usingLocalWs3kp);
        const dockerOk = !!(status?.ws4kp?.dockerAvailable || status?.ws3kp?.dockerAvailable);
        ['btn-ws4kp-start', 'btn-ws4kp-stop', 'btn-ws3kp-start', 'btn-ws3kp-stop'].forEach((id) => {
            const btn = $(id);
            if (btn) btn.disabled = !dockerOk;
        });
    }

    async function loadWeather() {
        try {
            const settings = await api('/setup/settings');
            if ($('weather-base-url')) {
                $('weather-base-url').value = settings.weatherStarBaseUrl || '';
            }
            if ($('weather-permalink-query')) {
                $('weather-permalink-query').value = settings.weatherStarPermalinkQuery || '';
            }
            if ($('ws4kp-host-port')) $('ws4kp-host-port').value = settings.ws4kpHostPort ?? 8080;
            if ($('ws4kp-image')) $('ws4kp-image').value = settings.ws4kpImage || 'ghcr.io/netbymatt/ws4kp';
            if ($('ws3kp-host-port')) $('ws3kp-host-port').value = settings.ws3kpHostPort ?? 8083;
            if ($('ws3kp-image')) $('ws3kp-image').value = settings.ws3kpImage || 'ghcr.io/netbymatt/ws3kp';
            if ($('weather-auto-start-ws4kp')) {
                $('weather-auto-start-ws4kp').checked = !!settings.autoStartWeatherStarDocker;
            }
            if ($('weather-auto-wide-169')) {
                $('weather-auto-wide-169').checked = settings.weatherStarAutoWideForSixteenNine !== false;
            }
            const dockerStatus = await api('/weather/docker/status');
            renderWeatherDockerStatus(dockerStatus);
        } catch (err) {
            reportApiError(err, 'Could not load weather settings.');
        }
    }

    async function startWeatherDocker(variant, updateBaseUrl) {
        const isWs4kp = variant === 'ws4kp';
        const hostPort = Number((isWs4kp ? $('ws4kp-host-port') : $('ws3kp-host-port'))?.value || (isWs4kp ? 8080 : 8083));
        const image = (isWs4kp ? $('ws4kp-image') : $('ws3kp-image'))?.value?.trim();
        try {
            const data = await api('/weather/docker/' + variant + '/start', {
                method: 'POST',
                body: JSON.stringify({ hostPort, image: image || null, updateBaseUrl: !!updateBaseUrl })
            });
            renderWeatherDockerStatus(data);
            if (updateBaseUrl && data.configuredBaseUrl) {
                applyWeatherBaseUrl(data.configuredBaseUrl);
            }
            toast(`${variant} started.`, 'success');
        } catch (err) {
            reportApiError(err, 'Could not start WeatherStar Docker.');
        }
    }

    async function stopWeatherDocker(variant) {
        try {
            const data = await api('/weather/docker/' + variant + '/stop', { method: 'POST', body: '{}' });
            renderWeatherDockerStatus(data);
            toast(`${variant} stopped.`, 'success');
        } catch (err) {
            reportApiError(err, 'Could not stop WeatherStar Docker.');
        }
    }

    async function saveWeatherSettings() {
        const weatherStarBaseUrl = $('weather-base-url')?.value.trim() || '';
        const weatherStarPermalinkQuery = $('weather-permalink-query')?.value.trim() || '';
        const weatherStarFullPermalink = $('weather-full-permalink')?.value.trim() || '';
        try {
            await api('/setup/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    weatherStarBaseUrl: weatherStarBaseUrl || null,
                    weatherStarPermalinkQuery: weatherStarPermalinkQuery || null,
                    weatherStarFullPermalink: weatherStarFullPermalink || null,
                    autoStartWeatherStarDocker: !!$('weather-auto-start-ws4kp')?.checked,
                    weatherStarAutoWideForSixteenNine: !!$('weather-auto-wide-169')?.checked
                })
            });
            toast('Weather settings saved.', 'success');
            await loadWeather();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function importWeatherPermalink() {
        const permalink = $('weather-full-permalink')?.value.trim();
        if (!permalink) {
            toast('Paste a full WeatherStar permalink first.', 'error');
            return;
        }

        try {
            const split = splitWeatherPermalink(permalink);
            if ($('weather-base-url')) {
                $('weather-base-url').value = split.baseUrl;
            }
            if ($('weather-permalink-query')) {
                $('weather-permalink-query').value = split.query;
            }
            toast('Permalink imported into base URL and display settings.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function useTestWeatherUrl() {
        applyWeatherBaseUrl('https://weather.jmthornton.net');
    }

    function applyWeatherBaseUrl(url) {
        const input = $('weather-base-url');
        if (input) {
            input.value = url;
        }
    }

    function bindWeatherUrlPresets() {
        qa('.weather-url-preset').forEach((btn) => {
            btn.onclick = () => applyWeatherBaseUrl(btn.dataset.url || '');
        });
    }

    function renderPlaywrightDockerStatus(status) {
        playwrightDockerStatus = status;
        const el = $('playwright-docker-status');
        if (!el || !status) {
            return;
        }

        let stateLine;
        if (!status.dockerAvailable) {
            stateLine = 'Docker not available — install Docker and ensure Jellyfin can run docker';
        } else if (!status.running) {
            stateLine = 'Stopped';
        } else if (status.cdpReachable) {
            stateLine = `Running · CDP reachable at ${status.cdpEndpoint}`;
        } else if (status.statusMessage) {
            stateLine = status.statusMessage;
        } else {
            stateLine = 'Running but CDP not reachable from Jellyfin — click Stop, then Start';
        }

        const networkLine = status.jellyfinInDocker
            ? (status.sharesJellyfinNetwork
                ? `Jellyfin in Docker · sharing network namespace${status.jellyfinContainerRef ? ' · ' + status.jellyfinContainerRef : ''}${status.sidecarNetworkParent ? ' · parent ' + status.sidecarNetworkParent : ''}`
                : 'Jellyfin in Docker · host-published CDP port')
            : 'Jellyfin on host';

        const detailLine = status.running && !status.cdpReachable && status.chromeListeningInsideSidecar
            ? '<div class="meta">Chrome responds inside the sidecar but not from Jellyfin — stale network attachment is likely.</div>'
            : '';

        el.innerHTML = `<div>${escapeHtml(stateLine)}</div><div class="meta">${escapeHtml(status.containerName)} · ${escapeHtml(status.image)} · port ${status.cdpPort} · ${escapeHtml(networkLine)}</div>${detailLine}`;

        const dockerOk = !!status.dockerAvailable;
        ['btn-playwright-start', 'btn-playwright-stop'].forEach((id) => {
            const btn = $(id);
            if (btn) {
                btn.disabled = !dockerOk;
            }
        });
    }

    async function loadPlaywright() {
        try {
            const status = await api('/playwright/docker/status');
            if ($('playwright-auto-start')) {
                $('playwright-auto-start').checked = !!status.autoStartOnJellyfinStartup;
            }
            renderPlaywrightDockerStatus(status);
        } catch (err) {
            reportApiError(err, 'Could not load Playwright settings.');
        }
    }

    async function startPlaywrightDocker() {
        try {
            const data = await api('/playwright/docker/start', { method: 'POST', body: '{}' });
            renderPlaywrightDockerStatus(data);
            toast('Playwright sidecar started.', 'success');
        } catch (err) {
            reportApiError(err, 'Could not start Playwright sidecar.');
        }
    }

    async function stopPlaywrightDocker() {
        try {
            const data = await api('/playwright/docker/stop', { method: 'POST', body: '{}' });
            renderPlaywrightDockerStatus(data);
            toast('Playwright sidecar stopped.', 'success');
        } catch (err) {
            reportApiError(err, 'Could not stop Playwright sidecar.');
        }
    }

    async function savePlaywrightSettings() {
        try {
            await api('/setup/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    autoStartPlaywrightDockerSidecar: !!$('playwright-auto-start')?.checked
                })
            });
            toast('Playwright settings saved.', 'success');
            await loadPlaywright();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function loadSetup() {
        try {
            const data = await api('/setup/urls');
            applySetupData(data);
            try {
                const settings = await api('/setup/settings');
                if ($('setup-public-base')) $('setup-public-base').value = settings.publicBaseUrl || '';
            } catch (settingsErr) {
                console.warn('Could not load setup settings', settingsErr);
            }
        } catch (err) {
            applySetupData({ m3u: '', epg: '', instructions: [] });
            toast('Failed to load Live TV URLs: ' + err.message, 'error');
        }
    }

    async function saveSetupSettings() {
        const publicBaseUrl = $('setup-public-base').value.trim();
        try {
            const data = await api('/setup/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    publicBaseUrl: publicBaseUrl || null
                })
            });
            applySetupData(data);
            if ($('setup-public-base')) $('setup-public-base').value = publicBaseUrl;
            toast('Live TV URLs updated.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function normalizeConfigPageRoot(node) {
        if (!node) {
            return null;
        }

        if (node.id === 'FinTVConfigPage') {
            return node;
        }

        if (typeof node.querySelector === 'function') {
            const nested = node.querySelector('#FinTVConfigPage');
            if (nested) {
                return nested;
            }
        }

        if (typeof node.closest === 'function') {
            return node.closest('#FinTVConfigPage');
        }

        return null;
    }

    function syncConfigPage(preferred) {
        const resolved = resolveConfigPage(preferred || configPage);
        if (resolved) {
            configPage = resolved;
        }

        return configPage;
    }

    async function loadLists() {
        try {
            await ensureFinTvLists(true);
            renderListsTable();
        } catch (err) {
            reportApiError(err, 'Could not load FinTV lists.');
        }
    }

    function renderListsTable() {
        const el = $('lists-table');
        if (!finTvLists.length) {
            el.innerHTML = '<div class="empty-state">No FinTV lists registered yet. Add a Jellyfin playlist to use it in lineups.</div>';
            return;
        }

        el.innerHTML = finTvLists.map((list) => {
            const mode = list.playbackMode === 1 ? 'Random' : 'Sequential';
            return `<div class="list-card">
                <div>
                    <strong>${escapeHtml(list.name)}</strong>
                    <div class="meta">${list.itemCount || 0} items · ${mode}</div>
                </div>
                <div class="row-actions">
                    <button type="button" data-edit-list="${list.id}">Edit</button>
                    <button type="button" data-delete-list="${list.id}">Delete</button>
                </div>
            </div>`;
        }).join('');

        el.querySelectorAll('[data-edit-list]').forEach((btn) => {
            btn.onclick = () => openListForm(btn.dataset.editList);
        });
        el.querySelectorAll('[data-delete-list]').forEach((btn) => {
            btn.onclick = () => deleteList(btn.dataset.deleteList);
        });
    }

    async function openListForm(editId) {
        const existing = editId ? finTvLists.find((l) => l.id === editId) : null;
        let jellyfinOptions = '';

        if (!existing) {
            const playlists = await api('/lists/jellyfin-playlists?unregisteredOnly=true') || [];
            if (!playlists.length) {
                toast('No unregistered Jellyfin playlists found.', 'info');
                return;
            }

            jellyfinOptions = playlists.map((p) =>
                `<option value="${p.id}">${escapeHtml(p.name)} (${p.itemCount} items)</option>`).join('');
        }

        const body = existing
            ? `<label class="field"><span>Name</span><input id="list-name" class="emby-input" value="${escapeHtml(existing.name)}"></label>
               <label class="field"><span>Playback mode</span>
                 <select id="list-mode" class="emby-select">
                   <option value="0"${existing.playbackMode === 0 ? ' selected' : ''}>Sequential</option>
                   <option value="1"${existing.playbackMode === 1 ? ' selected' : ''}>Random</option>
                 </select></label>`
            : `<label class="field"><span>Jellyfin playlist</span>
                 <select id="list-jellyfin-id" class="emby-select">${jellyfinOptions}</select></label>
               <label class="field"><span>Display name (optional)</span><input id="list-name" class="emby-input"></label>
               <label class="field"><span>Playback mode</span>
                 <select id="list-mode" class="emby-select">
                   <option value="0">Sequential</option>
                   <option value="1">Random</option>
                 </select></label>`;

        openModal(existing ? 'Edit FinTV List' : 'Add FinTV List', body, `
            <button type="button" class="emby-button" id="list-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="list-save">Save</button>`);

        document.getElementById('list-cancel').onclick = closeModal;
        document.getElementById('list-save').onclick = async () => {
            try {
                if (existing) {
                    await api('/lists/' + existing.id, {
                        method: 'PUT',
                        body: JSON.stringify({
                            name: document.getElementById('list-name').value.trim(),
                            playbackMode: parseInt(document.getElementById('list-mode').value, 10)
                        })
                    });
                } else {
                    await api('/lists', {
                        method: 'POST',
                        body: JSON.stringify({
                            jellyfinPlaylistId: document.getElementById('list-jellyfin-id').value,
                            name: document.getElementById('list-name').value.trim(),
                            playbackMode: parseInt(document.getElementById('list-mode').value, 10)
                        })
                    });
                }

                closeModal();
                toast('List saved.', 'success');
                await loadLists();
            } catch (err) {
                reportApiError(err, 'Could not save list.');
            }
        };
    }

    async function deleteList(id) {
        if (!confirm('Remove this FinTV list registration?')) return;
        try {
            await api('/lists/' + id, { method: 'DELETE' });
            toast('List removed.', 'success');
            await loadLists();
        } catch (err) {
            reportApiError(err, 'Could not delete list.');
        }
    }

    function populateSpecialChannelSelect() {
        const select = $('special-channel-select');
        if (!select) return;
        select.innerHTML = channels
            .filter((c) => c.contentType !== CONTENT_TYPE_VALUES.Weather)
            .map((c) => `<option value="${c.id}">${escapeHtml(formatChannelNumber(c.number) + ' · ' + c.name)}</option>`)
            .join('');
        if (!specialChannelId && select.options.length) {
            specialChannelId = select.value;
        } else if (specialChannelId) {
            select.value = specialChannelId;
        }
    }

    async function loadSpecialPresentations() {
        populateSpecialChannelSelect();
        specialChannelId = $('special-channel-select').value;
        if (!specialChannelId) {
            $('special-list').innerHTML = '<div class="empty-state">Create a non-weather channel first.</div>';
            return;
        }

        try {
            await ensureFinTvLists();
            specialPresentations = await api('/special-presentations/' + specialChannelId) || [];
            renderSpecialPresentationList();
        } catch (err) {
            reportApiError(err, 'Could not load special presentations.');
        }
    }

    function presentationSummary(p) {
        const candidates = p.candidates || [];
        if (!candidates.length) return 'No content';
        if (candidates.length === 1) return candidateSummary(candidates[0]);
        return `${candidates.length} candidates`;
    }

    function renderSpecialPresentationList() {
        const el = $('special-list');
        if (!specialPresentations.length) {
            el.innerHTML = '<div class="empty-state">No special presentations configured for this channel.</div>';
            return;
        }

        el.innerHTML = specialPresentations.map((p) => {
            const span = Math.max(1, p.spanSlots || 1);
            return `<div class="presentation-card">
                <div>
                    <strong>${escapeHtml(p.name)}</strong>${p.enabled ? '' : ' <span class="meta">(disabled)</span>'}
                    <div class="meta">${DAYS[p.dayOfWeek]} · ${slotTimeInputValue(p.slotIndex)} · ${span * 30} min · ${escapeHtml(presentationSummary(p))}</div>
                </div>
                <div class="row-actions">
                    <button type="button" data-edit-special="${p.id}">Edit</button>
                    <button type="button" data-delete-special="${p.id}">Delete</button>
                </div>
            </div>`;
        }).join('');

        el.querySelectorAll('[data-edit-special]').forEach((btn) => {
            btn.onclick = () => openSpecialPresentationForm(btn.dataset.editSpecial);
        });
        el.querySelectorAll('[data-delete-special]').forEach((btn) => {
            btn.onclick = () => deleteSpecialPresentation(btn.dataset.deleteSpecial);
        });
    }

    function buildRuleFilterJson() {
        const tags = (document.getElementById('sp-tags')?.value || '')
            .split(',')
            .map((t) => t.trim())
            .filter(Boolean);
        const filter = {};
        const genre = document.getElementById('sp-genre')?.value.trim();
        const titleContains = document.getElementById('sp-title')?.value.trim();
        const minYear = document.getElementById('sp-min-year')?.value;
        const maxYear = document.getElementById('sp-max-year')?.value;
        const minRating = document.getElementById('sp-min-rating')?.value.trim();
        const maxRating = document.getElementById('sp-max-rating')?.value.trim();
        if (genre) filter.genre = genre;
        if (tags.length) filter.tags = tags;
        if (titleContains) filter.titleContains = titleContains;
        if (minYear) filter.minYear = parseInt(minYear, 10);
        if (maxYear) filter.maxYear = parseInt(maxYear, 10);
        if (minRating) filter.minRating = minRating;
        if (maxRating) filter.maxRating = maxRating;
        return JSON.stringify(filter);
    }

    async function openSpecialPresentationForm(editId) {
        const existing = editId ? specialPresentations.find((p) => p.id === editId) : null;
        await ensureFinTvLists();
        const channel = channels.find((c) => c.id === specialChannelId);
        const draft = existing
            ? JSON.parse(JSON.stringify(existing))
            : { name: '', enabled: true, dayOfWeek: 1, slotIndex: 36, spanSlots: 2, candidates: [] };

        let contentMode = 0;
        if (draft.candidates?.length === 1) {
            if (draft.candidates[0].kind === 2) contentMode = 1;
            if (draft.candidates[0].kind === 3) contentMode = 2;
        }

        const body = `
            <label class="field"><span>Name</span><input id="sp-name" class="emby-input" value="${escapeHtml(draft.name || '')}"></label>
            <label class="field checkbox-field"><input id="sp-enabled" type="checkbox"${draft.enabled !== false ? ' checked' : ''}><span>Enabled</span></label>
            <label class="field"><span>Day of week</span>
                <select id="sp-day" class="emby-select">${DAYS.map((d, i) =>
                    `<option value="${i}"${draft.dayOfWeek === i ? ' selected' : ''}>${d}</option>`).join('')}</select></label>
            <label class="field"><span>Start time</span><input id="sp-time" type="time" class="emby-input" value="${slotTimeInputValue(draft.slotIndex || 0)}"></label>
            <label class="field"><span>Block length (30-min slots)</span><input id="sp-span" type="number" min="1" max="8" class="emby-input" value="${Math.max(1, draft.spanSlots || 1)}"></label>
            <label class="field"><span>Content mode</span>
                <select id="sp-content-mode" class="emby-select">
                    <option value="0">Fixed items</option>
                    <option value="1">Rule-based</option>
                    <option value="2">FinTV list</option>
                </select></label>
            <div id="sp-content-panel"></div>`;

        openModal(existing ? 'Edit Special Presentation' : 'Add Special Presentation', body, `
            <button type="button" class="emby-button" id="sp-cancel">Cancel</button>
            <button type="button" class="raised button-submit emby-button" id="sp-save">Save</button>`);

        document.getElementById('sp-content-mode').value = String(contentMode);
        draft.candidates = draft.candidates || [];

        function renderContentPanel() {
            const mode = parseInt(document.getElementById('sp-content-mode').value, 10);
            const panel = document.getElementById('sp-content-panel');
            if (mode === 1) {
                let filter = {};
                try { filter = JSON.parse(draft.candidates[0]?.filterJson || '{}'); } catch (e) { filter = {}; }
                panel.innerHTML = `
                    <label class="field"><span>Genre</span><input id="sp-genre" class="emby-input" value="${escapeHtml(filter.genre || '')}"></label>
                    <label class="field"><span>Tags (comma-separated)</span><input id="sp-tags" class="emby-input" value="${escapeHtml((filter.tags || []).join(', '))}"></label>
                    <label class="field"><span>Title contains</span><input id="sp-title" class="emby-input" value="${escapeHtml(filter.titleContains || '')}"></label>
                    <div class="form-grid">
                        <label class="field"><span>Min year</span><input id="sp-min-year" type="number" class="emby-input" value="${filter.minYear || ''}"></label>
                        <label class="field"><span>Max year</span><input id="sp-max-year" type="number" class="emby-input" value="${filter.maxYear || ''}"></label>
                    </div>
                    <div class="form-grid">
                        <label class="field"><span>Min rating</span><input id="sp-min-rating" class="emby-input" placeholder="PG" value="${escapeHtml(filter.minRating || '')}"></label>
                        <label class="field"><span>Max rating</span><input id="sp-max-rating" class="emby-input" placeholder="PG-13" value="${escapeHtml(filter.maxRating || '')}"></label>
                    </div>`;
                return;
            }

            if (mode === 2) {
                panel.innerHTML = `<label class="field"><span>FinTV list</span>
                    <select id="sp-list-id" class="emby-select">
                        ${finTvLists.map((l) => `<option value="${l.id}"${draft.candidates[0]?.finTvListId === l.id ? ' selected' : ''}>${escapeHtml(l.name)}</option>`).join('')}
                    </select></label>`;
                return;
            }

            panel.innerHTML = `
                <div id="sp-candidates" class="candidate-list">${renderCandidateRows(draft.candidates)}</div>
                <label class="field"><span>Search library</span>
                    <input id="sp-search" type="search" class="emby-input" placeholder="Type at least 2 characters…"></label>
                <div id="sp-search-results" class="search-results"></div>`;

            bindCandidateRowActions(draft, 'sp-candidates');
            let timer;
            document.getElementById('sp-search').oninput = (ev) => {
                clearTimeout(timer);
                timer = setTimeout(async () => {
                    const q = ev.target.value;
                    const resultsEl = document.getElementById('sp-search-results');
                    if (!q || q.trim().length < 2) {
                        resultsEl.innerHTML = '';
                        return;
                    }
                    const params = new URLSearchParams({ q: q.trim(), limit: '20' });
                    if (channel) params.set('contentType', channel.contentType);
                    const results = await api('/catalog/search?' + params.toString());
                    resultsEl.innerHTML = (results || []).map((item) =>
                        `<div class="search-result" data-id="${item.id}">
                            <strong>${escapeHtml(item.name)}</strong>
                            <div class="sub">${escapeHtml(item.type)}</div>
                        </div>`).join('') || '<div class="search-result">No matches</div>';
                    resultsEl.querySelectorAll('.search-result[data-id]').forEach((row) => {
                        row.onclick = () => {
                            itemTitleCache[row.dataset.id] = row.querySelector('strong').textContent;
                            draft.candidates.push({ kind: 0, jellyfinItemId: row.dataset.id, weight: 1, sortOrder: draft.candidates.length });
                            refreshCandidateList(draft, 'sp-candidates');
                        };
                    });
                }, 250);
            };
        }

        document.getElementById('sp-content-mode').onchange = renderContentPanel;
        renderContentPanel();

        document.getElementById('sp-cancel').onclick = closeModal;
        document.getElementById('sp-save').onclick = async () => {
            const name = document.getElementById('sp-name').value.trim();
            if (!name) {
                toast('Presentation name is required.', 'error');
                return;
            }

            const mode = parseInt(document.getElementById('sp-content-mode').value, 10);
            let candidates = [];
            if (mode === 1) {
                candidates = [{ kind: 2, filterJson: buildRuleFilterJson(), weight: 1, sortOrder: 0 }];
            } else if (mode === 2) {
                const listId = document.getElementById('sp-list-id').value;
                if (!listId) {
                    toast('Select a FinTV list.', 'error');
                    return;
                }
                candidates = [{ kind: 3, finTvListId: listId, weight: 1, sortOrder: 0 }];
            } else {
                candidates = draft.candidates;
            }

            if (!candidates.length) {
                toast('Add at least one content candidate.', 'error');
                return;
            }

            const payload = {
                name,
                enabled: document.getElementById('sp-enabled').checked,
                dayOfWeek: parseInt(document.getElementById('sp-day').value, 10),
                slotIndex: slotIndexFromTime(document.getElementById('sp-time').value),
                spanSlots: Math.max(1, Math.min(8, parseInt(document.getElementById('sp-span').value, 10) || 1)),
                candidates
            };

            try {
                if (existing) {
                    await api('/special-presentations/' + existing.id, { method: 'PUT', body: JSON.stringify(payload) });
                } else {
                    await api('/special-presentations/' + specialChannelId, { method: 'POST', body: JSON.stringify(payload) });
                }
                closeModal();
                toast('Special presentation saved.', 'success');
                await loadSpecialPresentations();
            } catch (err) {
                reportApiError(err, 'Could not save special presentation.');
            }
        };
    }

    async function deleteSpecialPresentation(id) {
        if (!confirm('Delete this special presentation?')) return;
        try {
            await api('/special-presentations/' + id, { method: 'DELETE' });
            toast('Special presentation deleted.', 'success');
            await loadSpecialPresentations();
        } catch (err) {
            reportApiError(err, 'Could not delete special presentation.');
        }
    }

    function syncConfigPageFromEvent(event) {
        const page = normalizeConfigPageRoot(event && event.target);
        if (page) {
            configPage = page;
            return page;
        }

        return syncConfigPage();
    }

    function switchTab(name) {
        if (!syncConfigPage()) {
            return;
        }

        qa('.fintv-tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.tab === name));
        qa('.tab-panel').forEach((p) => p.classList.toggle('active', p.id === 'tab-' + name));
        stopOnAirPolling();
        if (name === 'channels') startOnAirPolling();
        if (name === 'setup') loadSetup();
        if (name === 'ebs') loadEbs();
        if (name === 'ai') loadAi();
        if (name === 'weather') loadWeather();
        if (name === 'playwright') loadPlaywright();
        if (name === 'presets') loadPresets();
        if (name === 'lineups') loadLineups();
        if (name === 'list') loadLists();
        if (name === 'special') loadSpecialPresentations();
        if (name === 'commercials') loadCommercials();
        if (name === 'logos') loadLogos();
    }

    function copyText(elementId) {
        const text = $(elementId).textContent;
        navigator.clipboard.writeText(text).then(() => toast('Copied to clipboard.', 'success')).catch(() => {
            window.prompt('Copy URL:', text);
        });
    }

    function isActiveConfigPage(candidate) {
        if (!candidate || !document.contains(candidate)) {
            return false;
        }

        if (candidate.classList.contains('hide')
            || candidate.classList.contains('hidden')
            || candidate.getAttribute('aria-hidden') === 'true') {
            return false;
        }

        const rect = candidate.getBoundingClientRect();
        if (rect.width > 0 && rect.height > 0) {
            return true;
        }

        return candidate.classList.contains('active')
            || candidate.classList.contains('mainAnimatedPage');
    }

    function bindEvents() {
        if (!configPage) {
            return;
        }

        function click(id, handler) {
            const el = $(id);
            if (el) {
                el.onclick = handler;
            }
        }

        function change(id, handler) {
            const el = $(id);
            if (el) {
                el.onchange = handler;
            }
        }

        qa('.fintv-tabs .tab').forEach((tab) => tab.onclick = () => switchTab(tab.dataset.tab));
        click('btn-new-channel', () => openNewChannelForm());
        click('btn-close-channel', () => showChannelForm(false));
        click('btn-cancel-channel', () => showChannelForm(false));
        click('btn-delete-channel', () => deleteChannel(editingChannelId));
        const channelForm = $('channel-form');
        if (channelForm) {
            channelForm.onsubmit = saveChannel;
        }
        change('ch-content-type', toggleWeatherFields);
        change('ch-logo-set', () => populateLogoSelectors({ logoSetId: $('ch-logo-set').value, logoFileName: '' }));
        const channelFilterEl = $('channel-filter');
        if (channelFilterEl) {
            channelFilterEl.oninput = (e) => { channelFilter = e.target.value.trim(); renderChannelsList(); };
        }

        change('lineup-channel-select', loadLineups);
        click('btn-save-lineup', saveLineup);
        click('btn-rebuild-lineup', rebuildLineup);
        click('btn-preview-lineup', previewLineup);
        click('btn-add-override', openOverrideForm);
        click('btn-add-list', () => openListForm().catch((e) => toast(e.message, 'error')));
        click('btn-add-special', () => openSpecialPresentationForm().catch((e) => toast(e.message, 'error')));
        change('special-channel-select', loadSpecialPresentations);

        click('btn-sync-commercials', () => api('/commercials/sync', { method: 'POST' })
            .then(() => { toast('Commercial sync started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-scan-blackframes', () => api('/commercials/scan-blackframes', { method: 'POST' })
            .then(() => { toast('Blackframe scan started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-save-brainz', () => saveBrainzSettings().catch((e) => toast(e.message, 'error')));
        click('btn-preview-brainz', () => previewBrainz().catch((e) => toast(e.message, 'error')));
        click('btn-sync-brainz', () => syncBrainz().catch((e) => toast(e.message, 'error')));
        click('btn-sync-logos', () => api('/logos/sets/binarygeek119/sync', { method: 'POST' })
            .then(() => { toast('Logo set refreshed.', 'success'); return loadLogos(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-repair-logos', () => repairChannelLogos());
        click('btn-create-logo-set', openCreateLogoSetModal);
        click('btn-rebuild-all', () => api('/tasks/rebuild-all', { method: 'POST' })
            .then(() => {
                toast('Rebuild all started in background. This may take several minutes.', 'success');
                $('task-status').textContent = 'Rebuild all playouts running in background…';
            })
            .catch((e) => toast(e.message, 'error')));

        qa('.btn-copy').forEach((btn) => btn.onclick = () => copyText(btn.dataset.copyTarget));
        click('btn-save-setup', saveSetupSettings);
        click('btn-save-ebs', () => saveEbsSettings().catch((e) => toast(e.message, 'error')));
        click('btn-save-ai-settings', () => saveAiSettings().catch((e) => toast(e.message, 'error')));
        click('btn-test-ai', () => { void testAiConnection(); });
        click('btn-ai-generate-all', () => generateAllAiLineups().catch((e) => toast(e.message, 'error')));
        click('btn-ai-cancel-generate-all', () => cancelGenerateAll().catch((e) => toast(e.message, 'error')));
        click('btn-ai-apply', () => applyAiLineup().catch((e) => toast(e.message, 'error')));
        click('btn-ai-discard', discardAiPreview);
        change('ai-enabled', updateAiUiState);
        click('btn-upload-ebs-usa', () => uploadEbsSlate('usa', 'ebs-usa-file'));
        click('btn-upload-ebs-international', () => uploadEbsSlate('international', 'ebs-international-file'));
        click('btn-remove-ebs-usa', () => removeEbsSlate('usa'));
        click('btn-remove-ebs-international', () => removeEbsSlate('international'));
        click('btn-save-weather', saveWeatherSettings);
        click('btn-import-weather-permalink', importWeatherPermalink);
        click('btn-use-test-weather-url', useTestWeatherUrl);
        click('btn-ws4kp-start', () => startWeatherDocker('ws4kp', true).catch((e) => toast(e.message, 'error')));
        click('btn-ws4kp-stop', () => stopWeatherDocker('ws4kp').catch((e) => toast(e.message, 'error')));
        click('btn-ws4kp-use-url', () => startWeatherDocker('ws4kp', true).catch((e) => toast(e.message, 'error')));
        click('btn-ws3kp-start', () => startWeatherDocker('ws3kp', true).catch((e) => toast(e.message, 'error')));
        click('btn-ws3kp-stop', () => stopWeatherDocker('ws3kp').catch((e) => toast(e.message, 'error')));
        click('btn-ws3kp-use-url', () => startWeatherDocker('ws3kp', true).catch((e) => toast(e.message, 'error')));
        click('btn-playwright-start', () => startPlaywrightDocker().catch((e) => toast(e.message, 'error')));
        click('btn-playwright-stop', () => stopPlaywrightDocker().catch((e) => toast(e.message, 'error')));
        click('btn-save-playwright', savePlaywrightSettings);
        bindWeatherUrlPresets();
        change('ebs-display-mode', updateEbsFieldVisibility);
        change('ebs-audio-mode', updateEbsFieldVisibility);
        change('ebs-music-source', updateEbsFieldVisibility);
        click('btn-apply-presets', () => { void applyPresets(); });
        change('preset-numbering-mode', loadPresets);
        click('modal-close', closeModal);
        const modalBackdrop = $('modal-backdrop');
        if (modalBackdrop) {
            modalBackdrop.onclick = (e) => { if (e.target === modalBackdrop) closeModal(); };
        }

        const previewDate = $('lineup-preview-date');
        if (previewDate && !previewDate.value) {
            previewDate.value = todayIsoDate();
        }
    }

    async function refresh() {
        await loadChannels();
        await loadSetup();
    }

    function init(page) {
        if (!syncConfigPage(page)) {
            return Promise.resolve();
        }

        bindEvents();
        startOnAirPolling();
        return refresh().catch((err) => reportApiError(err, 'Could not load FinTV admin.'));
    }

    function resolveConfigPage(preferred) {
        preferred = normalizeConfigPageRoot(preferred);

        if (isActiveConfigPage(preferred)) {
            return preferred;
        }

        const pages = document.querySelectorAll('#FinTVConfigPage');
        for (let i = pages.length - 1; i >= 0; i--) {
            if (isActiveConfigPage(pages[i])) {
                return pages[i];
            }
        }

        if (preferred && document.contains(preferred)) {
            return preferred;
        }

        return pages.length ? pages[pages.length - 1] : null;
    }

    function bootFinTvAdmin(page) {
        page = resolveConfigPage(page);
        if (!page || !window.FinTV || !window.FinTV.init) {
            return false;
        }

        window.FinTV.init(page);
        return true;
    }

    window.FinTV = { init, refresh, loadChannels, loadSetup, bootFinTvAdmin };
})();

export default function (view) {
    function boot() {
        if (window.FinTV && window.FinTV.bootFinTvAdmin) {
            window.FinTV.bootFinTvAdmin(view);
        }
    }

    view.addEventListener('viewshow', boot);
    view.addEventListener('viewdestroy', function () {
        delete view.dataset.fintvBound;
        document.querySelectorAll('#FinTVConfigPage').forEach((page) => {
            delete page.dataset.fintvBound;
        });
    });
    boot();
}
