/* zpxml-model.js — чтение XMLDocument шаблона в модель для отрисовки.
   Знает формат ZennoPoster. Ничего не знает про экран и про правки. */

let steps = {}, edges = [], stepList = [];
let startId = '', goodEndId = '', badEndId = '';
let terminals = [];

// ── Parse ───────────────────────────────────────────────────────────────────

function buildFromDoc(doc) {
    // The runtime refuses anything but <Project> (XmlTemplate.Parse) — say the
    // same thing here instead of the useless "no steps found" further down.
    const root = doc.documentElement;
    if (root && root.tagName !== 'Project' && !root.querySelector('Step'))
        throw new Error('root element is <' + root.tagName + '>, expected <Project> — not a ZennoPoster template');

    steps = {};
    doc.querySelectorAll('Step').forEach(s => {
        const id = s.getAttribute('ID');
        if (!id) return;
        const label = s.getAttribute('UserText') || '';
        const x = parseFloat(s.getAttribute('x'));
        const y = parseFloat(s.getAttribute('y'));
        const branches = [];
        s.querySelectorAll('Branch').forEach(b => {
            const res = b.querySelector('Results');
            const onSuccess = res?.querySelector('OnSuccess')?.textContent || '';
            const onError   = res?.querySelector('OnError')?.textContent   || '';
            // Switch options. The number in <CaseN> is the option's position in
            // the editor and decides which of two equal keys fires first, so the
            // order in the file is not the order that matters. <Default> is a
            // separate node, not "case 999".
            const cases = [];
            if (res) {
                const numbered = [];
                Array.from(res.children).forEach(c => {
                    const m = /^Case(\d+)$/.exec(c.tagName);
                    if (m) { numbered.push([+m[1], c]); return; }
                    // The editor lists Default above the numbered options.
                    if (c.tagName === 'Default') numbered.push([-1, c]);
                });
                numbered.sort((a, b) => a[0] - b[0]);
                numbered.forEach(([num, c]) => {
                    // An option whose Value is empty has no arrow on the canvas,
                    // but it is still a row in the editor — keep it, drop only
                    // the port and the edge.
                    const pair = parsePair(c) || { key: '', val: '' };
                    cases.push({
                        number:    num,
                        key:       pair.key,
                        val:       pair.val,
                        isDefault: num === -1
                    });
                });
            }
            branches.push({
                id:       b.getAttribute('ID')       || '',
                type:     b.getAttribute('Type')     || '',
                action:   b.getAttribute('Action')   || '',
                userText: b.getAttribute('UserText') || '',
                // Absent attribute means "enabled" — the runtime reads it the same way.
                disabled: /^true$/i.test(b.getAttribute('IsDisable') || ''),
                optional: /^true$/i.test(b.getAttribute('IsNotNecessarily') || ''),
                code:     b.querySelector('Code')?.textContent || '',
                // ZennoPoster stores the tested value under different parameter
                // names depending on the action, so look for the macro instead
                // of betting on one field. Empty when nothing looks like one.
                switchVar: findMacro(b.querySelector('Parameters')),
                params:    flattenParams(b.querySelector('Parameters')),
                outputVariable: res?.querySelector('OutputVariable')?.textContent || '',
                onSuccess, onError, cases
            });
        });
        steps[id] = {
            id, label, branches,
            x: isFinite(x) ? x : null,
            y: isFinite(y) ? y : null
        };
    });

    stepList = Object.values(steps);
    if (!stepList.length) throw new Error('No Step elements found');

    // Start / GoodEnd / BadEnd are canvas nodes of their own — round ones — and
    // they carry x/y just like a step does.
    terminals = [];
    const term = (tag, kind, label) => {
        const el = doc.querySelector(tag);
        if (!el) return '';
        const raw = el.getAttribute('nextAction') || '';
        const x = parseFloat(el.getAttribute('x'));
        const y = parseFloat(el.getAttribute('y'));
        terminals.push({
            id: '__' + kind, kind, label, target: raw,
            x: isFinite(x) ? x : null,
            y: isFinite(y) ? y : null
        });
        return raw.split('|')[0] || '';
    };
    startId   = term('Start',   'start', 'Start');
    goodEndId = term('GoodEnd', 'good',  'Good End');
    badEndId  = term('BadEnd',  'bad',   'Bad End');

    buildEdges();
    markReachable();
}

/// Steps you cannot get to from Start, GoodEnd or BadEnd. Mirrors
/// XmlTemplate.UnreachableSteps(): counting only from Start is wrong, because
/// the end handlers start chains of their own.
function markReachable() {
    const seen = new Set();
    const queue = [startId, goodEndId, badEndId].filter(id => id && steps[id]);
    while (queue.length) {
        const id = queue.shift();
        if (seen.has(id) || !steps[id]) continue;
        seen.add(id);
        steps[id].branches.forEach(b => {
            [b.onSuccess, b.onError, ...b.cases.map(c => c.val)].forEach(raw => {
                const t = (raw || '').trim().split('|')[0];
                if (t && steps[t] && !seen.has(t)) queue.push(t);
            });
        });
    }
    stepList.forEach(s => s.reachable = seen.has(s.id));
}

/// Имя блока. В реальных шаблонах Step@UserText пуст, и своего имени у блока
/// нет. Подставлять сюда подпись одной из его веток нельзя: тогда блок из
/// тринадцати действий назывался бы по восьмому из них, и это читалось бы как
/// факт. Без имени показываем короткий идентификатор.
function stepLabel(s) {
    return s.label.trim() || ('step ' + s.id.substring(0, 8));
}

