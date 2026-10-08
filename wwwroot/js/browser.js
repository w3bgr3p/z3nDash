// browser.js — страница Browser: профили, прокси и потоки антидетект-браузера.
// Чем управляем, выбирается переключателем: ZennoBrowser или ShardX Launcher.
// Каждый провайдер приводит ответы своего API к общему виду, рендер один.

(function () {
    'use strict';

    const LS_KEY = 'browser.provider';

    async function apiJson(base, path, opts) {
        const r = await fetch(base + path, opts);
        const text = await r.text();
        if (!r.ok) {
            let msg = text;
            try { const j = JSON.parse(text); msg = j.error || j.message || text; } catch (_) {}
            throw new Error(r.status + (msg ? ' ' + String(msg).slice(0, 200) : ' ' + r.statusText));
        }
        if (!text) return null;
        try { return JSON.parse(text); } catch (_) { return text; }
    }

    function jsonBody(method, body) {
        return { method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) };
    }

    // host:port из строки прокси — для имени и списка, без логина и пароля
    function proxyEndpoint(uri) {
        const s = String(uri || '').trim();
        try { const u = new URL(s); if (u.hostname) return u.hostname + (u.port ? ':' + u.port : ''); } catch (_) {}
        const at = s.lastIndexOf('@');
        const rest = (at >= 0 ? s.slice(at + 1) : s).replace(/^[a-z0-9]+:\/\//i, '');
        const parts = rest.split(':');
        return parts.length >= 2 ? parts[0] + ':' + parts[1] : rest;
    }

    // Выбор в окне прокси: { proxyId } — существующий, { uri } — новый, { clear: true } — снять

    // ── ZennoBrowser: /zb/api/v1/* → ZB API (заголовок Api-Token ставит сервер) ──
    const zb = {
        id: 'zb',
        subtitle: 'ZennoBrowser API',
        cfgKey: 'zennoBrowser',
        defaultHost: 'http://localhost:8160',
        hasTags: true,
        hasThreads: true,
        newProxyHint: 'URL: http://login:pass@host:port or socks5://login:pass@host:port',
        // osVersion у /profiles/create: пусто — по ОС хоста
        platforms: [['', 'Auto (host OS)'], ['win10', 'Windows 10'], ['win11', 'Windows 11']],
        canNewFolder: false,
        extraStat: { label: 'Threads', sub: 'active' },
        proxyHeaders: ['Name', 'URI', 'Check Status', 'Last Check'],

        get(path) { return apiJson('/zb/api/v1', path); },
        del(path, body) { return apiJson('/zb/api/v1', path, body ? jsonBody('DELETE', body) : { method: 'DELETE' }); },

        async ping() { await this.get('/workspaces?start=0&total=1'); return ''; },

        async running() {
            const data = await this.get('/browser_instances?workspaceId=-1&start=0&total=1000');
            const map = {};
            (data?.items || []).forEach(i => { map[i.profileId] = { pid: i.processId || 0, ws: i.connectionString || '' }; });
            return map;
        },

        async profiles() {
            const [pd, fd] = await Promise.all([
                this.get('/profiles?workspaceId=-1&start=0&total=1000'),
                this.get('/profile_folders?workspaceId=-1&start=0&total=1000'),
            ]);
            // 0000…0000 и 0000…0001 — служебные «без папки» (локальная и облачная).
            // Профиль без folderId ZB кладёт в локальную, облачную в форме создания не показываем.
            const folders = (fd?.items || []).map(f => ({
                id: f.id,
                name: (f.name || String(f.id).slice(0, 8)) + (f.location === 'Cloud' ? ' (cloud)' : ''),
                virtual: /^0{8}-0{4}-0{4}-0{4}-0{11}[01]$/.test(String(f.id)),
            }));
            const profiles = (pd?.items || []).map(p => ({
                id: p.id,
                name: p.name || String(p.id).slice(0, 8),
                folderId: p.folderId || '',
                proxyId: p.proxy?.id || '',
                proxyName: p.proxy ? (p.proxy.name || String(p.proxy.id).slice(0, 8)) : '',
                tags: (p.tags || []).map(t => t.name || '').filter(Boolean),
                status: p.status || '',
                lastStart: p.lastStartTime || '',
            }));
            return { profiles, folders };
        },

        async proxies() {
            const data = await this.get('/proxies?workspaceId=-1&start=0&total=1000');
            return (data?.items || []).map(p => ({
                id: p.id,
                name: p.name || '—',
                uri: p.proxyUri || '—',
                badge: p.checkStatus || '—',
                badgeClass: p.checkStatus === 'Ok' ? 'running' : p.checkStatus === 'Unknown' ? 'unknown' : 'stopped',
                last: p.checkStatusLastUpdateTime ? new Date(p.checkStatusLastUpdateTime).toLocaleString() : '—',
            }));
        },

        async start(id) {
            const d = await apiJson('/zb/api/v1', '/browser_instances/create?profileId=' + encodeURIComponent(id)
                + '&workspaceId=-1&desktopName=&threadToken=', { method: 'POST' });
            return 'Started — PID ' + (d?.processId || '?');
        },
        async stop(id) { await this.del('/browser_instances/' + encodeURIComponent(id) + '?workspaceId=-1'); },
        async stopMany(ids) { await this.del('/browser_instances/delete_bulk?workspaceId=-1', ids); },

        async threads() {
            const data = await this.get('/threads?workspaceId=-1&start=0&total=1000');
            return data?.items || [];
        },
        async freeThread(token) { await this.del('/threads/' + encodeURIComponent(token) + '?workspaceId=-1'); },
        async freeThreads(tokens) { await this.del('/threads/delete_bulk?workspaceId=-1', tokens); },

        // update_proxy_bulk меняет только прокси; без proxyServerId прокси снимается.
        // Проверено на живом ZB: привязка и снятие видны в поле proxy профиля.
        async resolveProxyId(choice) {
            if (!choice.uri) return choice.proxyId || '';
            const uri = choice.uri.trim();
            const found = (await this.proxies()).find(x => x.uri === uri);
            const id = found ? found.id
                : await apiJson('/zb/api/v1', '/proxies/create?workspaceId=-1&name=' + encodeURIComponent(proxyEndpoint(uri) || 'proxy')
                    + '&proxyUri=' + encodeURIComponent(uri), { method: 'POST' });
            if (!id) throw new Error('ZB did not return the new proxy id');
            return id;
        },

        async setProxy(profileId, choice) {
            const proxyId = await this.resolveProxyId(choice);
            const q = '/profiles/update_proxy_bulk?workspaceId=-1' + (proxyId ? '&proxyServerId=' + encodeURIComponent(proxyId) : '');
            await apiJson('/zb/api/v1', q, jsonBody('POST', [profileId]));
        },

        async createProfile(o) {
            const proxyId = await this.resolveProxyId(o.proxy);
            const q = new URLSearchParams({ workspaceId: '-1', name: o.name });
            if (o.folderId) q.set('folderId', o.folderId);
            if (proxyId) q.set('proxyServerId', proxyId);
            if (o.tags) q.set('tags', o.tags);
            if (o.platform) q.set('osVersion', o.platform);
            const id = await apiJson('/zb/api/v1', '/profiles/create?' + q, { method: 'POST' });
            return 'Created ' + o.name + (typeof id === 'string' ? ' · ' + id.slice(0, 8) : '');
        },
    };

    // ── ShardX Launcher: /shardx/api/* → локальный API (Bearer ставит сервер) ──
    // Контракт взят из OpenAPI «ShardX Launcher — Automation API» 0.1.0
    // (docs.proxyshard.com/shardx-launcher-api). Поля читаются терпимо: чего нет — прочерк.
    const shardx = {
        id: 'shardx',
        subtitle: 'ShardX Launcher API',
        cfgKey: 'shardX',
        defaultHost: 'http://127.0.0.1:40325',
        hasTags: false,
        hasThreads: false,
        newProxyHint: 'scheme://user:pass@host:port or host:port:user:pass. ShardX checks it (takes ~10 s) but binds it even if it is dead',
        platforms: [['Windows', 'Windows'], ['macOS', 'macOS'], ['Linux', 'Linux']],
        canNewFolder: true,   // папка в ShardX — просто строка у профиля
        extraStat: { label: 'Folders', sub: 'total' },
        proxyHeaders: ['Name', 'URI', 'Country', 'ID'],

        get(path) { return apiJson('/shardx/api', path); },
        post(path, body) { return apiJson('/shardx/api', path, jsonBody('POST', body || {})); },
        patch(path, body) { return apiJson('/shardx/api', path, jsonBody('PATCH', body || {})); },

        async ping() {
            // /health отвечает без токена, поэтому токен проверяет запрос к /running
            const h = await this.get('/health');
            await this.get('/running');
            return h && typeof h === 'object' && h.version ? 'v' + h.version : '';
        },

        async running() {
            const data = await this.get('/running');
            const map = {};
            (Array.isArray(data) ? data : []).forEach(r => {
                map[r.profile_id] = { pid: r.pid || 0, ws: r.cdp?.web_socket_debugger_url || '' };
            });
            return map;
        },

        async profiles() {
            const [pd, fd, xd] = await Promise.all([
                this.get('/profiles'),
                this.get('/folders').catch(() => []),
                this.get('/proxies').catch(() => []),
            ]);
            const proxyNames = {};
            (Array.isArray(xd) ? xd : []).forEach(x => { proxyNames[x.id] = x.name || (x.host + ':' + x.port); });

            const list = Array.isArray(pd) ? pd : [];
            const names = new Set((Array.isArray(fd) ? fd : []).filter(Boolean));
            list.forEach(p => { if (p.folder) names.add(p.folder); });
            const folders = [...names].sort().map(n => ({ id: n, name: n }));

            const profiles = list.map(p => ({
                id: p.id,
                name: p.name || String(p.id).slice(0, 8),
                folderId: p.folder || '',
                proxyId: p.proxy_id || '',
                proxyName: p.proxy_id ? (proxyNames[p.proxy_id] || String(p.proxy_id).slice(0, 8)) : '',
                tags: [],
                status: p.pinned ? 'pinned' : '',
                lastStart: p.last_launched_at || '',
            }));
            return { profiles, folders };
        },

        async proxies() {
            const data = await this.get('/proxies');
            return (Array.isArray(data) ? data : []).map(p => ({
                id: p.id,
                name: p.name || '—',
                uri: (p.kind ? p.kind + '://' : '') + (p.host || '') + (p.port ? ':' + p.port : '') || '—',
                badge: p.country || '—',
                badgeClass: 'stopped',
                last: p.id || '—',
            }));
        },

        async start(id) {
            const d = await this.post('/profiles/' + encodeURIComponent(id) + '/start', { headless: false });
            return 'Started — PID ' + (d?.pid || '?');
        },
        async stop(id) { await this.post('/profiles/' + encodeURIComponent(id) + '/stop'); },
        async stopMany(ids) {
            const res = await Promise.allSettled(ids.map(id => this.stop(id)));
            const failed = res.filter(r => r.status === 'rejected');
            if (failed.length) throw new Error(failed.length + ' of ' + ids.length + ' failed: ' + failed[0].reason.message);
        },

        // PATCH /profiles/{id}: proxy_id — привязать сохранённый, proxy_id "" — снять,
        // proxy — строка: лаунчер сохраняет её (без дублей), проверяет и привязывает.
        async setProxy(profileId, choice) {
            const body = choice.uri ? { proxy: choice.uri.trim() }
                : choice.proxyId ? { proxy_id: choice.proxyId }
                : { proxy_id: '' };
            await this.patch('/profiles/' + encodeURIComponent(profileId), body);
        },

        // Отпечаток от /fingerprint/new/{platform} кладётся в POST /profiles как есть
        async createProfile(o) {
            const env = await this.get('/fingerprint/new/' + encodeURIComponent(o.platform || 'Windows'));
            const fingerprint = env && typeof env === 'object' && env.fingerprint ? env.fingerprint : env;
            if (!fingerprint || typeof fingerprint !== 'object') throw new Error('ShardX did not return a fingerprint');
            const body = { name: o.name, fingerprint };
            if (o.folderId) body.folder = o.folderId;
            if (o.proxy.uri) body.proxy = o.proxy.uri.trim();
            else if (o.proxy.proxyId) body.proxy_id = o.proxy.proxyId;
            const d = await this.post('/profiles', body);
            return 'Created ' + o.name + (d?.id ? ' · ' + String(d.id).slice(0, 8) : '');
        },
    };

    const PROVIDERS = { zb, shardx };

    // ── State ────────────────────────────────────────────────────────────────────
    const $ = id => document.getElementById(id);
    let P = zb;
    let _connected = false, _gen = 0, _refreshTimer = null;
    let _profiles = [], _folders = [], _running = {}, _uptime = {};
    let _sortField = 'name', _sortDir = 1;

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    let _toastTimer = null;
    function toast(msg, type = '') {
        const el = $('toast');
        el.textContent = msg; el.className = 'show ' + type;
        clearTimeout(_toastTimer); _toastTimer = setTimeout(() => { el.className = ''; }, 2500);
    }

    function copyText(t) { navigator.clipboard?.writeText(t); toast('Copied', 'ok'); }

    function fmtUptime(sec) {
        const h = Math.floor(sec / 3600), m = Math.floor((sec % 3600) / 60), s = sec % 60;
        if (h) return `${h}h ${String(m).padStart(2, '0')}m`;
        if (m) return `${m}m ${String(s).padStart(2, '0')}s`;
        return `${s}s`;
    }

    // ZB отдаёт ISO, ShardX 2.0.3 — "@<unix-секунды>" (проверено на живом ответе)
    function parseTime(v) {
        if (!v) return NaN;
        const m = /^@(\d+)$/.exec(String(v));
        return m ? Number(m[1]) * 1000 : Date.parse(v);
    }

    function fmtDate(v) {
        if (!v) return '—';
        const ms = parseTime(v);
        return isNaN(ms) ? String(v) : new Date(ms).toLocaleString();
    }

    // ── Provider switch ──────────────────────────────────────────────────────────
    function loadChoice() {
        try { const v = localStorage.getItem(LS_KEY); if (PROVIDERS[v]) return v; } catch (_) {}
        return 'zb';
    }
    function saveChoice(id) { try { localStorage.setItem(LS_KEY, id); } catch (_) {} }

    function selectProvider(id) {
        P = PROVIDERS[id] || zb;
        saveChoice(P.id);
        _gen++;
        _connected = false;
        clearInterval(_refreshTimer);
        _profiles = []; _folders = []; _running = {}; _uptime = {};

        document.querySelectorAll('.provider-btn').forEach(b => b.classList.toggle('active', b.dataset.provider === P.id));
        $('providerSubtitle').textContent = P.subtitle;
        document.body.classList.toggle('no-tags', !P.hasTags);
        _proxyBusy = false; closeProxyModal();
        _npBusy = false; closeNewProfileModal();
        $('statExtraLabel').textContent = P.extraStat.label;
        $('statExtraSub').textContent = P.extraStat.sub;
        $('proxiesHead').innerHTML = P.proxyHeaders.map(h => `<th>${esc(h)}</th>`).join('');
        $('tabBtnThreads').hidden = !P.hasThreads;
        if (!P.hasThreads && $('tab-threads').classList.contains('active')) showTab('profiles');

        ['statRunning', 'statProfiles', 'statProxies', 'statExtra'].forEach(s => { $(s).textContent = '—'; });
        ['profilesCount', 'proxiesCount', 'threadsCount'].forEach(s => { $(s).textContent = '0'; });
        $('profileFolderFilter').innerHTML = '<option value="">All folders</option>';
        $('profilesTbody').innerHTML = '<tr><td colspan="7" class="empty">Not connected</td></tr>';
        $('proxiesTbody').innerHTML = '<tr><td colspan="4" class="empty">Not connected</td></tr>';
        $('threadsTbody').innerHTML = '<tr><td colspan="4" class="empty">Not connected</td></tr>';

        connect();
    }

    // ── Connect ──────────────────────────────────────────────────────────────────
    async function showConfig() {
        try {
            const cfg = await fetch('/config').then(r => r.json());
            const ba = cfg?.browsersApi || cfg?.BrowsersApi || {};
            const key = P.cfgKey, keyUp = key[0].toUpperCase() + key.slice(1);
            const c = ba[key] || ba[keyUp] || {};
            const host = c.host || c.Host || P.defaultHost;
            const token = c.token || c.Token || '';
            $('hostLabel').textContent = host;
            $('keyStatus').textContent = token ? '●●●●' : 'not set';
            $('keyStatus').style.color = token ? 'var(--text-dim)' : 'var(--red)';
        } catch (_) {
            $('hostLabel').textContent = P.defaultHost;
            $('keyStatus').textContent = '?';
        }
    }

    function setVersion(v) {
        $('versionLabel').textContent = v || '';
        $('versionLabel').hidden = !v;
        $('versionSep').hidden = !v;
    }

    async function connect() {
        const gen = _gen;
        const dot = $('statusDot'), btn = $('btnConnect');
        btn.disabled = true; dot.className = 'dot'; setVersion('');
        await showConfig();
        try {
            const version = await P.ping();
            if (gen !== _gen) return;
            _connected = true; dot.className = 'dot ok';
            setVersion(version);
            toast('Connected', 'ok');
            await refreshAll();
            clearInterval(_refreshTimer);
            _refreshTimer = setInterval(() => { if (_connected && gen === _gen) loadRunning(); }, 10000);
        } catch (e) {
            if (gen !== _gen) return;
            _connected = false; dot.className = 'dot err';
            toast('Failed: ' + e.message, 'err');
        } finally {
            if (gen === _gen) btn.disabled = false;
        }
    }

    async function refreshAll() {
        const jobs = [loadRunning(), loadProfiles(), loadProxies()];
        if (P.hasThreads) jobs.push(loadThreads());
        await Promise.allSettled(jobs);
    }

    // ── Running instances ────────────────────────────────────────────────────────
    async function loadRunning() {
        if (!_connected) return;
        const gen = _gen;
        try {
            const map = await P.running();
            if (gen !== _gen) return;
            _running = map;
            $('statRunning').textContent = Object.keys(map).length;

            _uptime = {};
            const pids = Object.values(map).map(i => i.pid).filter(Boolean);
            if (pids.length) {
                try {
                    const up = await fetch('/zb/process/uptime?pids=' + pids.join(',')).then(r => r.json());
                    if (gen !== _gen) return;
                    pids.forEach(pid => { const d = up[String(pid)]; _uptime[pid] = d ? d.uptimeSeconds : null; });
                } catch (_) {}
            }
            renderProfiles();
        } catch (e) { console.error('loadRunning:', e); }
    }

    // ShardX отвечает на stop сразу, а из /running профиль уходит секунд через 7
    // (замерено на 2.0.3). Список перечитывается, пока остановленные не пропадут.
    async function reloadRunningAfterStop(ids) {
        const gen = _gen;
        const until = Date.now() + 15000;
        await loadRunning();
        while (gen === _gen && Date.now() < until && ids.some(id => _running[id])) {
            await new Promise(r => setTimeout(r, 1500));
            if (gen === _gen) await loadRunning();
        }
    }

    async function stopProfile(id, btn) {
        btn.disabled = true;
        try { await P.stop(id); toast('Stopped', 'ok'); await reloadRunningAfterStop([id]); }
        catch (e) { toast('Stop failed: ' + e.message, 'err'); btn.disabled = false; }
    }

    async function killProcess(pid, btn) {
        if (!await Dialog.confirm(`Force kill PID ${pid}?\nThe browser API is not used — a direct Process.Kill.`)) return;
        btn.disabled = true;
        try {
            const r = await fetch('/zb/process/kill', jsonBody('POST', { pid }));
            if (!r.ok) throw new Error(r.status + ' ' + r.statusText);
            const res = await r.json();
            toast(res.killed ? `Killed PID ${pid}` : `PID ${pid} already dead`, 'ok');
            await loadRunning();
        } catch (e) { toast('Kill failed: ' + e.message, 'err'); btn.disabled = false; }
    }

    async function startProfile(id, btn) {
        btn.disabled = true;
        try { toast(await P.start(id), 'ok'); await loadRunning(); }
        catch (e) { toast('Error: ' + e.message, 'err'); }
        finally { btn.disabled = false; }
    }

    // ── Profiles ─────────────────────────────────────────────────────────────────
    async function loadProfiles() {
        if (!_connected) return;
        const gen = _gen;
        const tbody = $('profilesTbody');
        tbody.innerHTML = '<tr><td colspan="7" class="loading"><span class="spin"></span>Loading...</td></tr>';
        try {
            const { profiles, folders } = await P.profiles();
            if (gen !== _gen) return;
            _profiles = profiles; _folders = folders;
            const sel = $('profileFolderFilter');
            const cur = sel.value;
            sel.innerHTML = '<option value="">All folders</option>'
                + folders.map(f => `<option value="${esc(f.id)}">${esc(f.name)}</option>`).join('');
            sel.value = folders.some(f => String(f.id) === cur) ? cur : '';
            $('statProfiles').textContent = profiles.length;
            if (!P.hasThreads) $('statExtra').textContent = folders.length;
            renderProfiles();
        } catch (e) {
            if (gen !== _gen) return;
            tbody.innerHTML = `<tr><td colspan="7" class="empty">Error: ${esc(e.message)}</td></tr>`;
        }
    }

    function folderName(p) {
        if (!p.folderId) return '—';
        const f = _folders.find(x => String(x.id) === String(p.folderId));
        return f ? f.name : String(p.folderId).slice(0, 8);
    }

    function renderProfiles() {
        const tbody = $('profilesTbody');
        if (!_connected) return;
        const search = $('profileSearch').value.toLowerCase();
        const folderId = $('profileFolderFilter').value;
        const runningOnly = $('profileRunningOnly').checked;

        let items = _profiles;
        if (search) items = items.filter(p => (p.name + ' ' + p.proxyName + ' ' + p.tags.join(' ')).toLowerCase().includes(search));
        if (folderId) items = items.filter(p => String(p.folderId) === folderId);
        if (runningOnly) items = items.filter(p => !!_running[p.id]);

        const cmp = (a, b) => String(a).localeCompare(String(b), undefined, { numeric: true, sensitivity: 'base' });
        items = [...items].sort((a, b) => {
            switch (_sortField) {
                case 'folder':    return _sortDir * cmp(folderName(a), folderName(b));
                case 'proxy':     return _sortDir * cmp(a.proxyName, b.proxyName);
                case 'status':    return _sortDir * cmp(a.status.toLowerCase(), b.status.toLowerCase());
                case 'lastStart': return _sortDir * ((parseTime(a.lastStart) || 0) - (parseTime(b.lastStart) || 0));
                default:          return _sortDir * cmp(a.name, b.name);
            }
        });

        document.querySelectorAll('#tab-profiles th[data-sort]').forEach(th => {
            th.classList.remove('sort-asc', 'sort-desc');
            if (th.dataset.sort === _sortField) th.classList.add(_sortDir === 1 ? 'sort-asc' : 'sort-desc');
        });

        $('profilesCount').textContent = items.length;
        if (!items.length) { tbody.innerHTML = '<tr><td colspan="7" class="empty">No profiles found</td></tr>'; return; }

        tbody.innerHTML = items.map(p => {
            const inst = _running[p.id];
            const id = esc(p.id);
            const pid = inst?.pid || 0;
            const tags = p.tags.map(t => `<span class="tag">${esc(t)}</span>`).join('') || '—';
            const actions = inst
                ? `<button class="btn sm red" data-act="stop" data-id="${id}">Stop</button>
                   ${pid ? `<button class="btn sm orange" data-act="kill" data-pid="${pid}">Kill</button>` : ''}
                   ${inst.ws ? `<button class="btn sm" data-act="copy" data-text="${esc(inst.ws)}" title="${esc(inst.ws)}">WS</button>` : ''}
                   <button class="btn sm" data-act="copy" data-text="${id}">ID</button>`
                : `<button class="btn sm green" data-act="start" data-id="${id}">▶ Start</button>
                   <button class="btn sm" data-act="copy" data-text="${id}">ID</button>`;
            const up = pid ? _uptime[pid] : null;
            const mid = inst
                ? `<span class="uptime">PID ${pid || '—'} · ${up != null ? fmtUptime(up) : '—'}</span>`
                : esc(fmtDate(p.lastStart));
            const status = inst
                ? '<span class="badge running">running</span>'
                : p.status ? `<span class="badge unknown">${esc(p.status)}</span>` : '<span class="badge stopped">—</span>';
            return `<tr${inst ? ' class="is-running"' : ''}>
                <td class="name-cell" title="${id}">${esc(p.name)}</td>
                <td class="mono">${esc(folderName(p))}</td>
                <td class="proxy-cell"><span class="proxy-name" title="${esc(p.proxyName)}">${esc(p.proxyName || '—')}</span><button class="btn sm" data-act="proxy" data-id="${id}" title="Set proxy">✎</button></td>
                <td class="col-tags">${tags}</td>
                <td>${status}</td>
                <td class="mono">${mid}</td>
                <td style="min-width:160px"><div class="btn-row">${actions}</div></td>
            </tr>`;
        }).join('');
    }

    // ── Proxies ──────────────────────────────────────────────────────────────────
    async function loadProxies() {
        if (!_connected) return;
        const gen = _gen;
        const tbody = $('proxiesTbody');
        tbody.innerHTML = '<tr><td colspan="4" class="loading"><span class="spin"></span>Loading...</td></tr>';
        try {
            const items = await P.proxies();
            if (gen !== _gen) return;
            $('statProxies').textContent = items.length;
            $('proxiesCount').textContent = items.length;
            if (!items.length) { tbody.innerHTML = '<tr><td colspan="4" class="empty">No proxies</td></tr>'; return; }
            tbody.innerHTML = items.map(p => {
                const uri = p.uri.length > 52 ? p.uri.slice(0, 52) + '…' : p.uri;
                return `<tr><td class="name-cell">${esc(p.name)}</td>
                    <td class="mono" title="${esc(p.uri)}">${esc(uri)}</td>
                    <td><span class="badge ${p.badgeClass}">${esc(p.badge)}</span></td>
                    <td class="mono">${esc(p.last)}</td></tr>`;
            }).join('');
        } catch (e) {
            if (gen !== _gen) return;
            tbody.innerHTML = `<tr><td colspan="4" class="empty">Error: ${esc(e.message)}</td></tr>`;
        }
    }

    // ── Threads (ZennoBrowser) ───────────────────────────────────────────────────
    async function loadThreads() {
        if (!_connected || !P.hasThreads) return;
        const gen = _gen;
        const tbody = $('threadsTbody');
        tbody.innerHTML = '<tr><td colspan="4" class="loading"><span class="spin"></span>Loading...</td></tr>';
        try {
            const items = await P.threads();
            if (gen !== _gen) return;
            $('statExtra').textContent = items.length;
            $('threadsCount').textContent = items.length;
            if (!items.length) { tbody.innerHTML = '<tr><td colspan="4" class="empty">No threads</td></tr>'; return; }
            tbody.innerHTML = items.map(t => {
                const token = esc(t.threadToken || '');
                return `<tr data-token="${token}">
                    <td class="token-cell" title="${token}">${token || '—'}</td>
                    <td>${esc(t.type || '—')}</td><td class="mono">${esc(fmtDate(t.createdAt))}</td>
                    <td><button class="btn sm red" data-act="free" data-token="${token}">Free</button></td>
                </tr>`;
            }).join('');
        } catch (e) {
            if (gen !== _gen) return;
            $('statExtra').textContent = '?';
            $('threadsCount').textContent = '?';
            tbody.innerHTML = `<tr><td colspan="4" class="empty">Error: ${esc(e.message)}</td></tr>`;
        }
    }

    async function freeThread(token, btn) {
        btn.disabled = true;
        try { await P.freeThread(token); toast('Thread freed', 'ok'); await loadThreads(); }
        catch (e) { toast('Error: ' + e.message, 'err'); btn.disabled = false; }
    }

    // ── Proxy picker ─────────────────────────────────────────────────────────────
    const NEW_PROXY = '__new__', NO_PROXY = '';
    const BASE_PROXY_OPTIONS = `<option value="${NO_PROXY}">— No proxy —</option><option value="${NEW_PROXY}">+ New proxy…</option>`;
    let _proxyTarget = null, _proxyBusy = false;

    function proxyModalError(msg) {
        $('proxyModalError').textContent = msg || '';
        $('proxyModalError').hidden = !msg;
    }

    function syncProxyNew() {
        const isNew = $('proxySelect').value === NEW_PROXY;
        $('proxyNewWrap').hidden = !isNew;
        if (isNew) setTimeout(() => $('proxyNewInput').focus(), 0);
    }

    async function loadProxyOptions() {
        const list = await P.proxies();
        list.sort((a, b) => String(a.name).localeCompare(String(b.name), undefined, { numeric: true, sensitivity: 'base' }));
        const html = BASE_PROXY_OPTIONS + list.map(x => {
            const ep = proxyEndpoint(x.uri);
            const label = x.name + (ep && ep !== x.name ? '  (' + ep + ')' : '');
            return `<option value="${esc(x.id)}">${esc(label)}</option>`;
        }).join('');
        return { list, html };
    }

    function proxyChoice(selectValue, newValue) {
        if (selectValue === NEW_PROXY) return newValue.trim() ? { uri: newValue.trim() } : null;
        if (selectValue === NO_PROXY) return { clear: true };
        return { proxyId: selectValue };
    }

    async function openProxyModal(profileId) {
        if (!_connected) return;
        const prof = _profiles.find(p => String(p.id) === String(profileId));
        if (!prof) return;
        _proxyTarget = prof;
        const gen = _gen;
        $('proxyModalProfile').textContent = prof.name + (prof.proxyName ? ' · now: ' + prof.proxyName : ' · no proxy');
        $('proxyNewInput').value = '';
        $('proxyNewInput').placeholder = P.id === 'zb' ? 'socks5://login:pass@host:port' : 'socks5://user:pass@host:port';
        $('proxyNewHint').textContent = P.newProxyHint;
        proxyModalError('');
        const sel = $('proxySelect');
        sel.innerHTML = '<option>Loading...</option>';
        sel.disabled = true;
        $('proxyApply').disabled = true;
        $('proxyModal').classList.add('open');

        let list = [], html = BASE_PROXY_OPTIONS;
        try { ({ list, html } = await loadProxyOptions()); }
        catch (e) { if (gen === _gen) proxyModalError('Proxy list: ' + e.message); }
        if (gen !== _gen || _proxyTarget !== prof) return;

        sel.innerHTML = html;
        sel.value = prof.proxyId && list.some(x => String(x.id) === String(prof.proxyId)) ? prof.proxyId : NO_PROXY;
        sel.disabled = false;
        $('proxyApply').disabled = false;
        syncProxyNew();
    }

    function closeProxyModal() {
        if (_proxyBusy) return;
        $('proxyModal').classList.remove('open');
        _proxyTarget = null;
    }

    async function applyProxy() {
        const prof = _proxyTarget;
        if (!prof || _proxyBusy) return;
        const choice = proxyChoice($('proxySelect').value, $('proxyNewInput').value);
        if (!choice) { proxyModalError('Enter a proxy string'); return; }

        _proxyBusy = true;
        $('proxyApply').disabled = true;
        proxyModalError('');
        try {
            await P.setProxy(prof.id, choice);
            _proxyBusy = false;
            closeProxyModal();
            toast(choice.clear ? 'Proxy removed' : 'Proxy set', 'ok');
            await Promise.allSettled([loadProfiles(), loadProxies()]);
        } catch (e) {
            proxyModalError(e.message);
        } finally {
            _proxyBusy = false;
            $('proxyApply').disabled = false;
        }
    }

    $('proxySelect').addEventListener('change', syncProxyNew);
    $('proxyCancel').addEventListener('click', closeProxyModal);
    $('proxyApply').addEventListener('click', applyProxy);
    $('proxyNewInput').addEventListener('keydown', e => { if (e.key === 'Enter') applyProxy(); });
    $('proxyModal').addEventListener('click', e => { if (e.target.id === 'proxyModal') closeProxyModal(); });
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && $('proxyModal').classList.contains('open')) closeProxyModal(); });

    // ── New profile ──────────────────────────────────────────────────────────────
    const NEW_FOLDER = '__new__';
    let _npOpen = false, _npBusy = false;

    function npError(msg) {
        $('npError').textContent = msg || '';
        $('npError').hidden = !msg;
    }

    function syncNpFields() {
        const newProxy = $('npProxy').value === NEW_PROXY;
        $('npProxyNewWrap').hidden = !newProxy;
        $('npFolderNewWrap').hidden = $('npFolder').value !== NEW_FOLDER;
    }

    async function openNewProfileModal() {
        if (!_connected) return;
        const gen = _gen;
        _npOpen = true;
        npError('');
        $('npName').value = '';
        $('npTags').value = '';
        $('npFolderNew').value = '';
        $('npProxyNew').value = '';
        $('npProxyNew').placeholder = P.id === 'zb' ? 'socks5://login:pass@host:port' : 'socks5://user:pass@host:port';
        $('npProxyHint').textContent = P.newProxyHint;
        $('npTagsWrap').hidden = !P.hasTags;
        $('npPlatform').innerHTML = P.platforms.map(([v, l]) => `<option value="${esc(v)}">${esc(l)}</option>`).join('');

        const curFolder = $('profileFolderFilter').value;
        $('npFolder').innerHTML = '<option value="">— No folder —</option>'
            + (P.canNewFolder ? `<option value="${NEW_FOLDER}">+ New folder…</option>` : '')
            + _folders.filter(f => !f.virtual).map(f => `<option value="${esc(f.id)}">${esc(f.name)}</option>`).join('');
        $('npFolder').value = _folders.some(f => !f.virtual && String(f.id) === curFolder) ? curFolder : '';

        const sel = $('npProxy');
        sel.innerHTML = '<option>Loading...</option>';
        sel.disabled = true;
        $('npCreate').disabled = true;
        syncNpFields();
        $('newProfileModal').classList.add('open');
        setTimeout(() => $('npName').focus(), 0);

        let html = BASE_PROXY_OPTIONS;
        try { ({ html } = await loadProxyOptions()); }
        catch (e) { if (gen === _gen) npError('Proxy list: ' + e.message); }
        if (gen !== _gen || !_npOpen) return;
        sel.innerHTML = html;
        sel.value = NO_PROXY;
        sel.disabled = false;
        $('npCreate').disabled = false;
        syncNpFields();
    }

    function closeNewProfileModal() {
        if (_npBusy) return;
        _npOpen = false;
        $('newProfileModal').classList.remove('open');
    }

    async function createProfile() {
        if (!_npOpen || _npBusy) return;
        const name = $('npName').value.trim();
        if (!name) { npError('Enter a profile name'); $('npName').focus(); return; }
        const proxy = proxyChoice($('npProxy').value, $('npProxyNew').value);
        if (!proxy) { npError('Enter a proxy string'); return; }
        let folderId = $('npFolder').value;
        if (folderId === NEW_FOLDER) {
            folderId = $('npFolderNew').value.trim();
            if (!folderId) { npError('Enter a folder name'); return; }
        }

        _npBusy = true;
        $('npCreate').disabled = true;
        npError('');
        try {
            const msg = await P.createProfile({
                name, folderId, proxy,
                platform: $('npPlatform').value,
                tags: P.hasTags ? $('npTags').value.trim() : '',
            });
            _npBusy = false;
            closeNewProfileModal();
            toast(msg, 'ok');
            await Promise.allSettled([loadProfiles(), loadProxies()]);
        } catch (e) {
            npError(e.message);
        } finally {
            _npBusy = false;
            $('npCreate').disabled = false;
        }
    }

    $('btnNewProfile').addEventListener('click', openNewProfileModal);
    $('npProxy').addEventListener('change', () => { syncNpFields(); if ($('npProxy').value === NEW_PROXY) $('npProxyNew').focus(); });
    $('npFolder').addEventListener('change', () => { syncNpFields(); if ($('npFolder').value === NEW_FOLDER) $('npFolderNew').focus(); });
    $('npCancel').addEventListener('click', closeNewProfileModal);
    $('npCreate').addEventListener('click', createProfile);
    ['npName', 'npFolderNew', 'npProxyNew', 'npTags'].forEach(id =>
        $(id).addEventListener('keydown', e => { if (e.key === 'Enter') createProfile(); }));
    $('newProfileModal').addEventListener('click', e => { if (e.target.id === 'newProfileModal') closeNewProfileModal(); });
    document.addEventListener('keydown', e => { if (e.key === 'Escape' && _npOpen) closeNewProfileModal(); });

    // ── Tabs ─────────────────────────────────────────────────────────────────────
    function showTab(name) {
        document.querySelectorAll('.tab-btn').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
        document.querySelectorAll('.tab-panel').forEach(p => p.classList.toggle('active', p.id === 'tab-' + name));
    }

    // ── Events ───────────────────────────────────────────────────────────────────
    document.querySelectorAll('.tab-btn').forEach(b => b.addEventListener('click', () => showTab(b.dataset.tab)));
    document.querySelectorAll('.provider-btn').forEach(b => b.addEventListener('click', () => {
        if (b.dataset.provider !== P.id || !_connected) selectProvider(b.dataset.provider);
    }));

    $('btnConnect').addEventListener('click', connect);
    $('btnRefreshProfiles').addEventListener('click', () => Promise.allSettled([loadRunning(), loadProfiles()]));
    $('btnRefreshProxies').addEventListener('click', loadProxies);
    $('btnRefreshThreads').addEventListener('click', loadThreads);
    $('profileSearch').addEventListener('input', renderProfiles);
    $('profileFolderFilter').addEventListener('change', renderProfiles);
    $('profileRunningOnly').addEventListener('change', renderProfiles);

    document.querySelector('#tab-profiles thead').addEventListener('click', e => {
        const th = e.target.closest('th[data-sort]');
        if (!th) return;
        if (_sortField === th.dataset.sort) _sortDir *= -1;
        else { _sortField = th.dataset.sort; _sortDir = 1; }
        renderProfiles();
    });

    document.addEventListener('click', e => {
        const btn = e.target.closest('button[data-act]');
        if (!btn) return;
        const d = btn.dataset;
        if (d.act === 'start') startProfile(d.id, btn);
        else if (d.act === 'stop') stopProfile(d.id, btn);
        else if (d.act === 'kill') killProcess(Number(d.pid), btn);
        else if (d.act === 'copy') copyText(d.text);
        else if (d.act === 'free') freeThread(d.token, btn);
        else if (d.act === 'proxy') openProxyModal(d.id);
    });

    $('btnStopAll').addEventListener('click', async () => {
        if (!_connected) return;
        const ids = Object.keys(_running);
        if (!ids.length) { toast('No running instances'); return; }
        if (!await Dialog.confirm(`Stop ${ids.length} instance(s)?`)) return;
        try { await P.stopMany(ids); toast(`Stopped ${ids.length}`, 'ok'); }
        catch (e) { toast('Error: ' + e.message, 'err'); }
        await reloadRunningAfterStop(ids);
    });

    $('btnFreeAllThreads').addEventListener('click', async () => {
        if (!_connected || !P.hasThreads) return;
        const tokens = [...document.querySelectorAll('#threadsTbody tr[data-token]')].map(r => r.dataset.token).filter(Boolean);
        if (!tokens.length) { toast('No threads'); return; }
        if (!await Dialog.confirm(`Free ${tokens.length} thread(s)?`)) return;
        try { await P.freeThreads(tokens); toast(`Freed ${tokens.length}`, 'ok'); await loadThreads(); }
        catch (e) { toast('Error: ' + e.message, 'err'); }
    });

    selectProvider(loadChoice());
})();
