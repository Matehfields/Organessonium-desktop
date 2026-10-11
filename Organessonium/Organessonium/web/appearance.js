'use strict';

Object.assign(I18N['pt-BR'], {
    accentColor:'Cor de destaque', accentSystem:'Usar cor do Windows', accentPreset:'Padrões do Windows', accentCustom:'Personalizada',
    accentHelp:'Afeta apenas o app. Tons muito claros ou escuros são ajustados para contraste.',
    accentPick:'Escolher cor', accentHex:'Cor hexadecimal', accentPalette:'Cores padrão'
});
Object.assign(I18N.en, {
    accentColor:'Accent color', accentSystem:'Use Windows color', accentPreset:'Windows presets', accentCustom:'Custom',
    accentHelp:'Only affects this app. Very light or dark colors are adjusted for contrast.',
    accentPick:'Choose color', accentHex:'Hexadecimal color', accentPalette:'Preset colors'
});

const WINDOWS_ACCENT_PRESETS = ['#0078D4','#0099BC','#00B7C3','#107C10','#FFB900','#CA5010','#D13438','#E3008C','#8764B8'];
const normalizeAccentColor = value => /^#[0-9a-f]{6}$/i.test(value || '') ? value.toUpperCase() : '#0078D4';

function accentLuminance(rgb) {
    return rgb.map(channel => channel / 255).map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4)
        .reduce((sum,value,index) => sum + value * [.2126,.7152,.0722][index],0);
}

function applyAccentColor() {
    const base = normalizeAccentColor(settings.accentColorMode === 'system'
        ? systemPreferences.accentColor : settings.accentColor);
    const rgb = [1,3,5].map(offset => parseInt(base.slice(offset,offset + 2),16));
    const dark = document.documentElement.dataset.theme !== 'light';
    const background = dark ? accentLuminance([13,13,13]) : accentLuminance([248,249,250]);
    const contrast = color => (Math.max(accentLuminance(color),background) + .05) / (Math.min(accentLuminance(color),background) + .05);
    let applied = rgb;
    for (let blend = .05; contrast(applied) < 3 && blend <= 1; blend += .05)
        applied = rgb.map(channel => Math.round(channel * (1 - blend) + (dark ? 255 : 0) * blend));
    const root = document.documentElement;
    root.style.setProperty('--accent', '#' + applied.map(channel => channel.toString(16).padStart(2,'0')).join(''));
    root.style.setProperty('--accent-rgb', applied.join(','));
    root.style.setProperty('--accent-foreground', accentLuminance(applied) > .179 ? '#101010' : '#ffffff');
    root.style.setProperty('--accent-base', base);
}

function syncAccentControls() {
    const mode = $('setting-accent-mode');
    if (!mode) return;
    settings.accentColorMode = ['system','preset','custom'].includes(settings.accentColorMode) ? settings.accentColorMode : 'system';
    settings.accentColor = normalizeAccentColor(settings.accentColor);
    mode.value = settings.accentColorMode;
    $('setting-accent-label').textContent = t('accentColor');
    ['system','preset','custom'].forEach(value => $('accent-' + value + '-text').textContent = t('accent' + value[0].toUpperCase() + value.slice(1)));
    $('setting-accent-help').textContent = t('accentHelp');
    $('accent-presets').setAttribute('aria-label',t('accentPalette'));
    $('accent-presets').classList.toggle('hidden', mode.value !== 'preset');
    $('accent-custom-controls').classList.toggle('hidden', mode.value !== 'custom');
    $('setting-accent-picker').value = settings.accentColor;
    $('setting-accent-picker').setAttribute('aria-label',t('accentPick'));
    $('setting-accent-hex').value = settings.accentColor;
    $('setting-accent-hex').removeAttribute('aria-invalid');
    $('setting-accent-hex').setAttribute('aria-label',t('accentHex'));
    if (!$('accent-presets').children.length) WINDOWS_ACCENT_PRESETS.forEach(color => {
        const button = document.createElement('button');
        button.className = 'accent-swatch'; button.type = 'button'; button.dataset.accent = color;
        button.style.setProperty('--swatch',color); button.title = color; button.setAttribute('aria-label',color);
        button.addEventListener('click',() => {
            settings.accentColorMode = 'preset'; settings.accentColor = color;
            applyAccentColor(); syncAccentControls(); queueSettingsSave();
        });
        $('accent-presets').appendChild(button);
    });
    $('accent-presets').querySelectorAll('button').forEach(button => button.setAttribute('aria-pressed',String(mode.value === 'preset' && button.dataset.accent === settings.accentColor)));
}

window._onSystemAccentChanged = color => {
    systemPreferences.accentColor = normalizeAccentColor(color);
    window.__organessoniumSystemPreferences = {...(window.__organessoniumSystemPreferences || {}),accentColor:systemPreferences.accentColor};
    applyAccentColor();
};

// The content animates, not the collection or its asynchronous requests.
function animateWorkspaceContent(element) {
    if (!element) return;
    const targets = element.id === 'main-area'
        ? [...element.children].filter(child => child.matches('.content-header,.files-grid,.files-list,.empty-state'))
        : [element];
    targets.forEach(target => target.classList.remove('workspace-content-enter'));
    if (document.documentElement.dataset.motion === 'reduced') return;
    void element.offsetWidth;
    targets.forEach(target => target.classList.add('workspace-content-enter'));
}

function syncSidebarSelection() {
    const settingsOpen = settingsPanel && settingsPanel.getAttribute('aria-hidden') === 'false';
    const active = 'sidebar-' + (settingsOpen ? 'settings' : activeLibraryView);
    ['files','favorites','devices','trash','settings'].forEach(name => {
        const button = $('sidebar-' + name);
        button.classList.toggle('active',button.id === active);
        if (button.id === active) button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current');
    });
    const button = $(active), line = $('sidebar-selection-indicator'), sidebar = $('app-sidebar');
    if (!button || !line || !sidebar) return;
    const y = button.getBoundingClientRect().top - sidebar.getBoundingClientRect().top + (button.offsetHeight - 18) / 2;
    if (!line.dataset.positioned) {
        line.style.transition = 'none'; line.dataset.positioned = 'true';
        requestAnimationFrame(() => line.style.removeProperty('transition'));
    }
    line.style.transform = 'translateY(' + Math.round(y) + 'px)';
    line.style.opacity = '1';
}

document.addEventListener('DOMContentLoaded',() => {
    syncAccentControls();
    $('setting-accent-mode').addEventListener('change',event => {
        settings.accentColorMode = event.target.value;
        applyAccentColor(); syncAccentControls(); queueSettingsSave();
    });
    const setCustom = value => {
        const valid = /^#[0-9a-f]{6}$/i.test(value);
        $('setting-accent-hex').setAttribute('aria-invalid',String(!valid));
        if (!valid) return;
        settings.accentColorMode = 'custom'; settings.accentColor = normalizeAccentColor(value);
        applyAccentColor(); $('setting-accent-picker').value = settings.accentColor;
        $('setting-accent-hex').value = settings.accentColor; queueSettingsSave();
    };
    $('setting-accent-picker').addEventListener('input',event => setCustom(event.target.value));
    $('setting-accent-hex').addEventListener('input',event => setCustom(event.target.value));
    window.addEventListener('resize',syncSidebarSelection);
    syncSidebarSelection();
});
