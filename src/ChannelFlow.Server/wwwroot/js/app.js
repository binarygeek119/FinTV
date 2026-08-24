(function () {
    'use strict';

    function appPathBase() {
        const value = typeof window.__CF_BASE__ === 'string' ? window.__CF_BASE__ : '';
        if (!value || value === '/') {
            return '';
        }
        return value.endsWith('/') ? value.slice(0, -1) : value;
    }

    function withAppBase(path) {
        const normalized = path.startsWith('/') ? path : '/' + path;
        return appPathBase() + normalized;
    }

    const CONTENT_TYPES = ['TV Show', 'Movie', 'Music Video', 'Music', 'Weather', 'News'];
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
        0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5,
        tvShow: 0, movie: 1, musicVideo: 2, music: 3, weather: 4, news: 5,
        TvShow: 0, Movie: 1, MusicVideo: 2, Music: 3, Weather: 4, News: 5
    };
    const ASPECT_RATIO_VALUES = {
        0: 0, 1: 1,
        sixteenNine: 0, fourThree: 1,
        SixteenNine: 0, FourThree: 1
    };
    const BUG_PLACEMENT_VALUES = {
        0: 0, 1: 1, 2: 2, 3: 3, 4: 4, 5: 5, 5: 5,
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
    let presetNumberingMode = 1;
    let configPage = null;
    let aiSettings = null;
    let aiChannels = [];
    let aiPlayoutTemplates = [];
    let aiPreview = null;
    let weatherDockerStatus = null;
    let newsFeeds = [];
    let finTvLists = [];
    let listNameCache = {};
    let specialPresentations = [];
    let specialChannelId = null;
    let commercialSearchPlaylists = [];
    let selectedSearchPlaylistId = null;
    let deepEditingChannelId = null;
    let deepChannelPlaylistIds = [];
    let guideData = null;
    let guideFromIso = null;
    let guideDateFilter = null;
    let guideTimer = null;
    const GUIDE_HOURS = 6;
    const GUIDE_PX_PER_MIN = 4;

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
        if (false) {
            return ApiClient.getUrl(normalized);
        }
        return withAppBase('/' + normalized.replace(/^(?:ChannelFlow|FinTV)\/api/, 'api'));
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
        channel.commercialSearchPlaylistIds = Array.isArray(channel.commercialSearchPlaylistIds)
            ? channel.commercialSearchPlaylistIds
            : [];
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

    function existingChannelDeepFields(channel) {
        const c = channel || (editingChannelId ? channels.find((x) => x.id === editingChannelId) : null);
        return {
            FilterJson: c?.filterJson ?? null,
            CatalogMode: c?.catalogMode ?? null,
            AiFineTunePrompt: c?.aiFineTunePrompt ?? null,
            CommercialPresetId: c?.commercialPresetId ?? null,
            CommercialSearchPlaylistIds: Array.isArray(c?.commercialSearchPlaylistIds)
                ? c.commercialSearchPlaylistIds.slice()
                : []
        };
    }

    function buildChannelPayload(form) {
        return Object.assign(existingChannelDeepFields(form.channel), {
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
        }, form.deep || {});
    }

    function isEmptyApiBody(data) {
        return data == null || data === '' || (typeof data === 'string' && !data.trim());
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
        if (isEmptyApiBody(data)) {
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
                if (statusCode === 204 || isEmptyApiBody(data)) {
                    finish(resolve, null);
                    return;
                }

                try {
                    finish(resolve, normalizeApiResponseData(data));
                } catch (parseErr) {
                    if (isEmptyApiBody(data)) {
                        finish(resolve, null);
                        return;
                    }

                    finish(reject, parseErr);
                }
            };

            const handleFailure = async (err) => {
                if (settled) {
                    return;
                }

                if (err instanceof Response) {
                    if (err.status === 204 || err.status === 205) {
                        finish(resolve, null);
                        return;
                    }

                    try {
                        await readApiFailure(err);
                    } catch (parsedErr) {
                        finish(reject, parsedErr instanceof Error ? parsedErr : new Error(String(parsedErr)));
                    }

                    return;
                }

                if (isEmptyApiResponseError(err)) {
                    finish(resolve, null);
                    return;
                }

                const message = String((err && err.message) || err || '');
                if (message.includes('Unexpected end of JSON input') || message === 'parsererror') {
                    finish(resolve, null);
                    return;
                }

                try {
                    await readApiFailure(err);
                } catch (parsedErr) {
                    finish(reject, parsedErr instanceof Error ? parsedErr : new Error(String(parsedErr)));
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
                }).catch((err) => {
                    void handleFailure(err);
                });
            }
        });
    }

    function api(path, options) {
        options = options || {};
        const url = resolveUrl('/api' + (path.startsWith('/') ? path : '/' + path));
        const method = options.method || 'GET';
        const body = options.body == null
            ? undefined
            : (typeof options.body === 'string' ? options.body : JSON.stringify(options.body));

        if (false && typeof ApiClient !== 'undefined' && typeof ApiClient.ajax === 'function') {
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
            cache: 'no-store',
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
            if (res.status === 401) { window.dispatchEvent(new Event('channelflow-auth-required')); throw new Error('Sign in required'); }
            if (!res.ok) {
                const text = await res.text();
                throw new Error(parseErrorMessage(text || res.statusText));
            }

            if (res.status === 204 || res.status === 205) {
                return null;
            }

            const text = await res.text();
            if (isEmptyApiBody(text)) {
                return null;
            }

            return normalizeApiResponse(parseApiJsonBody(text));
        }).catch((err) => {
            if (isNetworkError(err)) {
                throw new Error('Jellyfin server is unreachable');
            }

            throw err;
        });
    }

    async function apiForm(path, formData, method) {
        const url = resolveUrl('/api' + (path.startsWith('/') ? path : '/' + path));
        const httpMethod = method || 'POST';

        if (false && typeof ApiClient !== 'undefined' && typeof ApiClient.ajax === 'function') {
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

        const zip = location.match(/\b(\d{5})(?:-\d{4})?\b/);
        return zip ? zip[1] : location;
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
        decorateCheckboxes();
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
        [['ch-content-type', 'weather-fields'], ['deep-ch-content-type', 'deep-weather-fields']].forEach(([typeId, fieldsId]) => {
            const contentType = $(typeId);
            const weatherFields = $(fieldsId);
            if (!contentType || !weatherFields) {
                return;
            }

            weatherFields.classList.toggle('hidden', contentType.value !== '4');
        });
    }

    function populateLogoSelectors(channel, prefix) {
        prefix = prefix || 'ch';
        const setSelect = $(prefix + '-logo-set');
        const fileSelect = $(prefix + '-logo-file');
        if (!setSelect || !fileSelect) {
            return;
        }

        setSelect.innerHTML = '<option value="">None</option>' + logoSets.map((s) =>
            `<option value="${s.id}">${escapeHtml(s.name)} (${(s.entries || []).length})</option>`).join('');
        setSelect.value = channel?.logoSetId || '';

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
            const message = String((err && err.message) || '');
            if (isNetworkError(err) || message === 'Sign in required') {
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
                    <button type="button" data-quick-edit="${c.id}">Quick Edit</button>
                    <button type="button" data-edit="${c.id}">Edit</button>
                    <button type="button" data-lineup="${c.id}">Lineup</button>
                    <button type="button" class="btn-danger" data-delete="${c.id}">Delete</button>
                </td>
            </tr>`).join('')}</tbody></table>`;

        wrap.querySelectorAll('[data-quick-edit]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            editChannel(btn.dataset.quickEdit);
        });
        wrap.querySelectorAll('[data-edit]').forEach((btn) => btn.onclick = (e) => {
            e.stopPropagation();
            openChannelEditorWindow(btn.dataset.edit);
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
        $('channel-form-title').textContent = `Quick Edit ${c.name}`;
        $('btn-delete-channel').classList.remove('hidden');

        $('ch-number').value = c.number;
        $('ch-name').value = c.name;
        setSelectEnum('ch-content-type', CONTENT_TYPE_VALUES, c.contentType, 0);
        setSelectEnum('ch-aspect', ASPECT_RATIO_VALUES, c.aspectRatio, 0);
        $('ch-scanlines').checked = c.scanlinesEnabled;
        setSelectEnum('ch-bug', BUG_PLACEMENT_VALUES, c.bugPlacement, 0);
        $('ch-audio').value = c.audioLanguage || 'eng';
        if ($('ch-weather-location')) {
            $('ch-weather-location').value = parseWeatherLocationQuery(c.weatherLocationQuery) || '';
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
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
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

    function channelEditorPath(id) {
        return '/channels/' + id + '/edit';
    }

    function channelEditorIdFromPath(pathname) {
        const match = normalizePathname(pathname).match(/^\/channels\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\/edit$/i);
        return match ? match[1] : null;
    }

    function parseChannelFilter(filterJson) {
        if (!filterJson) {
            return {};
        }

        try {
            return typeof filterJson === 'string' ? (JSON.parse(filterJson) || {}) : (filterJson || {});
        } catch {
            return {};
        }
    }

    function libraryTagFromFilter(filterJson) {
        const filter = parseChannelFilter(filterJson);
        if (filter.presetId) {
            return String(filter.presetId);
        }

        const tags = Array.isArray(filter.tags) ? filter.tags : [];
        return tags.find((tag) => /^(?:channelflow|fintv)-/i.test(tag)) || '';
    }

    function mergeLibraryTagIntoFilter(existingJson, libraryTag) {
        const filter = parseChannelFilter(existingJson);
        const tag = String(libraryTag || '').trim();
        const otherTags = (Array.isArray(filter.tags) ? filter.tags : [])
            .filter((item) => item && !/^(?:channelflow|fintv)-/i.test(item) && item !== tag);
        if (tag) {
            filter.presetId = tag;
        } else {
            delete filter.presetId;
        }
        if (otherTags.length) {
            filter.tags = otherTags;
        } else {
            delete filter.tags;
        }

        return Object.keys(filter).length ? JSON.stringify(filter) : null;
    }

    function playlistSpotCount(playlist) {
        if (!playlist) {
            return 0;
        }

        if (playlist.itemCount != null) {
            return playlist.itemCount;
        }

        if (playlist.lastMatchedCount != null) {
            return playlist.lastMatchedCount;
        }

        return (playlist.videoSbids || []).length;
    }

    function setDeepEditorVisible(show) {
        const editor = $('channel-deep-editor');
        const app = $('channelflow-app');
        if (editor) {
            editor.classList.toggle('hidden', !show);
            editor.hidden = !show;
        }

        if (app) {
            app.classList.toggle('hidden', !!show);
        }

        document.body.classList.toggle('channel-editor-open', !!show);
        if (!show) {
            deepEditingChannelId = null;
            deepChannelPlaylistIds = [];
        }
    }

    function fillDeepChannelPlaylistPicker() {
        const picker = $('deep-ch-playlist-picker');
        const addBtn = $('btn-deep-ch-add-playlist');
        if (!picker) {
            return;
        }

        const assigned = new Set(deepChannelPlaylistIds.map(String));
        const available = commercialSearchPlaylists.filter((p) => !assigned.has(String(p.id)));
        picker.innerHTML = available.length
            ? available.map((p) => `<option value="${escapeHtml(p.id)}">${escapeHtml(p.name)}</option>`).join('')
            : '<option value="">No more playlists</option>';
        picker.disabled = available.length === 0;
        if (addBtn) {
            addBtn.disabled = available.length === 0;
        }
    }

    function renderDeepChannelPlaylists() {
        const wrap = $('deep-ch-playlists');
        if (!wrap) {
            return;
        }

        if (!deepChannelPlaylistIds.length) {
            wrap.innerHTML = commercialSearchPlaylists.length
                ? '<div class="playlist-empty">No playlists assigned. Commercial breaks use the default pool until you add one.</div>'
                : '<div class="playlist-empty">No search playlists yet. Create them on the Commercials tab, then add them here.</div>';
            fillDeepChannelPlaylistPicker();
            return;
        }

        wrap.innerHTML = deepChannelPlaylistIds.map((id) => {
            const playlist = commercialSearchPlaylists.find((p) => String(p.id) === String(id));
            const name = playlist ? playlist.name : 'Missing playlist';
            const query = playlist ? playlist.query : '';
            const count = playlistSpotCount(playlist);
            const missing = playlist ? '' : ' is-missing';
            return `<div class="playlist-chip${missing}" data-id="${escapeHtml(id)}">
                <div class="playlist-chip-main">
                    <strong>${escapeHtml(name)}</strong>
                    ${query ? `<span>${escapeHtml(query)}</span>` : ''}
                </div>
                <span class="playlist-chip-count">${count} spot${count === 1 ? '' : 's'}</span>
                <button type="button" class="btn-ghost" data-remove-playlist="${escapeHtml(id)}" title="Remove">&times;</button>
            </div>`;
        }).join('');

        wrap.querySelectorAll('[data-remove-playlist]').forEach((btn) => {
            btn.onclick = () => removeDeepChannelPlaylist(btn.dataset.removePlaylist);
        });
        fillDeepChannelPlaylistPicker();
    }

    function addDeepChannelPlaylist() {
        const picker = $('deep-ch-playlist-picker');
        const id = picker && picker.value;
        if (!id || deepChannelPlaylistIds.some((item) => String(item) === String(id))) {
            return;
        }

        deepChannelPlaylistIds.push(id);
        renderDeepChannelPlaylists();
    }

    function removeDeepChannelPlaylist(id) {
        deepChannelPlaylistIds = deepChannelPlaylistIds.filter((item) => String(item) !== String(id));
        renderDeepChannelPlaylists();
    }

    function fillDeepChannelForm(channel) {
        deepEditingChannelId = channel.id;
        deepChannelPlaylistIds = (channel.commercialSearchPlaylistIds || []).slice();
        $('deep-ch-title').textContent = channel.name;
        $('deep-ch-number').value = channel.number;
        $('deep-ch-name').value = channel.name;
        setSelectEnum('deep-ch-content-type', CONTENT_TYPE_VALUES, channel.contentType, 0);
        setSelectEnum('deep-ch-aspect', ASPECT_RATIO_VALUES, channel.aspectRatio, 0);
        $('deep-ch-scanlines').checked = !!channel.scanlinesEnabled;
        setSelectEnum('deep-ch-bug', BUG_PLACEMENT_VALUES, channel.bugPlacement, 0);
        $('deep-ch-audio').value = channel.audioLanguage || 'eng';
        if ($('deep-ch-weather-location')) {
            $('deep-ch-weather-location').value = parseWeatherLocationQuery(channel.weatherLocationQuery) || '';
        }
        $('deep-ch-enabled').checked = channel.enabled !== false;
        $('deep-ch-library-tag').value = libraryTagFromFilter(channel.filterJson);
        setSelectEnum('deep-ch-catalog-mode', { 0: 0, 1: 1, 2: 2, 3: 3 }, channel.catalogMode, 2);
        $('deep-ch-ai-prompt').value = channel.aiFineTunePrompt || '';
        populateLogoSelectors(channel, 'deep-ch');
        toggleWeatherFields();
        renderDeepChannelPlaylists();
    }

    async function openDeepChannelEditor(id, options) {
        options = options || {};
        const channel = channels.find((x) => String(x.id) === String(id));
        if (!channel) {
            toast('Channel not found.', 'error');
            return;
        }

        try {
            await loadLogoSetsForForm();
        } catch (err) {
            reportApiError(err, 'Could not load logo sets.');
            return;
        }

        try {
            commercialSearchPlaylists = await api('/commercials/search-playlists') || [];
        } catch (err) {
            commercialSearchPlaylists = commercialSearchPlaylists || [];
        }

        fillDeepChannelForm(channel);
        setDeepEditorVisible(true);
        document.title = 'Edit ' + channel.name + ' · ChannelFlow-Server';
        window.dispatchEvent(new CustomEvent('channelflow-tabchange', {
            detail: {
                tab: 'channels',
                title: 'Edit ' + channel.name,
                subtitle: formatChannelNumber(channel.number) + ' · Deep channel options'
            }
        }));

        const path = channelEditorPath(channel.id);
        if (!options.fromRoute && normalizePathname(location.pathname) !== path) {
            history.pushState({ tab: 'channel-editor', id: channel.id }, '', withAppBase(path));
        }
    }

    function closeDeepChannelEditor(options) {
        options = options || {};
        const wasOpen = document.body.classList.contains('channel-editor-open');
        setDeepEditorVisible(false);
        if (!wasOpen) {
            return;
        }

        if (window.opener && !window.opener.closed && !options.stay) {
            window.close();
            return;
        }

        if (!options.skipHistory && channelEditorIdFromPath(location.pathname)) {
            history.pushState({ tab: 'channels' }, '', withAppBase('/channels'));
        }
    }

    function openChannelEditorWindow(id) {
        const url = withAppBase(channelEditorPath(id));
        const win = window.open(url, 'channelflow-edit-' + id, 'width=1120,height=900');
        if (!win) {
            history.pushState({ tab: 'channel-editor', id }, '', url);
            openDeepChannelEditor(id, { fromRoute: true });
        }
    }

    async function saveDeepChannel() {
        const channel = channels.find((x) => String(x.id) === String(deepEditingChannelId));
        if (!channel) {
            toast('Channel not found.', 'error');
            return;
        }

        let number;
        try {
            number = parseChannelNumber($('deep-ch-number').value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        let weatherLocationQuery = null;
        try {
            weatherLocationQuery = parseWeatherLocationQuery($('deep-ch-weather-location')?.value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        const name = ($('deep-ch-name')?.value || '').trim();
        if (!name) {
            toast('Channel name is required.', 'error');
            return;
        }

        const payload = buildChannelPayload({
            channel,
            number,
            name,
            contentType: readSelectEnum('deep-ch-content-type', CONTENT_TYPE_VALUES, 0),
            aspectRatio: readSelectEnum('deep-ch-aspect', ASPECT_RATIO_VALUES, 0),
            scanlinesEnabled: !!$('deep-ch-scanlines')?.checked,
            bugPlacement: readSelectEnum('deep-ch-bug', BUG_PLACEMENT_VALUES, 0),
            audioLanguage: $('deep-ch-audio')?.value.trim() || 'eng',
            logoSetId: $('deep-ch-logo-set')?.value ? $('deep-ch-logo-set').value : null,
            logoFileName: $('deep-ch-logo-file')?.value || null,
            weatherLocationQuery,
            enabled: !!$('deep-ch-enabled')?.checked,
            deep: {
                FilterJson: mergeLibraryTagIntoFilter(channel.filterJson, $('deep-ch-library-tag')?.value),
                CatalogMode: readSelectEnum('deep-ch-catalog-mode', { 0: 0, 1: 1, 2: 2, 3: 3 }, 2),
                AiFineTunePrompt: ($('deep-ch-ai-prompt')?.value || '').trim() || null,
                CommercialSearchPlaylistIds: deepChannelPlaylistIds.slice()
            }
        });

        const saveBtn = $('btn-deep-ch-save');
        const originalLabel = saveBtn ? saveBtn.textContent : '';
        try {
            if (saveBtn) {
                saveBtn.disabled = true;
                saveBtn.textContent = 'Saving…';
            }

            await api('/channels/' + channel.id, { method: 'PUT', body: JSON.stringify(payload) });
            toast('Channel updated.', 'success');
            await loadChannels();
            const updated = channels.find((x) => String(x.id) === String(channel.id));
            if (updated) {
                fillDeepChannelForm(updated);
            }
        } catch (err) {
            reportApiError(err, 'Could not save channel.');
        } finally {
            if (saveBtn) {
                saveBtn.disabled = false;
                saveBtn.textContent = originalLabel || 'Save';
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
            const itemCount = Number(h.playoutItemCount || 0);
            const hasCoverageNow = !!h.hasCoverageNow;

            if (!hasCoverageNow) {
                banner.classList.remove('hidden');
                if (itemCount > 0 && h.earliestStartUtc) {
                    const nextStart = new Date(h.earliestStartUtc).toLocaleString();
                    banner.textContent = `Nothing on air right now. Next programme starts ${nextStart}. Live TV will show Off Air until then.`;
                } else {
                    banner.textContent = 'Live TV guide has no playout for this channel yet. Fill lineup slots (AI Generate or manual), then click Rebuild Playout.';
                }
                return;
            }

            if (!h.latestScheduledFinishUtc || daysBuilt < 0.5) {
                banner.classList.remove('hidden');
                banner.textContent = 'Guide playout is ending soon. Click Rebuild Playout to refresh the schedule.';
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
                : 'not set';

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
                    <option value="3">ChannelFlow list</option>
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
                    panel.innerHTML = `<label class="field"><span>ChannelFlow list</span>
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
                const rebuild = h.rebuild || {};

                if (rebuild.state === 'failed') {
                    toast(rebuild.error || 'Playout rebuild failed. Check the Jellyfin server log.', 'error');
                    return;
                }

                if (rebuild.state === 'completed') {
                    if (rebuild.hasCoverageNow) {
                        toast('Playout rebuild finished. Live TV guide is active for this channel.', 'success');
                    } else if (Number(rebuild.playoutItemCount || 0) > 0) {
                        toast('Playout rebuilt, but nothing is on air right now. Check the guide banner for the next start time.', 'success');
                    } else {
                        toast('Rebuild finished but the guide is empty. Fill lineup slots (Preview shows 0/48), then rebuild again.', 'error');
                    }
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

    const COMMERCIALBRAINZ_DEFAULT_URL = 'https://commercialbrainz.org';

    function normalizeBrainzBaseUrl(value) {
        const trimmed = (value || '').trim().replace(/\/+$/, '');
        if (!trimmed) {
            return COMMERCIALBRAINZ_DEFAULT_URL;
        }

        try {
            const host = new URL(trimmed).hostname;
            if (host.toLowerCase() === 'commercialbrainz.duckdns.org') {
                return COMMERCIALBRAINZ_DEFAULT_URL;
            }
        } catch {
            return COMMERCIALBRAINZ_DEFAULT_URL;
        }

        return trimmed;
    }

    function readBrainzSettingsFromForm() {
        return {
            enabled: !!$('cb-enabled')?.checked,
            baseUrl: normalizeBrainzBaseUrl($('cb-base-url')?.value),
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
            $('cb-base-url').value = normalizeBrainzBaseUrl(settings.baseUrl);
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

    function formatDuration(seconds) {
        const total = Math.max(0, Number(seconds) || 0);
        const mins = Math.floor(total / 60);
        const secs = Math.floor(total % 60);
        return `${mins}:${String(secs).padStart(2, '0')}`;
    }

    function renderBrainzPreview(preview) {
        const el = $('brainz-preview');
        if (!el) return;
        if (!preview) {
            el.innerHTML = '';
            return;
        }

        const samples = preview.samples || preview.Samples || [];
        const matched = preview.matchedCount ?? preview.MatchedCount ?? 0;
        const fetched = preview.fetchedCount ?? preview.FetchedCount ?? 0;
        const enabled = preview.enabled !== false && preview.Enabled !== false;
        const shown = samples.length;
        const extra = Math.max(0, matched - shown);
        let banner = extra > 0
            ? `Showing ${shown} of at least <strong>${matched}</strong> matching commercials (${fetched} scanned).`
            : `Sync will pull <strong>${matched}</strong> commercial${matched === 1 ? '' : 's'} from ${fetched} scanned videos.`;
        if (!enabled) {
            banner += ' CommercialBrainz is currently disabled — enable it to sync these spots.';
        }

        if (!shown) {
            el.innerHTML = `<div class="preview-banner">${banner}</div><div class="empty-state">No commercials match the current filters.</div>`;
            return;
        }

        el.innerHTML = `<div class="preview-banner">${banner}</div>
            <div class="brainz-preview-grid">${samples.map((item) => {
                const title = item.title || item.Title || 'Commercial';
                const brand = item.brand || item.Brand || '';
                const year = item.year ?? item.Year;
                const duration = formatDuration(item.durationSeconds ?? item.DurationSeconds);
                const youtubeUrl = item.youtubeUrl || item.youTubeUrl || item.YouTubeUrl || '';
                const youtubeId = item.youtubeVideoId || item.youTubeVideoId || item.YouTubeVideoId || '';
                const pageUrl = item.commercialPageUrl || item.CommercialPageUrl || '';
                const thumb = youtubeId
                    ? resolveUrl('ChannelFlow/api/commercials/brainz/thumbnail/' + encodeURIComponent(youtubeId))
                    : (item.thumbnailUrl || item.ThumbnailUrl || '');
                const meta = [brand, year, duration].filter((part) => part !== '' && part != null).join(' · ');
                const thumbHtml = thumb
                    ? `<img src="${escapeHtml(thumb)}" alt="" loading="lazy" onerror="this.style.display='none'">`
                    : '';
                const pageInner = `<div class="thumb">${thumbHtml}</div><div class="body"><div class="title">${escapeHtml(title)}</div><div class="meta">${escapeHtml(meta)}</div></div>`;
                const pageLink = pageUrl
                    ? `<a class="brainz-preview-link" href="${escapeHtml(pageUrl)}" target="_blank" rel="noopener">${pageInner}</a>`
                    : `<div class="brainz-preview-link">${pageInner}</div>`;
                const youtubeBtn = youtubeUrl
                    ? `<a class="brainz-yt-btn" href="${escapeHtml(youtubeUrl)}" target="_blank" rel="noopener">YouTube</a>`
                    : '';
                return `<div class="brainz-preview-card">${pageLink}${youtubeBtn}</div>`;
            }).join('')}</div>`;
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

    async function saveBrainzSettings(options = {}) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        try {
            const payload = readBrainzSettingsFromForm();
            const saved = await api('/commercials/brainz/settings', {
                method: 'PUT',
                body: JSON.stringify(payload)
            });
            applyBrainzSettings(saved);
            if (!options.silent) {
                toast('CommercialBrainz filters saved.', 'success');
            }
        } catch (err) {
            reportApiError(err, 'Could not save CommercialBrainz settings.');
        }
    }

    async function previewBrainz(options = {}) {
        const el = $('brainz-preview');
        if (!options.skipSave) {
            await saveBrainzSettings({ silent: true });
        }
        if (el) {
            el.innerHTML = '<div class="empty-state">Loading commercial preview…</div>';
        }
        const preview = await api('/commercials/brainz/preview', { method: 'POST' });
        renderBrainzPreview(preview);
        if (!options.silent) {
            toast(`Preview: ${preview.matchedCount ?? preview.MatchedCount ?? 0} commercials will be synced`, 'success');
        }
    }

    async function syncBrainz() {
        await saveBrainzSettings();
        await api('/commercials/brainz/sync', { method: 'POST' });
        toast('CommercialBrainz sync started.', 'success');
        const previewEl = $('brainz-preview');
        if (previewEl) {
            previewEl.dataset.loaded = '';
        }
        await loadCommercialBrainz();
    }

    function isBrainzCommercial(item) {
        return item.source === 1 || item.source === 'CommercialBrainz';
    }

    function commercialDurationSeconds(item) {
        const duration = item.duration;
        if (typeof duration === 'number') {
            return duration;
        }
        return Math.round((duration && duration.totalSeconds) || 0);
    }

    function renderCommercialTable(targetId, list, emptyMessage) {
        const el = $(targetId);
        if (!el) {
            return;
        }
        if (!list || !list.length) {
            el.innerHTML = `<div class="empty-state">${emptyMessage}</div>`;
            return;
        }

        el.innerHTML = `<table class="data-table">
            <thead><tr><th>Title</th><th>Brand</th><th>Duration</th><th>Year</th><th>Chapters</th></tr></thead>
            <tbody>${list.map((c) => `<tr>
                <td>${escapeHtml(c.title)}</td>
                <td>${escapeHtml(c.brand || '')}</td>
                <td>${commercialDurationSeconds(c)}s</td>
                <td>${escapeHtml(String(c.year ?? ''))}</td>
                <td>${(c.chapters || []).length}</td>
            </tr>`).join('')}</tbody></table>`;
    }

    async function loadCommercials() {
        try {
            const list = await api('/commercials');
            const status = await api('/commercials/scan-status');
            if ($('commercial-status')) {
                $('commercial-status').textContent = status ? JSON.stringify(status, null, 2) : 'No scan running.';
            }
            const jellyfin = (list || []).filter((c) => !isBrainzCommercial(c));
            renderCommercialTable('commercial-list', jellyfin, 'No Jellyfin commercials synced yet. Tag items with channelflow-commercial and click Sync Jellyfin Library.');
            await loadSearchPlaylists();
        } catch (err) {
            reportApiError(err, 'Could not load commercials.');
        }
    }

    async function loadSearchPlaylists() {
        const listEl = $('cb-playlist-list');
        if (!listEl) {
            return;
        }

        try {
            commercialSearchPlaylists = await api('/commercials/search-playlists') || [];
            if (!commercialSearchPlaylists.length) {
                selectedSearchPlaylistId = null;
                listEl.innerHTML = '<div class="empty-state">No search playlists yet. Add a name and query, then Add &amp; Pull.</div>';
                if ($('cb-playlist-items')) $('cb-playlist-items').innerHTML = '';
                return;
            }

            if (!selectedSearchPlaylistId || !commercialSearchPlaylists.some((p) => p.id === selectedSearchPlaylistId)) {
                selectedSearchPlaylistId = commercialSearchPlaylists[0].id;
            }

            listEl.innerHTML = `<table class="data-table">
                <thead><tr><th>Name</th><th>Search</th><th>Spots</th><th>Last pull</th><th></th></tr></thead>
                <tbody>${commercialSearchPlaylists.map((p) => {
                    const selected = p.id === selectedSearchPlaylistId ? ' class="selected"' : '';
                    const synced = p.lastSyncedAt ? new Date(p.lastSyncedAt).toLocaleString() : 'never';
                    const err = p.lastError ? ` title="${escapeHtml(p.lastError)}"` : '';
                    return `<tr data-playlist-id="${escapeHtml(p.id)}"${selected}${err}>
                        <td>${escapeHtml(p.name)}</td>
                        <td>${escapeHtml(p.query)}</td>
                        <td>${p.itemCount ?? p.lastMatchedCount ?? 0}</td>
                        <td>${escapeHtml(p.lastError ? 'error' : synced)}</td>
                        <td>
                            <button type="button" class="emby-button btn-pull-cb-playlist" data-id="${escapeHtml(p.id)}">Pull</button>
                            <button type="button" class="emby-button btn-delete-cb-playlist" data-id="${escapeHtml(p.id)}">Delete</button>
                        </td>
                    </tr>`;
                }).join('')}</tbody></table>`;

            listEl.querySelectorAll('tbody tr').forEach((row) => {
                row.onclick = (event) => {
                    if (event.target.closest('button')) {
                        return;
                    }
                    selectedSearchPlaylistId = row.dataset.playlistId;
                    listEl.querySelectorAll('tbody tr').forEach((r) => r.classList.toggle('selected', r.dataset.playlistId === selectedSearchPlaylistId));
                    renderSearchPlaylistItems();
                };
            });
            listEl.querySelectorAll('.btn-pull-cb-playlist').forEach((btn) => {
                btn.onclick = (event) => {
                    event.stopPropagation();
                    pullSearchPlaylist(btn.dataset.id);
                };
            });
            listEl.querySelectorAll('.btn-delete-cb-playlist').forEach((btn) => {
                btn.onclick = (event) => {
                    event.stopPropagation();
                    deleteSearchPlaylist(btn.dataset.id);
                };
            });
            renderSearchPlaylistItems();
        } catch (err) {
            reportApiError(err, 'Could not load search playlists.');
        }
    }

    function renderSearchPlaylistItems() {
        const el = $('cb-playlist-items');
        if (!el) {
            return;
        }
        const playlist = commercialSearchPlaylists.find((p) => p.id === selectedSearchPlaylistId);
        if (!playlist) {
            el.innerHTML = '';
            return;
        }
        const items = playlist.items || [];
        if (!items.length) {
            el.innerHTML = `<div class="empty-state">No spots in “${escapeHtml(playlist.name)}” yet. Click Pull.</div>`;
            return;
        }
        el.innerHTML = `<table class="data-table">
            <thead><tr><th>${escapeHtml(playlist.name)}</th><th>Brand</th><th>Year</th><th>Duration</th></tr></thead>
            <tbody>${items.map((c) => {
                const title = c.youtubeUrl
                    ? `<a href="${escapeHtml(c.youtubeUrl)}" target="_blank" rel="noopener">${escapeHtml(c.title)}</a>`
                    : escapeHtml(c.title);
                return `<tr>
                    <td>${title}</td>
                    <td>${escapeHtml(c.brand || '')}</td>
                    <td>${escapeHtml(String(c.year ?? ''))}</td>
                    <td>${c.durationSeconds ?? 0}s</td>
                </tr>`;
            }).join('')}</tbody></table>`;
    }

    async function addSearchPlaylist() {
        const name = ($('cb-playlist-name')?.value || '').trim();
        const query = ($('cb-playlist-query')?.value || '').trim();
        const maxResults = Number($('cb-playlist-max')?.value || '50');
        if (!name || !query) {
            toast('Name and search query are required.', 'error');
            return;
        }

        const btn = $('btn-add-cb-playlist');
        if (btn) btn.disabled = true;
        try {
            const playlist = await api('/commercials/search-playlists', {
                method: 'POST',
                body: { name, query, maxResults }
            });
            if ($('cb-playlist-name')) $('cb-playlist-name').value = '';
            if ($('cb-playlist-query')) $('cb-playlist-query').value = '';
            selectedSearchPlaylistId = playlist.id;
            toast(`Pulled ${playlist.itemCount ?? 0} spots into “${playlist.name}”.`, 'success');
            await loadCommercials();
        } catch (err) {
            toast(err.message, 'error');
        } finally {
            if (btn) btn.disabled = false;
        }
    }

    async function pullSearchPlaylist(id) {
        try {
            toast('Pulling CommercialBrainz search…');
            const playlist = await api('/commercials/search-playlists/' + id + '/pull', { method: 'POST' });
            selectedSearchPlaylistId = playlist.id;
            toast(`Pulled ${playlist.itemCount ?? 0} spots into “${playlist.name}”.`, 'success');
            await loadCommercials();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function deleteSearchPlaylist(id) {
        if (!confirm('Delete this search playlist? Synced commercials stay in the library.')) {
            return;
        }
        try {
            await api('/commercials/search-playlists/' + id, { method: 'DELETE' });
            if (selectedSearchPlaylistId === id) {
                selectedSearchPlaylistId = null;
            }
            toast('Search playlist deleted.', 'success');
            await loadSearchPlaylists();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function loadCommercialBrainz() {
        await loadBrainzSettings();
        const previewEl = $('brainz-preview');
        if (previewEl && previewEl.dataset.loaded !== '1') {
            previewEl.dataset.loaded = '1';
            try {
                await previewBrainz({ skipSave: true, silent: true });
            } catch (previewErr) {
                previewEl.dataset.loaded = '';
                previewEl.innerHTML = `<div class="empty-state">${escapeHtml(previewErr.message || 'Could not load commercial preview.')}</div>`;
            }
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
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
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
        refreshEbsPreviews();
    }

    function setEbsPreviewImage(img, url) {
        if (!img) return;
        const wrap = img.closest('.ebs-upload-preview-wrap');
        img.onload = () => {
            img.classList.remove('hidden');
            wrap?.classList.remove('hidden');
        };
        img.onerror = () => {
            img.classList.add('hidden');
            img.removeAttribute('src');
            wrap?.classList.add('hidden');
        };
        img.classList.add('hidden');
        img.src = url;
    }

    function refreshEbsPreviews() {
        const displayMode = Number($('ebs-display-mode')?.value || '0');
        const variant = Number($('ebs-slate-variant')?.value || '0');
        const bust = Date.now();
        const liveImg = $('ebs-live-preview-image');
        const liveCaption = $('ebs-live-preview-caption');
        const bars = $('ebs-live-preview-bars');
        const snow = $('ebs-live-preview-static');
        const missing = $('ebs-live-preview-missing');

        bars?.classList.toggle('hidden', displayMode !== 1);
        snow?.classList.toggle('hidden', displayMode !== 2);
        if (bars) bars.hidden = displayMode !== 1;
        if (snow) snow.hidden = displayMode !== 2;

        if (displayMode === 0) {
            if (liveCaption) {
                liveCaption.textContent = 'Shown during dead air and playback errors.';
            }
            if (missing) {
                missing.classList.add('hidden');
                missing.hidden = true;
            }
            if (liveImg) {
                liveImg.onload = () => {
                    liveImg.classList.remove('hidden');
                    if (missing) {
                        missing.classList.add('hidden');
                        missing.hidden = true;
                    }
                };
                liveImg.onerror = () => {
                    liveImg.classList.add('hidden');
                    liveImg.removeAttribute('src');
                    if (missing) {
                        missing.classList.remove('hidden');
                        missing.hidden = false;
                    }
                };
                liveImg.src = withAppBase('/api/ebs/preview?variant=' + variant + '&t=' + bust);
            }
        } else {
            if (liveImg) {
                liveImg.classList.add('hidden');
                liveImg.removeAttribute('src');
            }
            if (missing) {
                missing.classList.add('hidden');
                missing.hidden = true;
            }
            if (liveCaption) {
                liveCaption.textContent = displayMode === 1
                    ? 'Viewers see generated color bars during dead air and playback errors.'
                    : 'Viewers see generated TV static during dead air and playback errors.';
            }
        }

        setEbsPreviewImage($('ebs-usa-preview'), withAppBase('/api/ebs/slates/usa/image?t=' + bust));
        setEbsPreviewImage($('ebs-international-preview'), withAppBase('/api/ebs/slates/international/image?t=' + bust));
    }

    function populateEbsMusicLibraries(libraries, selectedId, selectedName, selectId) {
        const select = $(selectId || 'ebs-music-library') || $('setup-ebs-music-library');
        if (!select) return;

        const items = libraries || [];
        if (items.length === 0) {
            select.innerHTML = '<option value="">No music libraries found in Jellyfin</option>';
            return;
        }

        const options = items.map((lib) => {
            const id = lib.id ?? lib.Id ?? '';
            const name = lib.name ?? lib.Name ?? 'Music library';
            return `<option value="${escapeHtml(String(id))}">${escapeHtml(String(name))}</option>`;
        });
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
            ttsVoice: $('ai-tts-voice')?.value?.trim() || 'nova',
            openAiApiKey: normalizeApiKeyInput($('ai-openai-key')?.value) || null,
            veniceApiKey: normalizeApiKeyInput($('ai-venice-key')?.value) || null
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
            if ($('ai-tts-voice')) $('ai-tts-voice').value = aiSettings.ttsVoice || 'nova';
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
            await loadWeatherGuideCacheStatus();
        } catch (err) {
            reportApiError(err, 'Could not load AI settings.');
        }
    }

    let weatherGuideCachePollTimer = null;

    function renderWeatherGuideCacheStatus(status) {
        const el = $('ai-weather-guide-cache-status');
        const genBtn = $('btn-weather-guide-cache-generate');
        const clearBtn = $('btn-weather-guide-cache-clear');
        if (!el || !status) {
            return;
        }

        if (status.isGenerating) {
            el.textContent =
                `Generating weather guide cache… ${status.completeChannels}/${status.channelCount} channel(s) complete · ${status.entryCount} hour slot(s) cached.`;
            if (genBtn) {
                genBtn.disabled = true;
                genBtn.textContent = 'Generating…';
            }
            if (clearBtn) {
                clearBtn.disabled = true;
            }
            return;
        }

        if (genBtn) {
            genBtn.disabled = false;
            genBtn.textContent = 'Generate Weather Guide Cache';
        }
        if (clearBtn) {
            clearBtn.disabled = false;
        }

        if (!status.channelCount) {
            el.textContent = 'No enabled weather channels. Add a weather channel to build guide metadata.';
            return;
        }

        const dateLabel = status.forecastDate ? ` for ${status.forecastDate}` : '';
        const source = status.weatherSource ? ` Source: ${status.weatherSource}.` : '';
        const generated = status.lastGeneratedAt
            ? ` Last built ${new Date(status.lastGeneratedAt).toLocaleString()}.`
            : '';
        let line =
            `${status.completeChannels}/${status.channelCount} weather channel(s) have today's forecast${dateLabel} · ${status.entryCount} cached hour(s).${source}${generated}`;
        const partial = (status.channels || []).filter((c) => c.hoursCached > 0 && !c.isComplete);
        if (partial.length) {
            const names = partial.map((c) => `${c.channelName} (${c.hoursCached}/24)`).join(', ');
            line += ` Partial: ${names}.`;
        } else if (status.completeChannels < status.channelCount) {
            line += " Click Generate Weather Guide Cache to fill today's hours from the Weather tab source.";
        } else {
            line += ' Auto-refreshes at local midnight.';
        }

        el.textContent = line;
    }

    function stopWeatherGuideCachePolling() {
        if (weatherGuideCachePollTimer) {
            clearTimeout(weatherGuideCachePollTimer);
            weatherGuideCachePollTimer = null;
        }
    }

    function startWeatherGuideCachePolling() {
        stopWeatherGuideCachePolling();
        weatherGuideCachePollTimer = setTimeout(pollWeatherGuideCacheStatus, 3000);
    }

    async function loadWeatherGuideCacheStatus() {
        try {
            const status = await api('/ai/weather-guide-cache/status');
            renderWeatherGuideCacheStatus(status);
            if (status.isGenerating) {
                startWeatherGuideCachePolling();
            } else {
                stopWeatherGuideCachePolling();
            }
        } catch (err) {
            const el = $('ai-weather-guide-cache-status');
            if (el) {
                el.textContent = 'Could not load weather guide cache status.';
            }
        }
    }

    async function pollWeatherGuideCacheStatus() {
        try {
            const status = await api('/ai/weather-guide-cache/status');
            renderWeatherGuideCacheStatus(status);
            if (status.isGenerating) {
                startWeatherGuideCachePolling();
            } else {
                stopWeatherGuideCachePolling();
            }
        } catch (err) {
            stopWeatherGuideCachePolling();
        }
    }

    async function generateWeatherGuideCache(force = true) {
        const result = await api('/ai/weather-guide-cache/generate', {
            method: 'POST',
            body: JSON.stringify({ force })
        });

        if (result.alreadyRunning) {
            toast('Weather guide cache generation is already running.', 'info');
        } else if (result.queued) {
            toast('Weather guide cache generation started.', 'success');
        }

        renderWeatherGuideCacheStatus(result.status);
        if (result.status?.isGenerating) {
            startWeatherGuideCachePolling();
        }
    }

    async function clearWeatherGuideCache() {
        if (!confirm('Delete all cached weather guide metadata? EPG will use fallback titles until you generate a new cache.')) {
            return;
        }

        const result = await api('/ai/weather-guide-cache', { method: 'DELETE' });
        toast(`Cleared ${result.cleared} weather guide cache entries.`, 'success');
        await loadWeatherGuideCacheStatus();
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
                if (isGuideTabVisible()) {
                    loadGuide({ quiet: true }).catch(() => {});
                }
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
                await refreshGuide();
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
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
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

    function normalizeApiKeyInput(value) {
        if (!value) {
            return '';
        }

        let key = String(value).trim().replace(/^['"]|['"]$/g, '');
        if (/^bearer\s+/i.test(key)) {
            key = key.replace(/^bearer\s+/i, '').trim();
        }

        return key;
    }

    async function testAiConnection() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
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
                providerId: form.defaultProvider
            };
            if (form.openAiApiKey) payload.openAiApiKey = form.openAiApiKey;
            if (form.veniceApiKey) payload.veniceApiKey = form.veniceApiKey;
            const data = await api('/ai/settings/test', {
                method: 'POST',
                body: JSON.stringify(payload)
            });
            toast(`Connected to ${data.provider}.`, 'success');
        } catch (err) {
            toast((err && err.message) || 'AI connection test failed.', 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Test Connection';
            }
        }
    }

    async function saveAiChannelSettings(channelId) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
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

    async function waitForChannelGenerate(channelId, maxWaitMs) {
        const started = Date.now();
        const timeoutMs = maxWaitMs || 900000;
        while (Date.now() - started < timeoutMs) {
            const status = await api('/ai/channels/' + channelId + '/generate/status');
            if (!status.isRunning) {
                if (status.error) {
                    throw new Error(status.error);
                }

                if (!status.preview) {
                    throw new Error('AI lineup generation finished without a preview.');
                }

                return status;
            }

            await new Promise((resolve) => setTimeout(resolve, 3000));
        }

        throw new Error('AI lineup generation is taking longer than expected. Check the Jellyfin log and try again.');
    }

    async function generateAiLineup(channelId) {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        const row = q(`[data-ai-channel="${channelId}"]`);
        if (row) {
            await saveAiChannelSettings(channelId);
        }
        const btn = row?.querySelector('.ai-generate-channel');
        if (btn) btn.disabled = true;
        try {
            const start = await api('/ai/channels/' + channelId + '/generate', { method: 'POST', body: '{}' });
            if (start.alreadyRunning) {
                toast('AI lineup generation is already running for this channel…', 'info');
            } else {
                toast('AI lineup generation started…', 'info');
            }

            const status = await waitForChannelGenerate(channelId);
            aiPreview = status.preview;
            renderAiPreview();
            if (status.applyError) {
                toast('AI lineup generated, but the Live TV guide was not rebuilt: ' + status.applyError, 'error');
            } else {
                toast('AI lineup generated and Live TV guide rebuilt.', 'success');
                await refreshGuide();
            }
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
        $('ai-preview-summary').textContent = `AI chose from ${aiPreview.catalogSummary.includedInPrompt} of ${aiPreview.catalogSummary.totalAvailable} tagged items · ${AI_CATALOG_MODES[aiPreview.catalogMode] || 'Mixed'} mode${templateLabel} · lineup is applied and the Live TV guide is rebuilt (up to 14 days)`;
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
            await refreshGuide();
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
            await refreshGuide();
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
            reportApiError(err, 'Could not load Off Air settings.');
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
            toast('Off Air settings saved.', 'success');
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
            refreshEbsPreviews();
            if (input) input.value = '';
            toast('Custom Off Air slate uploaded.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function removeEbsSlate(variant) {
        try {
            const data = await api('/ebs/slates/' + variant, { method: 'DELETE' });
            renderEbsCustomSlateStatus(data.customSlates);
            refreshEbsPreviews();
            toast('Custom Off Air slate removed.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function renderWeatherStatus(status) {
        const el = $('weather-renderer-status');
        if (!el) return;
        const variant = status?.weatherStarVariant === 'ws3kp' ? 'ws3kp' : 'ws4kp';
        const label = variant === 'ws3kp' ? 'WeatherStar 3000' : 'WeatherStar 4000';
        const source = status?.weatherSource === 'us' ? 'United States (NOAA)'
            : status?.weatherSource === 'world' ? 'World (Open-Meteo)'
            : 'Auto (NOAA in the US, Open-Meteo worldwide)';
        el.innerHTML = `<div>${escapeHtml(label)} native live stream</div><div class="meta">${escapeHtml(source)}</div>`;
        const variantSelect = $('weather-star-variant');
        if (variantSelect) {
            variantSelect.value = variant;
        }
        if ($('weather-source')) {
            $('weather-source').value = status?.weatherSource || 'auto';
        }
        const select = $('weather-music-library');
        if (select && Array.isArray(status?.musicLibraries)) {
            const current = status.weatherMusicLibraryId || '';
            select.innerHTML = '<option value="">Use Off Air / all music</option>' +
                status.musicLibraries.map((lib) => `<option value="${escapeHtml(lib.id)}" ${lib.id === current ? 'selected' : ''}>${escapeHtml(lib.name)}</option>`).join('');
            if (current) select.value = current;
        }
        if ($('weather-default-zip')) {
            $('weather-default-zip').value = status?.weatherDefaultLocationQuery || '';
        }
        renderWeatherZipList(status?.weatherChannels || []);
        applyWeatherQueryToForm(status?.weatherStarPermalinkQuery || '');
        if ($('weather-auto-wide-169')) {
            $('weather-auto-wide-169').checked = status?.weatherStarAutoWideForSixteenNine !== false;
        }
        if ($('weather-alert-overlay-mode')) {
            const mode = status?.weatherAlertOverlayMode || 'off';
            $('weather-alert-overlay-mode').value = ['off', 'cutin', 'ticker'].includes(mode) ? mode : 'off';
        }
        if ($('weather-alert-cutin-interval')) {
            $('weather-alert-cutin-interval').value = String(status?.weatherAlertCutInIntervalMinutes || 15);
        }
        if ($('weather-alert-cutin-duration')) {
            $('weather-alert-cutin-duration').value = String(status?.weatherAlertCutInDurationSeconds || 20);
        }
        toggleWeatherAlertCutInFields();
    }

    const WEATHER_SCREEN_KEYS = [
        'hazards',
        'current-weather',
        'latest-observations',
        'hourly',
        'hourly-graph',
        'travel',
        'regional-forecast',
        'local-forecast',
        'extended-forecast',
        'almanac',
        'spc-outlook',
        'radar'
    ];

    function extractWeatherZip(value) {
        const match = String(value || '').match(/\b(\d{5})(?:-\d{4})?\b/);
        return match ? match[1] : '';
    }

    function readWeatherLocation(value, label) {
        const trimmed = String(value || '').trim();
        if (trimmed.length < 2) {
            throw new Error((label || 'Location') + ' is required (US ZIP, city, or lat,lon).');
        }
        return trimmed;
    }

    function weatherQueryFlag(params, key, fallback) {
        const raw = params.get(key);
        if (raw == null || raw === '') {
            return fallback;
        }
        return raw !== 'false' && raw !== '0';
    }

    function applyWeatherQueryToForm(query) {
        const params = new URLSearchParams(String(query || '').replace(/^\?/, ''));
        WEATHER_SCREEN_KEYS.forEach((key) => {
            const el = $('wx-' + key);
            if (el) {
                el.checked = weatherQueryFlag(params, key, true);
            }
        });
        if ($('wx-stickyKiosk')) {
            $('wx-stickyKiosk').checked = weatherQueryFlag(params, 'stickyKiosk', true);
        }
        if ($('wx-customTextEnable')) {
            $('wx-customTextEnable').checked = weatherQueryFlag(params, 'customTextEnable', false);
        }
        if ($('wx-scanLines')) {
            $('wx-scanLines').checked = weatherQueryFlag(params, 'scanLines', false);
        }
        if ($('wx-customText')) {
            $('wx-customText').value = params.get('customText') || '';
        }
        if ($('wx-units')) {
            $('wx-units').value = params.get('units') || 'us';
        }
        if ($('wx-viewMode')) {
            $('wx-viewMode').value = params.get('viewMode') || 'standard';
        }
        if ($('wx-speed')) {
            const speed = Number(params.get('speed') || '1').toFixed(2);
            $('wx-speed').value = ['0.50', '0.75', '1.00', '1.25', '1.50'].includes(speed) ? speed : '1.00';
        }
        if ($('wx-mediaVolume')) {
            $('wx-mediaVolume').value = params.get('mediaVolume') || '0.75';
        }
    }

    function serializeWeatherQueryFromForm() {
        const params = new URLSearchParams();
        WEATHER_SCREEN_KEYS.forEach((key) => {
            params.set(key, $('wx-' + key)?.checked ? 'true' : 'false');
        });
        params.set('stickyKiosk', $('wx-stickyKiosk')?.checked ? 'true' : 'false');
        params.set('customTextEnable', $('wx-customTextEnable')?.checked ? 'true' : 'false');
        params.set('speed', $('wx-speed')?.value || '1.00');
        params.set('viewMode', $('wx-viewMode')?.value || 'standard');
        params.set('units', $('wx-units')?.value || 'us');
        params.set('customText', $('wx-customText')?.value.trim() || '');
        params.set('mediaVolume', $('wx-mediaVolume')?.value || '0.75');
        params.set('scanLines', $('wx-scanLines')?.checked ? 'true' : 'false');
        return params.toString();
    }

    function toggleWeatherAlertCutInFields() {
        const mode = $('weather-alert-overlay-mode')?.value || 'off';
        const fields = $('weather-alert-cutin-fields');
        if (fields) {
            fields.classList.toggle('hidden', mode === 'off');
        }
        const interval = $('weather-alert-interval-wrap');
        if (interval) {
            interval.classList.toggle('hidden', mode !== 'cutin');
        }
    }

    function hideWeatherAlertTestPreview() {
        const preview = $('weather-alert-test-preview');
        const hint = $('weather-alert-test-hint');
        if (preview) {
            preview.innerHTML = '';
            preview.hidden = true;
            preview.classList.add('hidden');
        }
        if (hint) {
            hint.textContent = '';
            hint.hidden = true;
            hint.classList.add('hidden');
        }
    }

    function showWeatherAlertTestPreview(data) {
        const preview = $('weather-alert-test-preview');
        const hint = $('weather-alert-test-hint');
        if (!preview) {
            return;
        }
        const parts = [];
        const jpeg = data?.hazardsJpeg;
        if (data?.mode === 'cutin' && jpeg) {
            parts.push('<img alt="Sample weather alerts screen" src="data:image/jpeg;base64,' + jpeg + '">');
        } else if (data?.mode === 'cutin') {
            parts.push('<p class="hint" style="padding:0.75rem;margin:0">' + escapeHtml(data.headline || data.eventName || 'Sample weather alert') + '</p>');
        }
        if (data?.mode === 'ticker') {
            const text = escapeHtml(data.tickerText || data.headline || 'WEATHER ALERT');
            parts.push('<div class="weather-alert-ticker-preview" role="img" aria-label="Sample scrolling weather alert"><span>' + text + '</span></div>');
        }
        preview.innerHTML = parts.join('');
        preview.hidden = parts.length === 0;
        preview.classList.toggle('hidden', parts.length === 0);
        if (hint) {
            hint.textContent = data?.message || '';
            hint.hidden = !data?.message;
            hint.classList.toggle('hidden', !data?.message);
        }
    }

    async function testWeatherAlerts() {
        const btn = $('btn-test-weather-alerts');
        const originalLabel = btn ? btn.textContent : '';
        try {
            if (btn) {
                btn.disabled = true;
                btn.textContent = 'Testing…';
            }
            const mode = $('weather-alert-overlay-mode')?.value || 'off';
            const data = await api('/weather/alerts/test', {
                method: 'POST',
                body: JSON.stringify({
                    mode,
                    durationSeconds: Number($('weather-alert-cutin-duration')?.value || 20)
                })
            });
            showWeatherAlertTestPreview(data);
            toast(data.eventName ? `Sample ${data.eventName} is ready.` : 'Sample weather alert is ready.', 'success');
        } catch (err) {
            hideWeatherAlertTestPreview();
            toast(err.message || 'Could not test weather alerts.', 'error');
        } finally {
            if (btn) {
                btn.disabled = false;
                btn.textContent = originalLabel || 'Test alerts';
            }
        }
    }

    function renderWeatherZipList(rows) {
        const el = $('weather-zip-list');
        if (!el) {
            return;
        }
        if (!rows.length) {
            el.innerHTML = '<p class="hint">No weather channels yet. Apply a WeatherStar preset or create a Weather channel, then set its location here.</p>';
            return;
        }
        el.innerHTML = rows.map((ch) => {
            const location = ch.location || ch.weatherLocationQuery || ch.zip || '';
            return '<div class="weather-zip-row">'
                + '<div class="wx-channel-label">' + escapeHtml((ch.number || '') + ' · ' + (ch.name || 'Weather')) + '</div>'
                + '<label class="field"><span>Location</span>'
                + '<input class="emby-input weather-channel-zip" data-channel-id="' + escapeHtml(ch.id)
                + '" placeholder="50317 or London, UK" value="' + escapeHtml(location) + '">'
                + '</label></div>';
        }).join('');
    }

    async function loadWeather() {
        try {
            const status = await api('/weather/status');
            renderWeatherStatus(status);
        } catch (err) {
            reportApiError(err, 'Could not load weather settings.');
        }
    }

    async function saveWeatherSettings(successMessage) {
        const musicSelect = $('weather-music-library');
        const selected = musicSelect?.selectedOptions?.[0];
        try {
            const defaultRaw = $('weather-default-zip')?.value.trim() || '';
            const defaultLocation = defaultRaw ? readWeatherLocation(defaultRaw, 'Default location') : '';
            const channelZips = Array.from(qa('.weather-channel-zip'))
                .filter((input) => (input.value || '').trim().length >= 2)
                .map((input) => ({
                    id: input.dataset.channelId,
                    location: readWeatherLocation(input.value, 'Channel location')
                }));
            const saved = await api('/weather/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    weatherStarPermalinkQuery: serializeWeatherQueryFromForm(),
                    weatherStarAutoWideForSixteenNine: !!$('weather-auto-wide-169')?.checked,
                    weatherStarVariant: $('weather-star-variant')?.value || 'ws4kp',
                    weatherSource: $('weather-source')?.value || 'auto',
                    weatherMusicLibraryId: musicSelect?.value || '',
                    weatherMusicLibraryName: selected && selected.value ? selected.textContent : '',
                    defaultLocation,
                    weatherAlertOverlayMode: $('weather-alert-overlay-mode')?.value || 'off',
                    weatherAlertCutInIntervalMinutes: Number($('weather-alert-cutin-interval')?.value || 15),
                    weatherAlertCutInDurationSeconds: Number($('weather-alert-cutin-duration')?.value || 20),
                    channels: channelZips
                })
            });
            toast(typeof successMessage === 'string' ? successMessage : 'Weather settings saved.', 'success');
            renderWeatherStatus(saved);
            if (channels.length) {
                await loadChannels();
            }
        } catch (err) {
            toast(err.message || 'Could not save weather settings.', 'error');
        }
    }

    async function loadNews() {
        try {
            const [settings, feeds] = await Promise.all([
                api('/news/settings'),
                api('/news/feeds')
            ]);
            if ($('news-header')) $('news-header').value = settings.headerText || 'FlowWire News';
            if ($('news-count')) $('news-count').value = settings.articleCount || 8;
            if ($('news-refresh')) $('news-refresh').value = settings.refreshMinutes || 10;
            if ($('news-intro')) $('news-intro').value = settings.introText || '';
            if ($('news-outro')) $('news-outro').value = settings.outroText || '';
            if ($('news-tts')) $('news-tts').checked = settings.ttsEnabled !== false;
            if ($('news-tts-engine')) $('news-tts-engine').value = settings.ttsEngine === 'ai' ? 'ai' : 'google';
            if ($('news-ai-rewrite')) $('news-ai-rewrite').checked = !!settings.aiRewrite;
            if ($('news-show-header')) $('news-show-header').checked = settings.showHeader !== false;
            if ($('news-headlines-only')) $('news-headlines-only').checked = !!settings.readHeadlinesOnly;
            if ($('news-bulletin-enabled')) $('news-bulletin-enabled').checked = settings.bulletinVideosEnabled !== false;
            if ($('news-min-new')) $('news-min-new').value = settings.minNewStories || 1;
            renderNewsBulletinStatus(settings.bulletin);
            const voice = $('news-voice');
            if (voice) {
                const value = settings.voice || 'en-US';
                if (![...voice.options].some((o) => o.value === value)) {
                    const extra = document.createElement('option');
                    extra.value = value;
                    extra.textContent = value;
                    voice.appendChild(extra);
                }
                voice.value = value;
            }
            const noMusic = $('news-no-music');
            const music = $('news-music-library');
            const libraries = Array.isArray(settings.musicLibraries) ? settings.musicLibraries : [];
            if (music) {
                const current = settings.musicLibraryId || '';
                const silent = String(current).toLowerCase() === 'none';
                music.innerHTML = '<option value="">Use Off Air background music</option>' +
                    libraries.map((lib) => `<option value="${escapeHtml(lib.id)}">${escapeHtml(lib.name)}</option>`).join('');
                music.value = silent ? '' : current;
                if (!silent && music.value !== current) {
                    music.value = '';
                }
                if (noMusic) {
                    noMusic.checked = silent;
                }
                syncNewsMusicUi();
            }
            syncNewsTtsUi();
            newsFeeds = Array.isArray(feeds) ? feeds : [];
            if (newsFeeds.length === 0) {
                newsFeeds = [{ name: 'NPR News', url: 'https://feeds.npr.org/1001/rss.xml', enabled: true }];
            }
            renderNewsFeeds();
            await loadNewsPreview(false);
        } catch (err) {
            reportApiError(err, 'Could not load news settings.');
        }
    }

    function renderNewsFeeds() {
        const list = $('news-feeds-list');
        if (!list) return;
        list.innerHTML = newsFeeds.map((feed, index) => `
            <div class="news-feed-row" data-index="${index}">
                <label class="checkbox-field" title="Enabled">
                    <input type="checkbox" class="news-feed-enabled" ${feed.enabled !== false ? 'checked' : ''}>
                    <span class="channelflow-check-box" aria-hidden="true"></span>
                </label>
                <input type="text" class="emby-input news-feed-name" placeholder="Name" value="${escapeHtml(feed.name || '')}">
                <input type="url" class="emby-input news-feed-url" placeholder="https://example.com/rss.xml" value="${escapeHtml(feed.url || '')}">
                <button type="button" class="emby-button news-feed-remove" data-index="${index}">Remove</button>
            </div>
        `).join('');
        decorateCheckboxes();
        list.querySelectorAll('.news-feed-remove').forEach((btn) => {
            btn.onclick = () => {
                newsFeeds.splice(Number(btn.dataset.index), 1);
                renderNewsFeeds();
            };
        });
    }

    function collectNewsFeeds() {
        const list = $('news-feeds-list');
        if (!list) return newsFeeds;
        return Array.from(list.querySelectorAll('.news-feed-row')).map((row) => ({
            name: row.querySelector('.news-feed-name')?.value.trim() || '',
            url: row.querySelector('.news-feed-url')?.value.trim() || '',
            enabled: !!row.querySelector('.news-feed-enabled')?.checked
        })).filter((f) => f.url);
    }

    function addNewsFeedRow() {
        newsFeeds = collectNewsFeeds();
        newsFeeds.push({ name: '', url: '', enabled: true });
        renderNewsFeeds();
    }

    function syncNewsMusicUi() {
        const music = $('news-music-library');
        const noMusic = $('news-no-music');
        if (!music) {
            return;
        }
        music.disabled = !!noMusic?.checked;
    }

    function syncNewsTtsUi() {
        const enabled = !!$('news-tts')?.checked;
        const engine = $('news-tts-engine');
        if (engine) {
            engine.disabled = !enabled;
        }
    }

    async function saveNewsSettings() {
        const music = $('news-music-library');
        const noMusic = !!$('news-no-music')?.checked;
        await api('/news/settings', {
            method: 'PUT',
            body: JSON.stringify({
                headerText: $('news-header')?.value.trim() || 'FlowWire News',
                articleCount: Number($('news-count')?.value || 8),
                refreshMinutes: Number($('news-refresh')?.value || 10),
                ttsEnabled: !!$('news-tts')?.checked,
                ttsEngine: $('news-tts-engine')?.value === 'ai' ? 'ai' : 'google',
                aiRewrite: !!$('news-ai-rewrite')?.checked,
                showHeader: !!$('news-show-header')?.checked,
                readHeadlinesOnly: !!$('news-headlines-only')?.checked,
                voice: $('news-voice')?.value || 'en-US',
                introText: $('news-intro')?.value || '',
                outroText: $('news-outro')?.value || '',
                musicLibraryId: noMusic ? 'none' : (music?.value || ''),
                musicLibraryName: noMusic
                    ? 'None'
                    : (music?.value ? (music.selectedOptions?.[0]?.textContent || '').trim() : ''),
                minNewStories: Number($('news-min-new')?.value || 1),
                bulletinVideosEnabled: !!$('news-bulletin-enabled')?.checked
            })
        });
        toast('News settings saved.', 'success');
        await loadNews();
    }

    async function saveNewsFeeds() {
        newsFeeds = collectNewsFeeds();
        const saved = await api('/news/feeds', {
            method: 'PUT',
            body: JSON.stringify(newsFeeds)
        });
        newsFeeds = Array.isArray(saved) ? saved : newsFeeds;
        renderNewsFeeds();
        toast('RSS feeds saved.', 'success');
        await loadNewsPreview(true);
    }

    async function loadNewsPreview(force) {
        const data = await api('/news/preview' + (force ? '?force=true' : ''));
        const meta = $('news-preview-meta');
        const box = $('news-preview');
        if (meta) {
            meta.textContent = data.fetchedAt
                ? `Last fetched ${new Date(data.fetchedAt).toLocaleString()} · ${(data.articles || []).length} headlines`
                : 'No headlines cached yet. Save a feed and click Refresh headlines.';
        }
        if (box) {
            const articles = data.articles || [];
            box.innerHTML = articles.length
                ? articles.map((a) => `<article class="news-preview-item">${a.imageUrl ? `<img class="news-preview-thumb" src="${escapeHtml(a.imageUrl)}" alt="" referrerpolicy="no-referrer">` : ''}<div><h4>${escapeHtml(a.title)}</h4><p>${escapeHtml(a.summary || '')}</p></div></article>`).join('')
                : '<p class="hint">No headlines. Add an enabled RSS URL and refresh.</p>';
        }
    }

    function formatNewsBulletinStatus(bulletin) {
        if (!bulletin) {
            return 'No bulletin has run yet.';
        }
        if (bulletin.isRunning) {
            return 'Creating news video… intro, speech, pictures, and outro. The current video stays until this one finishes.';
        }
        const leftover = formatNewsLeftovers(bulletin.leftovers);
        const next = bulletin.nextRunAt ? `Next run ${new Date(bulletin.nextRunAt).toLocaleString()}.` : '';
        if (!bulletin.lastRunAt) {
            return `${next || 'No bulletin has run yet.'}${leftover}`.trim();
        }
        const when = new Date(bulletin.lastRunAt).toLocaleString();
        if (bulletin.lastCreated) {
            const path = bulletin.lastVideoPath ? ` Saved ${bulletin.lastVideoPath}.` : '';
            return `Last video ${when} · ${bulletin.lastEncodedStoryCount || 0} stories.${path} ${next}${leftover}`.trim();
        }
        const reason = bulletin.lastSkipReason || 'skipped';
        return `Last run ${when}: ${reason} ${next}${leftover}`.trim();
    }

    function formatNewsLeftovers(leftovers) {
        if (!leftovers) {
            return '';
        }
        const bits = [];
        if (leftovers.workFolders) bits.push(`${leftovers.workFolders} failed job folder${leftovers.workFolders === 1 ? '' : 's'}`);
        if (leftovers.partialFiles) bits.push(`${leftovers.partialFiles} incomplete file${leftovers.partialFiles === 1 ? '' : 's'}`);
        if (leftovers.extraVideos) bits.push(`${leftovers.extraVideos} old video${leftovers.extraVideos === 1 ? '' : 's'}`);
        if (leftovers.scratchFiles) bits.push(`${leftovers.scratchFiles} leftover scratch item${leftovers.scratchFiles === 1 ? '' : 's'}`);
        return bits.length ? ` Leftovers: ${bits.join(', ')}.` : '';
    }

    function setNewsBulletinButtons(running) {
        ['btn-run-news-bulletin', 'btn-run-news-bulletin-task'].forEach((id) => {
            const btn = $(id);
            if (!btn) {
                return;
            }
            btn.disabled = !!running;
            if (btn.id === 'btn-run-news-bulletin-task') {
                btn.textContent = running ? 'Creating…' : 'Create News Video';
            } else {
                btn.textContent = running ? 'Creating…' : 'Create news video now';
            }
        });
    }

    function renderNewsBulletinStatus(bulletin) {
        const text = formatNewsBulletinStatus(bulletin);
        const newsEl = $('news-bulletin-status');
        const taskEl = $('task-news-bulletin-status');
        if (newsEl) newsEl.textContent = text;
        if (taskEl) taskEl.textContent = text;
        setNewsBulletinButtons(!!bulletin?.isRunning);
    }

    async function runNewsBulletin() {
        setNewsBulletinButtons(true);
        try {
            const start = await api('/news/bulletins/run', { method: 'POST' });
            toast(start?.alreadyRunning
                ? 'News video is already being created…'
                : 'Creating news video in the background…', 'info');
            renderNewsBulletinStatus({ ...(start?.bulletin || {}), isRunning: true });
            const bulletin = await pollNewsBulletinUntilIdle();
            if (bulletin?.lastCreated) {
                toast('News video created.', 'success');
            } else {
                toast(bulletin?.lastSkipReason || 'News video finished.', 'success');
            }
        } finally {
            setNewsBulletinButtons(false);
        }
    }

    async function pollNewsBulletinUntilIdle() {
        const deadline = Date.now() + 15 * 60 * 1000;
        while (Date.now() < deadline) {
            const settings = await api('/news/settings');
            renderNewsBulletinStatus(settings.bulletin);
            if (!settings.bulletin?.isRunning) {
                return settings.bulletin;
            }
            await new Promise((resolve) => setTimeout(resolve, 3000));
        }
        throw new Error('News video is still encoding. Leave this tab open or check status again in a few minutes.');
    }

    async function cleanupNewsBulletins() {
        const result = await api('/news/bulletins/cleanup', { method: 'POST' });
        renderNewsBulletinStatus(result?.bulletin);
        const removed = (result?.removedWorkFolders || 0)
            + (result?.removedPartialFiles || 0)
            + (result?.removedOldVideos || 0)
            + (result?.removedScratchFiles || 0);
        toast(removed
            ? `Removed ${removed} leftover news file${removed === 1 ? '' : 's'}. The current video was kept.`
            : 'No leftover news jobs or old videos to remove.', 'success');
    }

    let catalogCleanupPollTimer = null;

    function renderCatalogCleanupStatus(status) {
        const el = $('catalog-cleanup-status');
        const runBtn = $('btn-run-catalog-cleanup');
        const grace = $('catalog-cleanup-grace');
        if (!el || !status) {
            return;
        }

        if (grace && typeof status.gracePeriodDays === 'number') {
            grace.value = String(status.gracePeriodDays);
        }

        if (status.isRunning) {
            el.textContent = 'Catalog cleanup is running… marking missing items, scanning remapped local files, then deleting rows past the grace period.';
            if (runBtn) {
                runBtn.disabled = true;
                runBtn.textContent = 'Cleaning…';
            }
            renderCatalogLocalScanStatus(status);
            return;
        }

        if (runBtn) {
            runBtn.disabled = !!status.localScan?.isRunning;
            runBtn.textContent = 'Run Catalog Cleanup';
        }

        if (status.lastError) {
            el.textContent =
                `Last run failed: ${status.lastError}` +
                (status.lastCompletedAt ? ` · previous success ${new Date(status.lastCompletedAt).toLocaleString()}` : '');
        } else if (status.lastCompletedAt) {
            el.textContent =
                `Last run ${new Date(status.lastCompletedAt).toLocaleString()}: marked ${status.markedMissing} missing, removed ${status.removed}. ` +
                `${status.currentlyMissing} catalog row(s) currently missing (waiting on the ${status.gracePeriodDays}-day grace period).`;
        } else {
            el.textContent =
                `${status.currentlyMissing} catalog row(s) currently missing. Grace period is ${status.gracePeriodDays} day(s). Run cleanup after a catalog sync, or wait for the daily task.`;
        }

        renderCatalogLocalScanStatus(status);
    }

    function renderCatalogLocalScanStatus(status) {
        const el = $('catalog-local-scan-status');
        const scanBtn = $('btn-scan-local-catalog');
        const scan = status?.localScan;
        if (!el) {
            return;
        }

        if (scan?.isRunning) {
            el.textContent =
                `Scanning remapped local files… ${scan.processedItems}/${scan.totalItems} checked · ${scan.found} found · ${scan.restored} restored · ${scan.markedMissing} marked missing.`;
            if (scanBtn) {
                scanBtn.disabled = true;
                scanBtn.textContent = 'Scanning…';
            }
            return;
        }

        if (scanBtn) {
            scanBtn.disabled = !!status?.isRunning;
            scanBtn.textContent = 'Scan Local Files';
        }

        if (scan?.lastError) {
            el.textContent =
                `Last scan failed: ${scan.lastError}` +
                (scan.lastCompletedAt ? ` · previous success ${new Date(scan.lastCompletedAt).toLocaleString()}` : '');
            return;
        }

        if (scan?.lastCompletedAt) {
            el.textContent =
                `Last scan ${new Date(scan.lastCompletedAt).toLocaleString()}: ${scan.found} file(s) present at remapped paths, restored ${scan.restored}, marked ${scan.markedMissing} missing, skipped ${scan.skipped} with no path.`;
            return;
        }

        el.textContent = 'Scan catalog items against remapped local files. If the remapped path exists, the Jellyfin item is present.';
    }

    async function loadCatalogCleanup() {
        if (!syncConfigPage()) {
            return;
        }

        try {
            const status = await api('/tasks/catalog-cleanup');
            renderCatalogCleanupStatus(status);
            if (status.isRunning || status.localScan?.isRunning) {
                startCatalogCleanupPolling();
            } else {
                stopCatalogCleanupPolling();
            }
        } catch (err) {
            const el = $('catalog-cleanup-status');
            if (el) {
                el.textContent = 'Could not load catalog cleanup status.';
            }
        }
    }

    function startCatalogCleanupPolling() {
        if (catalogCleanupPollTimer) {
            return;
        }

        catalogCleanupPollTimer = setInterval(() => {
            loadCatalogCleanup().catch(() => {});
        }, 3000);
    }

    function stopCatalogCleanupPolling() {
        if (catalogCleanupPollTimer) {
            clearInterval(catalogCleanupPollTimer);
            catalogCleanupPollTimer = null;
        }
    }

    async function saveCatalogCleanupSettings() {
        const days = Number($('catalog-cleanup-grace')?.value || '7');
        const status = await api('/tasks/catalog-cleanup', {
            method: 'PUT',
            body: JSON.stringify({ gracePeriodDays: days })
        });
        renderCatalogCleanupStatus(status);
        toast('Catalog cleanup grace period saved.', 'success');
    }

    async function runCatalogCleanup() {
        const result = await api('/tasks/catalog-cleanup/run', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('Catalog cleanup is already running.', 'info');
        } else {
            toast('Catalog cleanup started.', 'success');
        }
        if (result.status) {
            renderCatalogCleanupStatus(result.status);
        }
        startCatalogCleanupPolling();
        await loadCatalogCleanup();
    }

    async function runCatalogLocalScan() {
        const result = await api('/tasks/catalog-cleanup/scan-local', { method: 'POST' });
        if (result.alreadyRunning) {
            toast('A catalog scan or cleanup is already running.', 'info');
        } else {
            toast('Local file scan started.', 'success');
        }
        if (result.status) {
            renderCatalogCleanupStatus(result.status);
        }
        startCatalogCleanupPolling();
        await loadCatalogCleanup();
    }

    function renderAboutDl(elementId, rows) {
        const el = $(elementId);
        if (!el) {
            return;
        }
        el.innerHTML = rows
            .filter((row) => row[1] != null && row[1] !== '')
            .map(([label, value, href]) => {
                const text = escapeHtml(String(value));
                const dd = href
                    ? `<a href="${escapeHtml(String(href))}" target="_blank" rel="noopener">${text}</a>`
                    : text;
                return `<div class="about-row"><dt>${escapeHtml(label)}</dt><dd>${dd}</dd></div>`;
            })
            .join('') || '<div class="about-row"><dt>Status</dt><dd>Unavailable</dd></div>';
    }

    async function loadAbout() {
        try {
            const info = await api('/about');
            const app = info.app || {};
            const system = info.system || {};
            const transcode = info.transcode || {};
            renderAboutDl('about-app', [
                ['Author', app.author, app.authorUrl],
                ['Version', app.version],
                ['Build', app.revision],
                ['Packaging', app.packagingLabel || (app.packaging === 'docker' ? 'Docker' : 'Non-Docker')],
                ['Image', app.image],
                ['Runtime', app.framework],
                ['Homepage', app.homepage, app.homepage]
            ]);
            renderAboutDl('about-system', [
                ['Operating system', system.os],
                ['Architecture', system.architecture],
                ['Host', system.machineName],
                ['Environment', system.environmentName],
                ['CPU cores', system.processorCount],
                ['Memory (working set)', system.workingSet],
                ['GC heap', system.gcHeap],
                ['Uptime', system.uptime],
                ['Time zone', system.timeZone],
                ['Server time', system.serverTime],
                ['UTC', system.utcTime],
                ['Listen port', system.listenPort],
                ['Config folder', system.configFolder],
                ['PostgreSQL host', system.postgresHost],
                ['Active viewers', system.activeViewers]
            ]);
            const vaapi = transcode.useVaapi
                ? `Available (${transcode.vaapiDevice})`
                : (transcode.vaapiDeviceExists ? transcode.vaapiDevice : `Missing (${transcode.vaapiDevice || 'none'})`);
            renderAboutDl('about-transcode', [
                ['Encoder', transcode.encoder],
                ['Hardware acceleration', transcode.hardwareAcceleration],
                ['Source', transcode.source === 'saved' ? 'Saved on Transcode tab' : 'Container / environment default'],
                ['VAAPI device', vaapi],
                ['FFmpeg path', transcode.ffmpegPath],
                ['FFmpeg', transcode.ffmpegVersion],
                ['Environment default', `${transcode.environmentAcceleration || 'none'} / ${transcode.environmentEncoder || 'libx264'}`]
            ]);
        } catch (err) {
            reportApiError(err, 'Could not load About information.');
            renderAboutDl('about-app', [['Status', err.message || 'Could not load About information.']]);
        }
    }

    function syncTranscodeUi() {
        const accel = $('transcode-hwaccel')?.value || 'none';
        const vaapiField = $('transcode-vaapi-field');
        const vaapiHint = $('transcode-vaapi-hint');
        const showVaapi = accel === 'vaapi';
        if (vaapiField) {
            vaapiField.classList.toggle('hidden', !showVaapi);
        }
        if (vaapiHint) {
            vaapiHint.classList.toggle('hidden', !showVaapi);
        }
    }

    function renderTranscodeStatus(settings) {
        const el = $('transcode-status');
        if (!el || !settings) {
            return;
        }
        const lines = [
            `Effective encoder: ${settings.effectiveEncoder || 'libx264'}`,
            `FFmpeg: ${settings.ffmpegPath || 'ffmpeg'}`,
            `Source: ${settings.source === 'saved' ? 'saved on this tab' : 'container environment'}`
        ];
        if (settings.vaapiRequested && !settings.vaapiDeviceExists) {
            lines.push(`VAAPI device ${settings.vaapiDevice || '/dev/dri/renderD128'} was not found. Using software encode.`);
        } else if (settings.useVaapi) {
            lines.push(`VAAPI device ${settings.vaapiDevice} is available.`);
        }
        if (settings.environment) {
            lines.push(`Container default: ${settings.environment.hardwareAcceleration || 'none'} / ${settings.environment.videoEncoder || 'libx264'}`);
        }
        const runAhead = Number(settings.runAheadSeconds ?? 15);
        lines.push(runAhead > 0
            ? `Run-ahead buffer: ${runAhead}s`
            : 'Run-ahead buffer: off (real time)');
        el.textContent = lines.join('\n');
    }

    async function loadTranscode() {
        try {
            const settings = await api('/transcode/settings');
            if ($('transcode-hwaccel')) {
                $('transcode-hwaccel').value = settings.hardwareAcceleration || 'none';
            }
            if ($('transcode-encoder')) {
                const encoder = settings.videoEncoder || 'auto';
                const select = $('transcode-encoder');
                if (![...select.options].some((o) => o.value === encoder)) {
                    const extra = document.createElement('option');
                    extra.value = encoder;
                    extra.textContent = encoder;
                    select.appendChild(extra);
                }
                select.value = encoder;
            }
            if ($('transcode-vaapi-device')) {
                $('transcode-vaapi-device').value = settings.vaapiDevice || '/dev/dri/renderD128';
            }
            if ($('transcode-runahead')) {
                $('transcode-runahead').value = String(settings.runAheadSeconds ?? 15);
            }
            renderTranscodeStatus(settings);
            syncTranscodeUi();
        } catch (err) {
            reportApiError(err, 'Could not load transcode settings.');
        }
    }

    async function saveTranscodeSettings() {
        const saved = await api('/transcode/settings', {
            method: 'PUT',
            body: JSON.stringify({
                hardwareAcceleration: $('transcode-hwaccel')?.value || 'none',
                videoEncoder: $('transcode-encoder')?.value || 'auto',
                vaapiDevice: ($('transcode-vaapi-device')?.value || '').trim(),
                runAheadSeconds: Number($('transcode-runahead')?.value || '15')
            })
        });
        toast('Transcode settings saved. New streams use these settings immediately.', 'success');
        renderTranscodeStatus(saved);
        if ($('transcode-hwaccel')) {
            $('transcode-hwaccel').value = saved.hardwareAcceleration || 'none';
        }
        if ($('transcode-encoder')) {
            $('transcode-encoder').value = saved.videoEncoder || 'auto';
        }
        if ($('transcode-vaapi-device')) {
            $('transcode-vaapi-device').value = saved.vaapiDevice || '/dev/dri/renderD128';
        }
        if ($('transcode-runahead')) {
            $('transcode-runahead').value = String(saved.runAheadSeconds ?? 15);
        }
        syncTranscodeUi();
        const result = $('transcode-test-result');
        if (result) {
            result.textContent = '';
        }
    }

    async function testTranscode() {
        const resultEl = $('transcode-test-result');
        if (resultEl) {
            resultEl.textContent = 'Running a 1-second test encode…';
        }
        const result = await api('/transcode/test', { method: 'POST' });
        if (result.ok) {
            toast(`Test encode succeeded with ${result.encoder}.`, 'success');
            if (resultEl) {
                resultEl.textContent = `Test encode succeeded with ${result.encoder}.`;
            }
            return;
        }
        const error = result.error || `ffmpeg exited ${result.exitCode}`;
        toast(error, 'error');
        if (resultEl) {
            resultEl.textContent = error;
        }
    }

    async function resetTranscodeSettings() {
        const saved = await api('/transcode/settings', {
            method: 'PUT',
            body: JSON.stringify({ resetToEnvironment: true })
        });
        toast('Transcode settings reset to container environment.', 'success');
        await loadTranscode();
        renderTranscodeStatus(saved);
    }

    async function loadGeneral() {
        try {
            const [settings, timeZones] = await Promise.all([
                api('/general/settings'),
                api('/general/timezones')
            ]);

            const tzSelect = $('general-schedule-tz');
            if (tzSelect) {
                const selected = settings.scheduleTimeZone || 'America/New_York';
                const options = Array.isArray(timeZones) ? timeZones : [];
                const hasSelected = options.some((tz) => tz.id === selected);

                tzSelect.innerHTML = '';
                if (!hasSelected && selected) {
                    const legacy = document.createElement('option');
                    legacy.value = selected;
                    legacy.textContent = `${selected} (saved — pick a listed zone)`;
                    legacy.selected = true;
                    tzSelect.appendChild(legacy);
                }

                options.forEach((tz) => {
                    const option = document.createElement('option');
                    option.value = tz.id;
                    option.textContent = tz.label || `${tz.id} (${tz.offset || ''})`;
                    if (tz.id === selected) {
                        option.selected = true;
                    }
                    tzSelect.appendChild(option);
                });

                if (!tzSelect.value && options.length > 0) {
                    tzSelect.value = options[0].id;
                }
            }

            if ($('general-debug-logging')) {
                $('general-debug-logging').checked = !!settings.debugLogging;
            }
            if ($('general-playout-days')) {
                $('general-playout-days').value = String(settings.playoutDaysToBuild ?? 14);
            }
            if ($('general-stream-idle-timeout')) {
                $('general-stream-idle-timeout').value = String(settings.streamIdleTimeoutSeconds ?? 30);
            }
            if ($('general-public-url')) {
                $('general-public-url').value = settings.publicBaseUrl || '';
            }
            if ($('general-api-key')) {
                $('general-api-key').value = settings.apiKey || '';
            }
        } catch (err) {
            reportApiError(err, 'Could not load general settings.');
        }
    }

    async function saveGeneralSettings() {
        if (!syncConfigPage()) {
            toast('ChannelFlow-Server is not ready. Reload the page.', 'error');
            return;
        }

        try {
            const saved = await api('/general/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    debugLogging: !!$('general-debug-logging')?.checked,
                    scheduleTimeZone: $('general-schedule-tz')?.value || 'America/New_York',
                    playoutDaysToBuild: Number($('general-playout-days')?.value || '14'),
                    streamIdleTimeoutSeconds: Number($('general-stream-idle-timeout')?.value || '30'),
                    publicBaseUrl: ($('general-public-url')?.value || '').trim(),
                    apiKey: ($('general-api-key')?.value || '').trim() || null
                })
            });
            if ($('general-public-url')) {
                $('general-public-url').value = saved.publicBaseUrl || '';
            }
            toast('General settings saved.', 'success');
            await loadGeneral();
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function generatePluginApiKey() {
        if (!confirm('Generate a new API key? The Jellyfin plugin will stop linking until you paste the new key.')) {
            return;
        }

        try {
            const saved = await api('/general/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    debugLogging: !!$('general-debug-logging')?.checked,
                    scheduleTimeZone: $('general-schedule-tz')?.value || 'America/New_York',
                    playoutDaysToBuild: Number($('general-playout-days')?.value || '14'),
                    generateApiKey: true
                })
            });
            if ($('general-api-key')) {
                $('general-api-key').value = saved.apiKey || '';
            }
            toast('New plugin API key saved. Copy it into the Jellyfin plugin.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function copyPluginApiKey() {
        const text = ($('general-api-key')?.value || '').trim();
        if (!text) {
            toast('No API key to copy.', 'error');
            return;
        }

        navigator.clipboard.writeText(text).then(() => toast('API key copied.', 'success')).catch(() => {
            window.prompt('Copy API key:', text);
        });
    }

    function normalizeConfigPageRoot(node) {
        if (!node) {
            return null;
        }

        if (node.id === 'ChannelFlowConfigPage') {
            return node;
        }

        if (typeof node.querySelector === 'function') {
            const nested = node.querySelector('#ChannelFlowConfigPage');
            if (nested) {
                return nested;
            }
        }

        if (typeof node.closest === 'function') {
            return node.closest('#ChannelFlowConfigPage');
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
            reportApiError(err, 'Could not load ChannelFlow lists.');
        }
    }

    function selectedLibraryIds(containerId) {
        const el = $(containerId);
        if (!el) {
            return [];
        }

        return Array.from(el.querySelectorAll('input[type="checkbox"]:checked'))
            .flatMap((input) => String(input.dataset.libIds || input.dataset.libId || '').split(','))
            .map((id) => id.trim())
            .filter(Boolean);
    }

    function libraryMemberIds(lib) {
        if (Array.isArray(lib.ids) && lib.ids.length) {
            return lib.ids.map(String);
        }

        return lib.id ? [String(lib.id)] : [];
    }

    function renderJellyfinLibraryGroup(containerId, libraries, selectedIds, group) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        const matching = (libraries || []).filter((lib) => (lib.groups || []).includes(group));
        if (!matching.length) {
            el.innerHTML = '<div class="empty-state">No matching libraries in the synced catalog yet.</div>';
            return;
        }

        const selected = new Set((selectedIds || []).map(String));
        el.innerHTML = matching.map((lib) => {
            const count = lib.itemCount || 0;
            const memberIds = libraryMemberIds(lib);
            const checked = memberIds.some((id) => selected.has(id)) ? ' checked' : '';
            return `<label class="field checkbox-field">
                <input type="checkbox" data-lib-id="${escapeHtml(String(lib.id || memberIds[0] || ''))}" data-lib-ids="${escapeHtml(memberIds.join(','))}"${checked}>
                <span class="channelflow-check-box" aria-hidden="true"></span>
                <span>${escapeHtml(lib.name)} <span class="library-pick-meta">${count} item${count === 1 ? '' : 's'}</span></span>
            </label>`;
        }).join('');
    }

    async function loadJellyfinLibraries() {
        try {
            const data = await api('/catalog/libraries') || {};
            const libraries = data.libraries || [];
            renderJellyfinLibraryGroup('jf-lib-tv', libraries, data.tvLibraryIds, 'tv');
            renderJellyfinLibraryGroup('jf-lib-movies', libraries, data.movieLibraryIds, 'movies');
            renderJellyfinLibraryGroup('jf-lib-music', libraries, data.musicLibraryIds, 'music');
            renderJellyfinLibraryGroup('jf-lib-musicvideos', libraries, data.musicVideoLibraryIds, 'musicvideos');
            renderJellyfinLibraryGroup('jf-lib-news', libraries, data.homeVideoLibraryIds, 'news');
            decorateCheckboxes();
            await loadCatalogMedia();
        } catch (err) {
            reportApiError(err, 'Could not load Jellyfin libraries.');
        }
    }

    async function saveJellyfinLibraries() {
        await api('/catalog/libraries', {
            method: 'PUT',
            body: JSON.stringify({
                tvLibraryIds: selectedLibraryIds('jf-lib-tv'),
                movieLibraryIds: selectedLibraryIds('jf-lib-movies'),
                musicLibraryIds: selectedLibraryIds('jf-lib-music'),
                musicVideoLibraryIds: selectedLibraryIds('jf-lib-musicvideos'),
                homeVideoLibraryIds: selectedLibraryIds('jf-lib-news')
            })
        });
        toast('Jellyfin libraries saved.', 'success');
        await loadJellyfinLibraries();
    }

    async function loadCatalogMedia() {
        try {
            const data = await api('/catalog/media') || {};
            renderCatalogMediaTable('jf-media-tv', data.tvShows);
            renderCatalogMediaTable('jf-media-movies', data.movies);
            renderCatalogMediaTable('jf-media-music', data.music);
            renderCatalogMediaTable('jf-media-musicvideos', data.musicVideos);
            renderCatalogMediaTable('jf-media-news', data.pastTenseNews);
        } catch (err) {
            reportApiError(err, 'Could not load synced catalog media.');
        }
    }

    function renderCatalogMediaTable(containerId, rows) {
        const el = $(containerId);
        if (!el) {
            return;
        }

        const items = Array.isArray(rows) ? rows : [];
        if (!items.length) {
            el.innerHTML = '<div class="empty-state">No synced items yet. Send libraries from the Jellyfin plugin, then run catalog sync.</div>';
            return;
        }

        el.innerHTML = `<table class="data-table">
            <thead>
                <tr>
                    <th>Title</th>
                    <th>Runtime</th>
                    <th>Chapters</th>
                    <th>Rating</th>
                    <th>Plot</th>
                    <th>Stars</th>
                    <th>Jellyfin path</th>
                    <th>IDs</th>
                </tr>
            </thead>
            <tbody>
                ${items.map((row) => `<tr>
                    <td>${escapeHtml(row.name || '')}</td>
                    <td>${escapeHtml(row.runtime || '')}</td>
                    <td>${escapeHtml(row.chapters || '')}</td>
                    <td>${escapeHtml(row.rating || '')}</td>
                    <td class="catalog-plot" title="${escapeHtml(row.plot || '')}">${escapeHtml(row.plot || '')}</td>
                    <td>${escapeHtml(row.stars || '')}</td>
                    <td class="catalog-path" title="${escapeHtml(row.path || '')}">${escapeHtml(row.path || '')}</td>
                    <td class="catalog-ids" title="${escapeHtml(row.ids || '')}">${escapeHtml(row.ids || '')}</td>
                </tr>`).join('')}
            </tbody>
        </table>`;
    }

    function renderListsTable() {
        const el = $('lists-table');
        if (!finTvLists.length) {
            el.innerHTML = '<div class="empty-state">No ChannelFlow lists registered yet. Add a Jellyfin playlist to use it in lineups.</div>';
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

        openModal(existing ? 'Edit ChannelFlow List' : 'Add ChannelFlow List', body, `
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
        if (!confirm('Remove this ChannelFlow list registration?')) return;
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
            <label class="field checkbox-field"><input id="sp-enabled" type="checkbox"${draft.enabled !== false ? ' checked' : ''}><span class="channelflow-check-box" aria-hidden="true"></span><span>Enabled</span></label>
            <label class="field"><span>Day of week</span>
                <select id="sp-day" class="emby-select">${DAYS.map((d, i) =>
                    `<option value="${i}"${draft.dayOfWeek === i ? ' selected' : ''}>${d}</option>`).join('')}</select></label>
            <label class="field"><span>Start time</span><input id="sp-time" type="time" class="emby-input" value="${slotTimeInputValue(draft.slotIndex || 0)}"></label>
            <label class="field"><span>Block length (30-min slots)</span><input id="sp-span" type="number" min="1" max="8" class="emby-input" value="${Math.max(1, draft.spanSlots || 1)}"></label>
            <label class="field"><span>Content mode</span>
                <select id="sp-content-mode" class="emby-select">
                    <option value="0">Fixed items</option>
                    <option value="1">Rule-based</option>
                    <option value="2">ChannelFlow list</option>
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
                panel.innerHTML = `<label class="field"><span>ChannelFlow list</span>
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
                    toast('Select a ChannelFlow list.', 'error');
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

    function stopGuideClock() {
        if (guideTimer) {
            clearInterval(guideTimer);
            guideTimer = null;
        }
    }

    function startGuideClock() {
        stopGuideClock();
        positionGuideNowLine();
        guideTimer = setInterval(positionGuideNowLine, 15000);
    }

    function guideTimeZone() {
        return (guideData && guideData.timeZone) || undefined;
    }

    function formatGuideClock(iso) {
        try {
            return new Date(iso).toLocaleTimeString([], {
                hour: 'numeric',
                minute: '2-digit',
                timeZone: guideTimeZone()
            });
        } catch (ignore) {
            return new Date(iso).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
        }
    }

    function formatGuideDate(iso) {
        try {
            return new Intl.DateTimeFormat('en-CA', {
                timeZone: guideTimeZone(),
                year: 'numeric',
                month: '2-digit',
                day: '2-digit'
            }).format(new Date(iso));
        } catch (ignore) {
            return new Date(iso).toISOString().slice(0, 10);
        }
    }

    function isGuideTabVisible() {
        const tab = $('tab-guide');
        return !!(tab && !tab.classList.contains('hidden') && !tab.hidden);
    }

    function refreshGuide() {
        if (!isGuideTabVisible()) {
            return Promise.resolve();
        }

        return loadGuide();
    }

    async function loadGuide(options) {
        const root = $('tv-guide');
        if (!root) {
            return;
        }

        const quiet = !!(options && options.quiet);
        if (!quiet) {
            root.innerHTML = '<div class="empty-state">Loading guide…</div>';
        }
        try {
            const params = new URLSearchParams({ hours: String(GUIDE_HOURS) });
            if (guideFromIso) {
                params.set('from', guideFromIso);
            } else if (guideDateFilter) {
                params.set('date', guideDateFilter);
            }
            params.set('_', String(Date.now()));
            guideData = await api('/guide?' + params.toString());
            guideFromIso = guideData.from;
            const dateInput = $('guide-date');
            if (dateInput) {
                dateInput.value = formatGuideDate(guideData.from);
            }
            const range = $('guide-range-label');
            if (range) {
                range.textContent = formatGuideClock(guideData.from) + ' – ' + formatGuideClock(guideData.to)
                    + (guideData.timeZone ? ' · ' + guideData.timeZone : '');
            }
            renderGuide();
            startGuideClock();
        } catch (err) {
            reportApiError(err, 'Could not load the TV guide.');
            root.innerHTML = '<div class="empty-state">Could not load the TV guide.</div>';
        }
    }

    function shiftGuide(hours) {
        if (!guideData || !guideData.from) {
            return;
        }
        const next = new Date(new Date(guideData.from).getTime() + hours * 3600000);
        guideFromIso = next.toISOString();
        guideDateFilter = null;
        loadGuide();
    }

    function jumpGuideToNow() {
        guideFromIso = null;
        guideDateFilter = null;
        loadGuide();
    }

    function jumpGuideToDate(date) {
        guideFromIso = null;
        guideDateFilter = date || null;
        loadGuide();
    }

    function positionGuideNowLine() {
        if (!guideData) {
            return;
        }
        const from = new Date(guideData.from).getTime();
        const to = new Date(guideData.to).getTime();
        const now = Date.now();
        const inWindow = now >= from && now <= to;
        const left = ((now - from) / 60000 * GUIDE_PX_PER_MIN) + 'px';
        qa('.tv-guide-now, .tv-guide-now-bar').forEach((line) => {
            line.classList.toggle('hidden', !inWindow);
            if (inWindow) {
                line.style.left = left;
            }
        });
    }

    function renderGuide() {
        const root = $('tv-guide');
        if (!root || !guideData) {
            return;
        }

        const channels = guideData.channels || [];
        const programs = guideData.programs || [];
        if (!channels.length) {
            root.innerHTML = '<div class="empty-state">No enabled channels. Create channels first.</div>';
            return;
        }

        const from = new Date(guideData.from).getTime();
        const to = new Date(guideData.to).getTime();
        const totalMin = Math.max(30, (to - from) / 60000);
        const width = totalMin * GUIDE_PX_PER_MIN;
        const byChannel = {};
        programs.forEach((p) => {
            const key = p.channelId;
            if (!byChannel[key]) {
                byChannel[key] = [];
            }
            byChannel[key].push(p);
        });

        let ticks = '';
        for (let m = 0; m < totalMin; m += 30) {
            ticks += '<div class="tv-guide-tick" style="left:' + (m * GUIDE_PX_PER_MIN) + 'px">'
                + escapeHtml(formatGuideClock(new Date(from + m * 60000).toISOString()))
                + '</div>';
        }

        const rows = channels.map((ch) => {
            const blocks = (byChannel[ch.id] || []).map((p) => {
                const start = new Date(p.start).getTime();
                const finish = new Date(p.finish).getTime();
                const left = Math.max(0, (start - from) / 60000) * GUIDE_PX_PER_MIN;
                const visibleEnd = Math.min(to, finish);
                const visibleStart = Math.max(from, start);
                const w = Math.max(10, (visibleEnd - visibleStart) / 60000 * GUIDE_PX_PER_MIN - 2);
                const now = Date.now();
                const isNow = start <= now && finish > now;
                const type = String(ch.contentType || '').toLowerCase();
                return '<button type="button" class="tv-guide-block type-' + escapeHtml(type)
                    + (isNow ? ' is-now' : '') + (p.isVirtual ? ' is-virtual' : '') + '"'
                    + ' data-program="' + escapeHtml(p.id) + '"'
                    + ' style="left:' + left + 'px;width:' + w + 'px">'
                    + '<strong>' + escapeHtml(p.title || 'Untitled') + '</strong>'
                    + (p.subTitle ? '<span>' + escapeHtml(p.subTitle) + '</span>' : '')
                    + '</button>';
            }).join('');

            const logo = ch.logoUrl
                ? '<img src="' + escapeHtml(ch.logoUrl) + '" alt="" loading="lazy">'
                : '<span class="tv-guide-logo-fallback"></span>';
            return '<div class="tv-guide-row">'
                + '<button type="button" class="tv-guide-channel" data-channel="' + escapeHtml(ch.id) + '">'
                + logo
                + '<div><div class="num">' + escapeHtml(ch.number) + '</div>'
                + '<div class="name">' + escapeHtml(ch.name) + '</div></div>'
                + '</button>'
                + '<div class="tv-guide-track" style="width:' + width + 'px"><div class="tv-guide-now-bar"></div>' + blocks + '</div>'
                + '</div>';
        }).join('');

        root.innerHTML = '<div class="tv-guide-header">'
            + '<div class="tv-guide-corner">Channel</div>'
            + '<div class="tv-guide-times"><div class="tv-guide-times-inner" style="width:' + width + 'px">'
            + ticks + '<div class="tv-guide-now"></div></div></div>'
            + '</div>'
            + '<div class="tv-guide-body">' + rows + '</div>';

        const body = root.querySelector('.tv-guide-body');
        const times = root.querySelector('.tv-guide-times');
        if (body && times) {
            body.addEventListener('scroll', () => { times.scrollLeft = body.scrollLeft; });
        }
        root.querySelectorAll('[data-program]').forEach((btn) => {
            btn.onclick = () => openGuideProgram(btn.dataset.program);
        });
        root.querySelectorAll('[data-channel]').forEach((btn) => {
            btn.onclick = () => {
                selectedChannelId = btn.dataset.channel;
                switchTab('lineups');
            };
        });
        positionGuideNowLine();
    }

    function openGuideProgram(id) {
        const program = (guideData && guideData.programs || []).find((p) => p.id === id);
        if (!program) {
            return;
        }
        const channel = (guideData.channels || []).find((c) => c.id === program.channelId);
        const when = formatGuideClock(program.start) + ' – ' + formatGuideClock(program.finish);
        const meta = [program.episode, program.year, program.rating, (program.categories || []).join(', ')]
            .filter(Boolean)
            .join(' · ');
        const poster = program.posterUrl
            ? '<img class="tv-guide-poster" src="' + escapeHtml(program.posterUrl) + '" alt="">'
            : '';
        openModal(
            program.title || 'Programme',
            poster
                + '<p class="hint">' + escapeHtml((channel ? channel.number + ' · ' + channel.name + ' · ' : '') + when) + '</p>'
                + (meta ? '<p class="hint">' + escapeHtml(meta) + '</p>' : '')
                + (program.subTitle ? '<h4>' + escapeHtml(program.subTitle) + '</h4>' : '')
                + (program.description ? '<p>' + escapeHtml(program.description) + '</p>' : '<p class="hint">No description.</p>'),
            '<button type="button" class="emby-button" id="btn-guide-to-lineup">Open lineup</button>'
                + '<button type="button" class="raised button-submit emby-button" id="btn-guide-close-modal">Close</button>'
        );
        const closeBtn = $('btn-guide-close-modal');
        if (closeBtn) {
            closeBtn.onclick = closeModal;
        }
        const lineupBtn = $('btn-guide-to-lineup');
        if (lineupBtn) {
            lineupBtn.onclick = () => {
                closeModal();
                selectedChannelId = program.channelId;
                switchTab('lineups');
            };
        }
    }

    const TAB_PATHS = {
        guide: '/guide',
        channels: '/channels',
        presets: '/presets',
        lineups: '/lineups',
        list: '/lists',
        jellyfin: '/jellyfin-library',
        special: '/special',
        commercials: '/commercials',
        commercialbrainz: '/commercialbrainz',
        ebs: '/ebs',
        emergency: '/emergency',
        ai: '/ai',
        weather: '/weather',
        news: '/news',
        transcode: '/transcode',
        general: '/general',
        tasks: '/tasks',
        about: '/about',
        credits: '/credits'
    };

    const TAB_TITLES = {
        guide: 'TV Guide',
        channels: 'Channels',
        presets: 'Presets',
        lineups: 'Lineups',
        list: 'Lists',
        jellyfin: 'Jellyfin Library',
        special: 'Special Presentation',
        commercials: 'Commercials',
        commercialbrainz: 'CommercialBrainz',
        ebs: 'Off Air',
        emergency: 'Emergency Broadcast System',
        ai: 'AI',
        weather: 'Weather',
        news: 'News',
        transcode: 'Transcode',
        general: 'General',
        tasks: 'Tasks',
        about: 'About',
        credits: 'Credits'
    };

    const TAB_SUBTITLES = {
        guide: 'What\'s on now across ChannelFlow-Server channels',
        channels: 'Manage Live TV channels',
        presets: 'Create the Binarygeek119 ready-made lineup',
        lineups: 'Edit 24-hour schedules and playout',
        list: 'Register Jellyfin playlists as ChannelFlow lists',
        jellyfin: 'Choose which Jellyfin libraries to sync from',
        special: 'Recurring blocks that override the normal lineup',
        commercials: 'Jellyfin commercial library and blackframe scan',
        commercialbrainz: 'YouTube commercial pool from CommercialBrainz',
        ebs: 'Playback when a channel has nothing scheduled',
        emergency: 'NOAA watches and warnings on TV, movies, and music',
        ai: 'AI lineup generation and tagging',
        weather: 'WeatherStar live channels',
        news: 'FlowWire News',
        transcode: 'Hardware encoding for live MPEG-TS streams',
        general: 'Server-wide ChannelFlow-Server settings',
        tasks: 'Rebuild playouts and maintenance',
        about: 'Version, system, and transcode information',
        credits: 'People and projects ChannelFlow builds on'
    };

    function normalizePathname(pathname) {
        let path = String(pathname || '/').split('?')[0];
        const prefix = appPathBase();
        if (prefix && (path === prefix || path.startsWith(prefix + '/'))) {
            path = path.slice(prefix.length) || '/';
        }
        if (path.length > 1) {
            path = path.replace(/\/+$/, '');
        }
        return path || '/';
    }

    function tabFromPath(pathname) {
        const path = normalizePathname(pathname);
        if (path === '/' || path === '/index.html' || path === '/login') {
            return 'channels';
        }

        if (path === '/setup') {
            return 'general';
        }

        for (const name of Object.keys(TAB_PATHS)) {
            if (TAB_PATHS[name] === path) {
                return name;
            }
        }

        if (path === '/list') {
            return 'list';
        }

        if (path === '/jellyfin' || path === '/library') {
            return 'jellyfin';
        }

        return 'channels';
    }

    function isModifiedClick(event) {
        return event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0;
    }

    function onTabLinkClick(event) {
        const link = event.target.closest('a[data-tab]');
        if (!link || isModifiedClick(event) || !TAB_PATHS[link.dataset.tab]) {
            return;
        }

        event.preventDefault();
        switchTab(link.dataset.tab);
    }

    let tabRoutesBound = false;

    function bindTabRoutes() {
        if (tabRoutesBound) {
            return;
        }

        tabRoutesBound = true;
        document.addEventListener('click', onTabLinkClick);
        window.addEventListener('popstate', () => {
            const editorId = channelEditorIdFromPath(location.pathname);
            if (editorId) {
                openDeepChannelEditor(editorId, { fromRoute: true }).catch((err) => reportApiError(err, 'Could not open channel editor.'));
                return;
            }

            closeDeepChannelEditor({ skipHistory: true });
            switchTab(tabFromPath(location.pathname), { skipHistory: true });
        });
    }

    function applyTabFromLocation() {
        const path = normalizePathname(location.pathname);
        if (path === '/login' || channelEditorIdFromPath(path)) {
            return;
        }

        const tab = tabFromPath(path);
        switchTab(tab, { skipHistory: true });
        const app = document.getElementById('app-shell');
        if (app && !app.classList.contains('hidden') && (path === '/' || path === '/index.html' || path === '/setup')) {
            history.replaceState({ tab }, '', withAppBase(TAB_PATHS[tab]));
        }
    }

    function switchTab(name, options) {
        options = options || {};
        if (!TAB_PATHS[name]) {
            name = 'channels';
        }

        if (!syncConfigPage()) {
            return;
        }

        if (!options.keepEditor) {
            closeDeepChannelEditor({ skipHistory: true, stay: true });
        }

        qa('.channelflow-tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.tab === name));
        document.querySelectorAll('.tab-panel').forEach((p) => {
            const on = p.id === 'tab-' + name;
            p.classList.toggle('active', on);
            p.classList.toggle('hidden', !on);
            p.hidden = !on;
        });
        stopOnAirPolling();
        stopGuideClock();
        if (name === 'channels') startOnAirPolling();
        if (name === 'guide') loadGuide();
        if (name === 'general') loadGeneral();
        if (name === 'ebs') loadEbs();
        if (name === 'emergency') loadWeather();
        if (name === 'ai') loadAi();
        if (name === 'weather') loadWeather();
        if (name === 'news') loadNews();
        if (name === 'transcode') loadTranscode();
        if (name === 'about') loadAbout();
        if (name === 'presets') loadPresets();
        if (name === 'lineups') loadLineups();
        if (name === 'list') loadLists();
        if (name === 'jellyfin') loadJellyfinLibraries();
        if (name === 'tasks') {
            loadCatalogCleanup();
            api('/news/settings').then((settings) => renderNewsBulletinStatus(settings.bulletin)).catch(() => {});
        }
        if (name === 'special') loadSpecialPresentations();
        if (name === 'commercials') loadCommercials();
        if (name === 'commercialbrainz') loadCommercialBrainz();

        const path = TAB_PATHS[name];
        if (!options.skipHistory && normalizePathname(location.pathname) !== path) {
            history.pushState({ tab: name }, '', withAppBase(path));
        }

        document.title = (TAB_TITLES[name] || name) + ' · ChannelFlow-Server';
        window.dispatchEvent(new CustomEvent('channelflow-tabchange', {
            detail: { tab: name, title: TAB_TITLES[name], subtitle: TAB_SUBTITLES[name] }
        }));
    }

    function copyText(elementId) {
        const el = $(elementId);
        const text = (el && 'value' in el && el.value) ? el.value : (el?.textContent || '');
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

    function decorateCheckboxes() {
        qa('.checkbox-field').forEach((label) => {
            if (label.querySelector('.channelflow-check-box')) {
                return;
            }

            const input = label.querySelector('input[type="checkbox"]');
            if (!input) {
                return;
            }

            const box = document.createElement('span');
            box.className = 'channelflow-check-box';
            box.setAttribute('aria-hidden', 'true');
            input.insertAdjacentElement('afterend', box);
        });
    }

    function bindEvents() {
        if (!configPage) {
            return;
        }

        decorateCheckboxes();

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

        bindTabRoutes();
        click('btn-guide-prev', () => shiftGuide(-3));
        click('btn-guide-next', () => shiftGuide(3));
        click('btn-guide-now', () => jumpGuideToNow());
        change('guide-date', (e) => jumpGuideToDate(e.target.value));
        click('btn-new-channel', () => openNewChannelForm());
        click('btn-close-channel', () => showChannelForm(false));
        click('btn-cancel-channel', () => showChannelForm(false));
        click('btn-delete-channel', () => deleteChannel(editingChannelId));
        click('btn-deep-ch-back', () => {
            const openedAsWindow = !!(window.opener && !window.opener.closed);
            closeDeepChannelEditor();
            if (!openedAsWindow) {
                switchTab('channels');
            }
        });
        click('btn-deep-ch-save', () => saveDeepChannel().catch((err) => reportApiError(err, 'Could not save channel.')));
        click('btn-deep-ch-add-playlist', addDeepChannelPlaylist);
        const channelForm = $('channel-form');
        if (channelForm) {
            channelForm.onsubmit = saveChannel;
        }
        change('ch-content-type', toggleWeatherFields);
        change('deep-ch-content-type', toggleWeatherFields);
        change('ch-logo-set', () => populateLogoSelectors({ logoSetId: $('ch-logo-set').value, logoFileName: '' }));
        change('deep-ch-logo-set', () => populateLogoSelectors({ logoSetId: $('deep-ch-logo-set').value, logoFileName: '' }, 'deep-ch'));
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
        click('btn-save-jellyfin-libraries', () => saveJellyfinLibraries().catch((e) => toast(e.message, 'error')));
        click('btn-add-special', () => openSpecialPresentationForm().catch((e) => toast(e.message, 'error')));
        change('special-channel-select', loadSpecialPresentations);

        click('btn-sync-commercials', () => api('/commercials/sync', { method: 'POST' })
            .then(() => { toast('Commercial sync started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-scan-blackframes', () => api('/commercials/scan-blackframes', { method: 'POST' })
            .then(() => { toast('Blackframe scan started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-add-cb-playlist', () => addSearchPlaylist().catch((e) => toast(e.message, 'error')));
        click('btn-save-brainz', () => saveBrainzSettings().catch((e) => toast(e.message, 'error')));
        click('btn-preview-brainz', () => previewBrainz().catch((e) => toast(e.message, 'error')));
        click('btn-sync-brainz', () => syncBrainz().catch((e) => toast(e.message, 'error')));
        click('btn-rebuild-all', () => api('/tasks/rebuild-all', { method: 'POST' })
            .then(() => {
                toast('Rebuild all started in background. This may take several minutes.', 'success');
                $('task-status').textContent = 'Rebuild all playouts running in background…';
            })
            .catch((e) => toast(e.message, 'error')));
        click('btn-save-catalog-cleanup', () => saveCatalogCleanupSettings().catch((e) => toast(e.message, 'error')));
        click('btn-run-catalog-cleanup', () => runCatalogCleanup().catch((e) => toast(e.message, 'error')));
        click('btn-scan-local-catalog', () => runCatalogLocalScan().catch((e) => toast(e.message, 'error')));

        qa('.btn-copy').forEach((btn) => btn.onclick = () => copyText(btn.dataset.copyTarget));
        click('btn-save-general', saveGeneralSettings);
        click('btn-copy-api-key', copyPluginApiKey);
        click('btn-generate-api-key', () => generatePluginApiKey().catch((e) => toast(e.message, 'error')));
        click('btn-save-ebs', () => saveEbsSettings().catch((e) => toast(e.message, 'error')));
        click('btn-save-ai-settings', () => saveAiSettings().catch((e) => toast(e.message, 'error')));
        click('btn-test-ai', () => { void testAiConnection(); });
        click('btn-ai-generate-all', () => generateAllAiLineups().catch((e) => toast(e.message, 'error')));
        click('btn-ai-cancel-generate-all', () => cancelGenerateAll().catch((e) => toast(e.message, 'error')));
        click('btn-weather-guide-cache-generate', () => generateWeatherGuideCache().catch((e) => toast(e.message, 'error')));
        click('btn-weather-guide-cache-clear', () => clearWeatherGuideCache().catch((e) => toast(e.message, 'error')));
        click('btn-ai-apply', () => applyAiLineup().catch((e) => toast(e.message, 'error')));
        click('btn-ai-discard', discardAiPreview);
        change('ai-enabled', updateAiUiState);
        click('btn-upload-ebs-usa', () => uploadEbsSlate('usa', 'ebs-usa-file'));
        click('btn-upload-ebs-international', () => uploadEbsSlate('international', 'ebs-international-file'));
        click('btn-remove-ebs-usa', () => removeEbsSlate('usa'));
        click('btn-remove-ebs-international', () => removeEbsSlate('international'));
        click('btn-save-weather', () => saveWeatherSettings());
        click('btn-save-weather-source', () => saveWeatherSettings());
        click('btn-save-weather-alerts', () => saveWeatherSettings('Emergency Broadcast System settings saved.'));
        click('btn-test-weather-alerts', () => { void testWeatherAlerts(); });
        change('weather-alert-overlay-mode', toggleWeatherAlertCutInFields);
        click('btn-save-news', () => saveNewsSettings().catch((e) => toast(e.message, 'error')));
        change('news-no-music', syncNewsMusicUi);
        change('news-tts', syncNewsTtsUi);
        click('btn-save-transcode', () => saveTranscodeSettings().catch((e) => toast(e.message, 'error')));
        click('btn-test-transcode', () => testTranscode().catch((e) => toast(e.message, 'error')));
        click('btn-reset-transcode', () => resetTranscodeSettings().catch((e) => toast(e.message, 'error')));
        change('transcode-hwaccel', syncTranscodeUi);
        click('btn-save-news-feeds', () => saveNewsFeeds().catch((e) => toast(e.message, 'error')));
        click('btn-add-news-feed', addNewsFeedRow);
        click('btn-preview-news', () => loadNewsPreview(true).catch((e) => toast(e.message, 'error')));
        click('btn-run-news-bulletin', () => runNewsBulletin().catch((e) => toast(e.message, 'error')));
        click('btn-run-news-bulletin-task', () => runNewsBulletin().catch((e) => toast(e.message, 'error')));
        click('btn-cleanup-news-bulletin', () => cleanupNewsBulletins().catch((e) => toast(e.message, 'error')));
        click('btn-cleanup-news-bulletin-task', () => cleanupNewsBulletins().catch((e) => toast(e.message, 'error')));
        change('ebs-display-mode', updateEbsFieldVisibility);
        change('ebs-slate-variant', refreshEbsPreviews);
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
    }

    function init(page) {
        const app = document.getElementById('app-shell');
        if (app && app.classList.contains('hidden')) {
            return Promise.resolve();
        }

        if (!syncConfigPage(page)) {
            return Promise.resolve();
        }

        bindEvents();
        applyTabFromLocation();
        return refresh().then(() => {
            const editorId = channelEditorIdFromPath(location.pathname);
            if (editorId) {
                return openDeepChannelEditor(editorId, { fromRoute: true });
            }
        }).catch((err) => reportApiError(err, 'Could not load ChannelFlow-Server admin.'));
    }

    function resolveConfigPage(preferred) {
        preferred = normalizeConfigPageRoot(preferred);

        if (isActiveConfigPage(preferred)) {
            return preferred;
        }

        const pages = document.querySelectorAll('#ChannelFlowConfigPage');
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

    function bootChannelFlowAdmin(page) {
        page = resolveConfigPage(page);
        if (!page || !window.ChannelFlow || !window.ChannelFlow.init) {
            return false;
        }

        window.ChannelFlow.init(page);
        return true;
    }

    window.ChannelFlow = { init, refresh, loadChannels, bootChannelFlowAdmin, switchTab, tabFromPath };

    document.addEventListener('DOMContentLoaded', function () {
        const app = document.getElementById('app-shell');
        if (app && app.classList.contains('hidden')) {
            return;
        }

        const page = document.getElementById('ChannelFlowConfigPage');
        if (page && window.ChannelFlow) {
            window.ChannelFlow.init(page);
        }
    });
})();
