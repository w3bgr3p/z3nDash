/* zpxml.js — склейка: ввод файла, панель деталей, панорама и зум, тулбар. */

let selected = null, filterText = '';
let isDragging = false, startX, startY, startPX, startPY;

// ── File input ──────────────────────────────────────────────────────────────

document.getElementById('file-input').addEventListener('change', function () {
    if (this.files[0]) readFile(this.files[0]);
});

const dz = document.getElementById('drop-zone');
dz.addEventListener('click', () => document.getElementById('file-input').click());
dz.addEventListener('dragover', e => { e.preventDefault(); dz.classList.add('over'); });
dz.addEventListener('dragleave', () => dz.classList.remove('over'));
dz.addEventListener('drop', e => {
    e.preventDefault(); dz.classList.remove('over');
    if (e.dataTransfer.files[0]) readFile(e.dataTransfer.files[0]);
});

/// Открыть шаблон из байтов. Вынесено отдельно от readFile, чтобы файл можно
/// было подать и не через выбор в диалоге.
function openBuffer(buffer, fileName) {
    try {
        const doc = ZpDoc.open(buffer, fileName);
        buildFromDoc(doc);
        setStatus('✓ ' + fileName + ' · ' + stepList.length + ' steps · ' + edges.length + ' edges', 'ok');
        document.getElementById('empty').style.display = 'none';
        lastNote = '';
        layoutMode = hasCanvasCoords() ? 'canvas' : 'vertical';
        document.getElementById('btn-layout').textContent = LAYOUTS[layoutMode];
        render();
        resetView();
    } catch (err) {
        setStatus('error: ' + err.message, 'err');
        console.error(err);
    }
}

/// Единственный путь обновления экрана после правки документа. Модель всегда
/// перечитывается из документа, поэтому рассинхрон невозможен.
function refresh(note) {
    buildFromDoc(ZpDoc.doc);
    render();
    if (note) lastNote = note;
    updateStatus();
}

/// Строка состояния пересчитывается по документу, а не остаётся с числами
/// момента загрузки: после правок их становится больше или меньше.
let lastNote = '';

function updateStatus() {
    if (!ZpDoc.doc) return;
    const parts = ['✓ ' + ZpDoc.fileName,
                   stepList.length + ' steps',
                   edges.length + ' edges'];
    if (lastNote) parts.push(lastNote);
    if (ZpDoc.dirty) parts.push('modified');
    setStatus(parts.join(' · '), 'ok');
}

function updateDirtyMark() { updateStatus(); }

function readFile(file) {
    setStatus('reading the file…', 'info');
    const reader = new FileReader();
    reader.onload  = e => openBuffer(e.target.result, file.name);
    reader.onerror = () => setStatus('could not read the file', 'err');
    reader.readAsArrayBuffer(file);
}

