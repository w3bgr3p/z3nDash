// Diagnostic mode: browser half of DiagTrace (Controllers/DiagTrace*.cs).
// Ctrl+Shift+D toggles recording. While recording, every same-origin fetch gets
// an X-Diag-Id header so the server record of the same request can be joined
// with this one; clicks, long tasks, slow input events and open event streams
// are recorded too. Events go to /diag/client in batches.
// Must load before any page script that calls fetch or opens an EventSource.
(function () {
    'use strict';
    if (window.__z3nDiag) return;

    var origFetch = window.fetch.bind(window);
    var OrigES    = window.EventSource;
    var on    = false;   // server says recording
    var known = false;   // /diag/state answered
    var buf = [], early = [];
    var seq = 0, busy = 0, sseOpen = 0;
    var lastAct = null;
    var prefix = Math.random().toString(36).slice(2, 7);
    var pageName = location.pathname + location.search;
    var waiting = {};    // href -> [{rec, p0}] until the resource timing entry arrives
    var inTimer = 0, inEvent = false;

    // Polls run from setInterval: a fetch made inside one is marked src=timer and is
    // never attributed to a click that happened to precede it.
    var origSetInterval = window.setInterval;
    window.setInterval = function (cb) {
        var args = Array.prototype.slice.call(arguments);
        if (typeof cb === 'function')
            args[0] = function () { inTimer++; try { return cb.apply(this, arguments); } finally { inTimer--; } };
        return origSetInterval.apply(window, args);
    };

    // Wall clock in ms on the same scale as the server's DateTime.UtcNow.
    // Re-anchored on every call: performance.now() drifts from the system clock over hours.
    function wall(p) { return Date.now() - performance.now() + (p === undefined ? performance.now() : p); }
    function r1(v) { return Math.round(v * 10) / 10; }

    function push(rec) {
        rec.page = pageName;
        if (on) buf.push(rec);
        else if (!known && early.length < 500) early.push(rec);
    }

    function label(el) {
        try {
            if (!el || !el.closest) return '';
            var t = el.closest('button,a,[onclick],[role=button],input,select,tr,li') || el;
            var s = t.getAttribute('title') || t.getAttribute('aria-label') || t.innerText || t.textContent || t.value || t.id || t.className || t.tagName;
            return String(s).replace(/\s+/g, ' ').trim().slice(0, 60);
        } catch (e) { return ''; }
    }

    // Two nearest page functions that called fetch: tells a poll (loadList) from a button handler.
    function caller() {
        try {
            var lines = String(new Error().stack || '').split('\n').slice(1), out = [];
            for (var i = 0; i < lines.length && out.length < 2; i++) {
                if (lines[i].indexOf('diag.js') >= 0) continue;
                var m = /at (?:async )?([\w$.<>]+) \(/.exec(lines[i]);
                if (m) out.push(m[1]);
            }
            return out.join('<');
        } catch (e) { return ''; }
    }

    // ── fetch ────────────────────────────────────────────────────────────────

    window.fetch = function (input, init) {
        var url;
        try { url = new URL(input && input.url ? input.url : String(input), location.href); }
        catch (e) { return origFetch(input, init); }

        var tracked = (on || !known) && url.origin === location.origin && url.pathname.indexOf('/diag/') !== 0;
        if (!tracked) {
            busy++;
            return origFetch(input, init).finally(function () { busy--; });
        }

        var cid = prefix + '-' + (++seq), req;
        try { req = new Request(input, init); req.headers.set('X-Diag-Id', cid); }
        catch (e) { return origFetch(input, init); }

        var p0  = performance.now();
        var rec = { k: 'cf', t: r1(wall(p0)), cid: cid, m: req.method,
                    u: url.pathname + url.search.slice(0, 80), busy: busy, sse: sseOpen, fn: caller(),
                    src: inTimer ? 'timer' : inEvent ? 'event' : '' };
        if (rec.src !== 'timer' && lastAct && rec.t - lastAct.t <= 3000) rec.act_t = lastAct.t;

        busy++;
        var slot = { rec: rec, p0: p0 };
        (waiting[url.href] = waiting[url.href] || []).push(slot);
        // No timing entry in 30 s (aborted, never finished) — keep what is known.
        slot.timer = setTimeout(function () { settle(url.href, slot, null); }, 30000);

        return origFetch(req).then(function (res) {
            busy--;
            rec.st  = res.status;
            rec.dur = r1(performance.now() - p0);
            return res;
        }, function (err) {
            busy--;
            rec.err = String(err && err.message || err).slice(0, 200);
            rec.dur = r1(performance.now() - p0);
            settle(url.href, slot, null);
            throw err;
        });
    };

    function settle(href, slot, entry) {
        var list = waiting[href];
        if (!list) return;
        var i = list.indexOf(slot);
        if (i < 0) return;
        list.splice(i, 1);
        if (!list.length) delete waiting[href];
        clearTimeout(slot.timer);
        var rec = slot.rec;
        if (entry) {
            // requestStart is 0 when the browser hides timing; then only dur is known.
            if (entry.requestStart > 0) {
                rec.stall = r1(entry.requestStart - entry.startTime);
                rec.ttfb  = r1(entry.responseStart - entry.requestStart);
                rec.rs    = r1(wall(entry.requestStart));
            }
            rec.dl = r1(entry.responseEnd - entry.responseStart);
            rec.re = r1(wall(entry.responseEnd));
        }
        push(rec);
    }

    function onResource(entry) {
        var list = waiting[entry.name];
        if (!list || (entry.initiatorType !== 'fetch' && entry.initiatorType !== 'other')) return;
        var best = null, gap = 100;
        for (var i = 0; i < list.length; i++) {
            var d = Math.abs(entry.startTime - list[i].p0);
            if (d < gap) { gap = d; best = list[i]; }
        }
        if (best) settle(entry.name, best, entry);
    }

    try {
        new PerformanceObserver(function (l) { l.getEntries().forEach(onResource); })
            .observe({ type: 'resource' });
        performance.setResourceTimingBufferSize(2000);
        performance.addEventListener('resourcetimingbufferfull', function () { performance.clearResourceTimings(); });
    } catch (e) { /* no Resource Timing: records keep dur only */ }

    // ── EventSource: each open stream holds one connection to the server ───

    if (OrigES) {
        var DiagES = function (u, cfg) {
            var es = new OrigES(u, cfg), closed = false, path = '';
            try { path = new URL(String(u), location.href).pathname; } catch (e) { path = String(u); }
            sseOpen++;
            push({ k: 'ces', t: r1(wall()), u: path, state: 'new', n: sseOpen });
            es.addEventListener('open', function () { push({ k: 'ces', t: r1(wall()), u: path, state: 'open', n: sseOpen }); });
            es.addEventListener('error', function () {
                if (es.readyState === 2 && !closed) { closed = true; sseOpen--; }
                push({ k: 'ces', t: r1(wall()), u: path, state: 'error', rs: es.readyState, n: sseOpen });
            });
            var close = es.close.bind(es);
            es.close = function () {
                if (!closed) { closed = true; sseOpen--; push({ k: 'ces', t: r1(wall()), u: path, state: 'close', n: sseOpen }); }
                close();
            };
            return es;
        };
        DiagES.prototype = OrigES.prototype;
        DiagES.CONNECTING = 0; DiagES.OPEN = 1; DiagES.CLOSED = 2;
        window.EventSource = DiagES;
    }

    // ── User actions and main-thread stalls ─────────────────────────────────

    function act(what, target) {
        if (!on) return;
        // Set for the synchronous part of the dispatch; cleared once handlers have run.
        inEvent = true;
        setTimeout(function () { inEvent = false; }, 0);
        lastAct = { t: r1(wall()), label: label(target) };
        push({ k: 'ca', t: lastAct.t, what: what, label: lastAct.label });
    }

    document.addEventListener('click', function (e) { act('click', e.target); }, true);
    document.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === 'Delete') act('key ' + e.key, e.target);
    }, true);

    try {
        new PerformanceObserver(function (l) {
            if (!on) return;
            l.getEntries().forEach(function (e) {
                push({ k: 'clt', t: r1(wall(e.startTime)), ms: r1(e.duration) });
            });
        }).observe({ type: 'longtask' });
    } catch (e) { /* longtask unsupported */ }

    try {
        // Input → next paint, split into the wait before handlers ran and the handlers themselves.
        new PerformanceObserver(function (l) {
            if (!on) return;
            l.getEntries().forEach(function (e) {
                if (e.name !== 'click' && e.name !== 'keydown' && e.name !== 'pointerup') return;
                push({ k: 'cev', t: r1(wall(e.startTime)), type: e.name, ms: r1(e.duration),
                       delay: r1(e.processingStart - e.startTime), proc: r1(e.processingEnd - e.processingStart),
                       label: label(e.target) });
            });
        }).observe({ type: 'event', durationThreshold: 100 });
    } catch (e) { /* Event Timing unsupported */ }

    // ── Sending to the server ───────────────────────────────────────────────

    function flush() {
        while (buf.length) {
            var body = JSON.stringify(buf.splice(0, 150));
            var sent = false;
            try { sent = navigator.sendBeacon('/diag/client', new Blob([body], { type: 'application/json' })); } catch (e) { }
            if (!sent) origFetch('/diag/client', { method: 'POST', body: body, keepalive: true }).catch(function () { });
        }
    }
    origSetInterval(function () { if (on) flush(); }, 2000);
    window.addEventListener('pagehide', function () { if (on) flush(); });

    // ── State, toggle, badge ────────────────────────────────────────────────

    function apply(enabled) {
        var was = on;
        on = enabled; known = true;
        if (on && !was) {
            buf = buf.concat(early);
            push({ k: 'cpage', t: r1(wall()), ua: navigator.userAgent.slice(0, 160) });
        }
        early = [];
        badge();
    }

    function refresh() {
        origFetch('/diag/state', { cache: 'no-store' })
            .then(function (r) { return r.ok ? r.json() : { enabled: false }; })
            .then(function (j) { apply(!!j.enabled); }, function () { apply(false); });
    }

    // Other open pages learn about a toggle through storage events.
    function announce() { try { localStorage.setItem('z3n.diag.toggle', String(Date.now())); } catch (e) { } }
    window.addEventListener('storage', function (e) { if (e.key === 'z3n.diag.toggle') refresh(); });

    function post(path) {
        return origFetch(path, { method: 'POST' }).then(function (r) { return r.json(); });
    }

    function start() { post('/diag/start').then(function () { apply(true); announce(); }); }

    // The tail of the buffer must be on the server before the dump is closed:
    // a beacon can't be awaited, so the last batch goes as a plain POST.
    function flushAndWait() {
        var batches = [];
        while (buf.length) {
            var body = JSON.stringify(buf.splice(0, 150));
            batches.push(origFetch('/diag/client', { method: 'POST', body: body }).catch(function () { }));
        }
        return Promise.all(batches);
    }

    var stopping = false;
    function stop() {
        if (stopping) return;
        stopping = true;
        badgeText('saving…');
        flushAndWait().then(function () { return post('/diag/stop'); }).then(function (j) {
            stopping = false;
            apply(false); announce();
            showReport('Recording stopped.\nDump: ' + j.file + '\nReport: ' + j.report + '\n\n');
        }, function () { stopping = false; badgeText('DIAG REC'); });
    }

    function badgeText(text) {
        var span = badgeEl && badgeEl.children[1];
        if (span) span.textContent = text;
    }

    document.addEventListener('keydown', function (e) {
        if (e.ctrlKey && e.shiftKey && (e.key === 'D' || e.key === 'd' || e.code === 'KeyD')) {
            e.preventDefault();
            if (on) stop(); else start();
        }
    }, true);

    var badgeEl = null;
    function badge() {
        if (!document.body) { document.addEventListener('DOMContentLoaded', badge, { once: true }); return; }
        if (!on) { if (badgeEl) { badgeEl.remove(); badgeEl = null; } return; }
        if (badgeEl) return;
        badgeEl = document.createElement('div');
        badgeEl.style.cssText = 'position:fixed;left:12px;bottom:12px;z-index:2147483646;display:flex;gap:6px;align-items:center;' +
            'padding:4px 8px;border-radius:6px;background:rgba(20,20,20,.88);color:#f2f2f2;font:12px/1.4 monospace;' +
            'box-shadow:0 2px 8px rgba(0,0,0,.4)';
        badgeEl.title = 'Diagnostic recording. Ctrl+Shift+D stops it.';
        badgeEl.innerHTML = '<span style="color:#ff4d4d">●</span><span>DIAG REC</span>';
        badgeEl.appendChild(button('Report', function () { flushAndWait().then(function () { showReport(''); }); }));
        badgeEl.appendChild(button('Stop', stop));
        document.body.appendChild(badgeEl);
    }

    function button(text, fn) {
        var b = document.createElement('button');
        b.textContent = text;
        b.style.cssText = 'font:inherit;padding:1px 6px;border:1px solid #666;border-radius:4px;background:#333;color:#f2f2f2;cursor:pointer';
        b.addEventListener('click', function (e) { e.stopPropagation(); fn(); });
        return b;
    }

    function showReport(head) {
        origFetch('/diag/report', { cache: 'no-store' }).then(function (r) { return r.text(); }).then(function (text) {
            var wrap = document.createElement('div');
            wrap.style.cssText = 'position:fixed;inset:24px;z-index:2147483647;display:flex;flex-direction:column;' +
                'background:#161616;color:#e8e8e8;border:1px solid #555;border-radius:8px;box-shadow:0 8px 30px rgba(0,0,0,.6)';
            var bar = document.createElement('div');
            bar.style.cssText = 'display:flex;gap:6px;padding:6px;border-bottom:1px solid #444;font:12px monospace';
            var pre = document.createElement('pre');
            pre.style.cssText = 'flex:1;margin:0;padding:10px;overflow:auto;font:12px/1.45 monospace;white-space:pre';
            pre.textContent = head + text;
            bar.appendChild(button('Copy', function () { navigator.clipboard.writeText(pre.textContent); }));
            bar.appendChild(button('Close', function () { wrap.remove(); }));
            wrap.appendChild(bar); wrap.appendChild(pre);
            document.body.appendChild(wrap);
        });
    }

    refresh();
    window.__z3nDiag = { push: push, start: start, stop: stop, report: function () { showReport(''); } };
})();
