const LibrarySearch = (() => {
    const input = document.getElementById('search-input');
    const wrap = input.closest('.search-wrap');
    const clearButton = document.getElementById('library-search-clear');
    const popup = document.getElementById('library-search-popup');
    const list = document.getElementById('library-search-results');
    const status = document.getElementById('library-search-status');
    let request = 0, timer = 0, results = [], selected = -1;

    function close() {
        clearTimeout(timer);
        request++;
        popup.hidden = true;
        input.setAttribute('aria-expanded', 'false');
        input.removeAttribute('aria-activedescendant');
    }

    function clear() {
        input.value = '';
        clearButton.hidden = true;
        close();
    }

    function update() {
        close();
        const query = input.value.trim();
        clearButton.hidden = input.value.length === 0;
        if (!query) return;
        results = [];
        selected = -1;
        list.replaceChildren();
        status.hidden = false;
        status.textContent = t('searchLoading');
        popup.hidden = false;
        input.setAttribute('aria-expanded', 'true');
        const id = request;
        timer = setTimeout(() => window.bridge.searchLibrary(id, query), 140);
    }

    window._onLibrarySearchResults = (id, entries) => {
        if (id !== request || popup.hidden) return;
        results = entries;
        const rows = entries.map((entry, index) => {
            const row = document.createElement('div');
            row.id = 'library-search-option-' + index;
            row.className = 'library-search-option';
            row.dataset.index = index;
            row.setAttribute('role', 'option');
            row.setAttribute('aria-selected', 'false');
            const glyph = entry.type === 'folder' ? 'folder' : fileIcon(fileType(entry.name));
            row.innerHTML = icon(glyph, 18) + '<strong></strong><small></small>';
            row.querySelector('strong').textContent = entry.name;
            row.querySelector('small').textContent = entry.path === '/' ? t('sidebarFiles') : entry.path;
            return row;
        });
        list.replaceChildren(...rows);
        status.textContent = entries.length ? '' : t('searchEmpty');
        status.hidden = entries.length > 0;
    };

    function bind(open) {
        const select = index => {
            const entry = results[index];
            if (!entry) return;
            clear();
            input.blur();
            open(entry);
        };
        input.addEventListener('input', update);
        input.addEventListener('focus', update);
        clearButton.addEventListener('click', () => { clear(); input.focus(); });
        list.addEventListener('pointerdown', event => event.preventDefault());
        list.addEventListener('click', event => select(Number(event.target.closest('[data-index]')?.dataset.index)));
        document.addEventListener('pointerdown', event => { if (!wrap.contains(event.target)) close(); });
        wrap.addEventListener('focusout', event => { if (!wrap.contains(event.relatedTarget)) close(); });
        input.addEventListener('keydown', event => {
            if (popup.hidden) return;
            if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); close(); return; }
            if (!results.length) return;
            if (event.key === 'Enter') { event.preventDefault(); event.stopPropagation(); select(Math.max(0, selected)); return; }
            if (!['ArrowDown', 'ArrowUp'].includes(event.key)) return;
            event.preventDefault();
            event.stopPropagation();
            const next = selected < 0 ? (event.key === 'ArrowDown' ? 0 : results.length - 1)
                : (selected + (event.key === 'ArrowDown' ? 1 : -1) + results.length) % results.length;
            list.children[selected]?.setAttribute('aria-selected', 'false');
            selected = next;
            const option = list.children[selected];
            option.setAttribute('aria-selected', 'true');
            input.setAttribute('aria-activedescendant', option.id);
            option.scrollIntoView({ block: 'nearest' });
        });
    }

    return { bind, clear };
})();
