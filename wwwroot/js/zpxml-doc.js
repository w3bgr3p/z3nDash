/* zpxml-doc.js — документ шаблона: кодировка, сериализация, мутации, история.
   Единственное место, где шаблон изменяется. Про экран не знает ничего. */

const ZpDoc = {
    doc: null,          // XMLDocument — источник истины
    encoding: 'utf-8',  // кодировка исходного файла
    declaration: '',    // объявление <?xml ... ?> дословно
    fileName: '',
    dirty: false,

    /// Открыть файл. Принимает байты, а не строку: ZennoPoster сохраняет шаблоны
    /// в UTF-16, и декодирование их как UTF-8 даёт мусор с первого символа.
    open(buffer, fileName) {
        const b = new Uint8Array(buffer);
        let skip = 0;
        this.encoding = 'utf-8';
        if (b.length >= 2 && b[0] === 0xFF && b[1] === 0xFE) { this.encoding = 'utf-16le'; skip = 2; }
        else if (b.length >= 2 && b[0] === 0xFE && b[1] === 0xFF) { this.encoding = 'utf-16be'; skip = 2; }
        else if (b.length >= 3 && b[0] === 0xEF && b[1] === 0xBB && b[2] === 0xBF) { skip = 3; }

        let text;
        try { text = new TextDecoder(this.encoding).decode(b.subarray(skip)); }
        catch (e) { text = new TextDecoder('utf-8').decode(b.subarray(skip)); }

        // Объявление сохраняем дословно: в нём записана кодировка, и переписывать
        // его нельзя — ProjectMaker не читает шаблон в UTF-8.
        const m = text.match(/^\s*<\?xml[^?]*\?>/);
        this.declaration = m ? m[0].trim() : '';
        const body = text.slice(m ? m[0].length : 0).replace(/^﻿/, '');

        const parsed = new DOMParser().parseFromString(body, 'text/xml');
        const err = parsed.querySelector('parsererror');
        if (err) throw new Error(err.textContent.replace(/\s+/g, ' ').substring(0, 180));

        this.doc = parsed;
        this.fileName = fileName;
        this.dirty = false;
        this.undoStack = [];
        this.redoStack = [];
        return this.doc;
    },

    /// Снимок для восстановления после перехода на другую страницу. Историю
    /// правок не сохраняем: она стоила бы десятки копий документа, а вернуть
    /// нужно состояние, а не путь к нему.
    exportState() {
        if (!this.doc) return null;
        return {
            v: 1,
            fileName: this.fileName,
            encoding: this.encoding,
            declaration: this.declaration,
            dirty: this.dirty,
            xml: this.serialize()
        };
    },

    importState(state) {
        if (!state || state.v !== 1 || !state.xml) return false;
        const body = state.xml.replace(/^\s*<\?xml[^?]*\?>/, '');
        const parsed = new DOMParser().parseFromString(body, 'text/xml');
        if (parsed.querySelector('parsererror')) return false;

        this.doc = parsed;
        this.fileName = state.fileName || '';
        this.encoding = state.encoding || 'utf-8';
        this.declaration = state.declaration || '';
        this.dirty = !!state.dirty;
        this.undoStack = [];
        this.redoStack = [];
        return true;
    },

    /// Текст документа вместе с исходным объявлением.
    serialize() {
        const body = new XMLSerializer().serializeToString(this.doc);
        return (this.declaration || '') + body;
    },

    /// Байты для сохранения — в исходной кодировке, с исходным BOM.
    bytes() {
        const text = this.serialize();
        if (this.encoding === 'utf-16le' || this.encoding === 'utf-16be') {
            const le = this.encoding === 'utf-16le';
            const out = new Uint8Array(2 + text.length * 2);
            out[0] = le ? 0xFF : 0xFE;
            out[1] = le ? 0xFE : 0xFF;
            const view = new DataView(out.buffer);
            for (let i = 0; i < text.length; i++) view.setUint16(2 + i * 2, text.charCodeAt(i), le);
            return out;
        }
        return new TextEncoder().encode(text);
    },

    // ── Поиск элементов ──────────────────────────────────────────────────

    stepEl(stepId) {
        return [...this.doc.querySelectorAll('Step')].find(s => s.getAttribute('ID') === stepId) || null;
    },

    branchEl(branchId) {
        return [...this.doc.querySelectorAll('Branch')].find(b => b.getAttribute('ID') === branchId) || null;
    },

    // ── Операции ─────────────────────────────────────────────────────────

    /// Все места, где может стоять адрес перехода. Case и Default хранят его
    /// внутри экранированной разметки <Pair>, поэтому там замена по тексту.
    /// Пропущенная ссылка даёт «переход в никуда», который рантайм ловит уже
    /// в бою, поэтому список мест должен быть полным.
    retarget(oldAddr, newAddr) {
        if (!oldAddr || oldAddr === newAddr) return 0;
        let n = 0;

        this.doc.querySelectorAll('OnSuccess, OnError').forEach(el => {
            if ((el.textContent || '').trim() === oldAddr) { el.textContent = newAddr; n++; }
        });

        this.doc.querySelectorAll('Results > *').forEach(el => {
            if (!/^Case\d+$|^Default$/.test(el.tagName)) return;
            const txt = el.textContent || '';
            if (!txt.includes(oldAddr)) return;
            el.textContent = txt.split(oldAddr).join(newAddr);
            n++;
        });

        ['Start', 'GoodEnd', 'BadEnd'].forEach(tag => {
            const el = this.doc.querySelector(tag);
            if (!el || (el.getAttribute('nextAction') || '').trim() !== oldAddr) return;
            if (newAddr) el.setAttribute('nextAction', newAddr);
            else el.removeAttribute('nextAction');
            n++;
        });

        return n;
    },

    /// Сколько переходов ведёт на ветку. Нужно, чтобы сообщать пользователю,
    /// что именно изменилось при удалении.
    incomingCount(stepId, branchId) {
        const addr = stepId + '|' + branchId;
        let n = 0;
        this.doc.querySelectorAll('OnSuccess, OnError').forEach(el => {
            if ((el.textContent || '').trim() === addr) n++;
        });
        this.doc.querySelectorAll('Results > *').forEach(el => {
            if (/^Case\d+$|^Default$/.test(el.tagName) && (el.textContent || '').includes(addr)) n++;
        });
        ['Start', 'GoodEnd', 'BadEnd'].forEach(tag => {
            const el = this.doc.querySelector(tag);
            if (el && (el.getAttribute('nextAction') || '').trim() === addr) n++;
        });
        return n;
    },

    /// Блок без веток в ProjectMaker не существует, а сослаться на него нельзя.
    dropEmptySteps() {
        [...this.doc.querySelectorAll('Step')].forEach(s => {
            if (!s.querySelector('Branch')) s.remove();
        });
    },

    /// Перенести ветку в блок toStepId на позицию index. Элемент перемещается
    /// целиком, а не пересоздаётся: <Parameters> и <SettingsControl> обязаны
    /// уехать вместе с ним — без них ветка перестанет работать.
    /// Перестановка внутри блока — это тот же вызов с тем же toStepId; адрес
    /// при этом не меняется, и retarget не нужен.
    moveBranch(branchId, toStepId, index) {
        const el = this.branchEl(branchId);
        const to = this.stepEl(toStepId);
        if (!el || !to) return false;

        const fromStepId = el.parentNode.getAttribute('ID');
        this.snapshot();

        const siblings = [...to.children].filter(c => c.tagName === 'Branch' && c !== el);
        to.insertBefore(el, siblings[index] || null);

        if (fromStepId !== toStepId)
            this.retarget(fromStepId + '|' + branchId, toStepId + '|' + branchId);

        this.dropEmptySteps();
        return true;
    },

    // ── Правка содержимого ветки ─────────────────────────────────────────

    /// Узел параметра по пути из flattenParams: 'Finder.Tag' или
    /// 'Finder.SearchCondition@AttrValue'. Возвращает узел и имя атрибута,
    /// если путь указывает на атрибут.
    paramNode(branchId, path) {
        const el = this.branchEl(branchId);
        if (!el) return null;
        let node = el.querySelector('Parameters');
        if (!node) return null;

        const at = path.split('@');
        const segs = at[0].split('.');
        for (const seg of segs) {
            node = [...node.children].find(c => c.tagName === seg);
            if (!node) return null;
        }
        return { node, attr: at.length > 1 ? at[1] : null };
    },

    setParam(branchId, path, value) {
        const loc = this.paramNode(branchId, path);
        if (!loc) return false;
        const current = loc.attr ? loc.node.getAttribute(loc.attr) : loc.node.textContent;
        if (current === value) return false;      // пустая правка истории не стоит

        this.snapshot();
        if (loc.attr) loc.node.setAttribute(loc.attr, value);
        else loc.node.textContent = value;
        return true;
    },

    /// Код ветки OwnCode. Лежит в Parameters/Code.
    setCode(branchId, code) {
        const el = this.branchEl(branchId);
        const node = el && el.querySelector('Parameters > Code');
        if (!node || node.textContent === code) return false;
        this.snapshot();
        node.textContent = code;
        return true;
    },

    /// Подпись ветки — та, что видна на строке блока.
    setBranchText(branchId, text) {
        const el = this.branchEl(branchId);
        if (!el || (el.getAttribute('UserText') || '') === text) return false;
        this.snapshot();
        el.setAttribute('UserText', text);
        return true;
    },

    /// Флаги ветки. ZennoPoster пишет их не всегда; отсутствие значит «нет».
    setBranchFlag(branchId, flag, on) {
        const attr = flag === 'disabled' ? 'IsDisable' : 'IsNotNecessarily';
        const el = this.branchEl(branchId);
        if (!el) return false;
        const current = /^true$/i.test(el.getAttribute(attr) || '');
        if (current === on) return false;
        this.snapshot();
        el.setAttribute(attr, on ? 'True' : 'False');
        return true;
    },

    /// Ключ варианта Switch: то, с чем сравнивается переменная. Лежит внутри
    /// экранированной разметки <Pair>, рядом с адресом перехода — правим
    /// только <Key>, адрес не трогаем.
    setCaseKey(branchId, slot, key) {
        const el = this.branchEl(branchId);
        const results = el && el.querySelector('Results');
        if (!results) return false;

        const tag = slot === 'default' ? 'Default' : 'Case' + slot.split(':')[1];
        const node = results.querySelector(tag);
        if (!node) return false;

        const txt = node.textContent || '';
        const escaped = key.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
        const next = txt.includes('<Key>')
            ? txt.replace(/<Key>[^<]*<\/Key>/, '<Key>' + escaped + '</Key>')
            : '<Pair><Key>' + escaped + '</Key><Value></Value></Pair>';
        if (next === txt) return false;

        this.snapshot();
        node.textContent = next;
        return true;
    },

    /// Записать переход ветки. slot: 'ok' | 'err' | 'case:N' | 'default',
    /// где N — номер из имени тега <CaseN>, а не позиция в файле.
    /// target вида stepId|branchId, либо null — стереть переход.
    /// Case и Default хранят адрес внутри экранированной разметки <Pair>,
    /// поэтому там правится только <Value>, а ключ остаётся как был.
    setTransition(branchId, slot, target) {
        const el = this.branchEl(branchId);
        if (!el) return false;
        let results = el.querySelector('Results');
        if (!results) {
            results = this.doc.createElement('Results');
            el.appendChild(results);
        }

        const value = target || '';

        if (slot === 'ok' || slot === 'err') {
            const tag = slot === 'ok' ? 'OnSuccess' : 'OnError';
            this.snapshot();
            let node = results.querySelector(tag);
            if (!node) { node = this.doc.createElement(tag); results.appendChild(node); }
            node.textContent = value;
            return true;
        }

        const tag = slot === 'default' ? 'Default' : 'Case' + slot.split(':')[1];
        const node = results.querySelector(tag);
        if (!node) return false;          // варианта с таким номером в файле нет

        this.snapshot();
        const txt = node.textContent || '';
        node.textContent = txt.includes('<Value>')
            ? txt.replace(/<Value>[^<]*<\/Value>/, '<Value>' + value + '</Value>')
            : '<Pair><Key></Key><Value>' + value + '</Value></Pair>';
        return true;
    },

    /// Удалить ветку. Входящие переходы переводятся на следующую ветку того же
    /// блока — так маршрут сохраняет смысл. Если удаляемая была последней,
    /// переходы очищаются: по правилам рантайма пустой переход у последней
    /// ветки и означает конец маршрута.
    deleteBranch(branchId) {
        const el = this.branchEl(branchId);
        if (!el) return null;

        const stepEl = el.parentNode;
        const stepId = stepEl.getAttribute('ID');
        const siblings = [...stepEl.children].filter(c => c.tagName === 'Branch');
        const pos  = siblings.indexOf(el);
        const next = siblings[pos + 1] || null;

        const oldAddr = stepId + '|' + branchId;
        const newAddr = next ? stepId + '|' + next.getAttribute('ID') : '';

        this.snapshot();
        const moved = this.retarget(oldAddr, newAddr);
        el.remove();
        this.dropEmptySteps();

        return { moved, movedToIndex: next ? pos : null };
    },

    /// Создать блок рядом с существующими. Набор атрибутов копируется с уже
    /// имеющегося <Step>, а не выдумывается: какие из них нужны ProjectMaker,
    /// доподлинно неизвестно, поэтому повторяем то, что он пишет сам.
    /// Вставляем рядом с другими Step, а не в конец: после блоков идёт
    /// <StaticTechnologies>, и порядок узлов лучше не путать.
    createStep(x, y) {
        const sample = this.doc.querySelector('Step');
        if (!sample) return null;
        const el = this.doc.createElement('Step');
        [...sample.attributes].forEach(a => el.setAttribute(a.name, a.value));
        el.setAttribute('ID', crypto.randomUUID());
        el.setAttribute('x', String(Math.round(x)));
        el.setAttribute('y', String(Math.round(y)));
        el.setAttribute('UserText', '');
        sample.parentNode.insertBefore(el, sample.nextSibling);
        return el;
    },

    extractBranch(branchId, x, y) {
        const el = this.branchEl(branchId);
        if (!el) return false;
        const fromStepId = el.parentNode.getAttribute('ID');

        this.snapshot();
        const step = this.createStep(x, y);
        if (!step) return false;

        const toStepId = step.getAttribute('ID');
        step.appendChild(el);
        this.retarget(fromStepId + '|' + branchId, toStepId + '|' + branchId);
        this.dropEmptySteps();
        return true;
    },

    moveStep(stepId, x, y) {
        const el = this.stepEl(stepId);
        if (!el) return false;
        this.snapshot();
        el.setAttribute('x', String(Math.round(x)));
        el.setAttribute('y', String(Math.round(y)));
        return true;
    },

    // ── История ──────────────────────────────────────────────────────────

    undoStack: [],
    redoStack: [],
    maxHistory: 50,

    /// Снимок до правки. Строка документа — точное состояние, поэтому откат
    /// возвращает ровно то, что было, а не приблизительную реконструкцию.
    snapshot() {
        this.undoStack.push(this.serialize());
        if (this.undoStack.length > this.maxHistory) this.undoStack.shift();
        this.redoStack.length = 0;
        this.dirty = true;
    },

    _restore(text) {
        const body = text.replace(/^\s*<\?xml[^?]*\?>/, '');
        this.doc = new DOMParser().parseFromString(body, 'text/xml');
    },

    undo() {
        if (!this.undoStack.length) return false;
        this.redoStack.push(this.serialize());
        this._restore(this.undoStack.pop());
        this.dirty = true;
        return true;
    },

    redo() {
        if (!this.redoStack.length) return false;
        this.undoStack.push(this.serialize());
        this._restore(this.redoStack.pop());
        this.dirty = true;
        return true;
    }
};
