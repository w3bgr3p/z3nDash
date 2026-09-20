/* zpxml-view.js — геометрия блоков, раскладка, рёбра, отрисовка.
   Знает модель. Ничего не знает про XMLDocument шаблона. */

let layoutMode = 'canvas', scale = 0.72, panX = 24, panY = 24;
const LAYOUTS = { canvas: '▦ canvas', vertical: '↕ vertical', horizontal: '↔ horizontal' };
let positions = {};

// rather than to the block. Switch branches add a row per case below their own.
const NW = 132;      // block width, without the backing plate
// Подложка блока. Сверху она заметно шире остальных сторон: за неё берут весь
// блок, и в 4px приходилось целиться.
const PAD = 4;        // боковые и нижнее поле
const PAD_TOP = 15;   // верхняя полоса — то, за что тянут блок
const ROW_H = 26;    // one branch row
const VAR_H = 16;    // the switch variable line above its cases
const CASE_H = 17;   // one switch case row
const TERM_R = 26;   // Start / GoodEnd / BadEnd circle radius
const GX = 60, GY = 40;

// ── Block geometry ───────────────────────────────────────────────────────────

/// Row offsets inside a block: every branch is one row, and a switch adds a
/// variable line plus a row per case underneath it. Returns the rows and the
/// resulting block height, which is what the layout has to respect — blocks are
/// not all the same height, unlike the card the viewer used to draw.
function blockRows(step) {
    const rows = [];
    let y = 0;
    step.branches.forEach((b, bi) => {
        rows.push({ kind: 'branch', branchIndex: bi, y, h: ROW_H });
        y += ROW_H;
        if (b.cases.length) {
            rows.push({ kind: 'var', branchIndex: bi, y, h: VAR_H });
            y += VAR_H;
            b.cases.forEach((c, ci) => {
                rows.push({ kind: 'case', branchIndex: bi, caseIndex: ci, y, h: CASE_H });
                y += CASE_H;
            });
        }
    });
    return { rows, height: Math.max(y, ROW_H) };
}

function stepHeight(step) {
    if (step._h === undefined) step._h = blockRows(step).height + PAD_TOP + PAD;
    return step._h;
}

/// Where an arrow attaches. `side` picks the left or right edge of the row —
/// which one is chosen is up to routeEdge below, not fixed.
function anchor(ref, side) {
    if (ref.terminalId) {
        const p = positions[ref.terminalId];
        if (!p) return null;
        return { x: p.x + (side === 'right' ? TERM_R * 2 : 0), y: p.y + TERM_R, side };
    }
    const p = positions[ref.stepId];
    const step = steps[ref.stepId];
    if (!p || !step) return null;
    const { rows } = blockRows(step);
    const row = ref.caseIndex !== null && ref.caseIndex !== undefined
        ? rows.find(r => r.kind === 'case' && r.branchIndex === ref.branchIndex && r.caseIndex === ref.caseIndex)
        : rows.find(r => r.kind === 'branch' && r.branchIndex === ref.branchIndex);
    const top = row ? row.y : 0;
    const h   = row ? row.h : ROW_H;
    return { x: p.x + (side === 'right' ? NW + PAD * 2 : 0), y: p.y + PAD_TOP + top + h / 2, side };
}

/// Pick the shortest sensible route instead of always leaving right and
/// entering left. Going out of the side that faces away from the target means
/// the line has to cross its own block first, so that costs extra; among what
/// is left, the shortest pair of edges wins.
const BACKTRACK_COST = 260;

function routeEdge(from, to) {
    let best = null;
    for (const outSide of ['right', 'left']) {
        for (const inSide of ['left', 'right']) {
            const a = anchor(from, outSide), b = anchor(to, inSide);
            if (!a || !b) continue;
            let cost = Math.hypot(b.x - a.x, b.y - a.y);
            if (outSide === 'right' && b.x < a.x) cost += BACKTRACK_COST;
            if (outSide === 'left'  && b.x > a.x) cost += BACKTRACK_COST;
            if (inSide  === 'left'  && a.x > b.x) cost += BACKTRACK_COST;
            if (inSide  === 'right' && a.x < b.x) cost += BACKTRACK_COST;
            if (!best || cost < best.cost) best = { a, b, cost };
        }
    }
    return best;
}

