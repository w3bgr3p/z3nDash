/* zpxml-debug.js — пульт отладки: команды серверной сессии и приём её событий.
   Про устройство плеера не знает: шлёт команды и рисует то, что пришло. */

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

/// Каталог из пути файла. Без регулярки намеренно: разделитель на Windows —
/// обратный слэш, и любое лишнее экранирование при генерации кода превращает
/// выражение в тихо неработающее.
function dirNameOf(fullPath) {
    const cut = Math.max(fullPath.lastIndexOf('\\'), fullPath.lastIndexOf('/'));
    return cut > 0 ? fullPath.slice(0, cut) : fullPath;
}

/// Имя файла из пути. Шаблоны simroute разбирают project.Name на части
/// точкой — «simroute.megapari.xml» даёт им и сервис, и направление, —
/// поэтому подставлять вместо него заглушку нельзя.
function baseNameOf(fullPath) {
    const cut = Math.max(fullPath.lastIndexOf('\\'), fullPath.lastIndexOf('/'));
    return cut >= 0 ? fullPath.slice(cut + 1) : fullPath;
}

async function dbgStart() {
    if (!ZpDoc.doc) { setStatus('nothing to debug', 'err'); return; }

    // Каталог — это каталог открытого шаблона, отдельно его не спрашиваем.
    // Он известен, только если файл открыт системным диалогом: браузерный
    // пикер пути не отдаёт принципиально.
    if (!templatePath) {
        setStatus('debug: открой шаблон кнопкой «open from disk» — из браузерного '
                + 'выбора не виден путь, а он нужен для .env и общего кода', 'err');
        return;
    }

    const dir = dirNameOf(templatePath);

    setStatus('debug: starting…', 'info');
    const res = await dbgPost('start', {
        xml: ZpDoc.serialize(), projectDir: dir,
        name: baseNameOf(templatePath), headless: false
    });
    if (!res.ok) { setStatus('debug: ' + (res.error || 'не запустилось'), 'err'); return; }
    setStatus('debug: session started · ' + dir, 'ok');
}


async function dbgRunTo(stepId, branchId) {
    const before = dbgState;
    dbgSetState('running');
    const res = await dbgPost('runto', { stepId, branchId });
    if (!res.ok) { setStatus('debug: ' + (res.error || 'не принято'), 'err'); dbgSetState(before); }
}

// ── Состояние кнопок ─────────────────────────────────────────────────────────

/// Состояние кнопок — единственный источник правды о том, что сейчас можно.
/// Пока ветка выполняется, step/run/start недоступны: иначе непонятно, идёт
/// работа или уже закончилась, и легко нажать второй раз.
function dbgSetState(state) {
    dbgState = state;

    const label = document.getElementById('dbg-state');
    label.textContent = state === 'running' ? 'running…' : state;
    label.className = 'st-' + state;

    document.body.classList.toggle('debugging', state !== 'idle');
    document.body.classList.toggle('dbg-busy', state === 'running');

    const on = (id, enabled) => { document.getElementById(id).disabled = !enabled; };
    on('dbg-start', state === 'idle' || state === 'finished' || state === 'failed');
    on('dbg-step',  state === 'paused');
    on('dbg-run',   state === 'paused');
    on('dbg-pause', state === 'running');
    on('dbg-stop',  state === 'paused' || state === 'running');
    document.getElementById('dbg-vars-btn').disabled =
        !document.querySelector('#dbg-vars dt') || state === 'idle';
}

/// Команда, после которой сессия работает. Состояние переводится сразу, не
/// дожидаясь события: между нажатием и ответом ветка уже выполняется, и
/// кнопки обязаны это показывать.
async function dbgRunCommand(action) {
    const before = dbgState;
    dbgSetState('running');
    const res = await dbgPost(action);
    if (!res.ok) {
        setStatus('debug: ' + (res.error || 'команда не принята'), 'err');
        dbgSetState(before);
    }
}

// ── Лог и навигация ──────────────────────────────────────────────────────────

/// Лог плеера копится в памяти. Предел нужен: шаблон на тысячи веток иначе
/// съест вкладку.
const dbgLines = [];
let dbgShowNoise = false;

