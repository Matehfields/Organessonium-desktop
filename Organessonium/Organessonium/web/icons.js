'use strict';

// Bundled Google Material Symbols: no network dependency, common glyphs in every view.
function icon(name, size, color) {
    const symbol = /^[a-z][a-z0-9_]*$/.test(String(name)) ? name : 'insert_drive_file';
    const pixels = Number.isFinite(Number(size)) ? Math.max(8, Math.min(128, Number(size))) : 20;
    const filled = ['star','check_circle','folder','folder_open','folder_zip','image','movie'].includes(symbol);
    const media = symbol === 'image' || symbol === 'movie';
    const folderColor = ['folder','folder_open'].includes(symbol) && /^#[0-9a-f]{6}$/i.test(color || '') && color.toUpperCase() !== '#C4C7C5'
        ? `;--icon-color:${color}` : '';
    return `<span class="svg-icon${filled ? ' icon-filled' : ''}${media ? ' icon-media' : ''}" style="--icon-size:${pixels}px${folderColor}" aria-hidden="true">${symbol}</span>`;
}

function initIcons() {
    document.querySelectorAll('[data-icon]').forEach(el => {
        el.innerHTML = icon(el.getAttribute('data-icon'), parseInt(el.getAttribute('data-size') || '20', 10));
    });
}
