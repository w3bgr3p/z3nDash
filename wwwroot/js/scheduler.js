// scheduler.js

// Safe wrappers for PageState (may not exist in standalone mode)
var _PS = {
    save: function(obj) { if (typeof PageState !== 'undefined') PageState.save(obj); },
    load: function()    { return (typeof PageState !== 'undefined') ? PageState.load() : {}; },
};

var schedules  = [];
var _globalTerminal = 'cmd';
var selectedId = null;
var activeTab  = 'execution';
var outputPoll = null;
var formDirty  = false;
var activeExecutorFilters = new Set();

var _sseLog    = null;
var _sseHttp   = null;
var _sseOutput = null;

var curProject = '';
var curTaskId  = '';
var curRunId   = '';

// ── Config modal state ────────────────────────────────────────────────────────

var _cmEditor     = null;
var _cmFilePath   = '';
var _cmScheduleId = '';

function _isJs(executor)     { return executor === 'node' || executor === 'ts-node'; }
function _isPy(executor)     { return executor === 'python'; }
function _needsConfig(executor) { return _isJs(executor) || _isPy(executor); }

// ── Status helpers ────────────────────────────────────────────────────────────

function getTaskStatus(s) {
    if (s.status === 'running') return 'running';
    var neverRan = !s.runs_total || parseInt(s.runs_total) === 0;
    if (neverRan) return 'newbie';
    var hasSchedule = !!((s.cron && s.cron.trim()) ||
        (s.interval_minutes && parseInt(s.interval_minutes) > 0) ||
        (s.fixed_time && s.fixed_time.trim()));
    var isPaused = s.enabled === 'false';
    if (hasSchedule) return isPaused ? 'paused' : 'planned';
    var isFail = s.status === 'error' || (s.last_exit && s.last_exit !== '0');
    return isFail ? 'fail' : 'done';
}

function escHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// ── SSE ───────────────────────────────────────────────────────────────────────

function closeSse() {
    if (_sseLog)    { _sseLog.close();    _sseLog    = null; }
    if (_sseHttp)   { _sseHttp.close();   _sseHttp   = null; }
}

function closeSseOutput() {
    if (_sseOutput) { _sseOutput.close(); _sseOutput = null; }
}

function startSse() {
    closeSse();
    if (!curTaskId) return;
    _sseLog = new EventSource('/logs/stream?task_id=' + encodeURIComponent(curTaskId));
    _sseLog.addEventListener('message', function(e) {
        try { appendLogRow(JSON.parse(e.data)); } catch(err) {}
    });
    _sseLog.onerror = function() { _sseLog.close(); _sseLog = null; };

    _sseHttp = new EventSource('/http-logs/stream?task_id=' + encodeURIComponent(curTaskId));
    _sseHttp.addEventListener('message', function(e) {
        try { appendHttpRow(JSON.parse(e.data)); } catch(err) {}
    });
    _sseHttp.onerror = function() { _sseHttp.close(); _sseHttp = null; };
}

// ── Icons ─────────────────────────────────────────────────────────────────────

function execIcon(ex) {
    if (typeof EXEC_ICONS === 'undefined') return '<span style="font-size:9px;opacity:0.5">' + (ex || '?') + '</span>';
    var svg = EXEC_ICONS[ex];
    if (!svg) return '<span style="font-size:9px;opacity:0.5">' + (ex || '?') + '</span>';
    var sized = svg.replace('<svg ', '<svg width="14" height="14" ');
    return '<span title="' + escHtml(ex) + '" style="display:inline-flex;align-items:center;opacity:0.7">' + sized + '</span>';
}

// ── Init ──────────────────────────────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', function() {
    if (typeof AiPanel !== 'undefined') {
        // default cwd = fetch from server (app.py root)
        fetch('/ai/cwd').then(function(r){ return r.json(); }).then(function(d){
            AiPanel._defaultCwd = d.cwd || '';
            AiPanel._cwd        = d.cwd || '';
        }).catch(function(){});
        AiPanel.setContext(function() {
            if (!AiPanel._schedulerCtx) return '';
            var s = AiPanel._schedulerCtx;
            return 'Current scheduler task:\n' + JSON.stringify({
                id:         s.id,
                name:       s.name,
                executor:   s.executor,
                script_path: s.script_path,
                status:     s.status,
                last_run:   s.last_run,
                last_exit:  s.last_exit,
                cron:       s.cron,
                interval_minutes: s.interval_minutes,
            }, null, 2);
        });
    }
    loadList().catch(function(e) {
        document.getElementById('listScroll').innerHTML =
            '<div style="padding:12px;color:red">Error: ' + e.message + '</div>';
    });
    initResizer();
    initHResizer();
    initVResizer();
    restoreLayout();
    document.getElementById('detailBody').addEventListener('input',  function() { formDirty = true; });
    document.getElementById('detailBody').addEventListener('change', function() { formDirty = true; });
});

// ── Executor tag filter ───────────────────────────────────────────────────────

function renderExecutorTags() {
    var bar = document.getElementById('execFilterBar');
    if (!bar) return;
    var counts = {};
    schedules.forEach(function(s) {
        var ex = s.executor || 'unknown';
        counts[ex] = (counts[ex] || 0) + 1;
    });
    var keys = Object.keys(counts).sort();
    if (keys.length <= 1) { bar.innerHTML = ''; return; }
    var html = '<span class="tag-filter-label">Exec:</span>';
    keys.forEach(function(ex) {
        var active = activeExecutorFilters.has(ex);
        html += '<span class="tag-chip' + (active ? ' active' : '') + '" onclick="toggleExecFilter(\'' + escHtml(ex) + '\')">'
            + escHtml(ex) + ' <span style="opacity:0.55">' + counts[ex] + '</span></span>';
    });
    if (activeExecutorFilters.size > 0)
        html += '<span class="tag-chip clear-chip" onclick="clearExecFilters()">✕</span>';
    bar.innerHTML = html;
}

function toggleExecFilter(ex) {
    if (activeExecutorFilters.has(ex)) activeExecutorFilters.delete(ex);
    else activeExecutorFilters.add(ex);
    renderExecutorTags();
    renderList();
}

function clearExecFilters() {
    activeExecutorFilters.clear();
    renderExecutorTags();
    renderList();
}

// ── Load list ─────────────────────────────────────────────────────────────────

async function loadList() {
    var res = await fetch('/scheduler/list');
    if (!res.ok) throw new Error('/scheduler/list HTTP ' + res.status);
    schedules = await res.json();
    updateHeaderStats();
    renderExecutorTags();
    renderList();
    if (selectedId && !formDirty) {
        var still = schedules.find(function(s) { return s.id === selectedId; });
        if (still) {
            renderDetailActions(still);
            if (activeTab !== 'output') renderDetail(still);
        }
    } else if (!selectedId) {
        var saved = _PS.load().selectedId;
        if (saved) {
            var s = schedules.find(function(x) { return x.id === saved; });
            if (s) { selectRow(s.id); return; }
        }
        renderGlobalStats();
    }
}

function updateHeaderStats() {
    var total   = schedules.length;
    var running = schedules.filter(function(s) { return s.status === 'running'; }).length;
    var errors  = schedules.filter(function(s) { return getTaskStatus(s) === 'fail'; }).length;
    var enabled = schedules.filter(function(s) { return s.enabled !== 'false'; }).length;
    document.getElementById('headerStats').innerHTML =
        '<div class="stat-item">Tasks: <span class="stat-val">' + total + '</span></div>' +
        '<div class="stat-item">Running: <span class="stat-val stat-running">' + running + '</span></div>' +
        '<div class="stat-item">Active: <span class="stat-val">' + enabled + '</span></div>' +
        (errors ? '<div class="stat-item">Errors: <span class="stat-val stat-error">' + errors + '</span></div>' : '');
}

// ── List rendering ────────────────────────────────────────────────────────────

var collapsedGroups    = new Set();
var _groupsInitialized = false;

function toggleGroup(grp) {
    if (collapsedGroups.has(grp)) collapsedGroups.delete(grp);
    else collapsedGroups.add(grp);
    renderList();
}

function triggerLabel(s) {
    if (s.cron && s.cron.trim()) return s.cron.trim();
    if (s.interval_minutes && parseInt(s.interval_minutes) > 0) return 'every ' + s.interval_minutes + 'm';
    if (s.fixed_time && s.fixed_time.trim()) return 'at ' + s.fixed_time.trim();
    return 'on demand';
}

function getGroupName(name) {
    if (!name) return '';
    var dot = name.indexOf('.');
    return dot > 0 ? name.substring(0, dot) : '';
}

function renderList() {
    var q  = document.getElementById('searchInput').value.toLowerCase();
    var el = document.getElementById('listScroll');
    var filtered = schedules.filter(function(s) {
        if (activeExecutorFilters.size > 0 && !activeExecutorFilters.has(s.executor || 'unknown')) return false;
        return !q || (s.name && s.name.toLowerCase().indexOf(q) >= 0) ||
               (s.script_path && s.script_path.toLowerCase().indexOf(q) >= 0);
    });
    var countEl = document.getElementById('listCount');
    if (countEl) countEl.textContent = filtered.length;

    var groupCounts = {};
    filtered.forEach(function(s) {
        var g = getGroupName(s.name);
        if (g) groupCounts[g] = (groupCounts[g] || 0) + 1;
    });

    filtered.sort(function(a, b) {
        var ga = getGroupName(a.name), gb = getGroupName(b.name);
        var showA = ga && groupCounts[ga] > 1, showB = gb && groupCounts[gb] > 1;
        if (showA && !showB) return -1;
        if (!showA && showB) return 1;
        if (showA && showB) return ga < gb ? -1 : ga > gb ? 1 : 0;
        return 0;
    });

    if (!_groupsInitialized) {
        _groupsInitialized = true;
        Object.keys(groupCounts).forEach(function(g) {
            if (groupCounts[g] > 1) collapsedGroups.add(g);
        });
    }

    var html = '', lastGrp = null, firstUngrouped = true;

    filtered.forEach(function(s) {
        var grp      = getGroupName(s.name);
        var showGrp  = grp && groupCounts[grp] > 1;
        var collapsed = showGrp && collapsedGroups.has(grp);
        var shortName = showGrp && s.name.length > grp.length + 1 ? s.name.substring(grp.length + 1) : s.name;

        if (showGrp && grp !== lastGrp) {
            var grpRunning = filtered.filter(function(x) { return getGroupName(x.name) === grp && x.status === 'running'; }).length;
            var dots = grpRunning > 0
                ? Array(grpRunning).fill('<span style="display:inline-block;width:6px;height:6px;border-radius:50%;background:var(--green,#3fb950);margin-right:2px"></span>').join('')
                : '';
            var isCollapsed = collapsedGroups.has(grp);
            html += '<div class="group-header" onclick="toggleGroup(\'' + grp + '\')">'
                + '<span class="group-dot"></span>'
                + '<span class="group-name">' + escHtml(grp) + '</span>'
                + (dots ? '<span>' + dots + '</span>' : '')
                + '<span class="group-count">' + groupCounts[grp] + '</span>'
                + '<span class="group-chevron' + (isCollapsed ? ' collapsed' : '') + '">&#9660;</span>'
                + '</div>';
            lastGrp = grp;
        } else if (!showGrp) {
            if (lastGrp !== null) firstUngrouped = true;
            if (firstUngrouped && lastGrp !== null) { html += '<div class="ungrouped-separator"></div>'; firstUngrouped = false; }
            lastGrp = null;
        }

        if (collapsed) return;

        var status   = getTaskStatus(s);
        var disabled = s.enabled === 'false';
        var total    = parseInt(s.runs_total)   || 0;
        var done     = parseInt(s.runs_success) || 0;
        var trigger  = triggerLabel(s);
        var lastRun  = s.last_run ? s.last_run.slice(5, 16) : '';

        html += '<div class="schedule-row'
            + (showGrp ? ' group-child' : '')
            + (s.id === selectedId ? ' active' : '')
            + (disabled ? ' row-disabled' : '')
            + '" onclick="selectRow(\'' + s.id + '\')">'
            + '<span class="row-dot ' + (disabled ? 'disabled' : 'enabled')
            + '" title="' + (disabled ? 'Enable' : 'Disable')
            + '" onclick="event.stopPropagation();toggleEnabled(\'' + s.id + '\',\'' + s.enabled + '\')"></span>'
            + '<div class="row-info">'
            + '<div class="row-name">' + escHtml(showGrp ? shortName : (s.name || '(unnamed)')) + '</div>'
            + '<div class="row-sub">' + escHtml(trigger) + (lastRun ? ' · ' + lastRun : '') + '</div>'
            + '</div>'
            + '<div class="row-right">'
            + '<div style="display:flex;align-items:center;gap:4px">'
            + '<span class="task-status ' + status + '">' + status + '</span>'
            + '<span style="font-size:11px;" title="' + escHtml(s.executor || '') + '">' + execIcon(s.executor || '') + '</span>'
            + '</div>'
            + '<span class="row-counts"><span class="row-done">' + done + '</span><span class="row-total"> / ' + total + '</span></span>'
            + '</div>'
            + '</div>';
    });

    el.innerHTML = html || '<div style="padding:12px;color:var(--text2);text-align:center">No results</div>';
}

