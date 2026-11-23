// Optional analysis of ISO BMFF tables already present in the authenticated RAM cache.
(() => {
    'use strict';
    const decoder = new TextDecoder();
    const ascii = (view, offset) => String.fromCharCode(...new Uint8Array(view.buffer, view.byteOffset + offset, 4));
    const safe64 = (view, offset) => Number(view.getBigUint64(offset));
    const CODECS = { avc1: 'H.264', avc3: 'H.264', hvc1: 'HEVC', hev1: 'HEVC', vp09: 'VP9', vp08: 'VP8',
        av01: 'AV1', mp4a: 'MPEG-4 áudio', Opus: 'Opus', 'ac-3': 'AC-3', 'ec-3': 'E-AC-3', alac: 'ALAC' };

    function header(view, at, remaining) {
        if (at + 8 > view.byteLength) return null;
        const small = view.getUint32(at);
        const prefix = small === 1 ? 16 : 8;
        if (at + prefix > view.byteLength) return null;
        const size = small === 1 ? safe64(view, at + 8) : small || remaining;
        return Number.isSafeInteger(size) && size >= prefix && size <= remaining
            ? { type: ascii(view, at + 4), at, start: at + prefix, end: at + size, size } : null;
    }
    function boxes(view, start, end) {
        const result = [];
        for (let at = start; at < end;) {
            const box = header(view, at, end - at);
            if (!box) break;
            result.push(box);
            at = box.end;
        }
        return result;
    }
    const child = (view, parent, type) => parent && boxes(view, parent.start, parent.end).find(box => box.type === type);
    function timeScale(view, box) {
        const version = view.getUint8(box.start);
        const off = box.start + (version === 1 ? 20 : 12);
        if (box.end < off + (version === 1 ? 12 : 8)) return {};
        const scale = view.getUint32(off);
        const ticks = version === 1 ? safe64(view, off + 4) : view.getUint32(off + 4);
        return { scale, seconds: scale && ticks / scale };
    }
    function table(view, box, signed = false) {
        if (!box || box.end < box.start + 8) return [];
        const count = view.getUint32(box.start + 4);
        if (count > 300000 || box.start + 8 + count * 8 > box.end) return [];
        return Array.from({ length: count }, (_, index) => {
            const at = box.start + 8 + index * 8;
            return [view.getUint32(at), signed ? view.getInt32(at + 4) : view.getUint32(at + 4)];
        });
    }
    function samplesBitrate(view, sizes, seconds) {
        if (!sizes || !seconds || sizes.end < sizes.start + 12) return null;
        const fixed = view.getUint32(sizes.start + 4);
        const count = view.getUint32(sizes.start + 8);
        if (fixed) return count * fixed * 8 / seconds;
        if (count > 400000 || sizes.start + 12 + count * 4 > sizes.end) return null;
        let bytes = 0;
        for (let index = 0; index < count; index++) bytes += view.getUint32(sizes.start + 12 + index * 4);
        return bytes * 8 / seconds;
    }
    function editOffset(view, track) {
        const edit = child(view, child(view, track, 'edts'), 'elst');
        if (!edit) return 0;
        const wide = view.getUint8(edit.start) === 1;
        if (edit.start + (wide ? 28 : 20) > edit.end || view.getUint32(edit.start + 4) !== 1) return null;
        const at = edit.start + (wide ? 16 : 12);
        if (view.getUint32(at + (wide ? 8 : 4)) !== 0x10000) return null;
        const offset = wide ? Number(view.getBigInt64(at)) : view.getInt32(at);
        return Number.isSafeInteger(offset) && offset >= 0 ? offset : null;
    }
    function chapterList(view, moov) {
        const atom = child(view, child(view, moov, 'udta'), 'chpl');
        if (!atom || atom.start + 9 > atom.end || view.getUint8(atom.start) !== 1) return [];
        const count = view.getUint8(atom.start + 8);
        const chapters = [];
        let at = atom.start + 9;
        for (let index = 0; index < count && at + 9 <= atom.end; index++) {
            const start = safe64(view, at) / 10000000;
            const length = view.getUint8(at + 8);
            at += 9;
            if (at + length > atom.end || !Number.isFinite(start)) return [];
            chapters.push({ start, title: decoder.decode(new Uint8Array(view.buffer, view.byteOffset + at, length)) });
            at += length;
        }
        return chapters;
    }
    function parse(bytes) {
        const view = new DataView(bytes);
        const moov = boxes(view, 0, view.byteLength).find(box => box.type === 'moov');
        if (!moov) return null;
        const info = { container: 'ISO Base Media', chapters: chapterList(view, moov), video: null, audio: null };
        for (const track of boxes(view, moov.start, moov.end).filter(box => box.type === 'trak')) {
            const mdia = child(view, track, 'mdia');
            const handler = child(view, mdia, 'hdlr');
            const mdhd = child(view, mdia, 'mdhd');
            if (!handler || handler.start + 12 > handler.end || !mdhd) continue;
            const kind = { vide: 'video', soun: 'audio' }[ascii(view, handler.start + 8)];
            if (!kind || info[kind]) continue;
            const stbl = child(view, child(view, mdia, 'minf'), 'stbl');
            const desc = child(view, stbl, 'stsd');
            const time = timeScale(view, mdhd);
            const codec = desc && desc.start + 16 <= desc.end ? ascii(view, desc.start + 12) : '';
            const timing = table(view, child(view, stbl, 'stts'));
            const frames = timing.reduce((total, [count]) => total + count, 0);
            const ticks = timing.reduce((total, [count, delta]) => total + count * delta, 0);
            const fps = frames && ticks && time.scale ? frames * time.scale / ticks : null;
            info[kind] = { codec: CODECS[codec] || codec, bitrate: samplesBitrate(view, child(view, stbl, 'stsz'), time.seconds) };
            if (kind !== 'video') continue;
            info.video.fps = fps;
            const composition = child(view, stbl, 'ctts');
            const offsets = table(view, composition, composition && view.getUint8(composition.start) === 1);
            const skip = editOffset(view, track);
            if (frames && frames <= 300000 && time.scale && skip !== null &&
                (!composition || offsets.reduce((total, [count]) => total + count, 0) === frames)) {
                info.video.frameTimes = () => {
                    const values = new Float64Array(frames);
                    let index = 0, decodeTime = 0, offsetIndex = 0, left = offsets[0]?.[0] || 0;
                    for (const [count, delta] of timing) {
                        for (let frame = 0; frame < count; frame++) {
                            while (offsets.length && left <= 0 && offsetIndex + 1 < offsets.length) left = offsets[++offsetIndex][0];
                            values[index++] = Math.max(0, (decodeTime + (offsets[offsetIndex]?.[1] || 0) - skip) / time.scale);
                            decodeTime += delta;
                            left--;
                        }
                    }
                    return values.sort();
                };
            }
        }
        return info;
    }
    async function inspect(url, length, signal) {
        if (!url || length < 16) return null;
        const read = async (start, bytes) => {
            const end = Math.min(length, start + bytes) - 1;
            const response = await fetch(url, { headers: { Range: `bytes=${start}-${end}` }, signal });
            return response.status === 206 ? await response.arrayBuffer() : null;
        };
        const beginning = await read(0, 64);
        if (!beginning) return null;
        const start = new DataView(beginning);
        if (start.getUint32(0) === 0x1a45dfa3) return { container: 'EBML', chapters: [] };
        if (!['ftyp', 'moov', 'wide', 'free', 'mdat'].includes(ascii(start, 4))) return null;
        for (let at = 0, count = 0; at + 8 <= length && count < 64; count++) {
            const bytes = at === 0 ? beginning : await read(at, 16);
            const atom = bytes && header(new DataView(bytes), 0, length - at);
            if (!atom) return null;
            if (atom.type === 'moov') {
                if (atom.size > 4 * 1024 * 1024) return { container: 'ISO Base Media', chapters: [] };
                const output = new Uint8Array(atom.size);
                for (let offset = 0; offset < output.length; offset += 512 * 1024) {
                    const page = await read(at + offset, Math.min(512 * 1024, output.length - offset));
                    if (!page || page.byteLength !== Math.min(512 * 1024, output.length - offset)) return null;
                    output.set(new Uint8Array(page), offset);
                }
                return parse(output.buffer);
            }
            at += atom.size;
        }
        return { container: 'ISO Base Media', chapters: [] };
    }
    window.VideoInspection = { inspect, parse };
})();