/// <Pair><Key>…</Key><Value>stepId|branchId</Value></Pair>, stored escaped
/// inside the node's text. An empty Value is legal: it is an option with no
/// arrow drawn on the canvas.
/// First {-Something-} macro inside the node, used as the switch caption.
/// Плоский список параметров ветки: путь и значение, как они лежат в файле.
/// Разбирать по типам действия не берёмся — у каждого Type/Action свой набор
/// полей, и выдуманная схема врала бы на первом незнакомом действии. Поэтому
/// показываем всё, что есть, включая атрибуты: у HTMLElement условие поиска
/// элемента хранится именно в них.
function flattenParams(el, prefix, out, depth) {
    out = out || [];
    prefix = prefix || '';
    depth = depth || 0;
    if (!el || depth > 6) return out;

    Array.from(el.children).forEach(node => {
        const name = prefix + node.tagName;

        Array.from(node.attributes || []).forEach(a => {
            if ((a.value || '').trim()) out.push({ path: name + '@' + a.name, value: a.value });
        });

        const kids = Array.from(node.children);
        if (kids.length) { flattenParams(node, name + '.', out, depth + 1); return; }

        // Код ветки показывается отдельным блоком, дублировать не нужно.
        if (node.tagName === 'Code') return;
        const text = (node.textContent || '').trim();
        if (text) out.push({ path: name, value: text });
    });
    return out;
}

function findMacro(params) {
    if (!params) return '';
    const m = (params.textContent || '').match(/\{-[^}]{1,60}-\}/);
    return m ? m[0] : '';
}

function parsePair(node) {
    const txt = node.textContent || '';
    if (!txt.trim()) return null;
    try {
        const inner = new DOMParser().parseFromString(txt, 'text/xml');
        if (!inner.querySelector('parsererror')) {
            const val = inner.querySelector('Value')?.textContent || '';
            const key = inner.querySelector('Key')?.textContent   || '';
            if (val.trim()) return { key, val };
            return null;
        }
    } catch (e) { /* fall through to the regex below */ }
    const m  = txt.match(/<Value>([^<]*)<\/Value>/);
    const mk = txt.match(/<Key>([^<]*)<\/Key>/);
    return m && m[1].trim() ? { key: mk ? mk[1] : '', val: m[1] } : null;
}

// ── Graph ────────────────────────────────────────────────────────────────────

function parseRef(raw) {
    if (!raw || !raw.trim()) return null;
    const p = raw.trim().split('|');
    if (p.length < 2) return null;
    const step = steps[p[0].trim()];
    if (!step) return null;
    const bi = step.branches.findIndex(b => b.id === p[1].trim());
    // A transition to a branch that is not in the file is a broken template,
    // worth showing as an arrow to the block rather than swallowing.
    return { stepId: p[0].trim(), branchIndex: bi < 0 ? 0 : bi, dangling: bi < 0 };
}

function buildEdges() {
    edges = [];
    const add = (from, raw, label, kind) => {
        const to = parseRef(raw);
        if (!to) return;
        edges.push({ from, to, label, kind });
    };

    stepList.forEach(step => {
        step.branches.forEach((b, bi) => {
            const from = { stepId: step.id, branchIndex: bi, caseIndex: null };
            add(from, b.onSuccess, 'ok',  'ok');
            add(from, b.onError,   'err', 'err');
            b.cases.forEach((c, ci) =>
                add({ stepId: step.id, branchIndex: bi, caseIndex: ci }, c.val,
                    c.isDefault ? 'default' : c.key, 'case'));
        });
    });

    terminals.forEach(t => add({ terminalId: t.id }, t.target, '', t.kind === 'bad' ? 'err' : 'ok'));
}

// ── Labels and filtering ─────────────────────────────────────────────────────

function matchesFilter(s, ft) {
    if (s.id.toLowerCase().includes(ft)) return true;
    if (stepLabel(s).toLowerCase().includes(ft)) return true;
    return s.branches.some(b =>
        b.userText.toLowerCase().includes(ft) ||
        b.type.toLowerCase().includes(ft) ||
        b.action.toLowerCase().includes(ft));
}

function typeClass(t) {
    if (t === 'OwnCode')     return 'own';
    if (t === 'Logic')       return 'logic';
    if (t === 'Profile')     return 'profile';
    if (t === 'HTMLElement') return 'html';
    if (t === 'WebBrowser')  return 'web';
    return 'other';
}

/// Captions ZennoPoster shows on the canvas for actions that have no user text.
/// Read off the editor, not off a spec — anything missing falls back to the raw
/// action name, which is still true, just less friendly.
const ACTION_LABEL = {
    RiseEvent:     'click',
    SetAttribute:  'set attribute',
    GetAttribute:  'get attribute',
    CMD_NAVIGATE:  'navigate',
    CSharp:        'C# code',
    Update:        'update profile',
    Alert:         'alert',
    Pause:         'pause'
};

/// Short caption for one branch row. ZennoPoster shows the user's own caption
/// when there is one and a type-specific default otherwise.
function branchRowLabel(b) {
    if (b.userText.trim()) return b.userText.trim();
    return ACTION_LABEL[b.action] || b.action || b.type || 'branch';
}

/// The marker in the left gutter of a row — the icon column on the ZP canvas.
function branchMark(b) {
    if (b.type === 'OwnCode')     return 'C#';
    if (b.type === 'Logic')       return '&#9888;';
    if (b.type === 'Profile')     return '&#9679;';
    if (b.type === 'HTMLElement') return b.action === 'RiseEvent' ? '&#9889;' : '&lt;&gt;';
    if (b.type === 'WebBrowser')  return '&#8594;';
    return '&#9679;';
}
