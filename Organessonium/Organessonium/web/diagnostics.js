// Temporary diagnostics UI. No filesystem access and no configuration mutation.
window._onDiagnosticStatus = value => {
    const status = document.getElementById('diagnostic-status');
    const path = document.getElementById('diagnostic-directory');
    if (!status || !path) return;
    const english = document.documentElement.lang?.startsWith('en');
    status.textContent = value?.enabled
        ? (english ? 'Active, local collection only.' : 'Ativo, coleta somente local.')
        : (english ? 'Disabled or unavailable.' : 'Desativado ou indisponível.');
    path.textContent = value?.directory || '';
    path.title = value?.sessionId || '';
};
