(function () {
    const apiBase = '/FinTV/api';
    let channels = [];
    let selectedChannelId = null;
    let editingChannelId = null;
    let lineupSlots = [];

    function api(path, options) {
        return fetch(apiBase + path, Object.assign({
            headers: { 'Content-Type': 'application/json' }
        }, options || {})).then(async (res) => {
            if (!res.ok) throw new Error(await res.text());
            if (res.status === 204) return null;
            const text = await res.text();
            return text ? JSON.parse(text) : null;
        });
    }

    function slotTime(index) {
        const h = Math.floor(index / 2);
        const m = index % 2 ? '30' : '00';
        const h12 = ((h + 11) % 12) + 1;
        const ampm = h < 12 ? 'AM' : 'PM';
        return `${h12}:${m} ${ampm}`;
    }

    function switchTab(name) {
        document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
        document.querySelectorAll('.tab-panel').forEach(p => p.classList.toggle('active', p.id === 'tab-' + name));
        if (name === 'setup') loadSetup();
        if (name === 'lineups') loadLineups();
        if (name === 'commercials') loadCommercials();
        if (name === 'logos') loadLogos();
    }

    async function loadChannels() {
        channels = await api('/channels');
        const list = document.getElementById('channels-list');
        list.innerHTML = '<table><tr><th>#</th><th>Name</th><th>Type</th><th>Enabled</th><th></th></tr>' +
            channels.map(c => `<tr>
                <td>${c.number}</td><td>${c.name}</td><td>${c.contentType}</td><td>${c.enabled}</td>
                <td><button data-edit="${c.id}">Edit</button></td></tr>`).join('') + '</table>';
        list.querySelectorAll('[data-edit]').forEach(btn => btn.onclick = () => editChannel(btn.dataset.edit));

        const select = document.getElementById('lineup-channel-select');
        select.innerHTML = channels.map(c => `<option value="${c.id}">${c.number} - ${c.name}</option>`).join('');
        if (!selectedChannelId && channels[0]) selectedChannelId = channels[0].id;
        select.value = selectedChannelId || '';
    }

    function editChannel(id) {
        const c = channels.find(x => x.id === id);
        if (!c) return;
        editingChannelId = id;
        document.getElementById('channel-form').classList.remove('hidden');
        document.getElementById('ch-number').value = c.number;
        document.getElementById('ch-name').value = c.name;
        document.getElementById('ch-content-type').value = c.contentType;
        document.getElementById('ch-aspect').value = c.aspectRatio;
        document.getElementById('ch-scanlines').checked = c.scanlinesEnabled;
        document.getElementById('ch-bug').value = c.bugPlacement;
        document.getElementById('ch-audio').value = c.audioLanguage || 'eng';
        document.getElementById('ch-lat').value = c.weatherLatitude || '';
        document.getElementById('ch-lon').value = c.weatherLongitude || '';
        document.getElementById('ch-enabled').checked = c.enabled;
    }

    async function saveChannel(e) {
        e.preventDefault();
        const payload = {
            number: parseInt(document.getElementById('ch-number').value, 10),
            name: document.getElementById('ch-name').value,
            contentType: parseInt(document.getElementById('ch-content-type').value, 10),
            aspectRatio: parseInt(document.getElementById('ch-aspect').value, 10),
            scanlinesEnabled: document.getElementById('ch-scanlines').checked,
            bugPlacement: parseInt(document.getElementById('ch-bug').value, 10),
            audioLanguage: document.getElementById('ch-audio').value,
            weatherLatitude: parseFloat(document.getElementById('ch-lat').value) || null,
            weatherLongitude: parseFloat(document.getElementById('ch-lon').value) || null,
            enabled: document.getElementById('ch-enabled').checked
        };
        if (editingChannelId) await api('/channels/' + editingChannelId, { method: 'PUT', body: JSON.stringify(payload) });
        else await api('/channels', { method: 'POST', body: JSON.stringify(payload) });
        document.getElementById('channel-form').classList.add('hidden');
        editingChannelId = null;
        await loadChannels();
    }

    async function loadLineups() {
        selectedChannelId = document.getElementById('lineup-channel-select').value;
        if (!selectedChannelId) return;
        const data = await api('/lineups/' + selectedChannelId);
        lineupSlots = (data.lineup && data.lineup.slots) || [];
        if (lineupSlots.length === 0) lineupSlots = Array.from({ length: 48 }, (_, i) => ({ slotIndex: i, candidates: [] }));
        renderLineupGrid();
        document.getElementById('override-list').innerHTML = (data.overrides || []).map(o =>
            `<div class="card">${o.name} (${o.kind}) - ${o.slots.length} slots</div>`).join('');
    }

    function renderLineupGrid() {
        const grid = document.getElementById('lineup-grid');
        grid.innerHTML = lineupSlots.sort((a,b) => a.slotIndex - b.slotIndex).map(s =>
            `<div class="slot-card" data-slot="${s.slotIndex}">
                <div class="time">${slotTime(s.slotIndex)}</div>
                <div>${(s.candidates || []).length} items</div>
            </div>`).join('');
        grid.querySelectorAll('.slot-card').forEach(card => card.onclick = () => editSlot(parseInt(card.dataset.slot, 10)));
    }

    function editSlot(index) {
        const slot = lineupSlots.find(s => s.slotIndex === index) || { slotIndex: index, candidates: [] };
        const itemId = prompt('Add Jellyfin item ID (optional). Leave blank to skip.', '');
        if (itemId) {
            slot.candidates = slot.candidates || [];
            slot.candidates.push({ kind: 0, jellyfinItemId: itemId, weight: 1, sortOrder: slot.candidates.length });
        }
        const idx = lineupSlots.findIndex(s => s.slotIndex === index);
        if (idx >= 0) lineupSlots[idx] = slot; else lineupSlots.push(slot);
        renderLineupGrid();
    }

    async function saveLineup() {
        await api('/lineups/' + selectedChannelId, { method: 'PUT', body: JSON.stringify(lineupSlots) });
        alert('Lineup saved');
    }

    async function rebuildLineup() {
        await api('/lineups/' + selectedChannelId + '/rebuild', { method: 'POST' });
        alert('Playout rebuild started');
    }

    async function loadCommercials() {
        const list = await api('/commercials');
        document.getElementById('commercial-list').innerHTML = (list || []).map(c =>
            `<div class="card">${c.title} (${Math.round(c.duration.totalSeconds)}s) - ${(c.chapters||[]).length} chapters</div>`).join('');
        const status = await api('/commercials/scan-status');
        document.getElementById('commercial-status').textContent = JSON.stringify(status, null, 2);
    }

    async function loadLogos() {
        const sets = await api('/logos/sets');
        document.getElementById('logo-set-info').innerHTML = (sets || []).map(s =>
            `<div class="card">${s.name}: ${(s.entries||[]).length} logos</div>`).join('') || '<p>No logo sets imported yet.</p>';
    }

    async function loadSetup() {
        const data = await fetch('/FinTV/api/setup/urls').then(r => r.json());
        document.getElementById('setup-m3u').textContent = data.m3u;
        document.getElementById('setup-epg').textContent = data.epg;
        document.getElementById('setup-steps').innerHTML = (data.instructions || []).map(i => `<li>${i}</li>`).join('');
    }

    document.querySelectorAll('.tab').forEach(tab => tab.onclick = () => switchTab(tab.dataset.tab));
    document.getElementById('btn-new-channel').onclick = () => { editingChannelId = null; document.getElementById('channel-form').classList.remove('hidden'); };
    document.getElementById('btn-cancel-channel').onclick = () => document.getElementById('channel-form').classList.add('hidden');
    document.getElementById('channel-form').onsubmit = saveChannel;
    document.getElementById('lineup-channel-select').onchange = loadLineups;
    document.getElementById('btn-save-lineup').onclick = saveLineup;
    document.getElementById('btn-rebuild-lineup').onclick = rebuildLineup;
    document.getElementById('btn-sync-commercials').onclick = () => api('/commercials/sync', { method: 'POST' }).then(loadCommercials);
    document.getElementById('btn-scan-blackframes').onclick = () => api('/commercials/scan-blackframes', { method: 'POST' }).then(loadCommercials);
    document.getElementById('btn-sync-logos').onclick = () => api('/logos/sets/binarygeek119/sync', { method: 'POST' }).then(loadLogos);
    document.getElementById('btn-rebuild-all').onclick = () => api('/tasks/rebuild-all', { method: 'POST' }).then(() => alert('Rebuild started'));
    document.getElementById('btn-add-override').onclick = () => alert('Use the API to create detailed override lineups for special days like Friday movie night.');

    loadChannels().catch(console.error);
})();