function filterList() { renderList(); }

function deselect() {
    if (!selectedId) return;
    selectedId = null;
    formDirty  = false;
    stopProcStatsPoll();
    closeSse();
    clearOutputPoll();
    _PS.save({ selectedId: null });
    renderList();
    renderGlobalStats();
}

// ── Nav / global stats ────────────────────────────────────────────────────────

function renderGlobalStats() {
    var NAV_PAGES = [
        { icon: (typeof ICONS !== 'undefined' ? ICONS.scheduler : ''), title: 'z3nIO',      desc: 'Запуск .py, .js, .exe, .bat по cron, или интервалам (you are here)', url: '/scheduler.html', color: '#e3b341' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.zp7       : ''), title: 'ZP7',        desc: 'Управление ZP7',                                                      url: '/?page=zp7',      color: '#58a6ff' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.logs       : ''), title: 'Logs',       desc: 'Логи приложения с фильтрацией по уровню, машине, проекту, аккаунту.', url: '/?page=logs',     color: '#3fb950' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.http       : ''), title: 'HTTP',       desc: 'Перехваченные HTTP-запросы и ответы из ZP-задач. Replay запросов.',   url: '/?page=http',     color: '#d29922' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.json       : ''), title: 'JSON',       desc: 'Интерактивный JSON-tree с определением auth/captcha и replay.',       url: '/json',           color: '#4e9eff' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.clips      : ''), title: 'Clips',      desc: 'Copy-paste шаблоны, организованные в дерево.',                       url: '/?page=clips',    color: '#f0883e' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.text       : ''), title: 'Text Tools', desc: 'URL encode/decode, C# escaper, Base64, JSON escape.',                 url: '/text.html',      color: '#a371f7' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.config     : ''), title: 'Config',     desc: 'Статус сервера, редактор конфигурации, управление хранилищем логов.', url: '/?page=config',   color: '#f78166' },
    ];
    document.getElementById('detailHeader').style.display  = 'none';
    document.getElementById('hResizer').style.display      = 'none';
    document.getElementById('bottomPanels').style.display  = 'none';;
    var dp = document.getElementById('detailPanel');
    if (dp) { dp.style.flex = ''; dp.style.height = ''; }
    var cards = NAV_PAGES.map(function(p) {
        return '<a class="nav-card" href="' + p.url + '" style="--card-color:' + p.color + '">'
            + '<div class="card-icon">' + p.icon + '</div>'
            + '<div class="card-title">' + p.title + '</div>'
            + '<div class="card-desc">'  + p.desc  + '</div>'
            + '<div class="card-badge">Open &rarr;</div></a>';
    }).join('');
    document.getElementById('detailBody').innerHTML =
        '<div style="padding:20px 14px;display:flex;justify-content:center;align-items:center;min-height:100%;box-sizing:border-box">'
        + '<div class="nav-grid" style="max-width:860px;width:100%">' + cards + '</div></div>';
}

// ── Select / detail ───────────────────────────────────────────────────────────

function selectRow(id) {
    selectedId = id;
    formDirty  = false;
    stopProcStatsPoll();
    _PS.save({ selectedId: id });
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    renderList();
    showDetailHeader(s);
    activeTab = 'execution';
    setActiveTab('execution');
    renderDetail(s);
    showBottomPanels(s);
    _updateAiContext(s);
}

function _updateAiContext(s) {
    if (typeof AiPanel === 'undefined') return;
    AiPanel._schedulerCtx = s;
    // cwd is set when AI button is clicked (per-task or header)
}

function showDetailHeader(s) {
    document.getElementById('detailHeader').style.display = '';
    document.getElementById('detailTitle').textContent = s.name || '(unnamed)';
    document.getElementById('detailSub').textContent   = s.script_path || '';
    renderDetailActions(s);
}

function renderDetailActions(s) {
    var id         = s.id || '';
    var pauseLabel = s.enabled === 'false' ? '▶ Resume' : '⏸ Pause';
    var runLabel   = _isJs(s.executor) ? '▶ npm run' : '▶ Run';

    document.getElementById('detailActions').innerHTML =
        '<div class="action-group">'
        + '<button class="btn primary sm" onclick="runNow(\'' + id + '\')">' + runLabel + '</button>'
        + '<button class="btn sm" onclick="toggleEnabled(\'' + id + '\',\'' + (s.enabled || 'true') + '\')">' + pauseLabel + '</button>'
        + '<button class="btn stop sm" onclick="stopNow(\'' + id + '\')">■ Interrupt</button>'
        + '<button class="btn danger sm" onclick="deleteSchedule(\'' + id + '\',\'' + escHtml(s.name || '') + '\')">🗑</button>'
        + '<button class="btn sm" onclick="duplicateSchedule(\'' + id + '\')" style="border-color:#d29922;color:#d29922;">📋 Duplicate</button>'
        + '</div>'
        + '<div class="action-group">'
        + '<button class="btn green sm" onclick="openValuesModal(\'' + id + '\',\'' + escHtml(s.name || '') + '\')">⚙ Settings</button>'
        + '<button class="btn accent sm" onclick="openSchemaModal(\'' + id + '\',\'' + escHtml(s.name || '') + '\')">🔧 Edit Settings</button>'
        + '<button class="btn sm" onclick="openImportPayload(\'' + id + '\')" style="border-color:#58a6ff;color:#58a6ff;">📥 Import</button>'
        + '<button class="btn sm" onclick="exportPayload(\'' + id + '\')" style="border-color:#58a6ff;color:#58a6ff;">📤 Export</button>'
        + (s.executor === 'csx-internal' ? '<button class="btn sm" onclick="buildCsx(\'' + id + '\')" style="border-color:#a371f7;color:#a371f7;">🔨 Build csx</button>' : '')
        + (s.script_path ? '<button class="btn sm" data-fp="' + escHtml(s.script_path) + '" onclick="openScriptFile(this.dataset.fp)" style="border-color:#3fb950;color:#3fb950;">📄 Open File</button>' : '')
        + (s.script_path ? '<button class="btn sm" data-fp="' + escHtml(s.script_path) + '" onclick="openScriptFolder(this.dataset.fp)" style="border-color:#3fb950;color:#3fb950;">📁 Open Folder</button>' : '')
    + (s.script_path ? '<button class="btn sm" onclick="openAiForTask(\'' + escHtml(s.id) + '\')" style="border-color:var(--accent);color:var(--accent);">⟡ AI</button>' : '')
        + (s.script_path ? '<button class="btn sm" onclick="openInTerminal(\'' + escHtml(s.id) + '\')" style="border-color:#a371f7;color:#a371f7;">⌨ Terminal</button>' : '')
        + '</div>';

    // async: добавить кнопки config/install если нужно
    var prev = document.getElementById('extActionGroup');
    if (prev) prev.remove();
    if (_needsConfig(s.executor)) extendDetailActions(s);
}

// ── Config / Install buttons (async, добавляются после scan-folder) ───────────

async function extendDetailActions(s) {
    var res, info;
    try {
        res  = await fetch('/scheduler/scan-folder?id=' + encodeURIComponent(s.id));
        info = await res.json();
    } catch(e) { return; }

    var group = document.createElement('div');
    group.className = 'action-group';
    group.id = 'extActionGroup';

    // npm scripts dropdown
    if (_isJs(s.executor)) {
        try {
            var pkgRes   = await fetch('/scheduler/package-scripts?id=' + encodeURIComponent(s.id));
            var pkgData  = await pkgRes.json();
            var scripts  = pkgData.scripts || {};
            var names    = Object.keys(scripts);
            if (names.length > 0) {
                var sel = document.createElement('select');
                sel.id  = 'npmScriptSelect';
                sel.style.cssText = 'background:var(--bg);border:1px solid var(--accent);border-radius:3px;padding:2px 5px;color:var(--accent);font-size:10px;cursor:pointer;';
                sel.title = 'Select npm script to run';

                // detect currently selected from args
                var currentArgs = s.args || '';
                var NPM_PREFIX  = '__npm_run__';
                var currentScript = currentArgs.startsWith(NPM_PREFIX)
                    ? currentArgs.slice(NPM_PREFIX.length)
                    : '';

                names.forEach(function(name) {
                    var opt = document.createElement('option');
                    opt.value = name;
                    opt.textContent = name;
                    if (name === currentScript) opt.selected = true;
                    sel.appendChild(opt);
                });

                // if no args yet — select first and persist
                if (!currentScript && names.length > 0) {
                    sel.value = names[0];
                    _saveNpmScript(s.id, names[0]);
                }

                sel.addEventListener('change', function() {
                    _saveNpmScript(s.id, sel.value);
                    // update run button label
                    var runBtn = document.querySelector('#detailActions .btn.primary.sm');
                    if (runBtn) runBtn.textContent = '▶ npm run ' + sel.value;
                });

                group.appendChild(sel);

                // update run button to show selected script
                var runBtn = document.querySelector('#detailActions .btn.primary.sm');
                if (runBtn) runBtn.textContent = '▶ npm run ' + sel.value;
            }
        } catch(e) {}
    }

    var cfgLabel = _isJs(s.executor) ? 'config.json' : 'config.py';
    var cfgBtn   = document.createElement('button');
    cfgBtn.className = 'btn sm';
    cfgBtn.style.cssText = 'border-color:#58a6ff;color:#58a6ff;';
    cfgBtn.textContent = (info.has_config ? '⚙ ' : '+ ') + cfgLabel;
    cfgBtn.onclick = function() { openCmModal(s, 'config'); };
    group.appendChild(cfgBtn);

    if (_isJs(s.executor)) {
        var pkgBtn = document.createElement('button');
        pkgBtn.className = 'btn sm';
        pkgBtn.style.cssText = 'border-color:#58a6ff;color:#58a6ff;';
        pkgBtn.textContent = (info.has_package_json ? '📦 package.json' : '+ package.json');
        pkgBtn.onclick = function() { openCmModal(s, 'package'); };
        group.appendChild(pkgBtn);
    }

    var canInstall = _isJs(s.executor) || info.has_requirements;
    if (canInstall) {
        var instLabel = _isJs(s.executor) ? '📦 npm install' : '📦 pip install';
        var instBtn   = document.createElement('button');
        instBtn.className = 'btn sm';
        instBtn.style.cssText = 'border-color:#3fb950;color:#3fb950;';
        instBtn.textContent = instLabel;
        instBtn.onclick = function() { runInstall(s, instBtn); };
        group.appendChild(instBtn);
    }

    var actionsEl = document.getElementById('detailActions');
    if (actionsEl) actionsEl.appendChild(group);
}

