// Shared ZP7 history viewer. Reads the same node API as the task log panel.
window.ZpAllLogs = (() => {
    'use strict';
    const columns = [['ts', 'Time', 175], ['level', 'Level', 70], ['machine', 'Machine', 110],
        ['project', 'Project', 135], ['thread', 'Thread', 65], ['module', 'Module', 110], ['text', 'Message', 0]];
    const stateKey = 'zp7_all_logs_state';
    let dialog, rows = [], visible = [], selected = null, timer = null, request = null;
    let sortKey = 'ts', sortDirection = -1, auto = true;
    const el = id => dialog.querySelector('#zal-' + id);
    const value = id => el(id).value;
    const escape = text => String(text ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;'}[c]));
    const key = row => JSON.stringify(row);
    const fields = ['machine', 'project', 'level', 'thread', 'module', 'search', 'limit', 'kind', 'process'];

    function create() {
        dialog = document.createElement('dialog');
        dialog.id = 'zpAllLogs';
        dialog.setAttribute('aria-labelledby', 'zal-title');
        dialog.innerHTML = `
            <header><h2 id="zal-title">allLogs</h2><span id="zal-count"></span><button id="zal-close" aria-label="Close all logs">✕</button></header>
            <div class="zal-controls">
                <select id="zal-machine" aria-label="Machine"><option value="">All machines</option></select>
                <select id="zal-project" aria-label="Project"><option value="">All projects</option></select>
                <select id="zal-level" aria-label="Level"><option value="">All levels</option></select>
                <select id="zal-thread" aria-label="Thread"><option value="">All threads</option></select>
                <select id="zal-module" aria-label="Module"><option value="">All modules</option></select>
                <input id="zal-search" type="search" placeholder="Search text..." aria-label="Search log text">
                <select id="zal-process" aria-label="Process"><option>ZennoPoster</option><option>ProjectMaker</option></select>
                <select id="zal-kind" aria-label="Log type"><option value="execution">Execution</option><option value="errors">Errors</option><option value="critical">Critical</option></select>
                <select id="zal-limit" aria-label="Entries per node"><option value="100">100 / node</option><option value="500">500 / node</option><option value="2000" selected>2000 / node</option></select>
                <button id="zal-refresh">Refresh</button><button id="zal-auto">Auto: ON</button><button id="zal-reset">Reset filters</button>
            </div>
            <div class="zal-note">Recent history from ZP7 nodes: up to 2000 entries per node, within the last 512 KB of each log file. Click a column to sort.</div>
            <div id="zal-status" class="zal-status" role="status"></div>
            <div class="zal-content"><div class="zal-table-wrap"><table><colgroup>${columns.map(([, , width]) => `<col${width ? ` style="width:${width}px"` : ''}>`).join('')}</colgroup>
                <thead><tr>${columns.map(([field, label]) => `<th><button data-sort="${field}">${label}</button></th>`).join('')}</tr></thead><tbody id="zal-rows"></tbody></table></div>
                <aside class="zal-details" id="zal-details" hidden aria-label="Log details"></aside></div>`;
        document.body.append(dialog);
    }

    function options(id, items, label) {
        const select = el(id);
        // Do not disturb an open native picker during background refreshes.
        if (document.activeElement === select) return;
        const previous = value(id);
        const values = [...new Set([...items, previous].filter(Boolean))].sort((a, b) => a.localeCompare(b, undefined, { numeric: true }));
        if (select.options.length === values.length + 1 && values.every((item, index) => select.options[index + 1].value === item)) return;
        select.replaceChildren(new Option(label, ''), ...values.map(item => new Option(item, item)));
        select.value = previous;
    }

    function save() {
        try { sessionStorage.setItem(stateKey, JSON.stringify({ ...Object.fromEntries(fields.map(id => [id, value(id)])), sortKey, sortDirection, auto })); } catch {}
    }

    function restore() {
        try {
            const state = JSON.parse(sessionStorage.getItem(stateKey) || '{}');
            for (const id of fields) {
                if (typeof state[id] !== 'string') continue;
                if (['machine', 'project', 'level', 'thread', 'module'].includes(id) && state[id]) el(id).add(new Option(state[id], state[id]));
                el(id).value = state[id];
            }
            if (columns.some(([id]) => id === state.sortKey)) sortKey = state.sortKey;
            if (state.sortDirection === 1) sortDirection = 1;
            auto = state.auto !== false;
        } catch {}
        if (!value('limit')) el('limit').value = '2000';
        if (!value('process')) el('process').value = 'ZennoPoster';
        if (!value('kind')) el('kind').value = 'execution';
    }

    function render() {
        const search = value('search').toLowerCase();
        visible = rows.filter(row => ['level', 'thread', 'module'].every(id => !value(id) || row[id] === value(id))
            && (!search || [row.text, row.logger, row.project, row.module].some(text => text.toLowerCase().includes(search))));
        visible.sort((a, b) => sortDirection * a[sortKey].localeCompare(b[sortKey], undefined, { numeric: true }));
        const errors = visible.filter(row => ['ERROR', 'FATAL'].includes(row.level)).length;
        const warnings = visible.filter(row => ['WARN', 'WARNING'].includes(row.level)).length;
        el('count').textContent = `${visible.length} / ${rows.length} · Errors: ${errors} · Warn: ${warnings}`;
        el('rows').innerHTML = visible.length ? visible.map((row, index) => `<tr data-index="${index}" tabindex="0"${key(row) === selected ? ' class="selected"' : ''}>${columns.map(([field]) => `<td${field === 'level' ? ` data-level="${escape(row.level)}"` : ''} title="${escape(row[field])}">${escape(row[field])}</td>`).join('')}</tr>`).join('')
            : '<tr><td colspan="7">No logs matching the filters</td></tr>';
        dialog.querySelectorAll('[data-sort]').forEach(button => {
            const active = button.dataset.sort === sortKey;
            button.parentElement.setAttribute('aria-sort', active ? (sortDirection === 1 ? 'ascending' : 'descending') : 'none');
            button.textContent = columns.find(([field]) => field === button.dataset.sort)[1] + (active ? (sortDirection === 1 ? ' ↑' : ' ↓') : '');
        });
        if (selected && !visible.some(row => key(row) === selected)) { selected = null; el('details').hidden = true; }
        save();
    }

    async function getJson(url, signal) {
        const response = await fetch(url, { signal, cache: 'no-store' });
        const data = await response.json();
        if (!response.ok || data.error) throw new Error(data.error || `HTTP ${response.status}`);
        return data;
    }

    async function refresh() {
        if (!dialog.open) return;
        request?.abort();
        const controller = new AbortController();
        request = controller;
        el('status').textContent = 'Loading...';
        el('status').classList.remove('error');
        el('project').disabled = value('kind') !== 'execution';
        save();
        try {
            const nodes = await getJson('/zp/nodes?probe=false', controller.signal);
            if (!Array.isArray(nodes)) throw new Error('Invalid node list');
            if (controller.signal.aborted) return;
            options('machine', nodes.map(node => node.machine), 'All machines');
            const targets = nodes.filter(node => !value('machine') || node.machine === value('machine'));
            const query = new URLSearchParams({ kind: value('kind'), process: value('process'), n: value('limit') });
            if (value('kind') === 'execution' && value('project').trim()) query.set('project', value('project').trim());
            const results = await Promise.allSettled(targets.map(async node => {
                const params = new URLSearchParams(query);
                params.set('machine', node.machine);
                const data = await getJson('/zp/log?' + params, controller.signal);
                if (!Array.isArray(data.entries)) throw new Error('Invalid log response');
                return data.entries.map(entry => Object.fromEntries(['ts', 'level', 'project', 'thread', 'module', 'text', 'logger', 'machine'].map(field => {
                    const text = String(field === 'machine' ? node.machine : entry[field] ?? '');
                    return [field, field === 'text' ? text : text.trim()];
                })));
            }));
            if (controller.signal.aborted) return;
            rows = results.flatMap(result => result.status === 'fulfilled' ? result.value : []);
            for (const id of ['level', 'thread', 'module']) options(id, rows.map(row => row[id]), `All ${id === 'level' ? 'levels' : id === 'thread' ? 'threads' : 'modules'}`);
            const projects = [...rows.map(row => row.project), ...(typeof allTasks === 'undefined' ? [] : allTasks.map(task => task.Name)),
                ...Array.from(el('project').options, option => option.value)];
            options('project', projects, 'All projects');
            const failures = results.flatMap((result, index) => result.status === 'rejected' ? [`${targets[index].machine}: ${result.reason.message}`] : []);
            el('status').textContent = targets.length ? `${results.length - failures.length}/${targets.length} nodes · ${new Date().toLocaleTimeString()}${failures.length ? '\n' + failures.join('\n') : ''}` : 'No matching ZP7 nodes registered';
            el('status').classList.toggle('error', failures.length > 0);
            render();
        } catch (error) {
            if (controller.signal.aborted) return;
            rows = []; render();
            el('status').textContent = error.message;
            el('status').classList.add('error');
        } finally {
            if (request === controller) request = null;
        }
    }

    function showDetails(index) {
        const row = visible[index];
        if (!row) return;
        selected = key(row);
        render();
        el('details').hidden = false;
        el('details').innerHTML = `<button id="zal-hide-details">Close details</button> <button id="zal-copy">Copy message</button><dl>${[...columns.filter(([field]) => field !== 'text'), ['logger', 'Logger']].map(([field, label]) => `<dt>${label}</dt><dd>${escape(row[field])}</dd>`).join('')}</dl><pre>${escape(row.text)}</pre>`;
        el('hide-details').onclick = () => { selected = null; el('details').hidden = true; render(); };
        el('copy').onclick = async () => {
            try { await navigator.clipboard.writeText(row.text); el('copy').textContent = 'Copied'; }
            catch { el('copy').textContent = 'Copy failed — select the text'; }
        };
    }

    function startPolling() {
        clearInterval(timer);
        timer = null;
        el('auto').textContent = `Auto: ${auto ? 'ON' : 'OFF'}`;
        if (auto && dialog.open) timer = setInterval(() => { if (!request) refresh(); }, 3000);
    }

    function bind() {
        el('close').onclick = () => dialog.close();
        dialog.addEventListener('click', event => { if (event.target === dialog) { const rect = dialog.getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close(); } });
        dialog.addEventListener('close', () => { clearInterval(timer); request?.abort(); request = null; save(); });
        el('refresh').onclick = refresh;
        el('auto').onclick = () => { auto = !auto; startPolling(); save(); };
        for (const id of ['machine', 'project', 'process', 'kind', 'limit']) el(id).onchange = refresh;
        for (const id of ['level', 'thread', 'module']) el(id).onchange = render;
        el('search').oninput = render;
        el('reset').onclick = () => {
            for (const id of ['machine', 'project', 'level', 'thread', 'module', 'search']) el(id).value = '';
            sortKey = 'ts'; sortDirection = -1; refresh();
        };
        dialog.querySelectorAll('[data-sort]').forEach(button => button.onclick = () => {
            sortDirection = sortKey === button.dataset.sort ? -sortDirection : 1;
            sortKey = button.dataset.sort; render();
        });
        el('rows').onclick = event => { const row = event.target.closest('[data-index]'); if (row) showDetails(Number(row.dataset.index)); };
        el('rows').onkeydown = event => {
            if (event.key !== 'Enter' && event.key !== ' ') return;
            const row = event.target.closest('[data-index]');
            if (row) { event.preventDefault(); showDetails(Number(row.dataset.index)); }
        };
    }

    function open() {
        if (!dialog) { create(); restore(); bind(); }
        if (dialog.open) return;
        dialog.showModal();
        refresh(); startPolling();
    }

    window.addEventListener('DOMContentLoaded', () => { if (location.hash === '#allLogs') open(); });
    window.addEventListener('hashchange', () => { if (location.hash === '#allLogs') open(); });
    return { open };
})();
