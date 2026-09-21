'use strict';

// ─── Estado ───────────────────────────────────────────────────────────────────
let allApps = [], viewMode = 'grid', sortMode = 'name-asc';
let currentPath = '/', searchQuery = '', typeFilter = 'all', dateFilter = 'all';
let activeLibraryView = 'files', specialItems = [];
let serverReadDepth = 0;
let ctxTarget = null;
let customFolderColorTarget = null;
let ctxMode = 'item', ctxTabTarget = null;
let historyBack = [], historyForward = [];
let confirmCb = null;
let uploadConflictState = null;
let settings = {
    cardSize:'medium', downloadThreads:4, uploadThreads:4, previewThreads:4, maxParallelUploads:3, theme:'system', downloadPath:'', tempPath:'', dataPath:'',
    previewEnabled:true, videoPreviewQuality:'low', language:'system',
    animationsEnabled:true, hardwareAcceleration:true, encryptionMode:'standard', customKeyAvailable:false,
    proxy:{ proxyMode:'automatic', proxyHost:'', proxyPort:0, proxyProtocol:'auto', proxyAuthenticationEnabled:false, proxyUsername:'', proxyPassword:'' }
};
let systemPreferences = { theme:detectBrowserTheme(), language:getInitialLanguage() };

let previewFiles  = [], previewIndex = 0, previewAppRef = null;
let moveTarget    = null, moveSelectedPath = '/';
let selectedKeys = new Set();
let dragMoveKeys = [];
let itemDragState = null, dragSuppressClickUntil = 0;
let workspaceTabs = null, editorArea = null, activeWorkspaceTab = 'files';
let markdownTabs = new Map(), pendingMarkdownEditorApp = null;
let editorLinkState = null, editorImageState = null;
let pendingPastedMarkdownImages = new Map();
let renameTarget = null;
let filesNeedRefresh = false;
let marqueeState = null, marqueeEl = null;
let settingsSaveTimer = 0;
let smoothScrollFrame = 0, smoothScrollVelocity = 0;
let cardPreviewFrame = 0;
let thumbnailRequestTimer = 0;
let thumbnailObserver = null;
let visibleThumbnailIds = new Set();
let folderLoadingPath = '';
let folderStatisticsTimer = 0;
let folderFileCounts = new Map();
let libraryFileCount = null;
let libraryFolderCount = null;
let libraryTotalBytes = null;
let sidebarCollapsed = false;
let nativeTitleBarMetrics = { enabled:false, leftInset:0, rightInset:0, height:48 };
let nativeTitleBarDragTimer = 0;
let requestedFolderStatistics = new Map();
let activeImgWrap = null; // referência ao wrap do zoom atual
let activeVideoResizeCleanup = null;
let pdfPreviewState = null;
let pdfJsModulePromise = null;
const pdfJsSelectableLayers = new Map();
let pdfJsSelectionEventsBound = false;
let pdfJsSelectionPointerDown = false;
let pdfJsPreviousSelectionRange = null;
let pendingVideoQualitySwitch = null;
const transferRateStates = new Map();

const PDF_DEFAULT_BATCH_SIZE = 10;

const CARD_SIZES = { small:'130px', medium:'180px', large:'240px' };
const DEFAULT_FOLDER_COLOR = '#C4C7C5';
const FOLDER_COLORS = [DEFAULT_FOLDER_COLOR,'#F28B82','#FDD663','#81C995','#C58AF9','#FFB74D'];
const SORT_LABEL_KEYS = { 'name-asc':'sortName','name-desc':'sortName','size-desc':'sortSize','size-asc':'sortSize','date-desc':'sortDate','date-asc':'sortDate' };
const TYPE_COLOR  = {
    folder:DEFAULT_FOLDER_COLOR, image:'#E25B53', video:'#9C5CCF', audio:'#3F9B63',
    pdf:'#E35D6A', document:'#1F9FCB', spreadsheet:'#2E9D4D',
    presentation:'#D98224', archive:'#8D6E63', code:'#1E88E5', generic:'#6F7479'
};

const I18N = {
    'pt-BR': {
        appTitle:'Organessônium',
        searchPlaceholder:'Pesquisar em Organessônium...',
        back:'Voltar', forward:'Avançar', gridView:'Grade', listView:'Lista', refresh:'Atualizar', newFolder:'Nova pasta', newMarkdown:'Novo arquivo Markdown', newMarkdownDefault:'Novo documento.md',
        uploadFolder:'Enviar pasta', upload:'Enviar', settings:'Configurações',
        sidebarNew:'Novo', sidebarFiles:'Arquivos', sidebarFavorites:'Favoritos', sidebarDevices:'Dispositivos', sidebarTransfers:'Transferências', sidebarStorage:'Armazenamento no servidor',
        sidebarItems:'Itens no servidor', sidebarItemSuffix:'itens', sidebarFolders:'Pastas', sidebarCollapse:'Recolher barra lateral', sidebarExpand:'Expandir barra lateral',
        sortName:'Nome', sortSize:'Tamanho', sortDate:'Data',
        filterType:'Tipo', filterModified:'Modificado', filterAll:'Todos', filterFolders:'Pastas', filterImages:'Imagens', filterVideos:'Vídeos', filterAudio:'Áudio', filterDocuments:'Documentos', filterArchives:'Arquivos compactados', filterOther:'Outros',
        filterAnyDate:'Qualquer data', filterToday:'Hoje', filterLast7Days:'Últimos 7 dias', filterLast30Days:'Últimos 30 dias',
        sortNameAsc:'Nome A-Z', sortNameDesc:'Nome Z-A', sortSizeDesc:'Maior tamanho',
        sortSizeAsc:'Menor tamanho', sortDateDesc:'Mais recente', sortDateAsc:'Mais antigo',
        loading:'Carregando...', validatingServer:'Verificando alterações no servidor...', backgroundInProgress:'Em andamento...', creatingFolder:'Criando pasta...', waiting:'Aguardando...', transfers:'Transferências',
        filesFound:v => v.count + ' arquivo(s) encontrado(s)...',
        uploadProgress:'Enviando', downloadProgress:'Baixando', deleteProgress:'Apagando', moveProgress:'Movendo',
        cancelTransfersTitle:'Cancelar transferência?',
        cancelTransfersMessage:v => v.count === 1
            ? 'A transferência em andamento será cancelada.'
            : v.count + ' transferências em andamento serão canceladas.',
        cancelTransfers:'Cancelar transferência',
        transfersCancelled:'Cancelando transferência...',
        interruptedTransfersTitle:'Transferências interrompidas',
        interruptedTransfersMessage:v => 'Há ' + v.count + ' transferência(s) interrompida(s). Deseja retomar agora?',
        resumeTransfers:'Retomar',
        errorPrefix:'Erro: ',
        selectedCount:v => v.count + (v.count === 1 ? ' selecionado' : ' selecionados'),
        download:'Baixar', downloadFolderAction:'Baixar pasta', move:'Mover', delete:'Apagar', clearSelection:'Limpar seleção',
        open:'Abrir', preview:'Prévia', uploadFiles:'Enviar arquivos', closeTab:'Fechar guia', closeOtherTabs:'Fechar outras guias',
        switchToGrid:'Ver em grade', switchToList:'Ver em lista',
        uploadConflictTitle:'Arquivo já existe',
        uploadConflictMessage:v => '"' + v.name + '" já existe em ' + v.path + '.',
        uploadConflictDetails:v => {
            const total = (v.existing || 0) + (v.batch || 0);
            if (v.batch > 0 && v.existing > 0) return 'Já existe no app e também nesta seleção de upload.';
            if (v.batch > 0) return 'Outro arquivo desta seleção tem o mesmo nome e destino.';
            return total > 1 ? total + ' arquivos existentes usam esse nome.' : 'Já existe um arquivo com esse nome.';
        },
        uploadConflictOverwrite:'Gravar por cima',
        uploadConflictKeepBoth:'Manter os dois',
        uploadConflictSkip:'Pular',
        uploadConflictApplyAll:'Fazer isso para todos os conflitos deste upload',
        rename:'Renomear', addFavorite:'Adicionar aos favoritos', removeFavorite:'Remover dos favoritos',
        deleteFilesTitle:'Apagar arquivos?', deleteFilesMessage:v => v.count + ' item(ns) serão removidos permanentemente.',
        deleteFolderTitle:'Apagar pasta?', deleteFolderMessage:v => '"' + v.name + '" e todos os arquivos dentro dela serão removidos permanentemente.',
        deleting:'Apagando...', options:'Opções', openFolder:v => 'Abrir pasta ' + v.name,
        openPreview:v => 'Selecionar arquivo ' + v.name,
        settingsTitle:'Configurações', appearance:'Aparência', language:'Idioma',
        theme:'Tema', system:'Sistema', dark:'Escuro', light:'Claro', cardSize:'Tamanho dos cards',
        small:'Pequeno', medium:'Médio', large:'Grande', previews:'Prévias',
        videoPreviewQuality:'Qualidade do vídeo', videoPreviewLow:'480p', videoPreviewOriginal:'Original',
        videoPreviewQualityHelp:'Usa a prévia 480p para carregar vídeos mais rápido.',
        cache:'Cache', clearCache:'Limpar cache', clearCacheHelp:'Limpa prévias e imagens salvas localmente.',
        cacheSize:v => 'Uso atual: ' + v.size,
        cacheCleared:'Cache limpo.',
        performance:'Desempenho', enableAnimations:'Ativar animações',
        hardwareAcceleration:'Aceleração por hardware', requiresRestart:'Reinicie o app para aplicar.',
        encryption:'Proteção dos arquivos', encryptionMode:'Modo de proteção', encryptionStandard:'Padrão', encryptionCustom:'Chave personalizada',
        encryptionStandardHelp:'Gerenciada automaticamente pelo Organessônium.', encryptionPassphrase:'Senha da chave personalizada',
        encryptionWarning:'Arquivos protegidos com uma chave personalizada só poderão ser recuperados com essa chave. Guarde o arquivo de recuperação em local seguro.',
        encryptionConfirm:'Entendo que perder a chave impede a recuperação dos arquivos.', encryptionActivate:'Ativar chave personalizada',
        encryptionExport:'Exportar chave de recuperação', encryptionImport:'Importar chave', encryptionReady:'Chave personalizada disponível neste dispositivo.',
        encryptionNotReady:'Configure ou importe uma chave personalizada.', encryptionExportTitle:'Exportar chave de recuperação?',
        encryptionExportWarning:'Qualquer pessoa que possuir este arquivo poderá acessar os arquivos protegidos por esta chave.',
        encryptionImportTitle:'Importar chave de recuperação?', encryptionImportWarning:'Importe somente um arquivo de recuperação confiável.',
        encryptionUpdated:'Proteção dos novos arquivos atualizada.', encryptionProceed:'Continuar',
        enablePreviews:'Ativar prévias', simultaneousPreviews:'Prévias simultâneas', uploads:'Uploads', simultaneousUploadFiles:'Arquivos simultâneos', simultaneousUploadParts:'Partes simultâneas',
        uploadingItems:v => v.count === 1 ? 'Enviando 1 item' : 'Enviando ' + v.count + ' itens', startingUploads:'Iniciando uploads...', queuedUpload:'Na fila', cancelUpload:'Cancelar',
        sendingProgress:v => 'Enviando ' + v.done + '/' + v.total + ' (' + v.speed + ')',
        downloadingProgress:v => 'Baixando ' + v.done + '/' + v.total + ' (' + v.speed + ')',
        downloadEta:v => 'Conclusão estimada: ' + v.eta,
        downloads:'Downloads', downloadFolder:'Pasta de downloads',
        tempFolder:'Pasta temporária', dataFolder:'Pasta de dados', dataFolderHelp:'Usada na próxima inicialização. Os dados atuais são copiados sem apagar nada.', choose:'Escolher', connection:'Conexão',
        network:'Rede e proxy', proxyMode:'Modo de conexão', proxyModeAuto:'Automático (usar configuração do sistema)',
        proxyModeDisabled:'Desativado (conexão direta)', proxyModeManual:'Manual',
        proxyModeHelp:'O proxy é usado para falar com o Discord. Mude aqui se estiver atrás de um firewall ou rede corporativa.',
        proxyHost:'Endereço do proxy', proxyPort:'Porta', proxyProtocol:'Protocolo',
        proxyProtocolAuto:'Detectar automaticamente', proxyProtocolHttp:'HTTP', proxyProtocolHttps:'HTTPS (CONNECT)',
        proxyProtocolSocks5:'SOCKS5', proxyProtocolSocks4a:'SOCKS4a', proxyProtocolSocks4:'SOCKS4',
        proxyAuth:'Requer autenticação', proxyUser:'Usuário', proxyPass:'Senha',
        proxyTest:'Testar conexão com o proxy', proxyTesting:'Testando conexão com o proxy...',
        proxyTestSuccess:'Conectado com sucesso.',
        proxyTestDetected:'Conectado com sucesso (protocolo detectado: {protocol}).',
        proxyTestFail:'Falha: {message}',
        reconnect:'Reconfigurar bot', saveSettings:'Salvar configurações',
        logout:'Sair', logoutLabel:'Sair do bot', logoutHelp:'Remove o token, os IDs e os dados locais. O WebView2 e os logs são mantidos.',
        logoutTitle:'Sair do Organessônium?', logoutMessage:'O token, os IDs e os dados locais serão removidos deste computador. O WebView2 e os logs serão mantidos.',
        settingsSaved:'Configurações salvas.',
        folderTitle:'Nova pasta', folderName:'Nome da pasta', cancel:'Cancelar', create:'Criar',
        customFolderColor:'Cor personalizada', colorHex:'Hexadecimal', colorRgb:'RGB', colorApply:'Aplicar',
        moveTo:'Mover para...', moveHere:'Mover aqui',
        renameTitle:'Renomear', renameName:'Nome',
        emptyTitle:'Nenhum arquivo aqui', emptyHelp:'Arraste arquivos ou clique em "Enviar"',
        loaded:v => 'OK ' + v.count + ' arquivo(s).', storageUsed:v => v.size + ' utilizados',
        zoomOut:'Diminuir zoom', zoomIn:'Aumentar zoom', fitToScreen:'Ajustar a tela', close:'Fechar',
        previewErrorTitle:'Não foi possível carregar a prévia',
        previewUnavailable:'Prévia não disponível', unsupportedType:'Este arquivo não tem uma prévia disponível.',
        mediaSafeTitle:'Reproduzir mídia',
        mediaSafeMessage:'Se o formato não for compatível com o WebView2, abra no player do sistema.',
        mediaLoadError:'Não foi possível reproduzir este formato dentro do app.',
        openExternal:'Abrir externamente',
        quality:'Qualidade', preview480:'480p', fullQuality:'Completa', previewDownloading:'Baixando prévia...',
        previewCache:'Carregando do cache',
        previewLocating:'Localizando partes da prévia...', previewDownloadGeneric:'Baixando arquivo para a prévia',
        previewDownload480:'Baixando prévia 480p', previewDownloadFull:'Baixando qualidade completa',
        pdfLoading:'Carregando páginas...', pdfProgress:v => 'Carregando página ' + v.current + ' de ' + v.total,
        pdfLoaded:v => 'Carregadas ' + v.current + ' de ' + v.total + ' páginas',
        filesTab:'Arquivos', markdownEditor:'Editor Markdown', markdownPreview:'Prévia Markdown', markdownSeeMore:'Ver mais no editor',
        editorBold:'Negrito', editorItalic:'Itálico', editorHeading:'Título',
        editorList:'Lista', editorOrderedList:'Lista numerada', editorQuote:'Citação',
        editorCode:'Código', editorLink:'Link', editorImage:'Imagem', editorReady:'Editando Markdown',
        editorDocument:'Documento', editorFormat:'Formatar', editorInsert:'Inserir',
        editorView:'Visualizar', editorRaw:'Markdown puro', editorRich:'Editor visual',
        editorZoom:'Zoom', editorZoomReset:'Redefinir zoom',
        editorTable:'Tabela',
        editorFind:'Buscar', editorFindPlaceholder:'Buscar no documento...', editorFindNoResults:'Sem resultados',
        editorReplace:'Buscar e substituir', editorReplacePlaceholder:'Substituir por...',
        editorReplaceCurrent:'Substituir', editorReplaceAll:'Substituir tudo',
        editorAutosave:'Salvamento automático', editorSaving:'Salvando...', editorSaved:'Salvo agora',
        editorUnsaved:'Alterações pendentes', editorSaveError:'Falha ao salvar',
        editorInsertLink:'Inserir link', editorLinkText:'Texto', editorLinkUrl:'URL', editorInsert:'Inserir',
        editorInsertImage:'Inserir imagem', editorImageLocal:'PC', editorImageDrive:'Drive', editorImageUrl:'URL',
        editorImageLocalHelp:'Escolha uma imagem local para anexar ao mesmo post do Markdown.',
        editorImageDriveHelp:'Escolha uma imagem já salva no app.', editorImageUrlLabel:'URL da imagem',
        editorImageAlt:'Texto alternativo', editorChooseImage:'Escolher imagem',
        editorNoImages:'Nenhuma imagem salva no app.', editorUploadingImage:'Enviando imagem...',
        editorPasteImage:'Colando imagem...',
        archiveTitle:'Arquivo compactado', archiveTooLarge:v => 'Arquivo grande demais para listar aqui (' + v.size + ').',
        archiveUnsupported:'Este formato compactado não pode ser listado nesta prévia.',
        archiveEmpty:'Nenhum item encontrado no arquivo.',
        archiveItems:v => v.count + ' item(ns)',
        archiveShowing:v => 'Mostrando ' + v.shown + ' de ' + v.total + ' item(ns).'
    },
    en: {
        appTitle:'Organessônium',
        searchPlaceholder:'Search in Organessônium...',
        back:'Back', forward:'Forward', gridView:'Grid view', listView:'List view', refresh:'Refresh', newFolder:'New folder', newMarkdown:'New Markdown file', newMarkdownDefault:'Untitled document.md',
        uploadFolder:'Upload folder', upload:'Upload', settings:'Settings',
        sidebarNew:'New', sidebarFiles:'Files', sidebarFavorites:'Favorites', sidebarDevices:'Devices', sidebarTransfers:'Transfers', sidebarStorage:'Server storage',
        sidebarItems:'Items on server', sidebarItemSuffix:'items', sidebarFolders:'Folders', sidebarCollapse:'Collapse sidebar', sidebarExpand:'Expand sidebar',
        sortName:'Name', sortSize:'Size', sortDate:'Date',
        filterType:'Type', filterModified:'Modified', filterAll:'All', filterFolders:'Folders', filterImages:'Images', filterVideos:'Videos', filterAudio:'Audio', filterDocuments:'Documents', filterArchives:'Archives', filterOther:'Other',
        filterAnyDate:'Any time', filterToday:'Today', filterLast7Days:'Last 7 days', filterLast30Days:'Last 30 days',
        sortNameAsc:'Name A-Z', sortNameDesc:'Name Z-A', sortSizeDesc:'Largest',
        sortSizeAsc:'Smallest', sortDateDesc:'Newest', sortDateAsc:'Oldest',
        loading:'Loading...', validatingServer:'Checking for server changes...', backgroundInProgress:'In progress...', creatingFolder:'Creating folder...', waiting:'Waiting...', transfers:'Transfers',
        filesFound:v => v.count + ' file(s) found...',
        uploadProgress:'Uploading', downloadProgress:'Downloading', deleteProgress:'Deleting', moveProgress:'Moving',
        cancelTransfersTitle:'Cancel transfer?',
        cancelTransfersMessage:v => v.count === 1
            ? 'The running transfer will be canceled.'
            : v.count + ' running transfers will be canceled.',
        cancelTransfers:'Cancel transfer',
        transfersCancelled:'Canceling transfer...',
        interruptedTransfersTitle:'Interrupted transfers',
        interruptedTransfersMessage:v => 'There are ' + v.count + ' interrupted transfer(s). Resume now?',
        resumeTransfers:'Resume',
        errorPrefix:'Error: ',
        selectedCount:v => v.count + (v.count === 1 ? ' selected' : ' selected'),
        download:'Download', downloadFolderAction:'Download folder', move:'Move', delete:'Delete', clearSelection:'Clear selection',
        open:'Open', preview:'Preview', uploadFiles:'Upload files', closeTab:'Close tab', closeOtherTabs:'Close other tabs',
        switchToGrid:'View as grid', switchToList:'View as list',
        uploadConflictTitle:'File already exists',
        uploadConflictMessage:v => '"' + v.name + '" already exists in ' + v.path + '.',
        uploadConflictDetails:v => {
            const total = (v.existing || 0) + (v.batch || 0);
            if (v.batch > 0 && v.existing > 0) return 'It already exists in the app and also in this upload selection.';
            if (v.batch > 0) return 'Another file in this upload selection has the same name and destination.';
            return total > 1 ? total + ' existing files use this name.' : 'A file with this name already exists.';
        },
        uploadConflictOverwrite:'Overwrite',
        uploadConflictKeepBoth:'Keep both',
        uploadConflictSkip:'Skip',
        uploadConflictApplyAll:'Do this for every conflict in this upload',
        rename:'Rename', addFavorite:'Add to favorites', removeFavorite:'Remove from favorites',
        deleteFilesTitle:'Delete files?', deleteFilesMessage:v => v.count + ' item(s) will be permanently removed.',
        deleteFolderTitle:'Delete folder?', deleteFolderMessage:v => '"' + v.name + '" and every file inside it will be permanently removed.',
        deleting:'Deleting...', options:'Options', openFolder:v => 'Open folder ' + v.name,
        openPreview:v => 'Select file ' + v.name,
        settingsTitle:'Settings', appearance:'Appearance', language:'Language',
        theme:'Theme', system:'System', dark:'Dark', light:'Light', cardSize:'Card size',
        small:'Small', medium:'Medium', large:'Large', previews:'Previews',
        videoPreviewQuality:'Video quality', videoPreviewLow:'480p', videoPreviewOriginal:'Original',
        videoPreviewQualityHelp:'Uses the 480p preview to load videos faster.',
        cache:'Cache', clearCache:'Clear cache', clearCacheHelp:'Clears locally saved previews and images.',
        cacheSize:v => 'Current usage: ' + v.size,
        cacheCleared:'Cache cleared.',
        performance:'Performance', enableAnimations:'Enable animations',
        hardwareAcceleration:'Hardware acceleration', requiresRestart:'Restart the app to apply.',
        encryption:'File protection', encryptionMode:'Protection mode', encryptionStandard:'Standard', encryptionCustom:'Custom key',
        encryptionStandardHelp:'Managed automatically by Organessônium.', encryptionPassphrase:'Custom key passphrase',
        encryptionWarning:'Files protected with a custom key can only be recovered with that key. Store the recovery file safely.',
        encryptionConfirm:'I understand that losing the key prevents file recovery.', encryptionActivate:'Activate custom key',
        encryptionExport:'Export recovery key', encryptionImport:'Import key', encryptionReady:'A custom key is available on this device.',
        encryptionNotReady:'Configure or import a custom key.', encryptionExportTitle:'Export recovery key?',
        encryptionExportWarning:'Anyone with this file can access files protected by this key.',
        encryptionImportTitle:'Import recovery key?', encryptionImportWarning:'Import only a trusted recovery file.',
        encryptionUpdated:'New file protection updated.', encryptionProceed:'Continue',
        enablePreviews:'Enable previews', simultaneousPreviews:'Simultaneous previews', uploads:'Uploads', simultaneousUploadFiles:'Simultaneous files', simultaneousUploadParts:'Simultaneous parts',
        uploadingItems:v => v.count === 1 ? 'Uploading 1 item' : 'Uploading ' + v.count + ' items', startingUploads:'Starting uploads...', queuedUpload:'Queued', cancelUpload:'Cancel',
        sendingProgress:v => 'Uploading ' + v.done + '/' + v.total + ' (' + v.speed + ')',
        downloadingProgress:v => 'Downloading ' + v.done + '/' + v.total + ' (' + v.speed + ')',
        downloadEta:v => 'Estimated completion: ' + v.eta,
        downloads:'Downloads', downloadFolder:'Downloads folder',
        tempFolder:'Temporary folder', dataFolder:'Data folder', dataFolderHelp:'Used on the next startup. Current data is copied without deleting anything.', choose:'Choose', connection:'Connection',
        network:'Network and proxy', proxyMode:'Connection mode', proxyModeAuto:'Automatic (use system settings)',
        proxyModeDisabled:'Disabled (direct connection)', proxyModeManual:'Manual',
        proxyModeHelp:'The proxy is used to talk to Discord. Change this if you are behind a firewall or corporate network.',
        proxyHost:'Proxy address', proxyPort:'Port', proxyProtocol:'Protocol',
        proxyProtocolAuto:'Detect automatically', proxyProtocolHttp:'HTTP', proxyProtocolHttps:'HTTPS (CONNECT)',
        proxyProtocolSocks5:'SOCKS5', proxyProtocolSocks4a:'SOCKS4a', proxyProtocolSocks4:'SOCKS4',
        proxyAuth:'Requires authentication', proxyUser:'Username', proxyPass:'Password',
        proxyTest:'Test proxy connection', proxyTesting:'Testing proxy connection...',
        proxyTestSuccess:'Connected successfully.',
        proxyTestDetected:'Connected successfully (detected protocol: {protocol}).',
        proxyTestFail:'Failed: {message}',
        reconnect:'Reconnect bot', saveSettings:'Save settings',
        logout:'Sign out', logoutLabel:'Sign out of the bot', logoutHelp:'Removes the token, IDs, and local app data. WebView2 and logs are kept.',
        logoutTitle:'Sign out of Organessônium?', logoutMessage:'The token, IDs, and local app data will be removed from this computer. WebView2 and logs will be kept.',
        settingsSaved:'Settings saved.',
        folderTitle:'New folder', folderName:'Folder name', cancel:'Cancel', create:'Create',
        customFolderColor:'Custom color', colorHex:'Hexadecimal', colorRgb:'RGB', colorApply:'Apply',
        moveTo:'Move to...', moveHere:'Move here',
        renameTitle:'Rename', renameName:'Name',
        emptyTitle:'No files here', emptyHelp:'Drop files or click "Upload"',
        loaded:v => 'OK ' + v.count + ' file(s).', storageUsed:v => v.size + ' used',
        zoomOut:'Zoom out', zoomIn:'Zoom in', fitToScreen:'Fit to screen', close:'Close',
        previewErrorTitle:'Could not load preview',
        previewUnavailable:'Preview unavailable', unsupportedType:'This file does not have an available preview.',
        mediaSafeTitle:'Play media',
        mediaSafeMessage:'If the format is not supported by WebView2, open it in the system player.',
        mediaLoadError:'This format could not be played inside the app.',
        openExternal:'Open externally',
        quality:'Quality', preview480:'480p', fullQuality:'Full', previewDownloading:'Downloading preview...',
        previewCache:'Loading from cache',
        previewLocating:'Locating preview parts...', previewDownloadGeneric:'Downloading file for preview',
        previewDownload480:'Downloading 480p preview', previewDownloadFull:'Downloading full quality',
        pdfLoading:'Loading pages...', pdfProgress:v => 'Loading page ' + v.current + ' of ' + v.total,
        pdfLoaded:v => 'Loaded ' + v.current + ' of ' + v.total + ' pages',
        filesTab:'Files', markdownEditor:'Markdown editor', markdownPreview:'Markdown preview', markdownSeeMore:'View more in editor',
        editorBold:'Bold', editorItalic:'Italic', editorHeading:'Heading',
        editorList:'List', editorOrderedList:'Numbered list', editorQuote:'Quote',
        editorCode:'Code', editorLink:'Link', editorImage:'Image', editorReady:'Editing Markdown',
        editorDocument:'Document', editorFormat:'Format', editorInsert:'Insert',
        editorView:'View', editorRaw:'Raw Markdown', editorRich:'Visual editor',
        editorZoom:'Zoom', editorZoomReset:'Reset zoom',
        editorTable:'Table',
        editorFind:'Find', editorFindPlaceholder:'Find in document...', editorFindNoResults:'No results',
        editorReplace:'Find and replace', editorReplacePlaceholder:'Replace with...',
        editorReplaceCurrent:'Replace', editorReplaceAll:'Replace all',
        editorAutosave:'Autosave', editorSaving:'Saving...', editorSaved:'Saved now',
        editorUnsaved:'Unsaved changes', editorSaveError:'Save failed',
        editorInsertLink:'Insert link', editorLinkText:'Text', editorLinkUrl:'URL', editorInsert:'Insert',
        editorInsertImage:'Insert image', editorImageLocal:'PC', editorImageDrive:'Drive', editorImageUrl:'URL',
        editorImageLocalHelp:'Choose a local image to attach to the same Markdown post.',
        editorImageDriveHelp:'Choose an image already saved in the app.', editorImageUrlLabel:'Image URL',
        editorImageAlt:'Alt text', editorChooseImage:'Choose image',
        editorNoImages:'No images saved in the app.', editorUploadingImage:'Uploading image...',
        editorPasteImage:'Pasting image...',
        archiveTitle:'Compressed file', archiveTooLarge:v => 'File is too large to list here (' + v.size + ').',
        archiveUnsupported:'This compressed format cannot be listed in the preview.',
        archiveEmpty:'No items found in this file.',
        archiveItems:v => v.count + ' item(s)',
        archiveShowing:v => 'Showing ' + v.shown + ' of ' + v.total + ' item(s).'
    }
};

const $ = id => document.getElementById(id);
const filesGrid   = $('files-grid'),   filesList      = $('files-list'),
    emptyState  = $('empty-state'),  statusText     = $('status-text'),
    statusLoader= $('status-loader'),storageDisp    = $('storage-display'),
    serverItemCount = $('server-item-count'),
    opsPanel    = $('ops-panel'),    opsList        = $('ops-list'),
    sortMenu    = $('sort-menu'),    typeFilterMenu = $('type-filter-menu'), dateFilterMenu = $('date-filter-menu'), ctxMenu = $('ctx-menu'),
    dragOverlay = $('drag-overlay'),
    settingsPanel=$('settings-panel'), settingsOverlay=$('settings-overlay'),
    folderOverlay=$('folder-overlay'), folderInput   =$('folder-name-input'),
    confirmOverlay=$('confirm-overlay'), uploadConflictOverlay=$('upload-conflict-overlay'),
    moveOverlay = $('move-overlay'), moveTree       = $('move-tree'),
    pathCrumbs  = $('path-crumbs'), currentFolderTitle = $('current-folder-title'), currentFolderName = $('current-folder-name'),
    btnBack     = $('btn-back'),     btnForward     = $('btn-forward'),
    previewOverlay=$('preview-overlay');

function getInitialLanguage() {
    const lang = String((navigator.languages && navigator.languages[0]) || navigator.language || '').toLowerCase();
    return lang.startsWith('pt') ? 'pt-BR' : 'en';
}

function detectBrowserTheme() {
    try {
        return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
    } catch (_) {
        return 'dark';
    }
}

try {
    const colorSchemeQuery = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)');
    if (colorSchemeQuery) {
        const onColorSchemeChange = () => {
            systemPreferences.theme = detectBrowserTheme();
            if (settings.theme === 'system') applyTheme('system');
        };
        if (colorSchemeQuery.addEventListener) colorSchemeQuery.addEventListener('change', onColorSchemeChange);
        else if (colorSchemeQuery.addListener) colorSchemeQuery.addListener(onColorSchemeChange);
    }
} catch (_) {}

function resolveLanguage(value = settings.language) {
    if (value === 'system') return systemPreferences.language === 'pt-BR' ? 'pt-BR' : 'en';
    return value === 'pt-BR' ? 'pt-BR' : 'en';
}

function resolveTheme(value = settings.theme) {
    if (value === 'system') return systemPreferences.theme === 'light' ? 'light' : 'dark';
    return value === 'light' ? 'light' : 'dark';
}

function languageDisplay(value = settings.language) {
    const resolved = resolveLanguage(value);
    const name = resolved === 'pt-BR' ? 'Português' : 'English';
    return value === 'system' ? t('system') + ' (' + name + ')' : name;
}

function t(key, vars = {}) {
    const lang = resolveLanguage();
    const value = (I18N[lang] && I18N[lang][key]) || I18N['pt-BR'][key] || key;
    const text = typeof value === 'function' ? value(vars) : value;
    return String(text).replace(/\{(\w+)}/g, (_, k) => vars[k] == null ? '' : vars[k]);
}

function sortLabel(mode) {
    return t(SORT_LABEL_KEYS[mode] || 'sortName');
}

const TYPE_FILTER_LABEL_KEYS = {
    all:'filterAll', folders:'filterFolders', images:'filterImages', videos:'filterVideos', audio:'filterAudio',
    documents:'filterDocuments', archives:'filterArchives', other:'filterOther'
};
const DATE_FILTER_LABEL_KEYS = { all:'filterAnyDate', today:'filterToday', '7':'filterLast7Days', '30':'filterLast30Days' };

function updateFilterLabels() {
    const typeLabel = $('type-filter-label');
    if (typeLabel) typeLabel.textContent = t(typeFilter === 'all' ? 'filterType' : (TYPE_FILTER_LABEL_KEYS[typeFilter] || 'filterType'));
    const dateLabel = $('date-filter-label');
    if (dateLabel) dateLabel.textContent = t(dateFilter === 'all' ? 'filterModified' : (DATE_FILTER_LABEL_KEYS[dateFilter] || 'filterModified'));
    document.querySelectorAll('#type-filter-menu [data-type-filter]').forEach(btn => {
        const key = TYPE_FILTER_LABEL_KEYS[btn.dataset.typeFilter];
        if (key) btn.textContent = t(key);
        btn.classList.toggle('active', btn.dataset.typeFilter === typeFilter);
    });
    document.querySelectorAll('#date-filter-menu [data-date-filter]').forEach(btn => {
        const key = DATE_FILTER_LABEL_KEYS[btn.dataset.dateFilter];
        if (key) btn.textContent = t(key);
        btn.classList.toggle('active', btn.dataset.dateFilter === dateFilter);
    });
}

function setText(id, key) {
    const el = $(id);
    if (el) el.textContent = t(key);
}

function setA11y(id, key) {
    const el = $(id);
    if (!el) return;
    const label = t(key);
    el.setAttribute('aria-label', label);
    if (!el.textContent.trim()) el.title = label;
}

function applyI18n() {
    document.documentElement.lang = resolveLanguage();
    document.title = t('appTitle');
    const langSelect = $('setting-language');
    if (langSelect) langSelect.value = settings.language;
    if ($('language-current')) $('language-current').textContent = languageDisplay();
    document.querySelectorAll('#language-menu [data-language]').forEach(btn => {
        btn.setAttribute('aria-selected', btn.dataset.language === settings.language ? 'true' : 'false');
        if (btn.dataset.language === 'system') btn.textContent = t('system');
    });
    const search = $('search-input');
    if (search) search.placeholder = t('searchPlaceholder');

    setA11y('btn-back', 'back');
    setA11y('btn-forward', 'forward');
    setA11y('btn-grid', 'gridView');
    setA11y('btn-list', 'listView');
    setA11y('btn-refresh', 'refresh');
    setA11y('btn-settings', 'settings');
    setA11y('background-activity-close', 'close');
    setText('sidebar-new-text', 'upload');
    setA11y('btn-sidebar-new', 'upload');
    setText('sidebar-new-folder-text', 'newFolder');
    setText('sidebar-new-markdown-text', 'newMarkdown');
    setText('sidebar-upload-files-text', 'uploadFiles');
    setText('sidebar-upload-folder-text', 'uploadFolder');
    setText('sidebar-files-text', 'sidebarFiles');
    setA11y('sidebar-files', 'sidebarFiles');
    setText('sidebar-favorites-text', 'sidebarFavorites');
    setA11y('sidebar-favorites', 'sidebarFavorites');
    setText('sidebar-devices-text', 'sidebarDevices');
    setA11y('sidebar-devices', 'sidebarDevices');
    setText('sidebar-storage-title', 'sidebarStorage');
    setText('server-items-label', 'sidebarItemSuffix');
    setText('sidebar-settings-text', 'settings');
    setA11y('sidebar-settings', 'settings');
    updateFilterLabels();
    $('sort-label').textContent = sortLabel(sortMode);
    setA11y('preview-zoom-out', 'zoomOut');
    setA11y('preview-zoom-in', 'zoomIn');
    setA11y('preview-zoom-reset', 'fitToScreen');
    setA11y('preview-dl', 'download');
    setA11y('preview-close', 'close');

    document.querySelectorAll('#sort-menu [data-sort]').forEach(btn => {
        const key = {
            'name-asc':'sortNameAsc', 'name-desc':'sortNameDesc',
            'size-desc':'sortSizeDesc', 'size-asc':'sortSizeAsc',
            'date-desc':'sortDateDesc', 'date-asc':'sortDateAsc'
        }[btn.dataset.sort];
        btn.textContent = t(key);
    });

    setText('sel-download-text', 'download');
    setText('sel-move-text', 'move');
    setText('sel-delete-text', 'delete');
    setText('ctx-open-text', 'open');
    setText('ctx-move-text', 'move');
    setText('ctx-rename-text', 'rename');
    setText('ctx-delete-text', 'delete');
    setText('ctx-new-folder-text', 'newFolder');
    setText('ctx-upload-text', 'uploadFiles');
    setText('ctx-upload-folder-text', 'uploadFolder');
    setText('ctx-refresh-text', 'refresh');
    setText('ctx-grid-text', 'switchToGrid');
    setText('ctx-list-text', 'switchToList');
    setText('ctx-clear-text', 'clearSelection');
    setText('ctx-settings-text', 'settings');
    setText('ctx-close-tab-text', 'closeTab');
    setText('ctx-close-others-text', 'closeOtherTabs');
    setA11y('sel-clear', 'clearSelection');
    setText('empty-title', 'emptyTitle');
    setText('empty-help', 'emptyHelp');
    setText('settings-title', 'settingsTitle');
    setText('settings-appearance-title', 'appearance');
    setText('setting-language-label', 'language');
    setText('setting-theme-label', 'theme');
    setText('theme-system-text', 'system');
    setText('theme-dark-text', 'dark');
    setText('theme-light-text', 'light');
    setText('setting-card-size-label', 'cardSize');
    setText('card-size-small-text', 'small');
    setText('card-size-medium-text', 'medium');
    setText('card-size-large-text', 'large');
    setText('settings-preview-title', 'previews');
    setText('setting-preview-label', 'enablePreviews');
    setText('setting-preview-threads-label', 'simultaneousPreviews');
    setText('setting-video-preview-quality-label', 'videoPreviewQuality');
    setText('video-preview-low-text', 'videoPreviewLow');
    setText('video-preview-original-text', 'videoPreviewOriginal');
    setText('setting-video-preview-quality-help', 'videoPreviewQualityHelp');
    setText('setting-cache-label', 'cache');
    setText('setting-cache-help', 'clearCacheHelp');
    setText('btn-clear-cache', 'clearCache');
    setText('settings-performance-title', 'performance');
    setText('setting-animations-label', 'enableAnimations');
    setText('setting-hardware-label', 'hardwareAcceleration');
    setText('setting-hardware-help', 'requiresRestart');
    setText('settings-encryption-title', 'encryption');
    setText('setting-encryption-mode-label', 'encryptionMode');
    setText('btn-encryption-standard', 'encryptionStandard');
    setText('btn-encryption-custom', 'encryptionCustom');
    setText('setting-encryption-standard-help', 'encryptionStandardHelp');
    setText('setting-custom-passphrase-label', 'encryptionPassphrase');
    setText('setting-custom-key-warning', 'encryptionWarning');
    setText('setting-custom-key-confirmed-text', 'encryptionConfirm');
    setText('btn-configure-custom-key', 'encryptionActivate');
    setText('btn-export-custom-key', 'encryptionExport');
    setText('btn-import-custom-key', 'encryptionImport');
    setText('settings-uploads-title', 'uploads');
    setText('setting-upload-files-label', 'simultaneousUploadFiles');
    setText('setting-upload-threads-label', 'simultaneousUploadParts');
    setText('settings-downloads-title', 'downloads');
    setText('setting-dlpath-label', 'downloadFolder');
    setText('setting-tmppath-label', 'tempFolder');
    setText('setting-datapath-label', 'dataFolder');
    setText('setting-datapath-help', 'dataFolderHelp');
    setText('btn-choose-dl', 'choose');
    setText('btn-choose-tmp', 'choose');
    setText('btn-choose-data', 'choose');
    setText('settings-connection-title', 'connection');
    setText('settings-network-title', 'network');
    setText('setting-proxy-mode-label', 'proxyMode');
    setText('setting-proxy-mode-help', 'proxyModeHelp');
    setText('setting-proxy-host-label', 'proxyHost');
    setText('setting-proxy-port-label', 'proxyPort');
    setText('setting-proxy-protocol-label', 'proxyProtocol');
    setText('setting-proxy-auth-label', 'proxyAuth');
    setText('setting-proxy-user-label', 'proxyUser');
    setText('setting-proxy-pass-label', 'proxyPass');
    setText('btn-test-proxy', 'proxyTest');
    const proxyModeOptions = document.querySelectorAll('#setting-proxy-mode option');
    proxyModeOptions[0].textContent = t('proxyModeAuto');
    proxyModeOptions[1].textContent = t('proxyModeDisabled');
    proxyModeOptions[2].textContent = t('proxyModeManual');
    const proxyProtocolOptions = document.querySelectorAll('#setting-proxy-protocol option');
    proxyProtocolOptions[0].textContent = t('proxyProtocolAuto');
    proxyProtocolOptions[1].textContent = t('proxyProtocolHttp');
    proxyProtocolOptions[2].textContent = t('proxyProtocolHttps');
    proxyProtocolOptions[3].textContent = t('proxyProtocolSocks5');
    proxyProtocolOptions[4].textContent = t('proxyProtocolSocks4a');
    proxyProtocolOptions[5].textContent = t('proxyProtocolSocks4');
    setText('btn-reconnect', 'reconnect');
    setText('setting-logout-label', 'logoutLabel');
    setText('setting-logout-help', 'logoutHelp');
    setText('btn-logout', 'logout');
    setText('btn-save-settings', 'saveSettings');
    setText('folder-modal-title', 'folderTitle');
    setText('folder-cancel', 'cancel');
    setText('folder-confirm', 'create');
    setText('folder-color-title', 'customFolderColor');
    setText('folder-color-hex-label', 'colorHex');
    setText('folder-color-rgb-label', 'colorRgb');
    setText('folder-color-cancel', 'cancel');
    setText('folder-color-apply', 'colorApply');
    setText('move-modal-title', 'moveTo');
    setText('move-cancel', 'cancel');
    setText('move-confirm', 'moveHere');
    setText('rename-modal-title', 'renameTitle');
    setText('rename-cancel', 'cancel');
    setText('rename-confirm', 'rename');
    setText('confirm-cancel', 'cancel');
    setText('confirm-ok', 'delete');
    setText('upload-conflict-title', 'uploadConflictTitle');
    setText('upload-conflict-skip', 'uploadConflictSkip');
    setText('upload-conflict-keep', 'uploadConflictKeepBoth');
    setText('upload-conflict-overwrite', 'uploadConflictOverwrite');
    setText('upload-conflict-apply-text', 'uploadConflictApplyAll');
    setText('editor-link-title', 'editorInsertLink');
    setText('editor-link-text-label', 'editorLinkText');
    setText('editor-link-url-label', 'editorLinkUrl');
    setText('editor-link-cancel', 'cancel');
    setText('editor-link-confirm', 'editorInsert');
    setText('editor-image-title', 'editorInsertImage');
    setText('editor-image-tab-local', 'editorImageLocal');
    setText('editor-image-tab-drive', 'editorImageDrive');
    setText('editor-image-tab-url', 'editorImageUrl');
    setText('editor-image-local-help', 'editorImageLocalHelp');
    setText('editor-image-drive-help', 'editorImageDriveHelp');
    setText('editor-image-url-label', 'editorImageUrlLabel');
    setText('editor-image-alt-label', 'editorImageAlt');
    setText('editor-image-local-choose-text', 'editorChooseImage');
    setText('editor-image-cancel', 'cancel');
    setText('editor-image-confirm', 'editorInsert');
    if ($('folder-name-input')) $('folder-name-input').placeholder = t('folderName');
    if ($('rename-name-input')) $('rename-name-input').placeholder = t('renameName');
    updateSelectionUI();
    refreshOpsTitle();
    syncSidebarChrome();
    refreshSidebarSummary();
    refreshOpenEditorsI18n();
}