async function _saveNpmScript(scheduleId, scriptName) {
    var s = schedules.find(function(x) { return x.id === scheduleId; });
    if (!s) return;
    var newArgs = '__npm_run__' + scriptName;
    if (s.args === newArgs) return;
    s.args = newArgs;
    try {
        await fetch('/scheduler/save', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(Object.assign({}, s, { args: newArgs })),
        });
    } catch(e) {}
}

// ── Install stream ────────────────────────────────────────────────────────────

function runInstall(s, btn) {
    btn.disabled    = true;
    btn.textContent = '⏳ installing...';
    switchTab('output');

    var box   = _getOutputBox();
    var badge = _getLiveBadge();
    if (box)   { box.innerHTML = ''; }
    if (badge) { badge.style.display = 'inline-block'; }

    var src = new EventSource('/scheduler/install/stream?id=' + encodeURIComponent(s.id));

    src.addEventListener('output', function(e) {
        try {
            var d    = JSON.parse(e.data);
            var line  = d.line || '';
            var level = (d.level || 'INFO').toUpperCase();
            var timestamp = new Date().toLocaleString('en-US', {hour12: false});
            if (!box) return;
            var atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
            box.insertAdjacentHTML('beforeend',
                '<div class="out-line ' + level + '"><span class="out-line-text">' + escHtml(line) + '</span><span class="out-line-timestamp">' + timestamp + '</span></div>');
            if (atBottom) box.scrollTop = box.scrollHeight;
        } catch(err) {}
    });

    src.addEventListener('done', function() {
        src.close();
        var badge = _getLiveBadge();
        if (badge) badge.style.display = 'none';
        btn.disabled    = false;
        btn.textContent = _isJs(s.executor) ? '📦 npm install' : '📦 pip install';
    });

    src.onerror = function() {
        src.close();
        var badge = _getLiveBadge();
        if (badge) badge.style.display = 'none';
        btn.disabled    = false;
        btn.textContent = '❌ failed';
        setTimeout(function() {
            btn.textContent = _isJs(s.executor) ? '📦 npm install' : '📦 pip install';
        }, 3000);
    };
}

// ── CodeMirror config modal ───────────────────────────────────────────────────

async function openCmModal(s, type) {
    type = type || 'config';
    _cmScheduleId = s.id;

    var res, data;
    try {
        res  = await fetch('/scheduler/config-file?id=' + encodeURIComponent(s.id) + '&type=' + encodeURIComponent(type));
        data = await res.json();
    } catch(e) { Dialog.error(e.message); return; }

    var mode  = _isJs(s.executor) ? 'javascript' : 'python';
    var title = type === 'package' ? 'package.json'
              : _isJs(s.executor) ? 'config.json' : 'config.py';
    _cmFilePath = data.path || '';

    document.getElementById('cmTitle').textContent = title + (_cmFilePath ? ' — ' + _cmFilePath : '');
    document.getElementById('cmBody').innerHTML    = '<textarea id="cmTextarea"></textarea>';
    document.getElementById('cmOverlay').classList.add('open');

    if (_cmEditor) { try { _cmEditor.toTextArea(); } catch(e) {} _cmEditor = null; }

    _cmEditor = CodeMirror.fromTextArea(document.getElementById('cmTextarea'), {
        mode:           mode,
        theme:          'default',
        lineNumbers:    true,
        indentUnit:     2,
        tabSize:        2,
        indentWithTabs: false,
        lineWrapping:   false,
        autofocus:      true,
    });
    _cmEditor.setValue(data.ok ? (data.content || '') : '');
    setTimeout(function() { _cmEditor.refresh(); }, 50);
}

function closeCmModal() {
    document.getElementById('cmOverlay').classList.remove('open');
}

async function saveCmConfig() {
    if (!_cmEditor || !_cmFilePath) return;
    try {
        var res  = await fetch('/scheduler/config-file', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ path: _cmFilePath, content: _cmEditor.getValue() }),
        });
        var data = await res.json();
        if (data.ok) closeCmModal();
        else Dialog.error(data.error || 'Save failed');
    } catch(e) { Dialog.error(e.message); }
}

// ── Bottom panels ─────────────────────────────────────────────────────────────

function showBottomPanels(s) {
    var bp = document.getElementById('bottomPanels');
    bp.style.display = 'flex';
    bp.style.flexDirection = 'column';
    document.getElementById('hResizer').style.display = '';
    curProject = '';
    curTaskId  = s.schedule_tag || s.name || s.id;
    curRunId   = s.last_run_id  || '';
    loadOutput(s.id);
    startSseOutput(s.id);
}

function renderDetail(s) {
    if      (activeTab === 'settings') renderSettings(s);
    else if (activeTab === 'logs')     renderLogsTab(s);
    else                               renderExecution(s);
}

function renderLogsTab(s) {
    document.getElementById('detailBody').innerHTML =
        '<div style="display:flex;flex-direction:column;gap:5px;height:100%;min-height:0;">'
        + '<div style="display:flex;gap:0;flex:1;min-height:0;overflow:hidden;">'
        // Logs panel
        + '<div class="log-panel" id="logPanelLogs" style="flex:1;">'
        + '<div class="log-panel-header">'
        + '<div class="log-panel-title"><span class="icon">📋</span> Logs</div>'
        + '<div class="log-panel-filter">'
        + '<select id="logLevel" onchange="loadLogs()">'
        + '<option value="">All Levels</option>'
        + '<option>INFO</option><option>WARNING</option><option>ERROR</option><option>DEBUG</option>'
        + '</select>'
        + '<input id="logLimit" type="number" value="50" min="10" max="500" style="width:46px" onchange="loadLogs()">'
        + '<button class="panel-refresh" onclick="loadLogs()">↺</button>'
        + '<button class="panel-refresh" onclick="clearLogsPanel()" style="color:var(--red,#f85149);border-color:var(--red,#f85149);" title="Clear logs">🗑</button>'
        + '</div></div>'
        + '<div class="log-panel-scroll" id="logsScroll"><div class="log-empty">No logs</div></div>'
        + '</div>'
        // V resizer
        + '<div class="v-resizer" id="vResizer"></div>'
        // HTTP panel
        + '<div class="log-panel" id="logPanelHttp" style="flex:1;">'
        + '<div class="log-panel-header">'
        + '<div class="log-panel-title"><span class="icon">🌐</span> HTTP</div>'
        + '<div class="log-panel-filter">'
        + '<select id="httpMethod" onchange="loadHttp()"><option value="">All Methods</option><option>GET</option><option>POST</option><option>PUT</option></select>'
        + '<select id="httpStatus" onchange="loadHttp()"><option value="">All Status</option><option value="2">2xx</option><option value="4">4xx</option><option value="5">5xx</option></select>'
        + '<input id="httpUrl" placeholder="URL..." style="width:80px" oninput="loadHttp()">'
        + '<input id="httpLimit" type="number" value="50" min="10" max="500" style="width:46px" onchange="loadHttp()">'
        + '<button class="panel-refresh" onclick="loadHttp()">↺</button>'
        + '<button class="panel-refresh" onclick="clearHttpPanel()" style="color:var(--red,#f85149);border-color:var(--red,#f85149);" title="Clear HTTP">🗑</button>'
        + '</div></div>'
        + '<div class="log-panel-scroll" id="httpScroll"><div class="log-empty">No traffic</div></div>'
        + '</div>'
        + '</div></div>';

    loadLogs();
    loadHttp();
    startSse();
    initVResizer();
}

function switchTab(tab) {
    activeTab = tab;
    setActiveTab(tab);
    _PS.save({ activeTab: tab });
    if (tab !== 'execution') stopProcStatsPoll();
    if (tab !== 'logs') closeSse();
    var s = schedules.find(function(x) { return x.id === selectedId; });
    if (!s) return;
    renderDetail(s);
}

function setActiveTab(tab) {
    document.querySelectorAll('.dtab').forEach(function(el) {
        el.classList.toggle('active', el.dataset.tab === tab);
    });
}

// ── Execution tab ─────────────────────────────────────────────────────────────

var _procStatsPoll = null;

function stopProcStatsPoll() {
    if (_procStatsPoll) { clearInterval(_procStatsPoll); _procStatsPoll = null; }
}

function renderExecution(s) {
    stopProcStatsPoll();
    var status    = getTaskStatus(s);
    var total     = parseInt(s.runs_total)   || 0;
    var done      = parseInt(s.runs_success) || 0;
    var fail      = total - done;
    var lastRun   = s.last_run  || '—';
    var lastExit  = s.last_exit || '—';
    var trigger   = triggerLabel(s);
    var isRunning = status === 'running';
    var isParallel = s.on_overlap === 'parallel';

    document.getElementById('detailBody').innerHTML =
        '<div class="detail-grid">'
        + '<div class="detail-section">'
        + '<div class="info-card-title">Execution</div>'
        + infoRow('Status',     '<span class="' + (isRunning ? 'green' : status === 'error' ? 'red' : '') + '">' + status + '</span>')
        + infoRow('Is Working', isRunning ? '<span class="green">Yes</span>' : 'No')
        + infoRow('Done',       '<span class="green">' + done + '</span>')
        + infoRow('Total',      total)
        + infoRow('Failed',     fail > 0 ? '<span class="red">' + fail + '</span>' : '0')
        + infoRow('Last Exit',  lastExit === '0' ? '<span class="green">0</span>' : lastExit === '-1' ? '<span class="red">-1</span>' : lastExit)
        + (isRunning && !isParallel
            ? infoRow('PID',    '<span id="procPid"    class="accent">—</span>')
            + infoRow('Uptime', '<span id="procUptime" class="accent">—</span>')
            + infoRow('Memory', '<span id="procMem"    class="accent">—</span>')
            : '')
        + '</div>'
        + '<div class="detail-section">'
        + '<div class="info-card-title">Scheduler</div>'
        + infoRow('Active',     s.enabled !== 'false' ? '<span class="green">True</span>' : '<span class="red">False</span>')
        + infoRow('Last Run',   lastRun)
        + infoRow('Period',     trigger)
        + infoRow('On Overlap', s.on_overlap || '—')
        + (isParallel ? infoRow('Max Threads', s.max_threads || '1') : '')
        + infoRow('Executor',   s.executor || '—')
        + '</div>'
        + (isRunning && isParallel
            ? '<div class="detail-section" id="instancesCard"><div class="info-card-title">Active instances</div><div id="instancesList">—</div></div>'
            + '<div class="detail-section" id="queueCard"><div class="info-card-title">Queue (pending)</div><div id="queueList">—</div>'
            + '<button class="btn sm" style="margin-top:4px" onclick="clearQueue(\'' + escHtml(s.id) + '\')">Clear queue</button></div>'
            : '')
        + '</div>';

    if (isRunning) {
        var id = s.id;

        function pollStats() {
            fetch('/scheduler/process-stats?id=' + encodeURIComponent(id))
                .then(function(r) { return r.json(); })
                .then(function(d) {
                    var elPid    = document.getElementById('procPid');
                    var elUptime = document.getElementById('procUptime');
                    var elMem    = document.getElementById('procMem');
                    if (!elPid) { stopProcStatsPoll(); return; }
                    if (!d.running) { stopProcStatsPoll(); return; }
                    elPid.textContent    = d.pid > 0 ? d.pid : '—';
                    elUptime.textContent = d.uptimeSec + 's';
                    elMem.textContent    = d.memoryMB + ' MB';
                }).catch(function() {});
        }

        function pollInstances() {
            fetch('/scheduler/instances?id=' + encodeURIComponent(id))
                .then(function(r) { return r.json(); })
                .then(function(list) {
                    var el = document.getElementById('instancesList');
                    if (!el) return;
                    el.innerHTML = (!list || !list.length) ? '(none)' : list.map(function(inst) {
                        return '<div style="display:flex;align-items:center;gap:6px;margin:2px 0">'
                            + '<span class="accent" style="font-family:monospace;font-size:10px">' + escHtml(inst.runId) + '</span>'
                            + '<span style="color:var(--text2)">' + inst.uptimeSec + 's</span>'
                            + '<span style="color:var(--text2)">' + inst.memoryMB + 'MB</span>'
                            + '<button class="btn stop sm" onclick="killOneInstance(\'' + escHtml(id) + '\',\'' + escHtml(inst.runId) + '\')">✕</button>'
                            + '</div>';
                    }).join('');
                }).catch(function() {});

            fetch('/scheduler/queue?id=' + encodeURIComponent(id))
                .then(function(r) { return r.json(); })
                .then(function(list) {
                    var el = document.getElementById('queueList');
                    if (!el) return;
                    var pending = (list || []).filter(function(q) { return q.status === 'pending'; });
                    el.textContent = pending.length ? pending.length + ' pending' : '(empty)';
                }).catch(function() {});
        }

        if (!isParallel) pollStats();
        if (isParallel)  pollInstances();
        _procStatsPoll = setInterval(function() {
            if (!isParallel) pollStats();
            if (isParallel)  pollInstances();
        }, 2000);
    }
}

