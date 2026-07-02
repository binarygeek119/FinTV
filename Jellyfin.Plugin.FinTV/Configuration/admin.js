(function () {
    'use strict';

    const CONTENT_TYPES = ['TV Show', 'Movie', 'Music Video', 'Music', 'Weather'];
    const CANDIDATE_KINDS = ['Jellyfin Item', 'Collection', 'Filter Query'];
    const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];

    let channels = [];
    let logoSets = [];
    let selectedChannelId = null;
    let editingChannelId = null;
    let lineupSlots = [];
    let lineupOverrides = [];
    let itemTitleCache = {};
    let channelFilter = '';
    let channelPresets = [];
    let presetNumberingMode = 0;
    let configPage = null;

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
            if (parsed.title) {
                return parsed.title;
            }

            if (parsed.errors) {
                return Object.values(parsed.errors).flat().join(' ');
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

        throw new Error((err && err.message) || 'Request failed');
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
                dataType: 'json',
                headers: {
                    accept: 'application/json'
                }
            };

            if (body) {
                ajaxOptions.contentType = 'application/json';
                ajaxOptions.data = body;
            }

            return ApiClient.ajax(ajaxOptions)
                .then(normalizeApiResponse)
                .catch(readApiFailure);
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

        return fetch(url, fetchOptions).then(async (res) => {
            if (!res.ok) {
                const text = await res.text();
                throw new Error(parseErrorMessage(text || res.statusText));
            }

            if (res.status === 204) {
                return null;
            }

            const text = await res.text();
            return normalizeApiResponse(text ? JSON.parse(text) : null);
        });
    }

    function toast(message, type) {
        const el = document.createElement('div');
        el.className = 'toast' + (type ? ' ' + type : '');
        el.textContent = message;
        $('toast-container').appendChild(el);
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

    function parseWeatherCoordinates(latInput, lonInput) {
        let latText = String(latInput ?? '').trim();
        let lonText = String(lonInput ?? '').trim();

        if (latText.includes(',')) {
            const parts = latText.split(',').map((part) => part.trim()).filter(Boolean);
            if (parts.length >= 2) {
                latText = parts[0];
                lonText = parts[1];
            }
        }

        const lat = latText === '' ? null : Number(latText);
        const lon = lonText === '' ? null : Number(lonText);

        if (latText !== '' && Number.isNaN(lat)) {
            throw new Error('Weather latitude must be a valid number (e.g. 41.60574).');
        }

        if (lonText !== '' && Number.isNaN(lon)) {
            throw new Error('Weather longitude must be a valid number (e.g. -93.55002).');
        }

        if (lat != null && (lat < -90 || lat > 90)) {
            throw new Error('Weather latitude must be between -90 and 90.');
        }

        if (lon != null && (lon < -180 || lon > 180)) {
            throw new Error('Weather longitude must be between -180 and 180.');
        }

        return { lat, lon };
    }

    function splitWeatherCoordinateInput() {
        const latEl = $('ch-lat');
        const lonEl = $('ch-lon');
        if (!latEl || !lonEl || !latEl.value.includes(',')) {
            return;
        }

        try {
            const coords = parseWeatherCoordinates(latEl.value, lonEl.value);
            if (coords.lat != null) {
                latEl.value = String(coords.lat);
            }

            if (coords.lon != null) {
                lonEl.value = String(coords.lon);
            }
        } catch (ignore) {
            // Keep raw input until save validates.
        }
    }

    function formatWeatherCoordinate(value) {
        if (value == null || value === '') {
            return '';
        }

        return String(value);
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

    function candidateSummary(candidate) {
        if (candidate.kind === 1 && candidate.collectionName) return `Collection: ${candidate.collectionName}`;
        if (candidate.kind === 2 && candidate.filterJson) return 'Filter query';
        if (candidate.jellyfinItemId) return itemLabel(candidate.jellyfinItemId);
        return CANDIDATE_KINDS[candidate.kind] || 'Candidate';
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

    function filteredChannels() {
        if (!channelFilter) return channels;
        const q = channelFilter.toLowerCase();
        return channels.filter((c) =>
            c.name.toLowerCase().includes(q) ||
            formatChannelNumber(c.number).includes(q) ||
            CONTENT_TYPES[c.contentType].toLowerCase().includes(q));
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
                <td><span class="badge badge-type">${CONTENT_TYPES[c.contentType] || c.contentType}</span></td>
                <td><span class="badge ${c.enabled ? 'badge-on' : 'badge-off'}">${c.enabled ? 'On' : 'Off'}</span></td>
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
        channels = await api('/channels');
        renderChannelsList();

        const select = $('lineup-channel-select');
        select.innerHTML = channels.map((c) =>
            `<option value="${c.id}">${formatChannelNumber(c.number)} - ${escapeHtml(c.name)}</option>`).join('');
        if (!selectedChannelId && channels[0]) selectedChannelId = channels[0].id;
        select.value = selectedChannelId || '';

        await refreshDashboardStats();
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

        await loadLogoSetsForForm();
        editingChannelId = id;
        showChannelForm(true);
        $('channel-form-title').textContent = `Edit ${c.name}`;
        $('btn-delete-channel').classList.remove('hidden');

        $('ch-number').value = c.number;
        $('ch-name').value = c.name;
        $('ch-content-type').value = c.contentType;
        $('ch-aspect').value = c.aspectRatio;
        $('ch-scanlines').checked = c.scanlinesEnabled;
        $('ch-bug').value = c.bugPlacement;
        $('ch-audio').value = c.audioLanguage || 'eng';
        $('ch-lat').value = formatWeatherCoordinate(c.weatherLatitude);
        $('ch-lon').value = formatWeatherCoordinate(c.weatherLongitude);
        $('ch-enabled').checked = c.enabled;
        populateLogoSelectors(c);
        toggleWeatherFields();
        renderChannelsList();
        loadChannelNowPlaying(c.id);
    }

    async function saveChannel(e) {
        e.preventDefault();
        let number;
        try {
            number = parseChannelNumber($('ch-number').value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        let weatherCoords;
        try {
            weatherCoords = parseWeatherCoordinates($('ch-lat').value, $('ch-lon').value);
        } catch (err) {
            toast(err.message, 'error');
            return;
        }

        $('ch-lat').value = weatherCoords.lat == null ? '' : String(weatherCoords.lat);
        $('ch-lon').value = weatherCoords.lon == null ? '' : String(weatherCoords.lon);

        const payload = {
            number,
            name: $('ch-name').value.trim(),
            contentType: parseInt($('ch-content-type').value, 10),
            aspectRatio: parseInt($('ch-aspect').value, 10),
            scanlinesEnabled: $('ch-scanlines').checked,
            bugPlacement: parseInt($('ch-bug').value, 10),
            audioLanguage: $('ch-audio').value.trim(),
            logoSetId: $('ch-logo-set').value ? $('ch-logo-set').value : null,
            logoFileName: $('ch-logo-file').value || null,
            weatherLatitude: weatherCoords.lat,
            weatherLongitude: weatherCoords.lon,
            enabled: $('ch-enabled').checked
        };

        if (!payload.name) {
            toast('Channel name is required.', 'error');
            return;
        }

        try {
            if (editingChannelId) {
                await api('/channels/' + editingChannelId, { method: 'PUT', body: payload });
                toast('Channel updated.', 'success');
            } else {
                await api('/channels', { method: 'POST', body: payload });
                toast('Channel created.', 'success');
            }
            showChannelForm(false);
            await loadChannels();
        } catch (err) {
            toast(err.message, 'error');
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

        const data = await api('/lineups/' + selectedChannelId);
        lineupSlots = (data.lineup && data.lineup.slots) || [];
        lineupOverrides = data.overrides || [];
        if (lineupSlots.length === 0) {
            lineupSlots = Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }));
        }

        await collectItemIdsFromSlots(lineupSlots);
        renderLineupGrid();
        renderOverrideList();
        $('lineup-preview-banner').classList.add('hidden');
    }

    function renderLineupGrid() {
        const grid = $('lineup-grid');
        grid.innerHTML = lineupSlots.sort((a, b) => a.slotIndex - b.slotIndex).map((s) => {
            const count = (s.candidates || []).length;
            const first = count ? candidateSummary(s.candidates[0]) : 'Empty slot';
            return `<div class="slot-card ${count ? 'has-items' : 'empty'}" data-slot="${s.slotIndex}">
                <div class="time">${slotTime(s.slotIndex)}</div>
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
        const slot = lineupSlots.find((s) => s.slotIndex === index) || { slotIndex: index, candidates: [] };
        slot.candidates = slot.candidates || [];
        const channel = channels.find((c) => c.id === selectedChannelId);

        const body = `
            <p class="hint">Editing ${slotTime(index)} · add multiple weighted candidates for smart rotation.</p>
            <div id="slot-candidates" class="candidate-list">${renderCandidateRows(slot.candidates)}</div>
            <div class="field">
                <span>Add candidate</span>
                <select id="slot-add-kind" class="emby-select">
                    <option value="0">Jellyfin item</option>
                    <option value="1">Collection name</option>
                    <option value="2">Filter JSON</option>
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
            } else {
                panel.innerHTML = `<label class="field"><span>Filter JSON</span>
                    <textarea id="slot-filter" class="emby-input" rows="3" placeholder='{"genre":"Comedy"}'></textarea></label>
                    <button type="button" class="emby-button" id="slot-add-filter">Add filter</button>`;
                document.getElementById('slot-add-filter').onclick = () => {
                    const json = document.getElementById('slot-filter').value.trim();
                    if (!json) return;
                    slot.candidates.push({ kind: 2, filterJson: json, weight: 1, sortOrder: slot.candidates.length });
                    refreshCandidateList(slot);
                };
            }
        }

        function refreshCandidateList(currentSlot) {
            document.getElementById('slot-candidates').innerHTML = renderCandidateRows(currentSlot.candidates);
            bindCandidateRowActions(currentSlot);
        }

        function bindCandidateRowActions(currentSlot) {
            document.querySelectorAll('[data-remove-candidate]').forEach((btn) => {
                btn.onclick = () => {
                    currentSlot.candidates.splice(parseInt(btn.dataset.removeCandidate, 10), 1);
                    currentSlot.candidates.forEach((c, i) => { c.sortOrder = i; });
                    refreshCandidateList(currentSlot);
                };
            });
            document.querySelectorAll('[data-weight]').forEach((input) => {
                input.onchange = () => {
                    const idx = parseInt(input.dataset.weight, 10);
                    currentSlot.candidates[idx].weight = Math.max(1, parseInt(input.value, 10) || 1);
                };
            });
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
            <div class="sub">${CANDIDATE_KINDS[c.kind] || 'Item'} · weight ${c.weight || 1}</div></div>
            <input type="number" min="1" value="${c.weight || 1}" data-weight="${i}" style="width:70px">
            <button type="button" data-remove-candidate="${i}">Remove</button>
        </div>`).join('');
    }

    async function saveLineup() {
        try {
            await api('/lineups/' + selectedChannelId, { method: 'PUT', body: JSON.stringify(lineupSlots) });
            toast('Lineup saved.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function rebuildLineup() {
        try {
            await api('/lineups/' + selectedChannelId + '/rebuild', { method: 'POST' });
            toast('Playout rebuild started.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    async function previewLineup() {
        const dateVal = $('lineup-preview-date').value || todayIsoDate();
        try {
            const data = await api('/lineups/' + selectedChannelId + '/preview', {
                method: 'POST',
                body: JSON.stringify({ date: dateVal })
            });
            const filled = (data.slots || []).filter((s) => s.candidateCount > 0).length;
            $('lineup-preview-banner').classList.remove('hidden');
            $('lineup-preview-banner').textContent = `Preview for ${data.date}: ${filled}/48 slots have candidates.`;
        } catch (err) {
            toast(err.message, 'error');
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

    async function loadCommercials() {
        const list = await api('/commercials');
        const status = await api('/commercials/scan-status');
        $('commercial-status').textContent = status ? JSON.stringify(status, null, 2) : 'No scan running.';

        if (!list || !list.length) {
            $('commercial-list').innerHTML = '<div class="empty-state">No commercials synced yet.</div>';
            return;
        }

        $('commercial-list').innerHTML = `<table class="data-table">
            <thead><tr><th>Title</th><th>Duration</th><th>Chapters</th></tr></thead>
            <tbody>${list.map((c) => `<tr>
                <td>${escapeHtml(c.title)}</td>
                <td>${Math.round((c.duration && c.duration.totalSeconds) || 0)}s</td>
                <td>${(c.chapters || []).length}</td>
            </tr>`).join('')}</tbody></table>`;
    }

    async function loadLogos() {
        const sets = await api('/logos/sets') || [];
        if (!sets.length) {
            $('logo-set-info').innerHTML = '<div class="empty-state">No logo sets imported yet.</div>';
            return;
        }

        $('logo-set-info').innerHTML = sets.map((s) => {
            const files = (s.entries || []).slice(0, 48).map((e) =>
                `<span class="badge badge-type" style="margin:.15rem">${escapeHtml(e.displayName || e.fileName)}</span>`).join('');
            return `<div class="card"><h3>${escapeHtml(s.name)}</h3><p class="hint">${(s.entries || []).length} logos indexed · assign per channel in the Channels editor</p><div>${files || '<span class="hint">No files found</span>'}</div></div>`;
        }).join('');
    }

    async function loadPresets() {
        presetNumberingMode = parseInt($('preset-numbering-mode').value, 10) || 0;
        channelPresets = await api('/channels/presets?numberingMode=' + presetNumberingMode) || [];
        renderPresetsList();
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
        const updateExisting = $('preset-update-existing').checked;
        presetNumberingMode = parseInt($('preset-numbering-mode').value, 10) || 0;
        try {
            const result = await api('/channels/presets/apply', {
                method: 'POST',
                body: {
                    numberingMode: presetNumberingMode,
                    skipExisting: !updateExisting,
                    updateExisting: updateExisting
                }
            });
            const lines = [];
            if (result.created && result.created.length) {
                lines.push(`Created ${result.created.length}: ${result.created.map((r) => formatChannelNumber(r.number) + ' ' + r.name).join(', ')}`);
            }
            if (result.updated && result.updated.length) {
                lines.push(`Updated ${result.updated.length}: ${result.updated.map((r) => formatChannelNumber(r.number) + ' ' + r.name).join(', ')}`);
            }
            if (result.skipped && result.skipped.length) {
                lines.push(`Skipped ${result.skipped.length}.`);
            }
            $('presets-result').classList.remove('hidden');
            $('presets-result').textContent = lines.join('\n') || 'No changes made.';
            toast(lines[0] || 'Presets applied.', 'success');
            await loadPresets();
            await loadChannels();
        } catch (err) {
            toast(err.message, 'error');
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
        const source = Number($('setup-ebs-music-source')?.value || '1');
        const field = $('setup-ebs-library-field');
        if (field) field.style.display = source === 1 ? '' : 'none';
    }

    function populateEbsMusicLibraries(libraries, selectedId, selectedName) {
        const select = $('setup-ebs-music-library');
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

    async function loadSetup() {
        try {
            const data = await api('/setup/urls');
            applySetupData(data);
            try {
                const settings = await api('/setup/settings');
                if ($('setup-public-base')) $('setup-public-base').value = settings.publicBaseUrl || '';
                if ($('setup-ebs-music-source')) $('setup-ebs-music-source').value = String(settings.ebsBackgroundMusicSource ?? 1);
                populateEbsMusicLibraries(
                    settings.musicLibraries,
                    settings.ebsBackgroundMusicLibraryId || '',
                    settings.ebsBackgroundMusicLibraryName || 'Background Music'
                );
                updateEbsLibraryFieldVisibility();
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
        const ebsBackgroundMusicSource = Number($('setup-ebs-music-source')?.value || '1');
        const librarySelect = $('setup-ebs-music-library');
        const selectedOption = librarySelect?.selectedOptions?.[0];
        const ebsBackgroundMusicLibraryId = selectedOption?.value || null;
        const ebsBackgroundMusicLibraryName = selectedOption?.textContent?.trim() || 'Background Music';
        try {
            const data = await api('/setup/settings', {
                method: 'PUT',
                body: JSON.stringify({
                    publicBaseUrl: publicBaseUrl || null,
                    ebsBackgroundMusicSource,
                    ebsBackgroundMusicLibraryId,
                    ebsBackgroundMusicLibraryName
                })
            });
            applySetupData(data);
            if ($('setup-public-base')) $('setup-public-base').value = publicBaseUrl;
            updateEbsLibraryFieldVisibility();
            toast('Live TV URLs updated.', 'success');
        } catch (err) {
            toast(err.message, 'error');
        }
    }

    function syncConfigPage(preferred) {
        const resolved = resolveConfigPage(preferred || configPage);
        if (resolved) {
            configPage = resolved;
        }

        return configPage;
    }

    function switchTab(name) {
        if (!syncConfigPage()) {
            return;
        }

        qa('.fintv-tabs .tab').forEach((t) => t.classList.toggle('active', t.dataset.tab === name));
        qa('.tab-panel').forEach((p) => p.classList.toggle('active', p.id === 'tab-' + name));
        if (name === 'setup') loadSetup();
        if (name === 'presets') loadPresets();
        if (name === 'lineups') loadLineups();
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
        if (!configPage || configPage.dataset.fintvBound === '1') {
            return;
        }

        configPage.dataset.fintvBound = '1';

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
        const latInput = $('ch-lat');
        if (latInput) {
            latInput.addEventListener('change', splitWeatherCoordinateInput);
            latInput.addEventListener('blur', splitWeatherCoordinateInput);
        }
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

        click('btn-sync-commercials', () => api('/commercials/sync', { method: 'POST' })
            .then(() => { toast('Commercial sync started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-scan-blackframes', () => api('/commercials/scan-blackframes', { method: 'POST' })
            .then(() => { toast('Blackframe scan started.', 'success'); return loadCommercials(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-sync-logos', () => api('/logos/sets/binarygeek119/sync', { method: 'POST' })
            .then(() => { toast('Logo set refreshed.', 'success'); return loadLogos(); })
            .catch((e) => toast(e.message, 'error')));
        click('btn-rebuild-all', () => api('/tasks/rebuild-all', { method: 'POST' })
            .then(() => { toast('Rebuild all playouts started.', 'success'); $('task-status').textContent = 'Rebuild queued for all channels.'; })
            .catch((e) => toast(e.message, 'error')));

        qa('.btn-copy').forEach((btn) => btn.onclick = () => copyText(btn.dataset.copyTarget));
        click('btn-save-setup', saveSetupSettings);
        change('setup-ebs-music-source', updateEbsLibraryFieldVisibility);
        click('btn-apply-presets', applyPresets);
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
        return refresh().catch((err) => toast(err.message, 'error'));
    }

    function resolveConfigPage(preferred) {
        if (isActiveConfigPage(preferred)) {
            return preferred;
        }

        const pages = document.querySelectorAll('#FinTVConfigPage');
        for (let i = pages.length - 1; i >= 0; i--) {
            if (isActiveConfigPage(pages[i])) {
                return pages[i];
            }
        }

        return null;
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
    });
    boot();
}
