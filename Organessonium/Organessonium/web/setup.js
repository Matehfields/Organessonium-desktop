const inpToken = document.getElementById('inp-token');
const inpNames = document.getElementById('inp-names');
const inpForum = document.getElementById('inp-forum');
const btnConnect = document.getElementById('btn-connect');
const btnToggleToken = document.getElementById('btn-toggle-token');
const btnGuide = document.getElementById('btn-guide');
const btnCreateChannels = document.getElementById('btn-create-channels');
const btnCustomChannelNames = document.getElementById('btn-custom-channel-names');
const btnChannelIds = document.getElementById('btn-channel-ids');
const channelFields = document.getElementById('channel-fields');
const channelActions = document.getElementById('channel-actions');
const errorBox   = document.getElementById('error-box');
const formArea   = document.getElementById('form-area');
const loadArea   = document.getElementById('loading-area');
const loadMsg    = document.getElementById('loading-msg');
const selProxyMode   = document.getElementById('sel-proxy-mode');
const proxyManual    = document.getElementById('proxy-manual-fields');
const inpProxyHost   = document.getElementById('inp-proxy-host');
const inpProxyPort   = document.getElementById('inp-proxy-port');
const selProxyProtocol = document.getElementById('sel-proxy-protocol');
const chkProxyAuth   = document.getElementById('chk-proxy-auth');
const proxyAuthFields = document.getElementById('proxy-auth-fields');
const inpProxyUser   = document.getElementById('inp-proxy-user');
const inpProxyPass   = document.getElementById('inp-proxy-pass');
const btnTestProxy   = document.getElementById('btn-test-proxy');
const proxyTestResult = document.getElementById('proxy-test-result');