function infoRow(key, val) {
    return '<div class="info-row"><span class="info-key">' + key + '</span><span class="info-val">' + val + '</span></div>';
}

// ── Settings tab ──────────────────────────────────────────────────────────────

function newSchedule() {
    selectedId = '__new__';
    formDirty  = true;
    renderList();
    var s = { id:'', name:'', executor:'python', script_path:'', args:'',
              enabled:'true', cron:'', interval_minutes:'0', fixed_time:'', on_overlap:'skip' };
    document.getElementById('detailHeader').style.display = 'none';
    document.getElementById('bottomPanels').style.display = 'none';
    document.getElementById('hResizer').style.display     = 'none';
    closeSseOutput();
    var dp = document.getElementById('detailPanel');
    dp.style.flex = ''; dp.style.height = '';
    activeTab = 'settings';
    renderSettings(s);
}

function renderSettings(s) {
    var id = s.id || '';
    console.log('[renderSettings] s.terminal_override=', s.terminal_override, 's.terminal_init_cmd=', s.terminal_init_cmd);
    document.getElementById('detailBody').innerHTML =
        '<div class="form-grid">'
        + '<div class="form-label">Name</div>'
        + '<input class="form-input" id="f_name" value="' + escHtml(s.name) + '">'
        + '<div class="form-label">Script / Task</div>'
        + '<input class="form-input" id="f_script_path" value="' + escHtml(s.script_path) + '" placeholder="/path/to/script or folder">'
        + '<div class="form-label">Executor</div>'
        + '<select class="form-input" id="f_executor">'
        + ['python','node','ts-node','exe','bat','bash','ps1','internal'].map(function(e) {
            return '<option ' + (s.executor === e ? 'selected' : '') + '>' + e + '</option>';
        }).join('') + '</select>'
        + '<div class="form-label">Arguments</div>'
        + '<input class="form-input" id="f_args" value="' + escHtml(s.args) + '">'
        + '<div class="form-label">Virtual Env (Python)</div>'
        + '<div style="display:flex;gap:4px;align-items:center;">'
        + '<input class="form-input" id="f_venv_path" value="' + escHtml(s.venv_path || '') + '" placeholder="Auto-detect or /path/to/venv" style="flex:1">'
        + '<button class="btn" onclick="detectVenv(\'' + escHtml(id) + '\')" style="padding:4px 8px;font-size:10px">Detect</button>'
        + '</div>'
        + '<div class="form-label">Enabled</div>'
        + '<select class="form-input" id="f_enabled">'
        + '<option value="true" '  + (s.enabled !== 'false' ? 'selected' : '') + '>Yes</option>'
        + '<option value="false" ' + (s.enabled === 'false'  ? 'selected' : '') + '>No</option>'
        + '</select>'
        + '<div class="form-section">Schedule</div>'
        + '<div class="form-label">Period</div>'
        + '<select class="form-input" id="b_period" onchange="builderUpdate()">'
        + ['OnDemand','EveryDay','EveryWeek','EveryMonth','Interval'].map(function(o) {
            return '<option value="' + o + '" ' + (schedBuilderPeriodFromSaved(s) === o ? 'selected' : '') + '>' + o + '</option>';
        }).join('') + '</select>'
        + '<div id="b_time_row" class="form-label" style="display:none">Time(s)</div>'
        + '<div id="b_time_wrap" style="display:none"><input class="form-input" id="b_times" placeholder="09:00, 14:30" oninput="builderUpdate()" value="' + escHtml(schedBuilderTimesFromSaved(s)) + '"></div>'
        + '<div id="b_weekday_row" class="form-label" style="display:none">Day of week</div>'
        + '<div id="b_weekday_wrap" style="display:none"><div style="display:flex;gap:4px;flex-wrap:wrap" id="b_weekdays">'
        + ['Mon','Tue','Wed','Thu','Fri','Sat','Sun'].map(function(d, i) {
            var bit = i === 6 ? 0 : i + 1;
            return '<div class="wd-btn' + (schedBuilderWeekdayActive(s, bit) ? ' wd-on' : '') + '" data-bit="' + bit + '" onclick="toggleWeekday(this)">' + d + '</div>';
        }).join('') + '</div></div>'
        + '<div id="b_monthday_row" class="form-label" style="display:none">Day of month</div>'
        + '<div id="b_monthday_wrap" style="display:none"><input class="form-input" id="b_monthday" type="number" min="1" max="28" placeholder="1" oninput="builderUpdate()" value="' + escHtml(schedBuilderMonthdayFromSaved(s)) + '"></div>'
        + '<div id="b_interval_row" class="form-label" style="display:none">Every (min)</div>'
        + '<div id="b_interval_wrap" style="display:none"><input class="form-input" id="b_interval" type="number" min="1" placeholder="30" oninput="builderUpdate()" value="' + escHtml(schedBuilderIntervalFromSaved(s)) + '"></div>'
        + '<div class="form-section">Result</div>'
        + '<div class="form-label">Cron</div>'
        + '<div class="trigger-row"><input class="form-input" id="f_cron" value="' + escHtml(s.cron) + '" placeholder="0 * * * *" oninput="builderClear()"><span style="color:var(--text2);font-size:10px;white-space:nowrap">min h dom mon dow</span></div>'
        + '<div class="form-label">Interval (min)</div>'
        + '<input class="form-input" id="f_interval_minutes" type="number" min="0" value="' + (s.interval_minutes || 0) + '" oninput="builderClear()">'
        + '<div class="form-label">Fixed time</div>'
        + '<input class="form-input" id="f_fixed_time" value="' + escHtml(s.fixed_time) + '" placeholder="14:30" oninput="builderClear()">'
        + '<div class="form-section">Overlap</div>'
        + '<div class="form-label">On overlap</div>'
        + '<select class="form-input" id="f_on_overlap" onchange="onOverlapChanged()">'
        + '<option value="skip"         ' + (s.on_overlap === 'skip'         ? 'selected' : '') + '>Skip</option>'
        + '<option value="parallel"     ' + (s.on_overlap === 'parallel'     ? 'selected' : '') + '>Parallel</option>'
        + '<option value="kill_restart" ' + (s.on_overlap === 'kill_restart' ? 'selected' : '') + '>Kill &amp; restart</option>'
        + '</select>'
        + '<div class="form-label" id="f_max_threads_label" style="' + (s.on_overlap === 'parallel' ? '' : 'display:none') + '">Max threads</div>'
        + '<input class="form-input" id="f_max_threads" type="number" min="1" value="' + (s.max_threads || '1') + '" style="' + (s.on_overlap === 'parallel' ? '' : 'display:none') + '">'
        + '<div class="form-section">Terminal (per-task override)</div>'
        + '<div class="form-label">Terminal app</div>'
        + '<select class="form-input" id="f_terminal_override" onchange="onTerminalOverrideChange()">'
        + '<option value="">Use global (' + _globalTerminalLabel() + ')</option>'
        + '<option value="cmd"'         + (s.terminal_override === "cmd"         ? ' selected' : '') + '>cmd</option>'
        + '<option value="powershell"'  + (s.terminal_override === "powershell"  ? ' selected' : '') + '>PowerShell</option>'
        + '<option value="gitbash"'     + (s.terminal_override === "gitbash"     ? ' selected' : '') + '>Git Bash</option>'
        + '<option value="third_party"' + (s.terminal_override === "third_party" ? ' selected' : '') + '>Third Party</option>'
        + '</select>'
        + '<div class="form-label">Init command</div>'
        + '<input class="form-input" id="f_terminal_init_cmd" value="' + escHtml(s.terminal_init_cmd || '') + '" placeholder="e.g. conda activate myenv">'
        + '<div class="form-label"></div>'
        + '<div id="f_terminal_override_note" style="font-size:10px;color:var(--red,#f85149);padding-top:3px;"></div>'
        + '<div class="form-actions"><button class="btn primary" onclick="saveSchedule(\'' + escHtml(id) + '\')">Save</button></div>'
        + '</div>';
    builderUpdate();
    _checkTerminalOverrideNote();
    // DIAG
    console.log('[renderSettings] done, checking terminal section:', document.getElementById('f_terminal_override'));
}

// ── Schedule builder ──────────────────────────────────────────────────────────

