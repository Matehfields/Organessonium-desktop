'use strict';

Object.assign(I18N['pt-BR'], {
    makeCopy:'Fazer cópia', viewInformation:'Ver informações', versions:'Versões', makeDownload:'Fazer o download',
    addStarred:'Adicionar à pasta "Com estrela"', removeStarred:'Remover da pasta "Com estrela"',
    fileInformation:'Informações do arquivo', detailsLoading:'Carregando informações...', detailsPending:'Carregando...',
    detailsType:'Tipo', detailsLocation:'Localização', detailsSize:'Tamanho do arquivo', detailsStorage:'Tamanho utilizado',
    detailsUploaded:'Data e hora de upload', detailsModified:'Última modificação', detailsChunks:'Quantidade de chunks',
    detailsCurrent:'Versão atual', detailsNoVersions:'Este arquivo não tem versões anteriores.',
    detailsUnknown:'Não informado', detailsFolder:'Pasta', detailsImage:'Imagem', detailsVideo:'Vídeo',
    detailsAudio:'Áudio', detailsPdf:'PDF', detailsArchive:'Compactado', detailsMarkdown:'Markdown',
    detailsDocument:'Documento', detailsSpreadsheet:'Planilha', detailsPresentation:'Apresentação',
    detailsCode:'Código', detailsGeneric:'Arquivo'
});
Object.assign(I18N.en, {
    makeCopy:'Make a copy', viewInformation:'View information', versions:'Versions', makeDownload:'Download',
    addStarred:'Add to "Starred"', removeStarred:'Remove from "Starred"',
    fileInformation:'File information', detailsLoading:'Loading information...', detailsPending:'Loading...',
    detailsType:'Type', detailsLocation:'Location', detailsSize:'File size', detailsStorage:'Storage used',
    detailsUploaded:'Upload date and time', detailsModified:'Last modified', detailsChunks:'Chunk count',
    detailsCurrent:'Current version', detailsNoVersions:'This file has no previous versions.',
    detailsUnknown:'Not available', detailsFolder:'Folder', detailsImage:'Image', detailsVideo:'Video',
    detailsAudio:'Audio', detailsPdf:'PDF', detailsArchive:'Archive', detailsMarkdown:'Markdown',
    detailsDocument:'Document', detailsSpreadsheet:'Spreadsheet', detailsPresentation:'Presentation',
    detailsCode:'Code', detailsGeneric:'File'
});

const fileInformationRequests = new Map();
let fileInformationSequence = 0;
let fileDetailsRequest = '';
let fileDetailsFocus = null;

function requestFileInformation(app, callback, onError = () => {}) {
    const id = 'file-info-' + (++fileInformationSequence);
    fileInformationRequests.set(id, { callback, onError });
    window.bridge.requestFileInformation(app.metadataMessageId, id);
    return id;
}

window._onFileInformation = (id, value) => {
    const request = fileInformationRequests.get(id);
    fileInformationRequests.delete(id);
    if (request) request.callback(typeof value === 'string' ? JSON.parse(value) : value);
};
window._onFileInformationError = (id, message) => {
    const request = fileInformationRequests.get(id);
    fileInformationRequests.delete(id);
    if (request) request.onError(message);
};
window._onFileVersionPublished = threadId => {
    if (window._thumbs) delete window._thumbs[threadId];
    allApps.concat(specialItems, deviceItems).filter(app => fileRequestId(app) === threadId).forEach(app => { app.versions = []; });
};

function ensureFileDetailsDialog() {
    if ($('file-details-overlay')) return;
    const overlay = document.createElement('div');
    overlay.id = 'file-details-overlay';
    overlay.className = 'modal-overlay hidden';
    overlay.innerHTML = `<section class="modal-box file-details-dialog" role="dialog" aria-modal="true" aria-labelledby="file-details-title">
        <header><h3 id="file-details-title"></h3><button id="file-details-close" class="btn-icon" type="button">${icon('close',20)}</button></header>
        <div id="file-details-name"></div><div id="file-details-status" class="file-details-status" role="status"></div>
        <div id="file-details-content" aria-live="polite"></div></section>`;
    document.body.appendChild(overlay);
    $('file-details-close').addEventListener('click', closeFileDetails);
    overlay.addEventListener('click', event => { if (event.target === overlay) closeFileDetails(); });
    overlay.addEventListener('keydown', event => {
        if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); closeFileDetails(); }
        if (event.key !== 'Tab') return;
        const buttons = [...overlay.querySelectorAll('button')];
        const first = buttons[0], last = buttons[buttons.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    });
}

