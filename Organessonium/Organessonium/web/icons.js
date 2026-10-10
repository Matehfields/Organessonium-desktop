'use strict';

// Bundled Google Material Symbols: no network dependency, common glyphs in every view.
function icon(name, size) {
    const symbol = /^[a-z][a-z0-9_]*$/.test(String(name)) ? name : 'insert_drive_file';
    const pixels = Number.isFinite(Number(size)) ? Math.max(8, Math.min(128, Number(size))) : 20;
    const filled = symbol === 'star' || symbol === 'check_circle';
    return `<span class="svg-icon${filled ? ' icon-filled' : ''}" style="--icon-size:${pixels}px" aria-hidden="true">${symbol}</span>`;
}

function initIcons() {
    document.querySelectorAll('[data-icon]').forEach(el => {
        el.innerHTML = icon(el.getAttribute('data-icon'), parseInt(el.getAttribute('data-size') || '20', 10));
    });
}
