/* nav.js */


// ── PageState — глобальный хелпер состояния страниц ───────────────
// Доступен на всех страницах т.к. nav.js подключён везде.
// Ключ в localStorage: 'state_<pathname><search>'
// Пример: 'state_/logs.html', 'state_/?page=http'
window.PageState = {
    _key() { return 'state_' + location.pathname + location.search; },
    load()  { try { return JSON.parse(localStorage.getItem(this._key())) || {}; } catch { return {}; } },
    save(patch) { localStorage.setItem(this._key(), JSON.stringify({ ...this.load(), ...patch })); },
    clear() { localStorage.removeItem(this._key()); }
};
// ──────────────────────────────────────────────────────────────────

(function () {

    const HOTKEYS = {
        'ctrl+shift+KeyH':   () => location.href = '/',
        //'ctrl+shift+KeyT':   () => typeof cycleTheme === 'function' && cycleTheme(),
        'ctrl+shift+Digit1': () => location.href = '/?page=pm',
        'ctrl+shift+Digit2': () => location.href = '/?page=logs',
        'ctrl+shift+Digit3': () => location.href = '/?page=http',
        'ctrl+shift+Digit4': () => location.href = '/report',
        'ctrl+shift+Digit5': () => location.href = '/json',
        'ctrl+shift+Digit6': () => location.href = '/?page=config',
        'ctrl+shift+Digit7': () => location.href = '/scheduler.html',
        'ctrl+shift+KeyT': () => location.href = '/text.html',
        'ctrl+shift+KeyO':   () => openOtpModal(),
        'ctrl+shift+KeyU': () => openUnlockModal(),

    };

    document.addEventListener('keydown', function (e) {
        if (!e.code) return;
        const key = [e.ctrlKey?'ctrl':'', e.shiftKey?'shift':'', e.code]
            .filter(Boolean).join('+');
        if (HOTKEYS[key]) { e.preventDefault(); HOTKEYS[key](); }
    });

    function activePage() {
        const p = location.pathname;
        const q = location.search;
        if (p.includes('scheduler')) return 'scheduler';
        if (q.includes('page=pm'))     return 'pm';
        if (q.includes('page=logs'))   return 'logs';
        if (q.includes('page=http'))   return 'http';
        if (q.includes('page=config')) return 'config';
        if (p.includes('report'))      return 'report';
        if (p.includes('json'))        return 'json';
        return 'home';
    }

    // Inline SVG — no CDN, no font dependency, works offline, consistent cross-OS rendering
    const SVG = {
        home:      `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M3 9.5L12 3l9 6.5V20a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V9.5z"/><path d="M9 21V12h6v9"/></svg>`,
        pm:        `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="8" height="8" rx="1.5"/><rect x="13" y="3" width="8" height="8" rx="1.5"/><rect x="3" y="13" width="8" height="8" rx="1.5"/><rect x="13" y="13" width="8" height="8" rx="1.5"/></svg>`,
        logs:      `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><circle cx="3.5" cy="6" r="1.2" fill="currentColor" stroke="none"/><circle cx="3.5" cy="12" r="1.2" fill="currentColor" stroke="none"/><circle cx="3.5" cy="18" r="1.2" fill="currentColor" stroke="none"/></svg>`,
        http:      `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h14"/><path d="M15 7l5 5-5 5"/><path d="M9 7L4 12l5 5" opacity="0.4"/></svg>`,
        report:    `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><polyline points="14 2 14 8 20 8"/><line x1="8" y1="13" x2="16" y2="13"/><line x1="8" y1="17" x2="13" y2="17"/></svg>`,
        json:      `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3H7a2 2 0 0 0-2 2v4a2 2 0 0 1-2 2 2 2 0 0 1 2 2v4a2 2 0 0 0 2 2h1"/><path d="M16 3h1a2 2 0 0 1 2 2v4a2 2 0 0 0 2 2 2 2 0 0 0-2 2v4a2 2 0 0 1-2 2h-1"/></svg>`,
        config:    `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"/></svg>`,
        scheduler: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><polyline points="12 7 12 12 15.5 15.5"/></svg>`,
        theme:     `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 3v18" stroke-opacity="0.35"/><path d="M12 3a9 9 0 0 0 0 18z" fill="currentColor" opacity="0.2" stroke="none"/></svg>`,
    };

    const ITEMS = [
        { id: 'home',      label: 'Home',      svg: SVG.home,      href: '/',               hotkey: 'H' },
        { id: 'pm',        label: 'PM',         svg: SVG.pm,        href: '/?page=pm',       hotkey: '1' },
        { id: 'logs',      label: 'Logs',       svg: SVG.logs,      href: '/?page=logs',     hotkey: '2' },
        { id: 'http',      label: 'HTTP',       svg: SVG.http,      href: '/?page=http',     hotkey: '3' },
        { id: 'report',    label: 'Report',     svg: SVG.report,    href: '/report',         hotkey: '4' },
        { id: 'json',      label: 'JSON',       svg: SVG.json,      href: '/json',           hotkey: '5' },
        { id: 'config',    label: 'Config',     svg: SVG.config,    href: '/?page=config',   hotkey: '6' },
        { id: 'scheduler', label: 'Scheduler',  svg: SVG.scheduler, href: '/scheduler.html', hotkey: '7' },
    ];

    function inject() {
        if (document.getElementById('zp-dock')) return;

        const current = activePage();

        const style = document.createElement('style');
        style.textContent = `
            #zp-dock-zone {
                position: fixed;
                bottom: 0; left: 0; right: 0;
                height: 6px;
                z-index: 9999;
                pointer-events: auto;
            }
            #zp-dock-wrap {
                position: fixed;
                bottom: 0; left: 0; right: 0;
                display: flex;
                justify-content: center;
                align-items: flex-end;
                pointer-events: none;
                z-index: 9999;
            }
            #zp-dock {
                display: flex;
                align-items: flex-end;
                gap: 8px;
                padding: 10px 16px 12px;
                background: rgba(13,17,23,0.9);
                border: 1px solid rgba(48,54,61,0.9);
                border-bottom: none;
                border-radius: 16px 16px 0 0;
                backdrop-filter: blur(20px);
                -webkit-backdrop-filter: blur(20px);
                box-shadow: 0 -4px 32px rgba(0,0,0,0.5), 0 -1px 0 rgba(255,255,255,0.04) inset;
                pointer-events: auto;
                opacity: 0;
                transform: translateY(110%);
                transition: opacity 0.2s cubic-bezier(.4,0,.2,1), transform 0.2s cubic-bezier(.4,0,.2,1);
            }
            #zp-dock.visible {
                opacity: 1;
                transform: translateY(0);
            }
            .zp-di {
                display: flex;
                flex-direction: column;
                align-items: center;
                gap: 4px;
                cursor: pointer;
                text-decoration: none;
                position: relative;
            }
            .zp-di-icon {
                width: 46px; height: 46px;
                display: flex; align-items: center; justify-content: center;
                border-radius: 12px;
                background: rgba(33,38,45,0.95);
                border: 1px solid rgba(48,54,61,0.7);
                color: #6e7681;
                transition: background 0.15s, border-color 0.15s, color 0.15s,
                            transform 0.15s cubic-bezier(.4,0,.2,1), box-shadow 0.15s;
                user-select: none;
            }
            .zp-di-icon svg {
                width: 19px; height: 19px;
                display: block;
                flex-shrink: 0;
                pointer-events: none;
            }
            .zp-di-label {
                font-size: 9px;
                font-family: 'JetBrains Mono', ui-monospace, monospace;
                color: #484f58;
                letter-spacing: 0.2px;
                text-align: center;
                white-space: nowrap;
                transition: color 0.15s;
                user-select: none;
                line-height: 1;
            }
            .zp-di:hover .zp-di-label { color: #8b949e; }
            .zp-di.active .zp-di-label { color: #58a6ff; }
            .zp-di:hover .zp-di-icon {
                background: rgba(56,68,84,0.95);
                border-color: rgba(88,166,255,0.5);
                color: #e6edf3;
                transform: translateY(-8px) scale(1.18);
                box-shadow: 0 8px 20px rgba(0,0,0,0.45);
            }
            .zp-di.active .zp-di-icon {
                background: rgba(30,50,80,0.95);
                border-color: rgba(56,139,253,0.8);
                color: #58a6ff;
                box-shadow: 0 0 14px rgba(56,139,253,0.3);
            }
            .zp-di.active:hover .zp-di-icon {
                transform: translateY(-8px) scale(1.18);
            }
            .zp-di-dot {
                width: 4px; height: 4px;
                border-radius: 50%;
                background: transparent;
                transition: background 0.15s;
                margin-top: -2px;
            }
            .zp-di.active .zp-di-dot { background: #58a6ff; }
            .zp-di-tip {
                position: absolute;
                bottom: 60px;
                left: 50%; transform: translateX(-50%);
                background: rgba(13,17,23,0.97);
                border: 1px solid #30363d;
                border-radius: 6px;
                padding: 4px 9px;
                font-size: 10px;
                color: #c9d1d9;
                white-space: nowrap;
                font-family: 'JetBrains Mono', ui-monospace, monospace;
                pointer-events: none;
                opacity: 0;
                transform: translateX(-50%) translateY(4px);
                transition: opacity 0.1s, transform 0.1s;
                z-index: 10001;
            }
            .zp-di:hover .zp-di-tip {
                opacity: 1;
                transform: translateX(-50%) translateY(0);
            }
            .zp-dock-sep {
                width: 1px; height: 34px;
                background: rgba(48,54,61,0.8);
                margin: 0 2px;
                align-self: center;
            }
        `;
        document.head.appendChild(style);

        const zone = document.createElement('div');
        zone.id = 'zp-dock-zone';

        const wrap = document.createElement('div');
        wrap.id = 'zp-dock-wrap';

        const dock = document.createElement('div');
        dock.id = 'zp-dock';

        ITEMS.forEach((item) => {
            if (item.id === 'otp') {
                const sep = document.createElement('div');
                sep.className = 'zp-dock-sep';
                dock.appendChild(sep);
            }

            const el = document.createElement(item.href ? 'a' : 'div');
            el.className = 'zp-di' + (item.id === current ? ' active' : '');
            if (item.href) el.href = item.href;
            if (item.onclick) el.addEventListener('click', e => { e.preventDefault(); item.onclick(); });

            el.innerHTML = `
                <div class="zp-di-icon">${item.svg}</div>
                <div class="zp-di-label">${item.label}</div>
                <div class="zp-di-tip">${item.label} <span style="color:#484f58">⌃⇧${item.hotkey}</span></div>
                <div class="zp-di-dot"></div>
            `;
            dock.appendChild(el);
        });

        // theme on home
        //if (current === 'home') {
        const sep = document.createElement('div');
        sep.className = 'zp-dock-sep';
        dock.appendChild(sep);

        const th = document.createElement('div');
        th.className = 'zp-di';
        th.innerHTML = `
                <div class="zp-di-icon">${SVG.theme}</div>
                <div class="zp-di-label">Theme</div>
                <div class="zp-di-tip">Theme <span style="color:#484f58">⌃⇧T</span></div>
                <div class="zp-di-dot"></div>
            `;
        th.onclick = () => typeof cycleTheme === 'function' && cycleTheme();
        dock.appendChild(th);
        //}

        wrap.appendChild(dock);
        document.body.appendChild(zone);
        document.body.appendChild(wrap);

        let hideTimer = null;

        function showDock() {
            clearTimeout(hideTimer);
            dock.classList.add('visible');
        }
        function schedulehide() {
            hideTimer = setTimeout(() => dock.classList.remove('visible'), 400);
        }

        // trigger zone — 6px strip at very bottom
        zone.addEventListener('mouseenter', showDock);
        zone.addEventListener('mouseleave', schedulehide);
        dock.addEventListener('mouseenter', showDock);
        dock.addEventListener('mouseleave', schedulehide);

        // fallback: mouse within 2px of bottom edge
        document.addEventListener('mousemove', function(e) {
            if (window.innerHeight - e.clientY <= 2) showDock();
        });

        injectOtp();
    }

    // ── OTP Modal ─────────────────────────────────────────────────────────────

    function injectOtp() {
        if (document.getElementById('zp-otp-overlay')) return;

        const style = document.createElement('style');
        style.textContent = `
            #zp-otp-overlay { display: none; position: fixed; inset: 0; background: rgba(0,0,0,0.75); z-index: 10001; align-items: center; justify-content: center; }
            #zp-otp-overlay.open { display: flex; }
            #zp-otp-box { background: #161b22; border: 1px solid #30363d; border-radius: 8px; width: 340px; padding: 18px; display: flex; flex-direction: column; gap: 12px; font-family: 'JetBrains Mono', monospace; font-size: 11px; color: #c9d1d9; }
            #zp-otp-box .zp-otp-hdr { display: flex; align-items: center; gap: 9px; }
            #zp-otp-box .zp-otp-hdr span { font-size: 18px; line-height: 1; }
            #zp-otp-box .zp-otp-hdr h3 { font-size: 13px; font-weight: 600; color: #e6edf3; flex: 1; margin: 0; }
            #zp-otp-box .zp-otp-hdr button { background: none; border: none; color: #8b949e; font-size: 16px; cursor: pointer; padding: 0 4px; line-height: 1; }
            #zp-otp-box .zp-otp-hdr button:hover { color: #e6edf3; }
            #zp-otp-key { background: #0d1117; border: 1px solid #30363d; border-radius: 4px; color: #c9d1d9; padding: 5px 9px; font-size: 11px; width: 100%; box-sizing: border-box; font-family: 'JetBrains Mono', monospace; outline: none; letter-spacing: 0.5px; }
            #zp-otp-key:focus { border-color: #388bfd; }
            #zp-otp-key::placeholder { color: #484f58; }
            #zp-otp-result { display: none; background: #0d1117; border: 1px solid #30363d; border-radius: 6px; padding: 12px 14px; text-align: center; cursor: pointer; transition: border-color 0.15s; }
            #zp-otp-result:hover { border-color: #388bfd; }
            #zp-otp-code { font-size: 30px; font-weight: 700; letter-spacing: 8px; color: #388bfd; font-family: 'JetBrains Mono', monospace; }
            #zp-otp-hint { font-size: 9px; color: #484f58; margin-top: 3px; text-transform: uppercase; letter-spacing: 0.5px; }
            #zp-otp-timer { display: none; align-items: center; gap: 7px; }
            #zp-otp-bar-wrap { flex: 1; height: 3px; background: #21262d; border-radius: 2px; overflow: hidden; }
            #zp-otp-bar-fill { height: 100%; background: #388bfd; transition: width 0.9s linear; border-radius: 2px; }
            #zp-otp-sec { font-size: 10px; color: #8b949e; width: 26px; text-align: right; }
            #zp-otp-status { font-size: 10px; color: #8b949e; min-height: 14px; }
            #zp-otp-status.err { color: #f85149; }
            #zp-otp-status.ok  { color: #3fb950; }
            .zp-otp-actions { display: flex; gap: 7px; justify-content: flex-end; }
            .zp-btn { padding: 4px 12px; border-radius: 5px; border: 1px solid #30363d; font-size: 11px; font-family: 'JetBrains Mono', monospace; cursor: pointer; background: #21262d; color: #c9d1d9; }
            .zp-btn:hover { background: #30363d; }
            .zp-btn:disabled { opacity: 0.4; cursor: default; }
            .zp-btn.primary { background: #238636; border-color: #238636; color: #fff; }
            .zp-btn.primary:hover { background: #2ea043; }
        `;
        document.head.appendChild(style);

        const overlay = document.createElement('div');
        overlay.id = 'zp-otp-overlay';
        overlay.innerHTML = `
            <div id="zp-otp-box">
                <div class="zp-otp-hdr">
                    <span>🔐</span><h3>OTP Generator</h3>
                    <button id="zp-otp-close" title="Close (Esc)">✕</button>
                </div>
                <input id="zp-otp-key" type="text" placeholder="Base32 secret key…" autocomplete="off" spellcheck="false">
                <div id="zp-otp-status"></div>
                <div id="zp-otp-result">
                    <div id="zp-otp-code">------</div>
                    <div id="zp-otp-hint">click to copy · auto-copied on generate</div>
                </div>
                <div id="zp-otp-timer">
                    <div id="zp-otp-bar-wrap"><div id="zp-otp-bar-fill" style="width:100%"></div></div>
                    <div id="zp-otp-sec">30s</div>
                </div>
                <div class="zp-otp-actions">
                    <button class="zp-btn" id="zp-otp-cancel">Close</button>
                    <button class="zp-btn primary" id="zp-otp-gen">Generate</button>
                </div>
            </div>
        `;
        document.body.appendChild(overlay);

        document.getElementById('zp-otp-close').onclick  = closeOtpModal;
        document.getElementById('zp-otp-cancel').onclick = closeOtpModal;
        document.getElementById('zp-otp-gen').onclick    = otpGenerate;
        document.getElementById('zp-otp-result').onclick = otpCopyCode;
        overlay.addEventListener('mousedown', e => { if (e.target === overlay) closeOtpModal(); });
    }

    let _otpTimer = null;

    function openOtpModal() {
        const overlay = document.getElementById('zp-otp-overlay');
        if (!overlay) return;
        overlay.classList.add('open');
        const inp = document.getElementById('zp-otp-key');
        setTimeout(() => inp.focus(), 50);
        inp.addEventListener('keydown', _otpKeys);
    }

    function closeOtpModal() {
        document.getElementById('zp-otp-overlay')?.classList.remove('open');
        clearInterval(_otpTimer); _otpTimer = null;
        document.getElementById('zp-otp-key')?.removeEventListener('keydown', _otpKeys);
    }

    function _otpKeys(e) {
        if (e.key === 'Enter')  otpGenerate();
        if (e.key === 'Escape') closeOtpModal();
    }

    function otpCopyCode() {
        const code = document.getElementById('zp-otp-code').textContent;
        if (code === '------') return;
        navigator.clipboard?.writeText(code);
        const s = document.getElementById('zp-otp-status');
        s.className = 'ok'; s.textContent = '✓ Copied';
        setTimeout(() => { s.textContent = ''; s.className = ''; }, 1500);
    }

    function _b32ToBytes(s) {
        const alpha = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
        s = s.toUpperCase().replace(/\s|=+$/g, '');
        let bits = 0, val = 0;
        const out = [];
        for (const c of s) {
            const i = alpha.indexOf(c);
            if (i < 0) throw new Error('Invalid Base32 char: ' + c);
            val = (val << 5) | i; bits += 5;
            if (bits >= 8) { bits -= 8; out.push((val >> bits) & 0xff); }
        }
        return new Uint8Array(out);
    }

    async function _totp(secret) {
        const key  = _b32ToBytes(secret);
        const now  = Math.floor(Date.now() / 1000);
        const buf  = new ArrayBuffer(8);
        new DataView(buf).setUint32(4, Math.floor(now / 30), false);
        const ck  = await crypto.subtle.importKey('raw', key, { name:'HMAC', hash:'SHA-1' }, false, ['sign']);
        const sig = new Uint8Array(await crypto.subtle.sign('HMAC', ck, buf));
        const off = sig[19] & 0xf;
        const num = (((sig[off]&0x7f)<<24)|(sig[off+1]<<16)|(sig[off+2]<<8)|sig[off+3]) % 1000000;
        return { code: String(num).padStart(6, '0'), remaining: 30 - (now % 30) };
    }

    async function otpGenerate() {
        const key    = document.getElementById('zp-otp-key').value.trim();
        const status = document.getElementById('zp-otp-status');
        const result = document.getElementById('zp-otp-result');
        const codeEl = document.getElementById('zp-otp-code');
        const timer  = document.getElementById('zp-otp-timer');
        const genBtn = document.getElementById('zp-otp-gen');

        status.className = ''; status.textContent = '';
        if (!key) { status.className = 'err'; status.textContent = 'Enter a Base32 secret key'; return; }

        genBtn.disabled = true;
        status.textContent = 'Generating…';

        try {
            let { code, remaining } = await _totp(key);
            if (remaining <= 5) {
                status.textContent = `Waiting ${remaining}s for fresh code…`;
                await new Promise(r => setTimeout(r, remaining * 1000 + 500));
                ({ code, remaining } = await _totp(key));
            }
            codeEl.textContent = code;
            result.style.display = 'block';
            timer.style.display  = 'flex';
            navigator.clipboard?.writeText(code);
            status.className = 'ok'; status.textContent = '✓ Copied to clipboard';

            clearInterval(_otpTimer);
            const tick = () => {
                const sec = 30 - (Math.floor(Date.now() / 1000) % 30);
                document.getElementById('zp-otp-sec').textContent = sec + 's';
                document.getElementById('zp-otp-bar-fill').style.width = (sec / 30 * 100) + '%';
                if (sec === 30) otpGenerate();
            };
            tick();
            _otpTimer = setInterval(tick, 1000);
        } catch (e) {
            status.className = 'err'; status.textContent = 'Error: ' + e.message;
        } finally {
            genBtn.disabled = false;
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', inject);
    } else {
        inject();
    }



})();