function syncMotionPreference() {
    document.documentElement.setAttribute('data-motion', settings.animationsEnabled === false ? 'reduced' : 'full');
}

function motionMs(ms) {
    return settings.animationsEnabled === false ? 0 : ms;
}

function setLanguage(value) {
    settings.language = value === 'system' || value === 'pt-BR' ? value : 'en';
    const select = $('setting-language');
    if (select) select.value = settings.language;
    try { localStorage.setItem('organessonium.lang', settings.language); } catch (_) {}
    applyI18n();
    render();
    queueSettingsSave();
}

function closeLanguageMenu() {
    const menu = $('language-menu');
    const box = $('language-select');
    if (menu) menu.classList.add('hidden');
    if (box) box.setAttribute('aria-expanded', 'false');
}

function toggleLanguageMenu() {
    const menu = $('language-menu');
    const box = $('language-select');
    if (!menu || !box) return;
    const open = menu.classList.toggle('hidden') === false;
    box.setAttribute('aria-expanded', open ? 'true' : 'false');
}

function formatServerCount(value) {
    const count = Number(value);
    if (!Number.isFinite(count) || count < 0) return '0';
    return new Intl.NumberFormat(resolveLanguage() === 'pt-BR' ? 'pt-BR' : 'en-US').format(Math.floor(count));
}

function refreshSidebarSummary() {
    const hasFiles = Number.isFinite(libraryFileCount);
    const hasFolders = Number.isFinite(libraryFolderCount);
    const hasBytes = Number.isFinite(libraryTotalBytes);
    if (storageDisp) storageDisp.textContent = hasBytes ? fmtSize(libraryTotalBytes) : fmtSize(0);
    if (serverItemCount) {
        serverItemCount.textContent = hasFiles && hasFolders
            ? formatServerCount(libraryFileCount + libraryFolderCount)
            : '0';
    }
}

function syncSidebarChrome() {
    document.body.classList.toggle('sidebar-collapsed', sidebarCollapsed);
    const button = $('btn-sidebar-toggle');
    if (!button) return;
    const label = t(sidebarCollapsed ? 'sidebarExpand' : 'sidebarCollapse');
    button.innerHTML = icon(sidebarCollapsed ? 'left_panel_open' : 'left_panel_close', 18);
    button.title = label;
    button.setAttribute('aria-label', label);
    button.setAttribute('aria-expanded', sidebarCollapsed ? 'false' : 'true');
    scheduleNativeTitleBarDragRegion();
}

function titleBarMetric(value, fallback) {
    const number = Number(value);
    return Number.isFinite(number) && number >= 0 ? Math.round(number) : fallback;
}

function scheduleNativeTitleBarDragRegion(delay = 200) {
    if (!nativeTitleBarMetrics.enabled) return;
    if (nativeTitleBarDragTimer) window.clearTimeout(nativeTitleBarDragTimer);
    nativeTitleBarDragTimer = window.setTimeout(() => {
        nativeTitleBarDragTimer = 0;
        if (!window.bridge || typeof window.bridge.setTitleBarDragRegion !== 'function') return;
        const rectangles = [];
        const addHeaderGap = (header, actions) => {
            const headerRect = header?.getBoundingClientRect();
            const actionsRect = actions?.getBoundingClientRect();
            if (!headerRect || !actionsRect || headerRect.height <= 0 || actionsRect.left <= headerRect.left) return;
            rectangles.push(
                Math.round(headerRect.left),
                Math.round(headerRect.top),
                Math.round(actionsRect.left - headerRect.left),
                Math.round(headerRect.height));
        };

        if (!previewOverlay.classList.contains('hidden')) {
            addHeaderGap(document.querySelector('.preview-topbar'), document.querySelector('.preview-topbar-actions'));
            if (rectangles.length > 0) window.bridge.setTitleBarDragRegion(...rectangles);
            return;
        }
        if (!settingsPanel.classList.contains('hidden')) {
            addHeaderGap(document.querySelector('.settings-header'), $('settings-close'));
            if (rectangles.length > 0) window.bridge.setTitleBarDragRegion(...rectangles);
            return;
        }

        const region = $('titlebar-drag-region');
        if (!region) return;
        const rect = region.getBoundingClientRect();
        const topbar = region.closest('.topbar');
        const topbarRect = topbar?.getBoundingClientRect();
        if (rect.width > 0 && topbarRect && topbarRect.height > 0) {
            rectangles.push(
                Math.round(rect.left),
                Math.round(topbarRect.top),
                Math.round(rect.width),
                Math.round(topbarRect.height));
        }

        const sidebarBrand = $('sidebar-brand');
        const sidebarRect = sidebarBrand?.getBoundingClientRect();
        if (!sidebarCollapsed && sidebarRect && sidebarRect.width > 0 && topbarRect && topbarRect.height > 0) {
            rectangles.push(
                Math.round(sidebarRect.left),
                Math.round(topbarRect.top),
                Math.round(sidebarRect.width),
                Math.round(topbarRect.height));
        }

        if (rectangles.length > 0) window.bridge.setTitleBarDragRegion(...rectangles);
    }, delay);
}

window._onNativeTitleBarMetrics = metrics => {
    const value = metrics || {};
    nativeTitleBarMetrics = {
        enabled: value.enabled === true,
        leftInset: titleBarMetric(value.leftInset, 0),
        rightInset: titleBarMetric(value.rightInset, 0),
        height: titleBarMetric(value.height, 48) || 48
    };
    document.documentElement.dataset.integratedTitlebar = nativeTitleBarMetrics.enabled ? 'true' : 'false';
    document.documentElement.style.setProperty('--native-titlebar-left-inset', nativeTitleBarMetrics.leftInset + 'px');
    document.documentElement.style.setProperty('--native-titlebar-right-inset', nativeTitleBarMetrics.rightInset + 'px');
    document.documentElement.style.setProperty('--native-titlebar-height', nativeTitleBarMetrics.height + 'px');
    scheduleNativeTitleBarDragRegion();
};

function toggleSidebar() {
    sidebarCollapsed = !sidebarCollapsed;
    try { localStorage.setItem('organessonium.sidebarCollapsed', sidebarCollapsed ? 'true' : 'false'); } catch (_) {}
    syncSidebarChrome();
}

function restoreSidebarState() {
    try { sidebarCollapsed = localStorage.getItem('organessonium.sidebarCollapsed') === 'true'; } catch (_) {}
    syncSidebarChrome();
}

function showTransfers() {
    opsPanel.classList.remove('hidden');
    $('sidebar-files')?.classList.remove('active');
    $('sidebar-files')?.removeAttribute('aria-current');
}

function showFilesWorkspace() {
    activateWorkspaceTab('files');
    setActiveLibraryView('files');
    if (currentPath !== '/') navigate('/');
    else render();
}

function setActiveLibraryView(view) {
    activeLibraryView = view;
    const createButton = $('btn-sidebar-new');
    if (createButton) {
        createButton.disabled = view !== 'files';
        createButton.setAttribute('aria-disabled', createButton.disabled ? 'true' : 'false');
    }
    if (view !== 'files') closeSidebarNewMenu();
    ['files','favorites','devices'].forEach(name => {
        const item = $('sidebar-' + name);
        item?.classList.toggle('active', name === view);
        if (name === view) item?.setAttribute('aria-current', 'page');
        else item?.removeAttribute('aria-current');
    });
}

function showFavoritesWorkspace() {
    activateWorkspaceTab('files');
    setActiveLibraryView('favorites');
    specialItems = [];
    folderLoadingPath = '/@favorites';
    render();
    window.bridge.loadFavorites();
}

function showDevicesWorkspace() {
    activateWorkspaceTab('files');
    setActiveLibraryView('devices');
    currentPath = '/@synchronized';
    folderLoadingPath = currentPath;
    historyBack = [];
    historyForward = [];
    render();
    window.bridge.loadSynchronizedDevices();
}

// ─── Java → JS ────────────────────────────────────────────────────────────────
window.onBridgeReady = () => { loadSettings(); setStatus(t('loading'), true); window.bridge.loadApps(); };

function appIdentityKey(app) {
    if (app && app.threadId) return 'thread:' + app.threadId;
    if (app && app.fileId) return 'file:' + app.fileId;
    if (app && app.folderId) return 'folder:' + app.folderId;
    return 'metadata:' + ((app && app.metadataMessageId) || '');
}

let registryValidationUsesCache = false;

window._onRegistryLoadStarted = hasCache => {
    registryValidationUsesCache = Boolean(hasCache);
    if (!registryValidationUsesCache) allApps = [];
    setStatus(registryValidationUsesCache ? t('validatingServer') : t('loading'), true);
    render();
};

window._onCachedAppsLoaded = apps => {
    if (!Array.isArray(apps)) return;
    allApps = apps;
    syncOpenMarkdownApps();
    render();
    refreshSidebarSummary();
    setStatus(t('validatingServer'), true);
    scheduleVisibleThumbnails();
};

window._onRegistryLoadProgress = count => {
    const progress = t('filesFound', { count:Number(count) || 0 });
    setStatus(registryValidationUsesCache ? t('validatingServer') + ' ' + progress : progress, true);
};

window._onLibraryFileCount = count => {
    const total = Number(count);
    if (!Number.isFinite(total) || total < 0) return;
    libraryFileCount = Math.floor(total);
    refreshSidebarSummary();
    if (currentPath === '/' && !registryValidationUsesCache) {
        setStatus(t('loaded', { count:libraryFileCount }), false);
    }
};

window._onLibraryStatistics = statistics => {
    const value = statistics || {};
    const fileCount = Number(value.fileCount);
    const folderCount = Number(value.folderCount);
    const totalBytes = Number(value.totalBytes);
    libraryFileCount = Number.isFinite(fileCount) && fileCount >= 0 ? Math.floor(fileCount) : null;
    libraryFolderCount = Number.isFinite(folderCount) && folderCount >= 0 ? Math.floor(folderCount) : null;
    libraryTotalBytes = Number.isFinite(totalBytes) && totalBytes >= 0 ? Math.floor(totalBytes) : null;
    refreshSidebarSummary();
};

window._onAppsBatch = apps => {
    if (!Array.isArray(apps) || !apps.length) return;
    const byId = new Map(allApps.map(app => [appIdentityKey(app), app]));
    apps.forEach(app => byId.set(appIdentityKey(app), app));
    allApps = [...byId.values()];
    syncOpenMarkdownApps();
    render();
    scheduleVisibleThumbnails();
    refreshSidebarSummary();
};

window._onAppsLoaded = apps => {
    allApps = apps;
    syncOpenMarkdownApps();
    render();
    refreshSidebarSummary();
    setStatus(t('loaded', { count:libraryFileCount ?? apps.length }), false);
    registryValidationUsesCache = false;
    scheduleVisibleThumbnails();
};
window._onLocalAppsChanged = apps => {
    if (!Array.isArray(apps)) return;
    folderFileCounts.clear();
    requestedFolderStatistics.clear();
    libraryFileCount = null;
    libraryFolderCount = null;
    libraryTotalBytes = null;
    allApps = apps;
    syncOpenMarkdownApps();
    render();
    refreshSidebarSummary();
    scheduleVisibleThumbnails();
};
window._onFolderLoadState = (path, loading) => {
    if (path !== currentPath) return;
    folderLoadingPath = loading ? path : '';
    render();
};
window._onServerReadState = active => {
    serverReadDepth = Math.max(0, serverReadDepth + (active ? 1 : -1));
    $('server-read-status')?.classList.toggle('hidden', serverReadDepth === 0);
};
window._onSpecialItemsLoaded = (view, items) => {
    if (!Array.isArray(items)) return;
    const byId = new Map(allApps.map(app => [appIdentityKey(app), app]));
    items.forEach(app => byId.set(appIdentityKey(app), app));
    allApps = [...byId.values()];
    if (view === 'favorites') {
        specialItems = items;
    }
    folderLoadingPath = '';
    if (activeLibraryView === view) render();
    scheduleVisibleThumbnails();
};
window._onFavoriteChanged = entry => {
    if (!entry) return;
    const key = appIdentityKey(entry);
    allApps = allApps.map(app => appIdentityKey(app) === key ? Object.assign(app, entry) : app);
    specialItems = entry.favorite
        ? [...specialItems.filter(app => appIdentityKey(app) !== key), entry]
        : specialItems.filter(app => appIdentityKey(app) !== key);
    render();
};
window._onFolderStatistics = (path, statistics) => {
    if (!Array.isArray(statistics)) return;
    statistics.forEach(statistic => {
        if (!statistic || !statistic.folderId || !Number.isFinite(Number(statistic.fileCount))) return;
        folderFileCounts.set(statistic.folderId, Number(statistic.fileCount));
        requestedFolderStatistics.delete(statistic.folderId);
    });
    if (path === currentPath) render();
};
window._onBackgroundActivity = (message, active) => {
    const activity = $('background-activity');
    if (!activity) return;
    const text = $('background-activity-text');
    if (active) {
        if (text) text.textContent = message || t('backgroundInProgress');
        activity.classList.remove('hidden');
    } else {
        activity.classList.add('hidden');
    }
};
window._onLoggedOut = () => {
    window.location.href = 'setup.html';
};

window._onSystemThemeChanged = theme => {
    systemPreferences.theme = theme === 'light' ? 'light' : 'dark';
    if (settings.theme === 'system') applyTheme('system');
};

window._onProxyTestResult = (success, message, protocol) => {
    const el = $('proxy-test-result');
    if (!el) return;
    if (success === true) {
        el.textContent = (protocol && protocol !== ''
            ? t('proxyTestDetected', { protocol })
            : t('proxyTestSuccess'));
        el.className = 'proxy-test-result ok';
    } else {
        el.textContent = t('proxyTestFail', { message: message || '' });
        el.className = 'proxy-test-result fail';
    }
    el.classList.remove('hidden');
};

function syncOpenMarkdownApps() {
    markdownTabs.forEach(entry => {
        if (!entry || !entry.app) return;
        const fresh = allApps.find(app =>
            (entry.app.threadId && app.threadId === entry.app.threadId) ||
            (entry.app.metadataMessageId && app.metadataMessageId === entry.app.metadataMessageId));
        if (!fresh) return;
        entry.app = Object.assign(entry.app, fresh);
        const label = entry.tab && entry.tab.querySelector('span');
        if (label) label.textContent = truncate(entry.app.name || '', 24);
        const title = entry.panel && entry.panel.querySelector('.editor-doc-copy strong');
        if (title) {
            title.textContent = truncate(entry.app.name || '', 42);
            title.title = entry.app.name || '';
        }
    });
}

window._onThumb = tid => {
    const url = window._thumbs && window._thumbs[tid];
    if (!url) return;

    document.querySelectorAll('[data-thumb="' + tid + '"]').forEach(el => applyThumbnailElement(el, url));
};

function ensureThumbnailObserver() {
    if (thumbnailObserver) return thumbnailObserver;
    thumbnailObserver = new IntersectionObserver(entries => {
        entries.forEach(entry => {
            const el = entry.target;
            const tid = el && el.dataset ? el.dataset.thumb : '';
            if (!tid) return;
            if (entry.isIntersecting) {
                visibleThumbnailIds.add(tid);
                const cached = window._thumbs && window._thumbs[tid];
                if (cached) applyThumbnailElement(el, cached);
                else scheduleVisibleThumbnails();
            } else {
                visibleThumbnailIds.delete(tid);
            }
        });
    }, { root:$('main-area'), rootMargin:'180px 0px', threshold:.01 });
    return thumbnailObserver;
}

function resetThumbnailViewport() {
    if (thumbnailObserver) thumbnailObserver.disconnect();
    visibleThumbnailIds.clear();
}

function bindThumbnailPreview(card, app, type, color) {
    const el = card && card.querySelector ? card.querySelector('[data-thumb]') : null;
    if (!el || !app || !app.threadId) return;
    el.dataset.thumbType = type || 'generic';
    el.dataset.thumbColor = color || TYPE_COLOR.generic;
    ensureThumbnailObserver().observe(el);
}

function applyThumbnailElement(el, url) {
    if (!el || !url) return;
    const img = document.createElement('img');
    img.className = 'card-preview-img';
    img.alt = '';
    img.draggable = false;
    img.decoding = 'async';
    img.loading = 'lazy';
    img.addEventListener('error', () => {
        if (img.parentNode === el) resetThumbnailElement(el);
    }, { once:true });
    el.innerHTML = '';
    el.appendChild(img);
    img.src = url;
}

function resetThumbnailElement(el) {
    if (!el) return;
    const type = el.dataset.thumbType || 'generic';
    const color = el.dataset.thumbColor || TYPE_COLOR.generic;
    el.innerHTML = icon(fileIcon(type), 48, color);
}

function requestVisibleThumbnails() {
    if (!settings.previewEnabled) return;
    if (!window.bridge || typeof window.bridge.loadVisibleThumbnails !== 'function') return;
    const ids = Array.from(document.querySelectorAll('[data-thumb]'))
        .map(element => element.dataset.thumb || '')
        .filter((id, index, all) => id && all.indexOf(id) === index)
        .filter(id => visibleThumbnailIds.has(id) && !(window._thumbs && window._thumbs[id]));
    if (!ids.length) return;
    window.bridge.loadVisibleThumbnails(JSON.stringify(ids));
}

function scheduleVisibleThumbnails() {
    if (!settings.previewEnabled) return;
    clearTimeout(thumbnailRequestTimer);
    thumbnailRequestTimer = setTimeout(() => {
        thumbnailRequestTimer = 0;
        requestVisibleThumbnails();
    }, 80);
}

window._onPreviewLoading = () => {
    disposeActiveImage();
    resetPdfPreviewState();
    $('preview-zoom-controls').classList.add('hidden');
    pendingVideoQualitySwitch = null;
    $('preview-body').innerHTML = `<div class="preview-load-state">
        <div class="preview-spinner"></div>
        <div class="preview-load-label">${esc(t('previewDownloading'))}</div>
    </div>`;
};

window._onPreviewProgress = (percent, phase, downloadedBytes, totalBytes) => {
    const value = Math.max(0, Math.min(100, Number(percent) || 0));
    const labels = {
        locating:t('previewLocating'), generic:t('previewDownloadGeneric'),
        video480:t('previewDownload480'), full:t('previewDownloadFull'), cache:t('previewCache')
    };
    const base = labels[phase] || t('previewDownloading');
    const downloaded = Number(downloadedBytes) || 0;
    const total = Number(totalBytes) || 0;
    const message = total > 0 ? `${base} - ${fmtSize(downloaded)} / ${fmtSize(total)}` : base;
    const mediaProgress = document.querySelector('.preview-media-quality-progress');
    if (mediaProgress && !mediaProgress.classList.contains('hidden')) {
        mediaProgress.querySelector('span').style.width = value + '%';
        mediaProgress.querySelector('small').textContent = message || (value + '%');
        return;
    }
    const state = $('preview-body').querySelector('.preview-load-state');
    if (!state) return;
    state.querySelector('.preview-load-label').textContent = message || t('previewDownloading');
};

window._onPreviewReady = (type, name, data) => {
    if (type === 'markdown' && finishMarkdownEditor(name, data)) return;
    disposeActiveImage();
    resetPdfPreviewState();
    const body = $('preview-body');
    body.innerHTML = '';
    $('preview-zoom-controls').classList.toggle('hidden', type !== 'image');

    if (type === 'image') {
        const wrap = document.createElement('div');
        wrap.className = 'preview-img-wrap';
        const img = document.createElement('img');
        img.className = 'preview-img';
        img.alt = name; img.draggable = false; img.decoding = 'async';
        img.addEventListener('error', () => {
            if (img.parentNode !== wrap || !wrap.isConnected) return;
            activeImgWrap = null;
            $('preview-zoom-controls').classList.add('hidden');
            body.innerHTML = `<div class="preview-unsupported">${icon('error',48,'#595959')}<p>${esc(t('previewErrorTitle'))}</p></div>`;
        }, { once:true });
        wrap.appendChild(img);
        body.appendChild(wrap);
        img.src = data;
        activeImgWrap = wrap;
        setupImageZoom(wrap, img);

    } else if (type === 'video' || type === 'audio') {
        const mediaInfo = safeParseJson(data, null);
        const mime = mediaInfo && mediaInfo.mime ? mediaInfo.mime : '';
        const src = mediaInfo && mediaInfo.src ? mediaInfo.src : '';
        const qualities = mediaInfo && Array.isArray(mediaInfo.qualities) ? mediaInfo.qualities : [];
        const currentQuality = mediaInfo && mediaInfo.quality ? mediaInfo.quality : 'full';
        const shell = document.createElement('div');
        shell.className = `preview-media-player-shell ${type}`;

        const player = document.createElement(type);
        player.className = `preview-media-player ${type}`;
        player.controls = type !== 'video';
        player.preload = 'metadata';
        player.autoplay = false;
        player.dataset.quality = currentQuality;
        if (type === 'video') player.playsInline = true;
        if (src) setMediaElementSource(player, src, mime);

        const fallback = document.createElement('div');
        fallback.className = 'preview-media-fallback hidden';
        fallback.innerHTML = `<div class="preview-media-icon">${icon(type === 'audio' ? 'audio_file' : 'movie', 54, TYPE_COLOR[type])}</div>
            <div class="preview-media-copy">
                <strong>${esc(t('mediaLoadError'))}</strong>
                <p>${esc(t('mediaSafeMessage'))}</p>
                ${mime ? `<small>${esc(mime)}</small>` : ''}
            </div>`;

        const actions = document.createElement('div');
        actions.className = 'preview-media-actions';
        actions.innerHTML = `${type === 'video' && qualities.length > 1 ? `<div class="preview-quality-control" id="preview-video-quality-control"><span>${esc(t('quality'))}</span><button type="button" class="preview-quality-button" id="preview-video-quality-button" data-value="${esc(currentQuality)}"><strong>${esc(videoQualityLabel(currentQuality))}</strong>${icon('expand_more',16)}</button><div class="preview-quality-options hidden" id="preview-video-quality-options">${qualities.map(quality => `<button type="button" class="preview-quality-option" data-video-quality-option="${esc(quality)}" aria-selected="${quality === currentQuality ? 'true' : 'false'}">${esc(videoQualityLabel(quality))}</button>`).join('')}</div></div>` : ''}
            <button class="btn-secondary" id="preview-open-external">${icon('open_in_new',17)}<span>${esc(t('openExternal'))}</span></button>
            <button class="btn-secondary" id="preview-media-download">${icon('download',17)}<span>${esc(t('download'))}</span></button>`;

        const qualityProgress = document.createElement('div');
        qualityProgress.className = 'preview-media-quality-progress hidden';
        qualityProgress.innerHTML = '<div><span></span></div><small></small>';

        let videoChrome = null;
        if (type === 'video') {
            videoChrome = createVideoChrome(player);
            shell.appendChild(videoChrome);
        } else {
            shell.appendChild(player);
        }
        shell.appendChild(fallback);
        shell.appendChild(qualityProgress);
        shell.appendChild(actions);
        shell.addEventListener('click', e => e.stopPropagation());
        body.appendChild(shell);

        player.addEventListener('error', () => {
            if (videoChrome) videoChrome.classList.add('hidden');
            player.classList.add('hidden');
            fallback.classList.remove('hidden');
        });
        if (src) player.load();
        else player.dispatchEvent(new Event('error'));
        $('preview-open-external')?.addEventListener('click', e => {
            e.stopPropagation();
            if (previewAppRef) window.bridge.openPreviewMedia(previewAppRef.threadId);
        });
        $('preview-media-download')?.addEventListener('click', e => {
            e.stopPropagation();
            if (previewAppRef) window.bridge.startDownload(previewAppRef.threadId, previewAppRef.name);
        });
        const qualityButton = $('preview-video-quality-button');
        const qualityOptions = $('preview-video-quality-options');
        const requestQualitySwitch = value => {
            if (!previewAppRef || !value) return;
            if (value === (player.dataset.quality || currentQuality)) return;
            pendingVideoQualitySwitch = {
                threadId:previewAppRef.threadId,
                time:Number.isFinite(player.currentTime) ? player.currentTime : 0,
                playing:!player.paused,
                previousQuality:player.dataset.quality || currentQuality
            };
            setPreviewQualityUi(shell, value);
            qualityOptions?.classList.add('hidden');
            qualityProgress.classList.remove('hidden');
            qualityProgress.querySelector('span').style.width = '0%';
            qualityProgress.querySelector('small').textContent = t('previewDownloading');
            window.bridge.requestVideoQuality(previewAppRef.threadId, previewAppRef.name, value);
        };
        qualityButton?.addEventListener('click', e => {
            e.stopPropagation();
            qualityOptions?.classList.toggle('hidden');
        });
        qualityOptions?.querySelectorAll('[data-video-quality-option]').forEach(option => {
            option.addEventListener('click', e => {
                e.stopPropagation();
                requestQualitySwitch(option.dataset.videoQualityOption || '');
            });
        });

    } else if (type === 'pdf') {
        const pdf = document.createElement('div');
        pdf.className = 'preview-pdf-doc';
        const meta = safeParseJson(data, {});
        const threadId = meta.threadId || (previewAppRef ? previewAppRef.threadId : '');
        if (!previewAppRef || (threadId && previewAppRef.threadId !== threadId)) return;
        if (meta.directUrl) {
            renderPdfJsPreview(pdf, meta, name, threadId);
            body.appendChild(pdf);
            return;
        }
        pdf.dataset.threadId = threadId;
        const pages = document.createElement('div');
        pages.className = 'pdf-pages';
        const progress = document.createElement('div');
        progress.className = 'pdf-progress';
        progress.textContent = meta.totalPages ? t('pdfLoaded', { current:0, total:meta.totalPages }) : t('pdfLoading');
        pdf.appendChild(pages);
        pdf.appendChild(progress);
        body.appendChild(pdf);
        setupPdfScroll(pdf);
        pdfPreviewState = {
            threadId,
            total: Number(meta.totalPages) || 0,
            batchSize: Number(meta.batchSize) || PDF_DEFAULT_BATCH_SIZE,
            nextPage: 1,
            loaded: 0,
            loading: false,
            done: false
        };
        pdf.addEventListener('scroll', maybeLoadMorePdfPages, { passive:true });
        requestNextPdfBatch();

    } else if (type === 'archive') {
        body.appendChild(renderArchivePreview(data, name));

    } else if (type === 'markdown') {
        body.appendChild(renderMarkdownPreview(data));

    } else if (type === 'document') {
        body.appendChild(renderDocumentPreview(data));

    } else if (type === 'text') {
        const pre = document.createElement('div');
        pre.className = 'preview-text'; pre.textContent = data;
        body.appendChild(pre);
    }
};

window._onPreviewError = msg => {
    $('preview-body').innerHTML = `<div class="preview-unsupported">${icon('error',48,'#595959')}<p>${esc(t('previewErrorTitle'))}</p><small>${esc(msg)}</small></div>`;
};

window._onVideoQualityReady = (threadId, quality, src, mime) => {
    if (!previewAppRef || previewAppRef.threadId !== threadId) return;
    const shell = $('preview-body').querySelector('.preview-media-player-shell.video');
    const player = shell && shell.querySelector('video');
    if (!player || !src) return;
    const saved = pendingVideoQualitySwitch && pendingVideoQualitySwitch.threadId === threadId
        ? pendingVideoQualitySwitch
        : { time:player.currentTime || 0, playing:!player.paused };
    pendingVideoQualitySwitch = null;
    const progress = shell.querySelector('.preview-media-quality-progress');
    if (progress) progress.classList.add('hidden');
    setPreviewQualityUi(shell, quality);
    player.dataset.quality = quality;
    player.classList.remove('hidden');
    shell.querySelector('.preview-video-frame')?.classList.remove('hidden');
    shell.querySelector('.preview-media-fallback')?.classList.add('hidden');
    setMediaElementSource(player, src, mime);
    player.addEventListener('loadedmetadata', () => {
        if (Number.isFinite(saved.time) && saved.time > 0) player.currentTime = Math.min(saved.time, player.duration || saved.time);
        if (saved.playing) player.play().catch(() => {});
    }, { once:true });
    player.load();
};

window._onVideoQualityError = (threadId, message) => {
    if (!previewAppRef || previewAppRef.threadId !== threadId) return;
    const shell = $('preview-body').querySelector('.preview-media-player-shell.video');
    if (!shell) return;
    shell.querySelector('.preview-media-quality-progress')?.classList.add('hidden');
    if (pendingVideoQualitySwitch?.previousQuality) setPreviewQualityUi(shell, pendingVideoQualitySwitch.previousQuality);
    pendingVideoQualitySwitch = null;
    setStatus(t('errorPrefix') + (message || t('previewErrorTitle')), false);
};

function videoQualityLabel(quality) {
    return quality === '480p' ? t('preview480') : t('fullQuality');
}

function setPreviewQualityUi(shell, quality) {
    if (!shell || !quality) return;
    const button = shell.querySelector('#preview-video-quality-button');
    if (button) {
        button.dataset.value = quality;
        const label = button.querySelector('strong');
        if (label) label.textContent = videoQualityLabel(quality);
    }
    shell.querySelectorAll('[data-video-quality-option]').forEach(option => {
        option.setAttribute('aria-selected', option.dataset.videoQualityOption === quality ? 'true' : 'false');
    });
}

function setMediaElementSource(player, src, mime) {
    player.pause();
    player.removeAttribute('src');
    while (player.firstChild) player.removeChild(player.firstChild);
    const source = document.createElement('source');
    source.src = src;
    if (mime) source.type = mime;
    player.appendChild(source);
}

function createVideoChrome(player) {
    const frame = document.createElement('div');
    frame.className = 'preview-video-frame';
    frame.appendChild(player);
    const controls = document.createElement('div');
    controls.className = 'preview-video-controls';
    controls.innerHTML = `<button data-video-play title="Play">${icon('play_arrow',20)}</button>
        <span data-video-current>0:00</span>
        <input data-video-seek type="range" min="0" max="1000" value="0" step="1" aria-label="Seek">
        <span data-video-duration>0:00</span>
        <button data-video-mute title="Mute">${icon('volume_up',19)}</button>
        <input data-video-volume type="range" min="0" max="1" value="1" step="0.01" aria-label="Volume">
        <button data-video-fullscreen title="Fullscreen">${icon('fullscreen',20)}</button>`;
    frame.appendChild(controls);

    const play = controls.querySelector('[data-video-play]');
    const seek = controls.querySelector('[data-video-seek]');
    const current = controls.querySelector('[data-video-current]');
    const duration = controls.querySelector('[data-video-duration]');
    const mute = controls.querySelector('[data-video-mute]');
    const volume = controls.querySelector('[data-video-volume]');
    let seeking = false;
    let resizeTimer = 0;
    let hostFullscreen = false;

    function syncFrameSize() {
        if (player.tagName !== 'VIDEO' || !player.videoWidth || !player.videoHeight) return;
        if (document.fullscreenElement === frame) return;
        const shell = frame.parentElement;
        const shellWidth = shell ? shell.clientWidth : 0;
        const maxWidth = Math.max(260, Math.min(1200, shellWidth || window.innerWidth - 56));
        const maxHeight = Math.max(180, Math.min(740, Math.round(window.innerHeight * 0.68)));
        const scale = Math.min(maxWidth / player.videoWidth, maxHeight / player.videoHeight);
        const width = Math.max(180, Math.round(player.videoWidth * scale));
        const height = Math.max(140, Math.round(player.videoHeight * scale));
        const controlsMinWidth = maxWidth >= 540 ? 540 : maxWidth;
        const frameWidth = Math.min(maxWidth, Math.max(width, controlsMinWidth));
        frame.style.width = frameWidth + 'px';
        player.style.width = Math.min(width, frameWidth) + 'px';
        player.style.height = Math.min(height, maxHeight) + 'px';
    }

    function scheduleFrameSize() {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(syncFrameSize, 30);
    }


    function setVideoFullscreen(enabled) {
        hostFullscreen = !!enabled;
        frame.classList.toggle('is-fullscreen', hostFullscreen || document.fullscreenElement === frame);
        if (window.bridge && typeof window.bridge.setHostFullscreen === 'function') {
            window.bridge.setHostFullscreen(hostFullscreen);
        }
        if (!hostFullscreen) scheduleFrameSize();
    }
    function refresh() {
        const dur = Number.isFinite(player.duration) ? player.duration : 0;
        const cur = Number.isFinite(player.currentTime) ? player.currentTime : 0;
        play.innerHTML = icon(player.paused ? 'play_arrow' : 'pause', 20);
        current.textContent = formatMediaTime(cur);
        duration.textContent = formatMediaTime(dur);
        if (!seeking) seek.value = dur > 0 ? String(Math.round(cur / dur * 1000)) : '0';
        mute.innerHTML = icon(player.muted || player.volume === 0 ? 'volume_off' : 'volume_up', 19);
        volume.value = player.muted ? '0' : String(player.volume);
    }

    play.addEventListener('click', e => {
        e.stopPropagation();
        if (player.paused) player.play().catch(() => {});
        else player.pause();
        refresh();
    });
    player.addEventListener('click', e => {
        e.stopPropagation();
        play.click();
    });
    seek.addEventListener('input', () => {
        seeking = true;
        const dur = Number.isFinite(player.duration) ? player.duration : 0;
        current.textContent = formatMediaTime(dur * (Number(seek.value) || 0) / 1000);
    });
    seek.addEventListener('change', () => {
        const dur = Number.isFinite(player.duration) ? player.duration : 0;
        if (dur > 0) player.currentTime = dur * (Number(seek.value) || 0) / 1000;
        seeking = false;
        refresh();
    });
    mute.addEventListener('click', e => {
        e.stopPropagation();
        player.muted = !player.muted;
        refresh();
    });
    volume.addEventListener('input', () => {
        player.volume = Math.max(0, Math.min(1, Number(volume.value) || 0));
        player.muted = player.volume === 0;
        refresh();
    });
    controls.querySelector('[data-video-fullscreen]').addEventListener('click', async e => {
        e.stopPropagation();
        if (document.fullscreenElement === frame || hostFullscreen) {
            if (document.fullscreenElement === frame) await document.exitFullscreen().catch(() => {});
            setVideoFullscreen(false);
            return;
        }

        let nativeStarted = false;
        if (frame.requestFullscreen) {
            try {
                await frame.requestFullscreen();
                nativeStarted = document.fullscreenElement === frame;
            } catch (_) {}
        }

        setVideoFullscreen(true);
        if (!nativeStarted) syncFrameSize();
    });
    ['loadedmetadata', 'timeupdate', 'play', 'pause', 'volumechange', 'durationchange'].forEach(eventName => {
        player.addEventListener(eventName, refresh);
    });
    player.addEventListener('loadedmetadata', syncFrameSize);
    const handleFullscreenChange = () => {
        setVideoFullscreen(document.fullscreenElement === frame);
    };
    if (activeVideoResizeCleanup) activeVideoResizeCleanup();
    window.addEventListener('resize', scheduleFrameSize, { passive:true });
    document.addEventListener('fullscreenchange', handleFullscreenChange);
    activeVideoResizeCleanup = () => {
        if (document.fullscreenElement === frame) document.exitFullscreen().catch(() => {});
        setVideoFullscreen(false);
        window.removeEventListener('resize', scheduleFrameSize);
        document.removeEventListener('fullscreenchange', handleFullscreenChange);
    };
    refresh();
    return frame;
}

