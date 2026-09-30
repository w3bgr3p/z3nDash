const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function loadTasker(elements = {}) {
    const context = {
        console,
        document: {
            addEventListener() {},
            getElementById(id) { return elements[id] || null; },
            querySelectorAll() { return []; },
        },
        window: { addEventListener() {} },
        fetch: async () => ({ ok: true, json: async () => ({}) }),
        setInterval() { return 1; },
        clearInterval() {},
        setTimeout,
        clearTimeout,
        URLSearchParams,
    };
    vm.createContext(context);
    const sourcePath = path.join(__dirname, '..', 'wwwroot', 'js', 'tasker.js');
    vm.runInContext(fs.readFileSync(sourcePath, 'utf8'), context, { filename: sourcePath });
    return context;
}

test('schedule form renders as one compact grid with concise field labels', () => {
    const context = loadTasker();

    const html = context.zpFormHtml({});

    assert.doesNotMatch(html, /class="form-section"/);
    assert.doesNotMatch(html, /Reset successes|z_reset_success/);
    assert.match(html, />Repeat mode</);
    assert.match(html, />Finish</);
});

test('schedule status renders as an Active and Inactive switch', () => {
    const context = loadTasker();

    const activeHtml = context.scheduleActionsHtml({ enabled: 'true' }, true);
    const inactiveHtml = context.scheduleActionsHtml({ enabled: 'false' }, true);

    assert.match(activeHtml, /<div class="form-label">Is Active<\/div>/);
    assert.match(activeHtml, /type="checkbox"[^>]*id="f_enabled"[^>]*checked/);
    assert.doesNotMatch(inactiveHtml, /id="f_enabled"[^>]*checked/);
    assert.match(activeHtml, /<label class="switch".*<span class="t2-label inactive">Inactive<.*<span class="t2-label active">Active</s);
    assert.doesNotMatch(activeHtml, /<select[^>]*id="f_enabled"/);
});

test('schedule status switch is serialized as true or false', () => {
    const elements = {
        f_schedule_mode: { value: 'zp' },
        f_enabled: { checked: false, value: 'on' },
    };
    const context = loadTasker(elements);

    assert.equal(context.collectScheduleFields({}).enabled, 'false');
    elements.f_enabled.checked = true;
    assert.equal(context.collectScheduleFields({}).enabled, 'true');
});

test('schedule status uses danger when inactive and success when active', () => {
    const cssPath = path.join(__dirname, '..', 'wwwroot', 'css', 'tasker.css');
    const css = fs.readFileSync(cssPath, 'utf8');
    const inactiveRule = css.match(/\.switch \.slider\s*\{([^}]*)\}/);
    const activeRule = css.match(/\.switch input:checked \+ \.slider\s*\{([^}]*)\}/);

    assert.ok(inactiveRule, 'inactive switch rule is missing');
    assert.ok(activeRule, 'active switch rule is missing');
    assert.match(inactiveRule[1], /background:\s*var\(--red-bg\)/);
    assert.match(inactiveRule[1], /border:\s*1px solid var\(--red-bg\)/);
    assert.match(activeRule[1], /background:\s*var\(--green-bg\)/);
    assert.match(activeRule[1], /border-color:\s*var\(--green-bg\)/);
});
