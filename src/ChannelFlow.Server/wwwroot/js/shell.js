(function () {
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

    const titles = {
        guide: 'TV Guide',
        channels: 'Channels',
        presets: 'Presets',
        lineups: 'Lineups',
        list: 'Lists',
        jellyfin: 'Jellyfin Library',
        special: 'Special Presentation',
        commercials: 'Commercials',
        commercialbrainz: 'CommercialBrainz',
        youtube: 'YouTube',
        ebs: 'Off Air',
        emergency: 'Emergency Broadcast System',
        ai: 'AI',
        weather: 'Weather',
        news: 'News',
        normalization: 'Normalization',
        transcode: 'Transcode',
        general: 'General',
        tasks: 'Tasks',
        about: 'About',
        credits: 'Credits'
    };

    const subtitles = {
        guide: 'What\'s on now across ChannelFlow-Server channels',
        channels: 'Manage Live TV channels',
        presets: 'Create the Binarygeek119 ready-made lineup',
        lineups: 'Edit 24-hour schedules and playout',
        list: 'Register Jellyfin playlists as ChannelFlow lists',
        jellyfin: 'Choose which Jellyfin libraries to sync from',
        special: 'Recurring blocks that override the normal lineup',
        commercials: 'Jellyfin commercials, saved playlists, and channel mapping',
        commercialbrainz: 'YouTube commercial pool from CommercialBrainz',
        youtube: 'YouTube cookies, Premium playback, and SponsorBlock',
        ebs: 'Playback when a channel has nothing scheduled',
        emergency: 'NOAA watches and warnings on TV, movies, and music',
        ai: 'AI lineup generation and tagging',
        weather: 'WeatherStar live channels',
        news: 'FlowWire News',
        normalization: 'Target format for the live MPEG-TS pipeline',
        transcode: 'Encoder for the live MPEG-TS pipeline',
        general: 'Server-wide ChannelFlow-Server settings',
        tasks: 'Rebuild playouts, clear the guide, and maintenance',
        about: 'Version, system, and transcode information',
        credits: 'People and projects ChannelFlow builds on'
    };

    async function api(path, options) {
        options = options || {};
        const res = await fetch(withAppBase(path), Object.assign({ credentials: 'same-origin', headers: { accept: 'application/json' } }, options));
        if (options.body && !options.headers) {
            /* handled below */
        }
        if (res.status === 204) {
            return null;
        }
        const text = await res.text();
        const data = text ? JSON.parse(text) : null;
        if (!res.ok) {
            throw new Error((data && data.message) || res.statusText);
        }
        return data;
    }

    async function postJson(path, body) {
        const res = await fetch(withAppBase(path), {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'content-type': 'application/json', accept: 'application/json' },
            body: JSON.stringify(body)
        });
        const text = await res.text();
        const data = text ? JSON.parse(text) : null;
        if (!res.ok) {
            throw new Error((data && data.message) || res.statusText);
        }
        return data;
    }

    function safeReturnPath(value) {
        if (!value || value[0] !== '/' || value[1] === '/') {
            return withAppBase('/channels');
        }

        const prefix = appPathBase();
        if (prefix && (value === prefix || value.startsWith(prefix + '/'))) {
            return value;
        }

        return withAppBase(value);
    }

    function syncDrawer(name, detail) {
        const nav = document.getElementById('drawer-nav');
        if (!nav) {
            return;
        }

        nav.querySelectorAll('a').forEach((a) => a.classList.toggle('active', a.dataset.tab === name));
        const title = (detail && detail.title) || titles[name];
        if (title) {
            document.getElementById('page-title').textContent = title;
        }
        const subtitle = document.getElementById('page-subtitle');
        if (subtitle) {
            subtitle.textContent = (detail && detail.subtitle) || subtitles[name] || '';
        }
    }

    function restorePathAfterLogin() {
        const path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        if (path !== '/login') {
            return;
        }

        const params = new URLSearchParams(location.search);
        const dest = safeReturnPath(params.get('ReturnUrl') || params.get('returnUrl') || '/channels');
        history.replaceState({}, '', dest);
    }

    function showLogin(needsSetup, userName) {
        document.getElementById('login-screen').classList.remove('hidden');
        document.getElementById('app-shell').classList.add('hidden');
        document.getElementById('auth-title').textContent = needsSetup ? 'Create admin' : 'Sign in';
        document.getElementById('login-submit').textContent = needsSetup ? 'Create account' : 'Sign in';
        const subtitle = document.getElementById('auth-subtitle');
        subtitle.textContent = needsSetup
            ? 'First launch — choose a username and password for ChannelFlow-Server'
            : '';
        subtitle.classList.toggle('hidden', !needsSetup);
        const confirmField = document.getElementById('login-pass-confirm-field');
        const confirmInput = document.getElementById('login-pass-confirm');
        const passInput = document.getElementById('login-pass');
        confirmField.classList.toggle('hidden', !needsSetup);
        confirmInput.required = !!needsSetup;
        confirmInput.minLength = needsSetup ? 8 : 0;
        passInput.minLength = needsSetup ? 8 : 0;
        passInput.autocomplete = needsSetup ? 'new-password' : 'current-password';
        if (userName) {
            document.getElementById('topbar-user').textContent = userName;
        }

        const path = (location.pathname || '/').replace(/\/+$/, '') || '/';
        if (path === '/' || path === '/index.html') {
            history.replaceState({}, '', withAppBase('/login'));
        }
        document.title = (needsSetup ? 'Create admin' : 'Sign in') + ' · ChannelFlow-Server';
    }

    function showApp(userName) {
        document.getElementById('login-screen').classList.add('hidden');
        document.getElementById('app-shell').classList.remove('hidden');
        document.getElementById('topbar-user').textContent = userName || '';
        restorePathAfterLogin();
        buildDrawer();
        const page = document.getElementById('ChannelFlowConfigPage');
        if (page && window.ChannelFlow) {
            window.ChannelFlow.init(page);
        }
        bindPathMappings();
    }

    function buildDrawer() {
        const nav = document.getElementById('drawer-nav');
        const tabs = document.querySelectorAll('#ChannelFlowConfigPage .channelflow-tabs .tab');
        nav.innerHTML = '';
        tabs.forEach((tab) => {
            const link = document.createElement('a');
            link.href = tab.getAttribute('href') || ('/' + tab.dataset.tab);
            link.textContent = tab.textContent.trim();
            link.dataset.tab = tab.dataset.tab;
            if (tab.classList.contains('active')) {
                link.classList.add('active');
            }
            nav.appendChild(link);
        });
        const current = window.ChannelFlow && window.ChannelFlow.tabFromPath
            ? window.ChannelFlow.tabFromPath(location.pathname)
            : 'channels';
        syncDrawer(current);
    }

    function bindPathMappings() {
        const general = document.getElementById('tab-general');
        if (!general || document.getElementById('path-map-card')) {
            return;
        }
        const card = document.createElement('div');
        card.className = 'section-card';
        card.id = 'path-map-card';
        card.innerHTML = '<div class="section-header"><h3>Library path remaps</h3></div>' +
            '<p class="muted">Jellyfin path prefix → local ChannelFlow-Server mount prefix</p>' +
            '<textarea id="path-mappings" rows="6" placeholder="/data/media = /media"></textarea>' +
            '<div class="toolbar"><button type="button" id="btn-save-paths" class="raised button-submit">Save remaps</button>' +
            '<button type="button" id="btn-test-paths" class="raised">Test remaps</button></div>' +
            '<pre id="path-test-result"></pre>';
        general.appendChild(card);
        fetch(withAppBase('/api/settings/path-mappings'), { credentials: 'same-origin' })
            .then((r) => r.json())
            .then((rows) => {
                document.getElementById('path-mappings').value = (rows || [])
                    .map((r) => r.jellyfinPrefix + ' = ' + r.localPrefix)
                    .join('\n');
            })
            .catch(() => { });
        document.getElementById('btn-save-paths').onclick = async () => {
            const mappings = document.getElementById('path-mappings').value.split('\n')
                .map((line) => line.split('='))
                .filter((p) => p.length >= 2)
                .map((p, i) => ({ jellyfinPrefix: p[0].trim(), localPrefix: p.slice(1).join('=').trim(), sortOrder: i }));
            await fetch(withAppBase('/api/settings/path-mappings'), {
                method: 'PUT',
                credentials: 'same-origin',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify(mappings)
            });
        };
        document.getElementById('btn-test-paths').onclick = async () => {
            const res = await fetch(withAppBase('/api/settings/path-mappings/test'), { method: 'POST', credentials: 'same-origin' });
            document.getElementById('path-test-result').textContent = JSON.stringify(await res.json(), null, 2);
        };
    }

    async function boot() {
        try {
            const status = await api('/api/auth/status');
            if (!status.authenticated) {
                showLogin(status.needsSetup);
            } else {
                showApp(status.userName);
            }

            document.getElementById('login-form').addEventListener('submit', async (e) => {
                e.preventDefault();
                const password = document.getElementById('login-pass').value;
                const confirm = document.getElementById('login-pass-confirm').value;
                const body = {
                    userName: document.getElementById('login-user').value,
                    password,
                    rememberMe: !!document.getElementById('login-remember')?.checked
                };
                const err = document.getElementById('login-error');
                try {
                    const needsSetup = status.needsSetup && !status.authenticated;
                    if (needsSetup && password !== confirm) {
                        throw new Error('Passwords do not match.');
                    }
                    const path = needsSetup ? '/api/auth/setup' : '/api/auth/login';
                    const result = await postJson(path, body);
                    showApp(result.userName);
                    err.textContent = '';
                } catch (ex) {
                    err.textContent = ex.message;
                }
            });
            document.getElementById('btn-logout').addEventListener('click', async () => {
                await fetch(withAppBase('/api/auth/logout'), { method: 'POST', credentials: 'same-origin' });
                location.reload();
            });
            window.addEventListener('channelflow-auth-required', () => showLogin(false));
            window.addEventListener('channelflow-tabchange', (e) => {
                if (e.detail && e.detail.tab) {
                    syncDrawer(e.detail.tab, e.detail);
                }
            });
        } catch (ex) {
            showLogin(true);
        }
    }

    document.addEventListener('DOMContentLoaded', boot);
})();