function formatMediaTime(seconds) {
    seconds = Math.max(0, Math.floor(Number(seconds) || 0));
    const h = Math.floor(seconds / 3600);
    const m = Math.floor((seconds % 3600) / 60);
    const s = seconds % 60;
    return h > 0
        ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
        : `${m}:${String(s).padStart(2, '0')}`;
}

window._onPreviewNotSupported = () => {
    $('preview-zoom-controls').classList.add('hidden');
    const body = $('preview-body');
    body.innerHTML = `<div class="preview-unsupported">${icon('insert_drive_file',64,'#333')}<p>${esc(t('previewUnavailable'))}</p><small>${esc(t('unsupportedType'))}</small><div class="preview-media-actions"><button class="btn-secondary" id="preview-unsupported-download">${icon('download',17)}<span>${esc(t('download'))}</span></button></div></div>`;
    $('preview-unsupported-download')?.addEventListener('click', e => {
        e.stopPropagation();
        if (previewAppRef) window.bridge.startDownload(previewAppRef.threadId, previewAppRef.name);
    });
};

window._onPdfPage = (threadId, page, total, src, textLayer = []) => {
    if (!pdfPreviewState || pdfPreviewState.threadId !== threadId) return;
    const pdf = getActivePdfDoc(threadId);
    if (!pdf) return;
    const pages = pdf.querySelector('.pdf-pages');
    if (!pages || pages.querySelector('[data-page="' + page + '"]')) return;
    const section = document.createElement('section');
    section.className = 'pdf-page';
    section.dataset.page = page;
    const img = document.createElement('img');
    img.className = 'pdf-page-img';
    img.alt = 'PDF page ' + page;
    img.draggable = false;
    img.onload = () => {
        layoutPdfTextLayer(section);
        maybeLoadMorePdfPages();
    };
    img.src = src;
    section.appendChild(img);
    section.appendChild(renderPdfTextLayer(textLayer));
    const next = Array.from(pages.children).find(el => Number(el.dataset.page) > page);
    pages.insertBefore(section, next || null);
    pdfPreviewState.loaded = Math.max(pdfPreviewState.loaded, page);
    updatePdfProgress();
    layoutPdfTextLayer(section);
};

window._onPdfBatchDone = (threadId, startPage, endPage, total) => {
    if (!pdfPreviewState || pdfPreviewState.threadId !== threadId) return;
    pdfPreviewState.total = Number(total) || pdfPreviewState.total;
    pdfPreviewState.loading = false;
    pdfPreviewState.nextPage = Math.max(pdfPreviewState.nextPage, Number(endPage) + 1);
    pdfPreviewState.done = pdfPreviewState.total > 0 && pdfPreviewState.nextPage > pdfPreviewState.total;
    updatePdfProgress();
    maybeLoadMorePdfPages();
};

window._onPdfBatchError = (threadId, msg) => {
    if (!pdfPreviewState || pdfPreviewState.threadId !== threadId) return;
    pdfPreviewState.loading = false;
    const progress = getActivePdfDoc(threadId)?.querySelector('.pdf-progress');
    if (progress) progress.textContent = msg || t('previewErrorTitle');
};

function resetPdfPreviewState() {
    if (pdfPreviewState && pdfPreviewState.mode === 'pdfjs') {
        unregisterPdfJsSelectableLayers(pdfPreviewState.pdf);
        try { pdfPreviewState.observer?.disconnect(); } catch (_) {}
        try { pdfPreviewState.thumbObserver?.disconnect(); } catch (_) {}
        try { pdfPreviewState.loadingTask?.destroy(); } catch (_) {}
        if (pdfPreviewState.scrollFrame) cancelAnimationFrame(pdfPreviewState.scrollFrame);
    }
    pdfPreviewState = null;
}

function getActivePdfDoc(threadId = null) {
    const pdf = $('preview-body').querySelector('.preview-pdf-doc');
    if (!pdf) return null;
    if (threadId && pdf.dataset.threadId !== threadId) return null;
    return pdf;
}

function setupPdfScroll(pdf) {
    // Keep native Chromium/WebView2 wheel behavior; loading more pages uses the scroll event below.
}

async function ensurePdfJs() {
    if (!pdfJsModulePromise) {
        const baseUrl = getAppScriptBaseUrl();
        pdfJsModulePromise = import(new URL('vendor/pdfjs/pdf.min.mjs', baseUrl).href).then(pdfjs => {
            pdfjs.GlobalWorkerOptions.workerSrc = new URL('vendor/pdfjs/pdf.worker.min.mjs', baseUrl).href;
            return pdfjs;
        });
    }
    return pdfJsModulePromise;
}

function getAppScriptBaseUrl() {
    const scripts = Array.from(document.scripts || []);
    const appScript = scripts.find(script => /(^|\/)app\.js(?:[?#].*)?$/i.test(script.src || ''));
    const source = appScript && appScript.src ? appScript.src : window.location.href;
    return new URL('.', source).href;
}

function renderPdfJsPreview(pdf, meta, name, threadId) {
    pdf.classList.add('preview-pdf-custom');
    pdf.dataset.threadId = threadId;
    pdf.innerHTML = `<aside class="pdf-sidebar" data-pdf-sidebar>
        <div class="pdf-sidebar-head">
            <span>${icon('view_list',16)}<strong>Páginas</strong></span>
            <button data-pdf-sidebar-close title="${esc(t('close'))}">${icon('close',14)}</button>
        </div>
        <div class="pdf-thumbs" data-pdf-thumbs></div>
    </aside>
    <section class="pdf-main">
        <div class="pdf-toolbar">
            <button data-pdf-toggle-sidebar title="Páginas">${icon('view_list',16)}</button>
            <div class="pdf-toolbar-divider"></div>
            <button data-pdf-zoom-out title="${esc(t('zoomOut'))}">${icon('remove',16)}</button>
            <button data-pdf-zoom-in title="${esc(t('zoomIn'))}">${icon('add',16)}</button>
            <button data-pdf-fit title="${esc(t('fitToScreen'))}">${icon('fit_screen',16)}</button>
            <span class="pdf-zoom-value" data-pdf-zoom>100%</span>
            <div class="pdf-toolbar-divider"></div>
            <label class="pdf-page-jump"><input data-pdf-page-input type="number" min="1" value="1"><span data-pdf-total>de 0</span></label>
            <button data-pdf-rotate title="Girar">${icon('refresh',16)}</button>
        </div>
        <div class="pdf-viewer-scroll" data-pdf-scroll>
            <div class="pdf-pages" data-pdf-pages></div>
            <div class="pdf-progress" data-pdf-progress>${esc(t('pdfLoading'))}</div>
        </div>
    </section>`;

    const state = {
        mode:'pdfjs',
        threadId,
        name,
        url:meta.directUrl,
        pdf,
        doc:null,
        loadingTask:null,
        total:0,
        zoom:1,
        rotation:0,
        currentPage:1,
        renderEpoch:0,
        rendering:new Set(),
        rendered:new Set(),
        thumbRendering:new Set(),
        thumbRendered:new Set(),
        observer:null,
        thumbObserver:null,
        scrollFrame:0,
        pagesEl:pdf.querySelector('[data-pdf-pages]'),
        thumbsEl:pdf.querySelector('[data-pdf-thumbs]'),
        scrollEl:pdf.querySelector('[data-pdf-scroll]'),
        progressEl:pdf.querySelector('[data-pdf-progress]'),
        pageInput:pdf.querySelector('[data-pdf-page-input]'),
        totalEl:pdf.querySelector('[data-pdf-total]'),
        zoomEl:pdf.querySelector('[data-pdf-zoom]')
    };
    pdfPreviewState = state;
    setupPdfJsControls(state);
    loadPdfJsDocument(state).catch(err => {
        if (pdfPreviewState !== state) return;
        if (state.progressEl) state.progressEl.textContent = err && err.message ? err.message : t('previewErrorTitle');
    });
}

function setupPdfJsControls(state) {
    const bind = (selector, handler) => state.pdf.querySelector(selector)?.addEventListener('click', e => {
        e.preventDefault();
        e.stopPropagation();
        handler();
    });
    bind('[data-pdf-toggle-sidebar]', () => state.pdf.classList.toggle('sidebar-collapsed'));
    bind('[data-pdf-sidebar-close]', () => state.pdf.classList.add('sidebar-collapsed'));
    bind('[data-pdf-zoom-out]', () => setPdfJsZoom(state, state.zoom - 0.12));
    bind('[data-pdf-zoom-in]', () => setPdfJsZoom(state, state.zoom + 0.12));
    bind('[data-pdf-fit]', () => setPdfJsZoom(state, 1));
    bind('[data-pdf-rotate]', () => {
        state.rotation = (state.rotation + 90) % 360;
        rerenderPdfJsPages(state);
    });
    state.pageInput?.addEventListener('keydown', e => {
        if (e.key !== 'Enter') return;
        e.preventDefault();
        scrollToPdfJsPage(state, Number(state.pageInput.value) || 1);
    });
    state.scrollEl?.addEventListener('scroll', () => {
        if (state.scrollFrame) return;
        state.scrollFrame = requestAnimationFrame(() => {
            state.scrollFrame = 0;
            updatePdfJsCurrentPage(state);
        });
    }, { passive:true });
}

async function loadPdfJsDocument(state) {
    const pdfjs = await ensurePdfJs();
    if (pdfPreviewState !== state) return;
    state.loadingTask = pdfjs.getDocument({ url:state.url });
    state.doc = await state.loadingTask.promise;
    if (pdfPreviewState !== state) return;
    state.total = state.doc.numPages || 0;
    if (state.totalEl) state.totalEl.textContent = 'de ' + state.total;
    if (state.pageInput) state.pageInput.max = String(state.total || 1);
    if (state.progressEl) state.progressEl.textContent = t('pdfLoaded', { current:0, total:state.total });
    buildPdfJsPageShells(state);
    updatePdfJsToolbar(state);
    observePdfJsPages(state);
    renderPdfJsPage(state, 1);
    renderPdfJsThumb(state, 1);
}

function buildPdfJsPageShells(state) {
    state.pagesEl.innerHTML = '';
    state.thumbsEl.innerHTML = '';
    for (let page = 1; page <= state.total; page++) {
        const section = document.createElement('section');
        section.className = 'pdf-page pdf-page-shell';
        section.dataset.page = page;
        section.innerHTML = `<div class="pdf-page-loading">${icon('picture_as_pdf',28,TYPE_COLOR.pdf)}<span>${page}</span></div>`;
        state.pagesEl.appendChild(section);

        const thumb = document.createElement('button');
        thumb.className = 'pdf-thumb';
        thumb.dataset.page = page;
        thumb.innerHTML = `<div class="pdf-thumb-canvas-wrap"></div><span>${page}</span>`;
        thumb.addEventListener('click', e => {
            e.preventDefault();
            scrollToPdfJsPage(state, page);
        });
        state.thumbsEl.appendChild(thumb);
    }
}

function observePdfJsPages(state) {
    state.observer?.disconnect();
    state.thumbObserver?.disconnect();
    state.observer = new IntersectionObserver(entries => {
        entries.forEach(entry => {
            if (entry.isIntersecting) renderPdfJsPage(state, Number(entry.target.dataset.page));
        });
        updatePdfJsCurrentPage(state);
    }, { root:state.scrollEl, rootMargin:'900px 0px', threshold:[0, .35, .7] });
    state.pagesEl.querySelectorAll('[data-page]').forEach(page => state.observer.observe(page));

    state.thumbObserver = new IntersectionObserver(entries => {
        entries.forEach(entry => {
            if (entry.isIntersecting) renderPdfJsThumb(state, Number(entry.target.dataset.page));
        });
    }, { root:state.thumbsEl, rootMargin:'700px 0px', threshold:.01 });
    state.thumbsEl.querySelectorAll('[data-page]').forEach(thumb => state.thumbObserver.observe(thumb));
}

async function renderPdfJsPage(state, pageNumber) {
    if (!state.doc || !pageNumber || state.rendered.has(pageNumber) || state.rendering.has(pageNumber)) return;
    const epoch = state.renderEpoch || 0;
    state.rendering.add(pageNumber);
    try {
        const page = await state.doc.getPage(pageNumber);
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0)) return;
        const shell = state.pagesEl.querySelector('[data-page="' + pageNumber + '"]');
        if (!shell) return;
        const base = page.getViewport({ scale:1, rotation:state.rotation });
        const available = Math.max(320, state.scrollEl.clientWidth - 72);
        const scale = Math.max(0.35, Math.min(3.6, available / base.width * state.zoom));
        const viewport = page.getViewport({ scale, rotation:state.rotation });
        const canvas = document.createElement('canvas');
        canvas.className = 'pdf-page-canvas';
        await paintPdfPageToCanvas(canvas, page, viewport);
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0)) return;
        const textLayer = document.createElement('div');
        textLayer.className = 'pdfjs-text-layer textLayer';
        textLayer.dataset.page = String(pageNumber);
        unregisterPdfJsSelectableLayers(shell);
        shell.innerHTML = '';
        shell.style.width = viewport.width + 'px';
        shell.style.minHeight = viewport.height + 'px';
        shell.appendChild(canvas);
        shell.appendChild(textLayer);
        shell.classList.add('rendered');
        state.rendered.add(pageNumber);
        if (state.progressEl) state.progressEl.textContent = t('pdfLoaded', { current:Math.min(state.rendered.size, state.total), total:state.total });
        renderPdfJsSelectableText(state, page, viewport, textLayer, epoch);
    } catch (err) {
        const shell = state.pagesEl.querySelector('[data-page="' + pageNumber + '"]');
        if (shell) shell.innerHTML = `<div class="pdf-page-error">${esc(err && err.message ? err.message : t('previewErrorTitle'))}</div>`;
    } finally {
        state.rendering.delete(pageNumber);
    }
}

async function renderPdfJsSelectableText(state, page, viewport, container, epoch) {
    try {
        const pdfjs = await ensurePdfJs();
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0) || !container.isConnected) return;
        const textContentSource = await page.getTextContent({ includeMarkedContent:true, disableNormalization:true });
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0) || !container.isConnected) return;
        const layer = new pdfjs.TextLayer({ textContentSource, container, viewport });
        container._pdfTextLayer = layer;
        await layer.render();
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0) || !container.isConnected) {
            try { layer.cancel(); } catch (_) {}
            return;
        }
        bindPdfJsSelectableLayer(container);
        container.classList.add('ready');
    } catch (err) {
        console.warn('PDF text layer unavailable', err);
        container.classList.add('unavailable');
    }
}

function bindPdfJsSelectableLayer(layer) {
    if (!layer || pdfJsSelectableLayers.has(layer)) return;
    layer.tabIndex = 0;
    const end = document.createElement('div');
    end.className = 'endOfContent';
    layer.appendChild(end);
    layer.addEventListener('mousedown', () => layer.classList.add('selecting'));
    layer.addEventListener('copy', event => {
        const selection = document.getSelection();
        if (!selection || selection.rangeCount === 0) return;
        const text = selection.toString();
        if (!text) return;
        event.clipboardData?.setData('text/plain', text.replace(/\u0000/g, ''));
        event.preventDefault();
        event.stopPropagation();
    });
    pdfJsSelectableLayers.set(layer, end);
    ensurePdfJsSelectionEvents();
}

function unregisterPdfJsSelectableLayers(root = null) {
    const layers = Array.from(pdfJsSelectableLayers.keys());
    layers.forEach(layer => {
        if (!root || layer === root || root.contains?.(layer) || !layer.isConnected) {
            try { layer._pdfTextLayer?.cancel(); } catch (_) {}
            pdfJsSelectableLayers.delete(layer);
        }
    });
}

function ensurePdfJsSelectionEvents() {
    if (pdfJsSelectionEventsBound) return;
    pdfJsSelectionEventsBound = true;
    document.addEventListener('pointerdown', () => { pdfJsSelectionPointerDown = true; });
    document.addEventListener('pointerup', () => {
        pdfJsSelectionPointerDown = false;
        resetPdfJsInactiveSelections();
    });
    window.addEventListener('blur', () => {
        pdfJsSelectionPointerDown = false;
        resetPdfJsInactiveSelections();
    });
    document.addEventListener('keyup', () => {
        if (!pdfJsSelectionPointerDown) resetPdfJsInactiveSelections();
    });
    document.addEventListener('selectionchange', syncPdfJsSelectionEnd);
}

function resetPdfJsInactiveSelections(activeLayers = new Set()) {
    for (const [layer, end] of Array.from(pdfJsSelectableLayers.entries())) {
        if (!layer.isConnected) {
            pdfJsSelectableLayers.delete(layer);
            continue;
        }
        if (activeLayers.has(layer)) continue;
        layer.appendChild(end);
        end.style.width = '';
        end.style.height = '';
        end.style.userSelect = '';
        layer.classList.remove('selecting');
    }
}

function syncPdfJsSelectionEnd() {
    const selection = document.getSelection();
    if (!selection || selection.rangeCount === 0) {
        resetPdfJsInactiveSelections();
        pdfJsPreviousSelectionRange = null;
        return;
    }
    const activeLayers = new Set();
    for (let i = 0; i < selection.rangeCount; i++) {
        const range = selection.getRangeAt(i);
        for (const layer of Array.from(pdfJsSelectableLayers.keys())) {
            if (!layer.isConnected) {
                pdfJsSelectableLayers.delete(layer);
                continue;
            }
            try {
                if (range.intersectsNode(layer)) activeLayers.add(layer);
            } catch (_) {}
        }
    }
    resetPdfJsInactiveSelections(activeLayers);
    activeLayers.forEach(layer => layer.classList.add('selecting'));
    if (!activeLayers.size) {
        pdfJsPreviousSelectionRange = null;
        return;
    }
    const range = selection.getRangeAt(0);
    const modifyStart = pdfJsPreviousSelectionRange &&
        (range.compareBoundaryPoints(Range.END_TO_END, pdfJsPreviousSelectionRange) === 0 ||
         range.compareBoundaryPoints(Range.START_TO_END, pdfJsPreviousSelectionRange) === 0);
    let anchor = modifyStart ? range.startContainer : range.endContainer;
    if (anchor.nodeType === Node.TEXT_NODE) anchor = anchor.parentNode;
    if (!anchor || !anchor.parentElement) return;
    if (!modifyStart && range.endOffset === 0) {
        while (anchor && !anchor.previousSibling && anchor.parentNode && !anchor.classList?.contains('textLayer')) {
            anchor = anchor.parentNode;
        }
        if (anchor?.previousSibling) anchor = anchor.previousSibling;
    }
    const parentLayer = anchor.parentElement?.closest('.pdfjs-text-layer');
    const end = parentLayer ? pdfJsSelectableLayers.get(parentLayer) : null;
    if (end) {
        end.style.width = parentLayer.style.width;
        end.style.height = parentLayer.style.height;
        end.style.userSelect = 'text';
        anchor.parentElement.insertBefore(end, modifyStart ? anchor : anchor.nextSibling);
    }
    pdfJsPreviousSelectionRange = range.cloneRange();
}

async function renderPdfJsThumb(state, pageNumber) {
    if (!state.doc || !pageNumber || state.thumbRendered.has(pageNumber) || state.thumbRendering.has(pageNumber)) return;
    const epoch = state.renderEpoch || 0;
    state.thumbRendering.add(pageNumber);
    try {
        const page = await state.doc.getPage(pageNumber);
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0)) return;
        const thumb = state.thumbsEl.querySelector('[data-page="' + pageNumber + '"] .pdf-thumb-canvas-wrap');
        if (!thumb) return;
        const base = page.getViewport({ scale:1, rotation:state.rotation });
        const viewport = page.getViewport({ scale:118 / base.width, rotation:state.rotation });
        const canvas = document.createElement('canvas');
        canvas.className = 'pdf-thumb-canvas';
        await paintPdfPageToCanvas(canvas, page, viewport);
        if (pdfPreviewState !== state || epoch !== (state.renderEpoch || 0)) return;
        thumb.innerHTML = '';
        thumb.appendChild(canvas);
        state.thumbRendered.add(pageNumber);
    } finally {
        state.thumbRendering.delete(pageNumber);
    }
}

function paintPdfPageToCanvas(canvas, page, viewport) {
    const dpr = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    canvas.width = Math.floor(viewport.width * dpr);
    canvas.height = Math.floor(viewport.height * dpr);
    canvas.style.width = viewport.width + 'px';
    canvas.style.height = viewport.height + 'px';
    const ctx = canvas.getContext('2d', { alpha:false });
    return page.render({ canvasContext:ctx, viewport, transform:[dpr, 0, 0, dpr, 0, 0] }).promise;
}

function setPdfJsZoom(state, zoom) {
    state.zoom = Math.max(0.45, Math.min(2.6, Number(zoom) || 1));
    rerenderPdfJsPages(state);
}

function rerenderPdfJsPages(state) {
    if (!state || state.mode !== 'pdfjs') return;
    const current = state.currentPage || 1;
    unregisterPdfJsSelectableLayers(state.pdf);
    state.rendered.clear();
    state.rendering.clear();
    state.thumbRendered.clear();
    state.thumbRendering.clear();
    state.renderEpoch = (state.renderEpoch || 0) + 1;
    buildPdfJsPageShells(state);
    observePdfJsPages(state);
    updatePdfJsToolbar(state);
    requestAnimationFrame(() => scrollToPdfJsPage(state, current, false));
    renderPdfJsPage(state, current);
    renderPdfJsThumb(state, current);
}

function scrollToPdfJsPage(state, pageNumber, smooth = true) {
    const target = Math.max(1, Math.min(state.total || 1, Number(pageNumber) || 1));
    const shell = state.pagesEl.querySelector('[data-page="' + target + '"]');
    if (shell) shell.scrollIntoView({ block:'start', behavior:smooth && settings.animationsEnabled !== false ? 'smooth' : 'auto' });
    state.currentPage = target;
    updatePdfJsToolbar(state);
    renderPdfJsPage(state, target);
    renderPdfJsThumb(state, target);
}

function updatePdfJsCurrentPage(state) {
    if (!state || !state.scrollEl || !state.pagesEl) return;
    const center = state.scrollEl.getBoundingClientRect().top + state.scrollEl.clientHeight * .42;
    let best = state.currentPage || 1;
    let bestDistance = Infinity;
    state.pagesEl.querySelectorAll('[data-page]').forEach(page => {
        const rect = page.getBoundingClientRect();
        const distance = Math.abs(rect.top - center);
        if (distance < bestDistance) {
            bestDistance = distance;
            best = Number(page.dataset.page) || best;
        }
    });
    if (best !== state.currentPage) {
        state.currentPage = best;
        updatePdfJsToolbar(state);
        renderPdfJsThumb(state, best);
    }
}

function updatePdfJsToolbar(state) {
    if (!state) return;
    if (state.pageInput && document.activeElement !== state.pageInput) state.pageInput.value = String(state.currentPage || 1);
    if (state.zoomEl) state.zoomEl.textContent = Math.round((state.zoom || 1) * 100) + '%';
    state.thumbsEl?.querySelectorAll('.pdf-thumb').forEach(btn => {
        btn.classList.toggle('active', Number(btn.dataset.page) === state.currentPage);
    });
}

function renderPdfTextLayer(runs) {
    const layer = document.createElement('div');
    layer.className = 'pdf-text-layer';
    (Array.isArray(runs) ? runs : []).forEach(run => {
        if (!run || !run.text) return;
        const span = document.createElement('span');
        span.className = 'pdf-text-run';
        span.textContent = run.text;
        span.dataset.x = clampPercent(run.x);
        span.dataset.y = clampPercent(run.y);
        span.dataset.w = clampPercent(run.w, 100);
        span.dataset.h = clampPercent(run.h, 100);
        span.dataset.fs = Number(run.fs) || 0.012;
        span.style.left = span.dataset.x + '%';
        span.style.top = span.dataset.y + '%';
        span.style.height = span.dataset.h + '%';
        layer.appendChild(span);
    });
    return layer;
}

function layoutPdfTextLayer(section) {
    if (!section) return;
    const width = section.clientWidth || 1;
    const height = section.clientHeight || 1;
    section.querySelectorAll('.pdf-text-run').forEach(span => {
        const ratio = Number(span.dataset.fs) || 0.012;
        const desiredWidth = Math.max(1, width * (Number(span.dataset.w) || 0) / 100);
        const desiredHeight = Math.max(1, height * (Number(span.dataset.h) || 0) / 100);
        const fontSize = Math.max(5, Math.min(48, width * ratio));
        span.style.fontSize = fontSize + 'px';
        span.style.lineHeight = Math.max(fontSize, desiredHeight) + 'px';
        span.style.transform = 'none';
        span.style.width = 'auto';
        const measured = span.scrollWidth || span.getBoundingClientRect().width || desiredWidth;
        const scale = Math.max(0.35, Math.min(3.2, desiredWidth / Math.max(1, measured)));
        span.style.transform = 'scaleX(' + scale.toFixed(4) + ')';
    });
}

function layoutAllPdfTextLayers() {
    document.querySelectorAll('.pdf-page').forEach(layoutPdfTextLayer);
}

function clampPercent(value, fallback = 0) {
    const n = Number(value);
    return Math.max(0, Math.min(100, Number.isFinite(n) ? n : fallback));
}

function requestNextPdfBatch() {
    const st = pdfPreviewState;
    if (!st || st.loading || st.done || !st.total || !window.bridge || !window.bridge.requestPdfPages) return;
    const start = st.nextPage;
    if (start > st.total) {
        st.done = true;
        updatePdfProgress();
        return;
    }
    const count = Math.min(st.batchSize || PDF_DEFAULT_BATCH_SIZE, st.total - start + 1);
    st.loading = true;
    updatePdfProgress(start);
    window.bridge.requestPdfPages(st.threadId, start, count);
}

function maybeLoadMorePdfPages() {
    const st = pdfPreviewState;
    if (!st || st.loading || st.done) return;
    const pdf = getActivePdfDoc(st.threadId);
    if (!pdf) return;
    const remaining = pdf.scrollHeight - pdf.scrollTop - pdf.clientHeight;
    const threshold = Math.max(720, pdf.clientHeight * 1.4);
    if (remaining <= threshold) requestNextPdfBatch();
}

function updatePdfProgress(loadingPage = null) {
    const st = pdfPreviewState;
    if (!st) return;
    const progress = getActivePdfDoc(st.threadId)?.querySelector('.pdf-progress');
    if (!progress) return;
    if (st.done) {
        progress.textContent = t('pdfLoaded', { current:st.total, total:st.total });
        return;
    }
    progress.textContent = st.loading
        ? t('pdfProgress', { current:loadingPage || st.nextPage, total:st.total })
        : t('pdfLoaded', { current:Math.min(st.loaded, st.total), total:st.total });
}

window._onStatus  = (m,l) => setStatus(m, l);
window._onError   = m     => setStatus(t('errorPrefix') + m, false);
window._onUploadConflict = (requestId, payload) => openUploadConflict(requestId, payload || {});
window._onPreviewCacheCleared = () => {
    setStatus(t('cacheCleared'), false);
    updatePreviewCacheSize(0);
    scheduleVisibleThumbnails();
};
window._onPreviewCacheSize = bytes => updatePreviewCacheSize(bytes);
window._onMarkdownSaved = (id, payload) => finishMarkdownAutoSave(id, payload || {});
window._onMarkdownSaveError = (id, msg) => failMarkdownAutoSave(id, msg || '');
window._onMarkdownImageReady = (id, payload) => {
    const data = payload || {};
    if (!data.url) return;
    if (editorImageState && editorImageState.entry && editorImageState.entry.id === id) {
        insertEditorImage(editorImageState.entry, editorImageState.selection, data.url, $('editor-image-alt').value.trim() || data.name || '');
        closeEditorImageDialog();
        return;
    }
    const pending = pendingPastedMarkdownImages.get(id);
    if (pending && pending.entry) {
        pendingPastedMarkdownImages.delete(id);
        insertEditorImage(pending.entry, pending.selection, data.url, pending.alt || data.name || '');
        setStatus(t('editorSaved'), false);
    }
};
window._onMarkdownImageError = (id, msg) => {
    if (editorImageState && editorImageState.entry && editorImageState.entry.id === id) {
        $('editor-image-status').textContent = msg || t('editorSaveError');
    }
    pendingPastedMarkdownImages.delete(id);
    setStatus(t('errorPrefix') + (msg || t('editorSaveError')), false);
};
window._onMarkdownImageCancelled = id => {
    if (editorImageState && editorImageState.entry && editorImageState.entry.id === id) {
        $('editor-image-status').textContent = '';
    }
    pendingPastedMarkdownImages.delete(id);
};
window._onEncryptedMarkdownImageReady = (id, reference, url) => {
    const entry = markdownTabs.get(id);
    if (!entry || !entry.editor || !reference || !url) return;
    entry.editor.querySelectorAll('img').forEach(img => {
        if (img.dataset.organessoniumImage === reference) {
            img.src = url;
            img.dataset.organessoniumReady = 'true';
            delete img.dataset.organessoniumLoading;
        }
    });
};
window._onEncryptionSettingsChanged = (mode, customKeyAvailable) => {
    settings.encryptionMode = mode === 'custom' ? 'custom' : 'standard';
    settings.customKeyAvailable = customKeyAvailable === true;
    syncEncryptionSettings();
    setStatus(t('encryptionUpdated'), false);
};
window._onEncryptionKeyExported = () => setStatus(t('encryptionExport'), false);
window._onDragOver  = () => dragOverlay.classList.remove('hidden');
window._onDragLeave = () => dragOverlay.classList.add('hidden');
window._onPathChosen = (type, path) => {
    if (type==='download') { $('setting-dlpath').value  = path; settings.downloadPath = path; }
    else if (type==='data') { $('setting-datapath').value = path; settings.dataPath = path; }
    else                   { $('setting-tmppath').value = path; settings.tempPath     = path; }
    queueSettingsSave();
};

window._opsAdd = (id, type, name) => {
    opsPanel.classList.remove('hidden');
    const item = document.createElement('div');
    item.className = 'op-item';
    item.id = 'op-' + id;
    item.dataset.operationId = id;
    item.dataset.opType = type || 'download';
    item.dataset.opStatus = 'active';
    const opIcon = type === 'upload' ? 'upload' : (type === 'delete' ? 'delete' : (type === 'move' ? 'drive_file_move' : 'download'));
    item.innerHTML = `<span class="op-icon ${type}">${icon(opIcon,17)}</span>
        <div class="op-info">
            <div class="op-name">${esc(truncate(name,34))}</div>
            <div class="op-msg" id="op-msg-${id}">${esc(t('waiting'))}</div>
            <div class="op-eta hidden" id="op-eta-${id}"></div>
            <div class="op-progress-line"><div class="op-progress"><div class="op-progress-bar" id="op-progress-${id}" style="width:0%"></div></div><span class="op-percent" id="op-percent-${id}">0%</span></div>
        </div>`;
    opsList.appendChild(item);
    opsList.scrollTop = opsList.scrollHeight;
    refreshOpsTitle();
};

window._opsUploadBatch = (id, names) => {
    opsPanel.classList.remove('hidden');
    const safeNames = Array.isArray(names) ? names : [];
    const existing = $('op-' + id);
    if (existing) existing.remove();
    const item = document.createElement('section');
    item.className = 'op-batch';
    item.id = 'op-' + id;
    item.dataset.operationId = id;
    item.dataset.opType = 'upload';
    item.dataset.opStatus = 'active';
    item.dataset.itemCount = String(safeNames.length);
    const rows = safeNames.map((name, index) => `<div class="op-file-row" id="op-file-${id}-${index}" data-file-status="queued">
        <span class="op-file-icon">${icon('description',16)}</span>
        <div class="op-file-copy">
            <div class="op-file-name" title="${esc(name)}">${esc(name)}</div>
            <div class="op-file-detail" id="op-file-detail-${id}-${index}">${esc(t('queuedUpload'))}</div>
        </div>
        <span class="op-file-percent" id="op-file-percent-${id}-${index}">0%</span>
        <span class="op-file-progress queued" id="op-file-progress-${id}-${index}" style="--progress:0" aria-label="0%"></span>
    </div>`).join('');
    item.innerHTML = `<div class="op-batch-summary">
            <span id="op-msg-${id}">${esc(t('startingUploads'))}</span>
            <button type="button" class="op-batch-cancel">${esc(t('cancelUpload'))}</button>
        </div>
        <div class="op-batch-track-line"><div class="op-batch-track"><span id="op-progress-${id}" style="width:0%"></span></div><span class="op-percent" id="op-percent-${id}">0%</span></div>
        <div class="op-file-list">${rows}</div>`;
    item.querySelector('.op-batch-cancel').addEventListener('click', e => {
        e.stopPropagation();
        requestCancelUploadBatch(id);
    });
    opsList.appendChild(item);
    opsList.scrollTop = opsList.scrollHeight;
    refreshOpsTitle();
};

window._opsUploadFileProgress = (id, fileIndex, status, percent, message) => {
    const row = $('op-file-' + id + '-' + fileIndex);
    if (!row) return;
    const normalizedStatus = ['active', 'waiting', 'done', 'error', 'cancelled'].includes(status) ? status : 'active';
    row.dataset.fileStatus = normalizedStatus;
    const detail = $('op-file-detail-' + id + '-' + fileIndex);
    if (detail && message) detail.textContent = message;
    const indicator = $('op-file-progress-' + id + '-' + fileIndex);
    if (!indicator) return;
    const incomingPercent = Number(percent);
    const previousPercent = Math.max(0, Number(row.dataset.progress) || 0);
    const hasProgress = Number.isFinite(incomingPercent) && incomingPercent >= 0;
    const safePercent = hasProgress
        ? Math.max(previousPercent, Math.min(100, incomingPercent))
        : previousPercent;
    if (hasProgress) {
        const displayPercent = Math.round(safePercent);
        row.dataset.progress = String(safePercent);
        indicator.style.setProperty('--progress', safePercent);
        indicator.setAttribute('aria-label', displayPercent + '%');
        const percentEl = $('op-file-percent-' + id + '-' + fileIndex);
        if (percentEl) percentEl.textContent = displayPercent + '%';
    }
    indicator.className = 'op-file-progress ' + normalizedStatus;
    if (normalizedStatus === 'done') indicator.innerHTML = icon('check',15);
    else if (normalizedStatus === 'waiting') indicator.innerHTML = icon('schedule',15);
    else if (normalizedStatus === 'error') indicator.innerHTML = icon('error',15);
    else if (normalizedStatus === 'cancelled') indicator.innerHTML = icon('block',15);
    else indicator.innerHTML = '';
    refreshBatchProgress(id);
};

window._opsUpdate = (id, status, msg) => {
    const item = $('op-'+id); if (!item) return;
    item.dataset.opStatus = status || 'active';
    const msgEl = $('op-msg-'+id); if (msgEl) msgEl.textContent = msg;
    if (item.classList.contains('op-batch')) {
        if (status === 'done') {
            setOpProgress(id, 100);
            item.querySelectorAll('.op-file-row').forEach(row => {
                if (row.dataset.fileStatus === 'active' || row.dataset.fileStatus === 'queued') {
                    window._opsUploadFileProgress(id, Number(row.id.split('-').pop()), 'done', 100, t('uploadProgress'));
                }
            });
            setTimeout(() => { transferRateStates.delete(id); item.remove(); if (!opsList.children.length) opsPanel.classList.add('hidden'); refreshOpsTitle(); }, 4500);
        } else if (status === 'cancelled') {
            item.querySelectorAll('.op-file-row').forEach(row => {
                if (!['done','error'].includes(row.dataset.fileStatus)) {
                    window._opsUploadFileProgress(id, Number(row.id.split('-').pop()), 'cancelled', -1, msg);
                }
            });
            setTimeout(() => { transferRateStates.delete(id); item.remove(); if (!opsList.children.length) opsPanel.classList.add('hidden'); refreshOpsTitle(); }, 4500);
        } else if (status === 'error') {
            item.querySelectorAll('.op-file-row').forEach(row => {
                if (row.dataset.fileStatus === 'queued') {
                    window._opsUploadFileProgress(id, Number(row.id.split('-').pop()), 'error', -1, msg);
                }
            });
        } else if (status === 'waiting') {
            item.querySelectorAll('.op-file-row').forEach(row => {
                if (!['done','error','cancelled'].includes(row.dataset.fileStatus)) {
                    window._opsUploadFileProgress(id, Number(row.id.split('-').pop()), 'waiting', -1, msg);
                }
            });
        }
        refreshOpsTitle();
        return;
    }

    const iconEl = item.querySelector('.op-icon');
    if (status==='done') {
        setOpProgress(id, 100);
        if (iconEl) { iconEl.className='op-icon done'; iconEl.innerHTML=icon('check_circle',17); }
        setTimeout(()=>{ transferRateStates.delete(id); item.remove(); if(!opsList.children.length) opsPanel.classList.add('hidden'); refreshOpsTitle(); },3000);
    } else if (status==='cancelled') {
        if (iconEl) { iconEl.className='op-icon error'; iconEl.innerHTML=icon('block',17); }
        setTimeout(()=>{ transferRateStates.delete(id); item.remove(); if(!opsList.children.length) opsPanel.classList.add('hidden'); refreshOpsTitle(); },3500);
    } else if (status==='waiting' && iconEl) {
        iconEl.className='op-icon waiting'; iconEl.innerHTML=icon('schedule',17);
    } else if (status==='error' && iconEl) {
        iconEl.className='op-icon error'; iconEl.innerHTML=icon('error',17);
    }
};

window._opsProgress = (id, percent, msg) => {
    window._opsUpdate(id, 'active', msg);
    setOpProgress(id, percent);
};

window._opsTransferMetrics = (id, direction, completedItems, totalItems, transferredBytes, totalBytes, activeTransferSeconds) => {
    const item = $('op-' + id);
    if (!item) return;
    const now = performance.now();
    const reportedBytes = Math.max(0, Number(transferredBytes) || 0);
    const total = Math.max(0, Number(totalBytes) || 0);
    const reportedActiveSeconds = Number(activeTransferSeconds);
    const hasActiveClock = Number.isFinite(reportedActiveSeconds) && reportedActiveSeconds >= 0;
    let state = transferRateStates.get(id);
    if (!state || state.direction !== direction || state.hasActiveClock !== hasActiveClock) {
        state = {
            direction,
            hasActiveClock,
            highWaterBytes:reportedBytes,
            activeSeconds:hasActiveClock ? reportedActiveSeconds : 0,
            samples:[[now, reportedBytes, hasActiveClock ? reportedActiveSeconds : 0]],
            speed:0
        };
        transferRateStates.set(id, state);
    } else {
        state.highWaterBytes = Math.max(state.highWaterBytes, reportedBytes);
        if (hasActiveClock) {
            state.activeSeconds = Math.max(state.activeSeconds, reportedActiveSeconds);
            state.samples.push([now, state.highWaterBytes, state.activeSeconds]);
            while (state.samples.length > 1 && state.activeSeconds - state.samples[1][2] >= 60) state.samples.shift();
            const baseline = state.samples[0];
            const elapsed = state.activeSeconds - baseline[2];
            state.speed = elapsed >= 1
                ? Math.max(0, state.highWaterBytes - baseline[1]) / elapsed
                : 0;
        } else {
            state.samples.push([now, state.highWaterBytes, 0]);
            const cutoff = now - 60000;
            while (state.samples.length > 1 && state.samples[1][0] <= cutoff) state.samples.shift();
            const baseline = state.samples[0];
            const elapsed = now - baseline[0];
            state.speed = elapsed >= 1000
                ? Math.max(0, state.highWaterBytes - baseline[1]) / (elapsed / 1000)
                : 0;
        }
    }
    const bytes = state.highWaterBytes;
    const completed = Math.max(Number(item.dataset.metricCompleted) || 0, Number(completedItems) || 0);

    item.dataset.metricDirection = direction === 'upload' ? 'upload' : 'download';
    item.dataset.metricCompleted = String(Math.max(0, completed));
    item.dataset.metricTotal = String(Math.max(1, Number(totalItems) || 1));
    item.dataset.metricSpeed = formatTransferRate(state.speed);
    item.dataset.metricReady = 'true';

    const etaEl = $('op-eta-' + id);
    if (etaEl) {
        const remaining = Math.max(0, total - bytes);
        const etaSeconds = state.speed > 0 && remaining > 0 ? remaining / state.speed : 0;
        etaEl.textContent = direction === 'download' && etaSeconds > 0
            ? t('downloadEta', { eta:formatTransferEta(etaSeconds) })
            : '';
        etaEl.classList.toggle('hidden', !etaEl.textContent);
    }
    refreshOpsTitle();
};

