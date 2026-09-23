/* xmlview.js — просмотрщик XML: дерево с виртуальной прокруткой.
   Техника прокрутки та же, что в json.html: распорка на всю высоту,
   рисуются только видимые строки, блок сдвигается transform'ом.        */

'use strict';

var ROW_H = 19, OVERSCAN = 12;   // фиксировано, как в json.html — не завязано на тему

var xmlDoc = null;          // разобранный документ
var allRows = [];           // плоский список всех узлов
var visibleRows = [];       // то, что реально рисуем
var collapsed = {};         // path -> true
var filter = '';

/// Кодировку берём из BOM: шаблоны ZennoPoster лежат в UTF-16, и чтение
/// как UTF-8 даёт нуль между каждой буквой.
function decodeXmlBuffer(buffer) {
    var b = new Uint8Array(buffer), skip = 0, enc = 'utf-8';
    if (b.length >= 2 && b[0] === 0xFF && b[1] === 0xFE)      { enc = 'utf-16le'; skip = 2; }
    else if (b.length >= 2 && b[0] === 0xFE && b[1] === 0xFF) { enc = 'utf-16be'; skip = 2; }
    else if (b.length >= 3 && b[0] === 0xEF && b[1] === 0xBB && b[2] === 0xBF) { skip = 3; }
    try { return new TextDecoder(enc).decode(b.subarray(skip)); }
    catch (e) { return new TextDecoder('utf-8').decode(b.subarray(skip)); }
}

// ── Разбор ─────────────────────────────────────────────────────────────────

function parseXml() {
    var raw = document.getElementById('xmlInput').value;
    var err = document.getElementById('errorBlock');
    err.classList.remove('show');

    if (!raw.trim()) { xmlDoc = null; allRows = []; rebuild(); setStats(null); return; }

    var doc = new DOMParser().parseFromString(raw, 'application/xml');
    var bad = doc.querySelector('parsererror');
    if (bad) {
        xmlDoc = null; allRows = []; rebuild(); setStats(null);
        err.textContent = (bad.textContent || 'XML parse error').replace(/\s+/g, ' ').trim().slice(0, 400);
        err.classList.add('show');
        setBadge('input', 'parse error', 'err');
        return;
    }
    xmlDoc = doc;
    autoCollapse(3);          // расставить точки сворачивания ДО первой сборки дерева
    allRows = flatten(doc);
    rebuild(true);
    setStats(allRows);
}

/// Плоский список узлов. Закрывающие строки нужны, чтобы у длинных блоков
/// был видимый конец — как у скобок в json.
function flatten(doc) {
    var rows = [], stack = [];
    for (var i = doc.childNodes.length - 1; i >= 0; i--)
        stack.push({ node: doc.childNodes[i], depth: 0, path: String(i) });

    while (stack.length) {
        var it = stack.pop();
        if (it.close) { rows.push({ close: true, depth: it.depth, name: it.name, path: it.path }); continue; }

        var n = it.node, kind = nodeKind(n);
        if (kind === 'skip') continue;

        var kids = kind === 'element' ? significantChildren(n) : [];
        var row = {
            path: it.path, depth: it.depth, kind: kind,
            name: n.nodeName,
            attrs: kind === 'element' ? attrList(n) : [],
            value: kind === 'element' ? inlineText(n, kids) : String(n.nodeValue || '').trim(),
            kids: kids.length
        };
        rows.push(row);

        if (kids.length && !collapsed[it.path]) {
            rows.push({ open: true });                       // отметка: дети следуют
            stack.push({ close: true, depth: it.depth, name: n.nodeName, path: it.path });
            for (var k = kids.length - 1; k >= 0; k--)
                stack.push({ node: kids[k], depth: it.depth + 1, path: it.path + '.' + k });
        }
    }
    return rows.filter(function (r) { return !r.open; });
}

function nodeKind(n) {
    switch (n.nodeType) {
        case 1: return 'element';
        case 3: return String(n.nodeValue || '').trim() ? 'text' : 'skip';
        case 4: return 'cdata';
        case 7: return 'pi';
        case 8: return 'comment';
        default: return 'skip';
    }
}

function significantChildren(el) {
    var out = [];
    for (var i = 0; i < el.childNodes.length; i++)
        if (nodeKind(el.childNodes[i]) !== 'skip') out.push(el.childNodes[i]);
    // единственный текстовый ребёнок показываем в самой строке элемента
    if (out.length === 1 && out[0].nodeType === 3) return [];
    return out;
}

