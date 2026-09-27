const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

function el() {
    const classes = new Set();
    return {
        innerHTML: '', textContent: '', value: '', style: {},
        classList: {
            add(c) { classes.add(c); },
            remove(c) { classes.delete(c); },
            toggle(c, on) { if (on) classes.add(c); else classes.delete(c); },
            contains(c) { return classes.has(c); },
        },
        querySelectorAll() { return []; },
        appendChild() {},
        focus() {},
    };
}

const MODAL_IDS = ['payloadOverlay', 'payloadTitle', 'payloadImport', 'payloadTabValues', 'payloadTabSchema',
    'valuesBody', 'schemaBody', 'payloadPreview', 'importPayloadInput', 'detailActions'];

function loadTasker({ inputs = [] } = {}) {
    const elements = Object.fromEntries(MODAL_IDS.map((id) => [id, el()]));
    const requests = [];
    const context = {
        console,
        document: {
            addEventListener() {},
            getElementById(id) { return elements[id] || null; },
            querySelectorAll(selector) { return selector === '#valuesBody [data-vkey]' ? inputs : []; },
        },
        window: { addEventListener() {} },
        navigator: { clipboard: { writeText: async () => {} } },
        fetch: async (url, opts) => {
            requests.push({ url, opts });
            const body = url.startsWith('/tasker/payload?')
                ? { schema: '[{"key":"url","type":"text"}]', values: '{"url":"old"}' }
                : { ok: true, command: 'python a.py --url old', skipped: [] };
            return { ok: true, json: async () => body };
        },
        setInterval() { return 1; },
        clearInterval() {},
        setTimeout,
        clearTimeout,
        URLSearchParams,
    };
    vm.createContext(context);
    const sourcePath = path.join(__dirname, '..', 'wwwroot', 'js', 'tasker.js');
    vm.runInContext(fs.readFileSync(sourcePath, 'utf8'), context, { filename: sourcePath });
    return { context, elements, requests };
}

test('task actions show one Payload button instead of five', () => {
    const { context, elements } = loadTasker();

    context.renderDetailActions({ id: 't1', name: 'n', executor: 'exe', script_path: 'a.exe' });

    const html = elements.detailActions.innerHTML;
    assert.match(html, /openPayloadModal\('t1'/);
    assert.doesNotMatch(html, /openSchemaModal|openValuesModal|openImportPayload|exportPayload\(|clearPayload\(/);
});

test('switching to Schema keeps values typed on the Values tab', async () => {
    const inputs = [{ dataset: { vkey: 'url' }, type: 'text', value: 'typed' }];
    const { context, elements } = loadTasker({ inputs });

    await context.openPayloadModal('t1', 'n');
    context.pmSwitch('schema');

    assert.equal(context.pmValues.url, 'typed');
    assert.equal(elements.valuesBody.style.display, 'none');
    assert.equal(elements.schemaBody.style.display, '');
    assert.ok(elements.payloadTabSchema.classList.contains('active'));
});

test('Save sends schema and values in one request and closes the modal', async () => {
    const inputs = [{ dataset: { vkey: 'url' }, type: 'text', value: 'typed' }];
    const { context, elements, requests } = loadTasker({ inputs });

    await context.openPayloadModal('t1', 'n');
    await context.savePayload();

    const save = requests.find((r) => r.url === '/tasker/payload' && r.opts && r.opts.method === 'POST');
    assert.ok(save, 'no POST /tasker/payload');
    const body = JSON.parse(save.opts.body);
    assert.equal(body.id, 't1');
    assert.equal(JSON.parse(body.schema)[0].key, 'url');
    assert.equal(JSON.parse(body.values).url, 'typed');
    assert.ok(!elements.payloadOverlay.classList.contains('open'));
});

test('Import fills the form and does not save by itself', async () => {
    const { context, elements, requests } = loadTasker();

    await context.openPayloadModal('t1', 'n');
    elements.importPayloadInput.value = JSON.stringify({
        schema: JSON.stringify([{ key: 'proxy', type: 'text' }]),
        values: JSON.stringify({ proxy: 'p' }),
    });
    context.applyPayloadImport();

    assert.equal(context.pmSchema[0].key, 'proxy');
    assert.equal(context.pmValues.proxy, 'p');
    assert.ok(!requests.some((r) => r.url === '/tasker/payload' && r.opts && r.opts.method === 'POST'));
});