window._onInterruptedTransfers = count => {
    const total = Number(count) || 0;
    if (total <= 0) return;
    askConfirm(t('interruptedTransfersTitle'), t('interruptedTransfersMessage', { count:total }), () => {
        if (window.bridge && window.bridge.resumeTransfers) window.bridge.resumeTransfers();
    }, 'resumeTransfers');
};

function setOpProgress(id, percent) {
    const bar = $('op-progress-'+id);
    if (!bar) return;
    const item = $('op-' + id);
    const incoming = Math.max(0, Math.min(100, Number(percent) || 0));
    const current = Math.max(0, Number(item?.dataset.progress) || 0);
    const safe = item?.classList.contains('op-batch') ? Math.max(current, incoming) : incoming;
    if (item) item.dataset.progress = String(safe);
    bar.style.width = safe + '%';
    const percentEl = $('op-percent-' + id);
    if (percentEl) percentEl.textContent = Math.round(safe) + '%';
}

function requestVisibleFolderStatistics() {
    if (!window.bridge || typeof window.bridge.loadVisibleFolderStatistics !== 'function') return;
    const requestedAt = Date.now();
    const folderIds = allApps
        .filter(app => app.type === 'folder' && (app.path || '/') === currentPath && app.folderId)
        .map(app => app.folderId)
        .filter(folderId => !folderFileCounts.has(folderId) && (requestedAt - (requestedFolderStatistics.get(folderId) || 0)) > 1000);
    if (!folderIds.length) return;
    folderIds.forEach(folderId => requestedFolderStatistics.set(folderId, requestedAt));
    window.bridge.loadVisibleFolderStatistics(JSON.stringify(folderIds));
}

function scheduleVisibleFolderStatistics() {
    clearTimeout(folderStatisticsTimer);
    folderStatisticsTimer = setTimeout(() => {
        folderStatisticsTimer = 0;
        requestVisibleFolderStatistics();
    }, 80);
}

function refreshBatchProgress(id) {
    const item = $('op-' + id);
    if (!item || !item.classList.contains('op-batch')) return;
    const rows = Array.from(item.querySelectorAll('.op-file-row'));
    if (!rows.length) return;
    const total = rows.reduce((sum, row) => sum + Math.max(0, Number(row.dataset.progress) || 0), 0);
    setOpProgress(id, total / rows.length);
}

function formatTransferRate(bytesPerSecond) {
    let value = Math.max(0, Number(bytesPerSecond) || 0);
    const units = ['B/s','KB/s','MB/s','GB/s','TB/s'];
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
    const digits = value >= 100 || unit === 0 ? 0 : 1;
    return new Intl.NumberFormat(resolveLanguage() === 'pt-BR' ? 'pt-BR' : 'en-US', {
        minimumFractionDigits:digits,
        maximumFractionDigits:digits
    }).format(value) + ' ' + units[unit];
}

function formatTransferEta(seconds) {
    const total = Math.max(1, Math.round(Number(seconds) || 0));
    if (total < 60) return total + ' s';
    const minutes = Math.ceil(total / 60);
    if (minutes < 60) return minutes + ' min';
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    return rest ? hours + ' h ' + rest + ' min' : hours + ' h';
}

function opTypeTitle(type) {
    return type === 'upload'
        ? t('uploadProgress')
        : (type === 'delete' ? t('deleteProgress') : (type === 'move' ? t('moveProgress') : t('downloadProgress')));
}

function refreshOpsTitle() {
    const items = Array.from(opsList.children);
    if (!items.length) {
        $('ops-title').textContent = t('transfers');
        return;
    }
    if (items.length === 1 && items[0].dataset.metricReady === 'true') {
        const item = items[0];
        const values = {
            done:Number(item.dataset.metricCompleted) || 0,
            total:Number(item.dataset.metricTotal) || 1,
            speed:item.dataset.metricSpeed || formatTransferRate(0)
        };
        $('ops-title').textContent = item.dataset.metricDirection === 'upload'
            ? t('sendingProgress', values)
            : t('downloadingProgress', values);
        return;
    }
    if (items.length === 1 && items[0].classList.contains('op-batch') && items[0].dataset.opType === 'upload') {
        $('ops-title').textContent = t('uploadingItems', { count:Number(items[0].dataset.itemCount) || 0 });
        return;
    }
    const types = new Set(items.map(el => el.dataset.opType || 'download'));
    const title = types.size === 1 ? opTypeTitle(items[0].dataset.opType || 'download') : t('transfers');
    $('ops-title').textContent = title + ' (' + items.length + ')';
}

function activeTransferIds() {
    return Array.from(opsList.children)
        .filter(item => ['upload', 'download'].includes(item.dataset.opType || 'download'))
        .filter(item => !['done', 'error', 'cancelled'].includes(item.dataset.opStatus || 'active'))
        .map(item => item.dataset.operationId || (item.id && item.id.startsWith('op-') ? item.id.slice(3) : ''))
        .filter(Boolean);
}

function requestCancelUploadBatch(id) {
    askConfirm(t('cancelTransfersTitle'), t('cancelTransfersMessage', { count:1 }), () => {
        setStatus(t('transfersCancelled'), true);
        if (window.bridge && window.bridge.cancelTransfers) window.bridge.cancelTransfers(JSON.stringify([id]));
    }, 'cancelTransfers');
}

function toggleOpsMinimized() {
    const body = $('ops-body');
    const minimized = body.classList.toggle('minimized');
    $('ops-toggle').innerHTML = icon(minimized ? 'expand_less' : 'expand_more', 18);
    $('ops-toggle').setAttribute('aria-expanded', minimized ? 'false' : 'true');
}

function requestCancelTransfers() {
    const ids = activeTransferIds();
    if (!ids.length) {
        opsPanel.classList.add('hidden');
        return;
    }

    askConfirm(t('cancelTransfersTitle'), t('cancelTransfersMessage', { count:ids.length }), () => {
        setStatus(t('transfersCancelled'), true);
        if (window.bridge && window.bridge.cancelTransfers) window.bridge.cancelTransfers(JSON.stringify(ids));
    }, 'cancelTransfers');
}

document.addEventListener('DOMContentLoaded', () => {
    initIcons();
    restoreSidebarState();
    syncMotionPreference();
    window.addEventListener('resize', () => {
        layoutAllPdfTextLayers();
        scheduleNativeTitleBarDragRegion();
    });
    applyI18n();

    $('btn-sidebar-toggle').addEventListener('click', toggleSidebar);
    $('btn-sidebar-new').addEventListener('click', e => {
        e.stopPropagation();
        toggleSidebarNewMenu();
    });
    $('sidebar-new-folder').addEventListener('click', () => { closeSidebarNewMenu(); openFolderModal(); });
    $('sidebar-new-markdown').addEventListener('click', () => { closeSidebarNewMenu(); createMarkdownDocument(); });
    $('sidebar-upload-files').addEventListener('click', () => { closeSidebarNewMenu(); window.bridge.startUpload(); });
    $('sidebar-upload-folder').addEventListener('click', () => { closeSidebarNewMenu(); window.bridge.startFolderUpload(); });
    $('sidebar-files').addEventListener('click', () => { closeSidebarNewMenu(); showFilesWorkspace(); });
    $('sidebar-favorites').addEventListener('click', () => { closeSidebarNewMenu(); showFavoritesWorkspace(); });
    $('sidebar-devices').addEventListener('click', () => { closeSidebarNewMenu(); showDevicesWorkspace(); });
    $('sidebar-settings').addEventListener('click', () => { closeSidebarNewMenu(); openSettings(); });

    $('btn-refresh').addEventListener('click',       refreshCurrentFolder);
    $('btn-grid').addEventListener('click', () => setView('grid'));
    $('btn-list').addEventListener('click', () => setView('list'));
    $('search-input').addEventListener('input', e => { searchQuery = e.target.value.toLowerCase(); render(); });

    btnBack.addEventListener('click',    goBack);
    btnForward.addEventListener('click', goForward);

    // Nova pasta
    $('folder-cancel').addEventListener('click',   closeFolderModal);
    $('folder-confirm').addEventListener('click',  confirmFolder);
    folderInput.addEventListener('keydown', e => { if (e.key==='Enter') confirmFolder(); if (e.key==='Escape') closeFolderModal(); });
    folderOverlay.addEventListener('click', e => { if (e.target===folderOverlay) closeFolderModal(); });
    $('folder-color-picker').addEventListener('input', e => syncCustomFolderColor(e.target.value, 'picker'));
    $('folder-color-hex').addEventListener('input', e => syncCustomFolderColor(e.target.value, 'hex'));
    ['r','g','b'].forEach(channel => $('folder-color-' + channel).addEventListener('input', () => syncCustomFolderColor('', 'rgb')));
    $('folder-color-cancel').addEventListener('click', closeCustomFolderColor);
    $('folder-color-apply').addEventListener('click', applyCustomFolderColor);
    $('folder-color-overlay').addEventListener('click', e => { if (e.target.id === 'folder-color-overlay') closeCustomFolderColor(); });
    $('rename-cancel').addEventListener('click',   closeRenameModal);
    $('rename-confirm').addEventListener('click',  confirmRename);
    $('rename-name-input').addEventListener('keydown', e => { if (e.key==='Enter') confirmRename(); if (e.key==='Escape') closeRenameModal(); });
    $('rename-overlay').addEventListener('click', e => { if (e.target.id === 'rename-overlay') closeRenameModal(); });

    // Confirmação
    $('confirm-ok').addEventListener('click',     () => { if (confirmCb) confirmCb(); hideConfirm(); });
    $('confirm-cancel').addEventListener('click', hideConfirm);
    confirmOverlay.addEventListener('click', e => { if (e.target===confirmOverlay) hideConfirm(); });
    $('upload-conflict-overwrite').addEventListener('click', () => resolveUploadConflict('overwrite'));
    $('upload-conflict-keep').addEventListener('click', () => resolveUploadConflict('keep'));
    $('upload-conflict-skip').addEventListener('click', () => resolveUploadConflict('skip'));
    uploadConflictOverlay.addEventListener('click', e => { if (e.target===uploadConflictOverlay) resolveUploadConflict('skip'); });
    $('editor-link-cancel').addEventListener('click', closeEditorLinkDialog);
    $('editor-link-confirm').addEventListener('click', confirmEditorLinkDialog);
    $('editor-link-overlay').addEventListener('click', e => { if (e.target.id === 'editor-link-overlay') closeEditorLinkDialog(); });
    $('editor-link-url').addEventListener('keydown', e => { if (e.key === 'Enter') confirmEditorLinkDialog(); if (e.key === 'Escape') closeEditorLinkDialog(); });
    $('editor-link-text').addEventListener('keydown', e => { if (e.key === 'Enter') $('editor-link-url').focus(); if (e.key === 'Escape') closeEditorLinkDialog(); });
    $('editor-image-cancel').addEventListener('click', closeEditorImageDialog);
    $('editor-image-confirm').addEventListener('click', confirmEditorImageDialog);
    $('editor-image-local-choose').addEventListener('click', chooseLocalMarkdownImage);
    $('editor-image-overlay').addEventListener('click', e => { if (e.target.id === 'editor-image-overlay') closeEditorImageDialog(); });
    document.querySelectorAll('[data-image-tab]').forEach(btn => btn.addEventListener('click', () => setEditorImageTab(btn.dataset.imageTab)));
    $('editor-image-url').addEventListener('keydown', e => { if (e.key === 'Enter') confirmEditorImageDialog(); if (e.key === 'Escape') closeEditorImageDialog(); });
    $('editor-image-alt').addEventListener('keydown', e => { if (e.key === 'Enter') confirmEditorImageDialog(); if (e.key === 'Escape') closeEditorImageDialog(); });

    // Mover
    $('ctx-move').addEventListener('click', () => {
        if (ctxTarget) openMoveModal(selectedKeys.has(itemKey(ctxTarget)) ? getSelectedApps() : ctxTarget);
        hideCtx();
    });
    $('move-cancel').addEventListener('click',  closeMoveModal);
    $('move-confirm').addEventListener('click', confirmMove);
    moveOverlay.addEventListener('click', e => { if (e.target===moveOverlay) closeMoveModal(); });
    $('sel-clear').addEventListener('click', clearSelection);
    $('sel-move').addEventListener('click', () => { if (selectedKeys.size) openMoveModal(getSelectedApps()); });
    $('sel-download').addEventListener('click', downloadSelected);
    $('sel-delete').addEventListener('click', deleteSelected);

    // Sort
    $('btn-sort').addEventListener('click', e => { e.stopPropagation(); closeSidebarNewMenu(); sortMenu.classList.toggle('hidden'); });
    sortMenu.querySelectorAll('button').forEach(btn => {
        btn.addEventListener('click', () => {
            sortMode = btn.dataset.sort;
            sortMenu.querySelectorAll('button').forEach(b=>b.classList.remove('active'));
            btn.classList.add('active');
            $('sort-label').textContent = sortLabel(sortMode);
            sortMenu.classList.add('hidden'); render();
        });
    });

    // Filtros da pasta atual
    $('btn-type-filter').addEventListener('click', e => {
        e.stopPropagation();
        closeSidebarNewMenu();
        typeFilterMenu.classList.toggle('hidden');
        dateFilterMenu.classList.add('hidden');
        sortMenu.classList.add('hidden');
    });
    $('btn-date-filter').addEventListener('click', e => {
        e.stopPropagation();
        closeSidebarNewMenu();
        dateFilterMenu.classList.toggle('hidden');
        typeFilterMenu.classList.add('hidden');
        sortMenu.classList.add('hidden');
    });
    typeFilterMenu.querySelectorAll('[data-type-filter]').forEach(btn => {
        btn.addEventListener('click', () => {
            typeFilter = btn.dataset.typeFilter || 'all';
            updateFilterLabels();
            typeFilterMenu.classList.add('hidden');
            render();
        });
    });
    dateFilterMenu.querySelectorAll('[data-date-filter]').forEach(btn => {
        btn.addEventListener('click', () => {
            dateFilter = btn.dataset.dateFilter || 'all';
            updateFilterLabels();
            dateFilterMenu.classList.add('hidden');
            render();
        });
    });

    // Ops
    $('ops-toggle').addEventListener('click', toggleOpsMinimized);
    $('ops-close').addEventListener('click', requestCancelTransfers);
    $('background-activity-close').addEventListener('click', () => $('background-activity').classList.add('hidden'));

    // Context
    $('ctx-open').addEventListener('click', () => { openContextTarget(); hideCtx(); });
    $('ctx-download').addEventListener('click', () => { if (ctxTarget) window.bridge.startDownload(ctxTarget.threadId, ctxTarget.name); hideCtx(); });
    $('ctx-rename').addEventListener('click', () => { const app = ctxTarget; hideCtx(); openRenameModal(app); });
    $('ctx-favorite').addEventListener('click', () => {
        const app = ctxTarget;
        hideCtx();
        if (app && app.metadataMessageId) window.bridge.setFavorite(app.metadataMessageId, !app.favorite);
    });
    $('ctx-new-folder').addEventListener('click', () => { hideCtx(); openFolderModal(); });
    $('ctx-upload').addEventListener('click', () => { hideCtx(); window.bridge.startUpload(); });
    $('ctx-upload-folder').addEventListener('click', () => { hideCtx(); window.bridge.startFolderUpload(); });
    $('ctx-refresh').addEventListener('click', () => { hideCtx(); refreshCurrentFolder(); });
    $('ctx-grid').addEventListener('click', () => { hideCtx(); setView('grid'); });
    $('ctx-list').addEventListener('click', () => { hideCtx(); setView('list'); });
    $('ctx-clear').addEventListener('click', () => { hideCtx(); clearSelection(); });
    $('ctx-settings').addEventListener('click', () => { hideCtx(); openSettings(); });
    $('ctx-close-tab').addEventListener('click', () => { const id = ctxTabTarget; hideCtx(); if (id && id !== 'files') closeMarkdownTab(id); });
    $('ctx-close-others').addEventListener('click', () => { const id = ctxTabTarget; hideCtx(); closeOtherMarkdownTabs(id); });
    $('ctx-folder-colors').addEventListener('click', e => {
        const custom = e.target.closest('#ctx-folder-color-custom');
        if (custom && ctxTarget && ctxTarget.metadataMessageId) {
            const target = ctxTarget;
            hideCtx();
            openCustomFolderColor(target);
            return;
        }
        const btn = e.target.closest('[data-folder-color]');
        if (!btn || !ctxTarget || !ctxTarget.metadataMessageId) return;
        window.bridge.updateFolderColor(ctxTarget.metadataMessageId, btn.dataset.folderColor);
        hideCtx();
    });
    $('ctx-delete').addEventListener('click', deleteContextTarget);

    // Preview
    $('preview-close').addEventListener('click', closePreview);
    $('preview-prev').addEventListener('click',  previewPrev);
    $('preview-next').addEventListener('click',  previewNext);
    $('preview-dl').addEventListener('click', () => { if (previewAppRef) window.bridge.startDownload(previewAppRef.threadId, previewAppRef.name); });
    $('preview-body').addEventListener('click', closePreviewFromBackdrop);

    // Zoom buttons
    $('preview-zoom-in').addEventListener('click',    () => { if (activeImgWrap) activeImgWrap._zoomIn(); });
    $('preview-zoom-out').addEventListener('click',   () => { if (activeImgWrap) activeImgWrap._zoomOut(); });
    $('preview-zoom-reset').addEventListener('click', () => { if (activeImgWrap) activeImgWrap._zoomReset(); });

    // Settings
    $('btn-settings')?.addEventListener('click',   openSettings);
    $('settings-close').addEventListener('click',  closeSettings);
    settingsOverlay.addEventListener('click',      closeSettings);
    if ($('btn-save-settings')) $('btn-save-settings').addEventListener('click', saveSettings);
    if ($('btn-clear-cache')) $('btn-clear-cache').addEventListener('click', () => {
        setStatus(t('loading'), true);
        if (window.bridge && window.bridge.clearPreviewCache) window.bridge.clearPreviewCache();
    });
    if ($('btn-logout')) $('btn-logout').addEventListener('click', () => {
        askConfirm(t('logoutTitle'), t('logoutMessage'), () => {
            closeSettings();
            setStatus(t('loading'), true);
            if (window.bridge && window.bridge.logout) window.bridge.logout();
        }, 'logout');
    });
    $('setting-threads').addEventListener('input', e => { settings.downloadThreads=+e.target.value; $('thr-val').textContent=e.target.value; queueSettingsSave(); });
    $('setting-preview-threads').addEventListener('input', e => { settings.previewThreads=+e.target.value; $('preview-thr-val').textContent=e.target.value; queueSettingsSave(); });
    $('setting-upload-files').addEventListener('input', e => { settings.maxParallelUploads=+e.target.value; $('upload-files-val').textContent=e.target.value; queueSettingsSave(); });
    $('setting-upload-threads').addEventListener('input', e => { settings.uploadThreads=+e.target.value; $('upload-thr-val').textContent=e.target.value; queueSettingsSave(); });
    $('btn-encryption-standard')?.addEventListener('click', () => {
        settings.encryptionMode = 'standard';
        syncEncryptionSettings();
        window.bridge.setEncryptionMode('standard');
    });
    $('btn-encryption-custom')?.addEventListener('click', () => {
        settings.encryptionMode = 'custom';
        syncEncryptionSettings();
    });
    $('btn-configure-custom-key')?.addEventListener('click', () => {
        const confirmation = $('setting-custom-key-confirmed');
        if (!confirmation?.checked) {
            $('setting-custom-key-status').textContent = t('encryptionConfirm');
            return;
        }
        const passphrase = $('setting-custom-passphrase').value;
        if (!passphrase && settings.customKeyAvailable) {
            window.bridge.setEncryptionMode('custom');
            return;
        }
        window.bridge.configureCustomEncryption(passphrase, true);
        $('setting-custom-passphrase').value = '';
    });
    $('btn-export-custom-key')?.addEventListener('click', () => {
        askConfirm(t('encryptionExportTitle'), t('encryptionExportWarning'), () => window.bridge.exportCustomEncryptionKey(), 'encryptionProceed');
    });
    $('btn-import-custom-key')?.addEventListener('click', () => {
        askConfirm(t('encryptionImportTitle'), t('encryptionImportWarning'), () => window.bridge.importCustomEncryptionKey(), 'encryptionProceed');
    });
    $('setting-proxy-mode').addEventListener('change', e => {
        settings.proxy.proxyMode = e.target.value;
        applyProxySettingsVisibility();
        queueSettingsSave();
    });
    $('setting-proxy-auth').addEventListener('change', e => {
        settings.proxy.proxyAuthenticationEnabled = e.target.checked;
        applyProxySettingsVisibility();
        queueSettingsSave();
    });
    $('setting-proxy-host').addEventListener('change', e => { settings.proxy.proxyHost = e.target.value.trim(); queueSettingsSave(); });
    $('setting-proxy-port').addEventListener('change', e => { settings.proxy.proxyPort = Number(e.target.value) || 0; queueSettingsSave(); });
    $('setting-proxy-protocol').addEventListener('change', e => { settings.proxy.proxyProtocol = e.target.value; queueSettingsSave(); });
    $('setting-proxy-user').addEventListener('change', e => { settings.proxy.proxyUsername = e.target.value; queueSettingsSave(); });
    $('setting-proxy-pass').addEventListener('change', e => { settings.proxy.proxyPassword = e.target.value; queueSettingsSave(); });
    $('btn-test-proxy').addEventListener('click', () => {
        const proxy = settings.proxy;
        $('proxy-test-result').textContent = t('proxyTesting');
        $('proxy-test-result').className = 'proxy-test-result testing';
        $('proxy-test-result').classList.remove('hidden');
        window.bridge.testProxy(proxy.proxyHost || '', proxy.proxyPort || 0, proxy.proxyProtocol || 'auto', !!proxy.proxyAuthenticationEnabled, proxy.proxyUsername || '', proxy.proxyPassword || '', true);
    });
    $('btn-choose-dl').addEventListener('click',  () => window.bridge.chooseDownloadPath());
    $('btn-choose-tmp').addEventListener('click', () => window.bridge.chooseTempPath());
    if ($('btn-choose-data')) $('btn-choose-data').addEventListener('click', () => window.bridge.chooseDataPath());
    bindPathSetting('setting-dlpath', 'downloadPath');
    bindPathSetting('setting-tmppath', 'tempPath');
    bindPathSetting('setting-datapath', 'dataPath');
    $('setting-preview').addEventListener('change', e => {
        settings.previewEnabled = e.target.checked;
        queueSettingsSave();
        if (settings.previewEnabled) requestVisibleThumbnails();
    });
    $('setting-animations').addEventListener('change', e => {
        settings.animationsEnabled = e.target.checked;
        syncMotionPreference();
        queueSettingsSave();
    });
    $('setting-hardware').addEventListener('change', e => {
        settings.hardwareAcceleration = e.target.checked;
        setStatus(t('requiresRestart'), false);
        queueSettingsSave();
    });
    $('setting-language').addEventListener('change', e => setLanguage(e.target.value));
    $('language-select-button').addEventListener('click', e => { e.stopPropagation(); toggleLanguageMenu(); });
    $('language-select').addEventListener('keydown', e => {
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggleLanguageMenu(); }
        if (e.key === 'Escape') closeLanguageMenu();
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
            e.preventDefault();
            const order = ['system', 'pt-BR', 'en'];
            const index = Math.max(0, order.indexOf(settings.language));
            const delta = e.key === 'ArrowDown' ? 1 : -1;
            const next = order[(index + delta + order.length) % order.length];
            setLanguage(next);
        }
    });
    $('language-menu').addEventListener('click', e => {
        const btn = e.target.closest('[data-language]');
        if (!btn) return;
        setLanguage(btn.dataset.language);
        closeLanguageMenu();
    });
    document.querySelectorAll('.theme-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.theme-btn').forEach(b=>b.classList.remove('active'));
            btn.classList.add('active'); settings.theme=btn.dataset.themeValue; applyTheme(settings.theme); queueSettingsSave();
        });
    });
    document.querySelectorAll('.card-size-btn[data-size]').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.card-size-btn[data-size]').forEach(b=>b.classList.remove('active'));
            btn.classList.add('active'); settings.cardSize=btn.dataset.size; applyCardSize(settings.cardSize); queueSettingsSave();
        });
    });
    document.querySelectorAll('.video-quality-btn').forEach(btn => {
        btn.addEventListener('click', () => {
            document.querySelectorAll('.video-quality-btn').forEach(b=>b.classList.remove('active'));
            btn.classList.add('active');
            settings.videoPreviewQuality = btn.dataset.videoQuality === 'original' ? 'original' : 'low';
            queueSettingsSave();
        });
    });

    document.addEventListener('click', e => {
        if (!$('btn-sidebar-new').contains(e.target) && !$('sidebar-new-menu').contains(e.target)) closeSidebarNewMenu();
        if (!$('btn-sort').contains(e.target)) sortMenu.classList.add('hidden');
        if (!$('btn-type-filter').contains(e.target) && !typeFilterMenu.contains(e.target)) typeFilterMenu.classList.add('hidden');
        if (!$('btn-date-filter').contains(e.target) && !dateFilterMenu.contains(e.target)) dateFilterMenu.classList.add('hidden');
        if (!$('language-select').contains(e.target)) closeLanguageMenu();
        if (!ctxMenu.contains(e.target))       hideCtx();
    });
    setupSmoothScroll();
    setupMarqueeSelection();
    setupRootDropTarget();
    $('main-area').addEventListener('contextmenu', showBackgroundCtx);
    document.addEventListener('contextmenu', e => {
        if (e.defaultPrevented) return;
        if (!$('main-area').classList.contains('hidden')) showBackgroundCtx(e);
    });
    window.addEventListener('resize', scheduleCardPreviewSync);
    document.addEventListener('keydown', e => {
        if (!confirmOverlay.classList.contains('hidden')) {
            if (e.key === 'Enter') {
                e.preventDefault();
                if (confirmCb) confirmCb();
                hideConfirm();
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                hideConfirm();
                return;
            }
        }
        if (!uploadConflictOverlay.classList.contains('hidden')) {
            if (e.key === 'Enter') {
                e.preventDefault();
                resolveUploadConflict('keep');
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                resolveUploadConflict('skip');
                return;
            }
        }
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'f') {
            const activeEditor = activeWorkspaceTab !== 'files' ? markdownTabs.get(activeWorkspaceTab) : null;
            if (activeEditor) {
                e.preventDefault();
                if (e.shiftKey) openEditorReplace(activeEditor);
                else openEditorFind(activeEditor);
                return;
            }
        }
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a' && activeWorkspaceTab === 'files' && canUseGlobalShortcut(e)) {
            e.preventDefault();
            selectAllCurrentView();
            return;
        }
        if ((e.key === 'Delete' || e.key === 'Backspace') && canUseGlobalShortcut(e)) {
            const focused = focusedFileApp();
            if (!selectedKeys.size && focused) toggleSelection(focused);
            if (getSelectedApps().length) {
                e.preventDefault();
                deleteSelected();
                return;
            }
        }
        if (e.key === 'F2' && canUseGlobalShortcut(e)) {
            const items = getSelectedApps();
            if (items.length === 1) {
                e.preventDefault();
                openRenameModal(items[0]);
                return;
            }
            const focused = document.activeElement && document.activeElement.closest ? document.activeElement.closest('[data-key]') : null;
            if (focused) {
                const app = allApps.find(a => itemKey(a) === focused.dataset.key);
                if (app) {
                    e.preventDefault();
                    openRenameModal(app);
                    return;
                }
            }
        }
        if (e.key==='Escape') {
            if (!previewOverlay.classList.contains('hidden')) { closePreview(); return; }
            if (selectedKeys.size) { clearSelection(); return; }
            closeSidebarNewMenu(); sortMenu.classList.add('hidden'); typeFilterMenu.classList.add('hidden'); dateFilterMenu.classList.add('hidden'); hideCtx(); closeSettings(); closeFolderModal(); hideConfirm(); closeMoveModal();
        }
        if (!previewOverlay.classList.contains('hidden')) {
            if (e.key==='ArrowLeft')  previewPrev();
            if (e.key==='ArrowRight') previewNext();
            if (e.key==='+' || e.key==='=') { if (activeImgWrap) activeImgWrap._zoomIn(); }
            if (e.key==='-')               { if (activeImgWrap) activeImgWrap._zoomOut(); }
            if (e.key==='0')               { if (activeImgWrap) activeImgWrap._zoomReset(); }
        }
        if (e.altKey && e.key==='ArrowLeft')  { e.preventDefault(); goBack(); }
        if (e.altKey && e.key==='ArrowRight') { e.preventDefault(); goForward(); }
    });
});

// ─── Zoom em imagem ───────────────────────────────────────────────────────────
function setupImageZoomLegacy(wrap, img) {
    let scale = 1, tx = 0, ty = 0;
    let isDragging = false, lastX = 0, lastY = 0;

    function maxTranslate() {
        // Quanto o usuário pode arrastar sem sair dos limites
        const ww = wrap.clientWidth, wh = wrap.clientHeight;
        const iw = img.naturalWidth  || ww;
        const ih = img.naturalHeight || wh;
        // Tamanho renderizado da imagem antes do scale
        const rendW = Math.min(ww, wh * iw / ih);
        const rendH = Math.min(wh, ww * ih / iw);
        const maxX = Math.max(0, (rendW  * scale - ww) / 2 / scale);
        const maxY = Math.max(0, (rendH  * scale - wh) / 2 / scale);
        return { maxX, maxY };
    }

    function clamp() {
        if (scale <= 1) { tx = 0; ty = 0; return; }
        const { maxX, maxY } = maxTranslate();
        tx = Math.max(-maxX, Math.min(maxX, tx));
        ty = Math.max(-maxY, Math.min(maxY, ty));
    }

    function apply() {
        img.style.transform = `scale(${scale}) translate(${tx}px,${ty}px)`;
        wrap.style.cursor = scale > 1 ? (isDragging ? 'grabbing' : 'grab') : 'default';
    }

    function updateUI() {
        const lbl = $('preview-zoom-label');
        if (lbl) lbl.textContent = Math.round(scale * 100) + '%';
        const inBtn  = $('preview-zoom-in');
        const outBtn = $('preview-zoom-out');
        if (inBtn)  inBtn.disabled  = scale >= 6;
        if (outBtn) outBtn.disabled = scale <= 1;
    }

    function zoomTo(newScale) {
        scale = Math.max(1, Math.min(6, newScale));
        clamp(); apply(); updateUI();
    }

    // Expose para botões externos
    wrap._zoomIn    = () => zoomTo(scale * 1.3);
    wrap._zoomOut   = () => zoomTo(scale / 1.3);
    wrap._zoomReset = () => { scale = 1; tx = 0; ty = 0; apply(); updateUI(); };

    // Scroll para zoom
    wrap.addEventListener('wheel', e => {
        e.preventDefault();
        zoomTo(scale * (e.deltaY < 0 ? 1.15 : 0.87));
    }, { passive: false });

    // Arrastar para pan
    wrap.addEventListener('mousedown', e => {
        if (scale <= 1) return;
        isDragging = true; lastX = e.clientX; lastY = e.clientY;
        apply(); e.preventDefault();
    });
    document.addEventListener('mousemove', e => {
        if (!isDragging) return;
        tx += (e.clientX - lastX) / scale;
        ty += (e.clientY - lastY) / scale;
        lastX = e.clientX; lastY = e.clientY;
        clamp(); apply();
    });
    document.addEventListener('mouseup', () => { if (isDragging) { isDragging = false; apply(); } });

    // Duplo clique — toggle zoom
    wrap.addEventListener('dblclick', () => {
        if (scale > 1) { scale = 1; tx = 0; ty = 0; }
        else scale = 2.5;
        clamp(); apply(); updateUI();
    });

    updateUI();
}

function setupImageZoom(wrap, img) {
    const MIN_SCALE = 1;
    const MAX_SCALE = 5;
    const STEP = 1.25;

    let scale = 1, tx = 0, ty = 0;
    let isDragging = false, lastX = 0, lastY = 0, lastMoveAt = 0;
    let movedDuringDrag = false, skipCloseUntil = 0;
    let frame = 0, inertiaFrame = 0, vx = 0, vy = 0;
    const PAN_MULTIPLIER = 1.08;
    let metrics = { wrapW: 1, wrapH: 1, imgW: 1, imgH: 1 };
    let maxX = 0, maxY = 0;

    function measure() {
        const wrapRect = wrap.getBoundingClientRect();
        const wrapW = Math.max(1, wrapRect.width);
        const wrapH = Math.max(1, wrapRect.height);
        const naturalW = img.naturalWidth || img.clientWidth || wrapW;
        const naturalH = img.naturalHeight || img.clientHeight || wrapH;
        const fit = Math.min(wrapW / naturalW, wrapH / naturalH, 1);
        metrics = { wrapW, wrapH, imgW: naturalW * fit, imgH: naturalH * fit };
        updateLimits();
    }

    function updateLimits() {
        if (scale <= MIN_SCALE) {
            maxX = 0; maxY = 0; tx = 0; ty = 0;
            return;
        }
        maxX = Math.max(0, (metrics.imgW * scale - metrics.wrapW) / 2);
        maxY = Math.max(0, (metrics.imgH * scale - metrics.wrapH) / 2);
        clampPan();
    }

    function clampPan() {
        if (scale <= MIN_SCALE) {
            tx = 0; ty = 0;
            return;
        }
        tx = Math.max(-maxX, Math.min(maxX, tx));
        ty = Math.max(-maxY, Math.min(maxY, ty));
    }

    function applyNow() {
        frame = 0;
        clampPan();
        img.style.transform = scale <= MIN_SCALE
            ? 'translate(0,0)'
            : `translate(${tx.toFixed(1)}px, ${ty.toFixed(1)}px) scale(${scale.toFixed(4)})`;
        wrap.classList.toggle('is-zoomed', scale > MIN_SCALE);
        wrap.classList.toggle('is-dragging', isDragging);
    }

    function scheduleApply() {
        if (!frame) frame = requestAnimationFrame(applyNow);
    }

    function updateUI() {
        const lbl = $('preview-zoom-label');
        if (lbl) lbl.textContent = Math.round(scale * 100) + '%';
        const inBtn  = $('preview-zoom-in');
        const outBtn = $('preview-zoom-out');
        const fitBtn = $('preview-zoom-reset');
        if (inBtn)  inBtn.disabled  = scale >= MAX_SCALE;
        if (outBtn) outBtn.disabled = scale <= MIN_SCALE;
        if (fitBtn) fitBtn.disabled = scale <= MIN_SCALE;
    }

    function cancelInertia() {
        if (inertiaFrame) cancelAnimationFrame(inertiaFrame);
        inertiaFrame = 0;
        vx = 0;
        vy = 0;
    }

    function startInertia() {
        if (scale <= MIN_SCALE || (Math.abs(vx) < 0.4 && Math.abs(vy) < 0.4)) return;
        const step = () => {
            tx += vx;
            ty += vy;
            const beforeX = tx, beforeY = ty;
            clampPan();
            if (beforeX !== tx) vx = 0;
            if (beforeY !== ty) vy = 0;
            vx *= 0.86;
            vy *= 0.86;
            scheduleApply();
            if (Math.abs(vx) < 0.25 && Math.abs(vy) < 0.25) {
                inertiaFrame = 0;
                vx = 0;
                vy = 0;
                return;
            }
            inertiaFrame = requestAnimationFrame(step);
        };
        inertiaFrame = requestAnimationFrame(step);
    }

    function fitToScreen() {
        cancelInertia();
        scale = MIN_SCALE;
        tx = 0; ty = 0;
        isDragging = false;
        updateLimits();
        scheduleApply();
        updateUI();
    }

    function zoomTo(nextScale, focusX, focusY) {
        cancelInertia();
        const next = Math.max(MIN_SCALE, Math.min(MAX_SCALE, nextScale));
        if (next === scale) return;

        if (focusX != null && focusY != null) {
            const rect = wrap.getBoundingClientRect();
            const cx = focusX - rect.left - rect.width / 2;
            const cy = focusY - rect.top - rect.height / 2;
            const ratio = next / scale;
            tx = cx - (cx - tx) * ratio;
            ty = cy - (cy - ty) * ratio;
        }

        scale = next;
        updateLimits();
        if (scale <= MIN_SCALE) { tx = 0; ty = 0; }
        scheduleApply();
        updateUI();
    }

    function onWheel(e) {
        e.preventDefault();
        cancelInertia();
        zoomTo(scale * (e.deltaY < 0 ? 1.12 : 0.89), e.clientX, e.clientY);
    }

    function onMouseDown(e) {
        if (scale <= MIN_SCALE) return;
        cancelInertia();
        isDragging = true;
        movedDuringDrag = false;
        lastX = e.clientX;
        lastY = e.clientY;
        lastMoveAt = performance.now();
        document.addEventListener('mousemove', onMouseMove);
        document.addEventListener('mouseup', onMouseUp);
        scheduleApply();
        e.preventDefault();
    }

    function onMouseMove(e) {
        if (!isDragging) return;
        if ((e.buttons & 1) !== 1) { onMouseUp(); return; }
        const now = performance.now();
        const dt = Math.max(8, now - lastMoveAt);
        const dx = (e.clientX - lastX) * PAN_MULTIPLIER;
        const dy = (e.clientY - lastY) * PAN_MULTIPLIER;
        if (Math.abs(dx) > 1 || Math.abs(dy) > 1) movedDuringDrag = true;
        tx += dx;
        ty += dy;
        vx = Math.max(-28, Math.min(28, dx * (16 / dt)));
        vy = Math.max(-28, Math.min(28, dy * (16 / dt)));
        lastX = e.clientX;
        lastY = e.clientY;
        lastMoveAt = now;
        e.preventDefault();
        scheduleApply();
    }

    function onMouseUp() {
        if (!isDragging) return;
        isDragging = false;
        if (movedDuringDrag) skipCloseUntil = Date.now() + 180;
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        scheduleApply();
        if (movedDuringDrag) startInertia();
    }

    function onDoubleClick(e) {
        if (scale > MIN_SCALE) fitToScreen();
        else zoomTo(2.5, e.clientX, e.clientY);
    }

    function onResize() {
        measure();
        scheduleApply();
    }

    wrap._zoomIn    = () => zoomTo(scale * STEP);
    wrap._zoomOut   = () => zoomTo(scale / STEP);
    wrap._zoomReset = fitToScreen;
    wrap._skipCloseClick = () => Date.now() < skipCloseUntil;
    wrap._dispose = () => {
        if (frame) cancelAnimationFrame(frame);
        cancelInertia();
        wrap.removeEventListener('wheel', onWheel);
        wrap.removeEventListener('mousedown', onMouseDown);
        wrap.removeEventListener('dblclick', onDoubleClick);
        wrap.removeEventListener('dragstart', preventDrag);
        document.removeEventListener('mousemove', onMouseMove);
        document.removeEventListener('mouseup', onMouseUp);
        window.removeEventListener('resize', onResize);
    };

    function preventDrag(e) { e.preventDefault(); }

    wrap.addEventListener('wheel', onWheel, { passive: false });
    wrap.addEventListener('mousedown', onMouseDown);
    wrap.addEventListener('dblclick', onDoubleClick);
    wrap.addEventListener('dragstart', preventDrag);
    window.addEventListener('resize', onResize);

    if (img.complete) {
        measure();
        fitToScreen();
    } else {
        measure();
        img.addEventListener('load', () => { measure(); fitToScreen(); }, { once: true });
    }
    updateUI();
}

