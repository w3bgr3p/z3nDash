/* ═══════════════════════════════════════════════════════
   theme.js — общий переключатель тем для всех страниц
   Подключать в <head> ПЕРВЫМ, до других скриптов

   Использование:
     initTheme()     — вызывается автоматически при загрузке
     cycleTheme()    — следующая тема по кругу
     setTheme('light') — установить конкретную тему

   Событие:
     document.addEventListener('themechange', e => e.detail.theme)
   ═══════════════════════════════════════════════════════ */

const THEMES = ['dark', 'light', 'hyper', 'tokyo', 'gruvbox', 'nord', 'amber', 'amoled'];
const THEME_KEY = 'zp-theme';

function getTheme() {
    return localStorage.getItem(THEME_KEY) || 'dark';
}

function setTheme(name) {
    if (!THEMES.includes(name)) name = 'dark';
    localStorage.setItem(THEME_KEY, name);
    document.documentElement.setAttribute('data-theme', name);
    // Обновить кнопку если есть на странице
    const btn = document.getElementById('themeBtn');
    if (btn) btn.textContent = 'THEME: ' + name.toUpperCase();
    document.dispatchEvent(new CustomEvent('themechange', { detail: { theme: name } }));
}

function cycleTheme() {
    const cur = getTheme();
    const next = THEMES[(THEMES.indexOf(cur) + 1) % THEMES.length];
    setTheme(next);
}

function initTheme() {
    setTheme(getTheme());
}

// Применяем сразу — до рендера страницы, без мигания
initTheme();