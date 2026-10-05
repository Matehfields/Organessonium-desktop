(function () {
    const send = (method, args) => {
        if (!window.chrome || !window.chrome.webview) return;
        window.chrome.webview.postMessage({ method, args: Array.from(args || []) });
    };

    const updateSettingsCache = args => {
        const current = window.__organessoniumSettings || {};
        window.__organessoniumSettings = {
            ...current,
            cardSize: args[0],
            downloadThreads: args[1],
            theme: args[2],
            downloadPath: args[3],
            tempPath: args[4],
            previewEnabled: args[5],
            language: args[6],
            animationsEnabled: args[7],
            hardwareAcceleration: args[8],
            videoPreviewQuality: args[9],
dataPath: args[10],
            uploadThreads: args[11],
            maxParallelUploads: args[12],
            previewThreads: args[20],
            videoPrefetchChunks: args[21],
            proxy: {
                proxyMode: args[13],
                proxyHost: args[14],
                proxyPort: args[15],
                proxyProtocol: args[16],
                proxyAuthenticationEnabled: args[17],
                proxyUsername: args[18],
                proxyPassword: args[19]
            }
        };
    };

    window.bridge = {
        connect: function () { send('connect', arguments); },
        logout: function () { send('logout', arguments); },
        openGuide: function () { send('openGuide', arguments); },
        loadApps: function () { send('loadApps', arguments); },
        loadFavorites: function () { send('loadFavorites', arguments); },
        loadSynchronizedDevices: function () { send('loadSynchronizedDevices', arguments); },
        loadSyncState: function () { send('loadSyncState', arguments); },
        discoverSyncFolders: function () { send('discoverSyncFolders', arguments); },
        chooseSyncFolder: function () { send('chooseSyncFolder', arguments); },
        openSyncFolder: function () { send('openSyncFolder', arguments); },
        addSyncFolder: function () { send('addSyncFolder', arguments); },
        updateSyncFolder: function () { send('updateSyncFolder', arguments); },
        removeSyncFolder: function () { send('removeSyncFolder', arguments); },
        syncNow: function () { send('syncNow', arguments); },
        pauseSync: function () { send('pauseSync', arguments); },
        resolveSyncConflict: function () { send('resolveSyncConflict', arguments); },
        setSyncPreferences: function () { send('setSyncPreferences', arguments); },
        setFavorite: function () { send('setFavorite', arguments); },
        refreshCurrentFolder: function () { send('refreshCurrentFolder', arguments); },
        refreshServerIndex: function () { send('refreshServerIndex', arguments); },
        loadVisibleThumbnails: function () { send('loadVisibleThumbnails', arguments); },
        loadVisibleFolderStatistics: function () { send('loadVisibleFolderStatistics', arguments); },
        requestPreview: function () { send('requestPreview', arguments); },
        requestVideoQuality: function () { send('requestVideoQuality', arguments); },
        closePreview: function () { send('closePreview', arguments); },
        videoPlayback: function () { send('videoPlayback', arguments); },
        openPreviewMedia: function () { send('openPreviewMedia', arguments); },
        requestPdfPages: function () { send('requestPdfPages', arguments); },
        setCurrentPath: function () { send('setCurrentPath', arguments); },
        createFolder: function () { send('createFolder', arguments); },
        moveItem: function () { send('moveItem', arguments); },
        moveItems: function () { send('moveItems', arguments); },
        updateFolderColor: function () { send('updateFolderColor', arguments); },
        renameItem: function () { send('renameItem', arguments); },
        startUpload: function () { send('startUpload', arguments); },
        startFolderUpload: function () { send('startFolderUpload', arguments); },
        startDownload: function () { send('startDownload', arguments); },
        cancelTransfers: function () { send('cancelTransfers', arguments); },
        resumeTransfers: function () { send('resumeTransfers', arguments); },
        failInterruptedTransfers: function () { send('failInterruptedTransfers', arguments); },
        deleteApp: function () { send('deleteApp', arguments); },
        deleteItems: function () { send('deleteItems', arguments); },
        restoreItems: function () { send('restoreItems', arguments); },
        saveMarkdown: function () { send('saveMarkdown', arguments); },
        uploadMarkdownImage: function () { send('uploadMarkdownImage', arguments); },
        uploadMarkdownImageData: function () { send('uploadMarkdownImageData', arguments); },
        resolveMarkdownImage: function () { send('resolveMarkdownImage', arguments); },
        resolveEncryptedMarkdownImage: function () { send('resolveEncryptedMarkdownImage', arguments); },
        resolveEncryptedDriveMarkdownImage: function () { send('resolveEncryptedDriveMarkdownImage', arguments); },
        configureCustomEncryption: function () { send('configureCustomEncryption', arguments); },
        setEncryptionMode: function () { send('setEncryptionMode', arguments); },
        exportCustomEncryptionKey: function () { send('exportCustomEncryptionKey', arguments); },
        importCustomEncryptionKey: function () { send('importCustomEncryptionKey', arguments); },
        chooseDownloadPath: function () { send('chooseDownloadPath', arguments); },
        chooseTempPath: function () { send('chooseTempPath', arguments); },
        chooseDataPath: function () { send('chooseDataPath', arguments); },
        clearPreviewCache: function () { send('clearPreviewCache', arguments); },
        getPreviewCacheSize: function () { send('getPreviewCacheSize', arguments); },
        resolveUploadConflict: function () { send('resolveUploadConflict', arguments); },
        applyWindowTheme: function () { send('applyWindowTheme', arguments); },
        setHostFullscreen: function () { send('setHostFullscreen', arguments); },
        setTitleBarDragRegion: function () { send('setTitleBarDragRegion', arguments); },
        testProxy: function () { send('testProxy', arguments); },
        saveSettings: function () {
            const args = Array.from(arguments);
            updateSettingsCache(args);
            send('saveSettings', args);
        },
        getSettings: function () {
            return JSON.stringify(window.__organessoniumSettings || {});
        },
        getSystemPreferences: function () {
            const language = String((navigator.languages && navigator.languages[0]) || navigator.language || '')
                .toLowerCase()
                .startsWith('pt') ? 'pt-BR' : 'en';
            const theme = window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
            return JSON.stringify({ theme, language, ...(window.__organessoniumSystemPreferences || {}) });
        }
    };
})();

