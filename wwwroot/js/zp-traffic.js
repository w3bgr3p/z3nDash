// Просмотр HTTP-трафика с нод ZP7. Читает /zp/traffic — хвост trafficLog.jsonl
// на ноде, тем же способом, что ZpAllLogs читает /zp/log.
//
// Перенесено со страницы http.html целиком: панель деталей, плеер запросов,
// API Skeleton/Example, конвертеры кода и cURL Import.
window.ZpTraffic = (() => {
    'use strict';

    const stateKey = 'zp7_traffic_state';
    const fields = ['machine', 'project', 'method', 'status', 'search', 'limit'];
    const methods = ['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD'];

    // Плеер бьёт в отдельный порт: origin + 1, там висит /http-replay.
    const replayServer = (() => {
        const u = new URL(window.location.href);
        return `${u.protocol}//${u.hostname}:${parseInt(u.port || '80') + 1}`;
    })();

    let dialog, rows = [], visible = [], selected = null, current = null;
    let timer = null, request = null, playRequest = null, auto = true;

    const el = id => dialog.querySelector('#ztr-' + id);
    const value = id => el(id).value;
    const escape = text => String(text ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    const key = row => JSON.stringify([row.timestamp, row.machine, row.method, row.statusCode, row.url]);

    function create() {
        dialog = document.createElement('dialog');
        dialog.id = 'zpTraffic';
        dialog.setAttribute('aria-labelledby', 'ztr-title');
        dialog.innerHTML = markup();
        document.body.append(dialog);
    }

    function markup() {
        return `
            <header><h2 id="ztr-title">traffic</h2><span id="ztr-count"></span><button id="ztr-close" aria-label="Close traffic">✕</button></header>
            <div class="ztr-controls">
                <select id="ztr-machine" aria-label="Machine"><option value="">All machines</option></select>
                <select id="ztr-project" aria-label="Project"><option value="">All projects</option></select>
                <select id="ztr-method" aria-label="Method"><option value="">All methods</option>${methods.map(m => `<option>${m}</option>`).join('')}</select>
                <select id="ztr-status" aria-label="Status">
                    <option value="">All statuses</option><option value="2">2xx Success</option><option value="3">3xx Redirect</option>
                    <option value="4">4xx Client Error</option><option value="5">5xx Server Error</option><option value="0">No response</option>
                </select>
                <input id="ztr-search" type="search" placeholder="Filter by URL..." aria-label="Filter by URL">
                <input id="ztr-limit" type="number" min="10" max="2000" step="10" value="200" aria-label="Entries per node">
                <button id="ztr-refresh">Refresh</button><button id="ztr-auto">Auto: ON</button>
                <button id="ztr-reset">Reset filters</button><button id="ztr-clear">Clear view</button>
                <button id="ztr-curl-open">&#x2B07; cURL Import</button>
            </div>
            <div id="ztr-note" class="ztr-status" role="status"></div>
            <div class="ztr-content">
                <div class="ztr-list" id="ztr-rows"></div>
                <aside class="ztr-details" id="ztr-details" hidden aria-label="Request details">
                    <div class="ztr-details-head"><span>Request details</span><button id="ztr-details-close" aria-label="Close details">✕</button></div>
                    <div class="ztr-details-body" id="ztr-details-body"></div>
                </aside>
            </div>
            <div class="ztr-overlay" id="ztr-play">
                <div class="ztr-dialog">
                    <div class="ztr-dialog-head"><span>&#9889; Request Replay</span><button id="ztr-play-close" aria-label="Close replay">✕</button></div>
                    <div class="ztr-url-row">
                        <select id="ztr-play-method">${methods.map(m => `<option>${m}</option>`).join('')}</select>
                        <input id="ztr-play-url" placeholder="https://...">
                        <button class="ztr-send" id="ztr-play-send">&#9654; Send</button>
                        <span class="ztr-play-status" id="ztr-play-status"></span>
                    </div>
                    <div class="ztr-dialog-body">
                        <div class="ztr-pane">
                            <div class="ztr-label">Headers (one per line, Key: Value)</div>
                            <textarea class="ztr-area" id="ztr-play-headers"></textarea>
                            <div class="ztr-label" style="border-top:1px solid var(--border)">Body</div>
                            <textarea class="ztr-area" id="ztr-play-body" style="flex:0 0 130px"></textarea>
                        </div>
                        <div class="ztr-pane">
                            <div class="ztr-label">Response</div>
                            <textarea class="ztr-area" id="ztr-play-response" readonly placeholder="Response will appear here..."></textarea>
                        </div>
                    </div>
                    <div class="ztr-dialog-foot">
                        <button id="ztr-play-copy">&#x1F4CB; Copy response</button>
                        <button id="ztr-play-curl">&#x1F4CB; cURL</button>
                    </div>
                </div>
            </div>
            <div class="ztr-overlay" id="ztr-curl">
                <div class="ztr-dialog auto">
                    <div class="ztr-dialog-head"><span>&#x2B07; cURL Import</span><button id="ztr-curl-close" aria-label="Close import">✕</button></div>
                    <div style="padding:10px 12px;display:flex;flex-direction:column;gap:8px;overflow:auto">
                        <div class="ztr-label" style="padding:0;border:none;background:none">Paste cURL command</div>
                        <textarea id="ztr-curl-input" style="min-height:140px;resize:vertical;font-family:ui-monospace,monospace"></textarea>
                        <div id="ztr-curl-error" class="ztr-error" hidden></div>
                    </div>
                    <div class="ztr-dialog-foot"><button class="ztr-send" id="ztr-curl-import">&#x2B07; Import</button><button id="ztr-curl-cancel">Cancel</button></div>
                </div>
            </div>
            <div class="ztr-flash" id="ztr-flash">Copied!</div>`;
    }

    // ── состояние и справочники ──────────────────────────────────────────────

    function save() {
        try { sessionStorage.setItem(stateKey, JSON.stringify({ ...Object.fromEntries(fields.map(id => [id, value(id)])), auto })); } catch {}
    }

    function restore() {
        try {
            const state = JSON.parse(sessionStorage.getItem(stateKey) || '{}');
            for (const id of fields) {
                if (typeof state[id] !== 'string') continue;
                if (['machine', 'project'].includes(id) && state[id]) el(id).add(new Option(state[id], state[id]));
                el(id).value = state[id];
            }
            auto = state.auto !== false;
        } catch {}
        if (!value('limit')) el('limit').value = '200';
    }

    function options(id, items, label) {
        const select = el(id);
        if (document.activeElement === select) return;   // не дёргаем открытый список
        const previous = value(id);
        const values = [...new Set([...items, previous].filter(Boolean))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
        if (select.options.length === values.length + 1 && values.every((item, index) => select.options[index + 1].value === item)) return;
        select.replaceChildren(new Option(label, ''), ...values.map(item => new Option(item, item)));
        select.value = previous;
    }

    // ── загрузка ─────────────────────────────────────────────────────────────

    /// В сообщение идёт то, что реально ответил сервер, а не «Unexpected end of
    /// JSON input»: пустой ответ и HTML-страница ошибки иначе неразличимы.
    async function getJson(url, signal) {
        const response = await fetch(url, { signal, cache: 'no-store' });
        const text = await response.text();
        let data;
        try { data = JSON.parse(text); }
        catch {
            const body = text.trim().slice(0, 200);
            throw new Error(`HTTP ${response.status}, not JSON: ${body || '(empty body)'}`);
        }
        if (!response.ok || data.error) throw new Error(data.error || `HTTP ${response.status}`);
        return data;
    }

    async function refresh() {
        if (!dialog.open) return;
        request?.abort();
        const controller = new AbortController();
        request = controller;
        el('note').textContent = 'Loading...';
        el('note').classList.remove('error');
        save();
        try {
            const nodes = await getJson('/zp/nodes?probe=false', controller.signal);
            if (!Array.isArray(nodes)) throw new Error('Invalid node list');
            if (controller.signal.aborted) return;
            options('machine', nodes.map(node => node.machine), 'All machines');

            const targets = nodes.filter(node => !value('machine') || node.machine === value('machine'));
            const query = new URLSearchParams({ tail: value('limit') || '200' });
            if (value('project').trim()) query.set('project', value('project').trim());

            const results = await Promise.allSettled(targets.map(async node => {
                const params = new URLSearchParams(query);
                params.set('machine', node.machine);
                const data = await getJson('/zp/traffic?' + params, controller.signal);
                if (!Array.isArray(data.entries)) throw new Error('Invalid traffic response');
                return data.entries.map(entry => ({ ...entry, machine: entry.machine || node.machine }));
            }));
            if (controller.signal.aborted) return;

            rows = results.flatMap(result => result.status === 'fulfilled' ? result.value : []);
            rows.sort((a, b) => String(b.timestamp ?? '').localeCompare(String(a.timestamp ?? '')));
            options('project', rows.map(row => row.project).concat(Array.from(el('project').options, option => option.value)), 'All projects');

            const failures = results.flatMap((result, index) => result.status === 'rejected' ? [`${targets[index].machine}: ${result.reason.message}`] : []);
            el('note').textContent = targets.length
                ? `${results.length - failures.length}/${targets.length} nodes · ${new Date().toLocaleTimeString()}${failures.length ? '\n' + failures.join('\n') : ''}`
                : 'No matching ZP7 nodes registered';
            el('note').classList.toggle('error', failures.length > 0);
            render();
        } catch (error) {
            if (controller.signal.aborted) return;
            rows = []; render();
            el('note').textContent = error.message;
            el('note').classList.add('error');
        } finally {
            if (request === controller) request = null;
        }
    }

    // ── список ───────────────────────────────────────────────────────────────

    const statusClass = code => code >= 200 && code < 300 ? 's2xx' : code >= 300 && code < 400 ? 's3xx'
        : code >= 400 && code < 500 ? 's4xx' : code >= 500 ? 's5xx' : 's0xx';
    const methodClass = m => methods.includes(m) ? m : 'GET';
    const truncate = (text, max) => text.length <= max ? text : text.slice(0, max) + '...';

    function matches(row) {
        const search = value('search').toLowerCase();
        if (value('method') && row.method !== value('method')) return false;
        if (value('status') && !String(row.statusCode ?? '').startsWith(value('status'))) return false;
        if (value('project') && (row.project || '') !== value('project')) return false;
        if (search && !String(row.url || '').toLowerCase().includes(search)) return false;
        return true;
    }

    function render() {
        visible = rows.filter(matches);

        const ok = visible.filter(row => row.statusCode >= 200 && row.statusCode < 300).length;
        const timed = visible.filter(row => typeof row.durationMs === 'number');
        const avg = timed.length ? Math.round(timed.reduce((sum, row) => sum + row.durationMs, 0) / timed.length) : 0;
        el('count').textContent = `${visible.length} / ${rows.length} · 2xx: ${visible.length ? Math.round(ok / visible.length * 100) : 0}% · avg ${avg}ms`;

        el('rows').innerHTML = visible.length ? visible.map((row, index) => {
            const url = String(row.url || '');
            const time = row.timestamp ? String(row.timestamp).slice(11, 23) : '';
            return `<div class="ztr-row${key(row) === selected ? ' selected' : ''}" data-index="${index}" tabindex="0">
                <span class="ztr-ts" title="${escape(row.timestamp)}">${escape(time)}</span>
                <span class="ztr-machine" title="${escape(row.machine)}">${escape(row.machine)}</span>
                <span class="m ${methodClass(row.method)}">${escape(row.method)}</span>
                <span class="ztr-url" title="${escape(url)}">${escape(truncate(url, 160))}</span>
                <span class="s ${statusClass(row.statusCode)}">${escape(row.statusCode)}</span>
                <span class="ztr-dur">${row.durationMs != null ? escape(row.durationMs) + 'ms' : '-'}</span>
                <span class="ztr-acc" title="${escape(row.account)}">${escape(row.account || '-')}</span>
            </div>`;
        }).join('') : `<div class="ztr-empty">${rows.length ? 'No requests matching the filters' : 'No traffic from the nodes'}</div>`;

        // Выбранная строка может уехать из выборки — панель деталей при этом
        // остаётся открытой: запись уже загружена, и с ней продолжают работать.
        // Импортированный из cURL запрос в списке не лежит вовсе.
    }

    // ── панель деталей ───────────────────────────────────────────────────────

    const empty = value => value === null || value === undefined || value === '' || value === '-';

    function headerLines(raw) {
        if (!raw) return [];
        if (Array.isArray(raw)) return raw.filter(line => line && line.trim());
        if (typeof raw === 'string') return raw.split(/\r?\n/).filter(line => line.trim());
        return [];
    }

    function prettyJson(text) {
        try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
    }

    function flash() {
        el('flash').classList.add('show');
        setTimeout(() => el('flash').classList.remove('show'), 1200);
    }

    function copy(text) {
        navigator.clipboard.writeText(text).then(flash, () => {});
    }

    function copyButton(button, text) {
        navigator.clipboard.writeText(text).then(() => {
            const original = button.textContent;
            button.textContent = '✓ Copied!';
            setTimeout(() => { button.textContent = original; }, 1800);
        }, () => {});
    }

    function toggle(element) {
        const open = element.classList.toggle('open');
        element.nextElementSibling.classList.toggle('open', open);
    }

    function showDetails(index) {
        const row = visible[index];
        if (!row) return;
        selected = key(row);
        current = row;
        render();
        renderDetails();
    }

    function hideDetails() {
        selected = null;
        current = null;
        el('details').hidden = true;
        render();
    }

    function renderDetails() {
        const row = current;
        if (!row) return;
        el('details').hidden = false;
        const body = el('details-body');
        body.replaceChildren();

        const url = document.createElement('div');
        url.className = 'ov-url';
        url.title = 'Click to copy URL';
        url.textContent = row.url;
        url.onclick = () => copy(row.url);
        body.append(url);

        const badges = document.createElement('div');
        badges.className = 'ov-badges';
        badges.innerHTML = `<span class="m ${methodClass(row.method)}">${escape(row.method)}</span>
            <span class="s ${statusClass(row.statusCode)}">${escape(row.statusCode)}</span>
            ${row.durationMs != null ? `<span class="badge-duration">${escape(row.durationMs)}ms</span>` : ''}
            ${!empty(row.timestamp) ? `<span class="badge-meta">${escape(row.timestamp)}</span>` : ''}`;
        body.append(badges);

        const proxy = !empty(row.proxy) ? row.proxy : (!empty(row.request?.proxy) ? row.request.proxy : null);
        const meta = [
            [row.machine, 'machine'], [row.project, 'project'], [row.source, 'origin'],
            [row.account, 'acc'], [row.session, 'sess'], [proxy, 'proxy'], [row.task_id, 'task'],
        ].filter(([item]) => !empty(item))
            .map(([item, label]) => `<span class="${label === 'origin' ? 'badge-origin' : 'badge-meta'}">${label === 'origin' ? '' : label + ':'}${escape(item)}</span>`);
        if (meta.length) {
            const line = document.createElement('div');
            line.className = 'ov-meta';
            line.innerHTML = meta.join('');
            body.append(line);
        }

        body.append(actions());
        body.append(section('Request', row.request || {}, true));
        body.append(section('Response', row.response || {}, false));
    }

    function actions() {
        const wrap = document.createElement('div');
        wrap.className = 'action-rows';
        wrap.innerHTML = `
            <div class="action-row">
                <button class="act-btn play" data-act="play">&#9654; Play</button>
                <button class="act-btn api-sk" data-act="skeleton">&#x2398; API Skeleton</button>
                <button class="act-btn api-ex" data-act="last">&#x2398; API Example</button>
            </div>
            <div class="action-row">
                <button class="act-btn httpcl" data-act="csharp">&#x2398; HttpClient</button>
                <button class="act-btn zp7" data-act="zp7">&#x2398; ZP7</button>
                <button class="act-btn hybrid" data-act="hybrid">&#x2398; Hybrid</button>
                <button class="act-btn python" data-act="python">&#x2398; Python</button>
                <button class="act-btn ts" data-act="ts">&#x2398; TypeScript</button>
                <button class="act-btn curl" data-act="curl">&#x2398; cURL</button>
            </div>`;
        wrap.onclick = event => {
            const button = event.target.closest('[data-act]');
            if (!button || !current) return;
            const act = button.dataset.act;
            if (act === 'play') { openPlay(); return; }
            if (act === 'skeleton' || act === 'last') { copyButton(button, apiStructure(act)); return; }
            copyButton(button, builders[act](current));
        };
        return wrap;
    }

    /// Одна секция деталей: Request или Response. Cookie показываем только у запроса —
    /// у ответа они приезжают строками Set-Cookie внутри заголовков.
    function section(title, part, isRequest) {
        const lines = headerLines(part.headers);
        let cookies = isRequest ? (part.cookies || part.Cookies || '') : '';
        if (isRequest && !cookies) {
            const line = lines.find(item => item.toLowerCase().startsWith('cookie:'));
            if (line) cookies = line.slice(line.indexOf(':') + 1).trim();
        }
        const shown = isRequest ? lines.filter(item => !item.toLowerCase().startsWith('cookie:')) : lines;

        const root = document.createElement('div');
        root.className = 'detail-section';

        const head = document.createElement('div');
        head.className = 'section-toggle open';
        head.innerHTML = `<span class="section-arrow">&#9654;</span> ${title}`;
        head.onclick = () => toggle(head);
        root.append(head);

        const body = document.createElement('div');
        body.className = 'section-body open';
        body.append(...subsection(`Headers (${shown.length})`, false, headerList(shown)));
        if (cookies) body.append(...subsection('Cookie', false, block('cookies-block', cookies, cookies)));
        body.append(...subsection('Body', true, part.body ? block('body-block', prettyJson(part.body), part.body) : note('Empty body')));
        root.append(body);
        return root;
    }

    function subsection(title, open, content) {
        const head = document.createElement('div');
        head.className = 'subsection-toggle' + (open ? ' open' : '');
        head.innerHTML = `<span class="subsection-arrow">&#9654;</span> ${title}`;
        head.onclick = () => toggle(head);

        const body = document.createElement('div');
        body.className = 'subsection-body' + (open ? ' open' : '');
        body.append(content);
        return [head, body];
    }

    function headerList(lines) {
        if (!lines.length) return note('No headers');
        const list = document.createElement('div');
        for (const line of lines) {
            const colon = line.indexOf(':', line.startsWith(':') ? 1 : 0);
            if (colon === -1) continue;
            const name = line.slice(0, colon).trim();
            const text = line.slice(colon + 1).trim();
            const item = document.createElement('div');
            item.className = 'header-item';
            item.title = 'Click the key to copy "Key: Value", the value to copy the value';
            item.innerHTML = `<span class="header-key">${escape(name)}</span><span class="header-value">${escape(text)}</span>`;
            item.querySelector('.header-key').onclick = () => copy(`${name}: ${text}`);
            item.querySelector('.header-value').onclick = () => copy(text);
            list.append(item);
        }
        return list.childElementCount ? list : note('No headers');
    }

    function block(className, shown, raw) {
        const element = document.createElement('div');
        element.className = className;
        element.title = 'Click to copy';
        element.textContent = shown;
        element.onclick = () => copy(raw);
        return element;
    }

    function note(text) {
        const element = document.createElement('div');
        element.className = 'empty-note';
        element.textContent = text;
        return element;
    }

    // ── конвертеры кода ──────────────────────────────────────────────────────
    // Перенесены со страницы http.html без изменения вывода.

    /// Заголовки запроса без псевдо-заголовков HTTP/2 и замаскированных значений.
    function codeHeaders(record) {
        return headerLines(record.request?.headers).filter(line => !line.startsWith(':') && !line.includes('***MASKED***'));
    }

    /// Разбирает заголовки, вынимая Content-Type, Cookie и User-Agent отдельно.
    function split(lines, extract) {
        const rest = [];
        const out = { contentType: '', cookie: '', userAgent: '' };
        for (const line of lines) {
            const colon = line.indexOf(':');
            if (colon === -1) continue;
            const name = line.slice(0, colon).trim();
            const text = line.slice(colon + 1).trim();
            const lower = name.toLowerCase();
            if (lower === 'content-type') { out.contentType = text; if (extract.includes('content-type')) continue; }
            if (lower === 'cookie') { out.cookie = text; if (extract.includes('cookie')) continue; }
            if (lower === 'user-agent') { out.userAgent = text; if (extract.includes('user-agent')) continue; }
            rest.push([name, text]);
        }
        out.rest = rest;
        return out;
    }

    const hasBody = record => record.request?.body && record.method !== 'GET' && record.method !== 'HEAD';

    const builders = {
        csharp: record => cSharp(record, true),
        hybrid: record => cSharp(record, false),

        zp7(record) {
            const parts = split(codeHeaders(record), ['content-type', 'cookie', 'user-agent']);
            const q = text => String(text ?? '').split('"').join('""');
            const extra = parts.rest.map(([name, text]) => `@"${q(name + ': ' + text)}"`);
            return [
                'var response = ZennoPoster.HTTP.Request(',
                `    ZennoLab.InterfacesLibrary.Enums.Http.HttpMethod.${record.method},`,
                `    @"${q(record.url)}",`,
                `    content: @"${q(record.request?.body || '')}",`,
                `    contentPostingType: @"${q(parts.contentType || 'application/x-www-form-urlencoded')}",`,
                '    proxy: "",',
                '    Encoding: "UTF-8",',
                '    respType: ZennoLab.InterfacesLibrary.Enums.Http.ResponceType.HeaderAndBody,',
                '    Timeout: 30000,',
                `    Cookies: @"${q(parts.cookie)}",`,
                `    UserAgent: @"${q(parts.userAgent)}",`,
                '    UseRedirect: true,',
                '    MaxRedirectCount: 5,',
                extra.length ? `    AdditionalHeaders: new string[] { ${extra.join(', ')} },` : '    AdditionalHeaders: null,',
                '    DownloadPath: null,',
                '    UseOriginalUrl: false,',
                '    throwExceptionOnError: true,',
                '    cookieContainer: null,',
                '    removeDefaultHeaders: true',
                ');',
            ].join('\n');
        },

        python(record) {
            const parts = split(codeHeaders(record), ['cookie']);
            const esc = text => String(text).split('\\').join('\\\\').split("'").join("\\'");
            const str = text => "'" + esc(text) + "'";
            const dict = pairs => pairs.length ? '{\n' + pairs.map(([k, v]) => '    ' + str(k) + ': ' + str(v)).join(',\n') + '\n}' : '{}';

            const lines = ['import requests', '', 'headers = ' + dict(parts.rest), ''];
            if (parts.cookie) {
                const jar = parts.cookie.split(';').map(pair => pair.split('=')).filter(pair => pair.length > 1)
                    .map(pair => [pair[0].trim(), pair.slice(1).join('=').trim()]);
                lines.push('cookies = ' + dict(jar), '');
            }
            const cookiesArg = parts.cookie ? ', cookies=cookies' : '';
            const call = record.method.toLowerCase();
            if (hasBody(record)) {
                if (parts.contentType.includes('json')) {
                    lines.push('import json', '', 'payload = json.loads(' + str(record.request.body) + ')', '',
                        'response = requests.' + call + '(' + str(record.url) + ', headers=headers, json=payload' + cookiesArg + ')');
                } else {
                    lines.push('payload = ' + str(record.request.body), '',
                        'response = requests.' + call + '(' + str(record.url) + ', headers=headers, data=payload' + cookiesArg + ')');
                }
            } else {
                lines.push('response = requests.' + call + '(' + str(record.url) + ', headers=headers' + cookiesArg + ')');
            }
            return lines.concat(['', 'print(response.status_code)', 'print(response.text)']).join('\n');
        },

        ts(record) {
            const parts = split(codeHeaders(record), []);
            const esc = text => String(text).replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/\r/g, '').replace(/\n/g, '\\n');
            const lines = ['const response = await fetch(', '  "' + esc(record.url) + '",', '  {',
                '    method: "' + record.method + '",', '    headers: {'];
            if (parts.rest.length) lines.push(parts.rest.map(([k, v]) => '      "' + esc(k) + '": "' + esc(v) + '"').join(',\n'));
            lines.push('    },');
            if (hasBody(record)) lines.push('    body: "' + esc(record.request.body) + '",');
            lines.push('  }', ');', '', 'const data = await response.text();', 'console.log(response.status, data);');
            return lines.join('\n');
        },

        curl(record) {
            const esc = text => String(text).split("'").join("'\\''");
            let out = 'curl -X ' + record.method + " \\\n  '" + esc(record.url) + "'";
            for (const line of codeHeaders(record)) out += " \\\n  -H '" + esc(line) + "'";
            if (hasBody(record)) out += " \\\n  --data-raw '" + esc(record.request.body) + "'";
            return out;
        },
    };

    /// HttpClient: async — отдельный листинг, sync («Hybrid») — тот же код на .Result.
    function cSharp(record, isAsync) {
        const parts = split(codeHeaders(record), ['content-type', 'cookie']);
        const esc = text => String(text).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
        const pascal = record.method.charAt(0) + record.method.slice(1).toLowerCase();

        const lines = [];
        if (isAsync) lines.push('using System.Net;', 'using System.Net.Http;', 'using System.Text;', '');
        lines.push('var handler = new HttpClientHandler', '{',
            '    AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,',
            '    UseCookies = false', '};', '');
        lines.push(isAsync ? 'using var client = new HttpClient(handler);' : 'var client = new HttpClient(handler);', '');
        lines.push(`var request = new HttpRequestMessage(HttpMethod.${pascal}, "${esc(record.url)}");`, '');

        if (parts.rest.length) {
            lines.push('// Headers');
            for (const [name, text] of parts.rest) lines.push(`request.Headers.TryAddWithoutValidation("${name}", "${esc(text)}");`);
            lines.push('');
        }
        if (parts.cookie) lines.push('// Cookies', `request.Headers.TryAddWithoutValidation("Cookie", "${esc(parts.cookie)}");`, '');
        if (hasBody(record)) {
            lines.push('// Body',
                `request.Content = new StringContent("${esc(record.request.body).replace(/\r/g, '').replace(/\n/g, '\\n')}", Encoding.UTF8, "${parts.contentType || 'application/json'}");`, '');
        }

        if (isAsync) {
            lines.push('var response = await client.SendAsync(request);', 'var responseBody = await response.Content.ReadAsStringAsync();', '',
                'Console.WriteLine($"Status: {(int)response.StatusCode}");', 'Console.WriteLine(responseBody);');
        } else {
            lines.push('var response = client.SendAsync(request).Result;', 'var responseBody = response.Content.ReadAsStringAsync().Result;', '',
                '// response.StatusCode, responseBody');
        }
        return lines.join('\n');
    }

    // ── API Skeleton / Example ───────────────────────────────────────────────
    // Сводит все показанные запросы в описание эндпоинтов: skeleton — типы полей,
    // last — последнее реальное значение.

    function skeleton(value) {
        if (value === null || value === undefined) return null;
        if (Array.isArray(value)) return value.length ? [skeleton(value[0])] : [];
        if (typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, skeleton(v)]));
        return typeof value;
    }

    function parseJson(text) {
        if (!text) return null;
        try { return JSON.parse(text); } catch { return null; }
    }

    function headerMap(lines, skip) {
        const map = {};
        for (const line of lines) {
            if (line.startsWith(':')) continue;
            const colon = line.indexOf(':');
            if (colon < 0) continue;
            const name = line.slice(0, colon).trim().toLowerCase();
            if (name === skip) continue;
            map[name] = line.slice(colon + 1).trim();
        }
        return map;
    }

    function apiStructure(mode) {
        const endpoints = new Map();

        for (const row of visible) {
            const path = String(row.url || '').split('?')[0];
            const last = path.slice(path.lastIndexOf('/') + 1);
            if (!path.endsWith('/') && last.includes('.')) continue;   // статика, не эндпоинт

            const id = row.method + ':' + row.url;
            const requestLines = headerLines(row.request?.headers);
            const responseLines = headerLines(row.response?.headers);

            if (!endpoints.has(id)) {
                const cookieLine = requestLines.find(line => line.toLowerCase().startsWith('cookie:'));
                endpoints.set(id, {
                    method: row.method,
                    url: row.url,
                    statusCode: row.statusCode,
                    requestHeaders: headerMap(requestLines, 'cookie'),
                    responseHeaders: headerMap(responseLines, 'set-cookie'),
                    requestCookies: cookieLine ? cookieLine.slice(cookieLine.indexOf(':') + 1).trim() : '',
                    responseCookies: responseLines.filter(line => line.toLowerCase().startsWith('set-cookie:'))
                        .map(line => line.slice(line.indexOf(':') + 1).trim()).join('\n'),
                    bodies: new Map(),
                    responseBody: null,
                });
            }
            const endpoint = endpoints.get(id);

            const requestBody = row.request?.body || '';
            const parsed = parseJson(requestBody);
            const shape = parsed !== null ? skeleton(parsed) : (requestBody ? 'string' : null);
            if (shape !== null) {
                const shapeKey = JSON.stringify(shape);
                endpoint.bodies.set(shapeKey, { skeleton: shape, last: parsed !== null ? parsed : requestBody });
            }

            const responseBody = row.response?.body || '';
            if (responseBody) {
                const parsedResponse = parseJson(responseBody);
                endpoint.responseBody = mode === 'skeleton'
                    ? (parsedResponse !== null ? skeleton(parsedResponse) : 'string')
                    : (parsedResponse !== null ? parsedResponse : responseBody);
            }
        }

        const result = [...endpoints.values()].map(endpoint => {
            const picked = [...endpoint.bodies.values()].map(item => mode === 'skeleton' ? item.skeleton : item.last);
            return {
                method: endpoint.method,
                url: endpoint.url,
                statusCode: endpoint.statusCode,
                requestHeaders: endpoint.requestHeaders,
                responseHeaders: endpoint.responseHeaders,
                requestCookies: endpoint.requestCookies || null,
                responseCookies: endpoint.responseCookies || null,
                requestBody: picked.length === 0 ? null : picked.length === 1 ? picked[0] : picked,
                responseBody: endpoint.responseBody,
            };
        });
        return JSON.stringify(result, null, 2);
    }

    // ── плеер запросов ───────────────────────────────────────────────────────

    function openPlay() {
        if (!current) return;
        el('play-method').value = methods.includes(current.method) ? current.method : 'GET';
        el('play-url').value = current.url || '';
        el('play-headers').value = headerLines(current.request?.headers).filter(line => !line.startsWith(':')).join('\n');
        el('play-body').value = prettyJson(current.request?.body || '');
        el('play-response').value = '';
        el('play-status').textContent = '';
        el('play-status').className = 'ztr-play-status';
        el('play').classList.add('show');
        el('play-url').focus();
    }

    function closePlay() {
        el('play').classList.remove('show');
        playRequest?.abort();
        playRequest = null;
    }

    function headersToObject(text) {
        const result = {};
        for (const line of text.split('\n')) {
            const colon = line.indexOf(':');
            if (colon > 0) result[line.slice(0, colon).trim()] = line.slice(colon + 1).trim();
        }
        return result;
    }

    async function send() {
        const url = el('play-url').value.trim();
        const status = el('play-status');
        const response = el('play-response');
        if (!url) { response.value = 'URL is empty'; return; }

        status.className = 'ztr-play-status';
        status.textContent = 'Sending...';
        response.value = '';

        // Сервер сам ставит кодировку ответа — просить сжатие бессмысленно.
        const headers = headersToObject(el('play-headers').value);
        delete headers['accept-encoding'];
        delete headers['Accept-Encoding'];
        const body = el('play-body').value.trim();

        playRequest?.abort();
        const controller = new AbortController();
        playRequest = controller;

        let raw;
        try {
            const reply = await fetch(`${replayServer}/http-replay`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ url, method: el('play-method').value, headers, body: body || null }),
                signal: controller.signal,
            });
            raw = { text: await reply.text(), code: reply.status };
        } catch (error) {
            if (controller.signal.aborted) return;
            status.className = 'ztr-play-status err';
            status.textContent = 'Unreachable';
            response.value = `Cannot reach ${replayServer}\n\n${error.message}`;
            return;
        } finally {
            if (playRequest === controller) playRequest = null;
        }

        const data = parseJson(raw.text);
        if (!data || data.error) {
            status.className = 'ztr-play-status err';
            status.textContent = data?.error || ('HTTP ' + raw.code);
            response.value = data?.error || (`HTTP ${raw.code}\n\n${raw.text}`);
            return;
        }

        const code = data.statusCode || raw.code;
        status.className = 'ztr-play-status ' + (code >= 200 && code < 400 ? 'ok' : 'err');
        status.textContent = `${code} · ${data.durationMs ?? '?'}ms`;
        const replyHeaders = Object.entries(data.responseHeaders || {}).map(([k, v]) => `${k}: ${v}`).join('\n');
        response.value = `-- Headers --\n${replyHeaders}\n\n-- Body --\n${prettyJson(data.responseBody || '')}`;
    }

    /// cURL из того, что сейчас в полях плеера, а не из исходной записи.
    function playCurl() {
        const esc = text => String(text).split("'").join("'\\''");
        let out = `curl -X ${el('play-method').value} '${esc(el('play-url').value)}'`;
        for (const line of el('play-headers').value.split('\n')) {
            if (line.trim() && !line.includes('***MASKED***')) out += ` \\\n  -H '${esc(line.trim())}'`;
        }
        const body = el('play-body').value;
        if (body.trim()) out += ` \\\n  -d '${esc(body)}'`;
        return out;
    }

    // ── cURL Import ──────────────────────────────────────────────────────────

    /// Токены с учётом кавычек: в одинарных экранирования нет, в двойных есть.
    function tokenize(text) {
        const normalized = text.replace(/\\\s*\n\s*/g, ' ').trim();
        const tokens = [];
        let i = 0;
        while (i < normalized.length) {
            if (normalized[i] === ' ') { i++; continue; }
            if (normalized[i] === "'") {
                const end = normalized.indexOf("'", i + 1);
                tokens.push(normalized.slice(i + 1, end < 0 ? normalized.length : end));
                i = (end < 0 ? normalized.length : end) + 1;
            } else if (normalized[i] === '"') {
                let j = i + 1, out = '';
                while (j < normalized.length && normalized[j] !== '"') {
                    if (normalized[j] === '\\' && j + 1 < normalized.length) { out += normalized[j + 1]; j += 2; }
                    else { out += normalized[j]; j++; }
                }
                tokens.push(out);
                i = j + 1;
            } else {
                let j = i;
                while (j < normalized.length && normalized[j] !== ' ') j++;
                tokens.push(normalized.slice(i, j));
                i = j;
            }
        }
        return tokens;
    }

    function parseCurl(text) {
        const tokens = tokenize(text);
        if (!tokens.length || tokens[0].toLowerCase() !== 'curl') throw new Error('Not a curl command');

        let method = 'GET', url = '', body = '';
        const headers = [];

        for (let i = 1; i < tokens.length; i++) {
            const token = tokens[i];
            if (token === '-X' || token === '--request') method = tokens[++i] || 'GET';
            else if (token === '-H' || token === '--header') { const h = tokens[++i]; if (h) headers.push(h); }
            else if (['-d', '--data', '--data-raw', '--data-binary'].includes(token)) body = tokens[++i] || '';
            else if (token === '--json') {
                body = tokens[++i] || '';
                if (!headers.some(h => h.toLowerCase().startsWith('content-type'))) headers.push('Content-Type: application/json');
            }
            else if (token === '-u' || token === '--user') headers.push('Authorization: Basic ' + btoa(tokens[++i] || ''));
            else if (token === '-x' || token === '--proxy') i++;
            else if (!token.startsWith('-') && !url) url = token;
        }

        if (!url) throw new Error('URL not found');
        if (body && method === 'GET') method = 'POST';

        return {
            method, url, statusCode: 0, durationMs: null,
            timestamp: new Date().toISOString().replace('T', ' ').slice(0, 23),
            machine: '', project: '', account: '', session: '', task_id: '',
            request: { headers, body: body || null, proxy: null },
            response: { headers: [], body: null },
        };
    }

    function importCurl() {
        const error = el('curl-error');
        error.hidden = true;
        let record;
        try { record = parseCurl(el('curl-input').value.trim()); }
        catch (failure) { error.textContent = failure.message; error.hidden = false; return; }

        el('curl').classList.remove('show');
        selected = null;
        current = record;
        render();
        renderDetails();
    }

    // ── связывание и открытие ────────────────────────────────────────────────

    function polling() {
        clearInterval(timer);
        timer = null;
        el('auto').textContent = `Auto: ${auto ? 'ON' : 'OFF'}`;
        // Пока открыт плеер, фон не трогаем: перерисовка спишет ответ на экране.
        if (auto && dialog.open) timer = setInterval(() => { if (!request && !el('play').classList.contains('show')) refresh(); }, 3000);
    }

    function bind() {
        el('close').onclick = () => dialog.close();
        el('details-close').onclick = hideDetails;
        el('refresh').onclick = refresh;
        el('auto').onclick = () => { auto = !auto; polling(); save(); };
        el('clear').onclick = () => { rows = []; hideDetails(); el('note').textContent = 'View cleared'; };
        el('reset').onclick = () => {
            for (const id of ['machine', 'project', 'method', 'status', 'search']) el(id).value = '';
            refresh();
        };
        for (const id of ['machine', 'project', 'limit']) el(id).onchange = refresh;
        for (const id of ['method', 'status']) el(id).onchange = () => { render(); save(); };
        el('search').oninput = () => { render(); save(); };

        el('rows').onclick = event => {
            const row = event.target.closest('[data-index]');
            if (row) showDetails(Number(row.dataset.index));
        };
        el('rows').onkeydown = event => {
            if (event.key !== 'Enter' && event.key !== ' ') return;
            const row = event.target.closest('[data-index]');
            if (row) { event.preventDefault(); showDetails(Number(row.dataset.index)); }
        };

        el('play-close').onclick = closePlay;
        el('play-send').onclick = send;
        el('play-url').onkeydown = event => { if (event.key === 'Enter') send(); };
        el('play-copy').onclick = event => copyButton(event.currentTarget, el('play-response').value);
        el('play-curl').onclick = event => copyButton(event.currentTarget, playCurl());
        el('play').onclick = event => { if (event.target === el('play')) closePlay(); };

        el('curl-open').onclick = () => {
            el('curl-input').value = '';
            el('curl-error').hidden = true;
            el('curl').classList.add('show');
            el('curl-input').focus();
        };
        el('curl-close').onclick = el('curl-cancel').onclick = () => el('curl').classList.remove('show');
        el('curl-import').onclick = importCurl;
        el('curl').onclick = event => { if (event.target === el('curl')) el('curl').classList.remove('show'); };

        // Esc закрывает сначала верхний оверлей, и только потом саму модалку.
        dialog.addEventListener('cancel', event => {
            for (const id of ['play', 'curl']) {
                if (!el(id).classList.contains('show')) continue;
                event.preventDefault();
                if (id === 'play') closePlay(); else el(id).classList.remove('show');
                return;
            }
        });
        dialog.addEventListener('close', () => { clearInterval(timer); request?.abort(); request = null; closePlay(); save(); });
    }

    function open() {
        if (!dialog) { create(); restore(); bind(); }
        if (dialog.open) return;
        dialog.showModal();
        refresh();
        polling();
    }

    window.addEventListener('DOMContentLoaded', () => { if (location.hash === '#traffic') open(); });
    window.addEventListener('hashchange', () => { if (location.hash === '#traffic') open(); });
    return { open };
})();