const SETUP_I18N = {
    'pt-BR': {
        title:'Organessônium - Configuração',
        subtitle:'Configure a conexão com o Discord para começar',
        token:'Token do Bot',
        toggle:'Mostrar/ocultar',
        tokenHint:'Developer Portal → Seu App → Bot → Reset Token',
        customNames:'Usar nomes diferentes para os canais',
        useIds:'Configurar pelos IDs dos canais',
        names:'Canal de índice names',
        namesHint:'Canal de texto usado como índice de arquivos',
        forum:'Fórum de arquivos files',
        forumHint:'Canal do tipo Fórum usado para armazenar os arquivos',
        namesId:'ID do canal names',
        namesIdHint:'ID do canal de texto usado como índice de arquivos',
        forumId:'ID do fórum files',
        forumIdHint:'ID do canal do tipo Fórum usado para armazenar os arquivos',
        createChannels:'Criar canais automaticamente',
        connect:'Conectar',
        guide:'Como criar o bot e configurar o Discord passo a passo',
        connecting:'Conectando...',
        findingChannels:'Procurando canais no servidor...',
        connectingDiscord:'Conectando ao Discord...',
        tokenRequired:'Insira o token do bot.',
        namesRequired:'Informe um nome para o canal de índice.',
        forumRequired:'Informe um nome para o fórum de arquivos.'
,idsRequired:'Informe os dois IDs dos canais.'
        ,invalidIds:'Os IDs devem conter apenas números e ter entre 16 e 22 dígitos.'
        ,proxyTitle:'Rede e proxy'
        ,proxyMode:'Modo de conexão'
        ,proxyModeAuto:'Automático (usar configuração do sistema)'
        ,proxyModeDisabled:'Desativado (conexão direta)'
        ,proxyModeManual:'Manual'
        ,proxyHint:'O proxy é usado para falar com o Discord. Mude aqui se estiver atrás de um firewall ou rede corporativa.'
        ,proxyHost:'Endereço do proxy'
        ,proxyPort:'Porta'
        ,proxyProtocol:'Protocolo'
        ,proxyProtocolAuto:'Detectar automaticamente'
        ,proxyProtocolHttp:'HTTP'
        ,proxyProtocolHttps:'HTTPS (CONNECT)'
        ,proxyProtocolSocks5:'SOCKS5'
        ,proxyProtocolSocks4a:'SOCKS4a'
        ,proxyProtocolSocks4:'SOCKS4'
        ,proxyAuth:'Requer autenticação (usuário e senha)'
        ,proxyUser:'Usuário'
        ,proxyPass:'Senha'
        ,proxyTest:'Testar conexão com o proxy'
        ,proxyTesting:'Testando conexão com o proxy...'
        ,proxyTestSuccess:'Conectado com sucesso.'
        ,proxyTestDetected:'Conectado com sucesso (protocolo detectado: {protocol}).'
        ,proxyTestFail:'Falha: {message}'
    },
    en: {
        title:'Organessônium - Setup',
        subtitle:'Configure the Discord connection to get started',
        token:'Bot Token',
        toggle:'Show/hide',
        tokenHint:'Developer Portal → Your App → Bot → Reset Token',
        customNames:'Use different channel names',
        useIds:'Configure using channel IDs',
        names:'Index channel names',
        namesHint:'Text channel used as the file index',
        forum:'Files forum files',
        forumHint:'Forum channel used to store files',
        namesId:'names channel ID',
        namesIdHint:'ID of the text channel used as the file index',
        forumId:'files forum ID',
        forumIdHint:'ID of the forum channel used to store files',
        createChannels:'Create channels automatically',
        connect:'Connect',
        guide:'How to create the bot and configure Discord step by step ->',
        connecting:'Connecting...',
        findingChannels:'Looking for channels on the server...',
        connectingDiscord:'Connecting to Discord...',
        tokenRequired:'Enter the bot token.',
        namesRequired:'Enter a name for the index channel.',
        forumRequired:'Enter a name for the files forum.'
        ,idsRequired:'Enter both channel IDs.'
        ,invalidIds:'IDs must contain only numbers and be between 16 and 22 digits long.'
        ,proxyTitle:'Network and proxy'
        ,proxyMode:'Connection mode'
        ,proxyModeAuto:'Automatic (use system settings)'
        ,proxyModeDisabled:'Disabled (direct connection)'
        ,proxyModeManual:'Manual'
        ,proxyHint:'The proxy is used to talk to Discord. Change this if you are behind a firewall or corporate network.'
        ,proxyHost:'Proxy address'
        ,proxyPort:'Port'
        ,proxyProtocol:'Protocol'
        ,proxyProtocolAuto:'Detect automatically'
        ,proxyProtocolHttp:'HTTP'
        ,proxyProtocolHttps:'HTTPS (CONNECT)'
        ,proxyProtocolSocks5:'SOCKS5'
        ,proxyProtocolSocks4a:'SOCKS4a'
        ,proxyProtocolSocks4:'SOCKS4'
        ,proxyAuth:'Requires authentication (username and password)'
        ,proxyUser:'Username'
        ,proxyPass:'Password'
        ,proxyTest:'Test proxy connection'
        ,proxyTesting:'Testing proxy connection...'
        ,proxyTestSuccess:'Connected successfully.'
        ,proxyTestDetected:'Connected successfully (detected protocol: {protocol}).'
        ,proxyTestFail:'Failed: {message}'
    }
};

let setupLanguage = systemLanguage();
let channelInputMode = 'auto';
let setupThemeSetting = 'system';
let setupPrefilled = false;

window.onBridgeReady = function() {
    applySetupPreferences();
};

applySetupPreferences();

function systemLanguage() {
    const lang = String((navigator.languages && navigator.languages[0]) || navigator.language || '').toLowerCase();
    return lang.startsWith('pt') ? 'pt-BR' : 'en';
}

function systemTheme() {
    return window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
}

function setupText(key) {
    return (SETUP_I18N[setupLanguage] && SETUP_I18N[setupLanguage][key]) || SETUP_I18N['pt-BR'][key] || key;
}

