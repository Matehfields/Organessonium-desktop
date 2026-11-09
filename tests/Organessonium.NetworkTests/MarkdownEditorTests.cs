using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private const BindingFlags MarkdownFlags = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string MarkdownFixtureHost = "markdown-image.fixture.test";

    private static void ConfigureMarkdownFixtureImages(SharedHarness h, CoreWebView2 view) =>
        view.SetVirtualHostNameToFolderMapping(MarkdownFixtureHost, h.Directory.CreateSubdirectory("markdown-fixtures").FullName,
            CoreWebView2HostResourceAccessKind.Allow);

    private static Task<TestOutcome> MarkdownEditorHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        using var h = new SharedHarness();
        h.Settings.Language = "pt-BR";
        h.Settings.PreviewEnabled = true;
        h.Settings.AnimationsEnabled = false;
        h.Store.Save(h.Settings);
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1280, 800, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, h.Directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true;
            browser.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 800);
            var web = h.Directory.CreateSubdirectory("web");
            var assembly = typeof(AppController).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith("web/", StringComparison.Ordinal)))
            {
                var path = Path.Combine(web.FullName, resource[4..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = assembly.GetManifestResourceStream(resource)!;
                await using var output = File.Create(path);
                await input.CopyToAsync(output);
            }
            var view = browser.CoreWebView2;
            view.SetVirtualHostNameToFolderMapping("organessonium.local", web.FullName, CoreWebView2HostResourceAccessKind.Allow);
            ConfigureMarkdownFixtureImages(h, view);
            using var dummy = new WebView2();
            var bridge = new WebMessageBridge(dummy, script => view.ExecuteScriptAsync(script));
            await using var controller = new AppController(new Window(), h.Store, bridge, _ => Task.CompletedTask,
                downloadClientFactory: () => new HttpClient(h.Server, false));
            SetFixtureSession(controller, h.Api, h.Settings, h.Registry);
            var resources = (PreviewResourceRegistry)typeof(AppController).GetField("_previewResources", MarkdownFlags)!.GetValue(controller)!;
            using var adapter = new WebView2PreviewAdapter(view, resources);
            adapter.Attach();
            view.WebMessageReceived += async (_, e) =>
            {
                using var message = JsonDocument.Parse(e.WebMessageAsJson);
                var method = message.RootElement.GetProperty("method").GetString();
                if (method is "requestPreview" or "saveMarkdown" or "uploadMarkdownImageData" or "closePreview" or
                    "resolveMarkdownImage" or "resolveEncryptedMarkdownImage" or "resolveEncryptedDriveMarkdownImage")
                    await controller.HandleJsonMessageAsync(e.WebMessageAsJson);
            };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await bridge.SetSettingsAsync(h.Settings);
            await bridge.NotifyReadyAsync();
            return await CheckMarkdownEditorAsync(h, controller, view, capture: false);
        }
        finally
        {
            browser?.Close();
            if (host != IntPtr.Zero) DestroyWindow(host);
        }
    });

    private static async Task<TestOutcome> CheckMarkdownEditorAsync(SharedHarness h, AppController controller, CoreWebView2 view, bool capture)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        async Task WaitFor(string expression, string failure)
        {
            for (var attempt = 0; attempt < 220; attempt++)
            {
                if (await view.ExecuteScriptAsync(expression) == "true") return;
                await Task.Delay(50, timeout.Token);
            }
            var state = await view.ExecuteScriptAsync("""
                (() => ({ events:window.fixtureImageEvents || [], status:window.editorFixture?.()?.saveState?.textContent,
                    images:[...(window.editorFixture?.()?.editor?.querySelectorAll('img') || [])]
                        .map(img => ({ ref:!!img.dataset.organessoniumImage, src:img.hasAttribute('src'), width:img.naturalWidth })),
                    tables:[...(window.editorFixture?.()?.editor?.querySelectorAll('table') || [])]
                        .map(table => [...table.rows].map(row => row.cells.length)),
                    jsErrors:window.fixtureJsErrors || [] }))()
                """);
            throw new InvalidOperationException(failure + " " + state);
        }

        await h.Registry.RefreshMappingsAsync(timeout.Token);
        var source = h.Directory.CreateSubdirectory("markdown-fixtures");
        var pixels = new byte[80 * 48 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = 190;
            pixels[index + 1] = (byte)(50 + index / 4 % 80);
            pixels[index + 2] = 80;
            pixels[index + 3] = 255;
        }
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(80, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 320)));
        var imagePath = Path.Combine(source.FullName, "Imagem do Drive.png");
        using (var output = File.Create(imagePath)) png.Save(output);
        var markdownPath = Path.Combine(source.FullName, "Notas de teste.md");
        await File.WriteAllTextAsync(markdownPath, "Notas de teste", timeout.Token);

        async Task<AppEntry> PublishLegacyFile(string path)
        {
            var file = new FileInfo(path);
            var post = await h.Api.CreateForumPostAsync("files", "markdown-fixture", "fixture", timeout.Token);
            var part = await h.Api.SendFileMessageAsync(post.ThreadId, "", file, timeout.Token);
            var entry = AppEntry.File(file.Name, post.ThreadId, TransferState.Now(), file.Length, "/");
            entry.ParentFolderId = "root";
            entry.ChunkCount = 1;
            entry.ChunkMessageIds = [part.Id];
            await h.Registry.AddEntryAsync(entry, cancellationToken: timeout.Token);
            return entry;
        }

        var original = await PublishLegacyFile(markdownPath);
        var driveImage = await PublishLegacyFile(imagePath);
        var fixtureEntries = new[] { original, driveImage };
        typeof(AppController).GetField("_loadedApps", MarkdownFlags)!.SetValue(controller, fixtureEntries);
        const string external = "https://" + MarkdownFixtureHost + "/image_a_b.png?first=1&second=2";
        var pngBytes = await File.ReadAllBytesAsync(imagePath, timeout.Token);
        File.Copy(imagePath, Path.Combine(source.FullName, "image_a_b.png"));
        await view.ExecuteScriptAsync("window._onAppsLoaded(" + JsonSerializer.Serialize(fixtureEntries, JsonOptions.Web) + ");" +
            "window.fixtureFile = " + JsonSerializer.Serialize(original.FileId) + ";");
        await view.ExecuteScriptAsync("""
            window.editorFixture = () => markdownTabs.get(activeWorkspaceTab);
            window.openEditorFixture = () => openPreview(allApps.find(app => app.fileId === window.fixtureFile));
            window.atEnd = node => {
                editorFixture().editor.focus();
                const range = document.createRange();
                range.selectNodeContents(node); range.collapse(false);
                window.getSelection().removeAllRanges(); window.getSelection().addRange(range);
            };
            window.insertFixtureTable = (rows, columns) => {
                editorFixture().panel.querySelector('[data-cmd="table"]').click();
                $('editor-table-rows').value = rows;
                $('editor-table-columns').value = columns;
                $('editor-table-confirm').click();
            };
            window.fixtureUploadNotices = [];
            window.fixtureImageEvents = [];
            window.fixtureJsErrors = [];
            window.addEventListener('error', event => { if (event.error) fixtureJsErrors.push(event.error.name); });
            const imageReady = window._onMarkdownImageReady;
            window._onMarkdownImageReady = (...args) => { fixtureImageEvents.push('uploaded'); imageReady(...args); };
            const imageError = window._onMarkdownImageError;
            window._onMarkdownImageError = (...args) => { fixtureImageEvents.push('uploadError'); imageError(...args); };
            const imageResolved = window._onEncryptedMarkdownImageReady;
            window._onEncryptedMarkdownImageReady = (...args) => {
                fixtureImageEvents.push(args[2] ? 'resolved' : 'resolveError');
                imageResolved(...args);
            };
            const addOperation = window._opsAdd;
            window._opsAdd = (id, type, name) => {
                if (type === 'upload') window.fixtureUploadNotices.push(name);
                return addOperation(id, type, name);
            };
            window.openEditorFixture();
            """);
        await WaitFor("editorFixture()?.editor?.textContent==='Notas de teste'", "o arquivo Markdown não abriu na guia");
        await view.ExecuteScriptAsync("window._onAppsBatch([allApps.find(app=>app.fileId===window.fixtureFile)]);");
        await WaitFor("editorFixture().tab.firstElementChild.textContent==='description'&&editorFixture().tab.querySelector('[data-tab-label]').textContent==='Notas de teste.md'",
            "atualizar a biblioteca substituiu o ícone de documento pelo nome do arquivo");

        var imageBytesScript = JsonSerializer.Serialize(pngBytes.Select(b => (int)b));
        await view.ExecuteScriptAsync("atEnd(editorFixture().editor);{const data=new DataTransfer();" +
            $"data.items.add(new File([new Uint8Array({imageBytesScript})],'Colada.png',{{type:'image/png'}}));" +
            "editorFixture().editor.dispatchEvent(new ClipboardEvent('paste',{clipboardData:data,bubbles:true,cancelable:true}));}");
        await WaitFor("editorFixture().editor.querySelector('img[data-organessonium-image]')?.naturalWidth===80",
            "o anexo local não foi enviado e resolvido na thread do documento");
        await view.ExecuteScriptAsync("""
            atEnd(editorFixture().editor);
            editorFixture().panel.querySelector('[data-cmd="image"]').click();
            $('editor-image-tab-drive').click();
            $('editor-image-drive-list').querySelector('.editor-image-drive-item').click();
            $('editor-image-confirm').click();
            """);
        await WaitFor("Array.from(editorFixture().editor.querySelectorAll('img')).filter(img=>img.naturalWidth===80).length===2",
            "a imagem legada do Drive não abriu pelo identificador de arquivo");

        await view.ExecuteScriptAsync("""
            atEnd(editorFixture().editor);
            insertFixtureTable(5, 4);
            """);
        await WaitFor("""
            (() => {
                const table = editorFixture().editor.querySelector(':scope > table');
                return table?.rows.length === 5 && Array.from(table.rows).every(row => row.cells.length === 4)
                    && table.rows[1].getBoundingClientRect().height >= 36;
            })()
            """, "a tabela 5×4 perdeu células ou ficou com linhas vazias comprimidas");
        await view.ExecuteScriptAsync("""
            atEnd(editorFixture().editor.querySelector('td'));
            document.execCommand('insertText', false, 'C:\\Fotos|A');
            insertFixtureTable(2, 1);
            """);
        await WaitFor("""
            (() => {
                const tables = editorFixture().editor.querySelectorAll(':scope > table');
                return tables.length === 2 && tables[0].rows[1].cells[0].textContent === 'C:\\Fotos|A'
                    && tables[1].rows.length === 2 && Array.from(tables[1].rows).every(row => row.cells.length === 1);
            })()
            """, "inserir uma tabela dentro da seleção de outra alterou as células anteriores");

        await view.ExecuteScriptAsync("editorFixture().rawToggle.click();window.fixtureExternal=" + JsonSerializer.Serialize(external) + ";");
        await view.ExecuteScriptAsync("""
            {
                const raw = editorFixture().rawEditor;
                raw.value = raw.value.replace('| --- | --- | --- | --- |', '| :--- | :---: | ---: | --- |')
                    + '\n\n[![URL \\] externa](' + window.fixtureExternal + ')](https://example.test/album)';
                raw.setSelectionRange(raw.value.length, raw.value.length);
                insertFixtureTable(2, 2);
                editorFixture().rawToggle.click();
            }
            """);
        const string rendered = """
            (() => {
                const page = editorFixture()?.editor;
                const tables = page?.querySelectorAll(':scope > table');
                const images = [...(page?.querySelectorAll('img') || [])];
                return tables?.length === 3 && tables[2].rows.length === 2 && tables[2].rows[0].cells.length === 2
                    && tables[0].rows[1].cells[0].textContent === 'C:\\Fotos|A'
                    && getComputedStyle(tables[0].rows[1].cells[2]).textAlign === 'right'
                    && images.length === 3 && images.every(img => img.naturalWidth === 80)
                    && images.some(img => img.alt === 'URL ] externa' && img.getAttribute('src') === window.fixtureExternal
                        && img.closest('a')?.getAttribute('href') === 'https://example.test/album');
            })()
            """;
        await WaitFor(rendered, "alternar para o modo renderizado perdeu imagens, alinhamento ou células");
        await WaitFor("editorFixture().saveState.classList.contains('saved')", "o conteúdo inserido não foi gravado");

        await view.ExecuteScriptAsync("""
            window.originalMarkdownAck = window._onMarkdownSaved;
            window._onMarkdownSaved = (...args) => { window.delayedMarkdownAck = args; };
            atEnd(editorFixture().editor.querySelector('td'));
            document.execCommand('insertText', false, ' antes');
            editorFixture().tab.querySelector('.tab-close').click();
            """);
        await WaitFor("window.delayedMarkdownAck&&editorFixture()?.saveState.classList.contains('saving')", "fechar a guia descartou a edição pendente");
        await view.ExecuteScriptAsync("""
            atEnd(editorFixture().editor.querySelectorAll('table')[1].rows[1].cells[0]);
            document.execCommand('insertText', false, 'Durante a gravação');
            window._onMarkdownSaved = window.originalMarkdownAck;
            window._onMarkdownSaved(...window.delayedMarkdownAck);
            """);
        await WaitFor("markdownTabs.size===0&&window.fixtureUploadNotices.length===0&&!document.querySelector('.op-item[data-op-type=upload]')",
            "o salvamento abriu uma notificação de upload ou deixou a guia pendente");

        var latest = h.Store.LoadCachedEntriesForFolder("root").Single(entry => entry.FileId == original.FileId);
        RequireSync(latest.ThreadId != original.ThreadId && latest.Versions.Any(version => version.Payload.ThreadId == original.ThreadId),
            "o teste não deslocou o documento para outra thread");
        // On reopening, resolve the Drive link from disk even when the other file is not in the loaded view.
        typeof(AppController).GetField("_loadedApps", MarkdownFlags)!.SetValue(controller, new[] { latest });
        await view.ExecuteScriptAsync("window.openEditorFixture();");
        await WaitFor("editorFixture()?.editor?.querySelectorAll('table').length===3&&" +
            "editorFixture().editor.querySelectorAll('table')[1].rows[1].cells[0].textContent==='Durante a gravação'&&" +
            "editorFixture().editor.querySelector('td').textContent==='C:\\\\Fotos|A antes'&&" +
            "[...editorFixture().editor.querySelectorAll('img')].length===3&&[...editorFixture().editor.querySelectorAll('img')].every(img=>img.naturalWidth===80)",
            "reabrir a guia perdeu as imagens ou uma alteração feita durante o salvamento");
        if (capture)
        {
            var path = Path.Combine(Environment.CurrentDirectory, "artifacts", "markdown-editor-review", "editor-secondary.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = File.Create(path);
            await view.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, output);
            await Task.Delay(1250, timeout.Token);
        }
        Console.WriteLine($"MARKDOWN_EDITOR_RESULT secondary={capture} attachmentsDecoded=3 tables=5x4,2x1,2x2 historicalThread=True savedWhileClosing=True uploadNotices=0");
        return TestOutcome.Pass();
    }
}
