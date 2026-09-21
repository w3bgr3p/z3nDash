    // ── Toast ─────────────────────────────────────────────────────────
    let _toastTimer;
    function toast(msg, type = 'ok') {
        const el = document.getElementById('toast');
        el.textContent = msg;
        el.className = 'show ' + type;
        clearTimeout(_toastTimer);
        _toastTimer = setTimeout(() => { el.className = ''; }, 2800);
    }

    // ── Uptime counter ────────────────────────────────────────────────
    let _startTime = null;
    function fmtUptime(secs) {
        const h = Math.floor(secs / 3600), m = Math.floor((secs % 3600) / 60), s = secs % 60;
        if (h > 0) return `${h}h ${m}m`;
        if (m > 0) return `${m}m ${s}s`;
        return `${s}s`;
    }
    function tickUptime() {
        if (!_startTime) return;
        const elapsed = Math.floor((Date.now() - _startTime) / 1000);
        document.getElementById('uptimeVal').textContent = fmtUptime(elapsed);
        document.getElementById('uptimeBadge').textContent = fmtUptime(elapsed);
    }

    // ── Format bytes ──────────────────────────────────────────────────
    function fmtBytes(bytes) {
        if (bytes === 0) return '0 B';
        const k = 1024, sizes = ['B','KB','MB','GB'];
        const i = Math.floor(Math.log(bytes) / Math.log(k));
        return (bytes / Math.pow(k, i)).toFixed(i > 1 ? 1 : 0) + ' ' + sizes[i];
    }

    // ── Load Status ───────────────────────────────────────────────────
    async function loadStatus() {
        try {
            const r = await fetch('/config/status');
            if (!r.ok) return;
            const d = await r.json();

            // uptime
            if (d.startedAt) {
                _startTime = new Date(d.startedAt).getTime();
                tickUptime();
            }

            setText('sPort', d.dashboardPort ?? '—');
            setText('sDbMode', d.dbMode ?? '—');

            const host = d.dbMode === 'SQLite'
                ? (d.sqlitePath ?? '—')
                : `${d.pgHost ?? ''}:${d.pgPort ?? ''}`;
            setText('sDbHost', host);

            setText('sLogsFolder', d.logsFolder || '—');
            setText('sReportsFolder', d.reportsFolder || '—');
            setText('sTempFolder', d.tempFolder || '—');
            setText('sMaxFile', d.maxFileSizeMb ? d.maxFileSizeMb + ' MB' : '—');

            // Ports chips
            const portsEl = document.getElementById('sPorts');
            if (d.listeningPorts && d.listeningPorts.length) {
                portsEl.innerHTML = d.listeningPorts.map(p =>
                    `<span class="port-chip">${p}</span>`
                ).join(' ');
            }

        } catch (e) {
            console.error('Status load failed', e);
        }
    }

    function setText(id, val) {
        const el = document.getElementById(id);
        if (el) el.textContent = val;
    }

    // ── Load Storage ──────────────────────────────────────────────────
    async function loadStorage() {
        const btn = document.getElementById('refreshStorageBtn');
        btn.innerHTML = '<span class="spinner"></span>…';
        btn.disabled = true;
        try {
            const r = await fetch('/config/storage');
            if (!r.ok) { toast('Storage fetch failed', 'err'); return; }
            const d = await r.json();

            document.getElementById('storageTotalSize').textContent = fmtBytes(d.totalBytes ?? 0);
            document.getElementById('storageDetail').textContent =
                `${d.fileCount ?? 0} files · ${fmtBytes(d.totalBytes ?? 0)} total`;
            document.getElementById('storageFileCount').textContent = `${d.fileCount ?? 0} files`;

            const maxBytes = (d.maxFileSizeMbHint ?? 500) * 1024 * 1024;
            const pct = Math.min(100, Math.round((d.totalBytes ?? 0) / maxBytes * 100));
            const bar = document.getElementById('storageBar');
            bar.style.width = pct + '%';
            bar.style.background = pct > 80 ? 'var(--red)' : pct > 50 ? 'var(--yellow)' : 'var(--accent)';
            document.getElementById('storageMaxHint').textContent = pct + '% of soft limit';

            const list = document.getElementById('fileList');
            if (d.files && d.files.length) {
                list.innerHTML = d.files.map(f => `
                <div class="file-item">
                    <span class="fname">${f.name}</span>
                    <span class="fsize">${fmtBytes(f.size)}</span>
                </div>
            `).join('');
            } else {
                list.innerHTML = '<div class="file-item"><span class="fname" style="color:var(--text-dim)">No log files</span></div>';
            }
        } catch (e) {
            toast('Storage error: ' + e.message, 'err');
        } finally {
            btn.innerHTML = '↻ Refresh';
            btn.disabled = false;
        }
    }

    // ── Clear Logs ────────────────────────────────────────────────────
    const CLEAR_MAP = {
        all:     { url: '/clear-all-logs', label: 'ALL logs' },
    };

    async function clearLogs(type) {
        const { url, label } = CLEAR_MAP[type];
        if (!await Dialog.confirm(`Clear ${label}? This cannot be undone.`)) return;

        const btnId = { all: 'clearAllBtn' }[type];
        const btn = document.getElementById(btnId);
        btn.disabled = true;
        btn.textContent = '…';

        try {
            const r = await fetch(url, { method: 'POST' });
            if (r.ok) {
                toast(`✓ ${label} cleared`, 'ok');
                await loadStorage();
            } else {
                toast(`Failed to clear ${label}`, 'err');
            }
        } catch (e) {
            toast('Error: ' + e.message, 'err');
        } finally {
            btn.disabled = false;
            btn.textContent = '☠ Clear ALL Logs';
        }
    }

    // ── Load Config ───────────────────────────────────────────────────
    async function loadConfig() {
        try {
            const r = await fetch('/config');
            if (!r.ok) { toast('Config load failed', 'err'); return; }
            const d = await r.json();

            const dbConfig   = d.dbConfig   ?? d.DbConfig   ?? {};
            const logsConfig = d.logsConfig ?? d.LogsConfig ?? {};
            const apiConfig  = d.apiConfig  ?? d.ApiConfig  ?? {};
            const browsersApi = d.browsersApi ?? d.BrowsersApi ?? {};
            const zennoBrowser = browsersApi.zennoBrowser ?? browsersApi.ZennoBrowser ?? {};
            const shardX = browsersApi.shardX ?? browsersApi.ShardX ?? {};

            setVal('cfgDbType',        (dbConfig.type ?? dbConfig.Type ?? 'sqlite').toLowerCase());
            setVal('cfgSqlitePath',    dbConfig.sqlitePath ?? dbConfig.SqlitePath ?? '');
            setVal('cfgPgConnectionString', dbConfig.postgresConnectionString ?? dbConfig.PostgresConnectionString ?? '');
            setVal('cfgDashboardPort', logsConfig.dashboardPort ?? logsConfig.DashboardPort ?? '');
            setVal('cfgLogsFolder',    logsConfig.logsFolder ?? logsConfig.LogsFolder ?? '');
            setVal('cfgTempFolder',    logsConfig.tempFolder ?? logsConfig.TempFolder ?? '');
            setVal('cfgReportsFolder', logsConfig.reportsFolder ?? logsConfig.ReportsFolder ?? '');
            setVal('cfgMaxFileSizeMb', logsConfig.maxFileSizeMb ?? logsConfig.MaxFileSizeMb ?? '');
            setVal('cfgZennoBrowserHost', zennoBrowser.host ?? zennoBrowser.Host ?? apiConfig.zbHost ?? apiConfig.ZbHost ?? 'http://localhost:8160');
            setVal('cfgZennoBrowserToken', zennoBrowser.token ?? zennoBrowser.Token ?? apiConfig.zb ?? apiConfig.ZB ?? '');
            setVal('cfgShardXHost', shardX.host ?? shardX.Host ?? 'http://127.0.0.1:40325');
            setVal('cfgShardXToken', shardX.token ?? shardX.Token ?? '');
            setVal('cfgJVarsPath',     d.securityConfig?.jVarsPath ?? '');

            // AI
            const aiHost = d.aiConfig?.omniRouteHost ?? d.AiConfig?.omniRouteHost ?? 'http://localhost:20128';
            setVal('cfgOmniRouteHost', aiHost);
            document.getElementById('aiProviderStatus').textContent = '● OmniRoute';

            togglePgFields();
        } catch(e) {
            toast('Config error: ' + e.message, 'err');
        }
    }

    function setVal(id, val) {
        const el = document.getElementById(id);
        if (el) el.value = val ?? '';
    }

    function togglePgFields() {
        const isPg = document.getElementById('cfgDbType').value === 'postgres';
        document.getElementById('pgFields').style.opacity = isPg ? '1' : '0.35';
        document.querySelectorAll('#pgFields input, #pgFields button').forEach(i => i.disabled = !isPg);
    }
    document.getElementById('cfgDbType').addEventListener('change', togglePgFields);

    function togglePgConnectionStringVisibility() {
        const input = document.getElementById('cfgPgConnectionString');
        const button = document.getElementById('btnTogglePgConnectionString');
        const show = input.type === 'password';
        input.type = show ? 'text' : 'password';
        button.textContent = show ? 'Hide' : 'Show';
    }

    async function copyPgConnectionString() {
        const value = document.getElementById('cfgPgConnectionString').value;
        if (!value) {
            toast('Connection string is empty', 'err');
            return;
        }

        try {
            await navigator.clipboard.writeText(value);
        } catch {
            const input = document.getElementById('cfgPgConnectionString');
            input.select();
            document.execCommand('copy');
            input.setSelectionRange(0, 0);
        }
        toast('✓ Connection string copied', 'ok');
    }

    // ── AI Provider ───────────────────────────────────────────────────
    async function saveAiConfig() {
        const omniRouteHost = document.getElementById('cfgOmniRouteHost').value || 'http://localhost:20128';
        const statusEl      = document.getElementById('aiProviderStatus');
        statusEl.textContent = '…';
        try {
            const r = await fetch('/config/ai-validate', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ omniRouteHost })
            });
            const d = await r.json();
            if (d.ok) {
                statusEl.textContent = '● OmniRoute';
                toast('✓ OmniRoute saved', 'ok');
            } else {
                statusEl.textContent = '✕ error';
                toast('AI save error: ' + (d.error ?? 'unknown'), 'err');
            }
        } catch(e) {
            statusEl.textContent = '✕ error';
            toast('AI save error: ' + e.message, 'err');
        }
    }

    // ── Save Config ───────────────────────────────────────────────────
    async function saveConfig() {
        const payload = {
            dbConfig: {
                type:             document.getElementById('cfgDbType').value,
                sqlitePath:       document.getElementById('cfgSqlitePath').value,
                postgresConnectionString: document.getElementById('cfgPgConnectionString').value,
            },
            logsConfig: {
                dashboardPort:  document.getElementById('cfgDashboardPort').value,
                logsFolder:     document.getElementById('cfgLogsFolder').value,
                tempFolder:     document.getElementById('cfgTempFolder').value,
                reportsFolder:  document.getElementById('cfgReportsFolder').value,
                maxFileSizeMb:  parseInt(document.getElementById('cfgMaxFileSizeMb').value) || 0,
            },
            browsersApi: {
                zennoBrowser: {
                    host: document.getElementById('cfgZennoBrowserHost').value,
                    token: document.getElementById('cfgZennoBrowserToken').value,
                },
                shardX: {
                    host: document.getElementById('cfgShardXHost').value,
                    token: document.getElementById('cfgShardXToken').value,
                },
            }
        };

        try {
            const r = await fetch('/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            if (r.ok) {
                toast('✓ Config saved', 'ok');
                await loadStatus();
            } else {
                const t = await r.text();
                toast('Save failed: ' + t, 'err');
            }
        } catch(e) {
            toast('Error: ' + e.message, 'err');
        }
    }

    // ── jVars rows editor ─────────────────────────────────────────────
    function jvEnsureSeed() {} // cfgPin берётся из Wallet PIN, не из таблицы

    function jvAddRow(key, value, isPassword) {
        const container = document.getElementById('jvarsRows');
        const row = document.createElement('div');
        row.style.cssText = 'display:grid; grid-template-columns:1fr 1fr auto; gap:6px; align-items:center;';

        const kInp = document.createElement('input');
        kInp.type        = 'text';
        kInp.placeholder = 'key';
        kInp.value       = key ?? '';
        kInp.className   = 'jv-key';
        kInp.style.cssText = 'background:var(--surface2); border:1px solid var(--border); border-radius:5px; color:var(--text-hi); font-family:"JetBrains Mono",monospace; font-size:11px; padding:6px 9px; outline:none; width:100%;';

        const vInp = document.createElement('input');
        vInp.type        = isPassword ? 'password' : 'text';
        vInp.placeholder = isPassword ? '••••••••' : 'value';
        vInp.value       = value ?? '';
        vInp.className   = 'jv-val';
        vInp.autocomplete = 'new-password';
        vInp.style.cssText = kInp.style.cssText;

        const del = document.createElement('button');
        del.className   = 'btn danger';
        del.textContent = 'x';
        del.style.cssText = 'padding:4px 9px; font-size:11px;';
        del.onclick = () => row.remove();

        row.appendChild(kInp);
        row.appendChild(vInp);
        row.appendChild(del);
        container.appendChild(row);
    }

    function jvCollect() {
        const result = {};
        document.querySelectorAll('#jvarsRows > div').forEach(row => {
            const k = row.querySelector('.jv-key').value.trim();
            const v = row.querySelector('.jv-val').value;
            if (k) result[k] = v;
        });
        return result;
    }

    // ── Save jVars (PIN + Path + local vars) ─────────────────────────
    async function saveJVars() {
        const pin  = document.getElementById('cfgPinInput').value;
        const path = document.getElementById('cfgJVarsPath').value.trim();

        if (!pin) { toast('PIN is required', 'err'); return; }
        if (!path) { toast('jVars path is required', 'err'); return; }

        const localVars = jvCollect();

        const btn = document.getElementById('saveJVarsBtn');
        btn.disabled = true;
        btn.textContent = '...';

        const payload = btoa(unescape(encodeURIComponent(JSON.stringify({ pin, jVarsPath: path, localVars }))));

        document.getElementById('cfgPinInput').value = '';

        try {
            const r = await fetch('/config/jvars', {
                method: 'POST',
                headers: { 'Content-Type': 'text/plain' },
                body: payload
            });
            if (r.ok) {
                toast('✓ PIN, path & vars saved', 'ok');
            } else {
                const t = await r.text();
                toast('Failed: ' + t, 'err');
            }
        } catch(e) {
            toast('Error: ' + e.message, 'err');
        } finally {
            btn.disabled = false;
            btn.textContent = '🔐 Set PIN & Path';
        }
    }

    // ── Maintenance: разовые операции ─────────────────────
    // Обе раньше жили экзекутором "internal" в планировщике. Расписание
    // им не нужно: выполняются руками. Вывод показываем целиком:
    // обе пишут файлы, и нужно видеть, куда именно они легли.

    function mtShowLog(lines) {
        const el = document.getElementById('mtLog');
        el.style.display = 'block';
        el.textContent = (lines || []).join(String.fromCharCode(10));
    }

    async function mtPost(url, body, btn, busyText, doneText) {
        const label = btn.textContent;
        btn.disabled = true;
        btn.textContent = busyText;
        try {
            const r = await fetch(url, {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify(body)
            });
            const d = await r.json().catch(() => ({}));
            mtShowLog(d.log && d.log.length ? d.log : [d.result || d.error || '']);
            if (r.ok && d.ok) toast(doneText + (d.result ? ': ' + d.result : ''), 'ok');
            else              toast('Failed: ' + (d.error || r.status), 'err');
        } catch (e) {
            mtShowLog([e.message]);
            toast('Error: ' + e.message, 'err');
        } finally {
            btn.disabled = false;
            btn.textContent = label;
        }
    }

    function mtClientBundle() {
        const name = document.getElementById('mtClientName').value.trim();
        const hwid = document.getElementById('mtClientHwid').value.trim();
        if (!name) { toast('Client name is required', 'err'); return; }
        if (!hwid) { toast('Client HWID is required', 'err'); return; }
        mtPost('/config/client-bundle', {
            clientName:   name,
            clientHwid:   hwid,
            outputFolder: document.getElementById('mtClientOut').value.trim()
        }, document.getElementById('mtBundleBtn'), 'building...', 'bundle ready');
    }

    function mtUpdateTemplates() {
        mtPost('/config/update-templates',
               { outDir: document.getElementById('mtTplOut').value.trim() },
               document.getElementById('mtTplBtn'), 'writing...', 'templates written');
    }

    // ── Memory Watchdog ───────────────────────────────────────────────
    function renderWatchdog(d) {
        const stateEl = document.getElementById('wdState');
        stateEl.innerHTML = d.enabled
            ? '<span class="pill ok">ON</span>'
            : '<span class="pill err">OFF</span>';

        const over = d.limitMb > 0 && d.currentMemMb > d.limitMb;
        document.getElementById('wdBadge').textContent =
            d.running ? (over ? 'OVER LIMIT' : 'watching') : 'no process';

        setText('wdProc', d.processName || '—');
        setText('wdCurMem', d.running ? d.currentMemMb + ' MB' + (d.instances > 1 ? ' (' + d.instances + ')' : '') : 'not running');
        setText('wdCurLimit', d.limitMb > 0 ? d.limitMb + ' MB' : 'off');
        setText('wdPid', d.running && d.pid > 0 ? d.pid : '—');
        setText('wdLastKill', d.lastKillTs ? d.lastKillTs + ' · ' + d.lastKillMemMb + ' MB' : 'never');
    }

    async function loadWatchdogStatus() {
        try {
            const r = await fetch('/watchdog/status');
            if (!r.ok) return;
            renderWatchdog(await r.json());
        } catch (e) { /* silent */ }
    }

    async function loadWatchdog() {
        try {
            const r = await fetch('/watchdog/status');
            if (!r.ok) return;
            const d = await r.json();
            setVal('wdProcessName', d.processName || 'ZennoPoster');
            document.getElementById('wdEnabled').value = d.enabled ? 'true' : 'false';
            setVal('wdLimitMb', d.limitMb ?? 0);
            setVal('wdIntervalSec', d.intervalSec ?? 15);
            renderWatchdog(d);
        } catch (e) { /* silent */ }
    }

    async function saveWatchdog() {
        const payload = {
            enabled:     document.getElementById('wdEnabled').value === 'true',
            processName: document.getElementById('wdProcessName').value.trim() || 'ZennoPoster',
            limitMb:     parseInt(document.getElementById('wdLimitMb').value) || 0,
            intervalSec: parseInt(document.getElementById('wdIntervalSec').value) || 15,
        };
        try {
            const r = await fetch('/watchdog/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const d = await r.json();
            if (d.ok) {
                toast('✓ Watchdog saved', 'ok');
                if (d.status) renderWatchdog(d.status);
            } else {
                toast('Save failed: ' + (d.error ?? 'unknown'), 'err');
            }
        } catch (e) {
            toast('Error: ' + e.message, 'err');
        }
    }

    // ── Clipboard Converter ───────────────────────────────────────────
    function renderClipConv(d) {
        if (!d.supported) {
            document.getElementById('ccBadge').textContent = 'Windows only';
            document.getElementById('ccState').innerHTML = '<span class="pill warn">N/A</span>';
            document.getElementById('ccFields').style.display = 'none';
            return;
        }

        document.getElementById('ccState').innerHTML = d.enabled
            ? '<span class="pill ok">ON</span>'
            : '<span class="pill err">OFF</span>';

        document.getElementById('ccActive').innerHTML = d.active
            ? '<span class="pill ok">active</span>'
            : '<span class="pill warn">idle</span>';

        document.getElementById('ccBadge').textContent =
            !d.enabled ? 'off' : (d.active ? 'listening' : 'waiting for ' + (d.processName || 'focus'));

        setText('ccFg', d.foreground || '—');

        (d.hotkeys || []).forEach((k, i) => {
            const el = document.getElementById('ccK' + i);
            if (!el) return;
            el.textContent = k.registered ? 'ok' : (k.error || '—');
        });

        setText('ccConverted', d.converted);
        setText('ccMissed',    d.missed);
        setText('ccErrors',    d.errors);
        setText('ccLast',      d.lastResult || '—');
        setText('ccLastErr',   d.lastError  || '—');
    }

    async function loadClipConvStatus() {
        try {
            const r = await fetch('/clipconv/status');
            if (!r.ok) return;
            renderClipConv(await r.json());
        } catch (e) { /* silent */ }
    }

    async function loadClipConv() {
        try {
            const r = await fetch('/clipconv/status');
            if (!r.ok) return;
            const d = await r.json();
            document.getElementById('ccEnabled').value = d.enabled ? 'true' : 'false';
            setVal('ccProcessName', d.processName ?? 'ProjectMaker');
            renderClipConv(d);
        } catch (e) { /* silent */ }
    }

    async function saveClipConv() {
        const payload = {
            enabled:     document.getElementById('ccEnabled').value === 'true',
            processName: document.getElementById('ccProcessName').value.trim(),
        };
        try {
            const r = await fetch('/clipconv/config', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(payload)
            });
            const d = await r.json();
            if (d.ok) {
                toast('✓ Clipboard converter saved', 'ok');
                if (d.status) renderClipConv(d.status);
            } else {
                toast('Save failed: ' + (d.error ?? 'unknown'), 'err');
            }
        } catch (e) {
            toast('Error: ' + e.message, 'err');
        }
    }

    async function clipConvSelfTest() {
        try {
            const r = await fetch('/clipconv/selftest');
            const d = await r.json();
            if (d.ok) {
                toast('✓ Self-test passed (' + d.cases.length + ' cases)', 'ok');
            } else {
                const bad = d.cases.filter(c => !c.passed).map(c => c.name).join(', ');
                toast('Self-test FAILED: ' + bad, 'err');
            }
        } catch (e) {
            toast('Error: ' + e.message, 'err');
        }
    }

    // ── Sections ──────────────────────────────────────────
    let _uptimeTimer   = null;
    let _servicesTimer = null;
    let _storageLoaded = false;

    function enterOverview() {
        loadStatus();
        if (!_storageLoaded) { _storageLoaded = true; loadStorage(); }
        if (_uptimeTimer === null) _uptimeTimer = setInterval(tickUptime, 1000);
    }
    function leaveOverview() {
        if (_uptimeTimer !== null) { clearInterval(_uptimeTimer); _uptimeTimer = null; }
    }
    function enterServices() {
        loadWatchdogStatus();
        loadClipConvStatus();
        if (_servicesTimer === null)
            _servicesTimer = setInterval(() => { loadWatchdogStatus(); loadClipConvStatus(); }, 5000);
    }
    function leaveServices() {
        if (_servicesTimer !== null) { clearInterval(_servicesTimer); _servicesTimer = null; }
    }

    const SECTIONS = [
        { id: 'overview', icon: '🟢', title: 'Overview',            enter: enterOverview, leave: leaveOverview },
        { id: 'db',       icon: '🗄', title: 'Database' },
        { id: 'server',   icon: '⚙',      title: 'Logs & Server' },
        { id: 'browsers', icon: '🌐', title: 'Browsers API' },
        { id: 'ai',       icon: '✨',      title: 'OmniRoute' },
        { id: 'services', icon: '🛡', title: 'Services',            enter: enterServices, leave: leaveServices },
        { id: 'security', icon: '🔐', title: 'Security · jVars' },
        { id: 'maint',    icon: '🧰', title: 'Maintenance' },
    ];

    const SEPARATORS_AFTER = ['overview', 'ai', 'services'];

    let _curSec = null;

    function buildRail() {
        const rail = document.getElementById('cfgRail');
        rail.textContent = '';
        SECTIONS.forEach(sec => {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'cfg-rail-item';
            b.dataset.sec = sec.id;
            b.innerHTML = '<span class="icon"></span><span class="label"></span>';
            b.querySelector('.icon').textContent  = sec.icon;
            b.querySelector('.label').textContent = sec.title;
            b.addEventListener('click', () => showSection(sec.id));
            rail.appendChild(b);
            if (SEPARATORS_AFTER.includes(sec.id)) {
                const hr = document.createElement('div');
                hr.className = 'cfg-rail-sep';
                rail.appendChild(hr);
            }
        });
    }

    function showSection(id) {
        if (!SECTIONS.some(s => s.id === id)) id = SECTIONS[0].id;
        if (id === _curSec) return;

        const prev = SECTIONS.find(s => s.id === _curSec);
        if (prev && prev.leave) prev.leave();

        document.querySelectorAll('.cfg-sec').forEach(el => {
            el.hidden = el.dataset.sec !== id;
        });
        document.querySelectorAll('.cfg-rail-item').forEach(el => {
            el.classList.toggle('active', el.dataset.sec === id);
        });

        _curSec = id;
        PageState.save({ sec: id });

        const cur = SECTIONS.find(s => s.id === id);
        if (cur && cur.enter) cur.enter();
    }


    // ── Init ──────────────────────────────────────────────────────────
    (async () => {
        buildRail();
        await Promise.all([loadConfig(), loadWatchdog(), loadClipConv()]);
        jvEnsureSeed();
        showSection((PageState.load() || {}).sec || SECTIONS[0].id);
    })();

    // ── IMPORT ────────────────────────────────────────────────────────

    const IMPORT_TYPES = [
        { id: 'proxy',    label: 'Proxy' },
        { id: 'evm_addr', label: 'Addresses · EVM' },
        { id: 'sol_addr', label: 'Addresses · SOL' },
    ];

    let _importQueue = [];
    let _importIdx   = 0;

    function openImport() {
        const overlay = document.getElementById('importOverlay');
        overlay.style.display = 'flex';
        document.getElementById('importStep1').style.display = '';
        document.getElementById('importStep2').style.display = 'none';
        renderChecks();
    }

    function closeImport() {
        document.getElementById('importOverlay').style.display = 'none';
        _importQueue = [];
        _importIdx   = 0;
    }

    function renderChecks() {
        const box = document.getElementById('importChecks');
        box.innerHTML = '';
        IMPORT_TYPES.forEach(t => {
            const row = document.createElement('label');
            row.style.cssText = 'display:flex; align-items:center; gap:8px; cursor:pointer; padding:3px 6px; border-radius:4px;';
            row.innerHTML = '<input type="checkbox" value="' + t.id + '" style="accent-color:var(--accent);"> <span>' + t.label + '</span>';
            box.appendChild(row);
        });
    }

    function startImport() {
        const checked = [...document.querySelectorAll('#importChecks input:checked')].map(el => el.value);
        if (checked.length === 0) { toast('Nothing selected', 'err'); return; }
        _importQueue = checked;
        _importIdx   = 0;
        document.getElementById('importStep1').style.display = 'none';
        document.getElementById('importStep2').style.display = '';
        renderStep();
    }

    function renderStep() {
        const id = _importQueue[_importIdx];
        const t  = IMPORT_TYPES.find(x => x.id === id);
        document.getElementById('importStepTitle').textContent = t.label;
        document.getElementById('importStepSub').textContent   = 'Step ' + (_importIdx + 1) + ' of ' + _importQueue.length;
        document.getElementById('importNextBtn').textContent   =
            _importIdx < _importQueue.length - 1 ? 'Import & Next \u2192' : 'Import \u2713';

        const body = document.getElementById('importStepBody');
        body.innerHTML = '';

        addTextarea(body, 'lines', 'One entry per line');
    }

    function addTextarea(parent, dataId, placeholder) {
        const ta = document.createElement('textarea');
        ta.dataset.id    = dataId;
        ta.placeholder   = placeholder;
        ta.style.cssText = 'width:100%; min-height:100px; resize:vertical; background:var(--bg); border:1px solid var(--border); border-radius:6px; color:var(--text); padding:8px; font-family:inherit; font-size:11px; box-sizing:border-box;';
        parent.appendChild(ta);
    }

    function getField(dataId) {
        const el = document.querySelector('#importStepBody [data-id="' + dataId + '"]');
        return el ? el.value.trim() : '';
    }

    async function submitStep() {
        const id      = _importQueue[_importIdx];
        const payload = buildPayload(id);
        if (!payload) return;

        const endpoint = resolveEndpoint(id);
        const btn      = document.getElementById('importNextBtn');
        btn.disabled   = true;
        btn.textContent = 'Importing\u2026';

        try {
            const res  = await fetch(endpoint, { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) });
            const data = await res.json();

            if (!data.ok) { toast('Error: ' + data.error, 'err'); return; }

            toast('\u2713 ' + data.imported + ' rows');
            _importIdx++;
            if (_importIdx < _importQueue.length) renderStep();
            else closeImport();
        } catch (e) {
            toast('Request failed: ' + e.message, 'err');
        } finally {
            btn.disabled    = false;
            btn.textContent = _importIdx < _importQueue.length - 1 ? 'Import & Next \u2192' : 'Import \u2713';
        }
    }

    function resolveEndpoint(id) {
        if (id === 'evm_addr' || id === 'sol_addr')                           return '/import/addresses';
        return '/import/' + id;
    }

    function buildPayload(id) {
        if (id === 'proxy')    return { lines: getField('lines') };
        if (id === 'evm_addr') return { type:'evm',  lines: getField('lines') };
        if (id === 'sol_addr') return { type:'sol',  lines: getField('lines') };

        return { lines: getField('lines') };
    }