/// Шум браузера. Страница тянет рекламу, аналитику и телеметрию с десятков
/// доменов, прокси их режет, и лог заполняется отказами, к шаблону не
/// относящимися.
///
/// Фильтровать по списку доменов бессмысленно — он бесконечен: bidswitch,
/// adcontroll, adjs.media, pixel.big-media, on.aws и так далее. Поэтому
/// прячем сам вид сообщения: отказ в CONNECT. Если из-за него упадёт шаг,
/// ошибка всё равно придёт от самого шага, с адресом.
function dbgIsNoise(line) {
    if (line.indexOf('[proxy]') !== 0) return false;
    return line.indexOf('CONNECT') >= 0;
}

function dbgLog(line) {
    dbgLines.push(line);
    if (dbgLines.length > 800) dbgLines.shift();
    dbgPaintLog();
}

function dbgPaintLog() {
    const host = document.getElementById('dbg-log');
    if (!host) return;
    const shown = dbgShowNoise ? dbgLines : dbgLines.filter(l => !dbgIsNoise(l));
    host.textContent = shown.join(String.fromCharCode(10));
    host.scrollTop = host.scrollHeight;

    const hidden = dbgLines.length - shown.length;
    const note = document.getElementById('dbg-log-note');
    if (note) note.textContent = hidden > 0 ? 'скрыто фоновых запросов браузера: ' + hidden : '';
}

/// Подтянуть холст к текущей ветке — но только если её не видно.
///
/// Раньше здесь было безусловное центрирование, и каждый шаг возвращал холст
/// на своё усмотрение: ты расставил вид как удобно, нажал step — и всё уехало.
/// Двигать чужой вид без нужды нельзя, поэтому сначала проверяем видимость, а
/// если двигать всё же приходится — сдвигаем на минимум, а не центрируем.
function dbgScrollToCurrent() {
    if (!dbgCurrent || !steps[dbgCurrent.stepId] || !positions[dbgCurrent.stepId]) return;

    const step = steps[dbgCurrent.stepId];
    const p    = positions[dbgCurrent.stepId];
    const rows = blockRows(step).rows.filter(r => r.kind === 'branch');
    const i    = step.branches.findIndex(b => b.id === dbgCurrent.branchId);
    const row  = i >= 0 ? rows[i] : null;

    // Прямоугольник строки в экранных координатах.
    const top    = p.y + PAD + (row ? row.y : 0);
    const height = row ? row.h : ROW_H;
    const x1 = p.x * scale + panX,             x2 = (p.x + NW + PAD * 2) * scale + panX;
    const y1 = top * scale + panY,             y2 = (top + height) * scale + panY;

    const wrap = document.getElementById('canvas-wrap').getBoundingClientRect();
    const m = 40;                                   // поле, чтобы строка не липла к краю
    const visible = x1 >= m && x2 <= wrap.width - m && y1 >= m && y2 <= wrap.height - m;
    if (visible) return;                            // видно — не трогаем вид вовсе

    // Сдвигаем ровно настолько, чтобы строка попала в поле зрения.
    if (x1 < m)                 panX += m - x1;
    else if (x2 > wrap.width - m)  panX -= x2 - (wrap.width - m);
    if (y1 < m)                 panY += m - y1;
    else if (y2 > wrap.height - m) panY -= y2 - (wrap.height - m);

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
            : 'шаг ' + snap.current.branchId.substring(0, 8) + ' — не из открытого шаблона')
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
        '<div class="bi bi-log"><div class="bi-head">player log' +
            '<label class="log-noise"><input type="checkbox" id="dbg-noise"' +
            (dbgShowNoise ? ' checked' : '') + '>browser noise</label></div>' +
            '<pre id="dbg-log"></pre><div id="dbg-log-note"></div></div>';

    const noise = document.getElementById('dbg-noise');
    if (noise) noise.addEventListener('change', () => { dbgShowNoise = noise.checked; dbgPaintLog(); });
    dbgPaintLog();

    dbgRenderVars(snap);
}