function schedBuilderPeriodFromSaved(s) {
    if (s.interval_minutes && parseInt(s.interval_minutes) > 0) return 'Interval';
    if (!s.cron) return 'OnDemand';
    var parts = s.cron.trim().split(/\s+/);
    if (parts.length !== 5) return 'OnDemand';
    var h = parts[1], dom = parts[2], dow = parts[4];
    if (dow !== '*') return 'EveryWeek';
    if (dom !== '*' && dom !== '?') return 'EveryMonth';
    if (h !== '*') return 'EveryDay';
    return 'OnDemand';
}
function schedBuilderTimesFromSaved(s) {
    if (!s.cron) return '';
    var parts = s.cron.trim().split(/\s+/);
    if (parts.length !== 5) return '';
    var min = parts[0], h = parts[1];
    if (h === '*' || min === '*') return '';
    var mins = min.split(','), hours = h.split(',');
    if (mins.length === 1 && hours.length >= 1)
        return hours.map(function(hh) { return pad2(hh) + ':' + pad2(mins[0]); }).join(', ');
    return '';
}
function schedBuilderWeekdayActive(s, bit) {
    if (!s.cron) return false;
    var parts = s.cron.trim().split(/\s+/);
    if (parts.length !== 5) return false;
    return parts[4].split(',').indexOf(String(bit)) >= 0;
}
function schedBuilderMonthdayFromSaved(s) {
    if (!s.cron) return '';
    var parts = s.cron.trim().split(/\s+/);
    if (parts.length !== 5) return '';
    var dom = parts[2];
    return (dom !== '*' && dom !== '?') ? dom : '';
}
function schedBuilderIntervalFromSaved(s) {
    return (s.interval_minutes && parseInt(s.interval_minutes) > 0) ? s.interval_minutes : '';
}
function builderClear() {
    var el = document.getElementById('b_period');
    if (el) { el.value = 'OnDemand'; builderShowRows('OnDemand'); }
}
function toggleWeekday(el) { el.classList.toggle('wd-on'); builderUpdate(); }
function builderShowRows(period) {
    var showTime     = ['EveryDay','EveryWeek','EveryMonth'].indexOf(period) >= 0;
    var showWeekday  = period === 'EveryWeek';
    var showMonthday = period === 'EveryMonth';
    var showInterval = period === 'Interval';
    setRowVisible('b_time_row',     showTime);     setRowVisible('b_time_wrap',     showTime);
    setRowVisible('b_weekday_row',  showWeekday);  setRowVisible('b_weekday_wrap',  showWeekday);
    setRowVisible('b_monthday_row', showMonthday); setRowVisible('b_monthday_wrap', showMonthday);
    setRowVisible('b_interval_row', showInterval); setRowVisible('b_interval_wrap', showInterval);
}
function setRowVisible(id, show) { var el = document.getElementById(id); if (el) el.style.display = show ? '' : 'none'; }
function builderUpdate() {
    var period = (document.getElementById('b_period') || {}).value || 'OnDemand';
    builderShowRows(period);
    if (period === 'OnDemand') return;
    if (period === 'Interval') {
        var mins = parseInt((document.getElementById('b_interval') || {}).value || '0');
        setCronField(''); setIntervalField(mins > 0 ? mins : 0); return;
    }
    var timesRaw  = ((document.getElementById('b_times') || {}).value || '').trim();
    var timesList = timesRaw ? timesRaw.split(',').map(function(t) { return t.trim(); }).filter(Boolean) : ['00:00'];
    if (timesList.length > 1) {
        var minutes = timesList.map(function(t) { var p = parseHHMM(t); return p.h * 60 + p.m; }).sort(function(a,b){return a-b;});
        var gaps = [];
        for (var i = 1; i < minutes.length; i++) gaps.push(minutes[i] - minutes[i-1]);
        var minGap = gaps.reduce(function(a,b){return Math.min(a,b);}, gaps[0] || 60);
        setCronField(''); setIntervalField(Math.max(1, minGap)); return;
    }
    var t = parseHHMM(timesList[0]);
    var cron = '';
    if (period === 'EveryDay') cron = t.m + ' ' + t.h + ' * * *';
    else if (period === 'EveryWeek') {
        var days = [];
        document.querySelectorAll('#b_weekdays .wd-btn.wd-on').forEach(function(b) { days.push(b.getAttribute('data-bit')); });
        cron = t.m + ' ' + t.h + ' * * ' + (days.length ? days.join(',') : '*');
    } else if (period === 'EveryMonth') {
        var dom = parseInt((document.getElementById('b_monthday') || {}).value || '1');
        if (!dom || dom < 1 || dom > 28) dom = 1;
        cron = t.m + ' ' + t.h + ' ' + dom + ' * *';
    }
    setCronField(cron); setIntervalField(0);
}
function setCronField(v)     { var el = document.getElementById('f_cron');             if (el) el.value = v; }
function setIntervalField(v) { var el = document.getElementById('f_interval_minutes'); if (el) el.value = v || 0; }
function parseHHMM(str)      { var p = (str || '00:00').split(':'); return { h: parseInt(p[0])||0, m: parseInt(p[1])||0 }; }
function pad2(n)             { return String(parseInt(n)||0).padStart(2,'0'); }

// ── Output tab ────────────────────────────────────────────────────────────────

function renderOutput(s) {
    // output is now in bottom panel, nothing to render in detailBody for output
    // kept for compat - unused
}

function renderOutputLines(text) {
    if (!text || !text.trim()) return '<div class="out-line empty">(no output yet)</div>';
    var normalized = text.replace(/\\n/g, '\n');
    return normalized.split('\n').map(function(line) {
        var level = 'INFO';
        if (/\[ERROR\]|\[ERR\]/i.test(line))        level = 'ERROR';
        else if (/\[WARNING\]|\[WARN\]/i.test(line)) level = 'WARNING';
        else if (/\[DEBUG\]/i.test(line))            level = 'DEBUG';
        var timestamp = new Date().toLocaleString('en-US', {hour12: false});
        return '<div class="out-line ' + level + '"><span class="out-line-text">' + escHtml(line) + '</span><span class="out-line-timestamp">' + timestamp + '</span></div>';
    }).join('');
}

// ── Output box (bottom panel) ─────────────────────────────────────────────────

function _getOutputBox()  { return document.getElementById('outputBox'); }
function _getLiveBadge()  { return document.getElementById('liveBadgeBottom'); }

function loadOutput(id) {
    // load last saved output from DB into bottom box
    fetch('/scheduler/output?id=' + encodeURIComponent(id))
        .then(function(r) { return r.json(); })
        .then(function(data) {
            var box = _getOutputBox();
            if (!box) return;
            var text = (data.output || '').replace(/\\n/g, '\n');
            if (!text.trim()) {
                box.innerHTML = '<div class="out-line empty">(no output yet)</div>';
            } else {
                var timestamp = new Date().toLocaleString('en-US', {hour12: false});
                box.innerHTML = text.split('\n').map(function(line) {
                    var level = 'INFO';
                    if (/\[ERROR\]|\[ERR\]/i.test(line))        level = 'ERROR';
                    else if (/\[WARNING\]|\[WARN\]/i.test(line)) level = 'WARNING';
                    else if (/\[DEBUG\]/i.test(line))            level = 'DEBUG';
                    return '<div class="out-line ' + level + '"><span class="out-line-text">' + escHtml(line) + '</span><span class="out-line-timestamp">' + timestamp + '</span></div>';
                }).join('');
                box.scrollTop = box.scrollHeight;
            }
        }).catch(function() {});
}

function reloadOutput() {
    if (selectedId && selectedId !== '__new__') loadOutput(selectedId);
}

function startSseOutput(id) {
    if (_sseOutput) { _sseOutput.close(); _sseOutput = null; }

    _sseOutput = new EventSource('/scheduler/output/stream?id=' + encodeURIComponent(id));

    _sseOutput.addEventListener('output', function(e) {
        try {
            var d     = JSON.parse(e.data);
            var box   = _getOutputBox();
            var badge = _getLiveBadge();
            if (!box) return;
            if (d.done) { if (badge) badge.style.display = 'none'; return; }
            if (d.clear) { box.innerHTML = ''; }
            var empty = box.querySelector('.out-line.empty');
            if (empty) empty.remove();
            var level       = (d.level || 'INFO').toUpperCase();
            var atBottom    = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
            var line        = d.line || '';
            var last        = box.lastElementChild;
            var replaceLast = !!d.replace_last;
            var timestamp   = new Date().toLocaleString('en-US', {hour12: false});
            function progressPrefix(s) { return s.replace(/[\d%\[\]]+.*$/, '').trim(); }
            var sameProgress = last && last.classList.contains('out-line') && !last.classList.contains('empty')
                && progressPrefix(line).length > 3 && progressPrefix(line) === progressPrefix(last.querySelector('.out-line-text') ? last.querySelector('.out-line-text').textContent : last.textContent);
            if (replaceLast || sameProgress) {
                last.className   = 'out-line ' + level;
                var textSpan = last.querySelector('.out-line-text');
                var timeSpan = last.querySelector('.out-line-timestamp');
                if (textSpan) textSpan.textContent = line;
                else {
                    last.innerHTML = '<span class="out-line-text">' + escHtml(line) + '</span><span class="out-line-timestamp">' + timestamp + '</span>';
                }
                if (timeSpan) timeSpan.textContent = timestamp;
            } else {
                box.insertAdjacentHTML('beforeend', '<div class="out-line ' + level + '"><span class="out-line-text">' + escHtml(line) + '</span><span class="out-line-timestamp">' + timestamp + '</span></div>');
            }
            if (atBottom) box.scrollTop = box.scrollHeight;
            if (badge) badge.style.display = 'inline-block';
        } catch(err) {}
    });

    _sseOutput.addEventListener('done', function() {
        var badge = _getLiveBadge();
        if (badge) badge.style.display = 'none';
        _sseOutput.close(); _sseOutput = null;
    });

    _sseOutput.onerror = function() {
        var badge = _getLiveBadge();
        if (badge) badge.style.display = 'none';
        _sseOutput.close(); _sseOutput = null;
    };
}

async function clearOutputBottom() {
    if (!selectedId || selectedId === '__new__') return;
    await fetch('/scheduler/clear-output', {
        method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({ id: selectedId })
    });
    var box = _getOutputBox();
    if (box) box.innerHTML = '<div class="out-line empty">(no output yet)</div>';
    var s = schedules.find(function(x) { return x.id === selectedId; });
    if (s) s.last_output = '';
}

function clearOutputPoll() {
    if (outputPoll)  { clearTimeout(outputPoll); outputPoll = null; }
    if (_sseOutput)  { _sseOutput.close(); _sseOutput = null; }
}

// ── Log / HTTP panels ─────────────────────────────────────────────────────────

function appendLogRow(row) {
    var el = document.getElementById('logsScroll');
    if (!el) return;
    var empty = el.querySelector('.log-empty');
    if (empty) empty.remove();
    var lvl    = (row.level || 'INFO').toUpperCase();
    var cls    = lvl === 'WARNING' ? 'WARN' : lvl;
    var time   = (row.timestamp || '').slice(11, 19);
    var acc    = (row.account && row.account !== '-') ? row.account : '';
    var caller = (row.caller  && row.caller  !== '-') ? row.caller  : '';
    el.insertAdjacentHTML('beforeend',
        '<div class="log-row">'
        + '<span class="log-time">'   + escHtml(time)   + '</span>'
        + '<span class="log-level '  + cls + '">' + lvl.slice(0,4) + '</span>'
        + '<span class="log-acc">'   + escHtml(acc)    + '</span>'
        + '<span class="log-caller">' + escHtml(caller) + '</span>'
        + '<span class="log-msg">'   + escHtml(row.message || '') + '</span>'
        + '</div>');
    el.scrollTop = el.scrollHeight;
}

function appendHttpRow(row) {
    var el = document.getElementById('httpScroll');
    if (!el) return;
    var empty = el.querySelector('.log-empty');
    if (empty) empty.remove();
    var sc  = row.statusCode || 0;
    var cls = sc >= 500 ? 's5xx' : sc >= 400 ? 's4xx' : 's2xx';
    var host = '';
    try { host = new URL(row.url || '').host; } catch(e) { host = row.url || ''; }
    var dur = row.durationMs != null ? row.durationMs + 'ms' : '';
    el.insertAdjacentHTML('beforeend',
        '<div class="http-row">'
        + '<span class="http-method">' + escHtml(row.method || '') + '</span>'
        + '<span class="http-status ' + cls + '">' + sc + '</span>'
        + '<span class="http-url" title="' + escHtml(row.url||'') + '">' + escHtml(host) + '</span>'
        + '<span class="http-dur">' + escHtml(dur) + '</span>'
        + '</div>');
    el.scrollTop = el.scrollHeight;
}

async function clearLogsPanel() {
    if (!curTaskId) return;
    if (!(await Dialog.confirm('Clear ALL logs?'))) return;
    try {
        await fetch('/clear', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({task_id: curTaskId}) });
        if (selectedId && selectedId !== '__new__') {
            await fetch('/scheduler/clear-output', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id: selectedId}) });
            var s = schedules.find(function(x) { return x.id === selectedId; });
            if (s) s.last_output = '';
        }
        document.getElementById('logsScroll').innerHTML = '<div class="log-empty">No logs</div>';
    } catch(e) { console.error('Clear logs failed:', e); }
}

async function clearHttpPanel() {
    if (!curTaskId) return;
    if (!(await Dialog.confirm('Clear HTTP logs for task: ' + curTaskId + '?'))) return;
    try {
        await fetch('/clear-http-logs-by-task', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({task_id: curTaskId}) });
        document.getElementById('httpScroll').innerHTML = '<div class="log-empty">No traffic</div>';
    } catch(e) { console.error('Clear HTTP logs failed:', e); }
}

