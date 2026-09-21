/* zpxml-debug.js — пульт отладки: команды серверной сессии и приём её событий.
   Про устройство плеера не знает: шлёт команды и рисует то, что пришло. */

const DBG_DIR_KEY = 'zpxml-project-dir';

let dbgState   = 'idle';
let dbgCurrent = null;      // { stepId, branchId } — где стоит исполнение
let dbgSnap    = null;      // последний снимок состояния

// ── Команды ──────────────────────────────────────────────────────────────────

/// Ответ разбирается вручную, а не через r.json(): когда маршрута нет,
/// сервер отвечает пустым 200, и r.json() падает с «Unexpected end of JSON
/// input» — по такому сообщению не понять, что произошло. Здесь в ошибку идёт
/// наблюдение: код ответа и то, что тело пустое.
async function dbgPost(action, body) {
    let r;
    try {
        r = await fetch('/dbg/' + action, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body || {})
        });
    } catch (e) {
        return { ok: false, error: 'запрос не ушёл: ' + e.message };
    }

    const text = await r.text();
    if (!text.trim())
        return { ok: false, error: 'HTTP ' + r.status + ', пустой ответ — обработчик /dbg не отвечает' };

    try {
        return JSON.parse(text);
    } catch (e) {
        return { ok: false, error: 'HTTP ' + r.status + ', ответ не JSON: ' + text.slice(0, 120) };
    }
}

async function dbgStart() {
    if (!ZpDoc.doc) { setStatus('nothing to debug', 'err'); return; }

    const dir = document.getElementById('dbg-dir').value.trim();
    // Каталог запоминается: браузер не отдаёт путь выбранного файла, и вводить
    // его заново на каждый запуск — лишнее.
    try { localStorage.setItem(DBG_DIR_KEY, dir); } catch (e) { /* ignore */ }

    setStatus('debug: starting…', 'info');
    const res = await dbgPost('start', {
        xml: ZpDoc.serialize(), projectDir: dir, headless: false
    });
    if (!res.ok) { setStatus('debug: ' + (res.error || 'не запустилось'), 'err'); return; }
    setStatus('debug: session started', 'ok');
}

async function dbgRunTo(stepId, branchId) {
    const res = await dbgPost('runto', { stepId, branchId });
    if (!res.ok) setStatus('debug: ' + (res.error || 'не принято'), 'err');
}

// ── Состояние кнопок ─────────────────────────────────────────────────────────

function dbgSetState(state) {
    dbgState = state;
    document.getElementById('dbg-state').textContent = state;
    document.body.classList.toggle('debugging', state !== 'idle');

    const on = (id, enabled) => { document.getElementById(id).disabled = !enabled; };
    on('dbg-start', state === 'idle' || state === 'finished' || state === 'failed');
    on('dbg-step',  state === 'paused');
    on('dbg-run',   state === 'paused');
    on('dbg-pause', state === 'running');
    on('dbg-stop',  state === 'paused' || state === 'running');
}

// ── Лог и навигация ──────────────────────────────────────────────────────────

/// Лог плеера копится в памяти. Предел нужен: шаблон на тысячи веток иначе
/// съест вкладку.
const dbgLines = [];

function dbgLog(line) {
    dbgLines.push(line);
    if (dbgLines.length > 500) dbgLines.shift();
    const host = document.getElementById('dbg-log');
    if (host) { host.textContent = dbgLines.join('\n'); host.scrollTop = host.scrollHeight; }
}

/// На тринадцати блоках текущая ветка регулярно оказывается за экраном.
function dbgScrollToCurrent() {
    if (!dbgCurrent || !steps[dbgCurrent.stepId] || !positions[dbgCurrent.stepId]) return;
    const p = positions[dbgCurrent.stepId];
    const wrap = document.getElementById('canvas-wrap').getBoundingClientRect();
    panX = wrap.width  / 2 - (p.x + NW / 2) * scale;
    panY = wrap.height / 2 - (p.y + stepHeight(steps[dbgCurrent.stepId]) / 2) * scale;
    applyTransform();
}

// ── Вкладки панели ───────────────────────────────────────────────────────────

/// На остановке панель сама переходит на debug, а по клику на строку
/// возвращается к branch: во время отладки смотришь состояние, а правя ветку —
/// саму ветку.
function dbgShowTab(tab) {
    document.querySelectorAll('.dtab').forEach(b => b.classList.toggle('active', b.dataset.tab === tab));
    document.getElementById('d-debug').classList.toggle('on', tab === 'debug');
    const hide = tab === 'debug';
    document.getElementById('d-meta').style.display     = hide ? 'none' : '';
    document.getElementById('d-branches').style.display = hide ? 'none' : '';
}

// ── Снимок состояния ─────────────────────────────────────────────────────────

