// tasker.js

// Safe wrappers for PageState (may not exist in standalone mode)
var _PS = {
    save: function(obj) { if (typeof PageState !== 'undefined') PageState.save(obj); },
    load: function()    { return (typeof PageState !== 'undefined') ? PageState.load() : {}; },
};

// Потолок одного залпа по кнопке Run. Тот же, что у SchedulerService.MaxManualBurst:
// сервер всё равно проверит, здесь — чтобы поле не давало ввести заведомо отказное.
var MAX_BURST = 10000;

var schedules  = [];
var selectedId = null;          // задача, которую показывает форма
var selectedIds = new Set();    // всё выделенное в списке, включая selectedId
var _selAnchor = null;          // откуда считать диапазон по Shift+клику
var _formBaseline = {};         // поля формы в том виде, как их отрисовали, — от них считается «изменено»
var activeTab  = 'execution';
var outputPoll = null;
var formDirty  = false;
var taskDraft = null;

// Keep editable values separate from the periodically refreshed server list.
function captureTaskDraft() {
    if (!selectedId) return;
    var base = taskDraft || schedules.find(function(s) { return s.id === selectedId; }) || {};
    taskDraft = Object.assign({}, base, collectScheduleFields(base));
}

function markTaskDirty() {
    formDirty = true;
    captureTaskDraft();
}

var _httpPoll  = null;
var _sseOutput = null;
var _livePoll  = null;   // опрос живых инстансов задачи: он и решает, гореть ли LIVE
var _liveId    = '';     // задача, за которой сейчас следит _livePoll

var curProject = '';
var curTaskId  = '';
var curRunId   = '';

// ── Config modal state ────────────────────────────────────────────────────────

var _cmEditor     = null;
var _cmFilePath   = '';
var _cmScheduleId = '';

function _isJs(executor)     { return executor === 'node' || executor === 'ts-node' || executor === 'npm'; }
function _isPy(executor)     { return executor === 'python'; }
function _needsConfig(executor) { return _isJs(executor) || _isPy(executor); }

// ── Status helpers ────────────────────────────────────────────────────────────

function getTaskStatus(s) {
    if (s.status === 'running') return 'running';
    if (scheduleHeld(s)) return 'paused';
    var neverRan = !s.runs_total || parseInt(s.runs_total) === 0;
    if (neverRan) return 'newbie';
    var hasSchedule = (s.schedule_mode || 'off') !== 'off';
    var isPaused = s.enabled === 'false';
    if (hasSchedule) return isPaused ? 'paused' : 'planned';
    var isFail = s.status === 'error' || (s.last_exit && s.last_exit !== '0');
    return isFail ? 'fail' : 'done';
}

function scheduleHeld(s) {
    return s.schedule_paused === 'true' || Date.parse(s.deferred_until || '') > Date.now();
}

function escHtml(s) {
    if (s === null || s === undefined) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

// ── SSE ───────────────────────────────────────────────────────────────────────

function closeSse() {
    if (_httpPoll)  { clearInterval(_httpPoll); _httpPoll = null; }
}

function closeSseOutput() {
    if (_sseOutput) { _sseOutput.close(); _sseOutput = null; }
    stopLiveWatch();
}

function startSse() {
    closeSse();
    if (!curTaskId) return;
    _httpPoll = setInterval(loadHttp, 3000);
}

// ── Icons ─────────────────────────────────────────────────────────────────────

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
                schedule_mode: s.schedule_mode,
            }, null, 2);
        });
    }
    loadList().catch(function(e) {
        document.getElementById('listScroll').innerHTML =
            '<div style="padding:12px;color:red">Error: ' + e.message + '</div>';
    });
    initResizer();
    initHResizer();
    restoreLayout();
    document.getElementById('detailBody').addEventListener('input', markTaskDirty);
    document.getElementById('detailBody').addEventListener('change', markTaskDirty);
    document.getElementById('detailBody').addEventListener('click', function(e) {
        if (e.target.closest('.wd-btn')) markTaskDirty();
    });
    document.addEventListener('keydown', onListKeyDown);
});

// Delete removes the selected task, but only when the key is not meant for
// something else: a field being typed into, or a dialog/modal on top.
function onListKeyDown(e) {
    if (e.key !== 'Delete' || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return;
    var t = e.target;
    if (t && (t.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(t.tagName))) return;
    if (document.querySelector('#dialogOverlay.open, .pm-overlay.open, .cm-modal-overlay.open')) return;
    if (!selectedId || selectedId === '__new__') return;
    var ids = _selectedList().map(function(s) { return s.id; });
    if (!ids.length) return;
    e.preventDefault();
    deleteSchedules(ids);
}

// ── Load list ─────────────────────────────────────────────────────────────────

async function loadList() {
    var res = await fetch('/tasker/list');
    if (!res.ok) throw new Error('/tasker/list HTTP ' + res.status);
    schedules = await res.json();
    selectedIds.forEach(function(id) {
        if (!schedules.some(function(s) { return s.id === id; })) selectedIds.delete(id);
    });
    updateHeaderStats();
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
    // Не «сколько задач включено», а «сколько работает по расписанию»:
    // у задачи без расписания включённости нет.
    var enabled = schedules.filter(function(s) {
        return (s.schedule_mode || 'off') !== 'off' && s.enabled !== 'false';
    }).length;
    document.getElementById('headerStats').innerHTML =
        '<div class="stat-item">Tasks: <span class="stat-val">' + total + '</span></div>' +
        '<div class="stat-item">Running: <span class="stat-val stat-running">' + running + '</span></div>' +
        '<div class="stat-item">Scheduled: <span class="stat-val">' + enabled + '</span></div>' +
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

/// Подпись расписания в списке. Для ZP-режима разворачивается коротко:
/// сырой JSON в строке списка читать невозможно.
function triggerLabel(s) {
    var mode = s.schedule_mode || 'off';
    if (mode === 'cron') return (s.cron || '').trim() || 'cron';
    if (mode !== 'zp')   return 'on demand';

    try {
        var z    = JSON.parse(s.schedule_json || '{}');
        var how  = { once: 'once', daily: 'daily', weekly: 'weekly', monthly: 'monthly' }[z.how] || 'zp';
        var rep  = z.repeat || {};
        if (z.how === 'once') return 'once';
        if (rep.mode === 'pause' || rep.mode === 'regular') return how + ' / ' + (rep.min || 0) + 'm';
        if (rep.mode === 'back_to_back') return how + ' / nonstop';
        if (rep.mode === 'spread')       return how + ' / spread';
        return how;
    } catch (e) { return 'zp'; }
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
                ? Array(grpRunning).fill('<span style="display:inline-block;width:6px;height:6px;border-radius:50%;background:var(--green);margin-right:2px"></span>').join('')
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

        var status    = getTaskStatus(s);
        // Включённость — свойство расписания. У задачи «по требованию» её нет:
        // ни гасить строку, ни предлагать переключатель смысла не имеет.
        var scheduled = (s.schedule_mode || 'off') !== 'off';
        var disabled  = scheduled && s.enabled === 'false';
        var total    = parseInt(s.runs_total)   || 0;
        var done     = parseInt(s.runs_success) || 0;
        var trigger  = triggerLabel(s);
        var lastRun  = s.last_run ? s.last_run.slice(5, 16) : '';

        html += '<div class="schedule-row'
            + (showGrp ? ' group-child' : '')
            + (s.id === selectedId ? ' active' : (selectedIds.has(s.id) ? ' multi' : ''))
            + (disabled ? ' row-disabled' : '')
            + '" data-id="' + s.id + '"'
            + ' onmousedown="if(event.shiftKey||event.ctrlKey||event.metaKey)event.preventDefault()"'
            + ' onclick="onRowClick(event,\'' + s.id + '\')">'
            + (scheduled
                ? '<span class="row-dot ' + (disabled ? 'disabled' : 'enabled')
                  + '" title="' + (disabled ? 'Enable' : 'Disable')
                  + '" onclick="event.stopPropagation();toggleEnabled(\'' + s.id + '\',\'' + s.enabled + '\')"></span>'
                : '<span class="row-dot ondemand" title="No schedule"></span>')
            + '<div class="row-info">'
            + '<div class="row-name">' + escHtml(showGrp ? shortName : (s.name || '(unnamed)')) + '</div>'
            + '<div class="row-sub">' + escHtml(trigger) + (lastRun ? ' · ' + lastRun : '') + '</div>'
            + '</div>'
            + '<div class="row-right">'
            + '<span class="task-status ' + status + '">' + status + '</span>'
            + '<span class="row-counts"><span class="row-done">' + done + '</span><span class="row-total"> / ' + total + '</span></span>'
            + '</div>'
            + '</div>';
    });

    el.innerHTML = html || '<div style="padding:12px;color:var(--text2);text-align:center">No results</div>';
}

function filterList() { renderList(); }

// Обычный клик — одна задача. Ctrl — добавить/убрать, Shift — диапазон от якоря.
// Форма при этом остаётся на прежней задаче: набранное в ней не теряется.
function onRowClick(e, id) {
    var multi = e.ctrlKey || e.metaKey;
    if ((!multi && !e.shiftKey) || !selectedId || selectedId === '__new__') {
        _selAnchor = id;
        selectRow(id);
        return;
    }
    if (e.shiftKey) {
        var order = Array.prototype.map.call(
            document.querySelectorAll('#listScroll .schedule-row[data-id]'),
            function(el) { return el.dataset.id; });
        var a = order.indexOf(_selAnchor || selectedId), b = order.indexOf(id);
        if (a < 0 || b < 0) { a = b = order.indexOf(id); }
        if (!multi) selectedIds = new Set([selectedId]);
        order.slice(Math.min(a, b), Math.max(a, b) + 1).forEach(function(x) { selectedIds.add(x); });
    } else {
        _selAnchor = id;
        if (selectedIds.has(id)) selectedIds.delete(id); else selectedIds.add(id);
        if (!selectedIds.size) { deselect(); return; }
        if (id === selectedId) {
            // Форму показывала снятая задача — переключаемся на оставшуюся.
            var rest = Array.from(selectedIds);
            selectRow(rest[rest.length - 1], true);
            return;
        }
    }
    renderList();
    renderBulkState();
}

// Выделенные задачи: основная первой, дальше в порядке списка.
function _selectedList() {
    var primary = schedules.find(function(s) { return s.id === selectedId; });
    var rest = schedules.filter(function(s) { return s.id !== selectedId && selectedIds.has(s.id); });
    return (primary ? [primary] : []).concat(rest);
}

function _bulkCount() {
    return selectedId && selectedId !== '__new__' ? _selectedList().length : 0;
}

/// Плашка «выделено N» и подписи кнопок Save — чтобы было видно, куда уйдёт правка.
function renderBulkState() {
    var n  = _bulkCount();
    var el = document.getElementById('detailBulk');
    if (el) {
        el.style.display = n > 1 ? '' : 'none';
        el.textContent = n > 1
            ? n + ' tasks selected. Fields you change in Settings or Schedule are saved to all of them (the name is not). '
              + 'Ctrl+click adds or removes a task, Shift+click selects a range.'
            : '';
    }
    document.querySelectorAll('.bulk-save').forEach(function(b) { b.textContent = _saveLabel(); });
}

function _saveLabel() {
    var n = _bulkCount();
    return n > 1 ? 'Save to ' + n + ' tasks' : 'Save';
}

