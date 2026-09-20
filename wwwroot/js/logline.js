/* ═══════════════════════════════════════════════════════
   logline.js — разбор и раскраска одной строки лога.
   Один рендерер на все окна вывода: живой SSE-поток, история
   из БД и поток установщика рисуются одинаково.
   ═══════════════════════════════════════════════════════ */

var LogLine = (function () {
'use strict';

// ── Экранирование ─────────────────────────────────────────────────────────────
// Кавычки не трогаем: строка живёт в текстовом узле, а подсветка чисел не должна
// натыкаться на цифры внутри html-сущностей вида &#39;.
function esc(s) {
    return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

// ── Палитра ───────────────────────────────────────────────────────────────────
// Двенадцать разнесённых тонов: соседние теги и соседние нити не сливаются.
var HUES = [205, 265, 320, 15, 40, 95, 150, 185, 240, 290, 340, 65];

function hashHue(s) {
    var h = 0;
    for (var i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
    return HUES[h % HUES.length];
}

// ── Управляющие последовательности ────────────────────────────────────────────
var ANSI_SGR = /\x1b\[([0-9;]*)m/;
var ANSI_ANY = /\x1b\[[0-9;?]*[ -\/]*[@-~]|\x1b\][^\x07]*\x07|\x1b[@-Z_]/g;

/// Прогресс-строки перерисовываются через \r — показываем последнюю версию.
function lastFrame(s) {
    var i = s.lastIndexOf('\r');
    return i < 0 ? s : (i === s.length - 1 ? s.slice(0, i) : s.slice(i + 1));
}

function stripAnsi(s) {
    return s.replace(ANSI_ANY, '');
}

var XTERM_STEPS = [0, 95, 135, 175, 215, 255];

/// Цвет xterm-256 в rgb: 16 базовых, куб 6×6×6, серая лесенка.
function xterm256(n) {
    if (n < 16) return null;                       // базовые берём из темы
    if (n < 232) {
        n -= 16;
        return 'rgb(' + XTERM_STEPS[(n / 36) | 0] + ','
                      + XTERM_STEPS[((n / 6) | 0) % 6] + ','
                      + XTERM_STEPS[n % 6] + ')';
    }
    var g = 8 + 10 * (n - 232);
    return 'rgb(' + g + ',' + g + ',' + g + ')';
}

/// Разбор SGR-кодов в отрезки со стилем. Фон игнорируем: у окна свой.
function ansiSegments(raw) {
    var segs = [], state = { fg: '', bold: false, dim: false, italic: false, under: false };
    var rest = raw, m;

    function push(text) {
        if (!text) return;
        var style = '';
        if (state.fg)     style += 'color:' + state.fg + ';';
        if (state.bold)   style += 'font-weight:600;';
        if (state.dim)    style += 'opacity:.65;';
        if (state.italic) style += 'font-style:italic;';
        if (state.under)  style += 'text-decoration:underline;';
        segs.push({ text: stripAnsi(text), style: style });
    }

    while ((m = ANSI_SGR.exec(rest))) {
        push(rest.slice(0, m.index));
        applySgr(state, m[1]);
        rest = rest.slice(m.index + m[0].length);
    }
    push(rest);
    return segs;
}

function applySgr(state, body) {
    var codes = (body === '' ? '0' : body).split(';').map(function (c) { return parseInt(c, 10) || 0; });

    for (var i = 0; i < codes.length; i++) {
        var c = codes[i];
        if (c === 0)                    { state.fg = ''; state.bold = state.dim = state.italic = state.under = false; }
        else if (c === 1)                 state.bold   = true;
        else if (c === 2)                 state.dim    = true;
        else if (c === 3)                 state.italic = true;
        else if (c === 4)                 state.under  = true;
        else if (c === 22)                state.bold   = state.dim = false;
        else if (c === 23)                state.italic = false;
        else if (c === 24)                state.under  = false;
        else if (c === 39)                state.fg     = '';
        else if (c >= 30 && c <= 37)      state.fg     = 'var(--ansi-' + (c - 30) + ')';
        else if (c >= 90 && c <= 97)      state.fg     = 'var(--ansi-' + (c - 90 + 8) + ')';
        else if (c === 38) {
            if (codes[i + 1] === 5)      { state.fg = xterm256(codes[i + 2]) || 'var(--ansi-' + (codes[i + 2] % 16) + ')'; i += 2; }
            else if (codes[i + 1] === 2) { state.fg = 'rgb(' + codes[i + 2] + ',' + codes[i + 3] + ',' + codes[i + 4] + ')'; i += 4; }
        }
        else if (c === 48) {              // фон пропускаем, но аргументы съедаем
            if (codes[i + 1] === 5)      i += 2;
            else if (codes[i + 1] === 2) i += 4;
        }
    }
}

// ── Уровни ────────────────────────────────────────────────────────────────────
// Первое слово тега решает уровень строки; всё прочее — просто именованный тег.
var TAG_LEVEL = {
    ERR: 'ERROR', ERROR: 'ERROR', ERRORS: 'ERROR', FAIL: 'ERROR', FAILED: 'ERROR',
    FATAL: 'ERROR', CRITICAL: 'ERROR', EXCEPTION: 'ERROR', PANIC: 'ERROR',
    WARN: 'WARNING', WARNING: 'WARNING',
    OK: 'OK', DONE: 'OK', SUCCESS: 'OK', SUCCEEDED: 'OK', PASS: 'OK', PASSED: 'OK', READY: 'OK',
    DEBUG: 'DEBUG', TRACE: 'DEBUG', VERBOSE: 'DEBUG'
};

var LEAD_TAGS  = /^\s*((?:\[[^\]\n]{1,48}\]\s*)+)/;
var LEAD_LABEL = /^\s*([A-Za-z][\w.\-]{0,30}):(\s)/;

/// Уровень по тегам, а если их нет — по тексту строки, как раньше.
function levelOf(plain, tags) {
    for (var i = 0; i < tags.length; i++) {
        var word = String(tags[i]).replace(/^[\[\s]+|[\]\s]+$/g, '').split(/[\s:,\-]/)[0].toUpperCase();
        if (TAG_LEVEL[word]) return TAG_LEVEL[word];
    }
    if (/\[(ERROR|ERR|FATAL)\]|(^|\s)(Traceback|Unhandled exception)\b/i.test(plain)) return 'ERROR';
    if (/\[(WARNING|WARN)\]/i.test(plain))                                            return 'WARNING';
    if (/\[(DEBUG|TRACE)\]/i.test(plain))                                             return 'DEBUG';
    return 'INFO';
}

/// Тег в свой цвет: известные уровни — семантические, остальные — по хешу имени.
function tagHtml(tag) {
    var inner = tag.replace(/^\[|\]$/g, '');
    var word  = inner.split(/[\s:,\-]/)[0].toUpperCase();
    var lvl   = TAG_LEVEL[word];
    if (lvl) return '<span class="ll-tag ll-' + lvl + '">' + esc(inner) + '</span>';
    return '<span class="ll-tag" style="--ll-h:' + hashHue(word) + '">' + esc(inner) + '</span>';
}

// ── Подсветка внутри строки ───────────────────────────────────────────────────
//   url | строка в кавычках | key=value | число | стрелка | скобка
// Круглых скобок здесь нет намеренно: в обычном тексте они встречаются чаще,
// чем в структурах, и подсветка каждой пары превращает лог в рябь.
var INLINE = /(https?:\/\/[^\s'"<>]+)|('[^'\n]*'|"[^"\n]*")|([A-Za-z_][\w.\-]*=[^\s,;)\]}]+)|(\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?(?:ms|s|m|h|%)?\b)|(=>|->|<-|::)|([{}\[\]])/g;

function highlight(raw) {
    var out = '', last = 0, m;
    INLINE.lastIndex = 0;
    while ((m = INLINE.exec(raw))) {
        out += esc(raw.slice(last, m.index));
        if (m[1])      out += '<span class="ll-url">' + esc(m[1]) + '</span>';
        else if (m[2]) out += '<span class="ll-str">' + esc(m[2]) + '</span>';
        else if (m[3]) {
            var eq = m[3].indexOf('=');
            out += '<span class="ll-key">' + esc(m[3].slice(0, eq)) + '</span>'
                 + '<span class="ll-op">=</span>'
                 + '<span class="ll-val">' + esc(m[3].slice(eq + 1)) + '</span>';
        }
        else if (m[4]) out += '<span class="ll-num">'  + esc(m[4]) + '</span>';
        else if (m[5]) out += '<span class="ll-op">'   + esc(m[5]) + '</span>';
        else           out += '<span class="ll-punc">' + esc(m[6]) + '</span>';
        last = m.index + m[0].length;
    }
    return out + esc(raw.slice(last));
}

/// Ведущие теги отдельно от тела: тег красится сам, тело — по правилам выше.
function bodyHtml(plain, tags, labelLen) {
    var head = tags.map(tagHtml).join('');
    var rest = plain.slice(labelLen);
    return head + highlight(rest);
}

// ── Сборка строки ─────────────────────────────────────────────────────────────

/// Ведущие теги строки и длина съеденного ими куска.
function leadTags(plain) {
    var mt = LEAD_TAGS.exec(plain);
    if (mt) return { list: mt[1].match(/\[[^\]\n]*\]/g) || [], len: mt[0].length };

    var ml = LEAD_LABEL.exec(plain);
    if (ml) return { list: ['[' + ml[1] + ':]'], len: ml[0].length - ml[2].length };

    return { list: [], len: 0 };
}

function contentHtml(frame, plain, tags) {
    // Скрипт сам выбрал цвета — уважаем их и ничего не додумываем поверх.
    if (frame.indexOf('\x1b') >= 0) {
        return ansiSegments(frame).map(function (s) {
            return s.style ? '<span style="' + s.style + '">' + esc(s.text) + '</span>' : esc(s.text);
        }).join('');
    }
    return bodyHtml(plain, tags.list, tags.len);
}

/// d: { line, run, ts, level } — как приходит из SSE-канала output.
function build(d) {
    var raw   = d && d.line != null ? String(d.line) : '';
    var frame = lastFrame(raw);
    var plain = stripAnsi(frame);
    var tags  = leadTags(plain);

    // Уровень с сервера уважаем, только когда он что-то утверждает.
    var fromServer = d && d.level ? String(d.level).toUpperCase() : '';
    var level = (fromServer && fromServer !== 'INFO') ? fromServer : levelOf(plain, tags.list);

    var run   = d && d.run ? String(d.run) : '';
    var when  = d && d.ts  ? new Date(d.ts) : new Date();
    var stamp = isNaN(when.getTime()) ? '' : when.toLocaleString('en-US', { hour12: false });

    var attrs = ' class="out-line ' + level + '"';
    if (run) attrs += ' data-run="' + esc(run) + '" style="--ll-h:' + hashHue(run) + '"';

    return '<div' + attrs + '>'
         + (run ? '<span class="out-line-run">' + esc(run.slice(0, 4)) + '</span>' : '')
         + '<span class="out-line-text">' + contentHtml(frame, plain, tags) + '</span>'
         + '<span class="out-line-timestamp">' + esc(stamp) + '</span>'
         + '</div>';
}

/// Только тело строки, без обёртки, времени и уровня: для панелей, где время,
/// уровень и поток уже стоят своими колонками (логи ZP7).
function body(line) {
    var frame = lastFrame(String(line == null ? '' : line));
    var plain = stripAnsi(frame);
    return contentHtml(frame, plain, leadTags(plain));
}

return {
    build:   build,
    body:    body,
    level:   function (line) { var p = stripAnsi(lastFrame(String(line || ''))); return levelOf(p, leadTags(p).list); },
    hue:     hashHue,
    plain:   function (line) { return stripAnsi(lastFrame(String(line || ''))); }
};

})();
