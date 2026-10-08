// System commit is independent of resident RAM. Values come from the live snapshot.
function memoryAllocationValues() {
    const pairs = currentSections.find(s => s.title === 'SYSTEM MEMORY SUMMARY')?.pairs || [];
    const value = key => {
        const raw = pairs.find(p => p.k === key)?.v;
        return raw === undefined ? null : Number.parseFloat(raw);
    };
    return { total: value('Total'), used: value('Used'), free: value('Free'),
        committed: value('Commit used bytes'), limit: value('Commit limit bytes'),
        peak: value('Commit peak bytes'), paged: value('Paged pool bytes'),
        nonpaged: value('Nonpaged pool bytes') };
}

function memorySize(bytes) {
    if (!Number.isFinite(bytes)) return 'Unavailable';
    return bytes < 1073741824
        ? (bytes / 1048576).toFixed(1) + ' MiB'
        : (bytes / 1073741824).toFixed(2) + ' GiB';
}

function memoryPressure(m) {
    if (!Number.isFinite(m.committed) || !(m.limit > 0)) return 'unavailable';
    const ratio = m.committed / m.limit;
    return ratio >= 0.97 ? 'critical' : ratio >= 0.9 ? 'warning' : 'normal';
}

function memoryPie(title, used, total, usedLabel, freeLabel, tone) {
    if (!Number.isFinite(used) || !(total > 0))
        return `<div class="memory-card"><h2>${esc(title)}</h2><p>Measurement unavailable</p></div>`;
    const percent = Math.max(0, Math.min(100, used / total * 100));
    const free = Math.max(0, total - used);
    const label = title + ': ' + usedLabel + ' ' + memorySize(used) +
        ', ' + freeLabel + ' ' + memorySize(free) + ', total ' + memorySize(total);
    return `<div class="memory-card ${tone}"><h2>${esc(title)}</h2>
        <div class="memory-chart-row">
            <div class="memory-pie" style="--used:${percent}%"
                 role="img" aria-label="${esc(label)}">
                <span>${percent.toFixed(1)}%</span>
            </div>
            <div class="memory-legend">
                <div><i class="memory-dot allocated"></i>${esc(usedLabel)}<strong>${memorySize(used)}</strong></div>
                <div><i class="memory-dot available"></i>${esc(freeLabel)}<strong>${memorySize(free)}</strong></div>
                <div class="memory-total">Total / limit<strong>${memorySize(total)}</strong></div>
            </div>
        </div></div>`;
}

function renderMemoryAllocation() {
    const m = memoryAllocationValues();
    const pressure = memoryPressure(m);
    const status = pressure === 'critical'
        ? 'Commit limit almost exhausted. New allocations can fail even with free RAM.'
        : pressure === 'warning' ? 'Commit usage is high. The remaining allocation headroom is small.'
        : pressure === 'unavailable' ? 'System commit measurement unavailable on this host.'
        : 'Allocation headroom is available.';
    return `<div class="memory-overview">
        <div class="memory-status ${pressure}" role="status">${status}</div>
        <div class="memory-cards">
            ${memoryPie('Physical RAM', m.used === null ? null : m.used * 1073741824,
                m.total === null ? null : m.total * 1073741824, 'In use', 'Available RAM', 'normal')}
            ${memoryPie('Promised memory (commit)', m.committed, m.limit,
                'Promised', 'Unpromised headroom', pressure)}
        </div>
        <p class="memory-explanation">RAM shows data currently in physical memory. Commit shows memory Windows
            has promised to applications, backed by RAM and paging files. Available RAM is already part of
            that backing capacity; it does not mean the same amount can still be promised.</p>
        <div class="memory-details">Peak commit since boot: <strong>${memorySize(m.peak)}</strong>
            · Paged kernel pool: <strong>${memorySize(m.paged)}</strong>
            · Nonpaged kernel pool: <strong>${memorySize(m.nonpaged)}</strong></div>
        <h2 class="memory-table-title">Allocation by process name</h2>
        <p class="memory-explanation">PRIVATE_MB is private allocated memory; RAM_MB is resident memory.
            All instances are summed. Process private totals do not cover all system commit
            (shared allocations and system memory also contribute). UNREADABLE counts processes
            whose private memory could not be read; those groups have incomplete totals.
            Values reflect the last Refresh.</p>
        </div>`;
}

function populateCommitStat() {
    const m = memoryAllocationValues();
    const el = document.getElementById('sCommit');
    const pressure = memoryPressure(m);
    el.textContent = pressure === 'unavailable' ? 'Unavailable'
        : memorySize(m.committed) + ' / ' + memorySize(m.limit) +
            ' (' + (m.committed / m.limit * 100).toFixed(1) + '%)';
    el.className = 'stat-val memory-commit ' + pressure;
    el.title = pressure === 'unavailable' ? 'System commit measurement unavailable'
        : 'Unpromised headroom: ' + memorySize(Math.max(0, m.limit - m.committed));
}
