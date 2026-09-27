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

test('script parameters become schema fields that round-trip to the same flags', () => {
    const { context } = loadTasker();
    const params = [
        { names: ['--proxy-url'], help: 'Proxy', default: null },
        { names: ['--dry'], action: 'store_true', default: false },
        { names: ['--mode'], choices: ['fast', 'slow'], default: 'fast' },
        { names: ['--acc_id'], default: 7 },
        { names: ['target'], positional: true },
        { names: ['-n'] },
        { names: [], exprs: { names: ['name'] } },
        { names: ['--x'], subcommand: 'run' },
        { names: ['--no-cache'], action: 'store_false' },
        { names: ['--dryRun'] },
        { names: ['--url'] },
        { names: ['-h', '--help'] },
    ];

    const r = context.schemaFromScriptParams([{ key: 'url', type: 'text' }], { url: 'keep' }, params);

    assert.deepEqual([...r.added], ['--proxy-url', '--dry', '--mode', '--acc_id']);
    assert.deepEqual([...r.schema.map((f) => f.key)], ['url', 'proxyUrl', 'dry', 'mode', 'acc_id']);
    assert.deepEqual([...r.schema.map((f) => f.type)], ['text', 'text', 'boolean', 'select', 'text']);
    assert.equal(r.schema[3].options, 'fast,slow');
    assert.equal(r.schema[1].label, 'Proxy');
    assert.equal(r.values.url, 'keep');
    assert.equal(r.values.dry, 'false');
    assert.equal(r.values.mode, 'fast');
    assert.equal(r.values.acc_id, '7');
    assert.equal(r.values.proxyUrl, undefined);
    assert.equal(r.skipped.length, 7);
});

test('From script fills the schema from script-params without saving', async () => {
    const { context, requests } = loadTasker();
    const alerts = [];
    context.Dialog = { alert(msg) { alerts.push(msg); }, error(msg) { alerts.push('ERR ' + msg); } };
    context.fetch = async (url, opts) => {
        requests.push({ url, opts });
        const body = url.startsWith('/tasker/script-params?')
            ? { ok: true, params: [{ names: ['--url'] }, { names: ['--headless'], action: 'store_true' }], uses_sys_argv: false }
            : { ok: true, command: '' };
        return { ok: true, json: async () => body };
    };
    context.pmScheduleId = 't1';
    context.pmSchema = [];
    context.pmValues = {};

    await context.importSchemaFromScript();

    assert.ok(requests.some((r) => r.url === '/tasker/script-params?id=t1'));
    assert.deepEqual([...context.pmSchema.map((f) => f.key)], ['url', 'headless']);
    assert.ok(!requests.some((r) => r.url === '/tasker/payload' && r.opts && r.opts.method === 'POST'));
    assert.match(alerts[0], /Added: --url --headless/);
});

test('From script shows the server error verbatim', async () => {
    const { context } = loadTasker();
    const alerts = [];
    context.Dialog = { alert(msg) { alerts.push(msg); }, error(msg) { alerts.push('ERR ' + msg); } };
    context.fetch = async () => ({ ok: true, json: async () => ({ ok: false, step: 'executor', error: 'executor is node, not python' }) });
    context.pmScheduleId = 't1';
    context.pmSchema = [];

    await context.importSchemaFromScript();

    assert.deepEqual(alerts, ['ERR [executor] executor is node, not python']);
    assert.equal(context.pmSchema.length, 0);
});