function applySetupPreferences() {
    let prefs = { language:systemLanguage(), theme:systemTheme() };
    let saved = { language:'system', theme:'system' };
    try {
        if (window.bridge && window.bridge.getSystemPreferences) {
            prefs = { ...prefs, ...JSON.parse(window.bridge.getSystemPreferences()) };
        }
    } catch (_) {}
    try {
        if (window.bridge && window.bridge.getSettings) {
            saved = { ...saved, ...JSON.parse(window.bridge.getSettings()) };
        }
    } catch (_) {}
    prefillSetupFields(saved);
    prefillProxyFields(saved.proxy);
    const languageSetting = ['system', 'pt-BR', 'en'].includes(saved.language) ? saved.language : 'system';
    setupThemeSetting = ['system', 'dark', 'light'].includes(saved.theme) ? saved.theme : 'system';
    setupLanguage = languageSetting === 'system' ? (prefs.language === 'pt-BR' ? 'pt-BR' : 'en') : languageSetting;
    const theme = setupThemeSetting === 'system' ? prefs.theme : setupThemeSetting;
    document.documentElement.lang = setupLanguage;
    document.documentElement.setAttribute('data-theme', theme === 'light' ? 'light' : 'dark');
    document.title = setupText('title');

    const labels = document.querySelectorAll('.field label');
    const hints = document.querySelectorAll('.hint');
    document.querySelector('.subtitle').textContent = setupText('subtitle');
    labels[0].textContent = setupText('token');
    labels[1].innerHTML = setupText('names').replace('names', '<code>names</code>');
    labels[2].innerHTML = setupText('forum').replace('files', '<code>files</code>');
    hints[0].textContent = setupText('tokenHint');
    hints[1].textContent = setupText('namesHint');
    hints[2].textContent = setupText('forumHint');
btnCreateChannels.textContent = setupText('createChannels');
    btnCustomChannelNames.textContent = setupText('customNames');
    btnChannelIds.textContent = setupText('useIds');
    btnToggleToken.title = setupText('toggle');
    btnToggleToken.setAttribute('aria-label', setupText('toggle'));
    loadMsg.textContent = setupText('connecting');
    applyProxyLabels();
    applyChannelFieldMode();
    renderSetupIcons();
}

function applyProxyLabels() {
    const summary = document.querySelector('#proxy-details summary');
    if (summary) summary.textContent = setupText('proxyTitle');
    document.querySelector('#proxy-details .field label[for="sel-proxy-mode"]').textContent = setupText('proxyMode');
    document.querySelector('#proxy-details .hint').textContent = setupText('proxyHint');
    const modeOptions = document.querySelectorAll('#sel-proxy-mode option');
    modeOptions[0].textContent = setupText('proxyModeAuto');
    modeOptions[1].textContent = setupText('proxyModeDisabled');
    modeOptions[2].textContent = setupText('proxyModeManual');
    document.querySelector('#proxy-manual-fields .field label[for="inp-proxy-host"]').textContent = setupText('proxyHost');
    document.querySelector('#proxy-manual-fields .field label[for="inp-proxy-port"]').textContent = setupText('proxyPort');
    document.querySelector('#proxy-manual-fields .field label[for="sel-proxy-protocol"]').textContent = setupText('proxyProtocol');
    const protocolOptions = document.querySelectorAll('#sel-proxy-protocol option');
    protocolOptions[0].textContent = setupText('proxyProtocolAuto');
    protocolOptions[1].textContent = setupText('proxyProtocolHttp');
    protocolOptions[2].textContent = setupText('proxyProtocolHttps');
    protocolOptions[3].textContent = setupText('proxyProtocolSocks5');
    protocolOptions[4].textContent = setupText('proxyProtocolSocks4a');
    protocolOptions[5].textContent = setupText('proxyProtocolSocks4');
    document.querySelector('#chk-proxy-auth').closest('.check-row').querySelector('span').textContent = setupText('proxyAuth');
    document.querySelector('#proxy-auth-fields .field label[for="inp-proxy-user"]').textContent = setupText('proxyUser');
    document.querySelector('#proxy-auth-fields .field label[for="inp-proxy-pass"]').textContent = setupText('proxyPass');
    btnTestProxy.textContent = setupText('proxyTest');
}

function setupIcon(name, size) {
    return typeof icon === 'function' ? icon(name, size || 20) : '';
}

function renderSetupIcons() {
    const tokenVisible = inpToken.type === 'text';
    btnToggleToken.innerHTML = setupIcon(tokenVisible ? 'visibility_off' : 'visibility', 22);
    btnConnect.innerHTML = `<span class="btn-icon">${setupIcon('login', 22)}</span><span>${setupText('connect')}</span>`;
    btnGuide.innerHTML = `<span>${setupText('guide')}</span><span class="guide-icon">${setupIcon('open_in_new', 18)}</span>`;
}