/// Переменные живут в своей панели и по умолчанию закрыты: их 85, и держать
/// такую простыню под профилем — значит прокручивать её каждый раз, чтобы
/// добраться до лога.
function dbgRenderVars(snap) {
    const list = document.getElementById('dbg-vars');
    if (!list) return;

    const changed = new Set(snap.changed || []);
    const vars = [...(snap.vars || [])].sort((a, b) =>
        (changed.has(b.name) ? 1 : 0) - (changed.has(a.name) ? 1 : 0) ||
        a.name.localeCompare(b.name));

    // Профиль — такие же данные прогона, как переменные: имя, почта и логин
    // подставляются в поля формы. Место им здесь, а не среди служебного.
    const profile = snap.profile
        ? ['name', 'email', 'login'].map(k =>
            '<dt class="from-profile">Profile.' + k + '</dt><dd>' +
            escHtml(snap.profile[k] || '') + '</dd>').join('')
        : '';

    list.innerHTML = profile + vars.map(v =>
        '<dt' + (changed.has(v.name) ? ' class="changed"' : '') + '>' + escHtml(v.name) + '</dt>' +
        '<dd>' + escHtml(v.value) + '</dd>').join('');

    dbgApplyVarFilter();
}

function dbgToggleVars(show) {
    const panel = document.getElementById('vars-panel');
    const open = show === undefined ? !panel.classList.contains('open') : show;
    panel.classList.toggle('open', open);
    document.getElementById('dbg-vars-btn').classList.toggle('active', open);
}

function dbgApplyVarFilter() {
    const input = document.getElementById('dbg-var-filter');
    if (!input) return;
    const q = input.value.toLowerCase().trim();
    document.querySelectorAll('#dbg-vars dt').forEach(dt => {
        const hide = q && !dt.textContent.toLowerCase().includes(q);
        dt.style.display = hide ? 'none' : '';
        if (dt.nextElementSibling) dt.nextElementSibling.style.display = hide ? 'none' : '';
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
            if (ev.last && ev.last.outcome === 'Failed') {
            // В бою такая ветка обрывает маршрут. Здесь позиция остаётся на ней,
            // и это надо сказать вслух: иначе непонятно, что делать дальше.
            setStatus('debug: шаг упал, позиция осталась на нём — можно поправить '
                    + 'и нажать step ещё раз, либо stop', 'err');
        }
        if (ev.state === 'paused' || ev.state === 'finished') {
                // Выделение идёт следом за исполнением: иначе панель branch
                // продолжает показывать ту ветку, которую кликнули раньше, и
                // кажется, будто шаг никуда не сдвинулся.
                if (ev.current && steps[ev.current.stepId]) {
                    const st = steps[ev.current.stepId];
                    const i  = st.branches.findIndex(b => b.id === ev.current.branchId);
                    if (i >= 0) selected = { stepId: st.id, branchIndex: i };
                }
                renderNodes();
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
    document.getElementById('dbg-start').addEventListener('click', dbgStart);
    document.getElementById('dbg-step' ).addEventListener('click', () => dbgRunCommand('step'));
    document.getElementById('dbg-run'  ).addEventListener('click', () => dbgRunCommand('run'));
    document.getElementById('dbg-pause').addEventListener('click', () => dbgPost('pause'));
    document.getElementById('dbg-stop' ).addEventListener('click', async () => {
        await dbgPost('stop');
        dbgCurrent = null;
        dbgSetState('idle');
        renderNodes();
    });

    document.querySelectorAll('.dtab').forEach(b =>
        b.addEventListener('click', () => dbgShowTab(b.dataset.tab)));

    document.getElementById('dbg-vars-btn').addEventListener('click', () => dbgToggleVars());
    document.getElementById('vars-close').addEventListener('click', () => dbgToggleVars(false));
    document.getElementById('dbg-var-filter').addEventListener('input', dbgApplyVarFilter);
    document.getElementById('dbg-var-filter').addEventListener('keydown', e => e.stopPropagation());

    // Состояние спрашивается при загрузке: сессия могла пережить перезагрузку
    // страницы, и пульт должен показать её, а не «idle».
    fetch('/dbg/state')
        .then(r => r.json())
        .then(s => { if (s && s.state) { dbgSetState(s.state); dbgCurrent = s.current || null; renderNodes(); } })
        .catch(() => { /* сервера нет — пульт остаётся в idle */ });

    dbgListen();
})();