/// Bezier whose handles point out of the chosen sides, so the line leaves and
/// arrives perpendicular to the block edge however the two are placed.
function edgePath(a, b) {
    const span = Math.hypot(b.x - a.x, b.y - a.y);
    const d    = Math.min(160, Math.max(28, span * 0.35));
    const dOut = a.side === 'right' ?  d : -d;
    const dIn  = b.side === 'left'  ? -d :  d;
    return 'M' + a.x + ',' + a.y +
           ' C' + (a.x + dOut) + ',' + a.y + ' ' + (b.x + dIn) + ',' + b.y + ' ' + b.x + ',' + b.y;
}

function layoutDag() {
    const adj = {};
    stepList.forEach(s => adj[s.id] = []);
    edges.forEach(e => {
        if (e.from.terminalId || !adj[e.from.stepId]) return;
        adj[e.from.stepId].push(e.to.stepId);
    });

    const order = [], visited = new Set();
    const visit = id => {
        if (visited.has(id) || !steps[id]) return;
        visited.add(id); order.push(id);
        (adj[id] || []).forEach(visit);
    };
    if (startId && steps[startId]) visit(startId);
    stepList.forEach(s => visit(s.id));

    const incoming = {};
    edges.forEach(e => {
        if (e.from.terminalId) return;
        (incoming[e.to.stepId] ||= []).push(e.from.stepId);
    });

    const ranks = {}, lanes = {};
    order.forEach(id => {
        let r = 0;
        (incoming[id] || []).forEach(from => {
            if (ranks[from] !== undefined) r = Math.max(r, ranks[from] + 1);
        });
        ranks[id] = r;
        (lanes[r] ||= []).push(id);
    });

    // Blocks differ in height, so a fixed row pitch would overlap them.
    const pos = {};
    const vertical = layoutMode !== 'horizontal';
    let cursor = 24;
    Object.keys(lanes).map(Number).sort((a, b) => a - b).forEach(rank => {
        const ids = lanes[rank];
        const tallest = Math.max(...ids.map(id => stepHeight(steps[id])));
        let along = 24;
        ids.forEach(id => {
            pos[id] = vertical ? { x: along, y: cursor } : { x: cursor, y: along };
            along += vertical ? NW + PAD * 2 + GX : stepHeight(steps[id]) + GY;
        });
        cursor += (vertical ? tallest : NW + PAD * 2) + GY;
    });
    placeTerminals(pos);
    return pos;
}

/// The template already carries the editor's own layout in x/y — on steps and
/// on the Start / GoodEnd / BadEnd nodes alike. Returns null when any of it is
/// missing, so the caller can fall back to a computed layout.
function layoutCanvas() {
    if (!stepList.length || stepList.some(s => s.x === null || s.y === null)) return null;
    const xs = stepList.map(s => s.x).concat(terminals.filter(t => t.x !== null).map(t => t.x));
    const ys = stepList.map(s => s.y).concat(terminals.filter(t => t.y !== null).map(t => t.y));
    const minX = Math.min(...xs), minY = Math.min(...ys);
    const pos = {};
    stepList.forEach(s => { pos[s.id] = { x: s.x - minX + 24, y: s.y - minY + 24 }; });
    terminals.forEach(t => {
        if (t.x !== null && t.y !== null) pos[t.id] = { x: t.x - minX + 24, y: t.y - minY + 24 };
    });
    placeTerminals(pos);
    return pos;
}