function prefillSetupFields(saved) {
    if (setupPrefilled || !saved) return;
    const token = typeof saved.botToken === 'string' ? saved.botToken : '';
    const namesId = typeof saved.namesChannelId === 'string' ? saved.namesChannelId : '';
    const forumId = typeof saved.appsForumId === 'string' ? saved.appsForumId : '';
    if (token && !inpToken.value) inpToken.value = token;
    if (namesId || forumId) {
        channelInputMode = 'ids';
        inpNames.value = namesId;
        inpForum.value = forumId;
        channelFields.classList.remove('hidden');
        channelActions.classList.add('hidden');
        setupPrefilled = true;
        return;
    }
    if (token) setupPrefilled = true;
}

function prefillProxyFields(proxy) {
    if (!proxy) return;
    const mode = ['automatic', 'disabled', 'manual'].includes(proxy.proxyMode) ? proxy.proxyMode : 'automatic';
    const protocol = ['auto', 'http', 'https', 'socks5', 'socks4a', 'socks4'].includes(proxy.proxyProtocol) ? proxy.proxyProtocol : 'auto';
    selProxyMode.value = mode;
    if (typeof proxy.proxyHost === 'string' && proxy.proxyHost) inpProxyHost.value = proxy.proxyHost;
    if (Number.isFinite(Number(proxy.proxyPort)) && Number(proxy.proxyPort) > 0) inpProxyPort.value = String(proxy.proxyPort);
    selProxyProtocol.value = protocol;
    if (proxy.proxyAuthenticationEnabled === true) {
        chkProxyAuth.checked = true;
        if (typeof proxy.proxyUsername === 'string' && proxy.proxyUsername) inpProxyUser.value = proxy.proxyUsername;
        if (typeof proxy.proxyPassword === 'string' && proxy.proxyPassword) inpProxyPass.value = proxy.proxyPassword;
    }
    applyProxyFieldsVisibility();
}

function applyProxyFieldsVisibility() {
    const manual = selProxyMode.value === 'manual';
    proxyManual.classList.toggle('hidden', !manual);
    proxyAuthFields.classList.toggle('hidden', !chkProxyAuth.checked);
}

function collectProxySettings() {
    const proxy = {
        proxyMode: selProxyMode.value,
        proxyHost: inpProxyHost.value.trim(),
        proxyPort: Number(inpProxyPort.value) || 0,
        proxyProtocol: selProxyProtocol.value,
        proxyAuthenticationEnabled: chkProxyAuth.checked,
        proxyUsername: chkProxyAuth.checked ? inpProxyUser.value : '',
        proxyPassword: chkProxyAuth.checked ? inpProxyPass.value : ''
    };
    return proxy;
}
window._onSetupConnecting = function(msg) {
    formArea.classList.add('hidden');
    loadArea.classList.remove('hidden');
    loadMsg.textContent = msg || setupText('findingChannels');
};

window._onSetupError = function(msg) {
    formArea.classList.remove('hidden');
    loadArea.classList.add('hidden');
    errorBox.textContent = msg;
    errorBox.classList.remove('hidden');
    channelActions.classList.add('hidden');
    btnConnect.disabled = false;
};

window._onSetupChannelsMissing = function(msg) {
    formArea.classList.remove('hidden');
    loadArea.classList.add('hidden');
    errorBox.textContent = msg;
    errorBox.classList.remove('hidden');
    channelActions.classList.remove('hidden');
    btnConnect.disabled = false;
};

window._onSystemThemeChanged = function(theme) {
    if (setupThemeSetting !== 'system') return;
    document.documentElement.setAttribute('data-theme', theme === 'light' ? 'light' : 'dark');
};

btnToggleToken.addEventListener('click', () => {
    inpToken.type = inpToken.type === 'password' ? 'text' : 'password';
    renderSetupIcons();
});

btnCustomChannelNames.addEventListener('click', () => {
    channelInputMode = 'names';
    if (!inpNames.value.trim() || /^\d+$/.test(inpNames.value.trim())) inpNames.value = 'names';
    if (!inpForum.value.trim() || /^\d+$/.test(inpForum.value.trim())) inpForum.value = 'files';
    channelFields.classList.remove('hidden');
    channelActions.classList.add('hidden');
    applyChannelFieldMode();
    inpNames.focus();
});

