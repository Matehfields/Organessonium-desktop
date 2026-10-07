const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { after, before, test } = require('node:test');
const { chromium } = require('playwright');

// Requires Playwright on NODE_PATH. No app or visible browser window is opened.
const appUrl = pathToFileURL(path.join(__dirname, '..', 'Organessonium', 'Organessonium', 'web', 'index.html')).href;
const MiB = 1024 * 1024;
let browser;

before(async () => { browser = await chromium.launch({ channel: 'msedge', headless: true }); });
after(async () => { await browser?.close(); });

async function openPanel() {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, locale: 'pt-BR' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route(/^https?:/, route => route.abort());
    await page.addInitScript(() => {
        window.metricsTime = 0;
        Object.defineProperty(performance, 'now', { value: () => window.metricsTime });
    });
    await page.goto(appUrl);
    assert.deepEqual(errors, [], 'the real app page must initialize without script errors');
    return page;
}

test('download bytes produce visible speed and remaining time when the bridge sends null', async () => {
    const page = await openPanel();
    try {
        await page.evaluate(MiB => {
            window._opsAdd('download', 'download', 'Arquivo.7z');
            window._opsTransferMetrics('download', 'download', 0, 1, 0, 100 * MiB, null);
        }, MiB);
        assert.equal(await page.locator('#op-eta-download').isVisible(), false);
        await page.evaluate(MiB => {
            window.metricsTime = 2000;
            window._opsProgress('download', 20, 'Baixando parte 2/10');
            window._opsTransferMetrics('download', 'download', 0, 1, 20 * MiB, 100 * MiB, null);
        }, MiB);
        assert.match(await page.locator('#ops-title').innerText(), /Baixando 0\/1 \(10,0 MB\/s\)/);
        assert.equal(await page.locator('#op-eta-download').isVisible(), true);
        assert.equal(await page.locator('#op-eta-download').innerText(), 'Tempo restante estimado: 8 s');

        await page.evaluate(MiB => {
            window.metricsTime = 4000;
            // Missing clocks and out-of-order byte counts also keep the wall-time rate.
            window._opsTransferMetrics('download', 'download', 0, 1, 15 * MiB, 100 * MiB);
        }, MiB);
        assert.match(await page.locator('#ops-title').innerText(), /5,0 MB\/s/);
        assert.equal(await page.locator('#op-eta-download').innerText(), 'Tempo restante estimado: 16 s');
        await page.evaluate(() => window._opsUpdate('download', 'cancelled', 'Cancelado'));
        assert.equal(await page.locator('#op-eta-download').isVisible(), false);
    } finally { await page.close(); }
});

test('upload batches display remaining time using active request time and clear it on waits and completion', async () => {
    const page = await openPanel();
    try {
        await page.evaluate(MiB => {
            window._opsUploadBatch('upload', ['Foto.jpg', 'Video.mp4']);
            window._opsTransferMetrics('upload', 'upload', 0, 2, 0, 100 * MiB, 0);
        }, MiB);
        assert.equal(await page.locator('#op-eta-upload').isVisible(), false);
        await page.evaluate(MiB => {
            window.metricsTime = 120000;
            window._opsUploadFileProgress('upload', 0, 'active', 20, 'Enviando partes');
            window._opsTransferMetrics('upload', 'upload', 0, 2, 20 * MiB, 100 * MiB, 2);
        }, MiB);
        assert.match(await page.locator('#ops-title').innerText(), /Enviando 0\/2 \(10,0 MB\/s\)/);
        assert.equal(await page.locator('#op-eta-upload').isVisible(), true);
        assert.equal(await page.locator('#op-eta-upload').innerText(), 'Tempo restante estimado: 8 s');
        await page.evaluate(() => window._opsUpdate('upload', 'waiting', 'Aguardando Discord'));
        assert.equal(await page.locator('#op-eta-upload').isVisible(), false);

        await page.evaluate(MiB => {
            window.metricsTime = 180000;
            window._opsProgress('upload', 40, 'Enviando partes');
            window._opsTransferMetrics('upload', 'upload', 1, 2, 40 * MiB, 100 * MiB, 4);
        }, MiB);
        assert.equal(await page.locator('#op-eta-upload').innerText(), 'Tempo restante estimado: 6 s');
        assert.equal(await page.locator('#op-eta-upload').isVisible(), true);
        await page.evaluate(MiB => {
            window._opsTransferMetrics('upload', 'upload', 2, 2, 100 * MiB, 100 * MiB, 10);
            window._opsUpdate('upload', 'done', 'Concluído');
        }, MiB);
        assert.equal(await page.locator('#op-eta-upload').isVisible(), false);
        assert.match(await page.locator('#ops-title').innerText(), /Enviando 2\/2/);
    } finally { await page.close(); }
});