function disposeActiveImage() {
    if (activeVideoResizeCleanup) { activeVideoResizeCleanup(); activeVideoResizeCleanup = null; }
    if (activeImgWrap && typeof activeImgWrap._dispose === 'function') activeImgWrap._dispose();
    activeImgWrap = null;
}

// ─── Preview ──────────────────────────────────────────────────────────────────
function closePreviewFromBackdrop(e) {
    if (!activeImgWrap) return;
    if (typeof activeImgWrap._skipCloseClick === 'function' && activeImgWrap._skipCloseClick()) return;
    const img = activeImgWrap.querySelector('img');
    if (!img) return;
    const r = img.getBoundingClientRect();
    const outsideImg = e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom;
    if (outsideImg) closePreview();
}

function setupSmoothScroll() {
    // Native Chromium/WebView2 scrolling gives the same cadence as Chrome.
}

function stepSmoothScroll() {
    const main = $('main-area');
    main.scrollTop += smoothScrollVelocity;
    smoothScrollVelocity *= 0.82;
    if (Math.abs(smoothScrollVelocity) < 0.4) {
        smoothScrollFrame = 0;
        smoothScrollVelocity = 0;
        return;
    }
    smoothScrollFrame = requestAnimationFrame(stepSmoothScroll);
}

function setupWorkspaceTabs() {
    if (workspaceTabs && editorArea) return;
    const topbar = document.querySelector('.topbar');
    workspaceTabs = document.createElement('div');
    workspaceTabs.id = 'workspace-tabs';
    workspaceTabs.className = 'workspace-tabs hidden';
    workspaceTabs.innerHTML = `<button class="workspace-tab active" data-tab-id="files">
        ${icon('folder',15)}<span>${esc(t('filesTab'))}</span>
    </button>`;
    topbar.insertAdjacentElement('afterend', workspaceTabs);

    editorArea = document.createElement('section');
    editorArea.id = 'editor-area';
    editorArea.className = 'editor-area hidden';
    $('main-area').insertAdjacentElement('afterend', editorArea);
    workspaceTabs.addEventListener('click', e => {
        const btn = e.target.closest('[data-tab-id]');
        if (btn) activateWorkspaceTab(btn.dataset.tabId);
        const close = e.target.closest('[data-close-tab]');
        if (close) {
            e.stopPropagation();
            closeMarkdownTab(close.dataset.closeTab);
        }
    });
    workspaceTabs.addEventListener('mousedown', e => {
        if (e.button === 1 && e.target.closest('[data-tab-id]')) e.preventDefault();
    });
    workspaceTabs.addEventListener('auxclick', e => {
        if (e.button !== 1) return;
        const tab = e.target.closest('[data-tab-id]');
        if (!tab) return;
        e.preventDefault();
        if (tab.dataset.tabId !== 'files') closeMarkdownTab(tab.dataset.tabId);
    });
    workspaceTabs.addEventListener('contextmenu', e => {
        const tab = e.target.closest('[data-tab-id]');
        if (tab) showTabCtx(e, tab.dataset.tabId);
    });
}

function activateWorkspaceTab(id) {
    if (!workspaceTabs || !editorArea) return;
    activeWorkspaceTab = id || 'files';
    $('main-area').classList.toggle('hidden', activeWorkspaceTab !== 'files');
    editorArea.classList.toggle('hidden', activeWorkspaceTab === 'files');
    if (activeWorkspaceTab !== 'files') editorArea.classList.add('editor-area-active');
    else editorArea.classList.remove('editor-area-active');
    workspaceTabs.querySelectorAll('.workspace-tab').forEach(btn => btn.classList.toggle('active', btn.dataset.tabId === activeWorkspaceTab));
    editorArea.querySelectorAll('.markdown-editor-tab').forEach(panel => panel.classList.toggle('hidden', panel.dataset.tabId !== activeWorkspaceTab));
    if (activeWorkspaceTab !== 'files') scheduleMarkdownToolbarLabelSync(markdownTabs.get(activeWorkspaceTab));
    if (activeWorkspaceTab === 'files') refreshFilesIfNeeded();
}

function refreshFilesIfNeeded() {
    if (!filesNeedRefresh || !window.bridge || !window.bridge.loadApps) {
        render();
        return;
    }
    filesNeedRefresh = false;
    setStatus(t('loading'), true);
    window.bridge.loadApps();
}

function openMarkdownEditor(app) {
    if (!app || !app.threadId) return;
    closePreview();
    setupWorkspaceTabs();
    showWorkspaceTabs();
    const id = 'md-' + app.threadId.replace(/[^a-zA-Z0-9_-]/g, '');
    if (markdownTabs.has(id)) {
        activateWorkspaceTab(id);
        return;
    }
    const tab = document.createElement('button');
    tab.className = 'workspace-tab';
    tab.dataset.tabId = id;
    tab.innerHTML = `${icon('edit_note',16)}<span>${esc(truncate(app.name, 24))}</span>
        <span class="tab-close" data-close-tab="${esc(id)}">${icon('close',14)}</span>`;
    workspaceTabs.appendChild(tab);

    const panel = document.createElement('section');
    panel.className = 'markdown-editor-tab';
    panel.dataset.tabId = id;
    panel.innerHTML = `<div class="editor-loading">
        <div class="editor-loading-card">
            <div class="preview-spinner"></div>
            <strong>${esc(t('markdownEditor'))}</strong>
            <span>${esc(t('loading'))}</span>
        </div>
    </div>`;
    editorArea.appendChild(panel);
    markdownTabs.set(id, {
        id, app, tab, panel,
        editor:null, rawEditor:null, saveState:null, rawToggle:null, zoomLabel:null,
        toolbarWrapCleanup:null,
        rawMode:false, zoom:1,
        lastSavedMarkdown:null, pendingMarkdown:null, saveTimer:0, saving:false, queued:false
    });
    pendingMarkdownEditorApp = { app, id };
    activateWorkspaceTab(id);
    setStatus(t('loading'), true);
    window.bridge.requestPreview(app.threadId, app.name);
}

function finishMarkdownEditor(name, markdown) {
    const pending = pendingMarkdownEditorApp;
    pendingMarkdownEditorApp = null;
    if (!pending || !markdownTabs.has(pending.id)) return false;
    const entry = markdownTabs.get(pending.id);
    entry.panel.innerHTML = '';
    entry.lastSavedMarkdown = markdown || '';
    entry.panel.appendChild(createMarkdownEditor(markdown || '', entry, pending.app.name || name));
    setStatus(t('editorReady'), false);
    activateWorkspaceTab(pending.id);
    return true;
}

function closeMarkdownTab(id) {
    const tab = markdownTabs.get(id);
    if (!tab) return;
    if (typeof tab.toolbarWrapCleanup === 'function') tab.toolbarWrapCleanup();
    tab.tab.remove();
    tab.panel.remove();
    markdownTabs.delete(id);
    activateWorkspaceTab(activeWorkspaceTab === id ? 'files' : activeWorkspaceTab);
    if (markdownTabs.size === 0) {
        activeWorkspaceTab = 'files';
        $('main-area').classList.remove('hidden');
        if (editorArea) editorArea.classList.add('hidden');
        hideWorkspaceTabs();
    }
}

function showWorkspaceTabs() {
    if (!workspaceTabs) return;
    workspaceTabs.classList.remove('hidden', 'tabs-closing');
    workspaceTabs.classList.add('tabs-opening');
    clearTimeout(workspaceTabs._animTimer);
    workspaceTabs._animTimer = setTimeout(() => workspaceTabs.classList.remove('tabs-opening'), motionMs(260));
}

function hideWorkspaceTabs() {
    if (!workspaceTabs) return;
    workspaceTabs.classList.remove('tabs-opening');
    workspaceTabs.classList.add('tabs-closing');
    clearTimeout(workspaceTabs._animTimer);
    workspaceTabs._animTimer = setTimeout(() => {
        if (markdownTabs.size === 0) workspaceTabs.classList.add('hidden');
        workspaceTabs.classList.remove('tabs-closing');
    }, motionMs(220));
}

function createMarkdownEditor(markdown, entry, name) {
    const shell = document.createElement('div');
    shell.className = 'markdown-editor-shell';
    shell.innerHTML = `<div class="markdown-editor-toolbar">
        <div class="editor-doc-meta">
            <div class="editor-doc-icon">${icon('description',18)}</div>
            <div class="editor-doc-copy">
                <span class="editor-doc-label" data-editor-label="editorDocument">${esc(t('editorDocument'))}</span>
                <strong title="${esc(name)}">${esc(truncate(name, 42))}</strong>
            </div>
            <span class="editor-save-state saved" data-save-state>${esc(t('editorSaved'))}</span>
        </div>
        <div class="editor-toolbar-groups">
            <div class="editor-tool-group" aria-label="${esc(t('editorHeading'))}">
                <span class="editor-group-label" data-editor-label="editorHeading">${esc(t('editorHeading'))}</span>
                <button data-cmd="formatBlock:h1" title="${esc(t('editorHeading'))} 1">H1</button>
                <button data-cmd="formatBlock:h2" title="${esc(t('editorHeading'))} 2">H2</button>
                <button data-cmd="formatBlock:h3" title="${esc(t('editorHeading'))} 3">H3</button>
                <button data-cmd="formatBlock:h4" title="${esc(t('editorHeading'))} 4">H4</button>
                <button data-cmd="formatBlock:h5" title="${esc(t('editorHeading'))} 5">H5</button>
                <button data-cmd="formatBlock:h6" title="${esc(t('editorHeading'))} 6">H6</button>
            </div>
            <div class="editor-tool-group" aria-label="${esc(t('editorFormat'))}">
                <span class="editor-group-label" data-editor-label="editorFormat">${esc(t('editorFormat'))}</span>
                <button data-cmd="bold" data-title-key="editorBold" title="${esc(t('editorBold'))} Ctrl+B"><strong>B</strong><span data-editor-label="editorBold">${esc(t('editorBold'))}</span></button>
                <button data-cmd="italic" data-title-key="editorItalic" title="${esc(t('editorItalic'))} Ctrl+I"><em>I</em><span data-editor-label="editorItalic">${esc(t('editorItalic'))}</span></button>
                <button data-cmd="formatBlock:blockquote" data-title-key="editorQuote" title="${esc(t('editorQuote'))}">${icon('format_quote',16)}<span data-editor-label="editorQuote">${esc(t('editorQuote'))}</span></button>
                <button data-cmd="formatBlock:pre" data-title-key="editorCode" title="${esc(t('editorCode'))}">${icon('code',16)}<span data-editor-label="editorCode">${esc(t('editorCode'))}</span></button>
            </div>
            <div class="editor-tool-group" aria-label="${esc(t('editorInsert'))}">
                <span class="editor-group-label" data-editor-label="editorInsert">${esc(t('editorInsert'))}</span>
                <button data-cmd="insertUnorderedList" data-title-key="editorList" title="${esc(t('editorList'))}">${icon('format_list_bulleted',16)}<span data-editor-label="editorList">${esc(t('editorList'))}</span></button>
                <button data-cmd="insertOrderedList" data-title-key="editorOrderedList" title="${esc(t('editorOrderedList'))}">${icon('format_list_numbered',16)}<span data-editor-label="editorOrderedList">${esc(t('editorOrderedList'))}</span></button>
                <button data-cmd="link" data-title-key="editorLink" title="${esc(t('editorLink'))} Ctrl+K">${icon('link',16)}<span data-editor-label="editorLink">${esc(t('editorLink'))}</span></button>
                <button data-cmd="image" data-title-key="editorImage" title="${esc(t('editorImage'))}">${icon('image',16)}<span data-editor-label="editorImage">${esc(t('editorImage'))}</span></button>
                <button data-cmd="table" data-title-key="editorTable" title="${esc(t('editorTable'))}">${icon('table_chart',16)}<span data-editor-label="editorTable">${esc(t('editorTable'))}</span></button>
            </div>
            <div class="editor-tool-group" aria-label="${esc(t('editorView'))}">
                <span class="editor-group-label" data-editor-label="editorView">${esc(t('editorView'))}</span>
                <button class="editor-icon-only" data-cmd="find" data-title-key="editorFind" title="${esc(t('editorFind'))} Ctrl+F">${icon('search',16)}</button>
                <button class="editor-icon-only" data-cmd="toggleRaw" data-raw-toggle title="${esc(t('editorRaw'))}">${icon('code',16)}</button>
                <button data-cmd="zoomOut" title="${esc(t('zoomOut'))}">${icon('remove',16)}</button>
                <span class="editor-zoom-label" data-editor-zoom>100%</span>
                <button data-cmd="zoomIn" title="${esc(t('zoomIn'))}">${icon('add',16)}</button>
                <button data-cmd="zoomReset" title="${esc(t('editorZoomReset'))}">${icon('fit_screen',16)}</button>
            </div>
        </div>
    </div>
    <div class="editor-find-bar hidden" data-editor-find>
        <div class="editor-find-row">
            <span class="editor-find-icon">${icon('search',15)}</span>
            <input type="text" data-find-input placeholder="${esc(t('editorFindPlaceholder'))}" aria-label="${esc(t('editorFind'))}">
            <span class="editor-find-count" data-find-count></span>
            <button data-find-replace-toggle title="${esc(t('editorReplace'))} Ctrl+Shift+F">${icon('refresh',14)}</button>
            <button data-find-prev title="${esc(t('back'))}">${icon('arrow_back',14)}</button>
            <button data-find-next title="${esc(t('forward'))}">${icon('arrow_forward',14)}</button>
            <button data-find-close title="${esc(t('close'))}">${icon('close',14)}</button>
        </div>
        <div class="editor-replace-row hidden" data-replace-row>
            <span class="editor-find-icon">${icon('drive_file_move',15)}</span>
            <input type="text" data-replace-input placeholder="${esc(t('editorReplacePlaceholder'))}" aria-label="${esc(t('editorReplace'))}">
            <button class="editor-replace-action" data-replace-one>${esc(t('editorReplaceCurrent'))}</button>
            <button class="editor-replace-action" data-replace-all>${esc(t('editorReplaceAll'))}</button>
        </div>
    </div>
    <div class="markdown-editor-scroll">
        <div class="markdown-editor-page" contenteditable="true" spellcheck="true" aria-label="${esc(name)}"></div>
        <textarea class="markdown-raw-editor hidden" spellcheck="false" aria-label="${esc(t('editorRaw'))}"></textarea>
    </div>`;
    const editor = shell.querySelector('.markdown-editor-page');
    const rawEditor = shell.querySelector('.markdown-raw-editor');
    entry.editor = editor;
    entry.rawEditor = rawEditor;
    entry.saveState = shell.querySelector('[data-save-state]');
    entry.rawToggle = shell.querySelector('[data-raw-toggle]');
    entry.zoomLabel = shell.querySelector('[data-editor-zoom]');
    entry.findBar = shell.querySelector('[data-editor-find]');
    entry.findInput = shell.querySelector('[data-find-input]');
    entry.findCount = shell.querySelector('[data-find-count]');
    entry.replaceRow = shell.querySelector('[data-replace-row]');
    entry.replaceInput = shell.querySelector('[data-replace-input]');
    editor.innerHTML = markdownToHtml(markdown) || '<p><br></p>';
    prepareEditorMedia(entry);
    rawEditor.value = markdown || '';
    const editorScroll = shell.querySelector('.markdown-editor-scroll');
    entry.editorScroll = editorScroll;
    rawEditor.wrap = 'soft';
    editorScroll.style.setProperty('--editor-zoom', String(entry.zoom || 1));
    setupMomentumScroll(editorScroll, { multiplier:0.82, friction:0.84, maxVelocity:130 });
    const toolbar = shell.querySelector('.markdown-editor-toolbar');
    entry.toolbarWrapCleanup = setupMarkdownToolbarWrapObserver(toolbar);
    toolbar.addEventListener('mousedown', e => {
        if (e.target.closest('[data-cmd]')) e.preventDefault();
    });
    toolbar.addEventListener('click', e => {
        const btn = e.target.closest('[data-cmd]');
        if (!btn) return;
        e.preventDefault();
        runMarkdownEditorCommand(entry, btn.dataset.cmd);
    });
    editor.addEventListener('keydown', e => handleMarkdownEditorShortcut(e, entry));
    editor.addEventListener('input', () => scheduleMarkdownAutoSave(entry));
    editor.addEventListener('keyup', () => refreshEditorToolbarState(entry));
    editor.addEventListener('mouseup', () => refreshEditorToolbarState(entry));
    editor.addEventListener('paste', e => pasteIntoMarkdownEditor(e, entry));
    editor.addEventListener('dragstart', e => handleEditorImageDragStart(e, entry));
    editor.addEventListener('dragover', e => handleEditorImageDragOver(e, entry));
    editor.addEventListener('drop', e => handleEditorImageDrop(e, entry));
    editor.addEventListener('dragend', () => { entry.draggedImage = null; });
    rawEditor.addEventListener('keydown', e => handleMarkdownEditorShortcut(e, entry));
    rawEditor.addEventListener('input', () => {
        autoSizeRawMarkdownEditor(entry);
        scheduleMarkdownAutoSave(entry);
    });
    entry.findInput.addEventListener('input', () => updateEditorFind(entry));
    entry.findInput.addEventListener('keydown', e => {
        if (e.key === 'Enter') { e.preventDefault(); jumpEditorFind(entry, e.shiftKey ? -1 : 1); }
        if (e.key === 'Escape') { e.preventDefault(); closeEditorFind(entry); }
    });
    if (entry.replaceInput) {
        entry.replaceInput.addEventListener('keydown', e => {
            if (e.key === 'Enter') { e.preventDefault(); replaceCurrentEditorFind(entry); }
            if (e.key === 'Escape') { e.preventDefault(); closeEditorFind(entry); }
        });
    }
    shell.querySelector('[data-find-replace-toggle]').addEventListener('click', () => openEditorReplace(entry));
    shell.querySelector('[data-find-prev]').addEventListener('click', () => jumpEditorFind(entry, -1));
    shell.querySelector('[data-find-next]').addEventListener('click', () => jumpEditorFind(entry, 1));
    shell.querySelector('[data-find-close]').addEventListener('click', () => closeEditorFind(entry));
    shell.querySelector('[data-replace-one]').addEventListener('click', () => replaceCurrentEditorFind(entry));
    shell.querySelector('[data-replace-all]').addEventListener('click', () => replaceAllEditorFind(entry));
    updateRawToggle(entry);
    applyEditorZoom(entry);
    updateMarkdownSaveState(entry, 'saved');
    return shell;
}

function setupMarkdownToolbarWrapObserver(toolbar) {
    if (!toolbar) return null;
    let frame = 0;
    let lastMeasuredWidth = -1;
    let forceNext = false;
    const schedule = (force = false) => {
        forceNext = forceNext || force;
        if (frame) return;
        frame = requestAnimationFrame(() => {
            frame = 0;
            const shouldForce = forceNext;
            forceNext = false;
            const width = toolbar.isConnected && toolbar.offsetParent !== null ? toolbar.clientWidth : 0;
            if (!shouldForce && width === lastMeasuredWidth) return;
            lastMeasuredWidth = width;
            syncMarkdownToolbarLabels(toolbar);
        });
    };
    toolbar._scheduleToolbarLabels = schedule;

    let observer = null;
    if (typeof ResizeObserver === 'function') {
        observer = new ResizeObserver(schedule);
        observer.observe(toolbar);
        const groups = toolbar.querySelector('.editor-toolbar-groups');
        if (groups) observer.observe(groups);
    } else {
        window.addEventListener('resize', schedule);
    }
    schedule(true);
    return () => {
        if (frame) cancelAnimationFrame(frame);
        if (observer) observer.disconnect();
        else window.removeEventListener('resize', schedule);
        delete toolbar._scheduleToolbarLabels;
    };
}

function scheduleMarkdownToolbarLabelSync(entry) {
    const toolbar = entry && entry.panel && entry.panel.querySelector('.markdown-editor-toolbar');
    if (!toolbar) return;
    if (typeof toolbar._scheduleToolbarLabels === 'function') toolbar._scheduleToolbarLabels(true);
    else syncMarkdownToolbarLabels(toolbar);
}

function syncMarkdownToolbarLabels(toolbar) {
    const groupsWrap = toolbar && toolbar.querySelector('.editor-toolbar-groups');
    if (!groupsWrap || !toolbar.isConnected || toolbar.offsetParent === null) return;
    toolbar.classList.remove('editor-toolbar-groups-wrapped');
    const groups = Array.from(groupsWrap.children).filter(child => child.classList && child.classList.contains('editor-tool-group'));
    if (groups.length < 2) return;
    const firstTop = groups[0].offsetTop;
    const wrapped = groups.some(group => Math.abs(group.offsetTop - firstTop) > 2);
    toolbar.classList.toggle('editor-toolbar-groups-wrapped', wrapped);
}

function runMarkdownEditorCommand(entry, command) {
    if (!entry) return;
    if (command === 'toggleRaw') { toggleRawMarkdown(entry); return; }
    if (command === 'find') { openEditorFind(entry); return; }
    if (command === 'zoomIn') { setEditorZoom(entry, (entry.zoom || 1) + 0.1); return; }
    if (command === 'zoomOut') { setEditorZoom(entry, (entry.zoom || 1) - 0.1); return; }
    if (command === 'zoomReset') { setEditorZoom(entry, 1); return; }

    if (entry.rawMode) {
        runRawMarkdownCommand(entry, command);
        return;
    }

    const editor = entry.editor;
    if (!editor) return;
    editor.focus();
    if (command.startsWith('formatBlock:')) {
        toggleBlockFormat(editor, command.split(':')[1]);
    } else if (command === 'link') {
        openEditorLinkDialog(entry);
        return;
    } else if (command === 'image') {
        openEditorImageDialog(entry);
        return;
    } else if (command === 'table') {
        insertMarkdownTable(entry, captureEditorInsertionState(entry, false));
        return;
    } else {
        document.execCommand(command, false, null);
    }
    normalizeEditorDom(editor);
    prepareEditorMedia(entry);
    refreshEditorToolbarState(entry);
    editor.dispatchEvent(new Event('input', { bubbles:true }));
}

function handleMarkdownEditorShortcut(e, entry) {
    if (!e.ctrlKey && !e.metaKey) return;
    const key = e.key.toLowerCase();
    if (key === 'f' && e.shiftKey) { e.preventDefault(); openEditorReplace(entry); return; }
    if (key === 'f') { e.preventDefault(); openEditorFind(entry); return; }
    if (key === 'b') { e.preventDefault(); runMarkdownEditorCommand(entry, 'bold'); }
    if (key === 'i') { e.preventDefault(); runMarkdownEditorCommand(entry, 'italic'); }
    if (key === 'k') { e.preventDefault(); runMarkdownEditorCommand(entry, 'link'); }
    if (key === '+' || key === '=') { e.preventDefault(); runMarkdownEditorCommand(entry, 'zoomIn'); }
    if (key === '-') { e.preventDefault(); runMarkdownEditorCommand(entry, 'zoomOut'); }
    if (key === '0') { e.preventDefault(); runMarkdownEditorCommand(entry, 'zoomReset'); }
    if (e.shiftKey && e.key === '8') { e.preventDefault(); runMarkdownEditorCommand(entry, 'insertUnorderedList'); }
    if (e.shiftKey && e.key === '7') { e.preventDefault(); runMarkdownEditorCommand(entry, 'insertOrderedList'); }
}

function getEditorMarkdown(entry) {
    if (!entry) return '';
    return entry.rawMode && entry.rawEditor
        ? entry.rawEditor.value.replace(/\r\n/g, '\n')
        : editorHtmlToMarkdown(entry.editor);
}

function toggleRawMarkdown(entry) {
    if (!entry || !entry.editor || !entry.rawEditor) return;
    closeEditorFind(entry, false);
    const scrollState = captureMarkdownEditorScroll(entry);
    if (!entry.rawMode) {
        entry.rawEditor.value = editorHtmlToMarkdown(entry.editor);
        entry.editor.classList.add('hidden');
        entry.rawEditor.classList.remove('hidden');
        entry.rawMode = true;
        autoSizeRawMarkdownEditor(entry);
        focusWithoutScroll(entry.rawEditor);
    } else {
        entry.editor.innerHTML = markdownToHtml(entry.rawEditor.value) || '<p><br></p>';
        normalizeEditorDom(entry.editor);
        prepareEditorMedia(entry);
        entry.rawEditor.classList.add('hidden');
        entry.editor.classList.remove('hidden');
        entry.rawMode = false;
        focusWithoutScroll(entry.editor);
    }
    restoreMarkdownEditorScroll(entry, scrollState);
    updateRawToggle(entry);
    scheduleMarkdownAutoSave(entry);
}

function captureMarkdownEditorScroll(entry) {
    const scroll = entry && (entry.editorScroll || entry.panel?.querySelector('.markdown-editor-scroll'));
    if (!scroll) return { ratio:0, top:0 };
    const max = Math.max(0, scroll.scrollHeight - scroll.clientHeight);
    return { ratio:max ? scroll.scrollTop / max : 0, top:scroll.scrollTop };
}

function restoreMarkdownEditorScroll(entry, state) {
    const scroll = entry && (entry.editorScroll || entry.panel?.querySelector('.markdown-editor-scroll'));
    if (!scroll || !state) return;
    requestAnimationFrame(() => {
        if (entry.rawMode) autoSizeRawMarkdownEditor(entry);
        const max = Math.max(0, scroll.scrollHeight - scroll.clientHeight);
        scroll.scrollTop = max ? Math.round(state.ratio * max) : Math.min(state.top || 0, max);
    });
}

function autoSizeRawMarkdownEditor(entry) {
    const textarea = entry && entry.rawEditor;
    const scroll = entry && (entry.editorScroll || entry.panel?.querySelector('.markdown-editor-scroll'));
    if (!textarea || textarea.classList.contains('hidden')) return;
    textarea.style.height = 'auto';
    const minHeight = scroll ? Math.max(260, scroll.clientHeight - 74) : 260;
    textarea.style.height = Math.max(minHeight, textarea.scrollHeight + 2) + 'px';
}

function focusWithoutScroll(el) {
    if (!el) return;
    try { el.focus({ preventScroll:true }); }
    catch { el.focus(); }
}

function updateRawToggle(entry) {
    if (!entry || !entry.rawToggle) return;
    entry.rawToggle.classList.toggle('active', !!entry.rawMode);
    entry.rawToggle.title = entry.rawMode ? t('editorRich') : t('editorRaw');
    entry.rawToggle.innerHTML = icon(entry.rawMode ? 'description' : 'code', 16);
}

function refreshOpenEditorsI18n() {
    if (workspaceTabs) {
        const filesLabel = workspaceTabs.querySelector('[data-tab-id="files"] span');
        if (filesLabel) filesLabel.textContent = t('filesTab');
    }
    markdownTabs.forEach(entry => {
        if (!entry || !entry.panel) return;
        entry.panel.querySelectorAll('[data-editor-label]').forEach(el => {
            const key = el.dataset.editorLabel;
            if (key) el.textContent = t(key);
        });
        entry.panel.querySelectorAll('[data-cmd^="formatBlock:h"]').forEach(btn => {
            btn.title = t('editorHeading') + ' ' + btn.dataset.cmd.replace('formatBlock:h', '');
        });
        entry.panel.querySelectorAll('[data-title-key]').forEach(btn => {
            const key = btn.dataset.titleKey;
            if (!key) return;
            const shortcut = btn.dataset.cmd === 'bold' ? ' Ctrl+B'
                : (btn.dataset.cmd === 'italic' ? ' Ctrl+I'
                    : (btn.dataset.cmd === 'link' ? ' Ctrl+K'
                        : (btn.dataset.cmd === 'find' ? ' Ctrl+F' : '')));
            btn.title = t(key) + shortcut;
        });
        const zoomOut = entry.panel.querySelector('[data-cmd="zoomOut"]');
        const zoomIn = entry.panel.querySelector('[data-cmd="zoomIn"]');
        const zoomReset = entry.panel.querySelector('[data-cmd="zoomReset"]');
        if (zoomOut) zoomOut.title = t('zoomOut');
        if (zoomIn) zoomIn.title = t('zoomIn');
        if (zoomReset) zoomReset.title = t('editorZoomReset');
        if (entry.rawEditor) entry.rawEditor.setAttribute('aria-label', t('editorRaw'));
        if (entry.findInput) {
            entry.findInput.placeholder = t('editorFindPlaceholder');
            entry.findInput.setAttribute('aria-label', t('editorFind'));
        }
        if (entry.replaceInput) {
            entry.replaceInput.placeholder = t('editorReplacePlaceholder');
            entry.replaceInput.setAttribute('aria-label', t('editorReplace'));
        }
        const replaceOne = entry.panel.querySelector('[data-replace-one]');
        const replaceAll = entry.panel.querySelector('[data-replace-all]');
        const replaceToggle = entry.panel.querySelector('[data-find-replace-toggle]');
        if (replaceOne) replaceOne.textContent = t('editorReplaceCurrent');
        if (replaceAll) replaceAll.textContent = t('editorReplaceAll');
        if (replaceToggle) replaceToggle.title = t('editorReplace') + ' Ctrl+Shift+F';
        updateRawToggle(entry);
        const state = ['saving', 'dirty', 'error'].find(name => entry.saveState && entry.saveState.classList.contains(name)) || 'saved';
        updateMarkdownSaveState(entry, state);
        scheduleMarkdownToolbarLabelSync(entry);
    });
}

function openEditorFind(entry) {
    if (!entry || !entry.findBar || !entry.findInput) return;
    entry.findBar.classList.remove('replace-mode');
    if (entry.replaceRow) entry.replaceRow.classList.add('hidden');
    entry.findBar.classList.remove('hidden');
    updateEditorFind(entry);
    entry.findInput.focus();
    entry.findInput.select();
}

function openEditorReplace(entry) {
    if (!entry || !entry.findBar || !entry.findInput) return;
    entry.findBar.classList.add('replace-mode');
    if (entry.replaceRow) entry.replaceRow.classList.remove('hidden');
    entry.findBar.classList.remove('hidden');
    updateEditorFind(entry);
    entry.findInput.focus();
    entry.findInput.select();
}

function closeEditorFind(entry, restoreFocus = true) {
    if (!entry) return;
    clearEditorFindMarks(entry);
    entry.findIndex = -1;
    entry.findMatches = [];
    if (entry.findBar) entry.findBar.classList.add('hidden');
    if (entry.replaceRow) entry.replaceRow.classList.add('hidden');
    if (entry.findBar) entry.findBar.classList.remove('replace-mode');
    if (entry.findCount) entry.findCount.textContent = '';
    if (restoreFocus) (entry.rawMode ? entry.rawEditor : entry.editor)?.focus();
}

function updateEditorFind(entry) {
    if (!entry || !entry.findInput) return;
    const query = entry.findInput.value || '';
    entry.findIndex = -1;
    entry.findMatches = [];
    if (entry.rawMode) {
        updateRawEditorFind(entry, query);
    } else {
        updateRichEditorFind(entry, query);
    }
    if (entry.findMatches.length) {
        entry.findIndex = 0;
        selectEditorFindMatch(entry, 0);
    } else {
        updateEditorFindCount(entry);
    }
}

function updateRawEditorFind(entry, query) {
    const textarea = entry.rawEditor;
    if (!textarea) return;
    if (!query) return;
    const haystack = textarea.value.toLowerCase();
    const needle = query.toLowerCase();
    let index = haystack.indexOf(needle);
    while (index >= 0) {
        entry.findMatches.push({ start:index, end:index + query.length });
        index = haystack.indexOf(needle, index + Math.max(1, query.length));
    }
}

function updateRichEditorFind(entry, query) {
    clearEditorFindMarks(entry);
    if (!query || !entry.editor) return;
    const needle = query.toLowerCase();
    const walker = document.createTreeWalker(entry.editor, NodeFilter.SHOW_TEXT, {
        acceptNode(node) {
            if (!node.nodeValue || !node.nodeValue.toLowerCase().includes(needle)) return NodeFilter.FILTER_REJECT;
            if (node.parentElement && node.parentElement.closest('mark.editor-find-match')) return NodeFilter.FILTER_REJECT;
            return NodeFilter.FILTER_ACCEPT;
        }
    });
    const textNodes = [];
    while (walker.nextNode()) textNodes.push(walker.currentNode);
    textNodes.forEach(node => markTextNodeMatches(node, query, entry.findMatches));
}

function markTextNodeMatches(node, query, matches) {
    const text = node.nodeValue;
    const lower = text.toLowerCase();
    const needle = query.toLowerCase();
    const frag = document.createDocumentFragment();
    let pos = 0, index = lower.indexOf(needle);
    while (index >= 0) {
        if (index > pos) frag.appendChild(document.createTextNode(text.slice(pos, index)));
        const mark = document.createElement('mark');
        mark.className = 'editor-find-match';
        mark.textContent = text.slice(index, index + query.length);
        frag.appendChild(mark);
        matches.push(mark);
        pos = index + query.length;
        index = lower.indexOf(needle, pos);
    }
    if (pos < text.length) frag.appendChild(document.createTextNode(text.slice(pos)));
    node.replaceWith(frag);
}

function clearEditorFindMarks(entry) {
    if (!entry || !entry.editor) return;
    entry.editor.querySelectorAll('mark.editor-find-match').forEach(mark => {
        mark.replaceWith(document.createTextNode(mark.textContent || ''));
    });
    entry.editor.normalize();
}

function jumpEditorFind(entry, direction) {
    if (!entry || !entry.findMatches || !entry.findMatches.length) {
        updateEditorFind(entry);
        return;
    }
    const total = entry.findMatches.length;
    entry.findIndex = (Number(entry.findIndex || 0) + direction + total) % total;
    selectEditorFindMatch(entry, entry.findIndex);
}

function selectEditorFindMatch(entry, index) {
    if (!entry || !entry.findMatches || !entry.findMatches.length) {
        updateEditorFindCount(entry);
        return;
    }
    if (entry.rawMode) {
        const match = entry.findMatches[index];
        scrollRawEditorToMatch(entry, match);
    } else {
        entry.findMatches.forEach(mark => mark.classList.remove('current'));
        const mark = entry.findMatches[index];
        if (mark && mark.parentNode) {
            mark.classList.add('current');
            mark.scrollIntoView({ block:'center', inline:'nearest' });
        }
    }
    updateEditorFindCount(entry);
}

function scrollRawEditorToMatch(entry, match) {
    if (!entry || !entry.rawEditor || !match) return;
    const textarea = entry.rawEditor;
    const scroll = entry.editorScroll || entry.panel?.querySelector('.markdown-editor-scroll');
    if (!scroll) return;
    const before = textarea.value.slice(0, match.start);
    const lineIndex = before.split('\n').length - 1;
    const lineHeight = parseFloat(getComputedStyle(textarea).lineHeight) || 22;
    const top = textarea.offsetTop + lineIndex * lineHeight - scroll.clientHeight / 2;
    scroll.scrollTop = Math.max(0, top);
}

function updateEditorFindCount(entry) {
    if (!entry || !entry.findCount) return;
    const total = entry.findMatches ? entry.findMatches.length : 0;
    if (!entry.findInput || !entry.findInput.value) {
        entry.findCount.textContent = '';
    } else if (!total) {
        entry.findCount.textContent = t('editorFindNoResults');
    } else {
        entry.findCount.textContent = (Number(entry.findIndex) + 1) + '/' + total;
    }
}

function replaceCurrentEditorFind(entry) {
    if (!entry || !entry.findInput || !entry.findInput.value) return;
    if (!entry.findMatches || !entry.findMatches.length) updateEditorFind(entry);
    if (!entry.findMatches || !entry.findMatches.length) return;
    const replacement = entry.replaceInput ? entry.replaceInput.value : '';
    const focusTarget = document.activeElement === entry.replaceInput ? entry.replaceInput : entry.findInput;

    if (entry.rawMode) {
        const match = entry.findMatches[Math.max(0, entry.findIndex || 0)];
        if (!match || !entry.rawEditor) return;
        entry.rawEditor.value = entry.rawEditor.value.slice(0, match.start) + replacement + entry.rawEditor.value.slice(match.end);
        autoSizeRawMarkdownEditor(entry);
        entry.rawEditor.dispatchEvent(new Event('input', { bubbles:true }));
    } else {
        const mark = entry.findMatches[Math.max(0, entry.findIndex || 0)];
        if (!mark || !mark.parentNode) return;
        mark.replaceWith(document.createTextNode(replacement));
        entry.editor.normalize();
        entry.editor.dispatchEvent(new Event('input', { bubbles:true }));
    }

    updateEditorFind(entry);
    focusWithoutScroll(focusTarget);
}

function replaceAllEditorFind(entry) {
    if (!entry || !entry.findInput || !entry.findInput.value) return;
    if (!entry.findMatches || !entry.findMatches.length) updateEditorFind(entry);
    if (!entry.findMatches || !entry.findMatches.length) return;
    const replacement = entry.replaceInput ? entry.replaceInput.value : '';
    const focusTarget = document.activeElement === entry.replaceInput ? entry.replaceInput : entry.findInput;

    if (entry.rawMode) {
        const pattern = new RegExp(escapeRegExp(entry.findInput.value), 'gi');
        entry.rawEditor.value = entry.rawEditor.value.replace(pattern, () => replacement);
        autoSizeRawMarkdownEditor(entry);
        entry.rawEditor.dispatchEvent(new Event('input', { bubbles:true }));
    } else {
        entry.findMatches.slice().forEach(mark => {
            if (mark && mark.parentNode) mark.replaceWith(document.createTextNode(replacement));
        });
        entry.editor.normalize();
        entry.editor.dispatchEvent(new Event('input', { bubbles:true }));
    }

    updateEditorFind(entry);
    focusWithoutScroll(focusTarget);
}

