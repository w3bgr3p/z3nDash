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

    return {
        decodeInputSettings: decodeInputSettings,
        buildInputPayload: buildInputPayload
    };
});