function deselect() {
    if (!selectedId) return;
    selectedIds = new Set();
    selectedId = null;
    taskDraft = null;
    formDirty  = false;
    stopProcStatsPoll();
    closeSse();
    clearOutputPoll();
    _PS.save({ selectedId: null });
    renderList();
    renderGlobalStats();
}

// ── Nav / global stats ────────────────────────────────────────────────────────

function _lastOutputLine(s) {
    var raw = (s.last_output || '').replace(/\\n/g, '\n');
    var lines = raw.split('\n');
    for (var i = lines.length - 1; i >= 0; i--) {
        var t = lines[i].trim();
        if (t) return t;
    }
    return '';
}

function renderGlobalStats() {
    var NAV_PAGES = [
        { icon: (typeof ICONS !== 'undefined' ? ICONS.scheduler : ''), title: 'z3nDash',      desc: 'Runs .py, .js, .exe, .bat on cron or intervals (you are here)', url: '/tasker.html', color: 'var(--yellow-lo)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.zp7       : ''), title: 'ZP7',        desc: 'ZennoPoster node and task control',                                                      url: '/?page=zp7',      color: 'var(--accent)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.logs       : ''), title: 'allLogs',    desc: 'Log history from every ZP7 node, filtered by level, machine, project and thread.', url: '/?page=zp7#allLogs',     color: 'var(--green)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.http       : ''), title: 'Traffic',    desc: 'HTTP traffic from the nodes and z3nDash, with details and replay.',   url: '/?page=zp7#traffic',     color: 'var(--yellow)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.json       : ''), title: 'JSON',       desc: 'Interactive JSON tree with auth/captcha detection and replay.',       url: '/json',           color: 'var(--accent)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.clips      : ''), title: 'Clips',      desc: 'Copy-paste snippets organised into a tree.',                       url: '/?page=clips',    color: 'var(--yellow-lo)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.text       : ''), title: 'Text Tools', desc: 'URL encode/decode, C# escaper, Base64, JSON escape.',                 url: '/text.html',      color: 'var(--steel)' },
        { icon: (typeof ICONS !== 'undefined' ? ICONS.config     : ''), title: 'Config',     desc: 'Server status, configuration editor, log storage management.', url: '/?page=config',   color: 'var(--red-lo)' },
    ];
    document.getElementById('detailHeader').style.display  = 'none';
    document.getElementById('hResizer').style.display      = 'none';
    document.getElementById('bottomPanels').style.display  = 'none';;
    var dp = document.getElementById('detailPanel');
    if (dp) { dp.style.flex = ''; dp.style.height = ''; }
    var body = document.getElementById('detailBody');
    var prevScroll = body.scrollTop;

    var total = schedules.length, running = 0, scheduled = 0, errors = 0, off = 0;
    schedules.forEach(function(s) {
        var st = getTaskStatus(s);
        if (st === 'running') running++;
        if (st === 'planned') scheduled++;
        if (st === 'fail')    errors++;
        if ((s.schedule_mode || 'off') !== 'off' && s.enabled === 'false') off++;
    });

    var statTiles =
        '<div class="ov-summary">'
        + '<div class="ov-stat"><span class="ov-stat-num">' + total + '</span><span class="ov-stat-label">Tasks</span></div>'
        + '<div class="ov-stat"><span class="ov-stat-num green">' + running + '</span><span class="ov-stat-label">Running</span></div>'
        + '<div class="ov-stat"><span class="ov-stat-num accent">' + scheduled + '</span><span class="ov-stat-label">Scheduled</span></div>'
        + '<div class="ov-stat"><span class="ov-stat-num' + (errors ? ' red' : '') + '">' + errors + '</span><span class="ov-stat-label">Errors</span></div>'
        + '<div class="ov-stat"><span class="ov-stat-num muted">' + off + '</span><span class="ov-stat-label">Disabled</span></div>'
        + '</div>';

    var ovOrder = { running: 0, fail: 1, planned: 2, newbie: 3, done: 4, paused: 5 };
    var sorted = schedules.slice().sort(function(a, b) {
        var sa = getTaskStatus(a), sb = getTaskStatus(b);
        var oa = (sa in ovOrder) ? ovOrder[sa] : 9;
        var ob = (sb in ovOrder) ? ovOrder[sb] : 9;
        if (oa !== ob) return oa - ob;
        return (a.name || '').localeCompare(b.name || '');
    });

    var rows = sorted.map(function(s) {
        var st       = getTaskStatus(s);
        var trigger  = triggerLabel(s);
        var lastRun  = s.last_run ? s.last_run.slice(5, 16) : '—';
        var doneN    = parseInt(s.runs_success) || 0;
        var totalN   = parseInt(s.runs_total)   || 0;
        var out      = _lastOutputLine(s);
        var isErr    = /\[ERROR\]|\[ERR\]/i.test(out);
        var disabled = (s.schedule_mode || 'off') !== 'off' && s.enabled === 'false';
        var dotCls   = st === 'running' ? ' run' : st === 'fail' ? ' fail' : disabled ? ' off' : '';
        return '<div class="ov-row' + (disabled ? ' off' : '') + '" onclick="selectRow(\'' + s.id + '\')">'
            + '<span class="ov-dot' + dotCls + '"></span>'
            + '<span class="ov-name" title="' + escHtml(s.name || '') + '">' + escHtml(s.name || '(unnamed)') + '</span>'
            + '<span><span class="task-status ' + st + '">' + st + '</span></span>'
            + '<span class="ov-cell" title="' + escHtml(trigger) + '">' + escHtml(trigger) + '</span>'
            + '<span class="ov-cell">' + escHtml(lastRun) + '</span>'
            + '<span class="ov-cell"><span class="row-done">' + doneN + '</span> / ' + totalN + '</span>'
            + (out
                ? '<span class="ov-out' + (isErr ? ' err' : '') + '" title="' + escHtml(out) + '">' + escHtml(out) + '</span>'
                : '<span class="ov-out empty">(no output)</span>')
            + '</div>';
    }).join('');

    var head = '<div class="ov-head"><span></span><span>Task</span><span>Status</span><span>Schedule</span><span>Last run</span><span>Done</span><span>Last output</span></div>';

    var links = NAV_PAGES.map(function(p) {
        return '<a class="ov-link" href="' + p.url + '" style="--card-color:' + p.color + '">' + p.title + '</a>';
    }).join('');

    body.innerHTML =
        '<div class="ov-wrap">'
        + statTiles
        + '<div class="ov-table">' + head
        + (rows || '<div style="padding:16px;color:var(--text2);text-align:center;font-size:11px">No tasks yet</div>')
        + '</div>'
        + '<div class="ov-links">' + links + '</div>'
        + '</div>';
    body.scrollTop = prevScroll;
}

// ── Select / detail ───────────────────────────────────────────────────────────

