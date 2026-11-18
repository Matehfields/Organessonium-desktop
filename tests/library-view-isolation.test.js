const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

class Element {
    constructor() {
        this.children = [];
        this.dataset = {};
        this.classList = { add() {}, remove() {}, toggle() {}, contains() { return false; } };
        this.style = { setProperty() {} };
    }
    set innerHTML(value) { this.html = value; this.children = []; }
    get innerHTML() { return this.html || ''; }
    appendChild(child) { this.children.push(child); return child; }
    querySelector() { return new Element(); }
    addEventListener() {}
    setAttribute() {}
    removeAttribute() {}
}

test('favorites and devices stay out of Files while folder cards omit counts', () => {
    const elements = new Map();
    const document = {
        getElementById(id) {
            if (!elements.has(id)) elements.set(id, new Element());
            return elements.get(id);
        },
        createElement() { return new Element(); },
        addEventListener() {},
        querySelectorAll() { return []; },
    };
    const bridgeCalls = [];
    const window = {
        bridge: {
            loadFavorites(force) { bridgeCalls.push(['favorites', force]); },
            loadSynchronizedDevices(force) { bridgeCalls.push(['devices', force]); },
            setCurrentPath() {},
        },
        matchMedia() { return { matches: false }; },
    };
    const context = vm.createContext({
        document, window, navigator: { language: 'pt-BR' },
        localStorage: { getItem() { return null; }, setItem() {} },
        setTimeout() { return 1; }, clearTimeout() {},
        console,
    });
    const source = fs.readFileSync(
        path.join(__dirname, '..', 'Organessonium', 'Organessonium', 'web', 'app.js'),
        'utf8',
    );
    vm.runInContext(source, context);
    vm.runInContext('icon = () => ""; activateWorkspaceTab = () => {}; scheduleCardPreviewSync = () => {}; scheduleVisibleFolderStatistics = () => {}; bindInternalItemDrag = () => {}; bindThumbnailPreview = () => {}', context);

    const folder = { type: 'folder', name: 'Fotos', path: '/', parentFolderId: 'root', folderId: 'photos' };
    const favorite = { type: 'file', name: 'favorite.jpg', path: '/Fotos', parentFolderId: 'photos', threadId: 'fav-thread', metadataMessageId: 'fav-meta' };
    const device = { type: 'folder', name: 'Telefone', path: '/@synchronized', parentFolderId: 'synchronized', folderId: 'phone' };
    context.rootItems = [folder];
    context.favoriteItems = [favorite];
    context.deviceItemsInput = [device];
    vm.runInContext('allApps = rootItems; render()', context);
    const grid = elements.get('files-grid');
    assert.match(grid.children[0].children[0].innerHTML, /Fotos/);
    assert.doesNotMatch(grid.children[0].children[0].innerHTML, /folder-count/);

    vm.runInContext('showFavoritesWorkspace(); window._onSpecialItemsLoaded("favorites", favoriteItems)', context);
    assert.equal(bridgeCalls.filter(([view]) => view === 'favorites').length, 1);
    assert.match(grid.children[0].children[0].innerHTML, /favorite\.jpg/);
    assert.equal(vm.runInContext('allApps.length', context), 1);
    assert.equal(vm.runInContext('getSelectedApps().length', context), 0);
    assert.equal(vm.runInContext('selectedKeys.add(itemKey(favoriteItems[0])); getSelectedApps().length', context), 1);

    vm.runInContext('showFilesWorkspace()', context);
    assert.doesNotMatch(grid.children[0].children[0].innerHTML, /favorite\.jpg/);
    assert.doesNotMatch(grid.children[0].children[0].innerHTML, /folder-count/);

    vm.runInContext('showFavoritesWorkspace()', context);
    assert.equal(bridgeCalls.filter(([view]) => view === 'favorites').length, 1);
    assert.equal(vm.runInContext('specialItems.length', context), 1);
    assert.match(grid.children[0].children[0].innerHTML, /favorite\.jpg/);
    vm.runInContext('showFavoritesWorkspace(true)', context);
    assert.deepEqual(bridgeCalls.at(-1), ['favorites', true]);
    vm.runInContext('showDevicesWorkspace(); window._onSpecialItemsLoaded("devices", deviceItemsInput)', context);
    assert.match(grid.children[0].children[0].innerHTML, /Telefone/);
    vm.runInContext('showDevicesWorkspace()', context);
    assert.equal(bridgeCalls.filter(([view]) => view === 'devices').length, 1);
    assert.equal(vm.runInContext('allApps.length', context), 1);
    vm.runInContext('showFilesWorkspace()', context);
    assert.doesNotMatch(grid.children[0].children[0].innerHTML, /folder-count/);
});