async function loadLogs() {
    if (!curTaskId) return;
    var level   = document.getElementById('logLevel').value;
    var limit   = document.getElementById('logLimit').value || 50;
    var session = '';
    var url = '/logs?limit=' + limit + '&task_id=' + encodeURIComponent(curTaskId)
        + (level   ? '&level='   + encodeURIComponent(level)   : '')
        + (session ? '&session=' + encodeURIComponent(session)  : '');
    try {
        var res  = await fetch(url);
        var data = await res.json();
        var el   = document.getElementById('logsScroll');
        if (!data.length) { await showOutputFallback(el); return; }
        el.innerHTML = data.slice().reverse().map(function(row) {
            var lvl    = (row.level || 'INFO').toUpperCase();
            var cls    = lvl === 'WARNING' ? 'WARN' : lvl;
            var time   = (row.timestamp || '').slice(11, 19);
            var acc    = (row.account && row.account !== '-') ? row.account : '';
            var caller = (row.caller  && row.caller  !== '-') ? row.caller  : '';
            return '<div class="log-row">'
                + '<span class="log-time">'   + escHtml(time)   + '</span>'
                + '<span class="log-level '  + cls + '">' + lvl.slice(0,4) + '</span>'
                + '<span class="log-acc">'   + escHtml(acc)    + '</span>'
                + '<span class="log-caller">' + escHtml(caller) + '</span>'
                + '<span class="log-msg">'   + escHtml(row.message || '') + '</span>'
                + '</div>';
        }).join('');
        el.scrollTop = el.scrollHeight;
    } catch(e) {}
}

async function showOutputFallback(el) {
    if (!selectedId || selectedId === '__new__') { el.innerHTML = '<div class="log-empty">No logs</div>'; return; }
    try {
        var res  = await fetch('/scheduler/output?id=' + encodeURIComponent(selectedId));
        var data = await res.json();
        var text = (data && data.output ? data.output.trim() : '').replace(/\\n/g, '\n');
        if (!text) { el.innerHTML = '<div class="log-empty">No logs</div>'; return; }
        el.innerHTML = '<div class="log-row" style="opacity:0.45;font-size:9px;padding:2px 8px;border-bottom:1px solid var(--border)">'
            + '<span class="log-msg">— no logger output, showing task stdout —</span></div>'
            + text.split('\n').map(function(line) {
                return '<div class="log-row"><span class="log-msg" style="white-space:pre-wrap">' + escHtml(line) + '</span></div>';
            }).join('');
        el.scrollTop = el.scrollHeight;
    } catch(e) { el.innerHTML = '<div class="log-empty">No logs</div>'; }
}

async function loadHttp() {
    if (!curTaskId) return;
    var method = document.getElementById('httpMethod').value;
    var status = document.getElementById('httpStatus').value;
    var urlFlt = document.getElementById('httpUrl').value;
    var limit  = document.getElementById('httpLimit').value || 50;
    var url = '/http-logs?limit=' + limit + '&task_id=' + encodeURIComponent(curTaskId)
        + (method ? '&method=' + encodeURIComponent(method) : '')
        + (status ? '&status=' + encodeURIComponent(status) : '')
        + (urlFlt ? '&url='    + encodeURIComponent(urlFlt) : '');
    try {
        var res  = await fetch(url);
        var data = await res.json();
        var el   = document.getElementById('httpScroll');
        if (!data.length) { el.innerHTML = '<div class="log-empty">No traffic</div>'; return; }
        el.innerHTML = data.map(function(row) {
            var sc  = row.statusCode || 0;
            var cls = sc >= 500 ? 's5xx' : sc >= 400 ? 's4xx' : 's2xx';
            var host = '';
            try { host = new URL(row.url || '').host; } catch(e) { host = row.url || ''; }
            var dur = row.durationMs != null ? row.durationMs + 'ms' : '';
            return '<div class="http-row">'
                + '<span class="http-method">' + escHtml(row.method || '') + '</span>'
                + '<span class="http-status ' + cls + '">' + sc + '</span>'
                + '<span class="http-url" title="' + escHtml(row.url||'') + '">' + escHtml(host) + '</span>'
                + '<span class="http-dur">' + escHtml(dur) + '</span>'
                + '</div>';
        }).join('');
        el.scrollTop = el.scrollHeight;
    } catch(e) {}
}

// ── CRUD ──────────────────────────────────────────────────────────────────────

function _globalTerminalLabel() {
    return _globalTerminal || 'cmd';
}

function onTerminalOverrideChange() {
    _checkTerminalOverrideNote();
}

function _checkTerminalOverrideNote() {
    var sel  = document.getElementById('f_terminal_override');
    var note = document.getElementById('f_terminal_override_note');
    if (!sel || !note) return;
    var val = sel.value;
    if (val === 'gitbash') {
        fetch('/scheduler/terminal-config')
            .then(function(r) { return r.json(); })
            .then(function(cfg) {
                note.textContent = cfg.gitbash_found ? '' : '⚠ Git Bash not found in default locations';
            }).catch(function() {});
    } else {
        note.textContent = '';
    }
}

function loadTerminalConfig() {
    fetch('/scheduler/terminal-config')
        .then(function(r) { return r.json(); })
        .then(function(cfg) {
            _globalTerminal = cfg.terminal || 'cmd';
            var sel  = document.getElementById('f_terminal');
            var path = document.getElementById('f_terminal_path');
            if (sel) { sel.value = cfg.terminal || 'cmd'; }
            if (path) path.value = cfg.terminal_path || '';
            _updateTerminalUI(cfg.terminal || 'cmd', cfg.gitbash_found || '');
        }).catch(function() {});
}

function _updateTerminalUI(term, gitbashFound) {
    var pathLabel = document.getElementById('f_terminal_path_row');
    var pathInput = document.getElementById('f_terminal_path');
    var note      = document.getElementById('f_terminal_note');
    var show = term === 'third_party';
    if (pathLabel) pathLabel.style.display = show ? '' : 'none';
    if (pathInput) pathInput.style.display = show ? '' : 'none';
    if (note) {
        if (term === 'gitbash' && !gitbashFound)
            note.textContent = '⚠ Git Bash not found in default locations';
        else
            note.textContent = '';
    }
}

async function saveTerminalConfig() {
    var term = (document.getElementById('f_terminal') || {}).value || 'cmd';
    var path = ((document.getElementById('f_terminal_path') || {}).value || '').trim();
    var cfg  = await fetch('/scheduler/terminal-config').then(function(r) { return r.json(); });

    if (term === 'gitbash' && !cfg.gitbash_found) {
        Dialog.error('Git Bash not found. Install Git for Windows or use Third Party.');
        return;
    }
    if (term === 'third_party' && !path) {
        Dialog.error('Enter path to terminal executable.');
        return;
    }
    var lines = 'TERMINAL = "' + term + '"';
    if (term === 'third_party') lines += '\nTERMINAL_PATH = "' + path + '"';
    Dialog.alert('Copy to config.py:\n\n' + lines, '⌨ Terminal config');
}

function openAiForTask(id) {
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    var scriptPath = s.script_path || '';
    AiPanel.setCwd(scriptPath);
    AiPanel.open();
}

async function openInTerminal(id) {
    try {
        var res  = await fetch('/scheduler/open-terminal?id=' + encodeURIComponent(id));
        var data = await res.json();
        if (!data.ok) Dialog.error(data.error || 'Cannot open terminal');
    } catch(e) { Dialog.error(e.message); }
}

async function saveSchedule(existingId) {
    var maxThreadsEl = document.getElementById('f_max_threads');
    var payload = {
        id:               existingId || undefined,
        name:             document.getElementById('f_name').value.trim(),
        executor:         document.getElementById('f_executor').value,
        script_path:      document.getElementById('f_script_path').value.trim(),
        args:             document.getElementById('f_args').value.trim(),
        venv_path:        document.getElementById('f_venv_path').value.trim(),
        enabled:          document.getElementById('f_enabled').value,
        cron:             document.getElementById('f_cron').value.trim(),
        interval_minutes: document.getElementById('f_interval_minutes').value,
        fixed_time:       document.getElementById('f_fixed_time').value.trim(),
        on_overlap:       document.getElementById('f_on_overlap').value,
        max_threads:      maxThreadsEl ? maxThreadsEl.value : '1',
        terminal_override: (document.getElementById('f_terminal_override') || {}).value || '',
        terminal_init_cmd: (document.getElementById('f_terminal_init_cmd') || {}).value || '',
    };
    var res  = await fetch('/scheduler/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) });
    var data = await res.json();
    if (data.ok) { selectedId = data.id; formDirty = false; await loadList(); selectRow(data.id); }
    else Dialog.error(data.error || 'Save failed');
}

async function detectVenv(id) {
    if (!id) return;
    var res = await fetch('/scheduler/detect-venv?id=' + encodeURIComponent(id));
    var data = await res.json();
    if (!data.ok) {
        Dialog.error('Failed to detect venv');
        return;
    }
    if (!data.venvs || data.venvs.length === 0) {
        Dialog.info('No virtual environments found in script folder.\n\nSearched for: .venv, venv, env');
        return;
    }
    // Use first detected venv
    var venv = data.venvs[0];
    var input = document.getElementById('f_venv_path');
    if (input) {
        input.value = venv.path;
        Dialog.info('Detected venv: ' + venv.name + '\n\nPath: ' + venv.path);
    }
}

function _nextDuplicateName(name, existingNames) {
    var match = name.match(/^(.*)\s\((\d+)\)$/);
    var base  = match ? match[1] : name;
    var n     = match ? parseInt(match[2]) : 1;
    var candidate;
    do { n++; candidate = base + ' (' + n + ')'; } while (existingNames.indexOf(candidate) >= 0);
    return candidate;
}

async function duplicateSchedule(id) {
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    var newName = _nextDuplicateName(s.name || 'task', schedules.map(function(x) { return x.name || ''; }));
    var res  = await fetch('/scheduler/save', {
        method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ name:newName, executor:s.executor, script_path:s.script_path, args:s.args,
            enabled:'false', cron:s.cron, interval_minutes:s.interval_minutes,
            fixed_time:s.fixed_time, on_overlap:s.on_overlap, max_threads:s.max_threads })
    });
    var data = await res.json();
    if (!data.ok) { Dialog.error(data.error || 'Duplicate: save failed'); return; }
    try {
        var pRes  = await fetch('/scheduler/payload?id=' + encodeURIComponent(id));
        var pData = await pRes.json();
        if (pData.schema || pData.values)
            await fetch('/scheduler/payload', { method:'POST', headers:{'Content-Type':'application/json'},
                body: JSON.stringify({ id:data.id, schema:pData.schema, values:pData.values }) });
    } catch(e) {}
    await loadList();
    selectRow(data.id);
}