function selectRow(id, keepSelection) {
    if (selectedId === id) captureTaskDraft();
    else { taskDraft = null; formDirty = false; _formBaseline = {}; }
    if (!keepSelection) selectedIds = new Set([id]);
    selectedIds.add(id);
    selectedId = id;
    stopProcStatsPoll();
    _PS.save({ selectedId: id });
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    renderList();
    showDetailHeader(s);
    activeTab = 'execution';
    setActiveTab('execution');
    renderDetail(taskDraft || s);
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
    var pauseLabel = s.enabled === 'false' ? '▶' : '⏸';
    // Pause relates to the schedule: an on-demand task has nothing to pause.
    var scheduled  = (s.schedule_mode || 'off') !== 'off';
    var runLabel   = _isJs(s.executor) ? '▶ npm run' : '▶ Run';

    document.getElementById('detailActions').innerHTML =
        '<div class="action-group">'
        + '<input type="number" class="form-input" id="runCount" value="1" min="1" max="' + MAX_BURST + '"'
        +   ' title="How many runs to queue. They start as threads free up." style="width:54px;text-align:center;padding:2px 4px">'
        + '<button class="btn primary sm" onclick="runNow(\'' + id + '\')">' + runLabel + '</button>'
        + (scheduled ? '<button class="btn sm" onclick="toggleEnabled(\'' + id + '\',\'' + (s.enabled || 'true') + '\')">' + pauseLabel + '</button>' : '')
        + (scheduleHeld(s) ? '<button class="btn sm" title="Clear script pause and deferral" onclick="resumeSchedule(\'' + id + '\')">Resume schedule</button>' : '')
        + '<button class="btn sm" title="Restart" onclick="restartNow(\'' + id + '\')" style="border-color:var(--yellow);color:var(--yellow);">↺</button>'
        + '<button class="btn stop sm" title="Interrupt" onclick="stopNow(\'' + id + '\')">■</button>'
        + '<button class="btn danger sm" onclick="deleteSchedule(\'' + id + '\',\'' + escHtml(s.name || '') + '\')">🗑</button>'
        + '<button class="btn sm" onclick="duplicateSchedule(\'' + id + '\')" style="border-color:var(--yellow);color:var(--yellow);">📋📋</button>'
        + '</div>'
        + '<div class="action-group">'
        + '<button class="btn green sm" onclick="openValuesModal(\'' + id + '\',\'' + escHtml(s.name || '') + '\')">⚙ </button>'
        + '<button class="btn accent sm" onclick="openSchemaModal(\'' + id + '\',\'' + escHtml(s.name || '') + '\')">🔧 </button>'
        + '<button class="btn sm" onclick="openImportPayload(\'' + id + '\')" style="border-color:var(--accent);color:var(--accent);">📥 </button>'
        + '<button class="btn sm" onclick="exportPayload(\'' + id + '\')" style="border-color:var(--accent);color:var(--accent);">📤 </button>'
        + '<button class="btn sm" title="Clear payload" onclick="clearPayload(\'' + id + '\',\'' + escHtml(s.name || '') + '\')" style="border-color:var(--red);color:var(--red);">🗑 </button>'
        + '</div>'
        + '<div class="action-group">'
        
        + (s.script_path ? '<button class="btn sm" data-fp="' + escHtml(s.script_path) + '" onclick="openScriptFile(this.dataset.fp)" style="border-color:var(--green);color:var(--green);">📄</button>' : '')
        + (s.script_path ? '<button class="btn sm" data-fp="' + escHtml(s.script_path) + '" onclick="openScriptFolder(this.dataset.fp)" style="border-color:var(--green);color:var(--green);">📁</button>' : '')

        + '</div>'
        + '<div class="action-group">'
        + (s.script_path ? '<button class="btn sm" onclick="openAiForTask(\'' + escHtml(s.id) + '\')" style="border-color:var(--accent);color:var(--accent);">⟡ AI</button>' : '')
        + (s.script_path ? '<button class="btn sm" onclick="openInTerminal(\'' + escHtml(s.id) + '\')" style="border-color:var(--steel);color:var(--steel);">⌨ Terminal</button>' : '')
        + (s.executor === 'csx' ? '<button class="btn sm" onclick="buildCsx(\'' + id + '\')" style="border-color:var(--steel);color:var(--steel);">🔨 Build csx</button>' : '')
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
        res  = await fetch('/tasker/scan-folder?id=' + encodeURIComponent(s.id));
        info = await res.json();
    } catch(e) { return; }

    var group = document.createElement('div');
    group.className = 'action-group';
    group.id = 'extActionGroup';

    // npm scripts dropdown
    if (_isJs(s.executor)) {
        try {
            var pkgRes   = await fetch('/tasker/package-scripts?id=' + encodeURIComponent(s.id));
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
    cfgBtn.style.cssText = 'border-color:var(--accent);color:var(--accent);';
    cfgBtn.textContent = (info.has_config ? '⚙ ' : '+ ') + cfgLabel;
    cfgBtn.onclick = function() { openCmModal(s, 'config'); };
    group.appendChild(cfgBtn);

    if (_isJs(s.executor)) {
        var pkgBtn = document.createElement('button');
        pkgBtn.className = 'btn sm';
        pkgBtn.style.cssText = 'border-color:var(--accent);color:var(--accent);';
        pkgBtn.textContent = (info.has_package_json ? '📦 package.json' : '+ package.json');
        pkgBtn.onclick = function() { openCmModal(s, 'package'); };
        group.appendChild(pkgBtn);
    }

    var canInstall = _isJs(s.executor) || info.has_requirements;
    if (canInstall) {
        var instLabel = _isJs(s.executor) ? '📦 npm install' : '📦 pip install';
        var instBtn   = document.createElement('button');
        instBtn.className = 'btn sm';
        instBtn.style.cssText = 'border-color:var(--green);color:var(--green);';
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
        await fetch('/tasker/save', {
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

    var box = _getOutputBox();
    if (box) { box.innerHTML = ''; }
    stopLiveWatch();
    _setLive(true);

    var src = new EventSource('/tasker/install/stream?id=' + encodeURIComponent(s.id));

    src.addEventListener('output', function(e) {
        try {
            var d = JSON.parse(e.data);
            if (!box) return;
            var atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
            _appendLine(box, d);
            if (atBottom) box.scrollTop = box.scrollHeight;
        } catch(err) {}
    });

    src.addEventListener('done', function() {
        src.close();
        startLiveWatch(selectedId);
        btn.disabled    = false;
        btn.textContent = _isJs(s.executor) ? '📦 npm install' : '📦 pip install';
    });

    src.onerror = function() {
        src.close();
        startLiveWatch(selectedId);
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
        res  = await fetch('/tasker/config-file?id=' + encodeURIComponent(s.id) + '&type=' + encodeURIComponent(type));
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
        var res  = await fetch('/tasker/config-file', {
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
    else if (activeTab === 'schedule') renderSchedule(s);
    else if (activeTab === 'traffic')  renderTrafficTab(s);
    else                               renderExecution(s);
    _captureBaseline();
    renderBulkState();
}

// Запоминаем поля ровно такими, какими их собрала форма сразу после отрисовки:
// сравнивать с записью из БД нельзя — JSON браузера и расписания форма
// сериализует по-своему, и неизменённое поле выглядело бы изменённым.
function _captureBaseline() {
    if (!selectedId || selectedId === '__new__') return;
    var prev   = schedules.find(function(x) { return x.id === selectedId; }) || {};
    var fields = collectScheduleFields(prev);
    Object.keys(fields).forEach(function(k) {
        if (!formDirty || !(k in _formBaseline)) _formBaseline[k] = fields[k];
    });
}

function renderTrafficTab(s) {
    document.getElementById('detailBody').innerHTML =
        '<div style="display:flex;flex-direction:column;gap:5px;height:100%;min-height:0;">'
        + '<div style="display:flex;gap:0;flex:1;min-height:0;overflow:hidden;">'
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
        + '</div></div>'
        + '<div class="log-panel-scroll" id="httpScroll"><div class="log-empty">No traffic</div></div>'
        + '</div>'
        + '</div></div>';

    loadHttp();
    startSse();
}

function switchTab(tab) {
    captureTaskDraft();
    activeTab = tab;
    setActiveTab(tab);
    _PS.save({ activeTab: tab });
    if (tab !== 'execution') stopProcStatsPoll();
    if (tab !== 'traffic') closeSse();
    var s = taskDraft || schedules.find(function(x) { return x.id === selectedId; });
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
var _queuePoll     = null;

function stopProcStatsPoll() {
    if (_procStatsPoll) { clearInterval(_procStatsPoll); _procStatsPoll = null; }
    if (_queuePoll)     { clearInterval(_queuePoll);     _queuePoll     = null; }
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
    // Многопоточная задача: ёмкость больше одной нити либо очередь поверх неё.
    var isMulti = (parseInt(s.max_threads || '1', 10) || 1) > 1 || s.on_overlap === 'parallel';

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
        + (isRunning && !isMulti
            ? infoRow('PID',    '<span id="procPid"    class="accent">—</span>')
            + infoRow('Uptime', '<span id="procUptime" class="accent">—</span>')
            + infoRow('Memory', '<span id="procMem"    class="accent">—</span>')
            : '')
        + '</div>'
        + '<div class="detail-section">'
        + '<div class="info-card-title">Scheduler</div>'
        // Строка Active есть только у задачи с расписанием.
        + ((s.schedule_mode || 'off') !== 'off'
            ? infoRow('Active', s.enabled !== 'false' ? '<span class="green">True</span>' : '<span class="red">False</span>')
            : '')
        + infoRow('Last Run',   lastRun)
        + (s.schedule_paused === 'true' ? infoRow('Script pause', 'Paused') : '')
        + (Date.parse(s.deferred_until || '') > Date.now()
            ? infoRow('Deferred until', escHtml(new Date(s.deferred_until).toLocaleString())) : '')
        + (scheduleHeld(s) && s.defer_reason ? infoRow('Reason', escHtml(s.defer_reason)) : '')
        + infoRow('Period',     trigger)
        + infoRow('Threads',    s.max_threads || '1')
        + infoRow('When full',  OVERLAP_LABELS[s.on_overlap] || s.on_overlap || '—')
        + infoRow('Max runtime', (parseInt(s.timeout_seconds || '0', 10) || 0) > 0 ? s.timeout_seconds + 's' : 'no limit')
        + '</div>'
        + (isRunning && isMulti
            ? '<div class="detail-section" id="instancesCard"><div class="info-card-title">Active instances</div><div id="instancesList">—</div></div>'
            : '')
        // Очередь скрыта, пока в ней пусто, и раскрывается опросом. Прежнее условие
        // (isRunning && isMulti) прятало её у однопоточной задачи — а залп «N раз»
        // при max_threads=1 живёт целиком в очереди и был бы не виден вовсе.
        + '<div class="detail-section" id="queueCard" style="display:none"><div class="info-card-title">Queue (pending)</div><div id="queueList">—</div>'
        + '<button class="btn sm" style="margin-top:4px" onclick="clearQueue(\'' + escHtml(s.id) + '\')">Clear queue</button></div>'
        + '</div>';

    if (isRunning) {
        var id = s.id;

        function pollStats() {
            fetch('/tasker/process-stats?id=' + encodeURIComponent(id))
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
            fetch('/tasker/instances?id=' + encodeURIComponent(id))
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
        }

        if (!isMulti) pollStats();
        if (isMulti)  pollInstances();
        _procStatsPoll = setInterval(function() {
            if (!isMulti) pollStats();
            if (isMulti)  pollInstances();
        }, 2000);
    }

    startQueuePoll(s.id);
}

/// Очередь опрашивается независимо от того, идёт ли прогон: залп «N раз» на
/// однопоточной задаче виден только здесь, а в паузе между прогонами статус
/// задачи успевает стать idle — по нему очередь пряталась бы и появлялась.
function startQueuePoll(id) {
    function poll() {
        fetch('/tasker/queue?id=' + encodeURIComponent(id))
            .then(function(r) { return r.json(); })
            .then(function(list) {
                var card = document.getElementById('queueCard');
                var el   = document.getElementById('queueList');
                if (!card || !el) { if (_queuePoll) { clearInterval(_queuePoll); _queuePoll = null; } return; }

                var pending = (list || []).filter(function(q) { return q.status === 'pending'; });
                card.style.display = pending.length ? '' : 'none';
                if (!pending.length) return;

                var manual = pending.filter(function(q) { return q.origin === 'manual'; }).length;
                el.textContent = manual === pending.length ? pending.length + ' pending (Run)'
                               : manual === 0              ? pending.length + ' pending'
                               : pending.length + ' pending, ' + manual + ' of them from Run';
            }).catch(function() {});
    }
    poll();
    _queuePoll = setInterval(poll, 2000);
}

function infoRow(key, val) {
    return '<div class="info-row"><span class="info-key">' + key + '</span><span class="info-val">' + val + '</span></div>';
}

// ── Settings tab ──────────────────────────────────────────────────────────────

function newSchedule() {
    selectedIds = new Set();
    selectedId = '__new__';
    formDirty  = true;
    renderList();
    var s = { id:'', name:'', executor:'python', script_path:'', args:'',
              enabled:'false', cron:'', on_overlap:'skip', max_threads:'1',
              timeout_seconds:'0',
              use_venv:'false', schedule_mode:'off', schedule_json:'' };
    taskDraft = Object.assign({}, s);
    document.getElementById('detailHeader').style.display = '';
    document.getElementById('detailTitle').textContent = 'New task';
    document.getElementById('detailSub').textContent = '';
    document.getElementById('detailActions').innerHTML = '';
    document.getElementById('bottomPanels').style.display = 'none';
    document.getElementById('hResizer').style.display     = 'none';
    closeSseOutput();
    var dp = document.getElementById('detailPanel');
    dp.style.flex = ''; dp.style.height = '';
    activeTab = 'settings';
    setActiveTab('settings');
    renderSettings(s);
}

// ── Executor capabilities ─────────────────────────────────────────────────────
//
// Форма Settings зависит от экзекутора: у части задач путь — это файл, у части
// папка. Аргументы прячутся там, где ими управляет не пользователь: у npm их
// пишет выпадашка скриптов проекта.

var EXECUTORS = ['python','node','ts-node','npm','exe','cmd','bat','bash','ps1',
                 'csx','xml'];

var EXECUTOR_SPEC = {
    'python':       { label: 'Script (.py)',      pick: 'file'   },
    'node':         { label: 'Script (.js)',      pick: 'file'   },
    'ts-node':      { label: 'Script (.ts)',      pick: 'file'   },
    'npm':          { label: 'Project folder',    pick: 'folder', noArgs: true },
    'exe':          { label: 'Executable (.exe)', pick: 'file'   },
    'cmd':          { label: 'Command',           pick: 'none'   },
    'bat':          { label: 'Script (.bat)',     pick: 'file'   },
    'bash':         { label: 'Script (.sh)',      pick: 'file'   },
    'ps1':          { label: 'Script (.ps1)',     pick: 'file'   },
    'csx':          { label: 'Script (.csx)',     pick: 'file'   },
    'xml':          { label: 'Template (.xml)',   pick: 'file'   },
};

function execSpec(executor) {
    return EXECUTOR_SPEC[executor] || EXECUTOR_SPEC['python'];
}

function renderSettings(s) {
    var id   = s.id || '';
    var spec = execSpec(s.executor);

    document.getElementById('detailBody').innerHTML =
        '<div class="form-grid">'
        + '<div class="form-label">Name</div>'
        + '<input class="form-input" id="f_name" value="' + escHtml(s.name) + '">'
        + '<div class="form-label">Executor</div>'
        + '<select class="form-input" id="f_executor" onchange="onExecutorChange()">'
        // Экзекутор удалённой задачи оставляем в списке: иначе select молча
        // выберет первый пункт и первое же сохранение подменит его на python.
        + (EXECUTORS.indexOf(s.executor) < 0 && s.executor ? [s.executor] : []).concat(EXECUTORS).map(function(e) {
            return '<option ' + (s.executor === e ? 'selected' : '') + '>' + e + '</option>';
        }).join('') + '</select>'
        + '<div class="form-label" id="f_script_label">' + spec.label + '</div>'
        + '<div id="f_script_wrap">' + scriptFieldHtml(s, spec) + '</div>'
        + '<div class="form-label" id="f_args_label"' + (spec.noArgs ? ' style="display:none"' : '') + '>Arguments</div>'
        + '<input class="form-input" id="f_args" value="' + escHtml(s.args) + '"' + (spec.noArgs ? ' style="display:none"' : '') + '>'
        + venvRowHtml(s, id)
        + browserSectionHtml(s)
        + '<div class="form-section">Threads</div>'
        + '<div class="form-label">Threads</div>'
        + '<div style="display:flex;gap:6px;align-items:center;">'
        +   '<input class="form-input" id="f_max_threads" type="number" min="1" max="256" style="width:70px" value="' + (s.max_threads || '1') + '" oninput="onThreadsChanged()">'
        +   '<span style="color:var(--text2);font-size:10px;">how many instances of the task may run at once</span>'
        + '</div>'
        + '<div class="form-label">When full</div>'
        + '<select class="form-input" id="f_on_overlap" onchange="onOverlapChanged()">'
        + '<option value="skip"         ' + (s.on_overlap === 'skip'         ? 'selected' : '') + '>Skip — drop the extra run</option>'
        + '<option value="parallel"     ' + (s.on_overlap === 'parallel'     ? 'selected' : '') + '>Queue — hold extra runs in a queue</option>'
        + '<option value="kill_restart" ' + (s.on_overlap === 'kill_restart' ? 'selected' : '') + '>Kill &amp; restart — stop the running ones and start over</option>'
        + '</select>'
        + '<div class="form-label"></div>'
        + '<div id="f_threads_hint" style="color:var(--text2);font-size:10px;"></div>'
        + '<div class="form-section">Limits</div>'
        + '<div class="form-label">Max runtime</div>'
        + '<div style="display:flex;gap:6px;align-items:center;">'
        +   '<input class="form-input" id="f_timeout_seconds" type="number" min="0" max="604800" style="width:90px" value="' + (s.timeout_seconds || '0') + '">'
        +   '<span style="color:var(--text2);font-size:10px;">seconds before a run is aborted; 0 — no limit</span>'
        + '</div>'
        + '<div class="form-actions"><button class="btn primary bulk-save" onclick="saveSchedule(\'' + escHtml(id) + '\')">' + _saveLabel() + '</button></div>'
        + '</div>';

    if (s.executor === 'xml') browserSyncRows();
    onThreadsChanged();
}

/// Поле пути: файл с пикером, папка с пикером каталога либо команда без пикера.
function scriptFieldHtml(s, spec) {
    var input = '<input class="form-input" id="f_script_path" style="flex:1;" value="' + escHtml(s.script_path) + '" placeholder="'
              + (spec.pick === 'none' ? 'command to run' : '/path/to/script or folder') + '">';
    if (spec.pick === 'none') return '<div style="display:flex;gap:4px;">' + input + '</div>';

    var btn = spec.pick === 'folder'
        ? '<button type="button" class="btn sm" onclick="pickPath(&#39;folder&#39;)" title="Pick a folder">📁</button>'
        : '<button type="button" class="btn sm" onclick="pickPath(&#39;file&#39;)" title="Pick a file">📄</button>';
    return '<div style="display:flex;gap:4px;">' + input + btn + '</div>';
}

/// venv нужен только питону: остальные экзекуторы про него ничего не знают.
function venvRowHtml(s, id) {
    if (s.executor !== 'python') return '';
    return '<div class="form-label">Use venv</div>'
        + '<div style="display:flex;gap:6px;align-items:center;">'
        + '<input type="checkbox" id="f_use_venv"' + (s.use_venv === 'true' ? ' checked' : '') + '>'
        + '<span style="color:var(--text2);font-size:10px;">the venv folder sits next to the script, the interpreter is the system one</span>'
        + '<button class="btn sm" onclick="ensureVenv(\'' + escHtml(id) + '\')" style="padding:3px 8px;">Create now</button>'
        + '</div>';
}

/// Смена экзекутора перерисовывает форму: набор полей у каждого свой.
function onExecutorChange() {
    var s = _formSnapshot();
    taskDraft = s;
    formDirty = true;
    renderSettings(s);
}

/// Текущее содержимое формы — чтобы перерисовка не теряла введённое.
function _formSnapshot() {
    var s = taskDraft || schedules.find(function(x) { return x.id === selectedId; }) || {};
    var snap = Object.assign({}, s);
    snap.name        = (document.getElementById('f_name')        || {}).value || '';
    snap.executor    = (document.getElementById('f_executor')    || {}).value || 'python';
    snap.script_path = (document.getElementById('f_script_path') || {}).value || '';
    snap.args        = (document.getElementById('f_args')        || {}).value || '';
    snap.on_overlap  = (document.getElementById('f_on_overlap')  || {}).value || 'skip';
    snap.max_threads = (document.getElementById('f_max_threads') || {}).value || '1';
    snap.timeout_seconds = (document.getElementById('f_timeout_seconds') || {}).value || '0';
    var venv = document.getElementById('f_use_venv');
    if (venv) snap.use_venv = venv.checked ? 'true' : 'false';
    if (document.getElementById('b_mode')) snap.browser_json = JSON.stringify(collectBrowser());
    return snap;
}

async function ensureVenv(id) {
    if (!id) { Dialog.info('Save the task first, then create the venv.'); return; }
    var res  = await fetch('/tasker/ensure-venv', {
        method:  'POST',
        headers: { 'Content-Type': 'application/json' },
        body:    JSON.stringify({ id: id }),
    });
    var data = await res.json();
    if (data.ok) Dialog.info('venv ready:\n\n' + data.interpreter);
    else         Dialog.error((data.log || []).join('\n') || data.error || 'venv was not created');
}

// ── Browser (xml) ─────────────────────────────────────────────────────────────
//
// Шаблон ZennoPoster всегда играется в браузере, и браузер этот может быть не
// наш: у антидетект-профиля свой отпечаток и свой прокси, поднимать поверх него
// Patchright бессмысленно. Поэтому режимов пять, а внешний подключается по CDP.
//
// Пресеты — это только заполнение полей: код у всех антиков один, разница лишь
// в URL и в том, как в ответе лежит эндпоинт.

var BROWSER_PRESETS = {
    adspower: {
        title: 'AdsPower',
        method: 'GET',
        url:    'http://local.adspower.net:50325/api/v1/browser/start?user_id={profile}',
        ws:     '{data.ws.puppeteer}',
        stopUrl:'http://local.adspower.net:50325/api/v1/browser/stop?user_id={profile}'
    },
    dolphin: {
        title: 'Dolphin Anty',
        method: 'GET',
        url:    'http://localhost:3001/v1.0/browser_profiles/{profile}/start?automation=1',
        ws:     'ws://127.0.0.1:{automation.port}{automation.wsEndpoint}',
        stopUrl:'http://localhost:3001/v1.0/browser_profiles/{profile}/stop'
    }
};

function browserDefaults() {
    return {
        mode: 'patchright', profile: '', cdp: '', close: true, headless: true,
        proxySource: 'payload',
        api: { method: 'GET', url: '', body: '', ws: '', stopMethod: 'GET', stopUrl: '' }
    };
}

function browserFromSaved(s) {
    var d = browserDefaults();
    if (!s.browser_json) return d;
    try {
        var p = JSON.parse(s.browser_json);
        return {
            mode:    p.mode    || d.mode,
            profile: p.profile || '',
            cdp:     p.cdp     || '',
            close:   p.close !== false,
            headless: p.headless !== false,
            proxySource: p.proxySource || d.proxySource,
            api:     Object.assign(d.api, p.api || {})
        };
    } catch (e) { return d; }
}

/// Блок браузера в Settings. Показывается только у xml: остальные экзекуторы
/// браузер не поднимают.
function browserSectionHtml(s) {
    if (s.executor !== 'xml') return '';
    var b = browserFromSaved(s);

    return '<div class="form-section">Browser</div>'
        + '<div class="form-label">Source</div>'
        + '<select class="form-input" id="b_mode" onchange="browserSyncRows()">'
        + [['patchright','Patchright (own browser)'],
           ['zennobrowser','ZennoBrowser'],
           ['shardx','ShardX'],
           ['cdp','External over CDP'],
           ['api','External via anti-detect API']]
            .map(function(o) { return '<option value="' + o[0] + '"' + (b.mode === o[0] ? ' selected' : '') + '>' + o[1] + '</option>'; }).join('')
        + '</select>'

        + '<div class="form-label" id="b_profile_label">Profile</div>'
        + '<div id="b_profile_wrap" style="display:flex;gap:6px;align-items:center;">'
        +   '<input class="form-input" id="b_profile" value="' + escHtml(b.profile) + '" placeholder="profile id" style="flex:1">'
        +   '<span style="color:var(--text2);font-size:10px;white-space:nowrap">payload overrides this</span>'
        + '</div>'

        + '<div class="form-label" id="b_cdp_label">CDP endpoint</div>'
        + '<input class="form-input" id="b_cdp" value="' + escHtml(b.cdp) + '" placeholder="ws://127.0.0.1:9222/devtools/browser/...">'

        + '<div class="form-label" id="b_preset_label">Preset</div>'
        + '<div id="b_preset_wrap">'
        +   '<select class="form-input" id="b_preset" onchange="applyBrowserPreset()">'
        +   '<option value="">— manual —</option>'
        +   Object.keys(BROWSER_PRESETS).map(function(k) {
                return '<option value="' + k + '">' + BROWSER_PRESETS[k].title + '</option>';
            }).join('')
        +   '</select>'
        + '</div>'

        + '<div class="form-label" id="b_method_label">Start method</div>'
        + '<select class="form-input" id="b_method" onchange="browserSyncRows()">'
        + ['GET','POST'].map(function(m) { return '<option' + (b.api.method === m ? ' selected' : '') + '>' + m + '</option>'; }).join('')
        + '</select>'

        + '<div class="form-label" id="b_url_label">Start URL</div>'
        + '<input class="form-input" id="b_url" value="' + escHtml(b.api.url) + '" placeholder="http://.../start?user_id={profile}">'

        + '<div class="form-label" id="b_body_label">Request body</div>'
        + '<input class="form-input" id="b_body" value="' + escHtml(b.api.body) + '" placeholder="JSON for POST, {profile} is substituted">'

        + '<div class="form-label" id="b_ws_label">Endpoint template</div>'
        + '<input class="form-input" id="b_ws" value="' + escHtml(b.api.ws) + '" placeholder="{data.ws.puppeteer}">'

        + '<div class="form-label" id="b_stop_label">Close URL</div>'
        + '<input class="form-input" id="b_stop" value="' + escHtml(b.api.stopUrl) + '" placeholder="http://.../stop?user_id={profile}">'

        + '<div class="form-label" id="b_close_label">Close after the run</div>'
        + '<div id="b_close_wrap"><input type="checkbox" id="b_close"' + (b.close ? ' checked' : '') + '></div>'

        + '<div class="form-label" id="b_proxy_label">Launch proxy</div>'
        + '<div id="b_proxy_wrap">'
        +   '<select class="form-input" id="b_proxy_source">'
        +   [['payload','From the run payload'],
             ['template','From the template variable "proxy"'],
             ['none','None - the template sets it itself']]
              .map(function(o) { return '<option value="' + o[0] + '"' + (b.proxySource === o[0] ? ' selected' : '') + '>' + o[1] + '</option>'; }).join('')
        +   '</select>'
        +   '<span style="color:var(--text2);font-size:10px">pick "none" when the template builds its own proxy and writes it back into that variable - otherwise the browser starts on the proxy left over from the previous run</span>'
        + '</div>'

        + '<div class="form-label" id="b_headless_label">Headless</div>'
        + '<div id="b_headless_wrap">'
        +   '<input type="checkbox" id="b_headless"' + (b.headless ? ' checked' : '') + '>'
        +   '<span style="color:var(--text2);font-size:10px;margin-left:6px">off shows the browser window - for watching where a template stops</span>'
        + '</div>';
}

/// Поля зависят от источника: у Patchright их нет вовсе, у CDP только эндпоинт.
function browserSyncRows() {
    var mode = _val('b_mode', 'patchright');
    var api  = mode === 'api';
    var shardx = mode === 'shardx';

    _row('b_profile_label', 'b_profile_wrap', mode === 'zennobrowser' || shardx || api);
    _row('b_cdp_label',     'b_cdp',          mode === 'cdp');
    _row('b_preset_label',  'b_preset_wrap',  api);
    _row('b_method_label',  'b_method',       api);
    _row('b_url_label',     'b_url',          api);
    _row('b_body_label',    'b_body',         api && _val('b_method', 'GET') === 'POST');
    _row('b_ws_label',      'b_ws',           api);
    _row('b_stop_label',    'b_stop',         api);
    _row('b_close_label',   'b_close_wrap',   api || shardx);
    _row('b_headless_label', 'b_headless_wrap', mode === 'patchright');
    // Прокси на старт отдаём только своему браузеру: внешние поднимают его не мы.
    _row('b_proxy_label',    'b_proxy_wrap',    mode === 'patchright');
}

function applyBrowserPreset() {
    var preset = BROWSER_PRESETS[_val('b_preset', '')];
    if (!preset) return;
    document.getElementById('b_method').value = preset.method;
    document.getElementById('b_url').value    = preset.url;
    document.getElementById('b_ws').value     = preset.ws;
    document.getElementById('b_stop').value   = preset.stopUrl;
    browserSyncRows();
}

/// Форма → JSON для колонки browser_json.
function collectBrowser() {
    return {
        mode:    _val('b_mode', 'patchright'),
        profile: _val('b_profile', ''),
        cdp:     (_val('b_cdp', '') || '').trim(),
        close:   !!(document.getElementById('b_close') || { checked: true }).checked,
        headless: !!(document.getElementById('b_headless') || { checked: true }).checked,
        proxySource: _val('b_proxy_source', 'payload'),
        api: {
            method:     _val('b_method', 'GET'),
            url:        (_val('b_url', '') || '').trim(),
            body:       (_val('b_body', '') || '').trim(),
            ws:         (_val('b_ws', '') || '').trim(),
            stopMethod: 'GET',
            stopUrl:    (_val('b_stop', '') || '').trim()
        }
    };
}

// ── Schedule tab ──────────────────────────────────────────────────────────────
//
// Три режима: расписания нет, модель планировщика ZennoPoster, сырой cron.
// ZP-часть повторяет шесть блоков оригинала: как выполнять, начать,
// сколько делать, когда повторять, как повторять, завершить.

var WEEKDAY_NAMES = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];

function zpDefaults() {
    return {
        how:       'daily',
        weekdays:  [],
        monthdays: '',
        start:     { mode: 'now',   at: '' },
        attempts:  { min: 1, max: 1, resetSuccess: false },
        windows:   [],
        repeat:    { mode: 'pause', min: 10, max: 10 },
        end:       { mode: 'never', at: '', min: 1, max: 1 },
    };
}

function zpFromSaved(s) {
    var d = zpDefaults();
    if (!s.schedule_json) return d;
    try {
        var p = JSON.parse(s.schedule_json);
        return {
            how:       p.how       || d.how,
            weekdays:  p.weekdays  || d.weekdays,
            monthdays: p.monthdays || d.monthdays,
            start:     Object.assign(d.start,    p.start    || {}),
            attempts:  Object.assign(d.attempts, p.attempts || {}),
            windows:   p.windows   || d.windows,
            repeat:    Object.assign(d.repeat,   p.repeat   || {}),
            end:       Object.assign(d.end,      p.end      || {}),
        };
    } catch (e) { return d; }
}

var _zp = zpDefaults();

function renderSchedule(s) {
    var mode = s.schedule_mode || 'off';
    _zp = zpFromSaved(s);

    document.getElementById('detailBody').innerHTML =
        '<div class="form-grid">'
        + '<div class="form-label">Schedule</div>'
        + '<select class="form-input" id="f_schedule_mode" onchange="onScheduleModeChange()">'
        + '<option value="off"  ' + (mode === 'off'  ? 'selected' : '') + '>Disabled</option>'
        + '<option value="zp"   ' + (mode === 'zp'   ? 'selected' : '') + '>ZP style</option>'
        + '<option value="cron" ' + (mode === 'cron' ? 'selected' : '') + '>Cron</option>'
        + '</select>'
        + '</div>'
        + '<div id="scheduleBody"></div>';

    renderScheduleBody(s, mode);
}

function onScheduleModeChange() {
    captureTaskDraft();
    formDirty = true;
    var s = taskDraft || {};
    _zp = zpFromSaved(s);
    renderScheduleBody(s, document.getElementById('f_schedule_mode').value);
}

function renderScheduleBody(s, mode) {
    var box = document.getElementById('scheduleBody');
    if (!box) return;

    if (mode === 'off') {
        box.innerHTML = '<div class="empty-state" style="padding:18px 4px;">No schedule — the task runs only from the Run button.</div>'
                      + scheduleActionsHtml(s, false);
        return;
    }
    if (mode === 'cron') {
        box.innerHTML = '<div class="form-grid">'
            + '<div class="form-label">Cron</div>'
            + '<div class="trigger-row"><input class="form-input" id="f_cron" value="' + escHtml(s.cron || '') + '" placeholder="0 * * * *"><span style="color:var(--text2);font-size:10px;white-space:nowrap">min h dom mon dow</span></div>'
            + '</div>'
            + previewBoxHtml()
            + scheduleActionsHtml(s, true);
        return;
    }

    box.innerHTML = zpFormHtml(s) + previewBoxHtml() + scheduleActionsHtml(s, true);
    zpSyncRows();
}

function scheduleActionsHtml(s, withToggle) {
    var enabled = s.enabled !== 'false';
    return '<div class="form-grid">'
        + (withToggle
            ? '<div class="form-label">Enabled</div>'
              + '<select class="form-input" id="f_enabled">'
              + '<option value="true" '  + (enabled  ? 'selected' : '') + '>Yes</option>'
              + '<option value="false" ' + (!enabled ? 'selected' : '') + '>No</option>'
              + '</select>'
            : '')
        + '<div class="form-actions">'
        + (withToggle ? '<button class="btn" onclick="previewSchedule()">Preview</button>' : '')
        + '<button class="btn primary bulk-save" onclick="saveSchedule(\'' + escHtml(s.id || '') + '\')">' + _saveLabel() + '</button>'
        + '</div>'
        + '</div>';
}

function previewBoxHtml() {
    return '<div id="schedulePreview" style="margin:6px 0;font-size:10px;color:var(--text2);"></div>';
}

function zpFormHtml(s) {
    var z = _zp;
    return '<div class="form-grid">'
        // 1. Как выполнять
        + '<div class="form-section">How to run</div>'
        + '<div class="form-label">Frequency</div>'
        + '<select class="form-input" id="z_how" onchange="zpSyncRows()">'
        + [['once','Once'],['daily','Every day'],['weekly','Every week'],['monthly','Every month']]
            .map(function(o) { return '<option value="' + o[0] + '"' + (z.how === o[0] ? ' selected' : '') + '>' + o[1] + '</option>'; }).join('')
        + '</select>'
        + '<div class="form-label" id="z_weekdays_label">Weekdays</div>'
        + '<div id="z_weekdays_wrap"><div style="display:flex;gap:4px;flex-wrap:wrap" id="z_weekdays">'
        + WEEKDAY_NAMES.map(function(d, i) {
            return '<div class="wd-btn' + (z.weekdays.indexOf(i) >= 0 ? ' wd-on' : '') + '" data-bit="' + i + '" onclick="this.classList.toggle(\'wd-on\')">' + d + '</div>';
          }).join('')
        + '</div></div>'
        + '<div class="form-label" id="z_monthdays_label">Days of month</div>'
        + '<input class="form-input" id="z_monthdays" value="' + escHtml(z.monthdays) + '" placeholder="1-5, 10, 20">'

        // 2. Начать
        + '<div class="form-section">Start</div>'
        + '<div class="form-label">Begins</div>'
        + '<select class="form-input" id="z_start_mode" onchange="zpSyncRows()">'
        + '<option value="now"' + (z.start.mode === 'now' ? ' selected' : '') + '>Right away</option>'
        + '<option value="date"' + (z.start.mode === 'date' ? ' selected' : '') + '>On a date</option>'
        + '</select>'
        + '<div class="form-label" id="z_start_at_label">Date and time</div>'
        + '<input class="form-input" id="z_start_at" type="datetime-local" value="' + escHtml(z.start.at || '') + '">'

        // 3. Сколько делать
        + '<div class="form-section">How much to do</div>'
        + '<div class="form-label">Attempts per run</div>'
        + rangeInputsHtml('z_attempts', z.attempts.min, z.attempts.max)
        + '<div class="form-label">Reset successes</div>'
        + '<div><input type="checkbox" id="z_reset_success"' + (z.attempts.resetSuccess ? ' checked' : '') + '></div>'

        // 4. Когда повторять
        + '<div class="form-section" id="z_windows_section">When to repeat</div>'
        + '<div class="form-label" id="z_windows_label">Time windows</div>'
        + '<div id="z_windows_wrap">'
        +   '<div id="z_windows"></div>'
        +   '<button class="btn sm" onclick="zpAddWindow()" style="margin-top:4px;">+ Add a window</button>'
        +   '<div style="color:var(--text2);font-size:10px;margin-top:3px;">empty — around the clock</div>'
        + '</div>'

        // 5. Как повторять
        + '<div class="form-section" id="z_repeat_section">How to repeat</div>'
        + '<div class="form-label" id="z_repeat_label">Mode</div>'
        + '<select class="form-input" id="z_repeat_mode" onchange="zpSyncRows()">'
        + [['back_to_back','Back to back'],['pause','Back to back with a pause'],['regular','At a fixed rate'],['spread','Spread over the window']]
            .map(function(o) { return '<option value="' + o[0] + '"' + (z.repeat.mode === o[0] ? ' selected' : '') + '>' + o[1] + '</option>'; }).join('')
        + '</select>'
        + '<div class="form-label" id="z_repeat_min_label">Minutes</div>'
        + rangeInputsHtml('z_repeat', z.repeat.min, z.repeat.max)

        // 6. Завершить
        + '<div class="form-section" id="z_end_section">Finish</div>'
        + '<div class="form-label" id="z_end_label">Condition</div>'
        + '<select class="form-input" id="z_end_mode" onchange="zpSyncRows()">'
        + [['never','Never'],['date','On a date'],['count','After N repeats']]
            .map(function(o) { return '<option value="' + o[0] + '"' + (z.end.mode === o[0] ? ' selected' : '') + '>' + o[1] + '</option>'; }).join('')
        + '</select>'
        + '<div class="form-label" id="z_end_at_label">Date and time</div>'
        + '<input class="form-input" id="z_end_at" type="datetime-local" value="' + escHtml(z.end.at || '') + '">'
        + '<div class="form-label" id="z_end_count_label">Repeats</div>'
        + rangeInputsHtml('z_end_count', z.end.min, z.end.max)
        + '</div>';
}

/// «Точное число или диапазон» — в ZP это одна строка с двумя полями.
function rangeInputsHtml(prefix, min, max) {
    return '<div id="' + prefix + '_wrap" style="display:flex;gap:4px;align-items:center;">'
        + '<input class="form-input" id="' + prefix + '_min" type="number" min="1" value="' + (min || 1) + '" style="width:70px">'
        + '<span style="color:var(--text2);font-size:10px;">to</span>'
        + '<input class="form-input" id="' + prefix + '_max" type="number" min="1" value="' + (max || min || 1) + '" style="width:70px">'
        + '</div>';
}

/// Показ строк по выбранным режимам: при «Один раз» блоки 4–6 в ZP недоступны.
function zpSyncRows() {
    var how    = _val('z_how', 'daily');
    var repeat = _val('z_repeat_mode', 'pause');
    var end    = _val('z_end_mode', 'never');
    var start  = _val('z_start_mode', 'now');
    var once   = how === 'once';

    _row('z_weekdays_label',  'z_weekdays_wrap',  how === 'weekly');
    _row('z_monthdays_label', 'z_monthdays',      how === 'monthly');
    _row('z_start_at_label',  'z_start_at',       start === 'date');

    _show('z_windows_section', !once);
    _row('z_windows_label',   'z_windows_wrap',   !once);

    _show('z_repeat_section', !once);
    _row('z_repeat_label',    'z_repeat_mode',    !once);
    _row('z_repeat_min_label','z_repeat_wrap',    !once && (repeat === 'pause' || repeat === 'regular'));

    _show('z_end_section',    !once);
    _row('z_end_label',       'z_end_mode',       !once);
    _row('z_end_at_label',    'z_end_at',         !once && end === 'date');
    _row('z_end_count_label', 'z_end_count_wrap', !once && end === 'count');

    zpRenderWindows();
}

function _val(id, fallback) { var el = document.getElementById(id); return el ? el.value : fallback; }
function _show(id, on)      { var el = document.getElementById(id); if (el) el.style.display = on ? '' : 'none'; }
function _row(labelId, fieldId, on) { _show(labelId, on); _show(fieldId, on); }

function zpRenderWindows() {
    var box = document.getElementById('z_windows');
    if (!box) return;
    box.innerHTML = _zp.windows.map(function(w, i) {
        return '<div style="display:flex;gap:4px;align-items:center;margin-bottom:3px;">'
            + '<input class="form-input" type="time" value="' + escHtml(w.from || '') + '" onchange="zpSetWindow(' + i + ',\'from\',this.value)" style="width:96px">'
            + '<span style="color:var(--text2);font-size:10px;">–</span>'
            + '<input class="form-input" type="time" value="' + escHtml(w.to || '') + '" onchange="zpSetWindow(' + i + ',\'to\',this.value)" style="width:96px">'
            + '<button class="btn sm danger" onclick="zpRemoveWindow(' + i + ')" style="padding:2px 7px;">✕</button>'
            + '</div>';
    }).join('');
}

function zpAddWindow()  { _zp.windows.push({ from: '09:00', to: '18:00' }); zpRenderWindows(); }
function zpRemoveWindow(i) { _zp.windows.splice(i, 1); zpRenderWindows(); }
function zpSetWindow(i, key, val) { if (_zp.windows[i]) _zp.windows[i][key] = val; }

/// Форма → JSON для колонки schedule_json.
function collectZp() {
    var weekdays = [];
    document.querySelectorAll('#z_weekdays .wd-btn.wd-on').forEach(function(b) {
        weekdays.push(parseInt(b.getAttribute('data-bit')));
    });
    return {
        how:       _val('z_how', 'daily'),
        weekdays:  weekdays,
        monthdays: _val('z_monthdays', ''),
        start:     { mode: _val('z_start_mode', 'now'), at: _val('z_start_at', '') },
        attempts:  {
            min: _num('z_attempts_min', 1),
            max: _num('z_attempts_max', 1),
            resetSuccess: !!(document.getElementById('z_reset_success') || {}).checked,
        },
        windows:   _zp.windows.filter(function(w) { return w.from && w.to; }),
        repeat:    { mode: _val('z_repeat_mode', 'pause'), min: _num('z_repeat_min', 10), max: _num('z_repeat_max', 10) },
        end:       { mode: _val('z_end_mode', 'never'), at: _val('z_end_at', ''), min: _num('z_end_count_min', 1), max: _num('z_end_count_max', 1) },
    };
}

function _num(id, fallback) {
    var el = document.getElementById(id);
    var n  = el ? parseInt(el.value) : NaN;
    return isNaN(n) ? fallback : n;
}

/// Предпросмотр ближайших запусков — заодно проверяет настройки:
/// пока сервер возвращает ошибки, расписание считается невалидным.
async function previewSchedule() {
    var box  = document.getElementById('schedulePreview');
    var mode = _val('f_schedule_mode', 'off');
    if (!box || mode === 'off') return;

    box.textContent = 'calculating...';
    var body = mode === 'cron'
        ? { mode: 'cron', cron: _val('f_cron', '') }
        : { mode: 'zp',   schedule_json: JSON.stringify(collectZp()) };

    try {
        var res  = await fetch('/tasker/schedule-preview', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify(body),
        });
        var data = await res.json();
        if (!data.ok) {
            box.innerHTML = '<span style="color:var(--red)">' + (data.errors || []).map(escHtml).join('<br>') + '</span>';
            return;
        }
        if (!data.times || data.times.length === 0) {
            box.innerHTML = '<span style="color:var(--red)">No upcoming runs — check the settings</span>';
            return;
        }
        box.innerHTML = 'Upcoming runs (UTC):<br>' + data.times.map(escHtml).join('<br>');
    } catch (e) {
        box.textContent = e.message;
    }
}

// ── Output tab ────────────────────────────────────────────────────────────────

function renderOutput(s) {
    // output is now in bottom panel, nothing to render in detailBody for output
    // kept for compat - unused
}

function renderOutputLines(text) {
    if (!text || !text.trim()) return '<div class="out-line empty">(no output yet)</div>';
    return text.replace(/\\n/g, '\n').split('\n')
               .map(function(line) { return LogLine.build({ line: line }); })
               .join('');
}

// ── Output box (bottom panel) ─────────────────────────────────────────────────

function _getOutputBox()  { return document.getElementById('outputBox'); }
function _getLiveBadge()  { return document.getElementById('liveBadgeBottom'); }

// ── Бейдж LIVE ────────────────────────────────────────────────────────────────
// Бейдж утверждает «прогон идёт прямо сейчас», поэтому источник для него один —
// список живых инстансов задачи. Событие в канале вывода таким источником быть
// не может: при подписке сервер повторяет накопленный лог, и строка, написанная
// час назад, зажигала бейдж на задаче, которая давно не работает.

function _setLive(on) {
    var badge = _getLiveBadge();
    if (badge) badge.style.display = on ? 'inline-block' : 'none';
}

function stopLiveWatch() {
    if (_livePoll) { clearInterval(_livePoll); _livePoll = null; }
    _liveId = '';
}

function startLiveWatch(id) {
    stopLiveWatch();
    if (!id || id === '__new__') { _setLive(false); return; }
    _liveId = id;
    _setLive(false);
    checkLive();
    _livePoll = setInterval(checkLive, 3000);
}

// Ответ пришёл после переключения задачи — он уже не про ту, что на экране.
function checkLive() {
    var id = _liveId;
    if (!id) return;
    fetch('/tasker/instances?id=' + encodeURIComponent(id))
        .then(function(r) { return r.json(); })
        .then(function(list) {
            if (_liveId !== id) return;
            _setLive(!!(list && list.length));
        })
        .catch(function() {});
}

// ── Нити и фильтры вывода ─────────────────────────────────────────────────────
// Пять параллельных нитей пишут в одно окно, поэтому у каждой свой цвет,
// бейдж с началом runId и чип в шапке для изоляции.

var _runsSeen  = {};
var _runFilter = null;

// «Подряд» на пяти нитях рождает новый runId каждые несколько секунд, поэтому
// в шапке живут только последние чипы: старые нити всё равно уже завершились.
var RUN_CHIP_LIMIT = 12;

function _resetRuns() {
    _runsSeen  = {};
    _runFilter = null;
    var chips = document.getElementById('runChips');
    if (chips) chips.innerHTML = '';
    var box = _getOutputBox();
    if (box) box.classList.remove('multi-run');
}

function _noteRun(run) {
    if (!run || _runsSeen[run]) return;
    _runsSeen[run] = true;

    var chips = document.getElementById('runChips');
    if (chips) {
        var chip = document.createElement('button');
        chip.className   = 'run-chip';
        chip.textContent = run.slice(0, 4);
        chip.title       = 'Only thread ' + run;
        chip.setAttribute('data-run', run);
        chip.style.setProperty('--ll-h', LogLine.hue(run));
        chip.onclick = function () { toggleRunFilter(run); };
        chips.appendChild(chip);
        _trimRunChips(chips);
        chips.scrollLeft = chips.scrollWidth;
    }

    // Бейдж нити нужен, только когда нитей действительно несколько.
    var box = _getOutputBox();
    if (box && Object.keys(_runsSeen).length > 1) box.classList.add('multi-run');
}

/// Выкинуть самые старые чипы, сняв с них фильтр, если он на них стоял.
function _trimRunChips(chips) {
    while (chips.children.length > RUN_CHIP_LIMIT) {
        var oldest = chips.firstElementChild;
        if (oldest.getAttribute('data-run') === _runFilter) toggleRunFilter(_runFilter);
        chips.removeChild(oldest);
    }
}

function toggleRunFilter(run) {
    _runFilter = (_runFilter === run) ? null : run;
    var chips = document.getElementById('runChips');
    if (chips) Array.prototype.forEach.call(chips.children, function (c) {
        c.classList.toggle('on', c.getAttribute('data-run') === _runFilter);
    });
    var box = _getOutputBox();
    if (!box) return;
    Array.prototype.forEach.call(box.querySelectorAll('.out-line'), function (el) {
        el.classList.toggle('ll-muted', !!_runFilter && el.getAttribute('data-run') !== _runFilter);
    });
}

function setOutputLevel(v) {
    var box = _getOutputBox();
    if (!box) return;
    box.classList.remove('lvl-warn', 'lvl-err');
    if (v === 'warn') box.classList.add('lvl-warn');
    if (v === 'err')  box.classList.add('lvl-err');
}

/// Дописать строку в окно с учётом активного фильтра по нити.
function _appendLine(box, d) {
    _noteRun(d.run);
    box.insertAdjacentHTML('beforeend', LogLine.build(d));
    var el = box.lastElementChild;
    if (_runFilter && el && el.getAttribute('data-run') !== _runFilter) el.classList.add('ll-muted');
    return el;
}

function loadOutput(id) {
    // load last saved output from DB into bottom box
    fetch('/tasker/output?id=' + encodeURIComponent(id))
        .then(function(r) { return r.json(); })
        .then(function(data) {
            var box = _getOutputBox();
            if (!box) return;
            // История из БД приходит без разбивки по нитям: только текст строк.
            _resetRuns();
            var text = data.output || '';
            if (!text.trim()) {
                box.innerHTML = '<div class="out-line empty">(no output yet)</div>';
            } else {
                box.innerHTML = renderOutputLines(text);
                box.scrollTop = box.scrollHeight;
            }
        }).catch(function() {});
}

function reloadOutput() {
    if (selectedId && selectedId !== '__new__') loadOutput(selectedId);
}

function startSseOutput(id) {
    if (_sseOutput) { _sseOutput.close(); _sseOutput = null; }
    _resetRuns();
    startLiveWatch(id);

    _sseOutput = new EventSource('/tasker/output/stream?id=' + encodeURIComponent(id));

    _sseOutput.addEventListener('output', function(e) {
        try {
            var d   = JSON.parse(e.data);
            var box = _getOutputBox();
            if (!box) return;
            if (d.done)  { _setLive(false); return; }
            if (d.clear) { box.innerHTML = ''; _resetRuns(); }
            var empty = box.querySelector('.out-line.empty');
            if (empty) empty.remove();

            var atBottom = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
            var plain    = LogLine.plain(d.line || '');
            var last     = box.lastElementChild;

            // Прогресс-строка одной нити перерисовывается на месте, а не копится.
            function progressPrefix(s) { return s.replace(/[\d%\[\]]+.*$/, '').trim(); }
            var lastSpan = last ? last.querySelector('.out-line-text') : null;
            var lastText = lastSpan ? lastSpan.textContent : '';
            var sameProgress = last && last.classList.contains('out-line') && !last.classList.contains('empty')
                && (last.getAttribute('data-run') || '') === (d.run || '')
                && progressPrefix(plain).length > 3
                && progressPrefix(plain) === progressPrefix(lastText);

            if (d.replace_last || sameProgress) {
                _noteRun(d.run);
                last.outerHTML = LogLine.build(d);
            } else {
                _appendLine(box, d);
            }
            if (atBottom) box.scrollTop = box.scrollHeight;
            // Живая строка — повод не ждать очередного тика опроса.
            if (!d.replay) checkLive();
        } catch(err) {}
    });

    _sseOutput.addEventListener('done', function() {
        _setLive(false);
        _sseOutput.close(); _sseOutput = null;
    });

    // Оборвалась подписка, а не прогон: гасить бейдж здесь нельзя, опрос сам
    // увидит, работает задача или нет.
    _sseOutput.onerror = function() {
        _sseOutput.close(); _sseOutput = null;
    };
}

async function clearOutputBottom() {
    if (!selectedId || selectedId === '__new__') return;
    await fetch('/tasker/clear-output', {
        method: 'POST', headers: {'Content-Type': 'application/json'},
        body: JSON.stringify({ id: selectedId })
    });
    var box = _getOutputBox();
    if (box) box.innerHTML = '<div class="out-line empty">(no output yet)</div>';
    _resetRuns();
    var s = schedules.find(function(x) { return x.id === selectedId; });
    if (s) s.last_output = '';
}

function clearOutputPoll() {
    if (outputPoll)  { clearTimeout(outputPoll); outputPoll = null; }
    if (_sseOutput)  { _sseOutput.close(); _sseOutput = null; }
    stopLiveWatch();
}

// ── Log / HTTP panels ─────────────────────────────────────────────────────────

async function loadHttp() {
    if (!curTaskId) return;
    var method = document.getElementById('httpMethod').value;
    var status = document.getElementById('httpStatus').value;
    var urlFlt = document.getElementById('httpUrl').value;
    var limit  = document.getElementById('httpLimit').value || 50;
    // Трафик лежит в trafficLog.jsonl; фильтры метода, статуса и URL — на клиенте,
    // эндпоинт знает только project и task_id.
    var url = '/traffic?tail=' + limit + '&task_id=' + encodeURIComponent(curTaskId);
    try {
        var res  = await fetch(url, { cache: 'no-store' });
        var text = await res.text();
        var el   = document.getElementById('httpScroll');
        var page;
        try { page = JSON.parse(text); }
        catch (err) { el.innerHTML = '<div class="log-empty">HTTP ' + res.status + ', not JSON: ' + escHtml(text.trim().slice(0, 200) || '(empty body)') + '</div>'; return; }
        if (!res.ok || page.error) { el.innerHTML = '<div class="log-empty">' + escHtml(page.error || ('HTTP ' + res.status)) + '</div>'; return; }

        var data = Array.isArray(page.entries) ? page.entries : [];
        if (method) data = data.filter(function(r) { return r.method === method; });
        if (status) data = data.filter(function(r) { return String(r.statusCode == null ? '' : r.statusCode).indexOf(status) === 0; });
        if (urlFlt) data = data.filter(function(r) { return String(r.url || '').toLowerCase().indexOf(urlFlt.toLowerCase()) !== -1; });
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

function openAiForTask(id) {
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    var scriptPath = s.script_path || '';
    AiPanel.setCwd(scriptPath);
    AiPanel.open();
}

async function openInTerminal(id) {
    try {
        var res  = await fetch('/tasker/open-terminal?id=' + encodeURIComponent(id));
        var data = await res.json();
        if (!data.ok) Dialog.error(data.error || 'Cannot open terminal');
    } catch(e) { Dialog.error(e.message); }
}

// Capture mounted fields only; values from other tabs remain in the draft.
function collectScheduleFields(prev) {
    var payload = {};

    if (document.getElementById('f_name')) {
        var maxThreadsEl = document.getElementById('f_max_threads');
        var venvEl       = document.getElementById('f_use_venv');
        payload.name        = document.getElementById('f_name').value.trim();
        payload.executor    = document.getElementById('f_executor').value;
        payload.script_path = document.getElementById('f_script_path').value.trim();
        payload.on_overlap  = document.getElementById('f_on_overlap').value;
        payload.max_threads = maxThreadsEl ? maxThreadsEl.value : '1';
        var timeoutEl = document.getElementById('f_timeout_seconds');
        payload.timeout_seconds = timeoutEl ? (parseInt(timeoutEl.value, 10) > 0 ? String(parseInt(timeoutEl.value, 10)) : '0')
                                            : (prev.timeout_seconds || '0');
        payload.use_venv    = venvEl ? (venvEl.checked ? 'true' : 'false') : (prev.use_venv || 'false');

        // У npm поля Arguments нет: их пишет выпадашка скриптов проекта.
        var argsEl = document.getElementById('f_args');
        if (argsEl && argsEl.style.display !== 'none') payload.args = argsEl.value.trim();

        // Браузер настраивается только у xml; у прочих экзекуторов колонку не трогаем.
        if (document.getElementById('b_mode')) payload.browser_json = JSON.stringify(collectBrowser());
    }

    var modeEl = document.getElementById('f_schedule_mode');
    if (modeEl) {
        var mode = modeEl.value;
        payload.schedule_mode = mode;
        if (document.getElementById('f_cron')) payload.cron = _val('f_cron', '');
        if (document.getElementById('z_how')) payload.schedule_json = JSON.stringify(collectZp());
        payload.enabled       = mode === 'off' ? 'false' : _val('f_enabled', 'true');
    }
    return payload;
}

async function saveSchedule(existingId) {
    captureTaskDraft();
    var prev = schedules.find(function(x) { return x.id === selectedId; }) || {};
    var payload = { id: existingId || undefined };
    ['name', 'executor', 'script_path', 'args', 'on_overlap', 'max_threads', 'timeout_seconds',
     'use_venv', 'browser_json', 'schedule_mode', 'cron', 'schedule_json', 'enabled'].forEach(function(key) {
        if (taskDraft && taskDraft[key] !== undefined) payload[key] = taskDraft[key];
    });
    var mode = payload.schedule_mode || 'off';
    payload.cron = mode === 'cron' ? (payload.cron || '').trim() : '';
    payload.schedule_json = mode === 'zp' ? (payload.schedule_json || '') : '';
    if (mode !== 'off' && (prev.schedule_mode !== mode || prev.schedule_json !== payload.schedule_json)) {
        payload.sched_runs = '0';
        payload.sched_started_at = new Date().toISOString();
    }

    // Остальным выделенным — только то, что поменяли в форме, и заранее:
    // после сохранения основной задачи базовая линия уже не та.
    var others = existingId ? _selectedList().slice(1) : [];
    var changed = others.length ? _changedFields(payload) : {};

    var data = await _postSave(payload);
    if (!data.ok) { Dialog.error(data.error || 'Save failed'); return; }

    var failed = [];
    if (Object.keys(changed).length) {
        for (var i = 0; i < others.length; i++) {
            var r = await _postSave(_bulkPayload(others[i], changed));
            if (!r.ok) failed.push((others[i].name || others[i].id) + ': ' + (r.error || 'Save failed'));
        }
    }
    selectedId = data.id; taskDraft = null; formDirty = false; _formBaseline = {};
    await loadList();
    selectRow(data.id, true);
    if (failed.length) Dialog.error('Not saved:\n' + failed.join('\n'));
}

async function _postSave(payload) {
    try {
        var res = await fetch('/tasker/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(payload) });
        var text = await res.text();
        try { return JSON.parse(text); }
        catch (e) { return { ok: false, error: 'HTTP ' + res.status + ': ' + text.slice(0, 300) }; }
    } catch (e) {
        return { ok: false, error: e.message };
    }
}

// Поля, которые в форме изменили. Имя не размножаем: одинаковые имена у разных задач никому не нужны.
function _changedFields(payload) {
    var out = {};
    Object.keys(_formBaseline).forEach(function(k) {
        if (k === 'name' || payload[k] === undefined) return;
        if (String(payload[k]) !== String(_formBaseline[k])) out[k] = payload[k];
    });
    // Расписание — единое целое: сменили режим — переносим его вместе с настройками.
    if ('schedule_mode' in out) { out.cron = payload.cron; out.schedule_json = payload.schedule_json; }
    return out;
}

function _bulkPayload(target, changed) {
    var p = Object.assign({ id: target.id }, changed);
    var mode = p.schedule_mode !== undefined ? p.schedule_mode : (target.schedule_mode || 'off');
    var json = p.schedule_json !== undefined ? p.schedule_json : (target.schedule_json || '');
    // Тот же сброс счётчика, что у основной задачи, но по её собственному прошлому.
    if (mode !== 'off' && (target.schedule_mode !== mode || (target.schedule_json || '') !== json)) {
        p.sched_runs = '0';
        p.sched_started_at = new Date().toISOString();
    }
    return p;
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
    var res  = await fetch('/tasker/save', {
        method:'POST', headers:{'Content-Type':'application/json'},
        body: JSON.stringify({ name:newName, executor:s.executor, script_path:s.script_path, args:s.args,
            enabled:'false', cron:s.cron, on_overlap:s.on_overlap, max_threads:s.max_threads,
            timeout_seconds:s.timeout_seconds,
            use_venv:s.use_venv, schedule_mode:s.schedule_mode, schedule_json:s.schedule_json })
    });
    var data = await res.json();
    if (!data.ok) { Dialog.error(data.error || 'Duplicate: save failed'); return; }
    try {
        var pRes  = await fetch('/tasker/payload?id=' + encodeURIComponent(id));
        var pData = await pRes.json();
        if (pData.schema || pData.values)
            await fetch('/tasker/payload', { method:'POST', headers:{'Content-Type':'application/json'},
                body: JSON.stringify({ id:data.id, schema:pData.schema, values:pData.values }) });
    } catch(e) {}
    await loadList();
    selectRow(data.id);
}

async function deleteSchedule(id, name) {
    if (!(await Dialog.confirm('Eliminate "' + (name||id) + '"?', '✕ Eliminate', true))) return;
    await fetch('/tasker/delete', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
    await _afterDelete();
}

async function deleteSchedules(ids) {
    if (ids.length === 1) {
        var one = schedules.find(function(x) { return x.id === ids[0]; }) || {};
        return deleteSchedule(ids[0], one.name || '');
    }
    var names = ids.map(function(id) {
        var s = schedules.find(function(x) { return x.id === id; }) || {};
        return '  ' + (s.name || id);
    });
    if (!(await Dialog.confirm('Eliminate ' + ids.length + ' tasks?\n' + names.join('\n'), '✕ Eliminate', true))) return;
    for (var i = 0; i < ids.length; i++) {
        await fetch('/tasker/delete', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:ids[i]}) });
    }
    await _afterDelete();
}

async function _afterDelete() {
    selectedIds = new Set();
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
        var res  = await fetch('/tasker/payload?id=' + encodeURIComponent(id));
        var data = await res.json();
        await navigator.clipboard.writeText(JSON.stringify({ schema:data.schema, values:data.values }, null, 2));
        Dialog.alert('Payload JSON copied to clipboard.', 'Exported');
    } catch(e) { Dialog.error(e.message); }
}

async function clearPayload(id, name) {
    // payload_values ровно пустая — единственное состояние, при котором планировщик
    // не перезапишет args базой-64. "{}" его не останавливает — скрипту
    // всё равно приедет позиционный аргумент e30=.
    if (!(await Dialog.confirm('Delete payload schema and values of "' + (name||id) + '"? Script will stop receiving the base64 argument.', '🗑 Clear payload', true))) return;
    try {
        var res  = await fetch('/tasker/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id, schema:'', values:''}) });
        var data = await res.json();
        if (!data.ok) { Dialog.error(data.error || 'Clear failed'); return; }
        await loadList();
        if (selectedId === id) selectRow(id);
    } catch(e) { Dialog.error(e.message); }
}

async function openScriptFile(filePath) {
    try {
        var res  = await fetch('/tasker/open-file?path=' + encodeURIComponent(filePath));
        var data = await res.json();
        if (!data.ok) Dialog.error(data.error || 'Cannot open file');
    } catch(e) { Dialog.error(e.message); }
}

async function openScriptFolder(filePath) {
    try {
        var res  = await fetch('/tasker/open-folder?path=' + encodeURIComponent(filePath));
        var data = await res.json();
        if (!data.ok) Dialog.error(data.error || 'Cannot open folder');
    } catch(e) { Dialog.error(e.message); }
}

async function toggleEnabled(id, current) {
    var newVal = current === 'true' ? 'false' : 'true';
    var s = schedules.find(function(x) { return x.id === id; });
    if (!s) return;
    await fetch('/tasker/save', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(Object.assign({}, s, {enabled: newVal})) });
    await loadList();
}

async function resumeSchedule(id) {
    var res = await fetch('/tasker/resume', { method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({id:id}) });
    if (!res.ok) { alert((await res.json()).error || 'Could not resume schedule'); return; }
    await loadList();
}

/// Сколько прогонов заказано полем рядом с кнопкой Run. Поля может не быть:
/// карточка задачи перерисовывается, и вызов способен пережить её замену.
function _runCount() {
    var el = document.getElementById('runCount');
    var n  = parseInt((el && el.value) || '1', 10);
    if (!isFinite(n) || n < 1) return 1;
    return Math.min(n, MAX_BURST);
}

async function runNow(id) {
    var res = await fetch('/tasker/run', {
        method:  'POST',
        headers: {'Content-Type':'application/json'},
        body:    JSON.stringify({id: id, count: _runCount()})
    });
    if (!res.ok) {
        var err = await res.json().catch(function() { return {}; });
        Dialog.error(err.error || ('Run failed: HTTP ' + res.status));
        return;
    }
    await loadList();
    startSseOutput(id);
}

async function buildCsx(id) {
    var btn = event.target;
    btn.disabled = true; btn.textContent = '⏳...';
    try {
        var res  = await fetch('/tasker/build', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
        var data = await res.json();
        if (data.ok) { btn.textContent = '✅ OK'; btn.style.borderColor = 'var(--green)'; btn.style.color = 'var(--green)'; }
        else { btn.textContent = '❌ Err'; btn.style.borderColor = 'var(--red)'; btn.style.color = 'var(--red)'; await Dialog.alert(data.errors.join('\n'), '🔨 Check errors'); }
    } catch(e) { btn.textContent = '❌'; }
    finally { setTimeout(function() { btn.disabled = false; btn.textContent = '🔨 Check'; btn.style.borderColor = 'var(--steel)'; btn.style.color = 'var(--steel)'; }, 3000); }
}

/// Сколько прогонов ждёт очереди. Остановка всех инстансов без этого выглядела бы
/// как остановка задачи, а через две секунды очередь подняла бы следующий.
async function _pendingCount(id) {
    try {
        var list = await (await fetch('/tasker/queue?id=' + encodeURIComponent(id))).json();
        return (list || []).filter(function(q) { return q.status === 'pending'; }).length;
    } catch (e) { return 0; }
}

async function _offerQueueDrop(id) {
    var pending = await _pendingCount(id);
    if (!pending) return;
    if (await Dialog.confirm(pending + ' more run(s) are queued and will start as threads free up.\nDrop them too?', '■ Interrupt', true))
        await fetch('/tasker/clear-queue', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
}

async function stopNow(id) {
    var res       = await fetch('/tasker/instances?id=' + encodeURIComponent(id));
    var instances = await res.json().catch(function() { return []; }) || [];
    if (instances.length === 0) {
        await fetch('/tasker/stop', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
        await _offerQueueDrop(id);
    } else if (instances.length === 1) {
        if (!(await Dialog.confirm('Kill running instance?', '■ Interrupt', true))) return;
        await fetch('/tasker/kill-instance', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id, runId:instances[0].runId}) });
    } else {
        var chosen = await _pickInstanceToKill(id, instances);
        if (chosen === null) return;
        var url = chosen === '__all__' ? '/tasker/stop' : '/tasker/kill-instance';
        var body = chosen === '__all__' ? {id:id} : {id:id, runId:chosen};
        await fetch(url, { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify(body) });
        // Убийство одного инстанса очередь не трогает: человек снял именно его.
        if (chosen === '__all__') await _offerQueueDrop(id);
    }
    await loadList();
}

async function restartNow(id) {
    await fetch('/tasker/stop', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({id: id}) });
    await new Promise(function(r) { setTimeout(r, 800); });
    await fetch('/tasker/run', { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({id: id, count: _runCount()}) });
    await loadList();
    startSseOutput(id);
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
    await fetch('/tasker/kill-instance', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id, runId:runId}) });
}

async function clearQueue(id) {
    await fetch('/tasker/clear-queue', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:id}) });
}