/// A terminal without coordinates still has to go somewhere: put it just left
/// of the branch it points at.
function placeTerminals(pos) {
    terminals.forEach(t => {
        if (pos[t.id]) return;
        const ref = parseRef(t.target);
        const p   = ref ? pos[ref.stepId] : null;
        pos[t.id] = p
            ? { x: p.x - TERM_R * 2 - GX, y: p.y }
            : { x: 24, y: 24 };
    });
}

function hasCanvasCoords() { return layoutCanvas() !== null; }

function render() {
    positions = (layoutMode === 'canvas' ? layoutCanvas() : null) || layoutDag();
    routeAll();
    renderNodes();
    renderEdges();
    updateMini();
}

/// Search has to cover what the card shows — the branch captions, since
/// Step@UserText is empty in real templates.

function renderNodes() {
    const canvas = document.getElementById('canvas');
    canvas.innerHTML = '';
    const ft = filterText;

    terminals.forEach(t => {
        const p = positions[t.id]; if (!p) return;
        const div = document.createElement('div');
        // Маршрут без точки входа должен быть заметен, а не получен молча.
        div.className = 'term term-' + t.kind + ((t.target || '').trim() ? '' : ' term-orphan');
        div.style.left = p.x + 'px';
        div.style.top  = p.y + 'px';
        div.style.width = div.style.height = (TERM_R * 2) + 'px';
        div.textContent = t.label;
        canvas.appendChild(div);
    });

    stepList.forEach(s => {
        if (ft && !matchesFilter(s, ft)) return;
        const p = positions[s.id]; if (!p) return;

        const div = document.createElement('div');
        div.className = 'block' +
                        (s.id === startId   ? ' entry' : '') +
                        (s.id === goodEndId ? ' good'  : '') +
                        (s.id === badEndId  ? ' bad'   : '') +
                        (s.reachable === false ? ' unreachable' : '') +
                        (selected && selected.stepId === s.id && selected.branchIndex !== null ? ' selected' : '') +
                        (selected && selected.stepId === s.id && selected.branchIndex === null ? ' block-selected' : '');
        div.style.left  = p.x + 'px';
        div.style.top   = p.y + 'px';
        div.style.width = (NW + PAD * 2) + 'px';

        let html = '';
        s.branches.forEach((b, bi) => {
            const on = selected && selected.stepId === s.id && selected.branchIndex === bi;
            html += '<div class="row t-' + typeClass(b.type) + (b.disabled ? ' off' : '') +
                        (b.optional ? ' opt' : '') + (on ? ' on' : '') +
                        '" style="height:' + ROW_H + 'px" data-b="' + bi + '">' +
                      (() => { const p = portHtml(s.id, bi, undefined, ''); return p.left; })() +
                      '<span class="mark ' + typeClass(b.type) + '">' + branchMark(b) + '</span>' +
                      '<span class="rlabel">' + escHtml(branchRowLabel(b)) + '</span>' +
                      (() => { const p = portHtml(s.id, bi, undefined, ''); return p.right; })() +
                      '<span class="drag-port ok"  data-slot="ok"></span>' +
                      '<span class="drag-port err" data-slot="err"></span>' +
                    '</div>';
            if (b.cases.length) {
                html += '<div class="vrow" style="height:' + VAR_H + 'px" data-b="' + bi + '">' +
                            escHtml(b.switchVar || b.action || 'switch') + '</div>';
                b.cases.forEach((c, ci) => {
                    const cp = portHtml(s.id, bi, ci, 'case');
                    html += '<div class="crow" style="height:' + CASE_H + 'px" data-b="' + bi +
                              '" data-c="' + ci + '">' +
                              cp.left +
                              '<span class="clabel">' +
                                (c.isDefault ? 'Default' : escHtml(c.number + ': ' + (c.key || 'undefined'))) +
                              '</span>' +
                              cp.right +
                              '<span class="drag-port case" data-slot="' +
                                (c.isDefault ? 'default' : 'case:' + c.number) + '"></span>' +
                            '</div>';
                });
            }
        });
        div.innerHTML = html;
        div.dataset.step = s.id;
        // Два уровня выделения разведены местом на экране: подложка — блок,
        // строка — ветка. Case-строки принадлежат ветке над ними.
        div.addEventListener('mousedown', e => {
            if (!editing) return;
            if (e.target.closest('.row, .vrow, .crow')) return;   // строка — не блок
            e.preventDefault(); e.stopPropagation();
            beginStepDrag(s, div, e);
        });
        // Слушатели строк вешаются после innerHTML: раньше этих элементов нет.
        div.querySelectorAll('.row').forEach(rowEl => {
            const bi = +rowEl.dataset.b;
            rowEl.addEventListener('mousedown', e => {
                if (!editing) return;
                if (e.target.closest('.drag-port')) return;
                e.preventDefault(); e.stopPropagation();
                beginBranchDrag(s, bi, e);
            });
        });
        // Порты есть и у case-строк; номер ветки-владельца берётся из data-b.
        div.querySelectorAll('.drag-port').forEach(port => {
            port.addEventListener('mousedown', e => {
                if (!editing) return;
                e.preventDefault(); e.stopPropagation();
                const row = port.closest('[data-b]');
                beginEdgeDrag(s.id, +row.dataset.b, port.dataset.slot, e);
            });
        });
        div.addEventListener('click', e => {
            e.stopPropagation();
            const row = e.target.closest('.row, .vrow, .crow');
            if (!row) { selectStep(s.id); return; }
            let bi = row.dataset.b !== undefined ? +row.dataset.b : null;
            if (bi === null) {
                const rows = [...div.children];
                for (let i = rows.indexOf(row); i >= 0; i--)
                    if (rows[i].dataset.b !== undefined) { bi = +rows[i].dataset.b; break; }
            }
            selectBranch(s.id, bi === null ? 0 : bi);
        });
        canvas.appendChild(div);
    });
    applyTransform();
}