btnChannelIds.addEventListener('click', () => {
    channelInputMode = 'ids';
    if (inpNames.value.trim() === 'names') inpNames.value = '';
    if (inpForum.value.trim() === 'files') inpForum.value = '';
    channelFields.classList.remove('hidden');
    channelActions.classList.add('hidden');
    applyChannelFieldMode();
    inpNames.focus();
});

btnCreateChannels.addEventListener('click', () => tryConnect(true));
btnConnect.addEventListener('click', () => tryConnect(false));
document.addEventListener('keydown', e => {
    if (e.key === 'Enter' && loadArea.classList.contains('hidden')) tryConnect(false);
});

selProxyMode.addEventListener('change', applyProxyFieldsVisibility);
chkProxyAuth.addEventListener('change', applyProxyFieldsVisibility);

btnTestProxy.addEventListener('click', () => {
    const proxy = collectProxySettings();
    proxyTestResult.classList.remove('hidden');
    proxyTestResult.textContent = setupText('proxyTesting');
    proxyTestResult.className = 'proxy-test-result testing';
    btnTestProxy.disabled = true;
    window.bridge.testProxy(proxy.proxyHost, proxy.proxyPort, proxy.proxyProtocol, proxy.proxyAuthenticationEnabled, proxy.proxyUsername, proxy.proxyPassword, true);
});

window._onProxyTestResult = function(success, message, protocol) {
    btnTestProxy.disabled = false;
    if (success === true) {
        const text = (protocol && protocol !== ''
            ? setupText('proxyTestDetected').replace('{protocol}', protocol)
            : setupText('proxyTestSuccess'));
        proxyTestResult.textContent = text;
        proxyTestResult.className = 'proxy-test-result ok';
    } else {
        proxyTestResult.textContent = setupText('proxyTestFail').replace('{message}', message || '');
        proxyTestResult.className = 'proxy-test-result fail';
    }
    proxyTestResult.classList.remove('hidden');
};

[inpToken, inpNames, inpForum].forEach(inp =>
    inp.addEventListener('keydown', e => { if (e.key === 'Enter') tryConnect(false); })
);

btnGuide.addEventListener('click', () => {
    if (window.bridge && window.bridge.openGuide) window.bridge.openGuide();
});

function tryConnect(allowCreate) {
    errorBox.classList.add('hidden');
    channelActions.classList.add('hidden');
    btnConnect.disabled = true;
    const token  = inpToken.value.trim();
    const names  = channelInputMode === 'auto' ? 'names' : inpNames.value.trim();
    const forum  = channelInputMode === 'auto' ? 'files' : inpForum.value.trim();
if (!token)  { showErr(setupText('tokenRequired')); return; }
    if (channelInputMode === 'ids') {
        if (!names || !forum) { showErr(setupText('idsRequired')); return; }
        if (!/^\d{16,22}$/.test(names) || !/^\d{16,22}$/.test(forum)) { showErr(setupText('invalidIds')); return; }
    }
    if (!names)  { showErr(setupText('namesRequired')); return; }
    if (!forum)  { showErr(setupText('forumRequired')); return; }
    window.bridge.connect(token, names, forum, !!allowCreate, JSON.stringify(collectProxySettings()));
}

function applyChannelFieldMode() {
    const labels = document.querySelectorAll('.field label');
    const hints = document.querySelectorAll('.hint');
    const ids = channelInputMode === 'ids';
    labels[1].innerHTML = setupText(ids ? 'namesId' : 'names').replace('names', '<code>names</code>');
    labels[2].innerHTML = setupText(ids ? 'forumId' : 'forum').replace('files', '<code>files</code>');
    hints[1].textContent = setupText(ids ? 'namesIdHint' : 'namesHint');
    hints[2].textContent = setupText(ids ? 'forumIdHint' : 'forumHint');
    inpNames.placeholder = ids ? '123456789012345678' : 'names';
    inpForum.placeholder = ids ? '123456789012345678' : 'files';
    inpNames.inputMode = ids ? 'numeric' : 'text';
    inpForum.inputMode = ids ? 'numeric' : 'text';
}

function showErr(msg) {
    errorBox.textContent = msg;
    errorBox.classList.remove('hidden');
    btnConnect.disabled = false;
}