function escapeRegExp(value) {
    return String(value || '').replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function runRawMarkdownCommand(entry, command) {
    const textarea = entry.rawEditor;
    if (!textarea) return;
    textarea.focus();
    if (command.startsWith('formatBlock:')) {
        const tag = command.split(':')[1];
        if (/h[1-6]/.test(tag)) toggleLinePrefix(textarea, '#'.repeat(Number(tag[1])) + ' ', /^#{1,6}\s+/);
        else if (tag === 'blockquote') toggleLinePrefix(textarea, '> ', /^>\s?/);
        else if (tag === 'pre') toggleFence(textarea);
    } else if (command === 'bold') wrapTextareaSelection(textarea, '**', '**', 'texto');
    else if (command === 'italic') wrapTextareaSelection(textarea, '*', '*', 'texto');
    else if (command === 'insertUnorderedList') toggleLinePrefix(textarea, '- ', /^[-*+]\s+/);
    else if (command === 'insertOrderedList') toggleOrderedPrefix(textarea);
    else if (command === 'table') insertTextareaText(textarea, defaultMarkdownTable());
    else if (command === 'link') {
        openEditorLinkDialog(entry);
        return;
    } else if (command === 'image') {
        openEditorImageDialog(entry);
        return;
    }
    textarea.dispatchEvent(new Event('input', { bubbles:true }));
}

function toggleLinePrefix(textarea, prefix, matcher) {
    const start = textarea.selectionStart, end = textarea.selectionEnd;
    const value = textarea.value;
    const lineStart = value.lastIndexOf('\n', Math.max(0, start - 1)) + 1;
    const lineEndPos = value.indexOf('\n', end);
    const lineEnd = lineEndPos === -1 ? value.length : lineEndPos;
    const block = value.slice(lineStart, lineEnd);
    const lines = block.split('\n');
    const allHave = lines.every(line => !line.trim() || matcher.test(line));
    const next = lines.map(line => {
        if (!line.trim()) return line;
        const stripped = line.replace(matcher, '');
        return allHave ? stripped : prefix + stripped;
    }).join('\n');
    textarea.value = value.slice(0, lineStart) + next + value.slice(lineEnd);
    textarea.setSelectionRange(lineStart, lineStart + next.length);
}

function toggleOrderedPrefix(textarea) {
    const start = textarea.selectionStart, end = textarea.selectionEnd;
    const value = textarea.value;
    const lineStart = value.lastIndexOf('\n', Math.max(0, start - 1)) + 1;
    const lineEndPos = value.indexOf('\n', end);
    const lineEnd = lineEndPos === -1 ? value.length : lineEndPos;
    const block = value.slice(lineStart, lineEnd);
    const lines = block.split('\n');
    const allHave = lines.every(line => !line.trim() || /^\d+\.\s+/.test(line));
    let n = 1;
    const next = lines.map(line => {
        if (!line.trim()) return line;
        return allHave ? line.replace(/^\d+\.\s+/, '') : line.replace(/^\d+\.\s+/, '').replace(/^/, (n++) + '. ');
    }).join('\n');
    textarea.value = value.slice(0, lineStart) + next + value.slice(lineEnd);
    textarea.setSelectionRange(lineStart, lineStart + next.length);
}

function toggleFence(textarea) {
    const start = textarea.selectionStart, end = textarea.selectionEnd;
    const value = textarea.value;
    const selected = value.slice(start, end) || 'codigo';
    const fenced = selected.startsWith('```\n') && selected.endsWith('\n```');
    const next = fenced ? selected.replace(/^```\n/, '').replace(/\n```$/, '') : '```\n' + selected.replace(/\n?$/, '\n') + '```';
    textarea.value = value.slice(0, start) + next + value.slice(end);
    textarea.setSelectionRange(start, start + next.length);
}

function wrapTextareaSelection(textarea, before, after, placeholder) {
    const start = textarea.selectionStart, end = textarea.selectionEnd;
    const value = textarea.value;
    const selected = value.slice(start, end) || placeholder || '';
    const hasWrap = selected.startsWith(before) && selected.endsWith(after);
    const next = hasWrap ? selected.slice(before.length, selected.length - after.length) : before + selected + after;
    textarea.value = value.slice(0, start) + next + value.slice(end);
    textarea.setSelectionRange(start + (hasWrap ? 0 : before.length), start + next.length - (hasWrap ? 0 : after.length));
}

function insertTextareaText(textarea, text) {
    const start = textarea.selectionStart, end = textarea.selectionEnd;
    textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
    textarea.setSelectionRange(start + text.length, start + text.length);
}

function toggleBlockFormat(editor, targetTag) {
    const block = getCurrentEditorBlock(editor);
    if (!block) {
        document.execCommand('formatBlock', false, targetTag);
        return;
    }
    const current = block.tagName.toLowerCase();
    if (current === targetTag || (targetTag === 'pre' && block.closest('pre'))) {
        replaceBlock(block.closest('pre') || block, 'p');
    } else {
        replaceBlock(block, targetTag);
    }
}

function getCurrentEditorBlock(editor) {
    const sel = window.getSelection();
    if (!sel || !sel.rangeCount) return null;
    let node = sel.anchorNode;
    if (!node || !editor.contains(node)) return null;
    if (node.nodeType === Node.TEXT_NODE) node = node.parentNode;
    while (node && node !== editor) {
        if (node.nodeType === Node.ELEMENT_NODE && /^(p|div|h[1-6]|blockquote|pre|li)$/i.test(node.tagName)) return node;
        node = node.parentNode;
    }
    return null;
}

function replaceBlock(block, tag) {
    const next = document.createElement(tag);
    if (tag === 'pre') {
        const code = document.createElement('code');
        code.textContent = block.innerText.replace(/\n+$/, '') || 'codigo';
        next.appendChild(code);
    } else {
        next.innerHTML = block.tagName.toLowerCase() === 'pre'
            ? esc(block.innerText.replace(/\n+$/, '') || '')
            : (block.innerHTML || '<br>');
    }
    block.replaceWith(next);
    placeCaretAtEnd(next);
}

function placeCaretAtEnd(el) {
    const range = document.createRange();
    range.selectNodeContents(el);
    range.collapse(false);
    const sel = window.getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
}

function normalizeEditorDom(editor) {
    editor.querySelectorAll('pre pre').forEach(pre => {
        pre.replaceWith(document.createTextNode(pre.innerText));
    });
    editor.querySelectorAll('pre').forEach(pre => {
        const nested = pre.querySelectorAll('pre, blockquote');
        nested.forEach(el => el.replaceWith(document.createTextNode(el.innerText)));
        if (!pre.querySelector('code')) {
            const code = document.createElement('code');
            code.textContent = pre.innerText.replace(/\n+$/, '');
            pre.textContent = '';
            pre.appendChild(code);
        }
    });
}

function refreshEditorToolbarState(entry) {
    if (!entry || !entry.editor || entry.rawMode) return;
    const toolbar = entry.panel.querySelector('.markdown-editor-toolbar');
    if (!toolbar) return;
    const block = getCurrentEditorBlock(entry.editor);
    const tag = block ? block.tagName.toLowerCase() : '';
    toolbar.querySelectorAll('[data-cmd^="formatBlock:"]').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.cmd === 'formatBlock:' + tag);
    });
    toolbar.querySelector('[data-cmd="bold"]')?.classList.toggle('active', document.queryCommandState('bold'));
    toolbar.querySelector('[data-cmd="italic"]')?.classList.toggle('active', document.queryCommandState('italic'));
}

function pastePlainText(e, editor) {
    if (!e.clipboardData) return;
    e.preventDefault();
    const text = e.clipboardData.getData('text/plain');
    document.execCommand('insertText', false, text);
    normalizeEditorDom(editor);
}

