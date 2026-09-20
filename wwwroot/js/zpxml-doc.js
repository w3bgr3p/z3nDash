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
