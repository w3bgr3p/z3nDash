(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) module.exports = api;
    root.ZpSettings = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    function decodeBase64Utf8(value) {
        if (!value) return '';
        if (typeof Buffer !== 'undefined') return Buffer.from(value, 'base64').toString('utf8');
        var bytes = Uint8Array.from(atob(value), function (c) { return c.charCodeAt(0); });
        return new TextDecoder().decode(bytes);
    }

    function encodeBase64Utf8(value) {
        if (typeof Buffer !== 'undefined') return Buffer.from(value, 'utf8').toString('base64');
        var bytes = new TextEncoder().encode(value);
        var binary = '';
        bytes.forEach(function (b) { binary += String.fromCharCode(b); });
        return btoa(binary);
    }

    function requireFlatStringObject(value) {
        if (!value || typeof value !== 'object' || Array.isArray(value))
            throw new Error('Input settings must be a flat JSON object');

        Object.keys(value).forEach(function (key) {
            if (typeof value[key] !== 'string')
                throw new Error('Input settings values must be string values');
        });
        return value;
    }

    function decodeInputSettings(response) {
        if (!response || typeof response.xml_b64 !== 'string' || !response.xml_b64)
            throw new Error('Node response does not contain xml_b64');

        var json = response.json_b64 ? decodeBase64Utf8(response.json_b64) : '{}';
        return {
            xmlB64: response.xml_b64,
            values: requireFlatStringObject(JSON.parse(json || '{}'))
        };
    }

    function buildInputPayload(xmlB64, jsonText) {
        if (!xmlB64) throw new Error('xml_b64 required');
        var values = requireFlatStringObject(JSON.parse(jsonText));
        return JSON.stringify({
            xml_b64: xmlB64,
            json_b64: encodeBase64Utf8(JSON.stringify(values))
        });
    }

    // ── Разбор InputSettings XML ──────────────────────────────────────────────
    // Порт ParseInputSettingsXml из z3nIO. Ключ поля лежит в OutputVariable
    // как {-Variable.имя-}; подпись приходит с HTML-разметкой, её снимаем.

    function fieldKey(outputVariable) {
        return String(outputVariable || '').replace('{-Variable.', '').replace('-}', '').trim();
    }

    function fieldLabel(name) {
        var holder = document.createElement('div');
        holder.innerHTML = String(name || '');
        return (holder.textContent || '').trim();
    }

    /// Поле InputSetting приходит в двух видах: из ZennoPoster — дочерними
    /// элементами, а в сохранённом шаблоне — атрибутами тега. Читаем оба, иначе
    /// на файле .xml разбор молча возвращает пустые поля.
    function childText(element, tag) {
        var node = element.getElementsByTagName(tag)[0];
        if (node) return node.textContent || '';
        return element.getAttribute ? (element.getAttribute(tag) || '') : '';
    }

    /// Список полей в порядке XML. Tab и Comment остаются в списке: по ним
    /// строятся вкладки и разделители, ключа у них нет.
    function parseFields(xmlB64, values) {
        var xml = decodeBase64Utf8(xmlB64);
        return parseFieldsXml(xml, values);
    }

    /// То же, но из готового XML: шаблон на диске не закодирован в base64.
    function parseFieldsXml(xml, values) {
        var doc = new DOMParser().parseFromString(xml, 'application/xml');
        if (doc.getElementsByTagName('parsererror').length)
            throw new Error('Input settings XML is not well-formed');

        var current = values || {};
        var nodes = doc.getElementsByTagName('InputSetting');
        var fields = [];
        for (var i = 0; i < nodes.length; i++) {
            var node = nodes[i];
            var outputVariable = childText(node, 'OutputVariable');
            var key = fieldKey(outputVariable);
            fields.push({
                type: childText(node, 'Type') || 'Text',
                key: key,
                label: fieldLabel(childText(node, 'Name')),
                // В сохранённом шаблоне текущего значения нет — есть только
                // DefaultValue, с которым шаблон и стартует.
                value: Object.prototype.hasOwnProperty.call(current, key)
                    ? current[key]
                    : (childText(node, 'Value') || childText(node, 'DefaultValue')),
                outputVar: outputVariable,
                help: childText(node, 'Help')
            });
        }
        return fields;
    }

    /// Варианты для DropDown берутся из подписи: «Прокси {http|socks5}».
    function fieldOptions(label) {
        var match = /\{([^}]+)\}/.exec(String(label || ''));
        return match ? match[1].split('|').map(function (o) { return o.trim(); }) : null;
    }

    /// Подпись без блока вариантов; если не осталось ничего — показываем ключ.
    function cleanLabel(field) {
        return String(field.label || '').replace(/\{[^}]+\}/g, '').trim() || field.key || '';
    }

    /// Тип поля ProjectMaker → тип в схеме payload планировщика.
    function schemaType(type) {
        switch (type) {
            case 'Tab': return 'tab';
            case 'Comment': return 'section';
            case 'Boolean': return 'boolean';
            case 'DropDown': return 'select';
            case 'DropDownMultiSelect': return 'multiselect';
            case 'Password': return 'password';
            default: return 'text';
        }
    }

    /// Payload планировщика: schema описывает поля, values — текущие значения.
    function buildSchedulerPayload(fields, values) {
        var schema = fields.map(function (field) {
            var type = schemaType(field.type);
            var item = { key: field.key || '', label: cleanLabel(field), type: type };
            var options = fieldOptions(field.label);
            if (options && (type === 'select' || type === 'multiselect')) item.options = options.join(', ');
            if (field.help) item.help = field.help;
            return item;
        });
        return JSON.stringify({
            schema: JSON.stringify(schema),
            values: JSON.stringify(requireFlatStringObject(values))
        });
    }

    return {
        decodeInputSettings: decodeInputSettings,
        parseFieldsXml: parseFieldsXml,
        buildInputPayload: buildInputPayload,
        parseFields: parseFields,
        fieldOptions: fieldOptions,
        cleanLabel: cleanLabel,
        buildSchedulerPayload: buildSchedulerPayload
    };
});