/// Отдать шаблон файлом. Кодировка и объявление — исходные: UTF-8 ProjectMaker
/// не открывает.
function saveFile() {
    if (!ZpDoc.doc) { setStatus('nothing to save', 'err'); return; }
    const blob = new Blob([ZpDoc.bytes()], { type: 'application/xml' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = ZpDoc.fileName || 'template.xml';
    document.body.appendChild(a);
    a.click();
    a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    ZpDoc.dirty = false;
    setStatus('saved ' + a.download, 'ok');
}

function setStatus(msg, type) {
    const s = document.getElementById('status');
    s.textContent = msg;
    s.className = type === 'err' ? 'err' : type === 'ok' ? 'ok' : '';
}

function clearAll() {
    steps = {}; edges = []; stepList = []; terminals = [];
    ZpDoc.doc = null; ZpDoc.dirty = false; ZpDoc.undoStack = []; ZpDoc.redoStack = [];
    document.getElementById('canvas').innerHTML = '';
    document.getElementById('edges').innerHTML  = '';
    document.getElementById('empty').style.display = 'flex';
    setStatus('', 'info');
    document.getElementById('file-input').value = '';
    closeDetail();
}

// ── Detail panel ─────────────────────────────────────────────────────────────

/// Выделение блока целиком: панель показывает сводку по шагу.
function selectStep(stepId) {
    selected = { stepId, branchIndex: null };
    renderNodes();
    showStepDetail(steps[stepId]);
}

function showStepDetail(step) {
    document.getElementById('detail').classList.add('open');
    document.getElementById('d-label').textContent = stepLabel(step);
    document.getElementById('d-meta').innerHTML =
        '<dt>step id</dt><dd>' + escHtml(step.id) + '</dd>' +
        '<dt>branches</dt><dd>' + step.branches.length + '</dd>' +
        (step.x !== null ? '<dt>canvas</dt><dd>' + step.x + ', ' + step.y + '</dd>' : '') +
        '<dt>reached</dt><dd>' + (step.reachable === false ? 'no — dead code' : 'yes') + '</dd>';
    const db = document.getElementById('d-branches');
    db.innerHTML = '<div class="bi"><div class="bi-head">step branches</div>' +
        step.branches.map((o, i) =>
            '<div class="sib' + (o.disabled ? ' off' : '') + '" data-i="' + i + '">#' + i +
            ' · ' + escHtml(branchRowLabel(o)) + '</div>').join('') + '</div>';
    db.querySelectorAll('.sib').forEach(el =>
        el.addEventListener('click', () => selectBranch(step.id, +el.dataset.i)));
}

/// Selection is one branch row, not the whole block: a step on the canvas
/// stacks many actions, and showing all of them at once buries the one that was
/// clicked.
function selectBranch(stepId, branchIndex) {
    selected = { stepId, branchIndex };
    renderNodes();
    showDetail(steps[stepId], branchIndex);
}

/// Where the transition of this branch actually goes, spelled out. An empty
/// OnSuccess is not "no transition": the runtime falls through to the next
/// branch of the same step, and ends the route on the last one.
function targetText(raw) {
    const ref = parseRef(raw);
    if (!ref) return null;
    const step = steps[ref.stepId];
    const b    = step.branches[ref.branchIndex];
    return stepLabel(step) + ' · #' + ref.branchIndex +
           (b ? ' ' + branchRowLabel(b) : '') + (ref.dangling ? ' (branch not in file)' : '');
}

function showDetail(step, bi) {
    const b = step.branches[bi];
    if (!b) return;
    document.getElementById('detail').classList.add('open');
    document.getElementById('d-label').textContent = branchRowLabel(b);

    document.getElementById('d-meta').innerHTML =
        '<dt>action</dt><dd>' + escHtml(b.type + ' / ' + b.action) + '</dd>' +
        '<dt>branch</dt><dd>#' + bi + ' of ' + step.branches.length + '</dd>' +
        '<dt>branch id</dt><dd>' + escHtml(b.id) + '</dd>' +
        '<dt>step</dt><dd>' + escHtml(stepLabel(step)) + '</dd>' +
        '<dt>step id</dt><dd>' + escHtml(step.id) + '</dd>' +
        (step.x !== null ? '<dt>canvas</dt><dd>' + step.x + ', ' + step.y + '</dd>' : '') +
        '<dt>reached</dt><dd>' + (step.reachable === false ? 'no — dead code' : 'yes') + '</dd>' +
        (b.disabled ? '<dt>state</dt><dd>disabled — not executed</dd>' : '') +
        (b.optional ? '<dt>state</dt><dd>optional — errors do not fail the route</dd>' : '') +
        (b.outputVariable ? '<dt>output</dt><dd>' + escHtml(b.outputVariable) + '</dd>' : '');

    const db = document.getElementById('d-branches');
    db.innerHTML = '';

    if (b.code && b.code.trim()) {
        const code = document.createElement('div');
        code.className = 'bi';
        code.innerHTML = '<div class="bi-head">code</div>' +
                         '<div class="bi-code full">' + escHtml(b.code.trim()) + '</div>';
        db.appendChild(code);
    }

    const links = document.createElement('div');
    links.className = 'bi';
    let html = '<div class="bi-head">transitions</div>';
    const okTarget = targetText(b.onSuccess);
    html += okTarget
        ? '<div class="bi-link ok">→ ok: ' + escHtml(okTarget) + '</div>'
        : '<div class="bi-link ok dim">→ ok: ' +
          (bi + 1 < step.branches.length ? 'next branch (#' + (bi + 1) + ')' : 'end of route') + '</div>';
    const errTarget = targetText(b.onError);
    html += errTarget
        ? '<div class="bi-link err">→ err: ' + escHtml(errTarget) + '</div>'
        : '<div class="bi-link err dim">→ err: ' +
          (b.optional ? 'skipped, branch is optional' : 'route fails') + '</div>';
    b.cases.forEach(c => {
        const t = targetText(c.val);
        html += '<div class="bi-link case' + (t ? '' : ' dim') + '">→ ' +
                (c.isDefault ? 'Default' : c.number + ': "' + escHtml(c.key) + '"') + ': ' +
                escHtml(t || 'no arrow') + '</div>';
    });
    links.innerHTML = html;
    db.appendChild(links);

    // The other actions of the same block, one line each, so the step is still
    // navigable without dumping every branch body into the panel.
    const sib = document.createElement('div');
    sib.className = 'bi';
    sib.innerHTML = '<div class="bi-head">step branches</div>' +
        step.branches.map((o, i) =>
            '<div class="sib' + (i === bi ? ' on' : '') + (o.disabled ? ' off' : '') +
                 '" data-i="' + i + '">#' + i + ' · ' + escHtml(branchRowLabel(o)) + '</div>').join('');
    sib.querySelectorAll('.sib').forEach(el =>
        el.addEventListener('click', () => selectBranch(step.id, +el.dataset.i)));
    db.appendChild(sib);
}

function escHtml(s) {
    return String(s ?? '')
        .replace(/&/g, '&amp;').replace(/</g, '&lt;')
        .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function closeDetail() {
    document.getElementById('detail').classList.remove('open');
    selected = null;
    renderNodes();
}

// ── Pan / zoom ───────────────────────────────────────────────────────────────

const wrap = document.getElementById('canvas-wrap');
wrap.addEventListener('mousedown', e => {
    if (e.target.closest('.block') || e.target.closest('#detail')) return;
    isDragging = true;
    startX = e.clientX; startY = e.clientY; startPX = panX; startPY = panY;
    wrap.classList.add('grabbing');
});
window.addEventListener('mousemove', e => {
    if (!isDragging) return;
    panX = startPX + (e.clientX - startX);
    panY = startPY + (e.clientY - startY);
    applyTransform();
});
window.addEventListener('mouseup', () => { isDragging = false; wrap.classList.remove('grabbing'); });
wrap.addEventListener('wheel', e => {
    e.preventDefault();
    scale = Math.max(0.1, Math.min(4, scale * (e.deltaY > 0 ? 0.88 : 1.12)));
    applyTransform(); updateMini();
}, { passive: false });
wrap.addEventListener('click', e => {
    if (!e.target.closest('.block') && !e.target.closest('#detail')) closeDetail();
});

// ── Toolbar wiring ───────────────────────────────────────────────────────────

// ── Detail panel resizer ─────────────────────────────────────────────────────

const DETAIL_W_KEY = 'zpxml-detail-w';

function setDetailWidth(px) {
    const w = Math.max(200, Math.min(Math.round(px), window.innerWidth - 120));
    document.documentElement.style.setProperty('--detail-w', w + 'px');
    // Browser storage can be blocked or empty; the panel must work regardless.
    try { localStorage.setItem(DETAIL_W_KEY, String(w)); } catch (e) { /* ignore */ }
    return w;
}

try {
    const saved = parseInt(localStorage.getItem(DETAIL_W_KEY) || '', 10);
    if (isFinite(saved)) setDetailWidth(saved);
} catch (e) { /* ignore */ }

(function () {
    const grip = document.getElementById('detail-resizer');
    const panel = document.getElementById('detail');
    let dragging = false;

    grip.addEventListener('mousedown', e => {
        e.preventDefault(); e.stopPropagation();
        dragging = true;
        grip.classList.add('dragging');
        document.body.style.userSelect = 'none';
    });
    window.addEventListener('mousemove', e => {
        if (!dragging) return;
        e.preventDefault();
        setDetailWidth(panel.getBoundingClientRect().right - e.clientX);
    });
    window.addEventListener('mouseup', () => {
        if (!dragging) return;
        dragging = false;
        grip.classList.remove('dragging');
        document.body.style.userSelect = '';
    });
    // A double click restores the default width.
    grip.addEventListener('dblclick', e => { e.stopPropagation(); setDetailWidth(290); });
})();

document.getElementById('btn-clear'  ).addEventListener('click', clearAll);
document.getElementById('btn-zoom-in').addEventListener('click', zoomIn);
document.getElementById('btn-zoom-out').addEventListener('click', zoomOut);
document.getElementById('btn-reset'  ).addEventListener('click', resetView);
document.getElementById('btn-layout' ).addEventListener('click', toggleLayout);
document.getElementById('btn-detail-close').addEventListener('click', closeDetail);
document.getElementById('search').addEventListener('input', function () { filterNodes(this.value); });
document.getElementById('btn-save').addEventListener('click', saveFile);

// ── Undo / redo ──────────────────────────────────────────────────────────────

/// Стоит ли отдать клавишу полю ввода. Отдельной функцией, потому что target
/// события — не обязательно элемент.
function isTextField(target) {
    return !!(target && typeof target.matches === 'function' && target.matches('input, textarea'));
}

document.addEventListener('keydown', e => {
    if (!ZpDoc.doc) return;
    const key  = e.key.toLowerCase();
    const undo = (e.ctrlKey || e.metaKey) && key === 'z' && !e.shiftKey;
    const redo = (e.ctrlKey || e.metaKey) && (key === 'y' || (key === 'z' && e.shiftKey));
    if (!undo && !redo) return;
    // В полях ввода Ctrl+Z должен работать как обычно. Проверка через
    // необязательный вызов: target не обязан быть элементом — у document,
    // например, matches нет вовсе, и обращение к нему рушит обработчик.
    if (isTextField(e.target)) return;
    e.preventDefault();
    if (undo ? ZpDoc.undo() : ZpDoc.redo()) { closeDetail(); refresh(undo ? 'undo' : 'redo'); }
});

window.addEventListener('beforeunload', e => {
    if (!ZpDoc.dirty) return;
    e.preventDefault();
    e.returnValue = '';
});

// ── Edit mode ────────────────────────────────────────────────────────────────

let editing = false;

function toggleEdit() {
    editing = !editing;
    document.body.classList.toggle('editing', editing);
    document.getElementById('btn-edit').classList.toggle('active', editing);
    setStatus(editing ? 'edit mode on' : 'edit mode off', 'info');
    updateDirtyMark();
}

document.getElementById('btn-edit').addEventListener('click', toggleEdit);

// ── Dragging a block ─────────────────────────────────────────────────────────

/// Перетаскивание блока за подложку. Документ во время движения не трогается:
/// двигается только элемент на экране, мутация происходит один раз, на
/// отпускании — иначе история забьётся промежуточными состояниями.
function beginStepDrag(step, div, ev) {
    const startX = ev.clientX, startY = ev.clientY;
    const from = positions[step.id];
    let dx = 0, dy = 0, moved = false;
    div.classList.add('dragging');

    const onMove = e => {
        dx = (e.clientX - startX) / scale;
        dy = (e.clientY - startY) / scale;
        if (Math.abs(dx) > 2 || Math.abs(dy) > 2) moved = true;
        div.style.left = (from.x + dx) + 'px';
        div.style.top  = (from.y + dy) + 'px';
    };

    const onUp = () => {
        window.removeEventListener('mousemove', onMove);
        window.removeEventListener('mouseup', onUp);
        div.classList.remove('dragging');
        if (!moved) { render(); return; }
        // Сдвиг применяем к исходным x/y из файла: раскладка отсчитывается от
        // левого верхнего угла холста, а координаты в файле — от своего начала.
        if (ZpDoc.moveStep(step.id, step.x + dx, step.y + dy))
            refresh('moved "' + stepLabel(step) + '"');
    };

    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
}