function pasteIntoMarkdownEditor(e, entry) {
    const items = e.clipboardData ? Array.from(e.clipboardData.items || []) : [];
    const imageItem = items.find(item => item.kind === 'file' && /^image\//i.test(item.type || ''));
    if (!imageItem) {
        pastePlainText(e, entry.editor);
        return;
    }

    e.preventDefault();
    const file = imageItem.getAsFile();
    if (!file) return;
    const reader = new FileReader();
    const selection = captureEditorInsertionState(entry, false);
    const alt = file.name ? file.name.replace(/\.[^.]+$/, '') : 'imagem';
    pendingPastedMarkdownImages.set(entry.id, { entry, selection, alt });
    setStatus(t('editorPasteImage'), true);
    reader.onload = () => {
        window.bridge.uploadMarkdownImageData(entry.id, entry.app.threadId || '', file.name || 'clipboard.png', String(reader.result || ''));
    };
    reader.onerror = () => {
        pendingPastedMarkdownImages.delete(entry.id);
        setStatus(t('errorPrefix') + t('editorSaveError'), false);
    };
    reader.readAsDataURL(file);
}

function prepareEditorMedia(entry) {
    const editor = entry && entry.editor;
    if (!editor) return;
    editor.querySelectorAll('img').forEach(img => {
        img.draggable = true;
        img.setAttribute('contenteditable', 'false');
        const original = img.dataset.organessoniumImage || img.getAttribute('src') || '';
        const isEncryptedAttachment = original.startsWith('organessonium-image://v1/');
        const isEncryptedDriveImage = original.startsWith('organessonium-drive-image://');
        if ((!isEncryptedAttachment && !isEncryptedDriveImage) || img.dataset.organessoniumReady === 'true' || img.dataset.organessoniumLoading === 'true') return;
        img.dataset.organessoniumImage = original;
        img.dataset.organessoniumLoading = 'true';
        if (isEncryptedAttachment && window.bridge?.resolveEncryptedMarkdownImage) {
            window.bridge.resolveEncryptedMarkdownImage(entry.id, entry.app.threadId || '', original);
        } else if (isEncryptedDriveImage && window.bridge?.resolveEncryptedDriveMarkdownImage) {
            window.bridge.resolveEncryptedDriveMarkdownImage(entry.id, original.slice('organessonium-drive-image://'.length));
        }
    });
}

function handleEditorImageDragStart(e, entry) {
    const img = e.target && e.target.closest ? e.target.closest('.markdown-editor-page img') : null;
    if (!img || !entry || !entry.editor || !entry.editor.contains(img)) return;
    entry.draggedImage = img;
    try {
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', img.getAttribute('alt') || img.getAttribute('src') || '');
    } catch (_) {}
}

function handleEditorImageDragOver(e, entry) {
    if (!entry || !entry.draggedImage) return;
    e.preventDefault();
    try { e.dataTransfer.dropEffect = 'move'; } catch (_) {}
}

function handleEditorImageDrop(e, entry) {
    if (!entry || !entry.draggedImage) return;
    const source = entry.draggedImage;
    entry.draggedImage = null;
    if (!source.isConnected) return;
    e.preventDefault();
    const range = caretRangeFromPoint(e.clientX, e.clientY);
    if (!range || !entry.editor.contains(range.startContainer)) return;
    const clone = source.cloneNode(true);
    clone.draggable = true;
    source.remove();
    range.insertNode(clone);
    range.setStartAfter(clone);
    range.collapse(true);
    const selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    normalizeEditorDom(entry.editor);
    prepareEditorMedia(entry);
    entry.editor.dispatchEvent(new Event('input', { bubbles:true }));
}

function caretRangeFromPoint(x, y) {
    if (document.caretRangeFromPoint) return document.caretRangeFromPoint(x, y);
    if (document.caretPositionFromPoint) {
        const pos = document.caretPositionFromPoint(x, y);
        if (!pos) return null;
        const range = document.createRange();
        range.setStart(pos.offsetNode, pos.offset);
        range.collapse(true);
        return range;
    }
    return null;
}

function setEditorZoom(entry, value) {
    if (!entry) return;
    entry.zoom = Math.max(0.7, Math.min(1.8, Number(value) || 1));
    applyEditorZoom(entry);
}

function applyEditorZoom(entry) {
    if (!entry || !entry.panel) return;
    const scroll = entry.panel.querySelector('.markdown-editor-scroll');
    if (scroll) scroll.style.setProperty('--editor-zoom', String(entry.zoom || 1));
    if (entry.zoomLabel) entry.zoomLabel.textContent = Math.round((entry.zoom || 1) * 100) + '%';
    if (entry.rawMode) requestAnimationFrame(() => autoSizeRawMarkdownEditor(entry));
}

function scheduleMarkdownAutoSave(entry) {
    if (!entry || (!entry.editor && !entry.rawEditor)) return;
    const markdown = getEditorMarkdown(entry);
    if (markdown === entry.lastSavedMarkdown) {
        updateMarkdownSaveState(entry, 'saved');
        return;
    }
    entry.pendingMarkdown = markdown;
    updateMarkdownSaveState(entry, 'dirty');
    clearTimeout(entry.saveTimer);
    entry.saveTimer = setTimeout(() => saveMarkdownNow(entry), 1800);
}

function saveMarkdownNow(entry) {
    if (!entry || (!entry.editor && !entry.rawEditor)) return;
    const markdown = entry.pendingMarkdown ?? getEditorMarkdown(entry);
    if (markdown === entry.lastSavedMarkdown) {
        updateMarkdownSaveState(entry, 'saved');
        return;
    }
    if (entry.saving) {
        entry.queued = true;
        return;
    }
    entry.saving = true;
    entry.queued = false;
    entry.pendingMarkdown = markdown;
    updateMarkdownSaveState(entry, 'saving');
    window.bridge.saveMarkdown(entry.id, entry.app.threadId, entry.app.metadataMessageId || '', entry.app.name, entry.app.path || '/', markdown);
}

function finishMarkdownAutoSave(id, payload) {
    const entry = markdownTabs.get(id);
    if (!entry) return;
    const oldThreadId = entry.app.threadId;
    const oldMetadataMessageId = entry.app.metadataMessageId;
    entry.saving = false;
    entry.lastSavedMarkdown = entry.pendingMarkdown ?? getEditorMarkdown(entry);
    entry.pendingMarkdown = null;
    entry.app = Object.assign(entry.app, payload);
    const idx = allApps.findIndex(a => (oldMetadataMessageId && a.metadataMessageId === oldMetadataMessageId) || (oldThreadId && a.threadId === oldThreadId));
    if (idx >= 0) allApps[idx] = Object.assign(allApps[idx], payload);
    else allApps.push(Object.assign({}, payload));
    filesNeedRefresh = true;
    if (activeWorkspaceTab === 'files') refreshFilesIfNeeded();
    updateMarkdownSaveState(entry, 'saved');
    setStatus(t('editorSaved'), false);
    if (entry.queued || getEditorMarkdown(entry) !== entry.lastSavedMarkdown) {
        scheduleMarkdownAutoSave(entry);
    }
}

function failMarkdownAutoSave(id, msg) {
    const entry = markdownTabs.get(id);
    if (!entry) return;
    entry.saving = false;
    updateMarkdownSaveState(entry, 'error');
    setStatus(t('editorSaveError') + (msg ? ': ' + msg : ''), false);
}

function updateMarkdownSaveState(entry, state) {
    if (!entry || !entry.saveState) return;
    const labels = {
        saved:t('editorSaved'),
        dirty:t('editorUnsaved'),
        saving:t('editorSaving'),
        error:t('editorSaveError')
    };
    entry.saveState.className = 'editor-save-state ' + state;
    entry.saveState.textContent = labels[state] || labels.saved;
}

function editorHtmlToMarkdown(root) {
    const blocks = Array.from(root.childNodes)
        .map(node => blockToMarkdown(node))
        .filter(block => block !== null && block !== undefined);
    return blocks.join('\n\n').replace(/\n{3,}/g, '\n\n').trimEnd();
}

function blockToMarkdown(node, index = 1) {
    if (node.nodeType === Node.TEXT_NODE) return node.nodeValue.trim() || null;
    if (node.nodeType !== Node.ELEMENT_NODE) return '';
    const tag = node.tagName.toLowerCase();
    if (/h[1-6]/.test(tag)) return '#'.repeat(Number(tag[1])) + ' ' + inlineMarkdown(node).trim();
    if (tag === 'p' || tag === 'div') return inlineMarkdown(node).trim();
    if (tag === 'blockquote') return inlineMarkdown(node).split('\n').map(line => '> ' + line).join('\n');
    if (tag === 'pre') return '```\n' + node.innerText.replace(/\n$/,'') + '\n```';
    if (tag === 'ul') return Array.from(node.children)
        .filter(li => li.tagName.toLowerCase() === 'li')
        .map(li => inlineMarkdown(li).trim())
        .filter(Boolean)
        .map(text => '- ' + text)
        .join('\n');
    if (tag === 'ol') {
        let n = 1;
        return Array.from(node.children)
            .filter(li => li.tagName.toLowerCase() === 'li')
            .map(li => inlineMarkdown(li).trim())
            .filter(Boolean)
            .map(text => (n++) + '. ' + text)
            .join('\n');
    }
    if (tag === 'table') return tableToMarkdown(node);
    if (tag === 'hr') return '---';
    if (tag === 'br') return '';
    return inlineMarkdown(node).trim();
}

function inlineMarkdown(node) {
    if (node.nodeType === Node.TEXT_NODE) return node.nodeValue.replace(/\s+/g, ' ');
    if (node.nodeType !== Node.ELEMENT_NODE) return '';
    const tag = node.tagName.toLowerCase();
    const inner = Array.from(node.childNodes).map(inlineMarkdown).join('');
    if (tag === 'strong' || tag === 'b') return '**' + inner + '**';
    if (tag === 'em' || tag === 'i') return '*' + inner + '*';
    if (tag === 'code') return '`' + node.innerText.replace(/`/g, '\\`') + '`';
    if (tag === 'a') return '[' + (inner || node.href) + '](' + (node.getAttribute('href') || '') + ')';
    if (tag === 'img') return '![' + (node.getAttribute('alt') || '') + '](' + (node.dataset.organessoniumImage || node.getAttribute('src') || '') + ')';
    if (tag === 'br') return '\n';
    return inner;
}

function tableToMarkdown(table) {
    const rows = Array.from(table.querySelectorAll('tr'))
        .map(row => Array.from(row.children)
            .filter(cell => /^(td|th)$/i.test(cell.tagName))
            .map(cell => escapeMarkdownCell(inlineMarkdown(cell).replace(/\n+/g, ' ').trim())))
        .filter(cells => cells.length);
    if (!rows.length) return '';
    const widths = Math.max(...rows.map(row => row.length));
    rows.forEach(row => { while (row.length < widths) row.push(''); });
    const header = rows[0];
    const body = rows.slice(1);
    const separator = Array.from({ length:widths }, () => '---');
    return [header, separator, ...body]
        .map(row => '| ' + row.join(' | ') + ' |')
        .join('\n');
}

function escapeMarkdownCell(text) {
    return String(text || '').replace(/\|/g, '\\|');
}

function markdownToHtml(markdown) {
    const lines = String(markdown || '').replace(/\r\n/g, '\n').split('\n');
    const html = [];
    let list = null, inCode = false, code = [];
    const closeList = () => { if (list) { html.push('</' + list + '>'); list = null; } };
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i];
        if (/^```/.test(line.trim())) {
            if (inCode) { html.push('<pre><code>' + esc(code.join('\n')) + '</code></pre>'); code = []; inCode = false; }
            else { closeList(); inCode = true; }
            continue;
        }
        if (inCode) { code.push(line); continue; }
        if (!line.trim()) { closeList(); continue; }
        if (isMarkdownTableHeader(line, lines[i + 1])) {
            closeList();
            const parsed = collectMarkdownTable(lines, i);
            html.push(parsed.html);
            i = parsed.nextIndex - 1;
            continue;
        }
        const h = line.match(/^(#{1,6})\s+(.+)$/);
        if (h) { closeList(); html.push('<h' + h[1].length + '>' + markdownInline(h[2]) + '</h' + h[1].length + '>'); continue; }
        const quote = line.match(/^>\s?(.*)$/);
        if (quote) { closeList(); html.push('<blockquote>' + markdownInline(quote[1]) + '</blockquote>'); continue; }
        const ul = line.match(/^\s*[-*+]\s+(.+)$/);
        if (ul) { if (list !== 'ul') { closeList(); list = 'ul'; html.push('<ul>'); } html.push('<li>' + markdownInline(ul[1]) + '</li>'); continue; }
        const ol = line.match(/^\s*\d+\.\s+(.+)$/);
        if (ol) { if (list !== 'ol') { closeList(); list = 'ol'; html.push('<ol>'); } html.push('<li>' + markdownInline(ol[1]) + '</li>'); continue; }
        if (/^\s*(-{3,}|\*{3,}|_{3,})\s*$/.test(line)) { closeList(); html.push('<hr>'); continue; }
        closeList();
        html.push('<p>' + markdownInline(line) + '</p>');
    }
    closeList();
    if (inCode) html.push('<pre><code>' + esc(code.join('\n')) + '</code></pre>');
    return html.join('');
}

function isMarkdownTableHeader(line, separator) {
    return splitMarkdownTableRow(line).length > 1
        && splitMarkdownTableRow(separator).length > 1
        && splitMarkdownTableRow(separator).every(cell => /^:?-{3,}:?$/.test(cell.trim()));
}

function collectMarkdownTable(lines, start) {
    const header = splitMarkdownTableRow(lines[start]);
    const rows = [];
    let index = start + 2;
    while (index < lines.length && splitMarkdownTableRow(lines[index]).length > 1) {
        rows.push(splitMarkdownTableRow(lines[index]));
        index++;
    }
    const columnCount = Math.max(header.length, ...rows.map(row => row.length), 0);
    const normalize = row => {
        const next = row.slice(0, columnCount);
        while (next.length < columnCount) next.push('');
        return next;
    };
    const headHtml = normalize(header).map(cell => '<th>' + markdownInline(cell.trim()) + '</th>').join('');
    const bodyHtml = rows.map(row => '<tr>' + normalize(row).map(cell => '<td>' + markdownInline(cell.trim()) + '</td>').join('') + '</tr>').join('');
    return {
        html:'<table><thead><tr>' + headHtml + '</tr></thead><tbody>' + bodyHtml + '</tbody></table>',
        nextIndex:index
    };
}

function splitMarkdownTableRow(line) {
    const text = String(line || '').trim();
    if (!text.includes('|')) return [];
    const cells = [];
    let cell = '', escaped = false;
    for (const ch of text.replace(/^\|/, '').replace(/\|$/, '')) {
        if (escaped) {
            cell += ch;
            escaped = false;
        } else if (ch === '\\') {
            escaped = true;
        } else if (ch === '|') {
            cells.push(cell);
            cell = '';
        } else {
            cell += ch;
        }
    }
    cells.push(cell);
    return cells;
}

function markdownInline(text) {
    let s = esc(text);
    s = s.replace(/!\[([^\]]*)\]\(([^)\s]+)\)/g, (_, alt, src) => '<img src="' + esc(src) + '" alt="' + esc(alt) + '">');
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_, label, href) => '<a href="' + esc(href) + '">' + label + '</a>');
    s = s.replace(/`([^`]+)`/g, '<code>$1</code>');
    s = s.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>').replace(/__([^_]+)__/g, '<strong>$1</strong>');
    s = s.replace(/\*([^*]+)\*/g, '<em>$1</em>').replace(/_([^_]+)_/g, '<em>$1</em>');
    return s;
}

function setupMomentumScroll(el, options = {}) {
    el._stopMomentumScroll = () => {};
}

function marqueeSurfaceBounds() {
    const main = $('main-area');
    const mainRect = main.getBoundingClientRect();
    const headerRect = main.querySelector('.content-header')?.getBoundingClientRect();
    return {
        mainRect,
        left:mainRect.left,
        right:mainRect.right,
        top:Math.max(mainRect.top, headerRect?.bottom || mainRect.top),
        bottom:mainRect.bottom
    };
}

function setupMarqueeSelection() {
    const main = $('main-area');
    main.addEventListener('mousedown', e => {
        if (e.button !== 0) return;
        if (previewOverlay && !previewOverlay.classList.contains('hidden')) return;
        if (e.target.closest('.file-card,.file-row,button,input,select,.modal-overlay,.settings-panel,.preview-overlay,.ctx-menu')) return;
        const surface = marqueeSurfaceBounds();
        if (e.clientX < surface.left || e.clientX > surface.right || e.clientY < surface.top || e.clientY > surface.bottom) return;
        hideCtx();
        if (!e.ctrlKey && !e.metaKey && !e.shiftKey) clearSelection();
        const mainRect = surface.mainRect;
        marqueeState = {
            startClientX:e.clientX,
            startClientY:e.clientY,
            lastClientX:e.clientX,
            lastClientY:e.clientY,
            startX:e.clientX - mainRect.left + main.scrollLeft,
            startY:e.clientY - mainRect.top + main.scrollTop,
            lastX:e.clientX - mainRect.left + main.scrollLeft,
            lastY:e.clientY - mainRect.top + main.scrollTop,
            additive:e.ctrlKey || e.metaKey || e.shiftKey,
            base:new Set(selectedKeys),
            active:false,
            autoScroll:0,
            autoScrollFrame:0
        };
        document.addEventListener('mousemove', onMarqueeMove);
        document.addEventListener('mouseup', onMarqueeUp);
        e.preventDefault();
    });
}

function onMarqueeMove(e) {
    if (!marqueeState) return;
    updateMarqueePointer(e.clientX, e.clientY);
    const moved = Math.abs(e.clientX - marqueeState.startClientX) + Math.abs(e.clientY - marqueeState.startClientY);
    if (!marqueeState.active && moved < 5) return;
    if (!marqueeState.active) {
        marqueeState.active = true;
        marqueeEl = document.createElement('div');
        marqueeEl.className = 'selection-rect';
        document.body.appendChild(marqueeEl);
    }
    updateMarqueeAutoScroll();
    updateMarqueeRect();
    updateMarqueeSelection();
    e.preventDefault();
}

function onMarqueeUp() {
    if (marqueeState && marqueeState.autoScrollFrame) cancelAnimationFrame(marqueeState.autoScrollFrame);
    if (marqueeEl) marqueeEl.remove();
    marqueeEl = null;
    marqueeState = null;
    document.removeEventListener('mousemove', onMarqueeMove);
    document.removeEventListener('mouseup', onMarqueeUp);
}

function updateMarqueePointer(clientX, clientY) {
    const main = $('main-area');
    const surface = marqueeSurfaceBounds();
    const boundedX = Math.max(surface.left, Math.min(surface.right, clientX));
    const boundedY = Math.max(surface.top, Math.min(surface.bottom, clientY));
    marqueeState.lastClientX = boundedX;
    marqueeState.lastClientY = boundedY;
    marqueeState.lastX = boundedX - surface.mainRect.left + main.scrollLeft;
    marqueeState.lastY = boundedY - surface.mainRect.top + main.scrollTop;
}

function updateMarqueeAutoScroll() {
    const st = marqueeState;
    if (!st || !st.active) return;
    const rect = marqueeSurfaceBounds();
    const margin = 78;
    let speed = 0;
    if (st.lastClientY < rect.top + margin) speed = -Math.ceil((rect.top + margin - st.lastClientY) / 3);
    else if (st.lastClientY > rect.bottom - margin) speed = Math.ceil((st.lastClientY - (rect.bottom - margin)) / 3);
    st.autoScroll = Math.max(-32, Math.min(32, speed));
    if (st.autoScroll && !st.autoScrollFrame) st.autoScrollFrame = requestAnimationFrame(stepMarqueeAutoScroll);
}

function stepMarqueeAutoScroll() {
    const st = marqueeState;
    if (!st || !st.active || !st.autoScroll) {
        if (st) st.autoScrollFrame = 0;
        return;
    }
    const main = $('main-area');
    main.scrollTop += st.autoScroll;
    updateMarqueePointer(st.lastClientX, st.lastClientY);
    updateMarqueeRect();
    updateMarqueeSelection();
    st.autoScrollFrame = requestAnimationFrame(stepMarqueeAutoScroll);
}

function updateMarqueeRect() {
    const main = $('main-area');
    const mainRect = main.getBoundingClientRect();
    const contentX = Math.min(marqueeState.startX, marqueeState.lastX);
    const contentY = Math.min(marqueeState.startY, marqueeState.lastY);
    const x = contentX - main.scrollLeft + mainRect.left;
    const y = contentY - main.scrollTop + mainRect.top;
    const w = Math.abs(marqueeState.lastX - marqueeState.startX);
    const h = Math.abs(marqueeState.lastY - marqueeState.startY);
    Object.assign(marqueeEl.style, { left:x + 'px', top:y + 'px', width:w + 'px', height:h + 'px' });
}

function updateMarqueeSelection() {
    const main = $('main-area');
    const mainRect = main.getBoundingClientRect();
    const rect = {
        left:Math.min(marqueeState.startX, marqueeState.lastX),
        top:Math.min(marqueeState.startY, marqueeState.lastY),
        right:Math.max(marqueeState.startX, marqueeState.lastX),
        bottom:Math.max(marqueeState.startY, marqueeState.lastY)
    };
    const next = marqueeState.additive ? new Set(marqueeState.base) : new Set();
    document.querySelectorAll('[data-key]').forEach(el => {
        if (!el.dataset.key) return;
        const r = el.getBoundingClientRect();
        const itemRect = {
            left:r.left - mainRect.left + main.scrollLeft,
            top:r.top - mainRect.top + main.scrollTop,
            right:r.right - mainRect.left + main.scrollLeft,
            bottom:r.bottom - mainRect.top + main.scrollTop
        };
        const hit = !(itemRect.right < rect.left || itemRect.left > rect.right || itemRect.bottom < rect.top || itemRect.top > rect.bottom);
        if (hit) next.add(el.dataset.key);
    });
    selectedKeys = next;
    updateSelectionUI();
}

function setupRootDropTarget() {
    const roots = [document.querySelector('.topbar-brand'), pathCrumbs, $('main-area')].filter(Boolean);
    roots.forEach(el => {
        el.addEventListener('dragover', e => {
            if (!dragMoveKeys.length || currentPath === '/') return;
            if (el.id === 'main-area' && e.target.closest('.file-card,.file-row,.preview-overlay,.modal-overlay,.settings-panel')) return;
            e.preventDefault();
            el.classList.add('root-drop-target');
        });
        el.addEventListener('dragleave', () => el.classList.remove('root-drop-target'));
        el.addEventListener('drop', e => {
            if (!dragMoveKeys.length || currentPath === '/') return;
            if (el.id === 'main-area' && e.target.closest('.file-card,.file-row,.preview-overlay,.modal-overlay,.settings-panel')) return;
            e.preventDefault();
            el.classList.remove('root-drop-target');
            moveIdsToPath(dragMoveKeys, '/');
        });
    });
}

function bindInternalItemDrag(el, app) {
    if (!el) return;
    el.draggable = false;
    el.addEventListener('dragstart', e => e.preventDefault());
    el.querySelectorAll('img').forEach(img => { img.draggable = false; });
    el.addEventListener('mousedown', e => beginInternalItemDrag(e, app, el));
}

function beginInternalItemDrag(e, app, sourceEl) {
    if (e.button !== 0 || !app || !app.metadataMessageId) return;
    if (e.target.closest('button,input,select,textarea,.btn-more,.btn-row-action')) return;
    if (previewOverlay && !previewOverlay.classList.contains('hidden')) return;
    hideCtx();
    itemDragState = {
        app,
        sourceEl,
        startX:e.clientX,
        startY:e.clientY,
        x:e.clientX,
        y:e.clientY,
        active:false,
        ids:[],
        ghost:null,
        targetPath:null,
        targetEl:null,
        autoScroll:0,
        autoScrollFrame:0
    };
    document.addEventListener('mousemove', onInternalItemDragMove, true);
    document.addEventListener('mouseup', onInternalItemDragUp, true);
}

function onInternalItemDragMove(e) {
    const st = itemDragState;
    if (!st) return;
    st.x = e.clientX;
    st.y = e.clientY;
    const moved = Math.abs(st.x - st.startX) + Math.abs(st.y - st.startY);
    if (!st.active && moved < 7) return;
    if (!st.active) activateInternalItemDrag(st);
    if (!st.active) return;
    updateInternalDragGhost(st);
    updateInternalDropTarget(st);
    updateInternalAutoScroll(st);
    e.preventDefault();
    e.stopPropagation();
}

function activateInternalItemDrag(st) {
    const key = itemKey(st.app);
    if (!selectedKeys.has(key)) {
        selectedKeys.clear();
        selectedKeys.add(key);
        updateSelectionUI();
    }
    const items = getSelectedApps();
    st.ids = items.map(a => a.metadataMessageId).filter(Boolean);
    if (!st.ids.length) return;
    dragMoveKeys = [...st.ids];
    st.active = true;
    document.body.classList.add('dragging-items');
    document.querySelectorAll('[data-key]').forEach(el => {
        if (selectedKeys.has(el.dataset.key)) el.classList.add('dragging-source');
    });
    st.ghost = makeDragGhost(items);
    st.ghost.classList.add('live-drag-ghost');
    document.body.appendChild(st.ghost);
    updateInternalDragGhost(st);
}

function updateInternalDragGhost(st) {
    if (!st.ghost) return;
    st.ghost.style.left = st.x + 'px';
    st.ghost.style.top = st.y + 'px';
}

function updateInternalDropTarget(st) {
    const target = findInternalDropTarget(st.x, st.y, st.ids);
    if (st.targetEl && (!target || target.el !== st.targetEl)) {
        st.targetEl.classList.remove('drop-target', 'root-drop-target');
    }
    st.targetPath = target ? target.path : null;
    st.targetEl = target ? target.el : null;
    if (target) target.el.classList.add(target.root ? 'root-drop-target' : 'drop-target');
}

function findInternalDropTarget(x, y, ids) {
    const el = document.elementFromPoint(x, y);
    if (!el) return null;
    const folderEl = el.closest('[data-drop-path]');
    if (folderEl && !folderEl.classList.contains('dragging-source')) {
        const path = folderEl.dataset.dropPath || '/';
        if (canMoveIdsToPath(ids, path)) return { path, el:folderEl, root:false };
    }
    if (currentPath !== '/') {
        const rootEl = el.closest('.topbar-brand,.path-crumbs,#main-area');
        const overItem = el.closest('.file-card,.file-row,.preview-overlay,.modal-overlay,.settings-panel,.ctx-menu');
        if (rootEl && !(rootEl.id === 'main-area' && overItem) && canMoveIdsToPath(ids, '/')) {
            return { path:'/', el:rootEl, root:true };
        }
    }
    return null;
}

function updateInternalAutoScroll(st) {
    const main = $('main-area');
    const rect = main.getBoundingClientRect();
    const margin = 74;
    let speed = 0;
    if (st.y < rect.top + margin) speed = -Math.ceil((rect.top + margin - st.y) / 3);
    else if (st.y > rect.bottom - margin) speed = Math.ceil((st.y - (rect.bottom - margin)) / 3);
    st.autoScroll = Math.max(-30, Math.min(30, speed));
    if (st.autoScroll && !st.autoScrollFrame) st.autoScrollFrame = requestAnimationFrame(stepInternalAutoScroll);
}

function stepInternalAutoScroll() {
    const st = itemDragState;
    if (!st || !st.active || !st.autoScroll) {
        if (st) st.autoScrollFrame = 0;
        return;
    }
    $('main-area').scrollTop += st.autoScroll;
    st.autoScrollFrame = requestAnimationFrame(stepInternalAutoScroll);
}

function onInternalItemDragUp(e) {
    const st = itemDragState;
    if (!st) return;
    const wasActive = st.active;
    const ids = [...(st.ids || [])];
    const targetPath = st.targetPath;
    cleanupInternalItemDrag();
    if (!wasActive) return;
    dragSuppressClickUntil = Date.now() + 260;
    e.preventDefault();
    e.stopPropagation();
    if (targetPath) moveIdsToPath(ids, targetPath);
}

function cleanupInternalItemDrag() {
    const st = itemDragState;
    document.removeEventListener('mousemove', onInternalItemDragMove, true);
    document.removeEventListener('mouseup', onInternalItemDragUp, true);
    if (st) {
        if (st.autoScrollFrame) cancelAnimationFrame(st.autoScrollFrame);
        if (st.ghost) st.ghost.remove();
        if (st.targetEl) st.targetEl.classList.remove('drop-target', 'root-drop-target');
    }
    itemDragState = null;
    clearDragState();
}

function canMoveIdsToPath(ids, path) {
    if (!ids || !ids.length || !path) return false;
    return ids.some(id => {
        const app = allApps.find(a => a.metadataMessageId === id);
        if (!app) return false;
        const current = app.path || '/';
        if (current === path) return false;
        if (app.type === 'folder') {
            const source = (current === '/' ? '/' : current + '/') + app.name;
            if (path === source || path.startsWith(source + '/')) return false;
        }
        return true;
    });
}

function shouldSuppressItemClick(e) {
    if (Date.now() >= dragSuppressClickUntil) return false;
    e.preventDefault();
    e.stopPropagation();
    return true;
}

const PREVIEWABLE = new Set(['jpg','jpeg','png','gif','bmp','webp','tiff','tif','svg',
    'mp4','mkv','avi','mov','webm','mp3','flac','wav','ogg','aac','m4a','pdf',
    'odt','ods','odp','rtf',
    'txt','md','json','xml','yaml','yml','log','csv','js','ts','java','py',
    'html','css','sh','bat','ini','cfg','c','cpp','cs','go','rs','rb']);
function canPreview(name) { return !!name; }

function loadPreviewApp(app, showLoading) {
    const previous = previewAppRef;
    if (previous && previous.threadId !== app.threadId) window.bridge.closePreview(previous.threadId);
    previewAppRef = app;
    $('preview-filename').textContent = app.name;
    $('preview-filesize').textContent = fmtSize(app.sizeBytes);
    updatePreviewNav();
    if (showLoading) window._onPreviewLoading();
    window.bridge.requestPreview(app.threadId, app.name);
}

function openPreview(app) {
    if (!settings.previewEnabled) return;
    if (isMarkdownFile(app.name)) { openMarkdownEditor(app); return; }
    previewFiles  = sorted(activeLibraryView === 'favorites'
        ? specialItems.filter(item => item.type !== 'folder')
        : filesAt(allApps, currentPath)).filter(a => canPreview(a.name));
    previewIndex  = previewFiles.findIndex(a => a.threadId === app.threadId);
    if (previewIndex < 0) previewIndex = 0;
    previewOverlay.classList.remove('hidden');
    scheduleNativeTitleBarDragRegion(0);
    loadPreviewApp(app, false);
}

function isMarkdownFile(name) {
    const ext = String(name || '').split('.').pop().toLowerCase();
    return ext === 'md' || ext === 'markdown';
}

function closePreview() {
    disposeActiveImage();
    resetPdfPreviewState();
    pendingVideoQualitySwitch = null;
    previewOverlay.classList.add('hidden');
    scheduleNativeTitleBarDragRegion(0);
    $('preview-body').innerHTML = '<div class="preview-spinner"></div>';
    if (previewAppRef) { window.bridge.closePreview(previewAppRef.threadId); previewAppRef = null; }
}

function previewPrev() {
    if (previewIndex <= 0) return; previewIndex--;
    loadPreviewApp(previewFiles[previewIndex], true);
}
function previewNext() {
    if (previewIndex >= previewFiles.length-1) return; previewIndex++;
    loadPreviewApp(previewFiles[previewIndex], true);
}
function updatePreviewNav() {
    $('preview-prev').disabled = previewIndex <= 0;
    $('preview-next').disabled = previewIndex >= previewFiles.length - 1;
    $('preview-position').textContent = previewFiles.length > 1 ? (previewIndex+1)+'/'+previewFiles.length : '';
}

// ─── Modal de mover ───────────────────────────────────────────────────────────
function openMoveModal(app) {
    moveTarget = Array.isArray(app) ? app : [app];
    moveSelectedPath = '/';
    buildMoveTree();
    moveOverlay.classList.remove('hidden');
}
function closeMoveModal() { moveOverlay.classList.add('hidden'); moveTarget = null; }
function confirmMove() {
    if (!moveTarget) return;
    const ids = moveTarget.map(a => a && a.metadataMessageId).filter(Boolean);
    moveIdsToPath(ids, moveSelectedPath);
    closeMoveModal();
}

function buildMoveTree() {
    moveTree.innerHTML = '';
    // Raiz
    const root = makeMoveItem('/', 'root', 0);
    moveTree.appendChild(root);
    // Pastas explícitas e derivadas
    const folders = getAllFolderPaths();
    folders.forEach(fp => {
        const depth = fp.split('/').filter(Boolean).length;
        const label = fp.split('/').filter(Boolean).pop() || fp;
        moveTree.appendChild(makeMoveItem(fp, label, depth));
    });
}

function getAllFolderPaths() {
    const s = new Set();
    // Pastas explícitas
    allApps.filter(a => a.type==='folder').forEach(a => {
        const fp = (a.path==='/' ? '' : a.path) + '/' + a.name;
        s.add(fp.startsWith('//') ? fp.slice(1) : fp);
    });
    // Derivadas dos arquivos
    allApps.filter(a => a.type!=='folder' && a.path && a.path !== '/').forEach(a => {
        const segs = a.path.split('/').filter(Boolean);
        let built = '';
        segs.forEach(seg => { built += '/' + seg; s.add(built); });
    });
    return [...s].sort();
}

function makeMoveItem(path, label, depth) {
    const el = document.createElement('div');
    el.className = 'move-tree-item' + (path === moveSelectedPath ? ' selected' : '');
    el.style.paddingLeft = (14 + depth * 18) + 'px';
    el.innerHTML = icon('folder', 17, DEFAULT_FOLDER_COLOR) + '<span>' + esc(label) + '</span>';
    el.addEventListener('click', () => {
        moveSelectedPath = path;
        moveTree.querySelectorAll('.move-tree-item').forEach(i => i.classList.remove('selected'));
        el.classList.add('selected');
    });
    return el;
}

// ─── Navegação ────────────────────────────────────────────────────────────────
function navigate(path, addHistory = true) {
    if (addHistory && path !== currentPath) { historyBack.push(currentPath); historyForward = []; }
    currentPath = path; searchQuery = ''; $('search-input').value = '';
    folderLoadingPath = path;
    const folderIds = path === '/'
        ? []
        : allApps
            .filter(app => isFolderApp(app) && folderPathForApp(app) === path && app.folderId)
            .map(app => app.folderId);
    window.bridge.setCurrentPath(path, path === '/' ? 'root' : JSON.stringify(folderIds));
    updateNavButtons(); render(); requestVisibleThumbnails();
}

function refreshCurrentFolder() {
    setStatus(t('loading'), true);
    folderFileCounts.clear();
    requestedFolderStatistics.clear();
    if (window.bridge && typeof window.bridge.refreshCurrentFolder === 'function') {
        window.bridge.refreshCurrentFolder();
        return;
    }
    window.bridge.loadApps();
}
function goBack()    { if (!historyBack.length) return;    historyForward.push(currentPath); navigate(historyBack.pop(), false); }
function goForward() { if (!historyForward.length) return; historyBack.push(currentPath);    navigate(historyForward.pop(), false); }
function updateNavButtons() { btnBack.disabled = !historyBack.length; btnForward.disabled = !historyForward.length; }

// ─── Modais simples ───────────────────────────────────────────────────────────
function openFolderModal()  { folderInput.value=''; folderOverlay.classList.remove('hidden'); setTimeout(()=>folderInput.focus(),50); }
function closeFolderModal() { folderOverlay.classList.add('hidden'); }
function confirmFolder()    { const n=folderInput.value.trim(); if(!n) return; setStatus(t('creatingFolder'), true); window.bridge.createFolder(n); closeFolderModal(); }

function toggleSidebarNewMenu() {
    const menu = $('sidebar-new-menu');
    if (!menu.classList.contains('hidden')) {
        closeSidebarNewMenu();
        return;
    }
    const button = $('btn-sidebar-new');
    const rect = button.getBoundingClientRect();
    const menuWidth = 224;
    const left = Math.max(8, Math.min(rect.left, window.innerWidth - menuWidth - 8));
    typeFilterMenu.classList.add('hidden');
    dateFilterMenu.classList.add('hidden');
    sortMenu.classList.add('hidden');
    hideCtx();
    menu.classList.remove('hidden');
    menu.style.left = left + 'px';
    menu.style.top = Math.min(rect.bottom + 8, window.innerHeight - menu.offsetHeight - 8) + 'px';
    button.setAttribute('aria-expanded', 'true');
    requestAnimationFrame(() => $('sidebar-new-folder').focus());
}

function closeSidebarNewMenu() {
    const menu = $('sidebar-new-menu');
    if (!menu) return;
    menu.classList.add('hidden');
    $('btn-sidebar-new')?.setAttribute('aria-expanded', 'false');
}

function createMarkdownDocument() {
    closePreview();
    setupWorkspaceTabs();
    showWorkspaceTabs();

    const baseName = t('newMarkdownDefault');
    const dot = baseName.lastIndexOf('.');
    const stem = dot > 0 ? baseName.slice(0, dot) : baseName;
    const extension = dot > 0 ? baseName.slice(dot) : '.md';
    const names = new Set(filesAt(allApps, currentPath).map(app => String(app.name || '').toLowerCase()));
    let name = baseName;
    for (let suffix = 2; names.has(name.toLowerCase()); suffix++) name = stem + ' (' + suffix + ')' + extension;

    const id = 'md-new-' + Date.now().toString(36);
    const app = { type:'file', name, path:currentPath, threadId:'', metadataMessageId:'' };
    const tab = document.createElement('button');
    tab.className = 'workspace-tab';
    tab.dataset.tabId = id;
    tab.innerHTML = `${icon('edit_note',16)}<span>${esc(truncate(name, 24))}</span>
        <span class="tab-close" data-close-tab="${esc(id)}">${icon('close',14)}</span>`;
    workspaceTabs.appendChild(tab);

    const panel = document.createElement('section');
    panel.className = 'markdown-editor-tab';
    panel.dataset.tabId = id;
    editorArea.appendChild(panel);
    const entry = {
        id, app, tab, panel,
        editor:null, rawEditor:null, saveState:null, rawToggle:null, zoomLabel:null,
        toolbarWrapCleanup:null,
        rawMode:false, zoom:1,
        lastSavedMarkdown:null, pendingMarkdown:'', saveTimer:0, saving:false, queued:false
    };
    markdownTabs.set(id, entry);
    panel.appendChild(createMarkdownEditor('', entry, name));
    activateWorkspaceTab(id);
    saveMarkdownNow(entry);
    requestAnimationFrame(() => entry.editor?.focus());
}

function normalizeHexColor(value) {
    const raw = String(value || '').trim().replace(/^#/, '');
    return /^[0-9a-f]{6}$/i.test(raw) ? '#' + raw.toUpperCase() : '';
}

function hexToRgb(value) {
    const hex = normalizeHexColor(value) || DEFAULT_FOLDER_COLOR;
    return { r:parseInt(hex.slice(1, 3), 16), g:parseInt(hex.slice(3, 5), 16), b:parseInt(hex.slice(5, 7), 16) };
}

function rgbToHex(r, g, b) {
    const channel = value => Math.max(0, Math.min(255, Number(value) || 0)).toString(16).padStart(2, '0');
    return ('#' + channel(r) + channel(g) + channel(b)).toUpperCase();
}

function syncCustomFolderColor(value, source) {
    let hex = '';
    if (source === 'rgb') {
        hex = rgbToHex($('folder-color-r').value, $('folder-color-g').value, $('folder-color-b').value);
    } else {
        hex = normalizeHexColor(value);
        $('folder-color-hex').classList.toggle('invalid', source === 'hex' && !hex);
        if (!hex) return;
    }
    const rgb = hexToRgb(hex);
    if (source !== 'hex') $('folder-color-hex').value = hex;
    if (source !== 'picker') $('folder-color-picker').value = hex;
    if (source !== 'rgb') {
        $('folder-color-r').value = rgb.r;
        $('folder-color-g').value = rgb.g;
        $('folder-color-b').value = rgb.b;
    }
}

function openCustomFolderColor(app) {
    if (!app || !app.metadataMessageId) return;
    customFolderColorTarget = app;
    const hex = normalizeHexColor(app.folderColor) || DEFAULT_FOLDER_COLOR;
    syncCustomFolderColor(hex, 'hex');
    $('folder-color-hex').value = hex;
    $('folder-color-hex').classList.remove('invalid');
    $('folder-color-overlay').classList.remove('hidden');
    setTimeout(() => $('folder-color-picker').focus(), 50);
}

function closeCustomFolderColor() {
    $('folder-color-overlay').classList.add('hidden');
    customFolderColorTarget = null;
}

function applyCustomFolderColor() {
    if (!customFolderColorTarget || !customFolderColorTarget.metadataMessageId) return;
    const hex = normalizeHexColor($('folder-color-hex').value);
    if (!hex) {
        $('folder-color-hex').classList.add('invalid');
        $('folder-color-hex').focus();
        return;
    }
    window.bridge.updateFolderColor(customFolderColorTarget.metadataMessageId, hex);
    closeCustomFolderColor();
}
function openRenameModal(app) {
    if (!app || !app.metadataMessageId) return;
    renameTarget = app;
    const input = $('rename-name-input');
    input.value = app.name || '';
    $('rename-overlay').classList.remove('hidden');
    setTimeout(() => {
        input.focus();
        const dot = input.value.lastIndexOf('.');
        if (dot > 0 && !isFolderApp(app)) input.setSelectionRange(0, dot);
        else input.select();
    }, 50);
}
function closeRenameModal() {
    $('rename-overlay').classList.add('hidden');
    renameTarget = null;
}
function confirmRename() {
    if (!renameTarget || !renameTarget.metadataMessageId) return;
    const name = $('rename-name-input').value.trim();
    if (!name || name === renameTarget.name) { closeRenameModal(); return; }
    setStatus(t('rename') + '...', true);
    window.bridge.renameItem(renameTarget.metadataMessageId, name);
    closeRenameModal();
}
function askConfirm(title, msg, onYes, confirmKey = 'delete') {
    $('confirm-title').textContent=title; $('confirm-message').textContent=msg;
    $('confirm-ok').textContent=t(confirmKey);
    confirmCb=onYes; confirmOverlay.classList.remove('hidden'); setTimeout(()=>$('confirm-ok').focus(),50);
}
function hideConfirm() { confirmOverlay.classList.add('hidden'); confirmCb=null; }

function openUploadConflict(requestId, payload = {}) {
    if (!requestId || !uploadConflictOverlay) return;
    const path = uploadConflictDisplayPath(payload.path || '/');
    const existing = Number(payload.existingCount || 0);
    const batch = Number(payload.batchConflictCount || 0);
    uploadConflictState = { requestId };
    $('upload-conflict-title').textContent = t('uploadConflictTitle');
    $('upload-conflict-message').textContent = t('uploadConflictMessage', { name: payload.fileName || '', path });
    $('upload-conflict-detail').textContent = t('uploadConflictDetails', { existing, batch });
    $('upload-conflict-overwrite').textContent = t('uploadConflictOverwrite');
    $('upload-conflict-keep').textContent = t('uploadConflictKeepBoth');
    $('upload-conflict-skip').textContent = t('uploadConflictSkip');
    $('upload-conflict-apply-text').textContent = t('uploadConflictApplyAll');
    $('upload-conflict-apply').checked = false;
    $('upload-conflict-apply-wrap').classList.toggle('hidden', !payload.multipleConflicts);
    uploadConflictOverlay.classList.remove('hidden');
    setTimeout(() => $('upload-conflict-keep').focus(), 50);
}

function resolveUploadConflict(action) {
    if (!uploadConflictState) return;
    const requestId = uploadConflictState.requestId;
    const applyAll = !!$('upload-conflict-apply').checked;
    uploadConflictState = null;
    uploadConflictOverlay.classList.add('hidden');
    if (window.bridge && window.bridge.resolveUploadConflict) {
        window.bridge.resolveUploadConflict(requestId, action, applyAll);
    }
}

function uploadConflictDisplayPath(path) {
    const normalized = String(path || '/').trim();
    return !normalized || normalized === '/' ? 'root' : normalized;
}

function openEditorLinkDialog(entry) {
    const overlay = $('editor-link-overlay');
    if (!overlay || !entry) return;
    const selection = captureEditorInsertionState(entry, true);
    editorLinkState = { entry, selection };
    $('editor-link-text').value = selection.text || '';
    $('editor-link-url').value = '';
    overlay.classList.remove('hidden');
    setTimeout(() => (selection.text ? $('editor-link-url') : $('editor-link-text')).focus(), 50);
}

function closeEditorLinkDialog() {
    const overlay = $('editor-link-overlay');
    if (overlay) overlay.classList.add('hidden');
    editorLinkState = null;
}

function confirmEditorLinkDialog() {
    if (!editorLinkState) return;
    const url = $('editor-link-url').value.trim();
    const label = $('editor-link-text').value.trim() || editorLinkState.selection.text || url;
    if (!url || !label) return;
    insertEditorLink(editorLinkState.entry, editorLinkState.selection, label, url);
    closeEditorLinkDialog();
}

function openEditorImageDialog(entry) {
    const overlay = $('editor-image-overlay');
    if (!overlay || !entry) return;
    const selection = captureEditorInsertionState(entry, false);
    editorImageState = { entry, selection, tab:'local', driveThreadId:'' };
    $('editor-image-url').value = '';
    $('editor-image-alt').value = selection.text || '';
    $('editor-image-status').textContent = '';
    setEditorImageTab('local');
    renderEditorImageDriveList();
    overlay.classList.remove('hidden');
    setTimeout(() => $('editor-image-local-choose').focus(), 50);
}

function closeEditorImageDialog() {
    const overlay = $('editor-image-overlay');
    if (overlay) overlay.classList.add('hidden');
    editorImageState = null;
}

function setEditorImageTab(tab) {
    if (!editorImageState) return;
    editorImageState.tab = tab;
    document.querySelectorAll('[data-image-tab]').forEach(btn => btn.classList.toggle('active', btn.dataset.imageTab === tab));
    document.querySelectorAll('[data-image-pane]').forEach(pane => pane.classList.toggle('hidden', pane.dataset.imagePane !== tab));
    $('editor-image-confirm').classList.toggle('hidden', tab === 'local');
    if (tab === 'url') setTimeout(() => $('editor-image-url').focus(), 30);
}

function renderEditorImageDriveList() {
    const list = $('editor-image-drive-list');
    if (!list || !editorImageState) return;
    const images = allApps
        .filter(app => app && app.type !== 'folder' && fileType(app.name) === 'image' && app.threadId && (!app.sizeBytes || app.sizeBytes <= 10 * 1024 * 1024))
        .sort((a,b) => String(a.name || '').localeCompare(String(b.name || '')));
    list.innerHTML = '';
    if (!images.length) {
        list.innerHTML = '<div class="editor-image-empty">' + esc(t('editorNoImages')) + '</div>';
        return;
    }
    images.forEach(app => {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'editor-image-drive-item';
        btn.dataset.threadId = app.threadId;
        btn.innerHTML = icon('image', 18, TYPE_COLOR.image) + '<span><strong>' + esc(app.name || 'imagem') + '</strong><small>' + esc(app.path || '/') + ' · ' + esc(fmtSize(app.sizeBytes || 0)) + '</small></span>';
        btn.addEventListener('click', () => {
            editorImageState.driveThreadId = app.threadId;
            $('editor-image-alt').value = $('editor-image-alt').value || (app.name || '').replace(/\.[^.]+$/, '');
            list.querySelectorAll('.editor-image-drive-item').forEach(item => item.classList.remove('selected'));
            btn.classList.add('selected');
        });
        list.appendChild(btn);
    });
}

function confirmEditorImageDialog() {
    if (!editorImageState) return;
    const tab = editorImageState.tab;
    if (tab === 'local') {
        chooseLocalMarkdownImage();
    } else if (tab === 'drive') {
        if (!editorImageState.driveThreadId) return;
        $('editor-image-status').textContent = t('loading');
        window.bridge.resolveMarkdownImage(editorImageState.entry.id, editorImageState.driveThreadId);
    } else {
        const url = $('editor-image-url').value.trim();
        if (!url) return;
        insertEditorImage(editorImageState.entry, editorImageState.selection, url, $('editor-image-alt').value.trim());
        closeEditorImageDialog();
    }
}

function chooseLocalMarkdownImage() {
    if (!editorImageState) return;
    $('editor-image-status').textContent = t('editorUploadingImage');
    window.bridge.uploadMarkdownImage(editorImageState.entry.id, editorImageState.entry.app.threadId || '');
}

function captureEditorInsertionState(entry, expandWord) {
    if (entry.rawMode && entry.rawEditor) {
        const textarea = entry.rawEditor;
        let start = textarea.selectionStart, end = textarea.selectionEnd;
        if (expandWord && start === end) {
            const expanded = expandTextareaWord(textarea.value, start);
            start = expanded.start; end = expanded.end;
            textarea.setSelectionRange(start, end);
        }
        return { raw:true, start, end, text:textarea.value.slice(start, end) };
    }

    const selection = window.getSelection();
    let range = null;
    if (selection && selection.rangeCount && entry.editor && entry.editor.contains(selection.anchorNode)) {
        range = selection.getRangeAt(0).cloneRange();
        if (expandWord && range.collapsed) range = expandContentEditableWord(range, entry.editor);
    }
    return { raw:false, range, text:range ? range.toString() : '' };
}

function expandTextareaWord(value, offset) {
    let start = offset, end = offset;
    while (start > 0 && /[^\s()[\]{}<>]/.test(value[start - 1])) start--;
    while (end < value.length && /[^\s()[\]{}<>]/.test(value[end])) end++;
    return { start, end };
}

function expandContentEditableWord(range, root) {
    const node = range.startContainer;
    if (!node || node.nodeType !== Node.TEXT_NODE || !root.contains(node)) return range;
    const value = node.nodeValue || '';
    const expanded = expandTextareaWord(value, range.startOffset);
    const next = range.cloneRange();
    next.setStart(node, expanded.start);
    next.setEnd(node, expanded.end);
    return next;
}

function restoreEditorInsertionState(entry, state) {
    if (!entry || !state) return;
    if (state.raw && entry.rawEditor) {
        entry.rawEditor.focus();
        entry.rawEditor.setSelectionRange(state.start, state.end);
    } else if (entry.editor && state.range) {
        entry.editor.focus();
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(state.range);
    } else if (entry.editor) {
        entry.editor.focus();
    }
}

function insertEditorLink(entry, state, label, url) {
    if (!entry) return;
    if (state.raw && entry.rawEditor) {
        const textarea = entry.rawEditor;
        const text = '[' + escapeMarkdownLabel(label) + '](' + escapeMarkdownUrl(url) + ')';
        replaceTextareaRange(textarea, state.start, state.end, text);
        textarea.dispatchEvent(new Event('input', { bubbles:true }));
        return;
    }
    restoreEditorInsertionState(entry, state);
    document.execCommand('insertHTML', false, '<a href="' + esc(url) + '">' + esc(label) + '</a>');
    normalizeEditorDom(entry.editor);
    refreshEditorToolbarState(entry);
    entry.editor.dispatchEvent(new Event('input', { bubbles:true }));
}

function insertEditorImage(entry, state, url, alt) {
    if (!entry) return;
    const safeAlt = alt || '';
    if (state.raw && entry.rawEditor) {
        const text = '![' + escapeMarkdownLabel(safeAlt) + '](' + escapeMarkdownUrl(url) + ')';
        replaceTextareaRange(entry.rawEditor, state.start, state.end, text);
        entry.rawEditor.dispatchEvent(new Event('input', { bubbles:true }));
        return;
    }
    restoreEditorInsertionState(entry, state);
    document.execCommand('insertHTML', false, '<img src="' + esc(url) + '" alt="' + esc(safeAlt) + '">');
    normalizeEditorDom(entry.editor);
    prepareEditorMedia(entry);
    refreshEditorToolbarState(entry);
    entry.editor.dispatchEvent(new Event('input', { bubbles:true }));
}

function insertMarkdownTable(entry, state) {
    if (!entry) return;
    if (state && state.raw && entry.rawEditor) {
        replaceTextareaRange(entry.rawEditor, state.start, state.end, defaultMarkdownTable());
        entry.rawEditor.dispatchEvent(new Event('input', { bubbles:true }));
        return;
    }
    const label = resolveLanguage() === 'en' ? 'Column' : 'Coluna';
    const html = '<table><thead><tr><th>' + label + ' 1</th><th>' + label + ' 2</th><th>' + label + ' 3</th></tr></thead><tbody><tr><td><br></td><td><br></td><td><br></td></tr><tr><td><br></td><td><br></td><td><br></td></tr></tbody></table><p><br></p>';
    restoreEditorInsertionState(entry, state);
    document.execCommand('insertHTML', false, html);
    normalizeEditorDom(entry.editor);
    prepareEditorMedia(entry);
    refreshEditorToolbarState(entry);
    entry.editor.dispatchEvent(new Event('input', { bubbles:true }));
}

function defaultMarkdownTable() {
    const label = resolveLanguage() === 'en' ? 'Column' : 'Coluna';
    return '| ' + label + ' 1 | ' + label + ' 2 | ' + label + ' 3 |\n| --- | --- | --- |\n|  |  |  |\n|  |  |  |\n';
}

function replaceTextareaRange(textarea, start, end, text) {
    textarea.value = textarea.value.slice(0, start) + text + textarea.value.slice(end);
    textarea.setSelectionRange(start + text.length, start + text.length);
}

function escapeMarkdownLabel(text) {
    return String(text || '').replace(/]/g, '\\]');
}

function escapeMarkdownUrl(url) {
    return String(url || '').replace(/\)/g, '%29').replace(/\s/g, '%20');
}

// ─── Settings ─────────────────────────────────────────────────────────────────
function loadSettings() {
    try {
        if (window.bridge && window.bridge.getSystemPreferences) {
            systemPreferences = { ...systemPreferences, ...JSON.parse(window.bridge.getSystemPreferences()) };
        }
    } catch(e){}
    try { const s=JSON.parse(window.bridge.getSettings()); settings={...settings,...s}; } catch(e){}
    settings.language = ['system', 'pt-BR', 'en'].includes(settings.language) ? settings.language : 'system';
    settings.theme = ['system', 'dark', 'light'].includes(settings.theme) ? settings.theme : 'system';
    settings.videoPreviewQuality = settings.videoPreviewQuality === 'original' ? 'original' : 'low';
    settings.cardSize = CARD_SIZES[settings.cardSize] ? settings.cardSize : 'medium';
    settings.uploadThreads = Math.max(1, Math.min(16, Number(settings.uploadThreads) || 4));
    settings.previewThreads = Math.max(1, Math.min(8, Number(settings.previewThreads) || 4));
    settings.maxParallelUploads = Math.max(1, Math.min(8, Number(settings.maxParallelUploads) || 3));
    settings.encryptionMode = settings.encryptionMode === 'custom' ? 'custom' : 'standard';
    settings.customKeyAvailable = settings.customKeyAvailable === true;
    applyTheme(settings.theme); applyCardSize(settings.cardSize);
    $('setting-threads').value=settings.downloadThreads; $('thr-val').textContent=settings.downloadThreads;
    $('setting-upload-files').value=settings.maxParallelUploads; $('upload-files-val').textContent=settings.maxParallelUploads;
    $('setting-upload-threads').value=settings.uploadThreads; $('upload-thr-val').textContent=settings.uploadThreads;
    $('setting-preview-threads').value=settings.previewThreads; $('preview-thr-val').textContent=settings.previewThreads;
    $('setting-dlpath').value=settings.downloadPath||''; $('setting-tmppath').value=settings.tempPath||''; $('setting-datapath').value=settings.dataPath||'';
    $('setting-preview').checked=settings.previewEnabled!==false;
    $('setting-animations').checked=settings.animationsEnabled!==false;
    $('setting-hardware').checked=settings.hardwareAcceleration!==false;
    loadProxySettings();
    syncEncryptionSettings();
    document.querySelectorAll('.theme-btn').forEach(b=>b.classList.toggle('active',b.dataset.themeValue===settings.theme));
    document.querySelectorAll('.card-size-btn[data-size]').forEach(b=>b.classList.toggle('active',b.dataset.size===(settings.cardSize||'medium')));
    document.querySelectorAll('.video-quality-btn').forEach(b=>b.classList.toggle('active',b.dataset.videoQuality===(settings.videoPreviewQuality||'low')));
    syncMotionPreference();
    applyI18n();
}
function syncEncryptionSettings() {
    const customMode = settings.encryptionMode === 'custom';
    const customAvailable = settings.customKeyAvailable === true;
    const customPanel = $('setting-custom-key');
    if (customPanel) customPanel.classList.toggle('hidden', !customMode);
    $('btn-encryption-standard')?.classList.toggle('active', !customMode);
    $('btn-encryption-custom')?.classList.toggle('active', customMode);
    const exportButton = $('btn-export-custom-key');
    if (exportButton) exportButton.disabled = !customAvailable;
    const status = $('setting-custom-key-status');
    if (status) status.textContent = customAvailable ? t('encryptionReady') : t('encryptionNotReady');
}
function loadProxySettings() {
    const proxy = settings.proxy || {};
    $('setting-proxy-mode').value = ['automatic', 'disabled', 'manual'].includes(proxy.proxyMode) ? proxy.proxyMode : 'automatic';
    $('setting-proxy-host').value = typeof proxy.proxyHost === 'string' ? proxy.proxyHost : '';
    $('setting-proxy-port').value = proxy.proxyPort ? String(proxy.proxyPort) : '';
    $('setting-proxy-protocol').value = ['auto', 'http', 'https', 'socks5', 'socks4a', 'socks4'].includes(proxy.proxyProtocol) ? proxy.proxyProtocol : 'auto';
    $('setting-proxy-auth').checked = proxy.proxyAuthenticationEnabled === true;
    $('setting-proxy-user').value = typeof proxy.proxyUsername === 'string' ? proxy.proxyUsername : '';
    $('setting-proxy-pass').value = typeof proxy.proxyPassword === 'string' ? proxy.proxyPassword : '';
    applyProxySettingsVisibility();
}

function applyProxySettingsVisibility() {
    const manual = $('setting-proxy-mode').value === 'manual';
    $('setting-proxy-manual').classList.toggle('hidden', !manual);
    $('setting-proxy-auth-fields').classList.toggle('hidden', !$('setting-proxy-auth').checked);
}

function bindPathSetting(inputId, settingKey) {
    const input = $(inputId);
    if (!input) return;
    const commit = () => {
        settings[settingKey] = input.value.trim();
        queueSettingsSave();
    };
    input.addEventListener('change', commit);
    input.addEventListener('blur', commit);
    input.addEventListener('keydown', e => {
        if (e.key === 'Enter') {
            e.preventDefault();
            commit();
            input.blur();
        }
    });
}
function queueSettingsSave() {
    if (!window.bridge || !window.bridge.saveSettings) return;
    clearTimeout(settingsSaveTimer);
    settingsSaveTimer = setTimeout(() => saveSettings(false), 220);
}
function updatePreviewCacheSize(bytes) {
    const el = $('setting-cache-size');
    if (!el) return;
    el.textContent = t('cacheSize', { size:fmtSize(Number(bytes) || 0) });
}
function saveSettings(showStatus = true) {
    window.bridge.saveSettings(
        settings.cardSize||'medium',
        settings.downloadThreads,
        settings.theme||'system',
        settings.downloadPath,
        settings.tempPath,
        settings.previewEnabled!==false,
        settings.language||'system',
        settings.animationsEnabled!==false,
        settings.hardwareAcceleration!==false,
        settings.videoPreviewQuality === 'original' ? 'original' : 'low',
        settings.dataPath || '',
        settings.uploadThreads,
        settings.maxParallelUploads,
        settings.proxy.proxyMode || 'automatic',
        settings.proxy.proxyHost || '',
        settings.proxy.proxyPort || 0,
        settings.proxy.proxyProtocol || 'auto',
        settings.proxy.proxyAuthenticationEnabled === true,
        settings.proxy.proxyUsername || '',
        settings.proxy.proxyPassword || '',
        settings.previewThreads
    );
    try { localStorage.setItem('organessonium.lang', settings.language||'system'); } catch (_) {}
    if (showStatus) setStatus(t('settingsSaved'), false);
}
function openSettings()  {
    settingsPanel.classList.remove('hidden');
    settingsOverlay.classList.remove('hidden');
    settingsPanel.setAttribute('aria-hidden', 'false');
    if (window.bridge && window.bridge.getPreviewCacheSize) window.bridge.getPreviewCacheSize();
    requestAnimationFrame(()=>{
        settingsPanel.classList.add('open');
        scheduleNativeTitleBarDragRegion(0);
    });
    setTimeout(() => {
        const target = $('language-select-button') || $('settings-close');
        if (target) target.focus();
    }, 40);
}
function closeSettings() {
    if (settingsSaveTimer) {
        clearTimeout(settingsSaveTimer);
        settingsSaveTimer = 0;
        saveSettings(false);
    }
    settingsPanel.classList.remove('open');
    settingsPanel.setAttribute('aria-hidden', 'true');
    setTimeout(()=>{
        settingsPanel.classList.add('hidden');
        settingsOverlay.classList.add('hidden');
        scheduleNativeTitleBarDragRegion(0);
    }, motionMs(260));
}
function applyTheme(t) {
    const theme = resolveTheme(t);
    document.documentElement.setAttribute('data-theme', theme);
    document.documentElement.setAttribute('data-theme-setting', t || 'system');
    try { if (window.bridge && window.bridge.applyWindowTheme) window.bridge.applyWindowTheme(t || 'system'); } catch (_) {}
}
function applyCardSize(s) {
    const size = CARD_SIZES[s] || CARD_SIZES.medium;
    document.documentElement.style.setProperty('--card-min', size);
    document.documentElement.style.setProperty('--card-preview-size', size);
    scheduleCardPreviewSync();
}

function scheduleCardPreviewSync() {
    if (!cardPreviewFrame) cardPreviewFrame = requestAnimationFrame(syncCardPreviewSize);
}

function syncCardPreviewSize() {
    cardPreviewFrame = 0;
    document.querySelectorAll('.cards-grid').forEach(grid => {
        const card = grid.querySelector('.file-card:not(.folder-card)');
        if (!card) return;
        const rect = card.getBoundingClientRect();
        const size = Math.max(104, Math.min(360, Math.round(rect.width)));
        grid.style.setProperty('--card-preview-size', size + 'px');
    });
}

// ─── Context menu ─────────────────────────────────────────────────────────────
function showCtx(e, app) {
    e.preventDefault?.();
    e.stopPropagation?.();
    ctxTarget = app;
    ctxMode = 'item';
    ctxTabTarget = null;
    if (app && app.metadataMessageId && !selectedKeys.has(itemKey(app))) {
        clearSelection();
        selectedKeys.add(itemKey(app));
        updateSelectionUI();
    }
    configureContextMenu('item', app);
    const r = e.currentTarget ? e.currentTarget.getBoundingClientRect() : null;
    const x = e.clientX || (r ? r.right - 160 : 12);
    const y = e.clientY || (r ? r.bottom + 4 : 12);
    positionContextMenu(x, y);
}

function showBackgroundCtx(e) {
    const target = e.target && e.target.closest ? e.target : null;
    if (target && !$('main-area').contains(target)) return;
    if (target && target.closest('.file-card,.file-row,.preview-overlay,.modal-overlay,.settings-panel,.ctx-menu,button,input,textarea,select')) return;
    e.preventDefault();
    ctxTarget = null;
    ctxMode = 'background';
    ctxTabTarget = null;
    configureContextMenu('background');
    positionContextMenu(e.clientX, e.clientY);
}

window._selectAllFilesFromNative = () => {
    if ($('main-area').classList.contains('hidden')) return false;
    if (!canUseGlobalShortcut({ target: document.activeElement })) return false;
    selectAllCurrentView();
    return true;
};

window._showContextAt = (x, y) => {
    if ($('main-area').classList.contains('hidden')) return false;
    const px = Number(x) || 0;
    const py = Number(y) || 0;
    const el = document.elementFromPoint(px, py);
    if (!el || !$('main-area').contains(el)) return false;

    const itemEl = el.closest('[data-key]');
    if (itemEl) {
        const app = allApps.find(a => itemKey(a) === itemEl.dataset.key);
        if (app) {
            showCtx({
                preventDefault(){},
                stopPropagation(){},
                clientX:px,
                clientY:py,
                currentTarget:itemEl
            }, app);
            return true;
        }
    }

    showBackgroundCtx({
        target:el,
        preventDefault(){},
        clientX:px,
        clientY:py
    });
    return !ctxMenu.classList.contains('hidden');
};

function showTabCtx(e, tabId) {
    e.preventDefault();
    e.stopPropagation();
    ctxTarget = null;
    ctxMode = 'tab';
    ctxTabTarget = tabId || 'files';
    configureContextMenu(tabId === 'files' ? 'files-tab' : 'tab');
    positionContextMenu(e.clientX, e.clientY);
}

function configureContextMenu(mode, app = null) {
    const isItem = mode === 'item' && !!app;
    const isFolder = isItem && isFolderApp(app);
    const hasSelection = selectedKeys.size > 0;
    const isTab = mode === 'tab';
    const isFilesTab = mode === 'files-tab';
    const hasOtherTabs = markdownTabs.size > (ctxTabTarget && ctxTabTarget !== 'files' ? 1 : 0);

    showCtxPart('ctx-open', isItem);
    showCtxPart('ctx-download', isItem && !!app.threadId);
    showCtxPart('ctx-move', isItem && !!app.metadataMessageId);
    showCtxPart('ctx-rename', isItem && !!app.metadataMessageId);
    showCtxPart('ctx-favorite', isItem && !!app.metadataMessageId);
    showCtxPart('ctx-folder-colors', isItem && isFolder && !!app.metadataMessageId);
    showCtxPart('ctx-delete', isItem && !!app.metadataMessageId);
    showCtxPart('ctx-item-sep', isItem);

    showCtxPart('ctx-new-folder', mode === 'background' || isFilesTab);
    showCtxPart('ctx-upload', mode === 'background' || isFilesTab);
    showCtxPart('ctx-upload-folder', mode === 'background' || isFilesTab);
    showCtxPart('ctx-refresh', mode === 'background' || isFilesTab);
    showCtxPart('ctx-view-sep', mode === 'background' || isFilesTab);
    showCtxPart('ctx-grid', mode === 'background' || isFilesTab);
    showCtxPart('ctx-list', mode === 'background' || isFilesTab);
    showCtxPart('ctx-clear', (mode === 'background' || isFilesTab) && hasSelection);
    showCtxPart('ctx-settings', mode === 'background' || isFilesTab);

    showCtxPart('ctx-close-tab', isTab);
    showCtxPart('ctx-close-others', isTab && hasOtherTabs);

    if (isItem) {
        $('ctx-open-text').textContent = isFolder ? t('open') : (canPreview(app.name) ? t('preview') : t('open'));
        $('ctx-dl-label').textContent = isFolder ? t('downloadFolderAction') : t('download');
        $('ctx-favorite-text').textContent = app.favorite ? t('removeFavorite') : t('addFavorite');
        $('ctx-favorite-icon').innerHTML = icon(app.favorite ? 'star' : 'star_border', 17);
    }
}

function showCtxPart(id, visible) {
    const el = $(id);
    if (el) el.classList.toggle('hidden', !visible);
}

function positionContextMenu(x, y) {
    ctxMenu.style.left = '0px';
    ctxMenu.style.top = '0px';
    ctxMenu.classList.remove('hidden');
    const w = ctxMenu.offsetWidth || 180;
    const h = ctxMenu.offsetHeight || 120;
    const px = Math.max(4, Math.min(window.innerWidth - w - 4, x));
    const py = Math.max(4, Math.min(window.innerHeight - h - 4, y));
    ctxMenu.style.left = px + 'px';
    ctxMenu.style.top = py + 'px';
}

function hideCtx() {
    ctxMenu.classList.add('hidden');
    ctxTarget = null;
    ctxTabTarget = null;
    ctxMode = 'item';
}

function isFolderApp(app) {
    return !!app && (app.type === 'folder' || (app.threadId && app.threadId.startsWith('folder_')));
}

function folderPathForApp(app) {
    const base = (app && app.path) || currentPath || '/';
    return base === '/' ? '/' + app.name : base + '/' + app.name;
}

function openContextTarget() {
    if (!ctxTarget) return;
    if (isFolderApp(ctxTarget)) {
        navigate(folderPathForApp(ctxTarget));
    } else if (settings.previewEnabled && canPreview(ctxTarget.name)) {
        openPreview(ctxTarget);
    } else {
        window.bridge.startDownload(ctxTarget.threadId, ctxTarget.name);
    }
}

function deleteContextTarget() {
    if (!ctxTarget || !ctxTarget.metadataMessageId) {
        hideCtx();
        return;
    }
    const selected = selectedKeys.has(itemKey(ctxTarget)) ? getSelectedApps() : [];
    const items = selected.length ? selected : [ctxTarget];
    hideCtx();
    askConfirm(deleteConfirmTitle(items), deleteConfirmMessage(items),
        () => {
            setStatus(t('deleting'), true);
            window.bridge.deleteItems(JSON.stringify(items.map(app => app.metadataMessageId).filter(Boolean)));
            clearSelection();
        });
}

function deleteConfirmTitle(items) {
    return items.length === 1 && isFolderApp(items[0]) ? t('deleteFolderTitle') : t('deleteFilesTitle');
}

function deleteConfirmMessage(items) {
    if (items.length === 1 && isFolderApp(items[0])) return t('deleteFolderMessage', { name:items[0].name || '' });
    return t('deleteFilesMessage', { count:items.length });
}

function closeOtherMarkdownTabs(keepId) {
    [...markdownTabs.keys()].forEach(id => {
        if (id !== keepId) closeMarkdownTab(id);
    });
}
function setView(m) { viewMode=m; $('btn-grid').classList.toggle('active',m==='grid'); $('btn-list').classList.toggle('active',m==='list'); render(); }
function setStatus(msg, loading) { statusText.textContent=msg; statusLoader.classList.toggle('hidden',!loading); }

// ─── Pastas e arquivos ────────────────────────────────────────────────────────
function childFolders(apps, path) {
    const s = new Set();
    apps.filter(a=>a.type==='folder'&&(a.path||'/')=== path).forEach(a=>s.add(a.name));
    apps.filter(a=>a.type!=='folder').forEach(a=>{
        const p=a.path||'/';
        const ok=path==='/'?(p!=='/'&&p.startsWith('/')):(p.startsWith(path+'/'));
        if(!ok) return;
        const rest=path==='/'?p.slice(1):p.slice(path.length+1);
        const seg=rest.split('/')[0]; if(seg) s.add(seg);
    });
    return [...s].sort((a,b)=>a.localeCompare(b));
}
function filesAt(apps, path) { return apps.filter(a=>a.type!=='folder'&&(a.path||'/')=== path); }
function matchesTypeFilter(app) {
    if (typeFilter === 'all' || typeFilter === 'folders') return true;
    const type = fileType(app && app.name);
    if (typeFilter === 'images') return type === 'image';
    if (typeFilter === 'videos') return type === 'video';
    if (typeFilter === 'audio') return type === 'audio';
    if (typeFilter === 'documents') return ['pdf','markdown','document','spreadsheet','presentation','code'].includes(type);
    if (typeFilter === 'archives') return type === 'archive';
    if (typeFilter === 'other') return !['image','video','audio','pdf','markdown','document','spreadsheet','presentation','code','archive'].includes(type);
    return true;
}
function matchesDateFilter(app) {
    if (dateFilter === 'all') return true;
    const timestamp = Date.parse(String(app && app.uploadedAt || ''));
    if (!Number.isFinite(timestamp)) return false;
    const date = new Date(timestamp);
    const now = new Date();
    if (dateFilter === 'today') {
        return date.getFullYear() === now.getFullYear() && date.getMonth() === now.getMonth() && date.getDate() === now.getDate();
    }
    const days = Number(dateFilter);
    return Number.isFinite(days) && timestamp >= Date.now() - days * 24 * 60 * 60 * 1000;
}
function sorted(arr) {
    const s=[...arr];
    switch(sortMode){
        case 'name-asc':  return s.sort((a,b)=>(a.name||'').localeCompare(b.name||''));
        case 'name-desc': return s.sort((a,b)=>(b.name||'').localeCompare(a.name||''));
        case 'size-asc':  return s.sort((a,b)=>(a.sizeBytes||0)-(b.sizeBytes||0));
        case 'size-desc': return s.sort((a,b)=>(b.sizeBytes||0)-(a.sizeBytes||0));
        case 'date-asc':  return s.sort((a,b)=>(a.uploadedAt||'').localeCompare(b.uploadedAt||''));
        case 'date-desc': return s.sort((a,b)=>(b.uploadedAt||'').localeCompare(a.uploadedAt||''));
    } return s;
}

// ─── Render ───────────────────────────────────────────────────────────────────
function render() {
    renderPathCrumbs();
    const favoritesView = activeLibraryView === 'favorites';
    const favoriteSource = favoritesView
        ? specialItems.filter(app => !searchQuery || (app.name || '').toLowerCase().includes(searchQuery))
        : [];
    const folders = favoritesView
        ? (typeFilter === 'all' || typeFilter === 'folders' ? sorted(favoriteSource.filter(app => app.type === 'folder')) : [])
        : (typeFilter === 'all' || typeFilter === 'folders' ? childFolders(allApps, currentPath) : []);
    let files = typeFilter === 'folders' ? [] : sorted(favoritesView ? favoriteSource.filter(app => app.type !== 'folder') : filesAt(allApps, currentPath));
    if (searchQuery && !favoritesView) files=sorted(allApps.filter(a=>a.type!=='folder'&&(a.name||'').toLowerCase().includes(searchQuery)));
    files = files.filter(app => matchesTypeFilter(app) && matchesDateFilter(app));

    filesGrid.innerHTML=''; filesList.innerHTML='';
    const total=folders.length+files.length;
    if (!total) {
        const loadingFolder = !searchQuery && (folderLoadingPath === currentPath || (favoritesView && folderLoadingPath === '/@favorites'));
        emptyState.classList.remove('hidden');
        emptyState.classList.toggle('folder-loading', loadingFolder);
        $('empty-title').textContent = loadingFolder ? t('loading') : t('emptyTitle');
        $('empty-help').textContent = loadingFolder ? '' : t('emptyHelp');
        return;
    }
    emptyState.classList.add('hidden');
    emptyState.classList.remove('folder-loading');

    if (viewMode==='grid') {
        filesGrid.classList.remove('hidden'); filesList.classList.add('hidden');
        if (folders.length) {
            const folderWrap = document.createElement('div');
            folderWrap.className = 'folders-grid';
            folders.forEach(f=>folderWrap.appendChild(favoritesView ? makeSpecialFolderCard(f) : makeDriveFolderCard(f)));
            filesGrid.appendChild(folderWrap);
        }
        if (files.length) {
            const fileWrap = document.createElement('div');
            fileWrap.className = 'cards-grid';
            files.forEach(a=>fileWrap.appendChild(makeDriveFileCard(a)));
            filesGrid.appendChild(fileWrap);
        }
    } else {
        filesGrid.classList.add('hidden'); filesList.classList.remove('hidden');
        filesList.appendChild(makeListHeader());
        folders.forEach(f=>filesList.appendChild(favoritesView ? makeSpecialFolderRow(f) : makeFolderRow(f)));
        files.forEach(a=>filesList.appendChild(makeFileRow(a)));
    }
    updateSelectionUI();
    scheduleCardPreviewSync();
    scheduleVisibleFolderStatistics();
}

function renderPathCrumbs() {
    pathCrumbs.innerHTML='';
    if (activeLibraryView === 'favorites') {
        if (currentFolderName) currentFolderName.textContent = t('sidebarFavorites');
        if (currentFolderTitle) currentFolderTitle.title = t('sidebarFavorites');
        pathCrumbs.classList.add('empty');
        return;
    }
    const isRoot = currentPath === '/';
    const devicesPath = currentPath === '/@synchronized' || currentPath.startsWith('/@synchronized/');
    const segs = currentPath.split('/').filter(Boolean).filter(segment => segment !== '@synchronized');
    const currentName = devicesPath && segs.length === 0 ? t('sidebarDevices') : isRoot ? t('filesTab') : segs[segs.length - 1];
    if (currentFolderName) currentFolderName.textContent = currentName;
    if (currentFolderTitle) {
        currentFolderTitle.title = [t('filesTab'), ...segs].join(' / ');
    }
    pathCrumbs.classList.toggle('empty', isRoot || (devicesPath && segs.length === 0));
    if (isRoot || (devicesPath && segs.length === 0)) return;
    const root = document.createElement('button');
    root.className = 'p-crumb p-crumb-root';
    root.textContent = devicesPath ? t('sidebarDevices') : 'root';
    root.title = root.textContent;
    root.addEventListener('click', () => devicesPath ? showDevicesWorkspace() : navigate('/'));
    pathCrumbs.appendChild(root);
    segs.slice(0, -1).forEach((seg,i)=>{
        const sep=document.createElement('span'); sep.className='p-crumb-sep'; sep.textContent='/';
        pathCrumbs.appendChild(sep);
        const btn=document.createElement('button');
        btn.className='p-crumb';
        btn.textContent=seg; btn.title=seg;
        btn.addEventListener('click',()=>navigate((devicesPath ? '/@synchronized/' : '/')+segs.slice(0,i+1).join('/')));
        pathCrumbs.appendChild(btn);
    });
    const sep=document.createElement('span'); sep.className='p-crumb-sep'; sep.textContent='/';
    pathCrumbs.appendChild(sep);
}

// ─── Cards ────────────────────────────────────────────────────────────────────
function makeFolderCard(name) {
    const fp=currentPath==='/'?'/'+name:currentPath+'/'+name;
    const count=allApps.filter(a=>a.type!=='folder'&&((a.path||'/')===fp||(a.path||'/').startsWith(fp+'/'))).length;
    const explicit=allApps.find(a=>a.type==='folder'&&(a.path||'/')=== currentPath&&a.name===name);
    const card=document.createElement('div');
    card.className='file-card folder-card'; card.setAttribute('role','listitem'); card.setAttribute('tabindex','0');
    card.innerHTML=`<div class="card-preview">${icon('folder',52,TYPE_COLOR.folder)}</div>
        <div class="card-footer">
            <span class="card-name" title="${esc(name)}">${esc(truncate(name,22))}</span>
            <button class="btn-more" aria-label="Opções">${icon('more_vert',16)}</button>
        </div>`;
    card.addEventListener('click', e=>{ if(!e.target.closest('.btn-more')) navigate(fp); });
    card.addEventListener('keydown', e=>{ if(e.key==='Enter') navigate(fp); });
    const folderObj=explicit||{name,threadId:'folder_implicit_'+name,type:'folder',path:currentPath};
    card.querySelector('.btn-more').addEventListener('click',e=>{ e.stopPropagation(); showCtx(e,folderObj); });
    card.addEventListener('contextmenu', e => showCtx(e, folderObj));
    return card;
}

function makeFileCard(app) {
    const type=fileType(app.name), clr=TYPE_COLOR[type]||TYPE_COLOR.generic;
    const card=document.createElement('div'); card.className='file-card'; card.setAttribute('role','listitem');
    const isThumbable=type==='image' || type==='video';

    card.innerHTML=(isThumbable
            ?`<div class="card-preview" data-thumb="${esc(app.threadId)}">${icon(fileIcon(type),48,clr)}</div>`
            :`<div class="card-preview">${icon(fileIcon(type),48,clr)}</div>`)
        +`<div class="card-footer">
            <span class="card-name" title="${esc(app.name)}">${esc(truncate(app.name,22))}</span>
            <button class="btn-more" aria-label="Opções">${icon('more_vert',16)}</button>
        </div>`;

    // Aplica thumb já em cache
    if (isThumbable && window._thumbs && window._thumbs[app.threadId]) {
        const prev=card.querySelector('.card-preview');
        applyThumbnailElement(prev, window._thumbs[app.threadId]);
    }

    if (isThumbable) bindThumbnailPreview(card, app, type, clr);
    card.addEventListener('click', e=>{
        if (e.target.closest('.btn-more')) return;
        if (settings.previewEnabled && canPreview(app.name)) openPreview(app);
    });
    card.querySelector('.btn-more').addEventListener('click',e=>{ e.stopPropagation(); showCtx(e,app); });
    card.addEventListener('contextmenu', e => showCtx(e, app));
    return card;
}

function openSpecialFolder(app) {
    setActiveLibraryView(app.path && app.path.startsWith('/@synchronized') ? 'devices' : 'files');
    navigate(folderPathForApp(app));
}

function makeSpecialFolderCard(app) {
    const color = app.folderColor || TYPE_COLOR.folder;
    const card = document.createElement('div');
    card.className = 'file-card folder-card';
    card.setAttribute('role', 'button');
    card.setAttribute('tabindex', '0');
    card.dataset.key = itemKey(app);
    const deviceIcon = app.deviceType === 'phone' ? 'smartphone' : app.deviceType === 'pc' ? 'computer' : 'folder';
    card.innerHTML = `<div class="folder-icon">${icon(deviceIcon,25,color)}</div>
        <span class="folder-name" title="${esc(app.name)}">${esc(truncate(app.name,34))}</span>
        ${app.favorite ? `<span class="item-favorite" title="${esc(t('removeFavorite'))}">${icon('star',16)}</span>` : ''}
        <button class="btn-more" aria-label="${esc(t('options'))}">${icon('more_vert',16)}</button>`;
    card.addEventListener('click', event => { if (!event.target.closest('.btn-more')) openSpecialFolder(app); });
    card.addEventListener('keydown', event => { if (event.key === 'Enter') openSpecialFolder(app); });
    card.querySelector('.btn-more').addEventListener('click', event => { event.stopPropagation(); showCtx(event, app); });
    card.addEventListener('contextmenu', event => showCtx(event, app));
    return card;
}

function makeSpecialFolderRow(app) {
    const row = document.createElement('div');
    row.className = 'file-row folder-row';
    row.dataset.key = itemKey(app);
    const deviceIcon = app.deviceType === 'phone' ? 'smartphone' : app.deviceType === 'pc' ? 'computer' : 'folder';
    row.innerHTML = `<div class="row-icon">${icon(deviceIcon,21,app.folderColor || TYPE_COLOR.folder)}</div>
        <div class="row-name">${esc(app.name)}</div><div class="row-date">${esc(app.uploadedAt || '')}</div><div class="row-size">${app.favorite ? icon('star',16) : ''}</div>
        <div class="row-actions"><button class="btn-row-action btn-more" title="${esc(t('options'))}">${icon('more_vert',17)}</button></div>`;
    row.addEventListener('dblclick', () => openSpecialFolder(app));
    row.querySelector('.btn-more').addEventListener('click', event => { event.stopPropagation(); showCtx(event, app); });
    row.addEventListener('contextmenu', event => showCtx(event, app));
    return row;
}

function makeDriveFolderCard(name) {
    const fp = currentPath==='/' ? '/' + name : currentPath + '/' + name;
    const explicitFolders = allApps.filter(a => a.type === 'folder' && (a.path||'/') === currentPath && a.name === name);
    const explicit = explicitFolders[0];
    const loadedCount = allApps.filter(a => a.type !== 'folder' && ((a.path||'/') === fp || (a.path||'/').startsWith(fp + '/'))).length;
    const knownCounts = explicitFolders
        .filter(folder => folder.folderId && folderFileCounts.has(folder.folderId))
        .map(folder => folderFileCounts.get(folder.folderId));
    const count = explicitFolders.length > 0 && knownCounts.length === explicitFolders.length
        ? knownCounts.reduce((total, value) => total + value, 0)
        : loadedCount > 0 ? loadedCount : '...';
    const folderObj = explicit || { name, threadId:'folder_implicit_' + name, type:'folder', path:currentPath, folderColor:TYPE_COLOR.folder };
    const color = folderObj.folderColor || TYPE_COLOR.folder;
    const card = document.createElement('div');
    card.className = 'file-card folder-card';
    card.setAttribute('role','button');
    card.setAttribute('tabindex','0');
    card.setAttribute('aria-label', t('openFolder', { name }));
    card.dataset.path = fp;
    card.dataset.dropPath = fp;
    card.draggable = false;
    if (folderObj.metadataMessageId) {
        card.dataset.key = itemKey(folderObj);
        card.setAttribute('aria-selected', selectedKeys.has(itemKey(folderObj)) ? 'true' : 'false');
        card.classList.toggle('selected', selectedKeys.has(itemKey(folderObj)));
    }
    card.style.setProperty('--folder-color', color);
    const folderIcon = folderObj.deviceType === 'phone' ? 'smartphone' : folderObj.deviceType === 'pc' ? 'computer' : 'folder';
    card.innerHTML = `<div class="folder-icon">${icon(folderIcon,25,color)}</div>
        <span class="folder-name" title="${esc(name)}">${esc(truncate(name,34))}</span>
        <span class="folder-count">${count}</span>
        ${folderObj.favorite ? `<span class="item-favorite" title="${esc(t('removeFavorite'))}">${icon('star',16)}</span>` : ''}
        <button class="btn-more" aria-label="${esc(t('options'))}">${icon('more_vert',16)}</button>`;
    card.addEventListener('click', e => {
        if (shouldSuppressItemClick(e)) return;
        if (e.target.closest('.btn-more')) return;
        if ((e.ctrlKey || e.metaKey || e.shiftKey) && folderObj.metadataMessageId) { toggleSelection(folderObj); return; }
        navigate(fp);
    });
    card.addEventListener('keydown', e => {
        if (e.key === ' ') { e.preventDefault(); if (folderObj.metadataMessageId) toggleSelection(folderObj); return; }
        if (e.key === 'Enter') { e.preventDefault(); navigate(fp); }
    });
    card.querySelector('.btn-more').addEventListener('click', e => { e.stopPropagation(); showCtx(e, folderObj); });
    card.addEventListener('contextmenu', e => showCtx(e, folderObj));
    bindInternalItemDrag(card, folderObj);
    card.addEventListener('dragover', e => {
        if (!dragMoveKeys.length) return;
        e.preventDefault();
        card.classList.add('drop-target');
    });
    card.addEventListener('dragleave', () => card.classList.remove('drop-target'));
    card.addEventListener('drop', e => {
        e.preventDefault();
        card.classList.remove('drop-target');
        const ids = dragMoveKeys.length ? dragMoveKeys : safeParseJson(e.dataTransfer.getData('text/plain'), []);
        moveIdsToPath(ids, fp);
    });
    return card;
}

function makeDriveFileCard(app) {
    const type = fileType(app.name), clr = TYPE_COLOR[type] || TYPE_COLOR.generic;
    const card = document.createElement('div');
    card.className = 'file-card';
    card.setAttribute('role','option');
    card.setAttribute('tabindex','0');
    card.setAttribute('aria-selected', selectedKeys.has(itemKey(app)) ? 'true' : 'false');
    card.setAttribute('aria-label', t('openPreview', { name:app.name }) + ', ' + fmtSize(app.sizeBytes));
    card.dataset.key = itemKey(app);
    card.draggable = false;
    card.classList.toggle('selected', selectedKeys.has(itemKey(app)));
    const isThumbable = type === 'image' || type === 'video';

    card.innerHTML = (isThumbable
            ? `<div class="card-preview" data-thumb="${esc(app.threadId)}">${icon(fileIcon(type),48,clr)}</div>`
            : `<div class="card-preview">${icon(fileIcon(type),48,clr)}</div>`)
        + `<div class="card-footer">
            <span class="card-name" title="${esc(app.name)}">${esc(truncate(app.name,22))}</span>
            ${app.favorite ? `<span class="item-favorite" title="${esc(t('removeFavorite'))}">${icon('star',16)}</span>` : ''}
            <button class="btn-more" aria-label="${esc(t('options'))}">${icon('more_vert',16)}</button>
        </div>`;

    if (isThumbable && window._thumbs && window._thumbs[app.threadId]) {
        const prev = card.querySelector('.card-preview');
        applyThumbnailElement(prev, window._thumbs[app.threadId]);
    }

    if (isThumbable) bindThumbnailPreview(card, app, type, clr);
    card.addEventListener('click', e => {
        if (shouldSuppressItemClick(e)) return;
        if (e.target.closest('.btn-more')) return;
        toggleSelection(app);
    });
    card.addEventListener('dblclick', e => {
        if (shouldSuppressItemClick(e)) return;
        if (e.target.closest('.btn-more')) return;
        if (settings.previewEnabled && canPreview(app.name)) openPreview(app);
    });
    card.addEventListener('keydown', e => {
        if (e.target.closest('.btn-more')) return;
        if (e.key === ' ') { e.preventDefault(); toggleSelection(app); }
        if (e.key === 'Enter' && settings.previewEnabled && canPreview(app.name)) openPreview(app);
    });
    bindInternalItemDrag(card, app);
    card.querySelector('.btn-more').addEventListener('click', e => { e.stopPropagation(); showCtx(e, app); });
    card.addEventListener('contextmenu', e => showCtx(e, app));
    return card;
}

function makeListHeader() {
    const h=document.createElement('div'); h.className='list-header';
    h.innerHTML=`<div></div><div>Nome</div><div>Data</div><div>Tamanho</div><div></div>`; return h;
}
function makeFolderRow(name) {
    const fp=currentPath==='/'?'/'+name:currentPath+'/'+name;
    const explicit=allApps.find(a=>a.type==='folder'&&(a.path||'/')=== currentPath&&a.name===name);
    const folderColor=(explicit&&explicit.folderColor)||TYPE_COLOR.folder;
    const row=document.createElement('div'); row.className='file-row folder-row';
    row.style.setProperty('--folder-color', folderColor);
    row.dataset.dropPath = fp;
    row.draggable = false;
    row.setAttribute('tabindex','0');
    row.setAttribute('role','button');
    row.setAttribute('aria-label', t('openFolder', { name }));
    const folderObj=explicit||{name,threadId:'folder_implicit_'+name,type:'folder',path:currentPath};
    if (explicit) {
        row.dataset.key = itemKey(explicit);
        row.setAttribute('aria-selected', selectedKeys.has(itemKey(explicit)) ? 'true' : 'false');
        row.classList.toggle('selected', selectedKeys.has(itemKey(explicit)));
    }
    row.innerHTML=`<div class="row-icon">${icon('folder',21,folderColor)}</div>
        <div class="row-name" title="${esc(name)}">${esc(name)}</div>
        <div class="row-date">—</div><div class="row-size">—</div>
        <div class="row-actions">
            <button class="btn-row-action" title="Baixar">${icon('download',17)}</button>
            <button class="btn-row-action" title="Mover">${icon('drive_file_move',17)}</button>
            ${explicit?`<button class="btn-row-action btn-danger-sm" title="Apagar">${icon('delete',17)}</button>`:''}
        </div>`;
    row.addEventListener('click', e=>{
        if (shouldSuppressItemClick(e)) return;
        if(e.target.closest('.btn-row-action')) return;
        if ((e.ctrlKey || e.metaKey || e.shiftKey) && explicit) { toggleSelection(explicit); return; }
        navigate(fp);
    });
    row.addEventListener('keydown', e=>{
        if(e.key===' '){ e.preventDefault(); if (explicit) toggleSelection(explicit); return; }
        if(e.key==='Enter'){ e.preventDefault(); navigate(fp); }
    });
    const [dlBtn, mvBtn] = row.querySelectorAll('.btn-row-action');
    bindInternalItemDrag(row, folderObj);
    row.addEventListener('dragover', e => {
        if (!dragMoveKeys.length) return;
        e.preventDefault();
        row.classList.add('drop-target');
    });
    row.addEventListener('dragleave', () => row.classList.remove('drop-target'));
    row.addEventListener('drop', e => {
        e.preventDefault();
        row.classList.remove('drop-target');
        const ids = dragMoveKeys.length ? dragMoveKeys : safeParseJson(e.dataTransfer.getData('text/plain'), []);
        moveIdsToPath(ids, fp);
    });
    dlBtn.addEventListener('click',e=>{ e.stopPropagation(); window.bridge.startDownload(folderObj.threadId,folderObj.name); });
    mvBtn.addEventListener('click',e=>{ e.stopPropagation(); if(explicit) openMoveModal(explicit); });
    row.addEventListener('contextmenu', e => showCtx(e, folderObj));
    if (explicit) {
        const delBtn=row.querySelectorAll('.btn-row-action')[2];
        if (delBtn) delBtn.addEventListener('click',e=>{ e.stopPropagation();
            askConfirm(t('deleteFolderTitle'), t('deleteFolderMessage', { name }),
                ()=>{ setStatus(t('deleting'), true); window.bridge.deleteApp(explicit.threadId, explicit.metadataMessageId||''); });
        });
    }
    return row;
}
function makeFileRow(app) {
    const type=fileType(app.name), clr=TYPE_COLOR[type]||TYPE_COLOR.generic;
    const row=document.createElement('div'); row.className='file-row';
    row.setAttribute('tabindex','0');
    row.setAttribute('role','option');
    row.setAttribute('aria-selected', selectedKeys.has(itemKey(app)) ? 'true' : 'false');
    row.setAttribute('aria-label', t('openPreview', { name:app.name }) + ', ' + fmtSize(app.sizeBytes));
    row.dataset.key = itemKey(app);
    row.draggable = false;
    row.classList.toggle('selected', selectedKeys.has(itemKey(app)));
    row.innerHTML=`<div class="row-icon">${icon(fileIcon(type),21,clr)}</div>
        <div class="row-name" title="${esc(app.name)}">${esc(app.name)}</div>
        <div class="row-date">${esc(app.uploadedAt||'—')}</div>
        <div class="row-size">${fmtSize(app.sizeBytes)}</div>
        <div class="row-actions">
            <button class="btn-row-action" title="Baixar">${icon('download',17)}</button>
            <button class="btn-row-action" title="Mover">${icon('drive_file_move',17)}</button>
            <button class="btn-row-action btn-danger-sm" title="Apagar">${icon('delete',17)}</button>
        </div>`;
    row.addEventListener('click', e=>{
        if (shouldSuppressItemClick(e)) return;
        if (e.target.closest('.btn-row-action')) return;
        toggleSelection(app);
    });
    row.addEventListener('dblclick', e=>{
        if (shouldSuppressItemClick(e)) return;
        if (e.target.closest('.btn-row-action')) return;
        if (settings.previewEnabled && canPreview(app.name)) openPreview(app);
    });
    row.addEventListener('keydown', e=>{
        if (e.target.closest('.btn-row-action')) return;
        if (e.key===' ') { e.preventDefault(); toggleSelection(app); }
        if (e.key==='Enter' && settings.previewEnabled && canPreview(app.name)) openPreview(app);
    });
    bindInternalItemDrag(row, app);
    const [dl,mv,del]=row.querySelectorAll('.btn-row-action');
    dl.addEventListener('click',  e=>{ e.stopPropagation(); window.bridge.startDownload(app.threadId,app.name); });
    mv.addEventListener('click',  e=>{ e.stopPropagation(); openMoveModal(app); });
    del.addEventListener('click', e=>{ e.stopPropagation();
        askConfirm(t('deleteFilesTitle'), t('deleteFilesMessage', { count:1 }),
            ()=>{ setStatus(t('deleting'), true); window.bridge.deleteApp(app.threadId, app.metadataMessageId||''); });
    });
    row.addEventListener('contextmenu', e => showCtx(e, app));
    return row;
}

// ─── Tipos e ícones ───────────────────────────────────────────────────────────
function itemKey(app) { return app && (app.metadataMessageId || app.threadId || app.name); }
function getSelectedApps() {
    return allApps.filter(a => selectedKeys.has(itemKey(a)) && a.metadataMessageId);
}
function focusedFileApp() {
    const focused = document.activeElement && document.activeElement.closest
        ? document.activeElement.closest('[data-key]')
        : null;
    if (!focused) return null;
    return allApps.find(a => itemKey(a) === focused.dataset.key && a.metadataMessageId) || null;
}
function updateSelectionUI() {
    const validKeys = new Set(allApps.filter(a => a.metadataMessageId).map(itemKey));
    selectedKeys = new Set([...selectedKeys].filter(k => validKeys.has(k)));
    document.querySelectorAll('[data-key]').forEach(el => {
        const selected = selectedKeys.has(el.dataset.key);
        el.classList.toggle('selected', selected);
        if (el.hasAttribute('aria-selected')) el.setAttribute('aria-selected', selected ? 'true' : 'false');
    });
    const bar = $('selection-bar');
    const count = getSelectedApps().length;
    bar.classList.toggle('hidden', count === 0);
    $('selection-count').textContent = t('selectedCount', { count });
}
function clearSelection() {
    selectedKeys.clear();
    updateSelectionUI();
}
function toggleSelection(app) {
    const key = itemKey(app);
    if (!key || !app.metadataMessageId) return;
    if (selectedKeys.has(key)) selectedKeys.delete(key);
    else selectedKeys.add(key);
    updateSelectionUI();
}
function canUseGlobalShortcut(e) {
    const target = e.target || document.activeElement;
    if (target && target.id === 'search-input' && !target.value && !$('main-area').classList.contains('hidden')) return true;
    return !target || !target.closest('input,textarea,select,[contenteditable="true"],.markdown-editor-page,.markdown-raw-editor,.preview-pdf-doc,.pdfjs-text-layer');
}
function selectableAppsInCurrentView() {
    if (searchQuery) {
        return sorted(allApps.filter(a => a.metadataMessageId && a.type !== 'folder' && (a.name || '').toLowerCase().includes(searchQuery)));
    }
    const files = filesAt(allApps, currentPath).filter(a => a.metadataMessageId);
    const folders = allApps.filter(a => a.metadataMessageId && a.type === 'folder' && (a.path || '/') === currentPath);
    return [...folders, ...files];
}
function selectAllCurrentView() {
    selectableAppsInCurrentView().forEach(app => {
        const key = itemKey(app);
        if (key) selectedKeys.add(key);
    });
    updateSelectionUI();
}
function startCardDrag(e, app) {
    if (!app.metadataMessageId) { e.preventDefault(); return; }
    const key = itemKey(app);
    if (!selectedKeys.has(key)) {
        selectedKeys.clear();
        selectedKeys.add(key);
        updateSelectionUI();
    }
    const items = getSelectedApps();
    dragMoveKeys = items.map(a => a.metadataMessageId);
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', JSON.stringify(dragMoveKeys));
    document.body.classList.add('dragging-items');
    document.querySelectorAll('[data-key]').forEach(el => {
        if (selectedKeys.has(el.dataset.key)) el.classList.add('dragging-source');
    });
    const ghost = makeDragGhost(items);
    document.body.appendChild(ghost);
    if (e.dataTransfer.setDragImage) e.dataTransfer.setDragImage(ghost, 28, 24);
    setTimeout(() => ghost.remove(), 0);
}
function makeDragGhost(items) {
    const first = items[0] || {};
    const type = fileType(first.name || '');
    const clr = first.type === 'folder' ? (first.folderColor || TYPE_COLOR.folder) : (TYPE_COLOR[type] || TYPE_COLOR.generic);
    const label = items.length > 1 ? t('selectedCount', { count:items.length }) : truncate(first.name || '', 30);
    const ghost = document.createElement('div');
    ghost.className = 'drag-ghost';
    ghost.innerHTML = `<span class="drag-ghost-icon">${icon(first.type === 'folder' ? 'folder' : fileIcon(type), 19, clr)}</span>
        <span class="drag-ghost-name">${esc(label)}</span>
        ${items.length > 1 ? `<span class="drag-ghost-count">${items.length}</span>` : ''}`;
    ghost.style.position = 'fixed';
    ghost.style.left = '-1200px';
    ghost.style.top = '-1200px';
    return ghost;
}
function clearDragState() {
    dragMoveKeys = [];
    document.body.classList.remove('dragging-items');
    document.querySelectorAll('.drop-target').forEach(el => el.classList.remove('drop-target'));
    document.querySelectorAll('.root-drop-target').forEach(el => el.classList.remove('root-drop-target'));
    document.querySelectorAll('.dragging-source').forEach(el => el.classList.remove('dragging-source'));
}
function moveIdsToPath(ids, path) {
    const cleanIds = (ids || []).filter(Boolean).filter(id => {
        const app = allApps.find(a => a.metadataMessageId === id);
        if (!app || (app.path || '/') === path) return false;
        if (app.type !== 'folder') return true;
        const source = (app.path === '/' ? '/' : app.path + '/') + app.name;
        return path !== source && !path.startsWith(source + '/');
    });
    if (!cleanIds.length) return;
    window.bridge.moveItems(JSON.stringify(cleanIds), path);
    clearDragState();
    clearSelection();
}
function downloadSelected() {
    getSelectedApps().forEach(app => window.bridge.startDownload(app.threadId, app.name));
    clearSelection();
}
function deleteSelected() {
    const items = getSelectedApps();
    if (!items.length) return;
    askConfirm(deleteConfirmTitle(items), deleteConfirmMessage(items),
        () => {
            setStatus(t('deleting'), true);
            window.bridge.deleteItems(JSON.stringify(items.map(app => app.metadataMessageId).filter(Boolean)));
            clearSelection();
        });
}
function safeParseJson(text, fallback) {
    try { return JSON.parse(text); } catch (_) { return fallback; }
}

function renderMarkdownPreview(markdown) {
    const wrap = document.createElement('div');
    wrap.className = 'markdown-preview-doc';
    const page = document.createElement('article');
    page.className = 'markdown-preview-page';
    const source = String(markdown || '');
    const excerpt = source.split(/\r?\n/).slice(0, 40).join('\n').slice(0, 6000);
    page.innerHTML = markdownToHtml(excerpt) || '<p></p>';
    wrap.appendChild(page);
    const actions = document.createElement('div');
    actions.className = 'markdown-preview-actions';
    const more = document.createElement('button');
    more.type = 'button';
    more.className = 'btn-primary markdown-preview-more';
    more.innerHTML = icon('edit_note', 17) + '<span>' + esc(t('markdownSeeMore')) + '</span>';
    more.addEventListener('click', () => {
        const app = previewAppRef;
        if (app) openMarkdownEditor(app);
    });
    actions.appendChild(more);
    wrap.appendChild(actions);
    return wrap;
}

function renderDocumentPreview(data) {
    const model = safeParseJson(data, null);
    if (model && Array.isArray(model.pages)) {
        const wrap = document.createElement('div');
        wrap.className = 'document-preview-pages';
        const formatClass = 'document-page-' + String(model.format || 'document').toLowerCase().replace(/[^a-z0-9_-]/g, '');
        model.pages.forEach((html, index) => {
            const page = document.createElement('article');
            page.className = 'document-page';
            page.classList.add(formatClass);
            page.dataset.page = String(index + 1);
            page.innerHTML = html || '<p></p>';
            wrap.appendChild(page);
        });
        return wrap;
    }
    const doc = document.createElement('div');
    doc.className = 'preview-document';
    doc.textContent = data;
    return doc;
}

function renderArchivePreview(data, archiveName = '') {
    const model = safeParseJson(data, {});
    const archiveRootName = archiveName || model.name || t('archiveTitle');
    const wrap = document.createElement('div');
    wrap.className = 'preview-archive';

    const header = document.createElement('div');
    header.className = 'archive-header';
    const format = (model.format || 'ZIP').toString().toUpperCase();
    header.innerHTML = `<div class="archive-title">${icon('folder_zip', 34, TYPE_COLOR.archive)}<div>
            <strong>${esc(t('archiveTitle'))}</strong>
            <span>${esc(format)}${model.engine ? ' / ' + esc(String(model.engine).toUpperCase()) : ''}</span>
        </div></div>
        <div class="archive-chips">
            <span>${esc(fmtSize(model.sizeBytes || 0))}</span>
            <span>${esc(t('archiveItems', { count:model.total || 0 }))}</span>
        </div>`;
    wrap.appendChild(header);

    if (model.tooLarge) {
        const msg = document.createElement('p');
        msg.className = 'archive-message';
        msg.textContent = t('archiveTooLarge', { size:fmtSize(model.sizeBytes || 0) });
        wrap.appendChild(msg);
        return wrap;
    }
    if (model.unsupportedArchive) {
        const msg = document.createElement('p');
        msg.className = 'archive-message';
        msg.textContent = t('archiveUnsupported');
        wrap.appendChild(msg);
        return wrap;
    }
    if (model.error) {
        const msg = document.createElement('p');
        msg.className = 'archive-message';
        msg.textContent = model.error;
        wrap.appendChild(msg);
        return wrap;
    }

    const entries = Array.isArray(model.entries) ? model.entries : [];
    const tree = buildArchiveTree(entries);
    let folderPath = [];

    const body = document.createElement('div');
    body.className = 'archive-body';
    setupMomentumScroll(body, { multiplier:1.08, friction:0.88, maxVelocity:150 });
    const crumbs = document.createElement('div');
    crumbs.className = 'archive-breadcrumbs';
    const summary = document.createElement('div');
    summary.className = 'archive-summary';
    const list = document.createElement('div');
    list.className = 'archive-list';

    body.appendChild(crumbs);
    body.appendChild(summary);
    body.appendChild(list);
    wrap.appendChild(body);

    const renderFolder = () => {
        const node = archiveNodeAt(tree, folderPath);
        const children = archiveChildren(node);
        renderArchiveBreadcrumbs(crumbs, folderPath, archiveRootName, nextPath => {
            folderPath = nextPath;
            renderFolder();
            if (body._stopMomentumScroll) body._stopMomentumScroll();
            body.scrollTop = 0;
        });
        summary.textContent = children.length
            ? t('archiveShowing', { shown:children.length, total:children.length })
            : t('archiveEmpty');
        list.innerHTML = '';
        children.forEach(entry => {
            list.appendChild(makeArchiveRow(entry, nextPath => {
                folderPath = nextPath;
                renderFolder();
                if (body._stopMomentumScroll) body._stopMomentumScroll();
                body.scrollTop = 0;
            }));
        });
    };
    renderFolder();
    return wrap;
}

function makeArchiveRow(entry, onOpenFolder) {
        const row = document.createElement('div');
        row.className = 'archive-row' + (entry.directory ? ' archive-folder' : '');
        row.setAttribute('role', entry.directory ? 'button' : 'listitem');
        row.setAttribute('tabindex', entry.directory ? '0' : '-1');
        const type = entry.directory ? 'folder' : fileType(entry.name || '');
        const clr = entry.directory ? TYPE_COLOR.folder : (TYPE_COLOR[type] || TYPE_COLOR.generic);
        row.innerHTML = `<span class="archive-row-icon">${icon(entry.directory ? 'folder' : fileIcon(type), 18, clr)}</span>
            <span class="archive-row-name" title="${esc(entry.path || entry.name || '')}">
                <span class="archive-base-name">${esc(entry.name || '')}</span>
            </span>
            <span class="archive-row-size">${entry.directory ? '' : fmtSize(entry.size)}</span>`;
        if (entry.directory) {
            const open = () => onOpenFolder(entry.path.split('/').filter(Boolean));
            row.addEventListener('click', open);
            row.addEventListener('keydown', e => {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    open();
                }
            });
        }
        return row;
}

function buildArchiveTree(entries) {
    const root = archiveNode('', '', true);
    entries.forEach(entry => {
        const clean = normalizeArchivePath(entry && entry.name);
        if (!clean) return;
        const parts = clean.split('/').filter(Boolean);
        let node = root;
        parts.forEach((part, index) => {
            const last = index === parts.length - 1;
            const isDirectory = !last || !!entry.directory;
            const path = node.path ? node.path + '/' + part : part;
            let child = node.children.get(part);
            if (!child) {
                child = archiveNode(part, path, isDirectory);
                node.children.set(part, child);
            }
            if (isDirectory) {
                child.directory = true;
            } else {
                if (!child.children.size) child.directory = false;
                child.size = Number(entry.size) || 0;
            }
            node = child;
        });
    });
    return root;
}

function archiveNode(name, path, directory) {
    return { name, path, directory, size:0, children:new Map() };
}

function archiveNodeAt(root, pathParts) {
    let node = root;
    pathParts.forEach(part => {
        if (node.children.has(part)) node = node.children.get(part);
    });
    return node;
}

function archiveChildren(node) {
    return Array.from(node.children.values()).sort((a, b) => {
        if (a.directory !== b.directory) return a.directory ? -1 : 1;
        return a.name.localeCompare(b.name, undefined, { numeric:true, sensitivity:'base' });
    });
}

function normalizeArchivePath(path) {
    return String(path || '').replace(/\\/g, '/').replace(/^\/+/g, '').replace(/\/+$/g, '');
}

function renderArchiveBreadcrumbs(el, pathParts, archiveName, onNavigate) {
    el.innerHTML = '';
    const root = document.createElement('button');
    root.className = 'archive-crumb archive-crumb-root' + (!pathParts.length ? ' active' : '');
    root.textContent = archiveName || t('archiveTitle');
    root.title = archiveName || t('archiveTitle');
    root.addEventListener('click', () => onNavigate([]));
    el.appendChild(root);
    pathParts.forEach((part, index) => {
        const sep = document.createElement('span');
        sep.className = 'archive-crumb-sep';
        sep.textContent = '/';
        el.appendChild(sep);
        const btn = document.createElement('button');
        btn.className = 'archive-crumb' + (index === pathParts.length - 1 ? ' active' : '');
        btn.textContent = part;
        btn.title = part;
        btn.addEventListener('click', () => onNavigate(pathParts.slice(0, index + 1)));
        el.appendChild(btn);
    });
}

function fileType(name) {
    if(!name) return 'generic';
    const e=name.split('.').pop().toLowerCase();
    if(['jpg','jpeg','png','gif','bmp','webp','tiff','tif','svg'].includes(e)) return 'image';
    if(['mp4','m4v','mov','webm','mkv','avi','3gp','wmv','flv'].includes(e))  return 'video';
    if(['mp3','flac','wav','ogg','aac','m4a'].includes(e))                     return 'audio';
    if(e==='pdf')                                                              return 'pdf';
    if(['md','markdown'].includes(e))                                          return 'markdown';
    if(['doc','docx','odt','rtf','txt'].includes(e))                           return 'document';
    if(['xls','xlsx','ods','csv'].includes(e))                                 return 'spreadsheet';
    if(['ppt','pptx','odp'].includes(e))                                       return 'presentation';
    if(['zip','zipx','jar','war','ear','apk','cbz','epub','xpi','7z','rar','tar','gz','tgz','bz2','bzip2','xz','txz','zst','lzma','cab','iso','arj','lzh','wim','dmg','deb','rpm'].includes(e)) return 'archive';
    if(['js','ts','jsx','tsx','html','css','py','java','cpp','c','cs','go',
        'rs','php','rb','swift','kt','json','xml','yaml','yml','sh','bat','ini','cfg'].includes(e)) return 'code';
    return 'generic';
}
function fileIcon(t) {
    return {image:'image',video:'movie',audio:'audio_file',pdf:'picture_as_pdf',
        markdown:'edit_note',document:'description',spreadsheet:'grid_on',presentation:'slideshow',
        archive:'folder_zip',code:'code',generic:'insert_drive_file'}[t]||'insert_drive_file';
}
function fmtSize(b) {
    b=Number(b); if(!Number.isFinite(b)||b<=0) return '0 B'; if(b<1024) return b+' B';
    if(b<1048576) return (b/1024).toFixed(0)+' KB';
    if(b<1073741824) return (b/1048576).toFixed(1)+' MB';
    if(b<1099511627776) return (b/1073741824).toFixed(2)+' GB';
    return (b/1099511627776).toFixed(2)+' TB';
}
function truncate(s,n){ return s&&s.length>n?s.slice(0,n)+'…':s; }
function esc(s){
    if(!s) return '';
    return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;').replace(/'/g,'&#39;');
}