function inlineText(el, kids) {
    if (kids.length) return '';
    var t = String(el.textContent || '').trim();
    return t;
}

function attrList(el) {
    var out = [];
    for (var i = 0; i < el.attributes.length; i++)
        out.push([el.attributes[i].name, el.attributes[i].value]);
    return out;
}

// ── Отрисовка ──────────────────────────────────────────────────────────────

function rebuild(resetScroll) {
    var f = filter.toLowerCase();
    visibleRows = !f ? allRows : allRows.filter(function (r) {
        if (r.close) return false;
        if ((r.name || '').toLowerCase().indexOf(f) >= 0) return true;
        if ((r.value || '').toLowerCase().indexOf(f) >= 0) return true;
        for (var i = 0; i < (r.attrs || []).length; i++)
            if ((r.attrs[i][0] + ' ' + r.attrs[i][1]).toLowerCase().indexOf(f) >= 0) return true;
        return false;
    });
    var wrap = document.getElementById('vscrollWrap');
    if (resetScroll && wrap) wrap.scrollTop = 0;
    render();
    setBadge('output', visibleRows.length + (f ? ' matched' : ' rows'), visibleRows.length ? 'ok' : '');
}

var rafPending = false;
function schedule() {
    if (rafPending) return;
    rafPending = true;
    requestAnimationFrame(function () { rafPending = false; render(); });
}

function render() {
    var wrap = document.getElementById('vscrollWrap');
    var spacer = document.getElementById('vscrollSpacer');
    var host = document.getElementById('vscrollRows');
    if (!wrap) return;
    var total = visibleRows.length;
    spacer.style.height = (total * ROW_H) + 'px';
    if (!total) {
        host.style.transform = '';
        host.innerHTML = '<div class="empty-msg">' + (xmlDoc ? 'No match.' : 'Nothing parsed yet.') + '</div>';
        return;
    }

    var start = Math.max(0, Math.floor(wrap.scrollTop / ROW_H) - OVERSCAN);
    var end   = Math.min(total, Math.ceil((wrap.scrollTop + wrap.clientHeight) / ROW_H) + OVERSCAN);
    var frag  = document.createDocumentFragment();
    for (var i = start; i < end; i++) frag.appendChild(rowEl(visibleRows[i]));
    host.innerHTML = '';
    host.appendChild(frag);
    host.style.transform = 'translateY(' + (start * ROW_H) + 'px)';
}

function rowEl(r) {
    var d = document.createElement('div');
    d.className = 'xv-row';
    d.style.paddingLeft = (4 + r.depth * 14) + 'px';

    if (r.close) {
        add(d, 'xv-punc', '</' + r.name + '>');
        return d;
    }
    if (r.kind === 'comment') { add(d, 'xv-comment', '<!-- ' + trunc(r.value, 200) + ' -->'); return d; }
    if (r.kind === 'pi')      { add(d, 'xv-pi', '<?' + r.name + ' ' + trunc(r.value, 160) + '?>'); return d; }
    if (r.kind === 'cdata')   { add(d, 'xv-cdata', '<![CDATA[ ' + trunc(r.value, 200) + ' ]]>'); return d; }
    if (r.kind === 'text')    { add(d, 'xv-text', trunc(r.value, 400)); return d; }

    if (r.kids) {
        var tw = add(d, 'xv-toggle', collapsed[r.path] ? '▸' : '▾');
        tw.onclick = function () { toggle(r.path); };
    } else add(d, 'xv-toggle xv-toggle-empty', '');

    add(d, 'xv-punc', '<');
    add(d, 'xv-tag', r.name);
    for (var i = 0; i < r.attrs.length; i++) {
        add(d, 'xv-attr', ' ' + r.attrs[i][0]);
        add(d, 'xv-punc', '=');
        add(d, 'xv-val', '"' + trunc(r.attrs[i][1], 120) + '"');
    }
    if (r.kids) {
        add(d, 'xv-punc', '>');
        if (collapsed[r.path]) add(d, 'xv-count', ' ' + r.kids + (r.kids === 1 ? ' node' : ' nodes'));
    } else if (r.value) {
        add(d, 'xv-punc', '>');
        add(d, 'xv-body', trunc(r.value, 300));
        add(d, 'xv-punc', '</' + r.name + '>');
    } else add(d, 'xv-punc', ' />');
    return d;
}

function add(parent, cls, text) {
    var s = document.createElement('span');
    s.className = cls;
    s.textContent = text;
    parent.appendChild(s);
    return s;
}

function trunc(s, n) { s = String(s == null ? '' : s); return s.length > n ? s.slice(0, n) + '…' : s; }

