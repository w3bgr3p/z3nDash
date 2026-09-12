(function (root, factory) {
    var api = factory();
    if (typeof module === 'object' && module.exports) module.exports = api;
    root.ZpThreadFilter = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';

    function toggle(selected, clicked) {
        return selected === clicked ? null : clicked;
    }

    function threads(entries) {
        var seen = Object.create(null);
        return (entries || []).reduce(function (result, entry) {
            var thread = entry && entry.thread ? String(entry.thread) : '';
            if (thread && !seen[thread]) {
                seen[thread] = true;
                result.push(thread);
            }
            return result;
        }, []);
    }

    function apply(entries, selectedThread, level) {
        var normalizedLevel = String(level || '').toUpperCase();
        return (entries || []).filter(function (entry) {
            if (selectedThread && String(entry.thread || '') !== selectedThread) return false;
            return !normalizedLevel || String(entry.level || '').toUpperCase() === normalizedLevel;
        });
    }

    return { toggle: toggle, threads: threads, apply: apply };
});
