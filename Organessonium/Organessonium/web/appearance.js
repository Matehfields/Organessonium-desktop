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
const settingsSelectControls = new Map();
let openSettingsSelect = null;

function closeSettingsSelect(focus = false) {
    const control = openSettingsSelect;
    if (!control) return;
    control.menu.classList.add('hidden');
    control.trigger.setAttribute('aria-expanded','false');
    openSettingsSelect = null;
    if (focus) control.trigger.focus({preventScroll:true});
}

function syncSettingsSelects() {
    document.querySelectorAll('#settings-panel select:not(#setting-language)').forEach(select => {
        let control = settingsSelectControls.get(select);
        if (!control) {
            const wrapper = select.closest('.settings-select-wrap');
            if (!wrapper) return;
            wrapper.classList.add('settings-custom-select');
            wrapper.querySelector(':scope > [data-icon]')?.remove();
            select.classList.add('native-hidden');
            select.tabIndex = -1;
            select.setAttribute('aria-hidden','true');
            const trigger = document.createElement('button');
            trigger.type = 'button'; trigger.className = 'settings-select-trigger';
            trigger.setAttribute('role','combobox'); trigger.setAttribute('aria-haspopup','listbox');
            trigger.setAttribute('aria-expanded','false');
            const label = select.labels[0];
            if (label) { label.id ||= select.id + '-label'; trigger.setAttribute('aria-labelledby',label.id); }
            const menu = document.createElement('div');
            menu.id = select.id + '-options'; menu.className = 'settings-select-menu hidden';
            menu.setAttribute('role','listbox'); trigger.setAttribute('aria-controls',menu.id);
            wrapper.append(trigger); document.body.append(menu);
            control = {select,trigger,menu}; settingsSelectControls.set(select,control);
            const current = control;
            const open = () => {
                closeSettingsSelect(); syncSettingsSelects();
                openSettingsSelect = current;
                const bounds = trigger.getBoundingClientRect();
                menu.style.width = Math.min(Math.max(bounds.width,240),innerWidth - 24) + 'px';
                menu.style.maxHeight = Math.min(280,innerHeight - 24) + 'px';
                menu.classList.remove('hidden');
                const height = menu.offsetHeight;
                menu.style.left = Math.max(12,Math.min(bounds.left,innerWidth - menu.offsetWidth - 12)) + 'px';
                menu.style.top = Math.max(12,bounds.bottom + height + 8 <= innerHeight ? bounds.bottom + 6 : bounds.top - height - 6) + 'px';
                trigger.setAttribute('aria-expanded','true');
                menu.querySelector('[aria-selected="true"]')?.focus({preventScroll:true});
            };
            trigger.addEventListener('click', () => openSettingsSelect === current ? closeSettingsSelect() : open());
            const navigate = event => {
                if (!['ArrowDown','ArrowUp','Home','End','Escape','Enter',' '].includes(event.key)) return;
                event.stopPropagation();
                if (event.key === 'Escape') { event.preventDefault(); closeSettingsSelect(true); return; }
                if (['Enter',' '].includes(event.key)) return;
                event.preventDefault();
                if (openSettingsSelect !== current) { open(); return; }
                const choices = [...menu.querySelectorAll('button:not(:disabled)')];
                const index = choices.indexOf(document.activeElement);
                const next = event.key === 'Home' ? 0 : event.key === 'End' ? choices.length - 1 : (index + (event.key === 'ArrowDown' ? 1 : -1) + choices.length) % choices.length;
                choices[next]?.focus({preventScroll:true});
            };
            trigger.addEventListener('keydown',navigate); menu.addEventListener('keydown',navigate);
            select.addEventListener('change',syncSettingsSelects);
        }
        const label = select.selectedOptions[0]?.textContent || '';
        if (control.trigger.dataset.label !== label) {
            control.trigger.innerHTML = '<span></span>' + icon('expand_more',18);
            control.trigger.firstElementChild.textContent = label;
            control.trigger.dataset.label = label;
        }
        control.trigger.disabled = select.disabled;
        const signature = [...select.options].map(option => [option.value,option.textContent,option.selected,option.disabled].join('\u0000')).join('\u0001');
        if (control.menu.dataset.options === signature) return;
        control.menu.dataset.options = signature; control.menu.replaceChildren();
        [...select.options].forEach(option => {
            const choice = document.createElement('button');
            choice.type = 'button'; choice.textContent = option.textContent; choice.disabled = option.disabled;
            choice.setAttribute('role','option'); choice.setAttribute('aria-selected',String(option.selected));
            choice.dataset.value = option.value;
            choice.addEventListener('click', () => {
                select.value = option.value; closeSettingsSelect(true);
                select.dispatchEvent(new Event('change',{bubbles:true}));
            });
            control.menu.append(choice);
        });
    });
}

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
    syncSettingsSelects();
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
    document.addEventListener('click',event => {
        if (openSettingsSelect && !openSettingsSelect.trigger.contains(event.target) && !openSettingsSelect.menu.contains(event.target)) closeSettingsSelect();
    });
    document.addEventListener('focusin',event => {
        if (openSettingsSelect && !openSettingsSelect.trigger.contains(event.target) && !openSettingsSelect.menu.contains(event.target)) closeSettingsSelect();
    });
    document.addEventListener('scroll',() => closeSettingsSelect(),true);
    window.addEventListener('resize',() => closeSettingsSelect());
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
