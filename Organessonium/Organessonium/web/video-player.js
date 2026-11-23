(() => {
    'use strict';
    const rates = [.25, .5, .75, 1, 1.25, 1.5, 1.75, 2];
    const defaults = { volume: 1, muted: false, rate: 1, captions: false, captionSize: 100,
        captionColor: 'white', captionBackground: 70, autoNext: false, repeat: false, ambient: false, remaining: false, resume: true };
    const clamp = (n, min, max) => Math.max(min, Math.min(max, n));
    const valid = n => Number.isFinite(n) && n >= 0;
    const asText = value => valid(value) ? formatMediaTime(value) : 'Indisponível';
    const decimal = value => value.toLocaleString('pt-BR', { maximumFractionDigits: 2 });
    const button = (action, glyph, title, old = '') =>
        '<button type="button" class="vp-button" data-action="' + action + '" ' + old +
        ' title="' + title + '" aria-label="' + title + '">' + icon(glyph, 20) + '</button>';

    function bufferedRanges(video) {
        const ranges = [];
        for (let i = 0; i < video.buffered.length; i++) ranges.push([video.buffered.start(i), video.buffered.end(i)]);
        return ranges;
    }

    function convertSubtitles(text, filename) {
        const clean = text.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n').trim();
        if (filename.toLowerCase().endsWith('.vtt')) {
            if (!/^WEBVTT(?:[ \t\n]|$)/.test(clean)) throw new Error('WebVTT inválido.');
            return clean + '\n';
        }
        const cues = clean.split(/\n[ \t]*\n/).map(block => {
            const lines = block.split('\n');
            const at = lines.findIndex(line => line.includes('-->'));
            if (at < 0 || !/^\s*\d{1,3}:\d{2}:\d{2}[,.]\d{3}\s+-->\s+\d{1,3}:\d{2}:\d{2}[,.]\d{3}\s*$/.test(lines[at])) return '';
            const time = lines[at].trim().replace(/,/g, '.').replace(/(^| )(\d):/g, '$10$2:');
            return [time, ...lines.slice(at + 1)].join('\n');
        }).filter(Boolean);
        if (!cues.length) throw new Error('Nenhuma legenda válida encontrada.');
        return 'WEBVTT\n\n' + cues.join('\n\n') + '\n';
    }

    function mount(video, details, actions) {
        const lifetime = new AbortController();
        const listen = (target, name, handler, options = {}) =>
            target?.addEventListener(name, handler, { ...options, signal: lifetime.signal });
        const preferences = { ...defaults, ...details.preferences };
        // Import the previous player's volume once. SQLite is authoritative thereafter.
        if (!preferences.initialized && details.preferences) {
            try {
                const legacy = JSON.parse(localStorage.getItem('video-volume') || 'null');
                if (valid(legacy?.volume) && legacy.volume <= 1) Object.assign(preferences, legacy);
            } catch (_) { /* Invalid legacy data can be discarded. */ }
        }
        const host = document.createElement('video-player');
        host.className = 'vp-host';
        const frame = document.createElement('media-container');
        frame.className = 'preview-video-frame vp-frame';
        frame.tabIndex = 0;
        frame.setAttribute('aria-label', 'Reprodutor de vídeo');
        const controls = document.createElement('div');
        controls.className = 'vp-controls';
        controls.innerHTML = '<div class="vp-timeline"><div class="vp-track"><div class="vp-buffer"></div><i class="vp-played"></i><div class="vp-chapters"></div></div>' +
            '<input type="range" min="0" max="0" step="any" value="0" data-video-seek aria-label="Posição no vídeo">' +
            '<div class="vp-hover" hidden><img alt="" hidden><span></span></div></div>' +
            '<div class="vp-bar"><div class="vp-group">' +
            '<media-play-button class="vp-button" data-video-play label="Reproduzir ou pausar" title="Reproduzir ou pausar (K)">' + icon('play_arrow', 22) + '</media-play-button>' +
            button('restart', 'restart_alt', 'Reiniciar') + button('back', 'replay_10', 'Voltar 10 segundos') +
            button('forward', 'forward_10', 'Avançar 10 segundos') +
            '<media-mute-button class="vp-button" data-video-mute label="Silenciar ou restaurar áudio" title="Silenciar (M)">' + icon('volume_up', 19) + '</media-mute-button>' +
            '<input class="vp-volume" data-video-volume aria-label="Volume" type="range" min="0" max="1" step=".01">' +
            '<button type="button" class="vp-time" data-action="time" title="Alternar tempo restante"><span data-video-current>0:00</span><span aria-hidden="true"> / </span><span data-video-duration>0:00</span></button></div>' +
            '<div class="vp-group">' + button('cc', 'closed_caption', 'Legendas') +
            button('settings', 'settings', 'Configurações') + button('mini', 'branding_watermark', 'Mini player') +
            button('pip', 'picture_in_picture_alt', 'Picture-in-Picture') +
            button('fullscreen', 'fullscreen', 'Tela cheia (F)', 'data-video-fullscreen') + '</div></div>';
        const spinner = document.createElement('div');
        spinner.className = 'vp-spinner';
        spinner.setAttribute('role', 'status');
        spinner.hidden = true;
        const notice = document.createElement('output');
        notice.className = 'vp-notice';
        notice.setAttribute('aria-live', 'polite');
        notice.hidden = true;
        const panel = document.createElement('section');
        panel.className = 'vp-panel';
        panel.hidden = true;
        panel.setAttribute('aria-label', 'Opções do reprodutor');
        const stats = document.createElement('section');
        stats.className = 'vp-stats';
        stats.hidden = true;
        stats.setAttribute('aria-label', 'Estatísticas para nerds');
        stats.innerHTML = '<header><strong>Estatísticas para nerds</strong>' + button('stats', 'close', 'Fechar estatísticas') + '</header><dl></dl>';
        const errorPanel = document.createElement('section');
        errorPanel.className = 'vp-error';
        errorPanel.hidden = true;
        errorPanel.setAttribute('role', 'alert');
        errorPanel.innerHTML = '<strong></strong><p></p><div>' + button('retry', 'refresh', 'Tentar novamente') +
            button('external', 'open_in_new', 'Abrir externamente') + button('download', 'download', 'Baixar arquivo') + '</div>';
        const subtitles = document.createElement('input');
        subtitles.type = 'file';
        subtitles.accept = '.vtt,.srt';
        subtitles.hidden = true;
        const ambient = document.createElement('canvas');
        ambient.className = 'vp-ambient';
        ambient.width = 24;
        ambient.height = 14;
        ambient.hidden = true;
        frame.append(ambient, video, controls, spinner, notice, panel, stats, errorPanel, subtitles);
        host.append(frame);

        const seek = controls.querySelector('[data-video-seek]');
        const timeline = controls.querySelector('.vp-timeline');
        const hover = controls.querySelector('.vp-hover');
        const timelineImage = hover.querySelector('img');
        const volume = controls.querySelector('[data-video-volume]');
        const play = controls.querySelector('[data-video-play]');
        const mute = controls.querySelector('[data-video-mute]');
        const pip = controls.querySelector('[data-action="pip"]');
        pip.hidden = !document.pictureInPictureEnabled || !video.requestPictureInPicture;
        const thumbnails = new Map();
        const thumbnailVideo = document.createElement('video');
        thumbnailVideo.muted = true;
        thumbnailVideo.playsInline = true;
        thumbnailVideo.crossOrigin = 'anonymous';
        thumbnailVideo.preload = 'metadata';
        const thumbnailCanvas = document.createElement('canvas');
        const buffer = controls.querySelector('.vp-buffer');
        const played = controls.querySelector('.vp-played');
        const captions = controls.querySelector('[data-action="cc"]');
        const current = controls.querySelector('[data-video-current]');
        const total = controls.querySelector('[data-video-duration]');
        let dragging = false, reduced = false, hostFull = false, panelType = '', panelTrigger = null;
        let hideTimer, spinnerTimer, noticeTimer, saveTimer, statsTimer, hoverTimer, thumbTimeout, clickTimer;
        let storedRange = '', requestedThumbnail = null, thumbUrl = '', subtitleUrl = '', subtitleTrack, snapshotUrl, snapshotTimer;
        let metadata = null, original = null, informationSeen = false, frameTimes, inspectAbort, frameHandle;
        let lastFrame = null, stepTarget = null, frameMark = null;
        let requestQuality = false, waiting = null, bufferCount = 0, bufferTotal = 0, lastBuffer = 0, seekStarted = 0, lastSeek = null;
        const openedAt = performance.now();
        let startup = null, downloaded = null, priorQuality = null, displayedFps = null, maxRate = 0;
        let previousNetworkSample = null, networkRate = null;
        const displayedFrames = [], frameIntervals = [];
        let lastDrawAmbient = 0;

        function scheduleSave() {
            clearTimeout(saveTimer);
            saveTimer = setTimeout(save, 250);
        }
        function save() {
            clearTimeout(saveTimer);
            saveTimer = null;
            preferences.initialized = true;
            actions.save(preferences);
        }
        function announce(message) {
            notice.textContent = message;
            notice.hidden = false;
            clearTimeout(noticeTimer);
            noticeTimer = setTimeout(() => { notice.hidden = true; }, 850);
        }
        function reveal() {
            frame.classList.remove('vp-idle');
            clearTimeout(hideTimer);
            if (video.paused || video.ended || panelType || dragging || reduced) return;
            hideTimer = setTimeout(() => {
                if (!video.paused && !panelType && !dragging && !controls.contains(frame.querySelector(':focus-visible')))
                    frame.classList.add('vp-idle');
            }, 2100);
        }
        function setVolume(value) {
            video.volume = clamp(value, 0, 1);
            video.muted = video.volume === 0;
        }
        function setRate(value) {
            if (!Number.isFinite(value)) return;
            video.playbackRate = clamp(value, .25, 3);
            announce(decimal(video.playbackRate) + '×');
        }
        function setTime(value, signal) {
            if (!valid(video.duration) || !Number.isFinite(value)) return;
            const target = clamp(value, 0, video.duration);
            if (Math.abs(target - video.currentTime) > .0001) video.currentTime = target;
            if (signal) announce(signal);
        }
        function skip(delta) { setTime(video.currentTime + delta, (delta > 0 ? '+' : '−') + Math.abs(delta) + ' s'); }
        function describeTime() {
            const duration = valid(video.duration) ? video.duration : 0;
            const time = dragging ? Number(seek.value) : video.currentTime;
            current.textContent = formatMediaTime(time);
            total.textContent = preferences.remaining ? '−' + formatMediaTime(duration - time) : formatMediaTime(duration);
            seek.max = String(duration);
            seek.disabled = !duration;
            if (!dragging) seek.value = String(time);
            seek.setAttribute('aria-valuetext', asText(time) + ' de ' + asText(duration));
            played.style.width = duration ? clamp(time / duration * 100, 0, 100) + '%' : '0%';
            const ranges = duration ? bufferedRanges(video) : [];
            const signature = duration + ':' + ranges.map(pair => pair.join('-')).join('/');
            if (signature === storedRange) return;
            storedRange = signature;
            buffer.replaceChildren(...ranges.map(([start, end]) => {
                const part = document.createElement('i');
                part.style.left = clamp(start / duration * 100, 0, 100) + '%';
                part.style.width = clamp((end - start) / duration * 100, 0, 100) + '%';
                return part;
            }));
        }
        function updateButtons() {
            play.innerHTML = icon(video.paused ? 'play_arrow' : 'pause', 22);
            mute.innerHTML = icon(video.muted || !video.volume ? 'volume_off' : 'volume_up', 19);
            volume.value = video.muted ? '0' : String(video.volume);
        }
        function showWait(reason, count) {
            if (waiting?.reason === reason) return;
            endWait();
            waiting = { reason, at: performance.now(), count };
            spinner.setAttribute('aria-label', reason);
            spinnerTimer = setTimeout(() => { if (waiting) spinner.hidden = false; }, 200);
        }
        function endWait() {
            clearTimeout(spinnerTimer);
            spinner.hidden = true;
            if (waiting?.count) {
                const elapsed = performance.now() - waiting.at;
                if (elapsed >= 200) {
                    bufferCount++;
                    lastBuffer = elapsed;
                    bufferTotal += elapsed;
                }
            }
            waiting = null;
        }
        function readFrame(_, result) {
            if (Number.isFinite(result.mediaTime)) {
                const previous = lastFrame;
                lastFrame = { time: result.mediaTime, count: result.presentedFrames };
                if (!video.seeking && previous && lastFrame.time > previous.time && lastFrame.count > previous.count) {
                    const interval = (lastFrame.time - previous.time) / (lastFrame.count - previous.count);
                    if (interval > .004 && interval < .2) {
                        frameIntervals.push(interval);
                        if (frameIntervals.length > 60) frameIntervals.shift();
                    }
                }
                if (!displayedFrames.includes(result.mediaTime)) {
                    displayedFrames.push(result.mediaTime);
                    if (displayedFrames.length > 140) displayedFrames.shift();
                }
                if (stepTarget !== null && !video.seeking) {
                    frameMark = result.mediaTime;
                    stepTarget = null;
                }
            }
            frameHandle = video.requestVideoFrameCallback(readFrame);
        }
        function stepFrame(direction) {
            if (!video.paused || video.seeking || !valid(video.duration)) return;
            if (!frameTimes && metadata?.video?.frameTimes) frameTimes = metadata.video.frameTimes();
            const lastPresented = lastFrame?.time;
            const tolerance = metadata?.video?.fps ? 1 / metadata.video.fps : .05;
            const currentFrame = frameMark ?? (Math.abs(lastPresented - video.currentTime) <= tolerance
                ? lastPresented : video.currentTime);
            let target = null;
            if (frameTimes?.length) {
                let lo = 0, hi = frameTimes.length;
                while (lo < hi) {
                    const mid = (lo + hi) >>> 1;
                    if (frameTimes[mid] <= currentFrame + .0005) lo = mid + 1;
                    else hi = mid;
                }
                const index = direction > 0 ? lo : lo - (Math.abs((frameTimes[lo - 1] ?? -1) - currentFrame) < .0005 ? 2 : 1);
                if (index >= 0 && index < frameTimes.length) target = frameTimes[index];
            } else {
                const samples = displayedFrames.filter(time => direction > 0 ? time > currentFrame + .001 : time < currentFrame - .001);
                const interval = metadata?.video?.fps ? 1 / metadata.video.fps :
                    frameIntervals.length ? [...frameIntervals].sort((a, b) => a - b)[Math.floor(frameIntervals.length / 2)] : null;
                const nearest = samples.length ? (direction > 0 ? Math.min(...samples) : Math.max(...samples)) : null;
                if (nearest !== null && (!interval || Math.abs(nearest - currentFrame) < interval * 1.6)) target = nearest;
                else if (interval) target = currentFrame + direction * interval;
                else { announce('Taxa de quadros indisponível'); return; }
            }
            if (target === null || target < 0 || target >= video.duration) return;
            frameMark = target;
            stepTarget = target;
            setTime(target + .00025, direction > 0 ? 'Quadro seguinte' : 'Quadro anterior');
        }
        function chapters() {
            const native = Array.from(video.textTracks).find(track => track.kind === 'chapters' && track.cues?.length);
            return native ? Array.from(native.cues, cue => ({ start: cue.startTime, title: cue.text })) : metadata?.chapters || [];
        }
        function updateChapters() {
            const markers = controls.querySelector('.vp-chapters');
            const duration = video.duration;
            markers.replaceChildren(...chapters().filter(chapter => chapter.start > 0 && chapter.start < duration).map(chapter => {
                const element = document.createElement('i');
                element.style.left = chapter.start / duration * 100 + '%';
                return element;
            }));
        }
        function inspect(force = false) {
            if (!details.cacheUrl || !details.streamBytes || informationSeen && !force) return;
            inspectAbort?.abort();
            const request = inspectAbort = new AbortController();
            window.VideoInspection?.inspect(details.cacheUrl, details.streamBytes, request.signal).then(value => {
                if (request.signal.aborted || !value) return;
                metadata = value;
                informationSeen = true;
                if (video.dataset.quality === 'full') original = { width: video.videoWidth, height: video.videoHeight, fps: value.video?.fps };
                updateChapters();
            }).catch(() => {});
        }
        function refreshCaptionStyle() {
            video.style.setProperty('--vp-caption-size', preferences.captionSize + '%');
            video.style.setProperty('--vp-caption-color', preferences.captionColor);
            video.style.setProperty('--vp-caption-bg', 'rgb(0 0 0 / ' + preferences.captionBackground + '%)');
        }
        function textTracks() {
            return Array.from(video.textTracks).filter(track => track.kind === 'captions' || track.kind === 'subtitles');
        }
        function selectCaption(index) {
            const tracks = textTracks();
            tracks.forEach((track, i) => { track.mode = index === i ? 'showing' : 'disabled'; });
            if (index < 0 || tracks[index]) preferences.captions = index >= 0;
            captions.setAttribute('aria-pressed', String(tracks.some(track => track.mode === 'showing')));
            scheduleSave();
        }
        function toggleCaptions() {
            const tracks = textTracks();
            if (!tracks.length) { openPanel('captions', captions); return; }
            selectCaption(tracks.some(track => track.mode === 'showing') ? -1 : 0);
        }
        function closePanel(restoreFocus = true) {
            panel.hidden = true;
            panelType = '';
            if (restoreFocus && panelTrigger?.isConnected) panelTrigger.focus({ preventScroll: true });
            panelTrigger = null;
            reveal();
        }
        function row(action, label, value = '', checked = false) {
            return '<button type="button" data-action="' + esc(action) + '"' + (checked ? ' aria-current="true"' : '') +
                '><span>' + esc(label) + '</span>' + (value ? '<small>' + esc(value) + '</small>' : '') + '</button>';
        }
        function option(action, label, currentValue, choices) {
            return '<label><span>' + label + '</span><select data-setting="' + action + '">' +
                choices.map(([value, name]) => '<option value="' + value + '"' + (String(currentValue) === String(value) ? ' selected' : '') +
                    '>' + name + '</option>').join('') + '</select></label>';
        }
        function toggleOption(action, label, value) {
            return '<label><span>' + label + '</span><input type="checkbox" data-setting="' + action + '"' + (value ? ' checked' : '') + '></label>';
        }
        function informationRows() {
            const videoTrack = metadata?.video;
            return [
                ['Nome', details.name], ['Tamanho do original', details.originalBytes ? actions.bytes(details.originalBytes) : ''],
                ['Duração', asText(video.duration)], ['Resolução nesta qualidade', video.videoWidth ? video.videoWidth + ' × ' + video.videoHeight : ''],
                ['Quadros por segundo', videoTrack?.fps ? decimal(videoTrack.fps) : ''], ['Vídeo', videoTrack?.codec],
                ['Áudio', metadata?.audio?.codec], ['Contêiner', metadata?.container],
                ['Bitrate total estimado', video.duration && details.streamBytes ? decimal(details.streamBytes * 8 / video.duration / 1000) + ' kbit/s' : ''],
                ['Enviado em', details.uploadedAt && !Number.isNaN(Date.parse(details.uploadedAt)) ? new Date(details.uploadedAt).toLocaleString('pt-BR') : '']
            ].filter(([, value]) => value);
        }
        function rowsToHtml(rows) {
            return rows.map(([label, value]) => '<dt>' + esc(label) + '</dt><dd>' + esc(String(value)) + '</dd>').join('');
        }
        function openPanel(name, trigger = panelTrigger) {
            if (name === 'information') inspect();
            panelType = name;
            panelTrigger = trigger;
            const speed = decimal(video.playbackRate) + '×';
            let title, body;
            switch (name) {
            case 'settings':
                title = 'Configurações';
                body = row('speed', 'Velocidade', speed) +
                    (details.qualities?.length > 1 ? row('quality', 'Qualidade', video.dataset.quality === '480p' ? '480p' : 'Original') : '') +
                    row('captions', 'Legendas') + (video.audioTracks?.length > 1 ? row('audio', 'Faixa de áudio') : '') +
                    (chapters().length ? row('chapters', 'Capítulos') : '') +
                    toggleOption('autoNext', 'Reproduzir o próximo vídeo', preferences.autoNext) +
                    toggleOption('repeat', 'Repetir este vídeo', preferences.repeat) +
                    toggleOption('ambient', 'Iluminação ambiente', preferences.ambient) +
                    toggleOption('resume', 'Continuar de onde parei', preferences.resume) +
                    row('stats', 'Estatísticas para nerds') + row('help', 'Atalhos');
                break;
            case 'speed':
                title = 'Velocidade';
                body = rates.map(rate => row('rate:' + rate, rate === 1 ? 'Normal' : decimal(rate) + '×', video.playbackRate === rate ? '✓' : '', video.playbackRate === rate)).join('') +
                    '<label><span>Personalizada</span><input type="number" min=".25" max="3" step=".05" data-setting="rate" value="' + video.playbackRate + '"></label>';
                break;
            case 'quality':
                title = 'Qualidade';
                body = details.qualities.map(quality => row('quality:' + quality, quality === '480p' ? '480p' : 'Original',
                    video.dataset.quality === quality ? '✓' : '', video.dataset.quality === quality)).join('');
                break;
            case 'captions':
                title = 'Legendas';
                body = row('cc:-1', 'Desativadas', !textTracks().some(track => track.mode === 'showing') ? '✓' : '') +
                    textTracks().map((track, index) => row('cc:' + index, track.label || track.language || 'Faixa ' + (index + 1),
                        track.mode === 'showing' ? '✓' : '', track.mode === 'showing')).join('') +
                    row('subtitleFile', 'Carregar WebVTT ou SRT') +
                    option('captionSize', 'Tamanho', preferences.captionSize, [75, 100, 125, 150, 200].map(size => [size, size + '%'])) +
                    option('captionColor', 'Cor', preferences.captionColor, [['white', 'Branco'], ['yellow', 'Amarelo'], ['cyan', 'Ciano']]) +
                    '<label><span>Opacidade do fundo</span><input aria-label="Opacidade do fundo" type="range" min="0" max="100" step="5" value="' +
                    preferences.captionBackground + '" data-setting="captionBackground"></label>';
                break;
            case 'audio':
                title = 'Faixa de áudio';
                body = Array.from(video.audioTracks || [], (track, index) =>
                    row('audio:' + index, track.label || track.language || 'Faixa ' + (index + 1), track.enabled ? '✓' : '', track.enabled)).join('');
                break;
            case 'chapters':
                title = 'Capítulos';
                body = chapters().map(chapter => row('seek:' + chapter.start, chapter.title || 'Capítulo', asText(chapter.start))).join('');
                break;
            case 'information':
                title = 'Informações do vídeo';
                body = '<dl>' + rowsToHtml(informationRows()) + '</dl>' + row('copy', 'Copiar informações');
                break;
            case 'help':
                title = 'Atalhos do reprodutor';
                body = '<dl>' + rowsToHtml([
                    ['Espaço / K', 'Reproduzir ou pausar'], ['J / L', 'Voltar / avançar 10 s'], ['← / →', 'Voltar / avançar 5 s'],
                    ['↑ / ↓ / M', 'Volume / silêncio'], ['F / I / C', 'Tela cheia / mini player / legendas'],
                    ['. / ,', 'Quadro seguinte / anterior ao pausar'], ['Home / End', 'Ir ao início / fim'],
                    ['0–9', 'Ir de 0 a 90%'], ['< / >', 'Reduzir / aumentar velocidade'],
                    ['Esc / ?', 'Fechar menu ou tela cheia / ajuda']
                ]) + '</dl>';
                break;
            default:
                title = 'Vídeo';
                body = row('play', video.paused ? 'Reproduzir' : 'Pausar') + row('restart', 'Reiniciar') +
                    row('repeat', 'Repetir', video.loop ? '✓' : '') + row('copy', 'Copiar informações') +
                    (video.readyState >= 2 && actions.capture ? row('capture', 'Salvar quadro atual') : '') +
                    (!pip.hidden ? row('pip', 'Picture-in-Picture') : '') + row('mini', 'Mini player') +
                    row('stats', 'Estatísticas para nerds') + row('information', 'Informações do vídeo') + row('help', 'Atalhos');
            }
            panel.innerHTML = '<header><strong>' + title + '</strong>' + button('closePanel', 'close', 'Fechar menu') + '</header>' + body;
            panel.hidden = false;
            (panel.querySelector('button:not([data-action="closePanel"])') || panel.querySelector('button'))?.focus({ preventScroll: true });
            reveal();
        }
        async function fullscreen() {
            if (document.fullscreenElement === frame) { await document.exitFullscreen(); return; }
            if (hostFull) { hostFull = false; frame.classList.remove('is-fullscreen'); actions.fullscreen(false); return; }
            if (reduced) toggleMini();
            try { await frame.requestFullscreen(); }
            catch (_) { hostFull = true; frame.classList.add('is-fullscreen'); actions.fullscreen(true); }
        }
        function toggleMini() {
            if (document.fullscreenElement === frame || hostFull) return;
            reduced = !reduced;
            frame.classList.toggle('vp-mini', reduced);
            const control = controls.querySelector('[data-action="mini"]');
            control.title = reduced ? 'Voltar ao player' : 'Mini player';
            control.setAttribute('aria-label', control.title);
            control.setAttribute('aria-pressed', String(reduced));
            actions.mini(reduced);
            reveal();
        }
        function drawAmbient() {
            ambient.hidden = !preferences.ambient || reduced;
            if (ambient.hidden || !video.videoWidth || performance.now() - lastDrawAmbient < 275) return;
            lastDrawAmbient = performance.now();
            ambient.getContext('2d')?.drawImage(video, 0, 0, ambient.width, ambient.height);
        }
        async function captureFrame() {
            if (video.readyState < 2) return;
            if (video.videoWidth * video.videoHeight > 36000000) { announce('Quadro grande demais para capturar'); return; }
            const canvas = document.createElement('canvas');
            canvas.width = video.videoWidth;
            canvas.height = video.videoHeight;
            if (!canvas.width || !canvas.height) return;
            canvas.getContext('2d').drawImage(video, 0, 0);
            canvas.toBlob(blob => {
                canvas.width = canvas.height = 0;
                if (!blob || lifetime.signal.aborted) return;
                if (snapshotUrl) URL.revokeObjectURL(snapshotUrl);
                clearTimeout(snapshotTimer);
                snapshotUrl = URL.createObjectURL(blob);
                snapshotTimer = setTimeout(() => { URL.revokeObjectURL(snapshotUrl); snapshotUrl = ''; }, 15000);
                actions.capture(snapshotUrl);
            }, 'image/png');
        }
        function captureReady(url) {
            if (!snapshotUrl || url !== snapshotUrl) return;
            const download = document.createElement('a');
            download.href = url;
            download.download = 'Quadro.png';
            download.click();
        }
        function selectQuality(quality) {
            if (requestQuality || video.dataset.quality === quality || !details.qualities?.includes(quality)) return;
            requestQuality = true;
            showWait('Trocando qualidade', false);
            actions.quality(quality);
        }
        function qualityFinished(data = {}) {
            requestQuality = false;
            if (!data.cacheUrl) { endWait(); return; }
            Object.assign(details, data);
            for (const url of thumbnails.values()) URL.revokeObjectURL(url);
            thumbnails.clear();
            resetThumbnail();
            informationSeen = false;
            metadata = null;
            frameTimes = null;
            frameMark = lastFrame = stepTarget = null;
            displayedFrames.length = frameIntervals.length = 0;
            downloaded = previousNetworkSample = networkRate = priorQuality = displayedFps = null;
            maxRate = 0;
            updateChapters();
            inspectAbort?.abort();
        }

        function toggleStats() {
            stats.hidden = !stats.hidden;
            clearInterval(statsTimer);
            if (!stats.hidden) {
                priorQuality = null;
                inspect();
                updateStats();
                statsTimer = setInterval(updateStats, 1000);
            }
        }
        function statsReady(values) {
            if (stats.hidden) return;
            downloaded = values;
            if (values.source) {
                const { bytes, elapsedSeconds } = values.source;
                if (previousNetworkSample && elapsedSeconds > previousNetworkSample.elapsedSeconds &&
                    bytes >= previousNetworkSample.bytes) {
                    networkRate = (bytes - previousNetworkSample.bytes) / (elapsedSeconds - previousNetworkSample.elapsedSeconds);
                    maxRate = Math.max(maxRate, networkRate);
                }
                previousNetworkSample = { bytes, elapsedSeconds };
            }
        }
        function updateStats() {
            if (requestQuality || !errorPanel.hidden) return;
            actions.stats(video.dataset.quality);
            const quality = video.getVideoPlaybackQuality?.();
            const now = performance.now();
            if (priorQuality && quality && now > priorQuality.at && quality.totalVideoFrames >= priorQuality.frames)
                displayedFps = Math.max(0, (quality.totalVideoFrames - priorQuality.frames -
                    (quality.droppedVideoFrames - priorQuality.dropped)) * 1000 / (now - priorQuality.at));
            if (quality) priorQuality = { at: now, frames: quality.totalVideoFrames, dropped: quality.droppedVideoFrames };
            const ranges = bufferedRanges(video);
            const forward = ranges.find(([start, end]) => start <= video.currentTime + .03 && end >= video.currentTime);
            const remaining = forward ? Math.max(0, forward[1] - video.currentTime) : 0;
            const cdn = downloaded?.source;
            const observedBytes = (cdn?.bytes || 0) + (downloaded?.reusedBytes || 0);
            const seconds = value => decimal(value) + ' s';
            const ms = value => value == null ? 'Indisponível' : decimal(value) + ' ms';
            const throughput = value => value == null ? 'Indisponível' : actions.bytes(Math.round(value)) + '/s';
            const resolution = (width, height) => width ? width + ' × ' + height : 'Indisponível';
            const rows = [
                ['Qualidade e resolução', (video.dataset.quality === '480p' ? '480p' : 'Original') + ' · ' + resolution(video.videoWidth, video.videoHeight)],
                ['Resolução original', resolution(original?.width, original?.height)],
                ['Área de apresentação', resolution(video.clientWidth, video.clientHeight)],
                ['FPS da faixa / apresentados (estimativa)', (metadata?.video?.fps ? decimal(metadata.video.fps) : 'Indisponível') +
                    ' / ' + (displayedFps == null ? 'Indisponível' : decimal(displayedFps))],
                ['Quadros processados / descartados', quality ? quality.totalVideoFrames + ' / ' + quality.droppedVideoFrames +
                    ' (' + decimal(quality.totalVideoFrames ? quality.droppedVideoFrames / quality.totalVideoFrames * 100 : 0) + '%)' : 'Indisponível'],
                ['Codecs', (metadata?.video?.codec || 'Indisponível') + ' / ' + (metadata?.audio?.codec || 'Indisponível')],
                ['Bitrate vídeo / áudio', [metadata?.video?.bitrate, metadata?.audio?.bitrate].map(value =>
                    value ? decimal(value / 1000) + ' kbit/s' : 'Indisponível').join(' / ')],
                ['Bitrate total estimado', video.duration && details.streamBytes ? decimal(details.streamBytes * 8 / video.duration / 1000) + ' kbit/s' : 'Indisponível'],
                ['Posição / duração / velocidade', asText(video.currentTime) + ' / ' + asText(video.duration) + ' · ' + decimal(video.playbackRate) + '×'],
                ['Download atual / média / pico observado', [networkRate, cdn?.elapsedSeconds ? cdn.bytes / cdn.elapsedSeconds : null,
                    previousNetworkSample ? maxRate : null].map(throughput).join(' · ')],
                ['Recebido da CDN / reutilizado da RAM', (cdn ? actions.bytes(cdn.bytes) : 'Indisponível') + ' / ' +
                    (downloaded ? actions.bytes(downloaded.reusedBytes) : 'Indisponível')],
                ['Origem em uso / reutilização observada', downloaded ?
                    (cdn?.active ? 'CDN e cache em RAM' : 'Cache em RAM') + ' · ' +
                        (observedBytes ? decimal(downloaded.reusedBytes / observedBytes * 100) + '%' : 'Indisponível') : 'Indisponível'],
                ['CDN ativas / concluídas / falhas / retentativas', cdn ? [cdn.active, cdn.completed, cdn.failed, cdn.retries].join(' / ') : 'Indisponível'],
                ['CDN cabeçalho / primeiro byte (médias)', ms(cdn?.headersMs) + ' / ' + ms(cdn?.firstByteMs)],
                ['Cache do app em RAM / disco', downloaded ? actions.bytes(downloaded.ramBytes) + ' / ' + actions.bytes(downloaded.diskBytes) : 'Indisponível'],
                ['Buffer contínuo / total no decodificador', seconds(remaining) + ' / ' + seconds(ranges.reduce((sum, [a,b]) => sum + b - a, 0))],
                ['Intervalos do decodificador', ranges.length ? ranges.slice(0, 8).map(([a,b]) => asText(a) + '–' + asText(b)).join(', ') : 'Nenhum'],
                ['Estado', waiting?.reason || (video.ended ? 'Encerrado' : video.paused ? 'Pausado' : 'Reproduzindo')],
                ['Interrupções / tempo total / última', bufferCount + ' / ' + ms(bufferTotal) + ' / ' + ms(lastBuffer)],
                ['Abertura / último seek', ms(startup) + ' / ' + ms(lastSeek)]
            ];
            stats.querySelector('dl').innerHTML = rowsToHtml(rows);
        }

        function resetThumbnail() {
            clearTimeout(thumbTimeout);
            requestedThumbnail = null;
            thumbUrl = '';
            thumbnailVideo.removeAttribute('src');
            thumbnailVideo.load();
        }
        function seekThumbnail() {
            if (requestedThumbnail && thumbnailVideo.readyState >= 1) {
                if (Math.abs(thumbnailVideo.currentTime - requestedThumbnail.time) > .01)
                    thumbnailVideo.currentTime = requestedThumbnail.time;
                else if (thumbnailVideo.readyState >= 2) finishThumbnail();
            }
        }
        function finishThumbnail() {
            const task = requestedThumbnail;
            if (!task || thumbnailVideo.seeking || thumbnailVideo.readyState < 2 || !thumbnailVideo.videoWidth ||
                Math.abs(thumbnailVideo.currentTime - task.time) > .25) return;
            clearTimeout(thumbTimeout);
            thumbnailCanvas.width = 160;
            thumbnailCanvas.height = Math.max(1, Math.round(160 * thumbnailVideo.videoHeight / thumbnailVideo.videoWidth));
            thumbnailCanvas.getContext('2d').drawImage(thumbnailVideo, 0, 0, thumbnailCanvas.width, thumbnailCanvas.height);
            requestedThumbnail = null;
            thumbnailCanvas.toBlob(blob => {
                if (!blob || lifetime.signal.aborted || task.source !== details.cacheUrl) return;
                if (thumbnails.has(task.key)) URL.revokeObjectURL(thumbnails.get(task.key));
                const url = URL.createObjectURL(blob);
                thumbnails.set(task.key, url);
                if (thumbnails.size > 36) {
                    const first = thumbnails.keys().next().value;
                    URL.revokeObjectURL(thumbnails.get(first));
                    thumbnails.delete(first);
                }
                if (!hover.hidden && hover.dataset.key === task.key) {
                    timelineImage.src = url;
                    timelineImage.hidden = false;
                }
            }, 'image/jpeg', .78);
        }
        function hoverTime(value, x) {
            hover.hidden = false;
            const duration = video.duration || 0;
            const bucket = Math.max(.5, Math.min(4, duration / 300));
            const time = clamp(Math.round(value / bucket) * bucket, 0, Math.max(0, duration - .01));
            const key = time.toFixed(3);
            const chapter = chapters().filter(item => item.start <= value).at(-1);
            hover.querySelector('span').textContent = asText(value) + (chapter?.title ? ' · ' + chapter.title : '');
            hover.style.left = clamp(x, Math.min(88, timeline.clientWidth / 2), Math.max(timeline.clientWidth - 88, timeline.clientWidth / 2)) + 'px';
            if (hover.dataset.key === key) return;
            hover.dataset.key = key;
            clearTimeout(hoverTimer);
            timelineImage.hidden = !thumbnails.has(key);
            if (!timelineImage.hidden) { timelineImage.src = thumbnails.get(key); return; }
            if (!details.cacheUrl || requestQuality || (!video.paused && video.readyState < 3)) return;
            hoverTimer = setTimeout(() => {
                requestedThumbnail = { time, key, source: details.cacheUrl };
                clearTimeout(thumbTimeout);
                thumbTimeout = setTimeout(resetThumbnail, 1000);
                if (thumbUrl !== details.cacheUrl) {
                    thumbUrl = details.cacheUrl;
                    thumbnailVideo.src = thumbUrl;
                } else seekThumbnail();
            }, 170);
        }
        function clearHover() {
            hover.hidden = true;
            delete hover.dataset.key;
            clearTimeout(hoverTimer);
            resetThumbnail();
        }
        async function execute(action, trigger) {
            const [type, value] = action.split(':');
            if (value !== undefined) {
                if (type === 'rate') setRate(Number(value));
                if (type === 'quality') selectQuality(value);
                if (type === 'cc') selectCaption(Number(value));
                if (type === 'seek') setTime(Number(value));
                if (type === 'audio') Array.from(video.audioTracks).forEach((track, index) => { track.enabled = index === Number(value); });
                closePanel();
                return;
            }
            if (['settings', 'quality', 'speed', 'captions', 'audio', 'chapters', 'information', 'help'].includes(type)) {
                if (panelType === type) closePanel(); else openPanel(type, panelTrigger || trigger);
                return;
            }
            if (panelType) closePanel();
            switch (type) {
            case 'play': play.click(); break;
            case 'restart': setTime(0); video.play().catch(() => {}); break;
            case 'back': skip(-10); break;
            case 'forward': skip(10); break;
            case 'time': preferences.remaining = !preferences.remaining; describeTime(); scheduleSave(); break;
            case 'cc': toggleCaptions(); break;
            case 'mini': toggleMini(); break;
            case 'fullscreen': await fullscreen(); break;
            case 'pip':
                if (document.pictureInPictureElement === video) await document.exitPictureInPicture();
                else await video.requestPictureInPicture();
                break;
            case 'repeat': preferences.repeat = video.loop = !video.loop; scheduleSave(); announce(video.loop ? 'Repetição ativada' : 'Repetição desativada'); break;
            case 'stats': toggleStats(); break;
            case 'subtitleFile': subtitles.click(); break;
            case 'capture': await captureFrame(); break;
            case 'copy':
                await navigator.clipboard.writeText(informationRows().map(([a,b]) => a + ': ' + b).join('\n'));
                announce('Informações copiadas');
                break;
            case 'retry': actions.retry(); break;
            case 'download': actions.download(); break;
            case 'external': actions.external(); break;
            }
        }

        function onKey(event) {
            const target = event.target;
            if (!frame.isConnected || event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey ||
                (reduced && !frame.contains(target)) ||
                (actions.blocksShortcuts?.() && !frame.contains(target))) return;
            const key = event.key.toLowerCase();
            if (panelType && key === 'escape') { event.preventDefault(); event.stopImmediatePropagation(); closePanel(); return; }
            if (target.closest?.('input,textarea,select,[contenteditable=true]')) return;
            if (panelType && panel.contains(target) && ['arrowdown', 'arrowup', 'home', 'end'].includes(key)) {
                const buttons = Array.from(panel.querySelectorAll('button'));
                let index = buttons.indexOf(document.activeElement);
                index = key === 'home' ? 0 : key === 'end' ? buttons.length - 1 :
                    (index + (key === 'arrowdown' ? 1 : -1) + buttons.length) % buttons.length;
                buttons[index]?.focus();
                event.preventDefault(); event.stopImmediatePropagation(); return;
            }
            if (key === ' ' && target.closest?.('button, [role=button], media-play-button, media-mute-button')) return;
            const direct = { ' ': 'play', k: 'play', j: 'back', l: 'forward', m: 'mute', f: 'fullscreen', c: 'cc', i: 'mini', '?': 'help' }[key];
            const handled = direct || ['arrowleft','arrowright','arrowup','arrowdown','home','end','.',',','<','>','contextmenu'].includes(key) ||
                (key === 'f10' && event.shiftKey) || /^[0-9]$/.test(key) || (key === 'escape' && (hostFull || document.fullscreenElement === frame));
            if (!handled) return;
            event.preventDefault(); event.stopImmediatePropagation(); reveal();
            if (direct === 'mute') { mute.click(); return; }
            if (direct) { execute(direct, frame).catch(() => announce('A ação está indisponível')); return; }
            if (/^[0-9]$/.test(key)) { setTime(video.duration * Number(key) / 10); return; }
            switch (key) {
            case 'arrowleft': skip(-5); break;
            case 'arrowright': skip(5); break;
            case 'arrowup': setVolume(video.volume + .05); break;
            case 'arrowdown': setVolume(video.volume - .05); break;
            case 'home': setTime(0); break;
            case 'end': setTime(video.duration); break;
            case '.': stepFrame(1); break;
            case ',': stepFrame(-1); break;
            case '<': setRate(rates.findLast(rate => rate < video.playbackRate) || rates[0]); break;
            case '>': setRate(rates.find(rate => rate > video.playbackRate) || rates.at(-1)); break;
            case 'escape': fullscreen().catch(() => {}); break;
            default: openPanel('context', frame);
            }
        }
        function onError(message, title = 'Falha na reprodução') {
            endWait();
            clearHover();
            video.pause();
            errorPanel.querySelector('strong').textContent = title;
            errorPanel.querySelector('p').textContent = message;
            errorPanel.hidden = false;
            reveal();
        }
        function clearError() { errorPanel.hidden = true; }

        video.volume = preferences.volume;
        video.muted = preferences.muted;
        video.defaultPlaybackRate = preferences.rate;
        video.playbackRate = preferences.rate;
        video.loop = preferences.repeat;
        video.preservesPitch = true;
        refreshCaptionStyle();
        if (video.requestVideoFrameCallback) frameHandle = video.requestVideoFrameCallback(readFrame);
        listen(window, 'keydown', onKey, { capture: true });
        listen(frame, 'click', event => {
            const action = event.target.closest('[data-action]');
            if (action) execute(action.dataset.action, action).catch(() => announce('A ação está indisponível'));
            else if (!panel.contains(event.target) && panelType) closePanel(false);
            event.stopPropagation();
        });
        listen(frame, 'contextmenu', event => { event.preventDefault(); event.stopPropagation(); openPanel('context', frame); });
        listen(frame, 'pointermove', reveal, { passive: true });
        listen(frame, 'focusin', reveal);
        listen(frame, 'pointerleave', clearHover);
        listen(video, 'click', () => { clearTimeout(clickTimer); clickTimer = setTimeout(() => play.click(), 220); });
        listen(video, 'dblclick', () => { clearTimeout(clickTimer); fullscreen().catch(() => {}); });
        listen(document, 'fullscreenchange', () => {
            const full = document.fullscreenElement === frame;
            frame.classList.toggle('is-fullscreen', full || hostFull);
            actions.fullscreen(full || hostFull);
            reveal();
        });
        listen(seek, 'input', () => {
            dragging = true;
            describeTime();
            const time = Number(seek.value);
            hoverTime(time, time * timeline.clientWidth / video.duration);
        });
        listen(seek, 'change', () => { dragging = false; setTime(Number(seek.value)); describeTime(); reveal(); });
        listen(seek, 'pointercancel', () => { dragging = false; clearHover(); describeTime(); });
        listen(timeline, 'pointermove', event => {
            if (dragging || !valid(video.duration) || !video.duration) return;
            const rect = timeline.getBoundingClientRect();
            const x = clamp(event.clientX - rect.left, 0, rect.width);
            hoverTime(x / rect.width * video.duration, x);
        });
        listen(timeline, 'pointerleave', () => { if (!dragging) clearHover(); });
        listen(volume, 'input', () => setVolume(Number(volume.value)));
        listen(video, 'volumechange', () => {
            if (preferences.volume !== video.volume || preferences.muted !== video.muted) {
                preferences.volume = video.volume; preferences.muted = video.muted; scheduleSave();
            }
            updateButtons();
        });
        listen(video, 'ratechange', () => {
            if (preferences.rate !== video.playbackRate) {
                preferences.rate = video.playbackRate; video.defaultPlaybackRate = video.playbackRate; scheduleSave();
            }
        });
        listen(video, 'loadstart', () => { if (!video.paused || requestQuality) showWait('Carregando vídeo', false); });
        listen(video, 'play', () => { if (video.readyState < 3) showWait('Carregando vídeo', false); updateButtons(); reveal(); });
        listen(video, 'playing', () => { endWait(); startup ??= performance.now() - openedAt; updateButtons(); reveal(); inspect(); });
        listen(video, 'pause', () => { if (!requestQuality) endWait(); updateButtons(); reveal(); });
        listen(video, 'waiting', () => { if (!video.paused && !video.ended && !waiting) showWait('Aguardando dados', startup !== null && !video.seeking); });
        listen(video, 'stalled', () => { if (!waiting && !video.paused && video.readyState < 3) showWait('Aguardando rede', startup !== null && !video.seeking); });
        listen(video, 'seeking', () => {
            seekStarted = performance.now();
            if (stepTarget === null) frameMark = null;
            if (!requestQuality) showWait('Buscando posição', false);
        });
        listen(video, 'seeked', () => {
            lastSeek = performance.now() - seekStarted;
            if (video.paused || video.readyState >= 3) endWait();
            else showWait('Aguardando dados', false);
            describeTime();
        });
        listen(video, 'canplay', () => { if (!requestQuality && !video.seeking) endWait(); inspect(); });
        listen(video, 'loadedmetadata', () => {
            if (video.dataset.quality === 'full' && video.videoWidth) original = { width: video.videoWidth, height: video.videoHeight };
            video.playbackRate = preferences.rate;
            if (preferences.captions && textTracks().length && !textTracks().some(track => track.mode === 'showing'))
                textTracks()[0].mode = 'showing';
            describeTime();
            updateChapters();
            inspect();
        });
        listen(video, 'progress', describeTime);
        listen(video, 'durationchange', describeTime);
        listen(video, 'timeupdate', () => { describeTime(); drawAmbient(); });
        listen(video, 'ended', () => { endWait(); updateButtons(); reveal(); if (preferences.autoNext) actions.next(); });
        listen(video.textTracks, 'addtrack', event => {
            if (event.track.kind === 'chapters') event.track.mode = 'hidden';
            if (preferences.captions && !textTracks().some(track => track.mode === 'showing') && textTracks().includes(event.track))
                event.track.mode = 'showing';
            updateChapters();
        });
        listen(video.textTracks, 'change', () =>
            captions.setAttribute('aria-pressed', String(textTracks().some(track => track.mode === 'showing'))));
        listen(panel, 'change', event => {
            const id = event.target.dataset.setting;
            if (!id) return;
            if (id === 'rate') { setRate(Number(event.target.value)); return; }
            preferences[id] = event.target.type === 'checkbox' ? event.target.checked :
                id === 'captionColor' ? event.target.value : Number(event.target.value);
            video.loop = preferences.repeat;
            refreshCaptionStyle();
            drawAmbient();
            scheduleSave();
        });
        listen(subtitles, 'change', async () => {
            const file = subtitles.files?.[0];
            subtitles.value = '';
            if (!file) return;
            if (file.size > 2 * 1024 * 1024) { announce('O arquivo de legenda é grande demais'); return; }
            try {
                const contents = convertSubtitles(await file.text(), file.name);
                if (lifetime.signal.aborted) return;
                subtitleTrack?.remove();
                if (subtitleUrl) URL.revokeObjectURL(subtitleUrl);
                subtitleTrack = document.createElement('track');
                subtitleTrack.kind = 'subtitles';
                subtitleTrack.label = file.name;
                subtitleUrl = URL.createObjectURL(new Blob([contents], { type: 'text/vtt' }));
                subtitleTrack.src = subtitleUrl;
                video.appendChild(subtitleTrack);
                selectCaption(textTracks().indexOf(subtitleTrack.track));
                announce('Legenda carregada');
            } catch (_) { announce('Não foi possível abrir a legenda'); }
        });
        listen(thumbnailVideo, 'loadedmetadata', seekThumbnail);
        listen(thumbnailVideo, 'seeked', finishThumbnail);
        listen(thumbnailVideo, 'loadeddata', finishThumbnail);
        listen(thumbnailVideo, 'error', () => { requestedThumbnail = null; });
        updateButtons();
        describeTime();
        reveal();
        return { host, frame, onError, clearError, qualityFinished, statsReady, captureReady,
            waiting: reason => showWait(reason, false), isMini: () => reduced,
            focus: () => frame.focus({ preventScroll: true }),
            dispose() {
                if (saveTimer) save();
                lifetime.abort();
                inspectAbort?.abort();
                for (const timer of [hideTimer, spinnerTimer, noticeTimer, statsTimer, hoverTimer, thumbTimeout, clickTimer, snapshotTimer])
                    clearTimeout(timer);
                if (frameHandle != null) video.cancelVideoFrameCallback(frameHandle);
                if (document.pictureInPictureElement === video) document.exitPictureInPicture().catch(() => {});
                if (document.fullscreenElement === frame) document.exitFullscreen().catch(() => {});
                if (hostFull) actions.fullscreen(false);
                if (reduced) actions.mini(false);
                resetThumbnail();
                for (const url of thumbnails.values()) URL.revokeObjectURL(url);
                if (subtitleUrl) URL.revokeObjectURL(subtitleUrl);
                if (snapshotUrl) URL.revokeObjectURL(snapshotUrl);
            }
        };
    }
    window.VideoPlayer = { mount, convertSubtitles };
})();