/// Переменных в рабочем шаблоне 85, поэтому изменённые последним шагом идут
/// наверх, а остальные — под фильтром по имени.
function dbgRenderSnapshot(snap) {
    const host = document.getElementById('d-debug');
    if (!host) return;

    const changed = new Set(snap.changed || []);
    const vars = [...(snap.vars || [])].sort((a, b) =>
        (changed.has(b.name) ? 1 : 0) - (changed.has(a.name) ? 1 : 0) ||
        a.name.localeCompare(b.name));

    // Сессия живёт на сервере и переживает смену файла на странице. Если её
    // текущей ветки нет в открытом шаблоне, значит отлаживается другой — и
    // молчать об этом нельзя: панель выглядела бы исправной, показывая чужое.
    const foreign = !!snap.current && !steps[snap.current.stepId];

    const where = snap.current
        ? (steps[snap.current.stepId]
            ? (() => {
                const st = steps[snap.current.stepId];
                const i  = st.branches.findIndex(b => b.id === snap.current.branchId);
                return stepLabel(st) + (i >= 0 ? ' · #' + i + ' ' + branchRowLabel(st.branches[i]) : '');
              })()
            : 'ветка ' + snap.current.branchId.substring(0, 8) + ' — не из открытого шаблона')
        : 'маршрут закончен';

    host.innerHTML =
        (foreign
            ? '<div class="bi warn"><div class="bi-head">внимание</div>' +
              'Сессия отладки идёт по другому шаблону, не по тому, что открыт на ' +
              'странице. Останови её и запусти заново, иначе позиция и переменные ' +
              'относятся к чужому прогону.</div>'
            : '') +
        '<div class="bi"><div class="bi-head">position</div><div class="dl">' +
            '<dt>state</dt><dd>' + escHtml(snap.state || '') + '</dd>' +
            '<dt>next</dt><dd>' + escHtml(where) + '</dd>' +
            '<dt>ran</dt><dd>' + (snap.ran || 0) + '</dd>' +
            (snap.last && snap.last.message
                ? '<dt>last</dt><dd>' + escHtml(snap.last.message) + '</dd>' : '') +
        '</div></div>' +
        (snap.profile
            ? '<div class="bi"><div class="bi-head">profile</div><div class="dl">' +
                '<dt>name</dt><dd>' + escHtml(snap.profile.name || '') + '</dd>' +
                '<dt>email</dt><dd>' + escHtml(snap.profile.email || '') + '</dd>' +
                '<dt>login</dt><dd>' + escHtml(snap.profile.login || '') + '</dd>' +
              '</div></div>'
            : '') +
        '<div class="bi"><div class="bi-head">player log</div><pre id="dbg-log"></pre></div>' +
        '<div class="bi"><div class="bi-head">variables</div>' +
            '<input id="dbg-var-filter" placeholder="filter…" spellcheck="false">' +
            '<div class="dl params" id="dbg-vars">' +
            vars.map(v => '<dt' + (changed.has(v.name) ? ' class="changed"' : '') + '>' +
                escHtml(v.name) + '</dt><dd>' + escHtml(v.value) + '</dd>').join('') +
            '</div></div>';

    const log = document.getElementById('dbg-log');
    if (log) { log.textContent = dbgLines.join('\n'); log.scrollTop = log.scrollHeight; }
    dbgBindVarFilter();
}

function dbgBindVarFilter() {
    const input = document.getElementById('dbg-var-filter');
    if (!input) return;
    input.addEventListener('keydown', e => e.stopPropagation());
    input.addEventListener('input', () => {
        const q = input.value.toLowerCase().trim();
        document.querySelectorAll('#dbg-vars dt').forEach(dt => {
            const hide = q && !dt.textContent.toLowerCase().includes(q);
            dt.style.display = hide ? 'none' : '';
            if (dt.nextElementSibling) dt.nextElementSibling.style.display = hide ? 'none' : '';
        });
    });
}

// ── Поток событий ────────────────────────────────────────────────────────────

/// Подписка восстанавливается при обрыве: иначе после перезапуска приложения
/// пульт молча перестаёт показывать состояние, хотя выглядит рабочим.
function dbgListen() {
    let es;
    try { es = new EventSource('/dbg/events'); }
    catch (e) { setTimeout(dbgListen, 5000); return; }

    const onEvent = e => {
        let ev;
        try { ev = JSON.parse(e.data); } catch (err) { return; }

        if (ev.log !== undefined) { dbgLog(ev.log); return; }

        if (ev.state) dbgSetState(ev.state);
        if (ev.current !== undefined) {
            dbgCurrent = ev.current;
            dbgSnap = ev;
            renderNodes();
            dbgScrollToCurrent();
            if (ev.state === 'paused' || ev.state === 'finished') {
                document.getElementById('detail').classList.add('open');
                dbgShowTab('debug');
                dbgRenderSnapshot(ev);
            }
        }
    };

    // SseHub рассылает именованное событие «output», а onmessage ловит только
    // безымянные — без этой подписки поток выглядит живым, но молчит.
    es.addEventListener('output', onEvent);
    es.onmessage = onEvent;

    es.onerror = () => { es.close(); setTimeout(dbgListen, 3000); };
}

// ── Подключение ──────────────────────────────────────────────────────────────

(function () {
    try {
        const saved = localStorage.getItem(DBG_DIR_KEY);
        if (saved) document.getElementById('dbg-dir').value = saved;
    } catch (e) { /* ignore */ }

    document.getElementById('dbg-dir').addEventListener('keydown', e => e.stopPropagation());
    document.getElementById('dbg-start').addEventListener('click', dbgStart);
    document.getElementById('dbg-step' ).addEventListener('click', () => dbgPost('step'));
    document.getElementById('dbg-run'  ).addEventListener('click', () => dbgPost('run'));
    document.getElementById('dbg-pause').addEventListener('click', () => dbgPost('pause'));
    document.getElementById('dbg-stop' ).addEventListener('click', async () => {
        await dbgPost('stop');
        dbgCurrent = null;
        dbgSetState('idle');
        renderNodes();
    });

    document.querySelectorAll('.dtab').forEach(b =>
        b.addEventListener('click', () => dbgShowTab(b.dataset.tab)));

    // Состояние спрашивается при загрузке: сессия могла пережить перезагрузку
    // страницы, и пульт должен показать её, а не «idle».
    fetch('/dbg/state')
        .then(r => r.json())
        .then(s => { if (s && s.state) { dbgSetState(s.state); dbgCurrent = s.current || null; renderNodes(); } })
        .catch(() => { /* сервера нет — пульт остаётся в idle */ });

    dbgListen();
})();
