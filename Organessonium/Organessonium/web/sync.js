/* UI only. Filesystem, planning, hashing and conflict rules are implemented in C#. */
(() => {
    const byId = id => document.getElementById(id);
    let data = { folders: [], statuses: [], issues: [], sources: [], paused: false };
    let issueShown = null, returnFocus = null;
    const seen = new Set();
    const en = () => resolveLanguage() === 'en';
    const text = (pt, english) => en() ? english : pt;
    const button = (label, action, css = 'btn-secondary') => {
        const b = document.createElement('button'); b.type = 'button'; b.className = css;
        b.textContent = label; b.addEventListener('click', action); return b;
    };
    const overlay = document.createElement('div'); overlay.id = 'sync-overlay'; overlay.className = 'modal-overlay hidden';
    overlay.innerHTML = '<section class="modal-box sync-dialog" role="dialog" aria-modal="true" aria-labelledby="sync-dialog-title"><h3 id="sync-dialog-title"></h3><div id="sync-dialog-body"></div><div id="sync-dialog-actions" class="modal-actions"></div></section>';
    document.body.appendChild(overlay);
    const body = byId('sync-dialog-body'), actions = byId('sync-dialog-actions');
    function close() { overlay.classList.add('hidden'); issueShown = null; if (returnFocus?.isConnected) returnFocus.focus(); }
    function dialog(title) {
        returnFocus = document.activeElement; byId('sync-dialog-title').textContent = title;
        body.replaceChildren(); actions.replaceChildren(); overlay.classList.remove('hidden');
    }
    overlay.addEventListener('keydown', e => {
        e.stopPropagation();
        if (e.key === 'Escape') { e.preventDefault(); close(); }
        if (e.key === 'Tab') {
            const controls = [...overlay.querySelectorAll('button,input,select')].filter(el => !el.disabled && el.getClientRects().length);
            if (!controls.length) return;
            const first = controls[0], last = controls[controls.length - 1];
            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
        }
    });
    function field(label, element) {
        const row = document.createElement('label'); row.className = 'sync-field';
        const title = document.createElement('span'); title.textContent = label; row.append(title, element); body.append(row); return element;
    }
    function input(id, value = '', type = 'text') {
        const element = document.createElement('input'); element.id = id; element.type = type;
        if (type === 'checkbox') element.checked = Boolean(value); else { element.value = value; element.className = 'modal-input'; }
        return element;
    }
    function select(id, options, value) {
        const element = document.createElement('select'); element.id = id; element.className = 'modal-input';
        options.forEach(([v, label]) => { const o = document.createElement('option'); o.value = v; o.textContent = label; element.append(o); });
        element.value = value; return element;
    }
    function configure(binding = null, source = null) {
        dialog(text(binding ? 'Opções da pasta sincronizada' : source ? 'Sincronizar esta pasta neste computador' : 'Adicionar pasta sincronizada',
            binding ? 'Folder synchronization options' : source ? 'Sync this folder on this computer' : 'Add synchronized folder'));
        field(text('Nome exibido', 'Display name'), input('sync-folder-name', binding?.name || source?.name || ''));
        const path = field(text('Pasta local', 'Local folder'), input('sync-local-path', binding?.localPath || '')); path.readOnly = true;
        if (!binding) body.append(button(text('Selecionar pasta...', 'Choose folder...'), () => window.bridge.chooseSyncFolder()));
        field(text('Sincronização automática', 'Automatic synchronization'), input('sync-auto', binding?.automatic ?? true, 'checkbox'));
        field(text('Sincronização bidirecional', 'Two-way synchronization'), input('sync-two-way', binding?.bidirectional ?? true, 'checkbox'));
        field(text('Quando os dois dispositivos alterarem o arquivo', 'When both devices change a file'), select('sync-conflict-policy', [
            ['ask', text('Perguntar antes de resolver', 'Ask before resolving')], ['both', text('Manter ambas as versões', 'Keep both versions')]
        ], binding?.conflictPolicy || 'ask'));
        field(text('Exclusões', 'Deletions'), select('sync-deletion-policy', [
            ['ask', text('Pedir confirmação', 'Ask for confirmation')], ['propagate', text('Sincronizar exclusões sem alterações concorrentes', 'Sync deletions without concurrent changes')],
            ['ignore', text('Não propagar exclusões automaticamente', 'Do not propagate deletions automatically')]
        ], binding?.deletionPolicy || 'ask'));
        field(text('Atualizar automaticamente versões vindas da nuvem', 'Automatically apply cloud-only updates'), input('sync-remote-auto', binding?.automaticRemoteUpdates ?? false, 'checkbox'));
        if (!binding) field(text('Iniciar sincronização com o Windows', 'Start synchronization with Windows'), input('sync-folder-startup', false, 'checkbox'));
        const help = document.createElement('p'); help.className = 'sync-help'; help.textContent = text('Alterações concorrentes são preservadas. Links e junctions não são sincronizados.', 'Concurrent changes are preserved. Links and junctions are not synchronized.'); body.append(help);
        actions.append(button(text('Cancelar', 'Cancel'), close), button(text('Salvar vínculo', 'Save binding'), () => {
            if (!path.value || !byId('sync-folder-name').value.trim()) { path.focus(); return; }
            const value = { ...(binding || {}), name: byId('sync-folder-name').value.trim(), localPath: path.value,
                automatic: byId('sync-auto').checked, bidirectional: byId('sync-two-way').checked,
                conflictPolicy: byId('sync-conflict-policy').value, deletionPolicy: byId('sync-deletion-policy').value,
                automaticRemoteUpdates: byId('sync-remote-auto').checked, startWithWindows: byId('sync-folder-startup')?.checked ?? binding?.startWithWindows ?? false };
            if (source) value.syncFolderId = source.sync.syncFolderId;
            if (binding) { window.bridge.updateSyncFolder(JSON.stringify(value)); close(); }
            else window.bridge.addSyncFolder(JSON.stringify(value), Boolean(source));
        }, 'btn-primary'));
        byId('sync-folder-name').focus();
    }
    function currentSource() {
        const id = currentFolderIds[0];
        return [...data.sources, ...allApps, ...deviceItems].find(f => f.folderId === id && f.sync?.syncFolderId && !f.sync.relativePath);
    }
    function statusLabel(id) {
        const status = data.statuses.find(s => s.syncFolderId === id);
        if (!status) return text('Aguardando sincronização', 'Waiting to synchronize');
        if (!en()) return status.message;
        return ({ synced: 'Synchronized', pending: 'Waiting for stable files', scanning: 'Checking changes', syncing: 'Synchronizing...', attention: 'Decisions pending', offline: 'Waiting to retry', paused: 'Paused', unavailable: 'Folder unavailable' })[status.state] || status.state;
    }
    function manage() {
        dialog(text('Pastas deste computador', 'Folders on this computer'));
        if (!data.folders.length) { const p = document.createElement('p'); p.textContent = text('Nenhuma pasta vinculada ainda.', 'No linked folders yet.'); body.append(p); }
        data.folders.forEach(folder => {
            const section = document.createElement('section'); section.className = 'sync-binding';
            const name = document.createElement('strong'); name.textContent = folder.name;
            const path = document.createElement('small'); path.textContent = folder.localPath;
            const status = document.createElement('p'); status.textContent = statusLabel(folder.syncFolderId);
            const buttons = document.createElement('div'); buttons.className = 'sync-binding-actions';
            buttons.append(button(text('Sincronizar agora', 'Sync now'), () => window.bridge.syncNow(folder.syncFolderId)),
                button(text(folder.paused ? 'Retomar' : 'Pausar', folder.paused ? 'Resume' : 'Pause'), () => window.bridge.updateSyncFolder(JSON.stringify({ ...folder, paused: !folder.paused }))),
                button(text('Opções', 'Options'), () => configure(folder)), button(text('Remover vínculo', 'Unlink'), () => {
                    dialog(text('Remover vínculo', 'Unlink folder'));
                    const p = document.createElement('p'); p.textContent = text('Os arquivos locais e remotos serão mantidos. Apenas a sincronização deste computador será removida.', 'Local and cloud files will remain. Only synchronization on this computer will be removed.'); body.append(p);
                    actions.append(button(text('Cancelar', 'Cancel'), manage), button(text('Remover vínculo', 'Unlink'), () => { window.bridge.removeSyncFolder(folder.syncFolderId); close(); }, 'btn-danger'));
                }));
            section.append(name, path, status, buttons); body.append(section);
        });
        if (data.issues.length) {
            const title = document.createElement('h3'); title.textContent = text('Decisões pendentes', 'Pending decisions'); body.append(title);
            data.issues.forEach(issue => body.append(button(issue.relativePath, () => showIssue(issue))));
        }
        actions.append(button(text('Fechar', 'Close'), close)); actions.firstChild.focus();
    }
    function showIssue(issue) {
        seen.add(issue.id); issueShown = issue.id;
        dialog(text(issue.kind === 'conflict' ? 'Conflito de sincronização' : 'Alteração na nuvem', issue.kind === 'conflict' ? 'Synchronization conflict' : 'Cloud update'));
        const message = document.createElement('p'); message.textContent = issue.message; body.append(message);
        const path = document.createElement('strong'); path.textContent = issue.relativePath; body.append(path);
        const dates = document.createElement('p'); dates.className = 'sync-help'; const remote = issue.remote[0];
        const format = value => value ? new Date(value).toLocaleString(resolveLanguage()) : text('Não disponível', 'Unavailable');
        dates.textContent = text('Local: ', 'Local: ') + format(issue.localModifiedUtc) + '\n' + text('Nuvem: ', 'Cloud: ') + format(remote?.modifiedUtc) + ' (' + (remote?.deviceName || text('outro dispositivo', 'another device')) + ')'; body.append(dates);
        if (issue.kind === 'unavailable') { actions.append(button(text('Ver depois', 'Later'), close)); return; }
        const remember = field(text('Fazer isso automaticamente para esta pasta no futuro', 'Do this automatically for this folder in the future'), input('sync-remember', false, 'checkbox'));
        if (issue.kind === 'conflict' || remote?.state === 'deleted') remember.parentElement.querySelector('span').textContent = text('Em conflitos futuros, manter ambas as versões', 'Keep both versions in future conflicts');
        const resolve = choice => {
            window.bridge.resolveSyncConflict(issue.syncFolderId, issue.id, choice,
                remember.checked && ((choice === 'cloud' && issue.kind === 'remoteUpdate' && remote?.state !== 'deleted') || choice === 'both')); close();
        };
        actions.append(button(text('Ver depois', 'Later'), close), button(text('Manter local', 'Keep local'), () => resolve('local')),
            button(text('Manter ambas', 'Keep both'), () => resolve('both')),
            button(remote?.state === 'deleted' ? text('Confirmar exclusão', 'Confirm deletion') : text('Usar versão da nuvem', 'Use cloud version'), () => resolve('cloud'), 'btn-primary'));
        const batch = data.issues.filter(i => i.syncFolderId === issue.syncFolderId && i.kind === 'remoteUpdate' && i.remote.length === 1 && i.remote[0].state !== 'deleted');
        if (issue.kind === 'remoteUpdate' && batch.length > 1) actions.append(button(text(`Substituir ${batch.length} versões`, `Replace ${batch.length} versions`), () => {
            batch.forEach(i => { seen.add(i.id); window.bridge.resolveSyncConflict(i.syncFolderId, i.id, 'cloud', remember.checked); }); close();
        }, 'btn-primary'));
        actions.firstChild.focus();
    }
    function paint() {
        byId('sync-actions').classList.toggle('hidden', activeLibraryView !== 'devices');
        byId('sync-add').textContent = text('Adicionar pasta sincronizada', 'Add synchronized folder');
        byId('sync-now').textContent = text('Sincronizar agora', 'Sync now');
        const count = data.issues.length ? ' (' + data.issues.length + ')' : '';
        byId('sync-manage').textContent = text('Pastas deste computador', 'Folders on this computer') + count;
        byId('sync-pause').textContent = data.paused ? text('Retomar sincronização', 'Resume synchronization') : text('Pausar sincronização', 'Pause synchronization');
        const source = currentSource();
        byId('sync-link').classList.toggle('hidden', !source || data.folders.some(f => f.syncFolderId === source.sync.syncFolderId));
        byId('sync-start-windows').checked = Boolean(data.startWithWindows);
        byId('sync-close-behavior').value = data.closeToTray ? 'tray' : 'exit';
        document.querySelectorAll('.sync-card-status').forEach(e => e.remove());
        for (const folder of data.folders) {
            const apps = [...allApps, ...deviceItems, ...data.sources].filter(a => a.sync?.syncFolderId === folder.syncFolderId && !a.sync.relativePath);
            document.querySelectorAll('.folder-card,.folder-row').forEach(card => {
                if (!apps.some(app => card.dataset.key === itemKey(app))) return;
                const status = document.createElement('small'); status.className = 'sync-card-status'; status.textContent = statusLabel(folder.syncFolderId);
                (card.querySelector('.row-name') || card).append(status);
            });
        }
    }
    const oldRender = render; render = (...args) => { oldRender(...args); paint(); };
    const oldReady = window.onBridgeReady; window.onBridgeReady = () => { oldReady(); window.bridge.loadSyncState(); };
    const originalNavigate = navigate;
    navigate = (path, addHistory = true, folderId = '') => {
        const source = [...allApps, ...deviceItems, ...data.sources].find(f => f.folderId === folderId);
        if (source?.sync?.canonicalFolderId && source.sync.canonicalFolderId !== folderId) {
            const binding = data.folders.find(f => f.syncFolderId === source.sync.syncFolderId);
            if (binding) return originalNavigate(binding.cloudPath, addHistory, binding.cloudFolderId);
            window.bridge.openSyncFolder(source.sync.canonicalFolderId); return;
        }
        return originalNavigate(path, addHistory, folderId);
    };
    window._onSyncNavigate = (path, id) => originalNavigate(path, true, id);
    window._onSyncState = state => {
        data = state; window.__syncState = state; paint();
        if (issueShown && !data.issues.some(i => i.id === issueShown)) close();
        const next = data.issues.find(i => !seen.has(i.id));
        if (next && !document.querySelector('.modal-overlay:not(.hidden)') && settingsPanel.classList.contains('hidden')) showIssue(next);
    };
    window._onSyncFolderPicked = path => {
        if (!byId('sync-local-path')) return;
        byId('sync-local-path').value = path;
        if (!byId('sync-folder-name').value) byId('sync-folder-name').value = path.replace(/[/\\]+$/, '').split(/[/\\]/).pop();
    };
    window._onSyncFolderSaved = () => close();
    window._onSyncTransfer = p => {
        if (!byId('op-' + p.id)) window._opsAdd(p.id, p.direction, p.name);
        window._opsProgress(p.id, p.percent, text('Sincronizando', 'Synchronizing'));
        if (p.status === 'completed') window._opsUpdate(p.id, 'done', text('Sincronizado', 'Synchronized'));
        else if (p.status === 'paused' || p.status === 'waiting') window._opsUpdate(p.id, 'waiting', text('Aguardando nova tentativa', 'Waiting to retry'));
    };
    byId('sync-add').addEventListener('click', () => configure());
    byId('sync-link').addEventListener('click', () => configure(null, currentSource()));
    byId('sync-now').addEventListener('click', () => window.bridge.syncNow(currentSource()?.sync?.syncFolderId || ''));
    byId('sync-pause').addEventListener('click', () => window.bridge.pauseSync(!data.paused));
    byId('sync-manage').addEventListener('click', manage);
    byId('sidebar-devices').addEventListener('click', () => { window.bridge.loadSyncState(); window.bridge.discoverSyncFolders(); });
    const savePreferences = () => window.bridge.setSyncPreferences(byId('sync-start-windows').checked, byId('sync-close-behavior').value === 'tray');
    byId('sync-start-windows').addEventListener('change', savePreferences);
    byId('sync-close-behavior').addEventListener('change', savePreferences);
    paint();
})();
