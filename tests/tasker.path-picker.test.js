const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

test('file picked after a Settings refresh replaces the existing task path', async () => {
    let currentField = { value: 'C:\\tasks\\old.py', isConnected: true };
    const executorField = { value: 'python' };
    let finishPicker;
    const pickerResponse = new Promise((resolve) => { finishPicker = resolve; });
    const context = {
        console,
        document: {
            addEventListener() {},
            getElementById(id) {
                if (id === 'f_script_path') return currentField;
                if (id === 'f_executor') return executorField;
                return null;
            },
        },
        window: { addEventListener() {} },
        fetch: () => pickerResponse,
        alert() {},
        setInterval() { return 1; },
        clearInterval() {},
        setTimeout,
        clearTimeout,
        URLSearchParams,
    };
    vm.createContext(context);
    const sourcePath = path.join(__dirname, '..', 'wwwroot', 'js', 'tasker.js');
    vm.runInContext(fs.readFileSync(sourcePath, 'utf8'), context, { filename: sourcePath });
    context.selectedId = 'task-1';
    context.markTaskDirty = () => { context.formDirty = true; };

    context.pickPath('file');
    currentField.isConnected = false;
    currentField = { value: 'C:\\tasks\\old.py', isConnected: true };
    finishPicker({ json: async () => ({ ok: true, path: 'C:\\tasks\\new.py' }) });
    await new Promise((resolve) => setImmediate(resolve));

    assert.equal(currentField.value, 'C:\\tasks\\new.py');
    assert.equal(context.formDirty, true);
});