/// Route every edge once, before the blocks are drawn, so a row's port can sit
/// on the side the arrow actually leaves from.
let portSides = {};

function routeAll() {
    portSides = {};
    edges.forEach(e => {
        e.route = routeEdge(e.from, e.to);
        if (!e.route || e.from.terminalId) return;
        const key = e.from.stepId + '|' + e.from.branchIndex +
                    (e.from.caseIndex !== null && e.from.caseIndex !== undefined ? '|c' + e.from.caseIndex : '');
        (portSides[key] ||= new Set()).add(e.route.a.side);
    });
}

function portHtml(stepId, branchIndex, caseIndex, cls) {
    const key = stepId + '|' + branchIndex + (caseIndex !== undefined ? '|c' + caseIndex : '');
    const sides = portSides[key];
    if (!sides || !sides.size) return { left: '', right: '' };
    return {
        left:  sides.has('left')  ? '<span class="port ' + cls + ' pl"></span>' : '',
        right: sides.has('right') ? '<span class="port ' + cls + ' pr"></span>' : ''
    };
}

function renderEdges() {
    const svg = document.getElementById('edges');
    let mx = 200, my = 200;
    Object.entries(positions).forEach(([id, p]) => {
        const w = steps[id] ? NW + PAD * 2 : TERM_R * 2;
        const h = steps[id] ? stepHeight(steps[id]) : TERM_R * 2;
        mx = Math.max(mx, p.x + w + 80);
        my = Math.max(my, p.y + h + 80);
    });
    svg.setAttribute('width', mx); svg.setAttribute('height', my);
    svg.innerHTML =
        '<defs>' +
        '<marker id="a-ok"   markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7 Z"/></marker>' +
        '<marker id="a-err"  markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7 Z"/></marker>' +
        '<marker id="a-case" markerWidth="7" markerHeight="7" refX="6" refY="3.5" orient="auto"><path d="M0,0 L7,3.5 L0,7 Z"/></marker>' +
        '</defs>';

    const ns = 'http://www.w3.org/2000/svg';
    const ft = filterText;
    edges.forEach(e => {
        if (ft) {
            const fs = e.from.terminalId ? null : steps[e.from.stepId];
            const ts = steps[e.to.stepId];
            if (!ts) return;
            if (!(fs && matchesFilter(fs, ft)) && !matchesFilter(ts, ft)) return;
        }
        if (!e.route) return;
        const { a, b } = e.route;
        const pathD = edgePath(a, b);

        const path = document.createElementNS(ns, 'path');
        path.setAttribute('class', 'edge edge-' + e.kind + (e.to.dangling ? ' dangling' : ''));
        path.setAttribute('d', pathD);
        path.setAttribute('marker-end', 'url(#a-' + e.kind + ')');
        svg.appendChild(path);

        if (e.label && e.label !== 'ok' && e.label !== 'err') {
            const txt = document.createElementNS(ns, 'text');
            txt.setAttribute('class', 'elabel elabel-' + e.kind);
            txt.setAttribute('x', (a.x + b.x) / 2);
            txt.setAttribute('y', (a.y + b.y) / 2 - 4);
            txt.setAttribute('text-anchor', 'middle');
            txt.textContent = e.label.length > 16 ? e.label.substring(0, 16) + '…' : e.label;
            svg.appendChild(txt);
        }
    });
}

