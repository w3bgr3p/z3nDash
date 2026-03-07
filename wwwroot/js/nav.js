/* nav.js */
(function () {

    const HOTKEYS = {
        'ctrl+shift+KeyH': () => location.href = '/',
        'ctrl+shift+KeyT': () => typeof cycleTheme === 'function' && cycleTheme(),
        'ctrl+shift+Digit1': () => location.href = '/?page=pm',
        'ctrl+shift+Digit2': () => location.href = '/?page=logs',
        'ctrl+shift+Digit3': () => location.href = '/?page=http',
        'ctrl+shift+Digit4': () => location.href = '/report',
        'ctrl+shift+Digit5': () => location.href = '/json',
        'ctrl+shift+Digit6': () => location.href = '/?page=config',
        'ctrl+shift+KeyO': () => openOtpModal(),
    };

    document.addEventListener('keydown', function (e) {
        if (!e.code) return;
        const key = [e.ctrlKey?'ctrl':'', e.shiftKey?'shift':'', e.code]
            .filter(Boolean).join('+');
        if (HOTKEYS[key]) { e.preventDefault(); HOTKEYS[key](); }
    });

    function isHomePage() {
        return (location.pathname === '/' || location.pathname === '/home')
            && !location.search.includes('page=');
    }

    function inject() {
        if (document.getElementById('zp-nav-bar')) return;

        const onHome = isHomePage();

        const bar = document.createElement('div');
        bar.id = 'zp-nav-bar';
        Object.assign(bar.style, {
            position: 'fixed', top: '0', left: '0',
            display: 'flex', alignItems: 'center', gap: '4px',
            padding: '3px 8px',
            background: 'rgba(22,27,34,0.95)',
            border: '1px solid #30363d',
            borderTop: 'none', borderLeft: 'none',
            borderBottomRightRadius: '6px',
            zIndex: '10000',
            fontFamily: "'JetBrains Mono', monospace",
            fontSize: '10px',
            opacity: '0',
            transition: 'opacity 0.15s',
            pointerEvents: 'none',
        });

        if (!onHome) {
            const homeBtn = document.createElement('a');
            homeBtn.href = '/';
            homeBtn.title = 'Home (Ctrl+Shift+H)';
            homeBtn.textContent = '⌂';
            Object.assign(homeBtn.style, {
                color: '#8b949e', textDecoration: 'none',
                padding: '2px 5px', borderRadius: '3px', fontSize: '14px',
            });
            homeBtn.onmouseenter = () => homeBtn.style.color = '#58a6ff';
            homeBtn.onmouseleave = () => homeBtn.style.color = '#8b949e';
            bar.appendChild(homeBtn);
        }

        // Config link — visible on all pages
        const isConfigPage = location.search.includes('page=config');
        if (!isConfigPage) {
            const sep = document.createElement('span');
            sep.textContent = '·';
            Object.assign(sep.style, { color: '#30363d', fontSize: '10px', padding: '0 1px' });
            bar.appendChild(sep);

            const cfgBtn = document.createElement('a');
            cfgBtn.href = '/?page=config';
            cfgBtn.title = 'Config (Ctrl+Shift+6)';
            cfgBtn.textContent = '⚙';
            Object.assign(cfgBtn.style, {
                color: '#8b949e', textDecoration: 'none',
                padding: '2px 5px', borderRadius: '3px', fontSize: '13px',
            });
            cfgBtn.onmouseenter = () => cfgBtn.style.color = '#f78166';
            cfgBtn.onmouseleave = () => cfgBtn.style.color = '#8b949e';
            bar.appendChild(cfgBtn);
        }

        if (onHome) {
            const themeBtn = document.createElement('button');
            themeBtn.id = 'zp-theme-btn';
            themeBtn.title = 'Cycle theme (Ctrl+Shift+T)';
            themeBtn.textContent = 'THEME: ' + (localStorage.getItem('zp-theme') || 'dark').toUpperCase();
            Object.assign(themeBtn.style, {
                background: 'none', border: 'none', color: '#8b949e', cursor: 'pointer',
                fontFamily: "'JetBrains Mono', monospace", fontSize: '10px', padding: '2px 5px',
            });
            themeBtn.onclick = () => typeof cycleTheme === 'function' && cycleTheme();
            themeBtn.onmouseenter = () => themeBtn.style.color = '#58a6ff';
            themeBtn.onmouseleave = () => themeBtn.style.color = '#8b949e';
            bar.appendChild(themeBtn);
            document.addEventListener('themechange', e => {
                themeBtn.textContent = 'THEME: ' + e.detail.theme.toUpperCase();
            });
        }

        document.body.appendChild(bar);

        // Show when mouse near top-left corner (50x50px zone)
        let hideTimer;
        document.addEventListener('mousemove', function(e) {
            if (e.clientX < 50 && e.clientY < 50) {
                clearTimeout(hideTimer);
                bar.style.opacity = '1';
                bar.style.pointerEvents = 'auto';
            } else if (!bar.matches(':hover')) {
                clearTimeout(hideTimer);
                hideTimer = setTimeout(() => {
                    bar.style.opacity = '0';
                    bar.style.pointerEvents = 'none';
                }, 400);
            }
        });

        bar.addEventListener('mouseenter', () => {
            clearTimeout(hideTimer);
            bar.style.opacity = '1';
            bar.style.pointerEvents = 'auto';
        });
        bar.addEventListener('mouseleave', () => {
            hideTimer = setTimeout(() => {
                bar.style.opacity = '0';
                bar.style.pointerEvents = 'none';
            }, 400);
        });

        injectOtp();
    }

    // ── OTP Modal ────────────────────────────────────────────────────────────

    function injectOtp() {
        if (document.getElementById('zp-otp-overlay')) return;

        const style = document.createElement('style');
        style.textContent = `
            #zp-otp-overlay {
                display: none; position: fixed; inset: 0;
                background: rgba(0,0,0,0.75);
                z-index: 10001; align-items: center; justify-content: center;
            }
            #zp-otp-overlay.open { display: flex; }
            #zp-otp-box {
                background: #161b22; border: 1px solid #30363d; border-radius: 8px;
                width: 340px; padding: 18px;
                display: flex; flex-direction: column; gap: 12px;
                font-family: 'JetBrains Mono', monospace; font-size: 11px;
                color: #c9d1d9;
            }
            #zp-otp-box .zp-otp-hdr {
                display: flex; align-items: center; gap: 9px;
            }
            #zp-otp-box .zp-otp-hdr span { font-size: 18px; line-height: 1; }
            #zp-otp-box .zp-otp-hdr h3 {
                font-size: 13px; font-weight: 600; color: #e6edf3; flex: 1; margin: 0;
            }
            #zp-otp-box .zp-otp-hdr button {
                background: none; border: none; color: #8b949e; font-size: 16px;
                cursor: pointer; padding: 0 4px; line-height: 1;
            }
            #zp-otp-box .zp-otp-hdr button:hover { color: #e6edf3; }
            #zp-otp-key {
                background: #0d1117; border: 1px solid #30363d; border-radius: 4px;
                color: #c9d1d9; padding: 5px 9px; font-size: 11px; width: 100%;
                box-sizing: border-box; font-family: 'JetBrains Mono', monospace;
                outline: none; letter-spacing: 0.5px;
            }
            #zp-otp-key:focus { border-color: #388bfd; }
            #zp-otp-key::placeholder { color: #484f58; }
            #zp-otp-result {
                display: none; background: #0d1117; border: 1px solid #30363d;
                border-radius: 6px; padding: 12px 14px; text-align: center;
                cursor: pointer; transition: border-color 0.15s;
            }
            #zp-otp-result:hover { border-color: #388bfd; }
            #zp-otp-code {
                font-size: 30px; font-weight: 700; letter-spacing: 8px;
                color: #388bfd; font-family: 'JetBrains Mono', monospace;
            }
            #zp-otp-hint {
                font-size: 9px; color: #484f58; margin-top: 3px;
                text-transform: uppercase; letter-spacing: 0.5px;
            }
            #zp-otp-timer {
                display: none; align-items: center; gap: 7px;
            }
            #zp-otp-bar-wrap {
                flex: 1; height: 3px; background: #21262d;
                border-radius: 2px; overflow: hidden;
            }
            #zp-otp-bar-fill {
                height: 100%; background: #388bfd;
                transition: width 0.9s linear; border-radius: 2px;
            }
            #zp-otp-sec { font-size: 10px; color: #8b949e; width: 26px; text-align: right; }
            #zp-otp-status { font-size: 10px; color: #8b949e; min-height: 14px; }
            #zp-otp-status.err { color: #f85149; }
            #zp-otp-status.ok  { color: #3fb950; }
            .zp-otp-actions { display: flex; gap: 7px; justify-content: flex-end; }
            .zp-btn {
                padding: 4px 12px; border-radius: 5px; border: 1px solid #30363d;
                font-size: 11px; font-family: 'JetBrains Mono', monospace;
                cursor: pointer; background: #21262d; color: #c9d1d9;
            }
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
                    <span>🔐</span>
                    <h3>OTP Generator</h3>
                    <button id="zp-otp-close" title="Close (Esc)">✕</button>
                </div>
                <input id="zp-otp-key" type="text" placeholder="Base32 secret key…"
                       autocomplete="off" spellcheck="false">
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

    // RFC 6238 TOTP via Web Crypto — no external deps
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

        if (!key) {
            status.className = 'err'; status.textContent = 'Enter a Base32 secret key';
            return;
        }

        genBtn.disabled = true;
        status.textContent = 'Generating…';

        try {
            let { code, remaining } = await _totp(key);

            // Mirror C# waitIfTimeLess=5
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

            // Countdown + auto-refresh on expiry
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

    // ─────────────────────────────────────────────────────────────────────────

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', inject);
    } else {
        inject();
    }

})();