var OVERLAP_LABELS = { skip: 'Skip', parallel: 'Queue', kill_restart: 'Kill & restart' };

/// Подсказка под блоком Threads: словами то же, что решает FireSchedule.
function onThreadsChanged() {
    var hint  = document.getElementById('f_threads_hint');
    var tEl   = document.getElementById('f_max_threads');
    var oEl   = document.getElementById('f_on_overlap');
    if (!hint || !tEl || !oEl) return;

    var n    = parseInt(tEl.value, 10) || 1;
    var full = { skip:         'extra scheduled runs are dropped',
                 parallel:     'extra runs queue up and start as threads free up',
                 kill_restart: 'running instances are stopped and started again' }[oEl.value] || '';

    hint.textContent = n > 1
        ? 'The scheduler tops up to ' + n + ' threads, starts are spread over 1-5 s. '
          + 'When all of them are busy — ' + full + '.'
        : 'A single thread. While it is busy — ' + full + '.';
}

function onOverlapChanged() { onThreadsChanged(); }

// ── Resizers ──────────────────────────────────────────────────────────────────

function restoreLayout() {
    var st = _PS.load();
    if (st.listW) document.getElementById('tasksPanel').style.width = st.listW + 'px';
    if (st.detailH && st.bottomH) {
        var tp = document.getElementById('detailPanel');
        var bp = document.getElementById('bottomPanels');
        tp.style.flex = 'none'; tp.style.height = st.detailH + 'px'; bp.style.height = st.bottomH + 'px';
    }
    // Вкладка Logs переименована в Traffic — переносим сохранённый выбор.
    if (st.activeTab === 'logs') st.activeTab = 'traffic';
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
        var res  = await fetch('/tasker/payload?id=' + encodeURIComponent(id));
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
    if (typeof parsed.schema !== 'string' || typeof parsed.values !== 'string') { alert('Missing schema or values fields'); return; }
    try {
        var res = await fetch('/tasker/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:_importPayloadId, schema:parsed.schema, values:parsed.values}) });
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
        // Пустой вариант из списка не выбрасываем: в шаблонах он значащий.
        // simroute.bolt.lgn на пустом proxy_iso подставляет location, и без
        // пустого пункта выбрать этот путь в интерфейсе нечем.
        var opts = (f.options||'').trim()
            ? f.options.split(',').map(function(o){return o.trim();})
            : [];
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
        var res  = await fetch('/tasker/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:pmScheduleId, schema:JSON.stringify(pmSchema), values:JSON.stringify(pmValues)}) });
        var data = await res.json();
        if (data.ok) closeSchemaModal(); else Dialog.error(data.error||'Save failed');
    } catch(e) { Dialog.error(e.message); }
}

async function saveValues() {
    if (!pmScheduleId) return;
    document.querySelectorAll('#valuesBody [data-vkey]').forEach(function(el) { pmValues[el.dataset.vkey] = el.type==='checkbox'?(el.checked?'true':'false'):el.value; });
    try {
        var res  = await fetch('/tasker/payload', { method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({id:pmScheduleId, schema:JSON.stringify(pmSchema), values:JSON.stringify(pmValues)}) });
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

// Системный диалог выбора пути. Из страницы полный путь получить нельзя —
// браузер отдаёт только имя файла, — поэтому диалог открывает само приложение,
// а сюда возвращается уже абсолютный путь. Отмена приходит пустой строкой и
// поле не трогает.
function pickPath(mode) {
    var field = document.getElementById('f_script_path');
    var exec  = (document.getElementById('f_executor') || {}).value || '';

    var extByExec = {
        'xml': 'xml', 'csx': 'csx',
        'python': 'py', 'node': 'js', 'ts-node': 'js', 'ps1': 'ps1',
        'exe': 'exe', 'cmd': 'cmd', 'bat': 'cmd', 'bash': 'sh'
    };

    var url = '/tasker/pick?mode=' + encodeURIComponent(mode)
            + '&ext=' + encodeURIComponent(extByExec[exec] || '')
            + '&start=' + encodeURIComponent(field.value || '');

    fetch(url)
        .then(function(r) { return r.json(); })
        .then(function(d) {
            if (!d.ok)   { alert('Could not open the dialog: ' + (d.error || '')); return; }
            if (d.path && field.isConnected) { field.value = d.path; markTaskDirty(); }
        })
        .catch(function(e) { alert('Could not open the dialog: ' + e); });
}
