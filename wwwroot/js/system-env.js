/* system-env.js — раздел Environment на странице System.
   Не часть снимка: свой пункт в левой навигации и своя панель (#envPanel),
   данные берёт с /config/environment. */

let envActive = false;
let envLoaded = false;

function envNavItem() {
    const el = document.createElement('div');
    el.className = 'nav-item env-nav' + (envActive ? ' active' : '');
    el.textContent = 'Environment';
    el.onclick = activateEnv;
    return el;
}

// Пункт Environment стоит первым, под отметкой времени снимка.
function appendEnvNav(nav) {
    const ts = nav.querySelector('.nav-ts');
    const sep = document.createElement('div');
    sep.className = 'nav-sep';
    const item = envNavItem();
    if (ts) { ts.after(item); item.after(sep); }
    else    { nav.prepend(sep); nav.prepend(item); }
}

function activateEnv() {
    envActive = true;
    try { PageState.save({ env: true }); } catch {}
    document.querySelectorAll('.nav-item').forEach(el => el.classList.toggle('active', el.classList.contains('env-nav')));
    document.querySelectorAll('.section-panel').forEach(el => el.classList.toggle('active', el.id === 'envPanel'));
    const empty = document.getElementById('emptyState');
    if (empty) empty.style.display = 'none';
    // Проверка идёт секунды (запускает python, node, dotnet...), поэтому сама
    // срабатывает только при первом входе, дальше — по кнопке.
    if (!envLoaded) loadEnvironment();
}

function deactivateEnv() {
    envActive = false;
    try { PageState.save({ env: false }); } catch {}
    document.querySelector('.env-nav')?.classList.remove('active');
    document.getElementById('envPanel')?.classList.remove('active');
}

function envStatus(msg, type) {
    const el = document.getElementById('envStatus');
    el.textContent = msg;
    el.className = 'env-status ' + (type || '');
}

async function loadEnvironment() {
    const btn   = document.getElementById('envCheckBtn');
    const list  = document.getElementById('envList');
    const stamp = document.getElementById('envStamp');
    btn.disabled = true;
    stamp.textContent = 'checking…';
    try {
        const r = await fetch('/config/environment');
        const text = await r.text();
        let d;
        try { d = JSON.parse(text); } catch (e) { throw new Error('HTTP ' + r.status + ': ' + text.slice(0, 300)); }
        if (!r.ok) throw new Error(d.error || ('HTTP ' + r.status));
        renderEnvironment(list, d.items || []);
        stamp.textContent = 'checked ' + new Date(d.checkedAt).toLocaleString();
        envLoaded = true;
    } catch (e) {
        stamp.textContent = 'check failed: ' + e.message;
    } finally {
        btn.disabled = false;
    }
}

// Установка идёт в отдельном окне консоли: там видны ход, запрос UAC и итог.
// Дашборд её не ждёт — по окончании жмут Check, PATH перечитывается сам.
async function installEnvironment(key, btn) {
    btn.disabled = true;
    try {
        const r = await fetch('/config/environment/install', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ key })
        });
        const text = await r.text();
        let d;
        try { d = JSON.parse(text); } catch (e) { d = { ok: false, error: 'HTTP ' + r.status + ': ' + text.slice(0, 300) }; }
        if (!d.ok) { envStatus('Install failed: ' + (d.error || 'unknown'), 'err'); return; }
        envStatus(d.kind === 'url'
            ? 'Opened: ' + d.command
            : d.title + ': installer started in a console window. Press Check when it finishes.', 'ok');
    } catch (e) {
        envStatus('Install failed: ' + e.message, 'err');
    } finally {
        btn.disabled = false;
    }
}

function renderEnvironment(list, items) {
    const pillClass = { found: 'ok', missing: 'err', info: 'warn' };
    list.textContent = '';
    let group = null;
    items.forEach(it => {
        if (it.group !== group) {
            group = it.group;
            const h = document.createElement('div');
            h.className = 'env-group';
            h.textContent = group;
            list.appendChild(h);
        }
        const row = document.createElement('div');
        row.className = 'env-row';

        const name = document.createElement('div');
        name.className = 'env-name';
        name.textContent = it.name;

        const state = document.createElement('div');
        const pill = document.createElement('span');
        pill.className = 'pill ' + (pillClass[it.state] || 'warn');
        pill.textContent = it.version || it.state;
        state.appendChild(pill);
        if (it.state === 'missing' && it.install) {
            const b = document.createElement('button');
            b.type = 'button';
            b.className = 'btn env-install';
            b.textContent = it.install === 'sqlite-odbc' ? 'Open page' : 'Install';
            b.addEventListener('click', () => installEnvironment(it.install, b));
            state.appendChild(document.createElement('br'));
            state.appendChild(b);
        }

        const info = document.createElement('div');
        if (it.path) {
            const p = document.createElement('div');
            p.className = 'env-path';
            p.textContent = it.path;
            info.appendChild(p);
        }
        if (it.detail) {
            const dt = document.createElement('div');
            dt.className = 'env-detail';
            dt.textContent = it.detail;
            info.appendChild(dt);
        }
        row.append(name, state, info);
        list.appendChild(row);
    });
}