function applyTransform() {
    const t = 'translate(' + panX + 'px,' + panY + 'px) scale(' + scale + ')';
    document.getElementById('canvas').style.transform = t;
    document.getElementById('edges').style.transform  = t;
}

// ── View controls ────────────────────────────────────────────────────────────

function updateMini() {
    document.getElementById('mini').textContent =
        stepList.length + ' steps · ' + edges.length + ' edges · ×' + scale.toFixed(2);
}

function zoomIn()    { scale = Math.min(scale * 1.2, 4);   applyTransform(); updateMini(); }
function zoomOut()   { scale = Math.max(scale / 1.2, 0.1); applyTransform(); updateMini(); }
/// Fit everything into the viewport. A fixed zoom does not work with the
/// canvas layout: ZennoPoster spreads steps over thousands of units, so most of
/// the graph used to sit off-screen after "reset".
function resetView() {
    const ps = Object.values(positions);
    if (!ps.length) { scale = 0.72; panX = 24; panY = 24; applyTransform(); updateMini(); return; }

    const box = Object.entries(positions).map(([id, p]) => ({
        x: p.x, y: p.y,
        w: steps[id] ? NW + PAD * 2 : TERM_R * 2,
        h: steps[id] ? stepHeight(steps[id]) : TERM_R * 2
    }));
    const minX = Math.min(...box.map(b => b.x)), maxX = Math.max(...box.map(b => b.x + b.w));
    const minY = Math.min(...box.map(b => b.y)), maxY = Math.max(...box.map(b => b.y + b.h));

    const wrap = document.getElementById('canvas-wrap');
    const pad  = 40;
    const vw   = wrap.clientWidth  - pad * 2;
    const vh   = wrap.clientHeight - pad * 2;
    if (vw <= 0 || vh <= 0) { applyTransform(); updateMini(); return; }

    scale = Math.max(0.1, Math.min(1.2, Math.min(vw / (maxX - minX || 1), vh / (maxY - minY || 1))));
    panX  = pad - minX * scale;
    panY  = pad - minY * scale;
    applyTransform(); updateMini();
}

function toggleLayout() {
    const order = hasCanvasCoords() ? ['canvas', 'vertical', 'horizontal'] : ['vertical', 'horizontal'];
    layoutMode = order[(order.indexOf(layoutMode) + 1) % order.length];
    document.getElementById('btn-layout').textContent = LAYOUTS[layoutMode];
    render();
}

function filterNodes(val) {
    filterText = val.toLowerCase().trim();
    renderNodes(); renderEdges();
}