function openFileDetails(app, versions) {
    ensureFileDetailsDialog();
    fileDetailsFocus = document.activeElement;
    $('file-details-title').textContent = t(versions ? 'versions' : 'fileInformation');
    $('file-details-name').textContent = app.name;
    $('file-details-close').setAttribute('aria-label', t('close'));
    if (versions) $('file-details-content').textContent = '';
    else renderFileInformation({entry:app}, true);
    setFileDetailsLoading(true);
    $('file-details-overlay').classList.remove('hidden');
    $('file-details-close').focus();
    fileDetailsRequest = requestFileInformation(app, info => {
        if (fileDetailsRequest !== id) return;
        setFileDetailsLoading(false);
        $('file-details-name').textContent = info.entry.name;
        if (versions) renderFileVersions(info.entry);
        else renderFileInformation(info);
    }, message => {
        if (fileDetailsRequest === id) {
            setFileDetailsLoading(false);
            $('file-details-content').textContent = message;
        }
    });
    const id = fileDetailsRequest;
}

function closeFileDetails() {
    const overlay = $('file-details-overlay');
    if (!overlay || overlay.classList.contains('hidden')) return false;
    overlay.classList.add('hidden');
    setFileDetailsLoading(false);
    fileDetailsRequest = '';
    fileDetailsFocus?.focus();
    return true;
}

function fileInformationDate(value) {
    const date = new Date(value);
    return value && Number.isFinite(date.getTime()) ? date.toLocaleString(resolveLanguage()) : t('detailsUnknown');
}

function setFileDetailsLoading(loading) {
    $('file-details-content').setAttribute('aria-busy', String(loading));
    const status = $('file-details-status');
    status.classList.toggle('loading', loading);
    status.innerHTML = loading ? `<span class="file-details-spinner" aria-hidden="true"></span><span>${esc(t('detailsLoading'))}</span>` : '';
}

function renderFileInformation(info, loading = false) {
    const app = info.entry;
    const category = isFolderApp(app) ? 'folder' : fileType(app.name);
    const label = 'details' + category[0].toUpperCase() + category.slice(1);
    const rows = [
        [t('detailsType'), t(label) + (app.mimeType ? ' (' + app.mimeType + ')' : '')],
        [t('detailsLocation'), app.path || '/'],
        [t('detailsSize'), isFolderApp(app) ? t('detailsUnknown') : fmtSize(app.sizeBytes)],
        [t('detailsStorage'), isFolderApp(app) ? t('detailsUnknown') : fmtSize(info.storageBytes)],
        [t('detailsUploaded'), fileInformationDate(app.uploadedAt)],
        [t('detailsModified'), fileInformationDate(app.modifiedAt || app.uploadedAt)],
        ['threadId', app.threadId || t('detailsUnknown')],
        ['messageId', app.metadataMessageId || t('detailsUnknown')],
        [t('detailsChunks'), String(info.chunkCount)]
    ];
    $('file-details-content').innerHTML = '<dl class="file-information">' + rows.map(([key,value]) =>
        `<div><dt>${esc(key)}</dt><dd${loading ? ' class="file-details-pending"' : ''}>${esc(loading ? t('detailsPending') : value)}</dd></div>`).join('') + '</dl>';
}

function renderFileVersions(app) {
    const history = (app.versions || []).slice().reverse();
    const rows = [{ current:true, payload:app }, ...history];
    $('file-details-content').innerHTML = '<div class="file-version-list">' + rows.map((version,index) => {
        const payload = version.payload;
        return `<div class="file-version-row"><div><strong>${esc(version.current ? t('detailsCurrent') : payload.name)}</strong>
            <small>${esc(fileInformationDate(payload.versionUploadedAt || payload.uploadedAt))} · ${esc(fmtSize(payload.sizeBytes))}</small></div>
            <button class="btn-secondary" data-version-index="${index}" type="button">${icon('download',18)} ${esc(t('makeDownload'))}</button></div>`;
    }).join('') + '</div>' + (history.length ? '' : `<p>${esc(t('detailsNoVersions'))}</p>`);
    $('file-details-content').querySelectorAll('[data-version-index]').forEach(button => button.addEventListener('click', () => {
        const version = rows[Number(button.dataset.versionIndex)];
        if (version.current) window.bridge.startDownload(app.threadId, app.name);
        else window.bridge.downloadVersion(app.metadataMessageId, version.versionId);
    }));
}
