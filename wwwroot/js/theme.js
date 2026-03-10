/* ═══════════════════════════════════════════════════════
   theme.js — общий переключатель тем для всех страниц
   Подключать в <head> ПЕРВЫМ, до других скриптов

   Использование:
     initTheme()        — вызывается автоматически при загрузке
     cycleTheme()       — следующая тема по кругу
     setTheme('light')  — установить конкретную тему
     createThemeSelect(container) — вставить <select> в элемент

   Событие:
     document.addEventListener('themechange', e => e.detail.theme)
   ═══════════════════════════════════════════════════════ */

const THEMES = [
    // ── Base ──
    { id: 'dark',      label: '⬛  Dark' },
    { id: 'light',     label: '⬜  Light' },
    { id: 'hyper',     label: '🟩  Hyper' },
    // ── Editor ──
    { id: 'tokyo',     label: '🌆  Tokyo Night' },
    { id: 'gruvbox',   label: '🟫  Gruvbox' },
    { id: 'nord',      label: '🧊  Nord' },
    { id: 'amber',     label: '🟡  Amber CRT' },
    { id: 'amoled',    label: '🖤  AMOLED' },
    // ── Blockchain ──
    { id: 'ethereum',  label: '🔷  Ethereum' },
    { id: 'optimism',  label: '🔴  Optimism' },
    { id: 'arbitrum',  label: '🔵  Arbitrum' },
    { id: 'polygon',   label: '🟣  Polygon' },
    { id: 'base',      label: '🫐  Base' },
    { id: 'solana',    label: '🟢  Solana' },
    { id: 'avalanche', label: '🔺  Avalanche' },
    { id: 'bnb',       label: '🟡  BNB Chain' },
    { id: 'sui',       label: '🩵  Sui' },
    { id: 'blast',     label: '⚡  Blast' },
];

const THEME_IDS = THEMES.map(t => t.id);
const THEME_KEY = 'zp-theme';

function getTheme() {
    return localStorage.getItem(THEME_KEY) || 'dark';
}

function setTheme(name) {
    if (!THEME_IDS.includes(name)) name = 'dark';
    localStorage.setItem(THEME_KEY, name);
    document.documentElement.setAttribute('data-theme', name);

    // Обновить все select-ы на странице
    document.querySelectorAll('.theme-select').forEach(sel => {
        if (sel.value !== name) sel.value = name;
    });

    // Обновить кнопку если есть (legacy)
    const btn = document.getElementById('themeBtn');
    if (btn) btn.textContent = 'THEME: ' + name.toUpperCase();

    document.dispatchEvent(new CustomEvent('themechange', { detail: { theme: name } }));
}

function cycleTheme() {
    const cur = getTheme();
    const next = THEME_IDS[(THEME_IDS.indexOf(cur) + 1) % THEME_IDS.length];
    setTheme(next);
}

function initTheme() {
    setTheme(getTheme());
}

/**
 * Создаёт <select> для переключения темы и вставляет его в container.
 * Если container не передан — ищет #themeBtn и заменяет его.
 *
 * @param {HTMLElement|string|null} container — элемент или CSS-селектор
 * @returns {HTMLSelectElement}
 */
function createThemeSelect(container) {
    const sel = document.createElement('select');
    sel.className = 'theme-select';

    // Группы
    const groups = [
        { label: 'Base',       ids: ['dark', 'light', 'hyper'] },
        { label: 'Editor',     ids: ['tokyo', 'gruvbox', 'nord', 'amber', 'amoled'] },
        { label: 'Blockchain', ids: ['ethereum', 'optimism', 'arbitrum', 'polygon', 'base', 'solana', 'avalanche', 'bnb', 'sui', 'blast'] },
    ];

    groups.forEach(group => {
        const og = document.createElement('optgroup');
        og.label = group.label;
        group.ids.forEach(id => {
            const t = THEMES.find(t => t.id === id);
            if (!t) return;
            const opt = document.createElement('option');
            opt.value = t.id;
            opt.textContent = t.label;
            og.appendChild(opt);
        });
        sel.appendChild(og);
    });

    sel.value = getTheme();
    sel.addEventListener('change', () => setTheme(sel.value));

    // Стили — инлайн чтобы работало до загрузки CSS
    sel.style.cssText = [
        'background: var(--bg2, #21262d)',
        'color: var(--text, #c9d1d9)',
        'border: 1px solid var(--border, #30363d)',
        'border-radius: var(--radius, 6px)',
        'padding: 2px 6px',
        'font: inherit',
        'font-size: 11px',
        'cursor: pointer',
        'outline: none',
    ].join(';');

    if (container) {
        const el = typeof container === 'string'
            ? document.querySelector(container)
            : container;
        if (el) el.appendChild(sel);
    } else {
        // Заменяем legacy #themeBtn если есть
        const btn = document.getElementById('themeBtn');
        if (btn) btn.replaceWith(sel);
    }

    return sel;
}

// Применяем сразу — до рендера страницы, без мигания
initTheme();

// После загрузки DOM — автоматически заменяем #themeBtn если он есть
document.addEventListener('DOMContentLoaded', () => {
    const btn = document.getElementById('themeBtn');
    if (btn) createThemeSelect();
});