async function deleteSchedule(id, name) {
    if (!(await Dialog.confirm('Eliminate "' + (name||id) + '"?', '✕ Eliminate', true))) return;
    await fetch('/scheduler/delete', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
    selectedId = null;
    closeSse();
    document.getElementById('detailHeader').style.display  = 'none';
    document.getElementById('bottomPanels').style.display  = 'none';
    document.getElementById('hResizer').style.display      = 'none';
    closeSseOutput();
    var dp = document.getElementById('detailPanel');
    dp.style.flex = ''; dp.style.height = '';
    document.getElementById('detailBody').innerHTML = '<div class="empty-state">Select a schedule or create a new one</div>';
    await loadList();
}

async function exportPayload(id) {
    try {
        var res  = await fetch('/scheduler/payload?id=' + encodeURIComponent(id));
        var data = await res.json();
        await navigator.clipboard.writeText(JSON.stringify({ schema:data.schema, values:data.values }, null, 2));
        Dialog.alert('Payload JSON copied to clipboard.', 'Exported');
    } catch(e) { Dialog.error(e.message); }
}

async function openScriptFile(filePath) {
    try {
        var res  = await fetch('/scheduler/open-file?path=' + encodeURIComponent(filePath));
        var data = await res.json();
        if (!data.ok) Dialog.error(data.error || 'Cannot open file');
    } catch(e) { Dialog.error(e.message); }
}

async function openScriptFolder(filePath) {
    try {
        var res  = await fetch('/scheduler/open-folder?path=' + encodeURIComponent(filePath));
        var data = await res.json();
        if (!data.ok) Dialog.error(data.error || 'Cannot open folder');
    } catch(e) { Dialog.error(e.message); }
}

async function toggleEnabled(id, current) {
    var newVal = current === 'true' ? 'false' : 'true';
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    await fetch('/scheduler/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(Object.assign({}, s, {enabled: newVal})) });
    await loadList();
}

async function runNow(id) {
    await fetch('/scheduler/run', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
    await loadList();
    startSseOutput(id);
}

async function buildCsx(id) {
    var btn = event.target;
    btn.disabled = true; btn.textContent = '⏳...';
    try {
        var res  = await fetch('/scheduler/build', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
        var data = await res.json();
        if (data.ok) { btn.textContent = '✅ OK'; btn.style.borderColor = '#3fb950'; btn.style.color = '#3fb950'; }
        else { btn.textContent = '❌ Err'; btn.style.borderColor = '#f85149'; btn.style.color = '#f85149'; await Dialog.alert(data.errors.join('\n'), '🔨 Check errors'); }
    } catch(e) { btn.textContent = '❌'; }
    finally { setTimeout(function() { btn.disabled = false; btn.textContent = '🔨 Check'; btn.style.borderColor = '#a371f7'; btn.style.color = '#a371f7'; }, 3000); }
}

async function stopNow(id) {
    var res       = await fetch('/scheduler/instances?id=' + encodeURIComponent(id));
    var instances = await res.json().catch(function() { return []; }) || [];
    if (instances.length === 0) {
        await fetch('/scheduler/stop', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
    } else if (instances.length === 1) {
        if (!(await Dialog.confirm('Kill running instance?', '■ Interrupt', true))) return;
        await fetch('/scheduler/kill-instance', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id, runId:instances[0].runId}) });
    } else {
        var chosen = await _pickInstanceToKill(id, instances);
        if (chosen === null) return;
        var url = chosen === '__all__' ? '/scheduler/stop' : '/scheduler/kill-instance';
        var body = chosen === '__all__' ? {id:id} : {id:id, runId:chosen};
        await fetch(url, { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
    }
    await loadList();
}

function _pickInstanceToKill(id, instances) {
    return new Promise(function(resolve) {
        var overlay = document.getElementById('killPickOverlay');
        if (!overlay) {
            overlay = document.createElement('div');
            overlay.id = 'killPickOverlay';
            overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;z-index:9999';
            document.body.appendChild(overlay);
        }
        overlay.innerHTML =
            '<div style="background:var(--bg1);border:1px solid var(--border);border-radius:8px;padding:20px;min-width:320px;max-width:480px">'
            + '<div style="font-weight:600;margin-bottom:12px">Select instance to kill</div>'
            + instances.map(function(inst) {
                return '<div style="display:flex;align-items:center;gap:8px;margin:4px 0;padding:4px 0;border-bottom:1px solid var(--border)">'
                    + '<span style="font-family:monospace;font-size:11px;flex:1;color:var(--accent)">' + escHtml(inst.runId) + '</span>'
                    + '<span style="color:var(--text2);font-size:11px">' + inst.uptimeSec + 's</span>'
                    + '<button class="btn stop sm" onclick="_killPickResolve(\'' + escHtml(inst.runId) + '\')">■ Kill</button>'
                    + '</div>';
            }).join('')
            + '<div style="display:flex;justify-content:flex-end;gap:8px;margin-top:12px">'
            + '<button class="btn sm" onclick="_killPickResolve(null)">Cancel</button>'
            + '<button class="btn danger sm" onclick="_killPickResolve(\'__all__\')">■ Kill all</button>'
            + '</div></div>';
        overlay.style.display = 'flex';
        window._killPickResolve = function(val) { overlay.style.display = 'none'; window._killPickResolve = null; resolve(val); };
    });
}

async function killOneInstance(id, runId) {
    await fetch('/scheduler/kill-instance', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id, runId:runId}) });
}

async function clearQueue(id) {
    await fetch('/scheduler/clear-queue', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
}

function onOverlapChanged() {
    var val   = document.getElementById('f_on_overlap').value;
    var label = document.getElementById('f_max_threads_label');
    var input = document.getElementById('f_max_threads');
    if (!label || !input) return;
    var show = val === 'parallel';
    label.style.display = show ? '' : 'none';
    input.style.display = show ? '' : 'none';
}

// ── Resizers ──────────────────────────────────────────────────────────────────

function restoreLayout() {
    var st = _PS.load();
    if (st.listW) document.getElementById('tasksPanel').style.width = st.listW + 'px';
    if (st.detailH && st.bottomH) {
        var tp = document.getElementById('detailPanel');
        var bp = document.getElementById('bottomPanels');
        tp.style.flex = 'none'; tp.style.height = st.detailH + 'px'; bp.style.height = st.bottomH + 'px';
    }
    if (st.logsFlexPct) {
        document.getElementById('logPanelLogs').style.flex = '0 0 ' + st.logsFlexPct + '%';
        document.getElementById('logPanelHttp').style.flex = '1 1 0';
    }
    if (st.activeTab) { activeTab = st.activeTab; setActiveTab(st.activeTab); }
}

function initResizer() {
    var resizer = document.getElementById('resizer');
    var panel   = document.getElementById('tasksPanel');
    var dragging = false, startX, startW;
    resizer.addEventListener('mousedown', function(e) { dragging = true; startX = e.clientX; startW = panel.offsetWidth; document.body.style.cursor = 'col-resize'; document.body.style.userSelect = 'none'; });
    document.addEventListener('mousemove', function(e) { if (!dragging) return; panel.style.width = Math.max(180, startW + (e.clientX - startX)) + 'px'; });
    document.addEventListener('mouseup',   function()  { if (dragging) { dragging = false; document.body.style.cursor = ''; document.body.style.userSelect = ''; _PS.save({listW: panel.offsetWidth}); } });
}

function initHResizer() {
    var resizer  = document.getElementById('hResizer');
    var topPanel = document.getElementById('detailPanel');
    var botPanel = document.getElementById('bottomPanels');
    if (!resizer) return;
    var dragging = false, startY, startTop, startBottom;
    resizer.addEventListener('mousedown', function(e) {
        dragging = true; startY = e.clientY;
        startTop = topPanel.offsetHeight; startBottom = botPanel.offsetHeight;
        document.body.style.cursor = 'row-resize'; document.body.style.userSelect = 'none';
        e.preventDefault();
    });
    document.addEventListener('mousemove', function(e) {
        if (!dragging) return;
        var dy = e.clientY - startY;
        topPanel.style.flex   = 'none';
        topPanel.style.height = Math.max(100, startTop + dy) + 'px';
        botPanel.style.height = Math.max(60, startBottom - dy) + 'px';
    });
    document.addEventListener('mouseup', function() {
        if (dragging) {
            dragging = false;
            document.body.style.cursor = ''; document.body.style.userSelect = '';
            _PS.save({detailH: topPanel.offsetHeight, bottomH: botPanel.offsetHeight});
        }
    });
}

function initVResizer() {
    var resizer    = document.getElementById('vResizer');
    var leftPanel  = document.getElementById('logPanelLogs');
    var rightPanel = document.getElementById('logPanelHttp');
    var container  = document.getElementById('bottomPanels');
    if (!resizer) return;
    var dragging = false;
    resizer.addEventListener('mousedown', function(e) { dragging = true; document.body.style.cursor = 'col-resize'; document.body.style.userSelect = 'none'; e.preventDefault(); });
    document.addEventListener('mousemove', function(e) { if (!dragging) return; var rect = container.getBoundingClientRect(); var pct = Math.max(15, Math.min(85, ((e.clientX - rect.left) / rect.width) * 100)); leftPanel.style.flex = '0 0 ' + pct + '%'; rightPanel.style.flex = '1 1 0'; });
    document.addEventListener('mouseup',   function()  { if (dragging) { dragging = false; document.body.style.cursor = ''; document.body.style.userSelect = ''; var pct = parseFloat(leftPanel.style.flexBasis) || (leftPanel.offsetWidth / container.offsetWidth * 100); _PS.save({logsFlexPct: Math.round(pct)}); } });
}

// ── Dialog ────────────────────────────────────────────────────────────────────

var Dialog = {
    _resolve: null,
    _open: function(icon, title, msg, mode) {
        document.getElementById('dialogIcon').textContent  = icon;
        document.getElementById('dialogTitle').textContent = title;
        document.getElementById('dialogMsg').textContent   = msg;
        document.getElementById('dialogInput').style.display = mode === 'prompt' ? 'block' : 'none';
        document.getElementById('dialogOverlay').classList.add('open');
        var self = this;
        return new Promise(function(r) { self._resolve = r; });
    },
    _close: function(val) {
        document.getElementById('dialogOverlay').classList.remove('open');
        if (this._resolve) { this._resolve(val); this._resolve = null; }
    },
    _buttons: function(btns) {
        document.getElementById('dialogButtons').innerHTML = btns.map(function(b) {
            return '<button class="btn ' + b.cls + '" onclick="Dialog._close(' + JSON.stringify(b.val) + ')">' + b.label + '</button>';
        }).join('');
    },
    alert:   function(msg, title)       { this._buttons([{label:'OK',cls:'primary',val:true}]); return this._open('i', title||'Info', msg, 'alert'); },
    confirm: function(msg, title, danger) { this._buttons([{label:'Cancel',cls:'',val:false},{label:'Confirm',cls:danger?'danger':'primary',val:true}]); return this._open('?', title||'Confirm', msg, 'confirm'); },
    error:   function(msg, title)       { return this.alert(msg, title||'Error'); },
};

// ── Payload modals ────────────────────────────────────────────────────────────

var pmScheduleId = null;
var pmSchema     = [];
var pmValues     = {};
var FIELD_TYPES  = ['text','password','boolean','select','multiselect','file','section','html','tab'];

async function _loadPayload(id) {
    try {
        var res  = await fetch('/scheduler/payload?id=' + encodeURIComponent(id));
        var data = await res.json();
        pmSchema = data.schema ? JSON.parse(data.schema) : [];
        pmValues = data.values ? JSON.parse(data.values) : {};
    } catch(e) { pmSchema = []; pmValues = {}; }
}

async function openSchemaModal(id, name) {
    pmScheduleId = id;
    await _loadPayload(id);
    document.getElementById('schemaTitle').textContent = 'Schema: ' + (name || id);
    document.getElementById('schemaOverlay').classList.add('open');
    renderConstructor();
}
function closeSchemaModal() { document.getElementById('schemaOverlay').classList.remove('open'); }

async function openValuesModal(id, name) {
    pmScheduleId = id;
    await _loadPayload(id);
    document.getElementById('valuesTitle').textContent = 'Values: ' + (name || id);
    document.getElementById('valuesOverlay').classList.add('open');
    renderValues();
}
function closeValuesModal() { document.getElementById('valuesOverlay').classList.remove('open'); }

var _importPayloadId = null;
function openImportPayload(id) {
    _importPayloadId = id;
    document.getElementById('importPayloadInput').value = '';
    document.getElementById('importPayloadOverlay').classList.add('open');
    setTimeout(function() { document.getElementById('importPayloadInput').focus(); }, 50);
}
function closeImportPayload() { document.getElementById('importPayloadOverlay').classList.remove('open'); _importPayloadId = null; }

async function confirmImportPayload() {
    var raw = document.getElementById('importPayloadInput').value.trim();
    if (!raw) return;
    var parsed;
    try { parsed = JSON.parse(raw); } catch(e) { alert('Invalid JSON: ' + e.message); return; }
    if (!parsed.schema || !parsed.values) { alert('Missing schema or values fields'); return; }
    try {
        var res = await fetch('/scheduler/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:_importPayloadId, schema:parsed.schema, values:parsed.values}) });
        if (!res.ok) throw new Error('HTTP ' + res.status);
        closeImportPayload();
        var s = schedules.find(function(x) { return x.id === _importPayloadId || x.id === selectedId; });
        if (s) openSchemaModal(s.id, s.name || s.id);
    } catch(e) { alert('Import failed: ' + e.message); }
}

function renderConstructor() {
    var body = document.getElementById('schemaBody');
    function thirdColProp(f) { return (f.type === 'select' || f.type === 'multiselect') ? 'options' : 'label'; }
    function thirdColVal(f)  { return (f.type === 'select' || f.type === 'multiselect') ? (f.options||'') : (f.label||''); }
    function thirdColPlaceholder(f) {
        if (f.type === 'select' || f.type === 'multiselect') return 'opt1, opt2';
        if (f.type === 'section') return 'Section title';
        if (f.type === 'html')    return '<b>HTML</b>...';
        if (f.type === 'tab')     return 'Tab title';
        return 'Label';
    }
    function keyInput(f, i) {
        var disabled = (f.type==='section'||f.type==='html'||f.type==='tab') ? ' disabled style="opacity:0.35"' : '';
        return '<input class="schema-input" placeholder="key" value="' + escHtml(f.key) + '"' + disabled + ' oninput="pmSchemaUpdate(' + i + ',\'key\',this.value)">';
    }
    var rows = pmSchema.map(function(f, i) {
        var typeOpts = FIELD_TYPES.map(function(t) { return '<option value="' + t + '"' + (f.type===t?' selected':'') + '>' + t + '</option>'; }).join('');
        return '<div class="schema-field-row" draggable="true" data-idx="' + i + '">'
            + '<span class="schema-drag-handle" title="Drag to reorder">⠿</span>'
            + keyInput(f, i)
            + '<select class="schema-input" onchange="pmSchemaUpdate(' + i + ',\'type\',this.value);renderConstructor()">' + typeOpts + '</select>'
            + '<input class="schema-input" placeholder="' + thirdColPlaceholder(f) + '" value="' + escHtml(thirdColVal(f)) + '" oninput="pmSchemaUpdate(' + i + ',\'' + thirdColProp(f) + '\',this.value)">'
            + '<button class="schema-del" onclick="pmSchemaRemove(' + i + ')">✕</button>'
            + '</div>';
    }).join('');
    body.innerHTML = (pmSchema.length ? '<div class="schema-col-header"><span></span><span>Key</span><span>Type</span><span>Label / Options</span><span></span></div>' : '')
        + rows
        + '<div style="margin-top:10px;display:flex;gap:6px">'
        + '<button class="btn sm" onclick="pmSchemaAdd(\'text\')">+ Field</button>'
        + '<button class="btn sm accent" onclick="pmSchemaAdd(\'section\')">+ Section</button>'
        + '<button class="btn sm" onclick="pmSchemaAdd(\'html\')">+ HTML</button>'
        + (pmSchema.length === 0 ? '<span style="color:var(--text2);font-size:10px;margin-left:6px">No fields</span>' : '')
        + '</div>';
    _bindSchemaDrag(body);
}
function pmSchemaUpdate(idx, prop, val) {
    if (!pmSchema[idx]) return;
    pmSchema[idx][prop] = val;
    if (prop === 'type' && (val === 'section' || val === 'html' || val === 'tab')) pmSchema[idx].key = '';
}
function pmSchemaAdd(type)   { pmSchema.push({key:'',label:'',type:type||'text',options:''}); renderConstructor(); }
function pmSchemaRemove(idx) { pmSchema.splice(idx, 1); renderConstructor(); }

var _dragSrcIdx = null;
function _bindSchemaDrag(container) {
    container.querySelectorAll('.schema-field-row[draggable]').forEach(function(row) {
        row.addEventListener('dragstart', function(e) { _dragSrcIdx = parseInt(row.dataset.idx); row.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; });
        row.addEventListener('dragend',   function()  { row.classList.remove('dragging'); container.querySelectorAll('.schema-field-row').forEach(function(r) { r.classList.remove('drag-over'); }); });
        row.addEventListener('dragover',  function(e) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; container.querySelectorAll('.schema-field-row').forEach(function(r) { r.classList.remove('drag-over'); }); row.classList.add('drag-over'); });
        row.addEventListener('drop',      function(e) { e.preventDefault(); var targetIdx = parseInt(row.dataset.idx); if (_dragSrcIdx === null || _dragSrcIdx === targetIdx) return; pmSchema.splice(targetIdx, 0, pmSchema.splice(_dragSrcIdx, 1)[0]); _dragSrcIdx = null; renderConstructor(); });
    });
}

function renderFieldHtml(f) {
    if (f.type === 'section') return '<div class="section-div">' + escHtml(f.label||'') + '</div>';
    if (f.type === 'html')    return '<div style="margin-bottom:8px">' + (f.label||'') + '</div>';
    if (!f.key) return '';
    var val = pmValues[f.key] !== undefined ? pmValues[f.key] : '';
    var labelHtml = '<div class="values-label">' + escHtml(f.label||f.key) + '<span class="values-key">' + escHtml(f.key) + '</span></div>';
    var inputHtml = '';
    if (f.type === 'boolean') {
        inputHtml = '<input type="checkbox" data-vkey="' + escHtml(f.key) + '" ' + ((val==='true'||val===true)?'checked':'') + ' style="width:14px;height:14px;cursor:pointer" onchange="pmValuesSet(\'' + escHtml(f.key) + '\',this.checked?\'true\':\'false\')">';
    } else if (f.type === 'password') {
        inputHtml = '<input class="values-input" type="password" data-vkey="' + escHtml(f.key) + '" value="' + escHtml(val) + '" oninput="pmValuesSet(\'' + escHtml(f.key) + '\',this.value)" autocomplete="new-password">';
    } else if (f.type === 'file') {
        var fid = 'fp_' + escHtml(f.key);
        inputHtml = '<div style="display:flex;gap:6px;align-items:center">'
            + '<input class="values-input" data-vkey="' + escHtml(f.key) + '" id="' + fid + '_text" value="' + escHtml(val) + '" oninput="pmValuesSet(\'' + escHtml(f.key) + '\',this.value)" placeholder="path..." style="flex:1">'
            + '<input type="file" id="' + fid + '_picker" style="display:none" onchange="(function(el){var t=document.getElementById(\'' + fid + '_text\');if(el.files[0]){t.value=el.files[0].path||el.files[0].name;pmValuesSet(\'' + escHtml(f.key) + '\',t.value);}})(this)">'
            + '<button class="btn sm" onclick="document.getElementById(\'' + fid + '_picker\').click()" style="flex-shrink:0;white-space:nowrap">Browse</button>'
            + '</div>';
    } else if (f.type === 'select') {
        var opts = (f.options||'').split(',').map(function(o){return o.trim();}).filter(Boolean);
        inputHtml = '<select class="values-input" data-vkey="' + escHtml(f.key) + '" onchange="pmValuesSet(\'' + escHtml(f.key) + '\',this.value)">'
            + opts.map(function(o){return '<option value="'+escHtml(o)+'"'+(o===val?' selected':'')+'>'+escHtml(o)+'</option>';}).join('') + '</select>';
    } else if (f.type === 'multiselect') {
        var opts    = (f.options||'').split(',').map(function(o){return o.trim();}).filter(Boolean);
        var selected = val ? val.split(',').map(function(v){return v.trim();}) : [];
        inputHtml = '<select class="values-input" multiple data-vkey="' + escHtml(f.key) + '" style="height:auto;min-height:60px" onchange="pmValuesSet(\'' + escHtml(f.key) + '\',[].slice.call(this.selectedOptions).map(function(o){return o.value;}).join(\',\'))">'
            + opts.map(function(o){return '<option value="'+escHtml(o)+'"'+(selected.indexOf(o)>=0?' selected':'')+'>'+escHtml(o)+'</option>';}).join('') + '</select>';
    } else {
        inputHtml = '<input class="values-input" data-vkey="' + escHtml(f.key) + '" value="' + escHtml(val) + '" oninput="pmValuesSet(\'' + escHtml(f.key) + '\',this.value)">';
    }
    return '<div class="values-field">' + labelHtml + inputHtml + '</div>';
}

var vmActiveTab = 0;
function renderValues() {
    var body = document.getElementById('valuesBody');
    if (!pmSchema.length) { body.innerHTML = '<div style="color:var(--text2);font-size:11px;padding:12px 0">No fields. Open Schema to add fields.</div>'; return; }
    var groups = [], current = {label:null, fields:[]};
    pmSchema.forEach(function(f) {
        if (f.type === 'tab') { groups.push(current); current = {label:f.label||('Tab '+(groups.length+1)), fields:[]}; }
        else current.fields.push(f);
    });
    groups.push(current);
    if (groups.length > 1 && groups[0].label === null && groups[0].fields.length === 0) groups.shift();
    var hasTabs = groups.length > 1 || groups[0].label !== null;
    if (!hasTabs) { body.innerHTML = groups[0].fields.map(renderFieldHtml).join(''); return; }
    if (vmActiveTab >= groups.length) vmActiveTab = 0;
    var tabBar = '<div style="display:flex;border-bottom:1px solid var(--border);margin-bottom:10px">'
        + groups.map(function(g,i) {
            var a = i === vmActiveTab;
            return '<div onclick="vmSwitchTab('+i+')" style="padding:6px 14px;font-size:11px;cursor:pointer;color:'+(a?'var(--accent)':'var(--text2)')+';border-bottom:2px solid '+(a?'var(--accent)':'transparent')+'">' + escHtml(g.label||'General') + '</div>';
        }).join('') + '</div>';
    body.innerHTML = tabBar + groups[vmActiveTab].fields.map(renderFieldHtml).join('');
}
function vmSwitchTab(idx) {
    document.querySelectorAll('#valuesBody [data-vkey]').forEach(function(el) { pmValues[el.dataset.vkey] = el.type==='checkbox'?(el.checked?'true':'false'):el.value; });
    vmActiveTab = idx; renderValues();
}
function pmValuesSet(key, val) { pmValues[key] = val; }

async function saveSchema() {
    if (!pmScheduleId) return;
    for (var i = 0; i < pmSchema.length; i++) {
        var f = pmSchema[i];
        if (f.type !== 'section' && f.type !== 'html' && f.type !== 'tab' && !f.key.trim()) { Dialog.error('Field #'+(i+1)+' must have a key.'); return; }
    }
    try {
        var res  = await fetch('/scheduler/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:pmScheduleId, schema:JSON.stringify(pmSchema), values:JSON.stringify(pmValues)}) });
        var data = await res.json();
        if (data.ok) closeSchemaModal(); else Dialog.error(data.error||'Save failed');
    } catch(e) { Dialog.error(e.message); }
}

async function saveValues() {
    if (!pmScheduleId) return;
    document.querySelectorAll('#valuesBody [data-vkey]').forEach(function(el) { pmValues[el.dataset.vkey] = el.type==='checkbox'?(el.checked?'true':'false'):el.value; });
    try {
        var res  = await fetch('/scheduler/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:pmScheduleId, schema:JSON.stringify(pmSchema), values:JSON.stringify(pmValues)}) });
        var data = await res.json();
        if (data.ok) closeValuesModal(); else Dialog.error(data.error||'Save failed');
    } catch(e) { Dialog.error(e.message); }
}

// ── Polling ───────────────────────────────────────────────────────────────────

setInterval(loadList, 10000);

window.addEventListener('resize', function() {
    var topPanel = document.getElementById('detailPanel');
    var botPanel = document.getElementById('bottomPanels');
    var rightCol = document.getElementById('rightCol');
    var hResizer = document.getElementById('hResizer');
    if (!topPanel || !botPanel || !botPanel.style.display || botPanel.style.display === 'none') return;
    if (topPanel.style.flex === 'none' || topPanel.style.height) {
        topPanel.style.height = Math.max(100, rightCol.clientHeight - (hResizer.offsetHeight||0) - botPanel.offsetHeight) + 'px';
    }
});