// ── Действия ───────────────────────────────────────────────────────────────

function refreshTree() {
    if (!xmlDoc) return;
    allRows = flatten(xmlDoc);
    rebuild();
}

function toggle(path) {
    if (collapsed[path]) delete collapsed[path]; else collapsed[path] = true;
    refreshTree();
}

function expandAll() {
    collapsed = {};
    setDepthActive(null);
    refreshTree();
}

function collapseAll() {
    if (!xmlDoc) return;
    collapsed = {};
    markCollapsed(xmlDoc, '', 0, 0);
    setDepthActive(null);
    refreshTree();
}

/// Раскрыть до уровня N: глубже — свернуть.
function expandToDepth(n) {
    if (!xmlDoc) return;
    collapsed = {};
    markCollapsed(xmlDoc, '', 0, n);
    setDepthActive(n);
    refreshTree();
}

function autoCollapse(n) { if (xmlDoc) { collapsed = {}; markCollapsed(xmlDoc, '', 0, n); setDepthActive(n); } }

function markCollapsed(doc, _base, _depth, limit) {
    // путь верхнего уровня — индекс в doc.childNodes, как в flatten()
    for (var c = 0; c < doc.childNodes.length; c++) markSub(doc.childNodes[c], String(c), 0, limit);
}

function markSub(n, path, depth, limit) {
    if (n.nodeType !== 1) return;
    var kids = significantChildren(n);
    if (kids.length && depth >= limit) collapsed[path] = true;
    for (var k = 0; k < kids.length; k++) markSub(kids[k], path + '.' + k, depth + 1, limit);
}

function setDepthActive(n) {
    document.querySelectorAll('.depth-btn').forEach(function (b) {
        b.classList.toggle('active', n !== null && +b.dataset.depth === n);
    });
}

var searchTimer = null;
function onSearch() {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(function () {
        filter = document.getElementById('search').value.trim();
        // при поиске раскрываем всё, иначе совпадения внутри свёрнутых не видны
        if (filter && xmlDoc) { collapsed = {}; allRows = flatten(xmlDoc); }
        rebuild(true);
    }, 180);
}

function formatInput() {
    if (!xmlDoc) { parseXml(); if (!xmlDoc) return; }
    document.getElementById('xmlInput').value = prettyPrint(xmlDoc);
    setBadge('input', 'formatted', 'ok');
}

/// Отступы по уровням. Узлы с одним текстом остаются в одну строку.
function prettyPrint(doc) {
    var out = [];
    if (/^\s*<\?xml/.test(document.getElementById('xmlInput').value)) {
        var m = document.getElementById('xmlInput').value.match(/^\s*(<\?xml[^>]*\?>)/);
        if (m) out.push(m[1]);
    }
    function walk(n, ind) {
        var pad = new Array(ind + 1).join('  ');
        var k = nodeKind(n);
        if (k === 'skip') return;
        if (k === 'text')    { out.push(pad + esc(String(n.nodeValue).trim())); return; }
        if (k === 'comment') { out.push(pad + '<!--' + n.nodeValue + '-->'); return; }
        if (k === 'cdata')   { out.push(pad + '<![CDATA[' + n.nodeValue + ']]>'); return; }
        if (k === 'pi')      { out.push(pad + '<?' + n.nodeName + ' ' + n.nodeValue + '?>'); return; }
        var attrs = attrList(n).map(function (a) { return ' ' + a[0] + '="' + escAttr(a[1]) + '"'; }).join('');
        var kids = significantChildren(n);
        var text = kids.length ? '' : String(n.textContent || '').trim();
        if (!kids.length && !text) { out.push(pad + '<' + n.nodeName + attrs + ' />'); return; }
        if (!kids.length)          { out.push(pad + '<' + n.nodeName + attrs + '>' + esc(text) + '</' + n.nodeName + '>'); return; }
        out.push(pad + '<' + n.nodeName + attrs + '>');
        kids.forEach(function (c) { walk(c, ind + 1); });
        out.push(pad + '</' + n.nodeName + '>');
    }
    for (var i = 0; i < doc.childNodes.length; i++) walk(doc.childNodes[i], 0);
    return out.join('\n');
}

