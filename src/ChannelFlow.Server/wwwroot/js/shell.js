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

    function svgIcon(paths) {
        return '<svg class="nav-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">' + paths + '</svg>';
    }

    const navIcons = {
        guide: svgIcon('<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M8 2v4M16 2v4M3 10h18"/><path d="M8 14h3M8 17h8"/>'),
        general: svgIcon('<circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1.1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/>'),
        channels: svgIcon('<rect x="2" y="8" width="20" height="12" rx="2"/><path d="M7 20h10M12 8V5M9 3.5L12 5l3-1.5"/>'),
        lineups: svgIcon('<rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18"/><path d="M8 14h.01M12 14h.01M16 14h.01M8 18h.01M12 18h.01M16 18h.01"/>'),
        presets: svgIcon('<path d="M12 3l1.4 4.4L18 9l-4.6 1.6L12 15l-1.4-4.4L6 9l4.6-1.6L12 3z"/><path d="M19 14l.8 2.4L22 17.2l-2.2.8L19 20.4l-.8-2.4-2.2-.8 2.2-.8L19 14z"/>'),
        list: svgIcon('<path d="M8 6h13M8 12h13M8 18h13"/><path d="M3 6h.01M3 12h.01M3 18h.01"/>'),
        special: svgIcon('<polygon points="12 2 15 9 22 10 17 15 18 22 12 18 6 22 7 15 2 10 9 9"/>'),
        jellyfin: svgIcon('<rect x="2" y="3" width="20" height="14" rx="2"/><path d="M8 21h8M12 17v4"/><path d="M10 9l5 3-5 3V9z"/>'),
        commercials: svgIcon('<path d="M3 11v2a1 1 0 0 0 1 1h2l5 4V6L6 10H4a1 1 0 0 0-1 1z"/><path d="M16 9.5a4 4 0 0 1 0 5"/><path d="M18.5 7a7 7 0 0 1 0 10"/>'),
        commercialbrainz: svgIcon('<path d="M8 8a3.2 3.2 0 0 1 5.4-2.2A3.2 3.2 0 0 1 18.5 9c0 .4 0 .8-.2 1.2A3.5 3.5 0 0 1 16 17H8.5A3.5 3.5 0 0 1 5 13.6 3.2 3.2 0 0 1 8 8z"/><path d="M9 12h.01M12 12h.01M15 12h.01"/>'),
        youtube: svgIcon('<rect x="2" y="6" width="20" height="12" rx="3"/><path d="M10 9.5v5l5-2.5-5-2.5z"/>'),
        ebs: svgIcon('<rect x="3" y="4" width="18" height="12" rx="2"/><path d="M8 20h8M12 16v4"/><path d="M7 8h10M7 12h6"/>'),
        weather: svgIcon('<path d="M17 18a4 4 0 0 0 0-8 5.5 5.5 0 0 0-10.4 1.5A3.5 3.5 0 0 0 7 18z"/><circle cx="7" cy="7" r="2.2"/><path d="M7 3.5v1M4.2 5.2l.8.8M3.5 8h1"/>'),
        news: svgIcon('<path d="M4 5h12a2 2 0 0 1 2 2v13H6a2 2 0 0 1-2-2V5z"/><path d="M18 8h2a2 2 0 0 1 2 2v8a3 3 0 0 1-3 3H6"/><path d="M8 9h6M8 13h6M8 17h4"/>'),
        emergency: svgIcon('<path d="M12 3l9 16H3L12 3z"/><path d="M12 10v4M12 17h.01"/>'),
        ai: svgIcon('<path d="M12 3v3M12 18v3M3 12h3M18 12h3M5.6 5.6l2.1 2.1M16.3 16.3l2.1 2.1M18.4 5.6l-2.1 2.1M7.7 16.3l-2.1 2.1"/><circle cx="12" cy="12" r="4"/>'),
        transcode: svgIcon('<rect x="2" y="4" width="8" height="7" rx="1.5"/><rect x="14" y="13" width="8" height="7" rx="1.5"/><path d="M10 7.5h2.5a3 3 0 0 1 3 3V13"/>'),
        tasks: svgIcon('<path d="M9 11l2 2 4-4"/><rect x="3" y="4" width="18" height="16" rx="2"/>'),
        about: svgIcon('<circle cx="12" cy="12" r="9"/><path d="M12 11v6M12 8h.01"/>'),
        credits: svgIcon('<path d="M17 21v-2a4 4 0 0 0-4-4H7a4 4 0 0 0-4 4v2"/><circle cx="10" cy="7" r="4"/><path d="M23 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75"/>')
    };

    const navGroupStarts = {
        channels: true,
        commercials: true,
        weather: true,
        transcode: true,
        about: true
    };

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
        transcode: 'Format and encoder for the live MPEG-TS pipeline',
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
        const active = nav.querySelector('a.active');
        if (active && typeof active.scrollIntoView === 'function') {
            active.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        }
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
            if (navGroupStarts[tab.dataset.tab] && nav.childElementCount) {
                const gap = document.createElement('div');
                gap.className = 'drawer-nav-gap';
                gap.setAttribute('aria-hidden', 'true');
                nav.appendChild(gap);
            }
            const link = document.createElement('a');
            link.href = tab.getAttribute('href') || ('/' + tab.dataset.tab);
            link.dataset.tab = tab.dataset.tab;
            link.innerHTML = navIcons[tab.dataset.tab] || navIcons.channels;
            const label = document.createElement('span');
            label.className = 'nav-label';
            label.textContent = tab.textContent.trim();
            link.appendChild(label);
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
        buildDrawer();
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
