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
    { id: 'dark',      label: '⬛  Dark' },
    { id: 'light',     label: '⬜  Light' },
    { id: 'hyper',     label: '🟩  Hyper' },
    { id: 'graphite',  label: '⬛  Graphite' },
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

    document.querySelectorAll('.theme-select').forEach(sel => {
        if (sel.value !== name) sel.value = name;
    });

    const btn = document.getElementById('themeBtn');
    if (btn) btn.textContent = 'THEME: ' + name.toUpperCase();

    document.dispatchEvent(new CustomEvent('themechange', { detail: { theme: name } }));

    // Persist to backend (fire-and-forget)
    fetch('/config/ui', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ theme: name })
    }).catch(() => {});
}


function cycleTheme() {
    const cur  = getTheme();
    const next = THEME_IDS[(THEME_IDS.indexOf(cur) + 1) % THEME_IDS.length];
    setTheme(next);
}

function initTheme() {
    // Применяем сразу из localStorage — без мигания
    setTheme(getTheme());

    // Затем синхронизируем с бэкендом
    fetch('/config/ui')
        .then(r => r.json())
        .then(data => {
            if (data.theme)        setTheme(data.theme);
            if (data.dockPosition) {
                localStorage.setItem('zp-dock-pos', data.dockPosition);
                setDockPosition(data.dockPosition);
            }
        })
        .catch(() => {});

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

    THEMES.forEach(theme => {
        const option = document.createElement('option');
        option.value = theme.id;
        option.textContent = theme.label;
        sel.appendChild(option);
    });

    sel.value = getTheme();
    sel.addEventListener('change', () => setTheme(sel.value));

    sel.style.cssText = [
        'background: var(--bg2)',
        'color: var(--text)',
        'border: 1px solid var(--border)',
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