function esc(s)     { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }
function escAttr(s) { return esc(s).replace(/"/g, '&quot;'); }

function copyAll() {
    var t = document.getElementById('xmlInput').value;
    if (!t) return;
    navigator.clipboard.writeText(t).then(function () { setBadge('input', 'copied', 'ok'); });
}

function clearAll() {
    document.getElementById('xmlInput').value = '';
    document.getElementById('search').value = '';
    filter = ''; collapsed = {};
    parseXml();
    setBadge('input', 'paste XML', '');
}

// ── Файл ───────────────────────────────────────────────────────────────────

function acceptText(text, name) {
    document.getElementById('xmlInput').value = text;
    collapsed = {}; filter = ''; document.getElementById('search').value = '';
    parseXml();
    if (xmlDoc) setBadge('input', name || 'loaded', 'ok');
}

var lastPath = '';
try { lastPath = localStorage.getItem('xml-view-path') || ''; } catch (e) { /* ignore */ }

// Системный диалог — тот же, что у zpxml: помнит каталог и отдаёт полный путь.
async function openFromDisk() {
    setBadge('input', 'choosing a file...', '');
    var picked;
    try { picked = await (await fetch('/tasker/pick?mode=file&ext=xml&start=' + encodeURIComponent(lastPath))).json(); }
    catch (e) { setBadge('input', 'file dialog failed: ' + e.message, 'err'); return; }
    if (!picked.ok)   { setBadge('input', 'file dialog: ' + (picked.error || 'did not open'), 'err'); return; }
    if (!picked.path) { setBadge('input', xmlDoc ? 'loaded' : 'paste XML', ''); return; }   // отмена — не ошибка
    try {
        var res = await fetch('/dbg/file?path=' + encodeURIComponent(picked.path));
        if (!res.ok) { setBadge('input', 'cannot read: ' + (await res.text()).slice(0, 120), 'err'); return; }
        var buf = await res.arrayBuffer();
        lastPath = picked.path;
        try { localStorage.setItem('xml-view-path', lastPath); } catch (e) { /* ignore */ }
        acceptText(decodeXmlBuffer(buf), picked.path.split(/[\/]/).pop());
    } catch (e) { setBadge('input', 'cannot read: ' + e.message, 'err'); }
}

function initDrop() {
    var zone = document.getElementById('leftPanel');
    if (!zone) return;
    ['dragenter', 'dragover'].forEach(function (ev) {
        zone.addEventListener(ev, function (e) { e.preventDefault(); zone.classList.add('drop-ok'); });
    });
    ['dragleave', 'drop'].forEach(function (ev) {
        zone.addEventListener(ev, function (e) { e.preventDefault(); zone.classList.remove('drop-ok'); });
    });
    zone.addEventListener('drop', function (e) {
        var f = e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files[0];
        if (!f) return;
        var fr = new FileReader();
        fr.onload  = function () { acceptText(decodeXmlBuffer(fr.result), f.name); };
        fr.onerror = function () { setBadge('input', 'cannot read ' + f.name, 'err'); };
        fr.readAsArrayBuffer(f);
    });
}

// ── Счётчики и значки ──────────────────────────────────────────────────────

function setStats(rows) {
    var el = document.getElementById('stats');
    if (!rows || !rows.length) { el.innerHTML = ''; return; }
    var elements = 0, attrs = 0, depth = 0;
    (function walk(n, d) {
        for (var i = 0; i < n.childNodes.length; i++) {
            var c = n.childNodes[i];
            if (c.nodeType !== 1) continue;
            elements++; attrs += c.attributes.length; if (d > depth) depth = d;
            walk(c, d + 1);
        }
    })(xmlDoc, 1);
    el.innerHTML = '';
    [['elements', elements], ['attributes', attrs], ['depth', depth]].forEach(function (p) {
        var s = document.createElement('span'); s.className = 'stat-chip';
        s.textContent = p[0] + ' ' + p[1]; el.appendChild(s);
    });
    if (document.getElementById('inputBadge').textContent === 'paste XML') setBadge('input', 'parsed', 'ok');
}

function setBadge(which, text, state) {
    var el = document.getElementById(which + 'Badge');
    if (!el) return;
    el.textContent = text;
    el.className = 'panel-badge' + (state ? ' ' + state : '');
}

// ── Старт ──────────────────────────────────────────────────────────────────

document.addEventListener('DOMContentLoaded', function () {
    var wrap = document.getElementById('vscrollWrap');
    wrap.addEventListener('scroll', schedule);
    window.addEventListener('resize', schedule);
    var input = document.getElementById('xmlInput');
    var t = null;
    input.addEventListener('input', function () { clearTimeout(t); t = setTimeout(parseXml, 350); });
    input.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') { e.preventDefault(); parseXml(); }
    });
    initDrop();
    render();   // показать «Nothing parsed yet» сразу, а не пустой блок
});
