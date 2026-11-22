using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using Organessonium.Bridge;
using Organessonium.Models;
using Organessonium.Services;
using Organessonium.Services.MetadataEncryption;

namespace Organessonium.NetworkTests;

internal static partial class Program
{
    private sealed class FailingEncryptionStore : IEncryptionSecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        internal bool FailWrites { get; set; }
        public string ReadEncryptionSecret(string key) => _values.GetValueOrDefault(key, "");
        public void WriteEncryptionSecret(string key, string value)
        {
            if (FailWrites) throw new IOException("Falha simulada ao salvar a configuração.");
            _values[key] = value;
        }
    }

    private sealed class DiscordAuthorizationFixture : HttpMessageHandler
    {
        internal bool ApiAuthorized { get; private set; }
        internal bool AttachmentAuthorized { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "discord.com")
            {
                ApiAuthorized = request.Headers.Authorization is { Scheme: "Bot", Parameter: "token-ficticio" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"id\":\"canal\",\"type\":0}", Encoding.UTF8, "application/json")
                });
            }
            AttachmentAuthorized = request.Headers.Authorization != null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        }
    }

    private static async Task<TestOutcome> DiscordAuthorizationStaysOnApiAsync()
    {
        var handler = new DiscordAuthorizationFixture();
        using var api = new DiscordApiClient("token-ficticio", new HttpClient(handler));
        await api.GetChannelAsync("canal");
        var contents = await api.DownloadAttachmentBytesAsync(new DiscordAttachmentDto { Url = "https://cdn.fixture.test/anexo" });
        return handler.ApiAuthorized && !handler.AttachmentAuthorized && contents.SequenceEqual(new byte[] { 1, 2, 3 })
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a autenticação não ficou restrita à requisição da API");
    }

    private sealed class AttachmentRedirectFixture : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool SafeHeaders { get; private set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            SafeHeaders &= request.Headers.Authorization == null &&
                !request.Headers.Contains("Cookie") && request.Headers.Range?.ToString() == "bytes=0-1";
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/start":
                    response.Headers.Location = new Uri("https://media.discordapp.net/next");
                    break;
                case "/next":
                    response.Headers.Location = new Uri("/final", UriKind.Relative);
                    break;
                case "/final":
                    response.StatusCode = HttpStatusCode.PartialContent;
                    response.Content = new ByteArrayContent([4, 2]);
                    break;
                default:
                    response.Headers.Location = new Uri("https://cdn.discordapp.com.example.test/attachment");
                    break;
            }
            return Task.FromResult(response);
        }
    }

    private static async Task<TestOutcome> DiscordAttachmentRedirectPolicyAsync()
    {
        var fixture = new AttachmentRedirectFixture();
        using var client = new HttpClient(new DiscordAttachmentHandler(fixture));
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bot", "fixture");
        client.DefaultRequestHeaders.Add("Cookie", "fixture=value");
        client.DefaultRequestHeaders.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1);
        using var result = await client.GetAsync("https://cdn.discordapp.com/start");
        var bytes = await result.Content.ReadAsByteArrayAsync();
        RequireSync(result.StatusCode == HttpStatusCode.PartialContent && bytes.SequenceEqual(new byte[] { 4, 2 }) &&
            fixture.Calls == 3, "anexo autorizado não chegou ao destino aprovado");
        RequireSync(await ThrowsAsync<InvalidDataException>(() => client.GetAsync("https://cdn.discordapp.com/forbidden")) &&
            fixture.Calls == 4, "redirecionamento externo chegou ao transporte");
        foreach (var forbidden in new[] { "https://127.0.0.1/a", "http://cdn.discordapp.com/a",
            "https://cdn.discordapp.com:8443/a", "https://fixture@cdn.discordapp.com/a" })
        {
            RequireSync(await ThrowsAsync<InvalidDataException>(() => client.GetAsync(forbidden)) &&
                fixture.Calls == 4, "destino inválido chegou ao transporte");
        }
        RequireSync(fixture.SafeHeaders, "credenciais chegaram ao CDN ou o Range foi perdido");
        return TestOutcome.Pass();
    }

    private static Task<TestOutcome> DiscordErrorDoesNotExposeResponseAsync()
    {
        const string sensitive = "conteudo-sensivel-de-teste";
        var body = "{\"code\":50035,\"message\":\"" + sensitive + "\"}";
        var error = new DiscordApiException(HttpStatusCode.BadRequest, body);
        return Task.FromResult(!error.Message.Contains(sensitive, StringComparison.Ordinal) &&
                               error.Message.Contains("50035", StringComparison.Ordinal) &&
                               error.Body == body && error.DiscordCode == 50035
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a mensagem de erro expôs a resposta recebida ou perdeu o código necessário"));
    }

    private static Task<TestOutcome> SecurePdfPreviewHiddenAsync() => OnHiddenDispatcherAsync(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("organessonium-pdf-csp-");
        var host = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        CoreWebView2Controller? browser = null;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, directory.CreateSubdirectory("browser").FullName);
            browser = await environment.CreateCoreWebView2ControllerAsync(host);
            browser.IsVisible = true;
            browser.Bounds = new System.Drawing.Rectangle(0, 0, 1000, 760);
            var web = directory.CreateSubdirectory("web");
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
            using var resources = new PreviewResourceRegistry();
            using var adapter = new WebView2PreviewAdapter(view, resources);
            adapter.Attach();
            var document = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n" +
                           "2 0 obj\n<< /Type /Pages /Count 1 /Kids [3 0 R] >>\nendobj\n" +
                           "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources <<>> >>\nendobj\n" +
                           "trailer\n<< /Root 1 0 R >>\n%%EOF";
            var url = resources.RegisterBytes(Encoding.ASCII.GetBytes(document), "application/pdf");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); };
            view.Navigate("https://organessonium.local/index.html");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var malicious = "[link](javascript:alert(1))\n\n![imagem](data:text/html,conteudo)\n\n" +
                            "<img src=x onerror=alert(1)>\n\n[seguro](https://example.test/arquivo)";
            await view.ExecuteScriptAsync("window.__organessoniumSettings={previewEnabled:true};window.onBridgeReady();" +
                "window.__md={fileId:'md',threadId:'md',name:'teste.md',type:'file',path:'/',parentFolderId:'root'};" +
                "window._onAppsLoaded([window.__md]);window.bridge.requestPreview=(id,name)=>window._onPreviewReady('markdown',name," +
                JsonSerializer.Serialize(malicious) + ");openPreview(window.__md);");
            var safeMarkdown = await view.ExecuteScriptAsync("(() => { const tab=markdownTabs.get(activeWorkspaceTab);" +
                "if(!tab?.editor)return false;tab.rawToggle.click();tab.rawToggle.click();" +
                "return !tab.editor.querySelector('script,iframe,object,embed,[onerror],[onclick],img') && " +
                "tab.editor.querySelectorAll('a').length===1 && " +
                "tab.editor.querySelector('a').getAttribute('href')==='https://example.test/arquivo'; })()");
            var markdownState = await view.ExecuteScriptAsync("(() => { const tab=markdownTabs.get(activeWorkspaceTab);return JSON.stringify({tab:!!tab,editor:!!tab?.editor,html:tab?.editor?.innerHTML}); })()");
            RequireSync(safeMarkdown == "true", "o editor renderizou conteúdo ou URLs não confiáveis: " + markdownState);
            await view.ExecuteScriptAsync("markdownTabs.get(activeWorkspaceTab).lastSavedMarkdown=getEditorMarkdown(markdownTabs.get(activeWorkspaceTab));closeMarkdownTab(activeWorkspaceTab)");
            await view.ExecuteScriptAsync("window.__organessoniumSettings={previewEnabled:true};window.onBridgeReady();" +
                "window.__pdf={fileId:'pdf',threadId:'pdf',name:'teste.pdf',type:'file',path:'/',parentFolderId:'root'};" +
                "window._onAppsLoaded([window.__pdf]);window.bridge.requestPreview=(id,name)=>" +
                "window._onPreviewReady('pdf',name,JSON.stringify({threadId:id,directUrl:" + JsonSerializer.Serialize(url) + "}),id);" +
                "openPreview(window.__pdf);");
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (await view.ExecuteScriptAsync("pdfPreviewState?.total===1&&pdfPreviewState.rendered.has(1)") == "true")
                    return TestOutcome.Pass();
                await Task.Delay(100);
            }
            var state = await view.ExecuteScriptAsync("JSON.stringify({total:pdfPreviewState?.total,progress:pdfPreviewState?.progressEl?.textContent})");
            return TestOutcome.Fail("a prévia do PDF não renderizou: " + state);
        }
        finally
        {
            browser?.Close();
            if (host != IntPtr.Zero) DestroyWindow(host);
            try { directory.Delete(true); } catch (IOException) { }
        }
    });

    private static Task<TestOutcome> WebAppSettingsNeverExposeSecretsAsync()
    {
        const string token = "token-de-teste-nao-exposto-95731";
        const string proxyPassword = "senha-de-proxy-nao-exposta-42816";
        var source = new AppSettings
        {
            BotToken = token,
            Proxy = new ProxySettings
            {
                Mode = ProxyMode.Manual,
                Host = "127.0.0.1",
                Port = 8080,
                AuthenticationEnabled = true,
                Username = "usuario-de-teste",
                Password = proxyPassword
            }
        };

        var web = WebAppSettings.FromSettings(source);
        var json = JsonSerializer.Serialize(web);
        var valid = web.BotTokenConfigured &&
                    web.Proxy.PasswordConfigured &&
                    web.Proxy.Username == "usuario-de-teste" &&
                    !json.Contains(token, StringComparison.Ordinal) &&
                    !json.Contains(proxyPassword, StringComparison.Ordinal) &&
                    json.Contains("\"botTokenConfigured\":true", StringComparison.Ordinal) &&
                    json.Contains("\"proxyPasswordConfigured\":true", StringComparison.Ordinal) &&
                    typeof(WebAppSettings).GetProperty("BotToken") == null &&
                    typeof(WebAppSettings).GetProperty("Password") == null &&
                    typeof(WebProxySettings).GetProperty("Password") == null;

        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("o contrato da interface ainda expõe ou omite credenciais da configuração"));
    }

    private static Task<TestOutcome> SettingsStoreProtectsCredentialsAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium_secret_store_");
        try
        {
            const string token = "token-local-protegido-61827";
            const string proxyPassword = "senha-proxy-protegida-74105";
            var store = new SettingsStore(root.CreateSubdirectory("data"));
            var settings = store.Load();
            settings.BotToken = token;
            settings.Proxy.Password = proxyPassword;
            store.Save(settings);

            var savedRows = ReadSettingRows(store.DatabasePath);
            var savedWithoutPlaintext =
                !savedRows.ContainsKey(SettingsStore.BotTokenSetting) &&
                !savedRows.ContainsKey(SettingsStore.ProxyPasswordSetting) &&
                savedRows.ContainsKey(SettingsStore.ProtectedBotTokenSetting) &&
                savedRows.ContainsKey(SettingsStore.ProtectedProxyPasswordSetting) &&
                savedRows.Values.All(value =>
                    !value.Contains(token, StringComparison.Ordinal) &&
                    !value.Contains(proxyPassword, StringComparison.Ordinal));

            var loaded = store.Load();
            var roundTrip = loaded.BotToken == token && loaded.Proxy.Password == proxyPassword;

            // An unavailable DPAPI value must survive unrelated preference saves.
            const string unavailable = "material-protegido-indisponivel";
            using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    UPDATE settings SET value = $value WHERE key IN ('botTokenProtectedV1', 'proxyPasswordProtectedV1');
                    INSERT OR REPLACE INTO settings(key, value) VALUES
                        ('botToken', 'token-legado-divergente'), ('proxyPassword', 'senha-legada-divergente');
                    """;
                command.Parameters.AddWithValue("$value", unavailable);
                command.ExecuteNonQuery();
            }
            var unavailableSettings = store.Load();
            unavailableSettings.Theme = "dark";
            store.Save(unavailableSettings);
            var unavailableRows = ReadSettingRows(store.DatabasePath);
            var unavailablePreserved = unavailableRows[SettingsStore.ProtectedBotTokenSetting] == unavailable &&
                                       unavailableRows[SettingsStore.ProtectedProxyPasswordSetting] == unavailable &&
                                       unavailableRows[SettingsStore.BotTokenSetting] == "token-legado-divergente" &&
                                       unavailableRows[SettingsStore.ProxyPasswordSetting] == "senha-legada-divergente";
            unavailableSettings.BotToken = token;
            unavailableSettings.Proxy.Password = proxyPassword;
            store.Save(unavailableSettings);
            var replacement = store.Load();
            var replacementWorks = replacement.BotToken == token && replacement.Proxy.Password == proxyPassword;

            using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    DELETE FROM settings WHERE key IN ('botTokenProtectedV1', 'proxyPasswordProtectedV1');
                    INSERT OR REPLACE INTO settings(key, value) VALUES
                        ('botToken', 'token-legado-em-texto'),
                        ('proxyPassword', 'senha-legada-em-texto');
                    """;
                command.ExecuteNonQuery();
            }

            var migrated = store.Load();
            var migratedRows = ReadSettingRows(store.DatabasePath);
            var migration =
                migrated.BotToken == "token-legado-em-texto" &&
                migrated.Proxy.Password == "senha-legada-em-texto" &&
                !migratedRows.ContainsKey(SettingsStore.BotTokenSetting) &&
                !migratedRows.ContainsKey(SettingsStore.ProxyPasswordSetting) &&
                migratedRows.ContainsKey(SettingsStore.ProtectedBotTokenSetting) &&
                migratedRows.ContainsKey(SettingsStore.ProtectedProxyPasswordSetting) &&
                migratedRows.Values.All(value =>
                    !value.Contains("token-legado-em-texto", StringComparison.Ordinal) &&
                    !value.Contains("senha-legada-em-texto", StringComparison.Ordinal));
            var legacyBytes = new[] { Encoding.UTF8.GetBytes("token-legado-em-texto"), Encoding.UTF8.GetBytes("senha-legada-em-texto") };
            var databaseFiles = new[] { store.DatabasePath, store.DatabasePath + "-wal", store.DatabasePath + "-journal" };
            var noLegacyBytes = databaseFiles.Where(File.Exists).All(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                var contents = copy.ToArray();
                return legacyBytes.All(secret => contents.AsSpan().IndexOf(secret) < 0);
            });

            using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
            {
                connection.Open();
                SettingsStore.ClearDatabaseContentsForLogout(connection);
            }

            var afterLogout = ReadSettingRows(store.DatabasePath);
            var cleared = afterLogout.Count == 0;

            return Task.FromResult(savedWithoutPlaintext && roundTrip && unavailablePreserved && replacementWorks && migration && noLegacyBytes && cleared
                ? TestOutcome.Pass()
                : TestOutcome.Fail($"o armazenamento local não protegeu, migrou ou limpou as credenciais (saved={savedWithoutPlaintext} roundTrip={roundTrip} unavailable={unavailablePreserved} replacement={replacementWorks} migration={migration} residual={noLegacyBytes} cleared={cleared})"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { root.Delete(true); } catch { }
        }
    }

    private static Dictionary<string, string> ReadSettingRows(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM settings";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            rows[reader.GetString(0)] = reader.GetString(1);
        }

        return rows;
    }

    private static Task<TestOutcome> IniMigrationPreservesUnavailableCredentialAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium_ini_security_");
        try
        {
            var store = new SettingsStore(root);
            using (var connection = new SqliteConnection($"Data Source={store.DatabasePath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO settings(key,value) VALUES ('botTokenProtectedV1','material-protegido-indisponivel')";
                command.ExecuteNonQuery();
            }
            File.WriteAllText(Path.Combine(root.FullName, "config.ini"), "botToken=token-legado-de-teste\nnamesChannelId=canal\n");
            var reopened = new SettingsStore(root, migrateIni: true);
            var rows = ReadSettingRows(reopened.DatabasePath);
            var preserved = rows[SettingsStore.ProtectedBotTokenSetting] == "material-protegido-indisponivel" &&
                            string.IsNullOrEmpty(reopened.Load().BotToken);
            return Task.FromResult(preserved ? TestOutcome.Pass() : TestOutcome.Fail("a migração do INI substituiu um token protegido indisponível"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { root.Delete(true); } catch { }
        }
    }

    private static Task<TestOutcome> IniMigrationProtectsCredentialsAndLogoutAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-ini-logout-");
        try
        {
            var data = root.CreateSubdirectory("data");
            var legacyPath = Path.Combine(root.FullName, "config.ini");
            const string token = "credencial-legada-ficticia";
            File.WriteAllText(legacyPath, "botToken=" + token + "\nnamesChannelId=names\nfoldersForumId=folders\nappsForumId=files\nserialChannelId=serial\n");
            // A lock simulates an interruption between database commit and INI replacement.
            using (File.Open(legacyPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var interrupted = new SettingsStore(data, migrateIni: true, legacyIniPath: legacyPath);
                RequireSync(interrupted.Load().BotToken == token, "a falha ao substituir o INI perdeu a credencial");
            }
            var reopened = new SettingsStore(data, migrateIni: true, legacyIniPath: legacyPath);
            var migrated = File.ReadAllText(legacyPath);
            RequireSync(reopened.Load().IsConfigured && !migrated.Contains(token, StringComparison.Ordinal) &&
                migrated.Contains("namesChannelId=names", StringComparison.Ordinal), "a retomada não removeu o token já persistido ou perdeu preferências");

            File.AppendAllText(legacyPath, "botToken=alternativa-legada-ficticia\n");
            var configured = new SettingsStore(data, migrateIni: true, legacyIniPath: legacyPath);
            var protectedIni = File.ReadAllText(legacyPath);
            RequireSync(configured.Load().BotToken == token &&
                !protectedIni.Contains("alternativa-legada-ficticia", StringComparison.Ordinal) &&
                protectedIni.Contains(SettingsStore.ProtectedBotTokenSetting + "=", StringComparison.Ordinal),
                "o token alternativo substituiu o ativo ou permaneceu em claro");
            configured.ClearLocalDataForLogout();
            var loggedOut = new SettingsStore(data, migrateIni: true, legacyIniPath: legacyPath);
            RequireSync(loggedOut.Load().BotToken.Length == 0 &&
                !File.ReadAllText(legacyPath).Contains("botToken", StringComparison.OrdinalIgnoreCase),
                "o INI externo voltou a fornecer uma credencial após logout");
            return Task.FromResult(TestOutcome.Pass());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static async Task<TestOutcome> CustomKeyHistoryAndAndroidRecoveryAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium-key-history-");
        try
        {
            var store = new SettingsStore(root);
            var encryption = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(store));
            var metadata = MetadataCodec.ForStore(store, () => EncryptionModes.Custom);
            encryption.ConfigureCustomKey("chave antiga sintetica");
            var old = encryption.CreateFileEncryption(EncryptionModes.Custom);
            var oldContext = new EncryptedPayloadContext("before", old, EncryptedPayloadRoles.Original, 0, 1);
            var oldPayload = encryption.EncryptBytes([8, 3], oldContext);
            var oldMetadata = metadata.Encode("{\"fileId\":\"before\"}");
            var noId = old.Clone();
            noId.KeyId = null;
            var legacyContext = new EncryptedPayloadContext("before", noId, EncryptedPayloadRoles.Original, 0, 1);

            // ORGKEY stores magic, version, master key and SHA-256 of those fields.
            var androidKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
            var legacy = new byte[71];
            "ORGKEY"u8.CopyTo(legacy);
            legacy[6] = 1;
            androidKey.CopyTo(legacy, 7);
            SHA256.HashData(legacy.AsSpan(0, 39)).CopyTo(legacy, 39);
            var path = Path.Combine(root.FullName, "android.organessonium-key");
            await File.WriteAllBytesAsync(path, legacy);
            RequireSync(await encryption.ImportCustomKeyAsync(path, null) == CustomKeyImportKind.Legacy,
                "o formato legado Android foi rejeitado");
            var current = encryption.CreateFileEncryption(EncryptionModes.Custom);
            var expectedId = Convert.ToBase64String(SHA256.HashData(androidKey)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            RequireSync(current.KeyId == expectedId && current.KeyId != old.KeyId, "a chave ativa não usa o identificador Android");
            var currentContext = new EncryptedPayloadContext("after", current, EncryptedPayloadRoles.Original, 0, 1);
            var currentPayload = encryption.EncryptBytes([9, 4], currentContext);

            var reopenedStore = new SettingsStore(root);
            var reopened = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(reopenedStore));
            var reopenedCodec = MetadataCodec.ForStore(reopenedStore);
            RequireSync(reopened.DecryptBytes(oldPayload, oldContext).SequenceEqual(new byte[] { 8, 3 }) &&
                reopened.DecryptBytes(oldPayload, legacyContext).SequenceEqual(new byte[] { 8, 3 }) &&
                reopened.DecryptBytes(currentPayload, currentContext).SequenceEqual(new byte[] { 9, 4 }) &&
                reopenedCodec.Decode(oldMetadata).Contains("before", StringComparison.Ordinal),
                "payload ou metadados da chave anterior ficaram ilegíveis após reabrir");
            RequireSync(Throws<EncryptedPayloadAuthenticationException>(() => reopened.DecryptBytes(oldPayload,
                new EncryptedPayloadContext("before", new EncryptionMetadata { Mode = old.Mode, Salt = old.Salt, KeyId = current.KeyId }, EncryptedPayloadRoles.Original, 0, 1))),
                "uma chave explícita incorreta foi ignorada");
            legacy[^1] ^= 1;
            await File.WriteAllBytesAsync(path, legacy);
            RequireSync(await ThrowsAsync<InvalidOperationException>(() => reopened.ImportCustomKeyAsync(path, null)) &&
                reopened.CreateFileEncryption(EncryptionModes.Custom).KeyId == expectedId, "o legado adulterado trocou a chave ativa");
            return TestOutcome.Pass();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            root.Delete(recursive: true);
        }
    }

    private static Task<TestOutcome> WebMessagePolicyRejectsUntrustedMessagesAsync()
    {
        static bool Validate(string json) => WebMessagePolicy.TryValidate(json, out _, out _);
        static bool Trusted(string source, string topLevel) => WebMessagePolicy.IsTrustedSource(source, topLevel);

        const string page = "https://organessonium.local/index.html";
        const string setup = "https://organessonium.local/setup.html";
        var tooManyArguments = "[" + string.Join(",", Enumerable.Repeat("null", 65)) + "]";
        var deepArguments = new string('[', 24) + new string(']', 24);
        var valid =
            WebMessagePolicy.IsLocalDocument(page) &&
            WebMessagePolicy.IsLocalDocument(setup) &&
            !WebMessagePolicy.IsLocalDocument("http://organessonium.local/index.html") &&
            !WebMessagePolicy.IsLocalDocument("https://organessonium.local:444/index.html") &&
            !WebMessagePolicy.IsLocalDocument("https://organessonium.local/outra.html") &&
            !WebMessagePolicy.IsLocalDocument("https://organessonium.local/index.html/") &&
            !WebMessagePolicy.IsLocalDocument("https://usuario:senha@organessonium.local/index.html") &&
            !WebMessagePolicy.IsLocalDocument("file:///C:/Windows/win.ini") &&
            Trusted(page, page) &&
            Trusted(setup, setup) &&
            !Trusted(setup, page) &&
            !Trusted(page + "?frame=1", page) &&
            !Trusted("https://evil.local/index.html", page) &&
            !Trusted("http://organessonium.local/index.html", page) &&
            !Trusted("https://organessonium.local:444/index.html", page) &&
            Validate("""{"method":"saveSettings","args":["medium",4,"dark","","",true,"pt-BR",true,true,"low","",4,3,"automatic","",0,"auto",false,"",null,4,1,false,"system","#0078D4",true]}""") &&
            Validate("""{"method":"provideRecoveryPassword","args":["pedido",null]}""") &&
            Validate("""{"method":"openExternalLink","args":["https://example.com"]}""") &&
            Validate("""{"method":"setupEncryption","args":["import"]}""") &&
            Validate("""{"method":"loadFavorites","args":[]}""") &&
            Validate("""{"method":"setTitleBarDragRegion","args":[0,0,200,48]}""") &&
            !Validate("") &&
            !Validate("null") &&
            !Validate("[]") &&
            !Validate("nao-e-json") &&
            !Validate("""{"method":12,"args":[]}""") &&
            !Validate("""{"method":"comando-desconhecido","args":[]}""") &&
            !Validate("""{"method":"saveSettings","args":{}}""") &&
            !Validate("""{"method":"saveSettings","args":[]}""") &&
            !Validate("""{"method":"deleteItems","args":[{},true]}""") &&
            !Validate("""{"method":"deleteItems","args":["[]","true"]}""") &&
            !Validate("""{"method":"openExternalLink"}""") &&
            !Validate("""{"method":"openExternalLink","args":[],"args":["https://example.com"]}""") &&
            !Validate("""{"method":"setTitleBarDragRegion","args":[0,0,200]}""") &&
            !Validate("""{"method":"saveSettings","args":[],"extra":true}""") &&
            !Validate("{\"method\":\"saveSettings\",\"args\":" + tooManyArguments + "}") &&
            !Validate("{\"method\":\"saveSettings\",\"args\":" + deepArguments + "}") &&
            !Validate(new string('x', WebMessagePolicy.MaximumMessageBytes + 1));

        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a política de mensagens aceitou origem, estrutura ou comando não confiável"));
    }

    private static Task<TestOutcome> ProxyPasswordPreservationAsync()
    {
        static ProxySettings Stored() => new()
        {
            AuthenticationEnabled = true,
            Username = "usuario",
            Password = "senha-salva"
        };

        static ProxySettings Requested(string password = "", bool authentication = true, string username = "usuario") => new()
        {
            AuthenticationEnabled = authentication,
            Username = username,
            Password = password
        };

        var omitted = Requested();
        AppController.PreserveStoredProxyPassword(omitted, Stored(), passwordOmitted: true);
        var omittedPreserved = omitted.Password == "senha-salva";

        var nullPassword = Requested();
        nullPassword.Password = null!;
        AppController.PreserveStoredProxyPassword(nullPassword, Stored());
        var nullPreserved = nullPassword.Password == "senha-salva";

        var explicitEmpty = Requested();
        AppController.PreserveStoredProxyPassword(explicitEmpty, Stored());
        var emptyKept = explicitEmpty.Password == "";

        var otherUser = Requested(username: "outro");
        AppController.PreserveStoredProxyPassword(otherUser, Stored(), passwordOmitted: true);
        var otherUserKept = otherUser.Password == "";

        var withoutAuthentication = Requested(authentication: false);
        AppController.PreserveStoredProxyPassword(withoutAuthentication, Stored(), passwordOmitted: true);
        var unauthenticatedKept = withoutAuthentication.Password == "";

        var explicitPassword = Requested("senha-nova");
        AppController.PreserveStoredProxyPassword(explicitPassword, Stored());
        var explicitKept = explicitPassword.Password == "senha-nova";

        var emptyStored = Stored();
        emptyStored.Password = "";
        var noStoredPassword = Requested();
        AppController.PreserveStoredProxyPassword(noStoredPassword, emptyStored, passwordOmitted: true);
        var emptyStoredKept = noStoredPassword.Password == "";

        var valid = omittedPreserved && nullPreserved && emptyKept && otherUserKept &&
                    unauthenticatedKept && explicitKept && emptyStoredKept;
        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a preservação da senha de proxy não respeitou omissão, usuário ou autenticação"));
    }

    private static Task<TestOutcome> ExternalLinkPolicyRestrictsSchemesAsync()
    {
        static bool Accepts(string? value) => ExternalLinkPolicy.TryNormalize(value, out _);
        var normalizedOk = ExternalLinkPolicy.TryNormalize("https://example.com", out var normalized);

        var valid =
            Accepts("https://example.com/path?first=1&second=2") &&
            Accepts("http://example.com") &&
            Accepts("mailto:usuario@example.com") &&
            normalizedOk && normalized.StartsWith("https://example.com/", StringComparison.Ordinal) &&
            !Accepts("javascript:alert(1)") &&
            !Accepts("data:text/html,<script>alert(1)</script>") &&
            !Accepts("file:///C:/Windows/win.ini") &&
            !Accepts("ftp://example.com") &&
            !Accepts("https://usuario:senha@example.com") &&
            !Accepts("https://") &&
            !Accepts("") &&
            !Accepts(null) &&
            !Accepts("https://example.com/" + new string('a', 2048)) &&
            !Accepts("https://example.com/a\nb");

        return Task.FromResult(valid
            ? TestOutcome.Pass()
            : TestOutcome.Fail("a política de links externos aceitou um esquema ou formato proibido"));
    }

    private static async Task<TestOutcome> CustomKeyRecoveryPasswordProtectionAsync()
    {
        var root = Directory.CreateTempSubdirectory("organessonium_recovery_security_");
        try
        {
            const string keyPassphrase = "chave-personalizada-de-teste";
            const string recoveryPassword = "senha-de-recuperacao-123";
            var storeA = new InMemoryEncryptionSecretStore();
            var providerA = new CustomEncryptionKeyProvider(storeA);
            var cryptoA = new EncryptionService(new DefaultEncryptionKeyProvider(), providerA);
            cryptoA.ConfigureCustomKey(keyPassphrase);
            var encryption = cryptoA.CreateFileEncryption(EncryptionModes.Custom);
            var context = new EncryptedPayloadContext("file-security", encryption, EncryptedPayloadRoles.Original, 0);
            var original = RandomNumberGenerator.GetBytes(2048);
            var payload = cryptoA.EncryptBytes(original, context);
            var exportPath = Path.Combine(root.FullName, "recovery.organessonium-key");
            await cryptoA.ExportCustomKeyAsync(exportPath, recoveryPassword);

            var exported = await File.ReadAllBytesAsync(exportPath);
            var formatOk = exported.Length == 85 &&
                           exported.AsSpan(0, 4).SequenceEqual("OEK2"u8) &&
                           exported[4] == 1;

            var storeB = new FailingEncryptionStore();
            var cryptoB = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeB));
            var importKind = await cryptoB.ImportCustomKeyAsync(exportPath, recoveryPassword);
            var restored = cryptoB.DecryptBytes(payload, context);
            var roundTrip = importKind == CustomKeyImportKind.PasswordProtected &&
                            CryptographicOperations.FixedTimeEquals(original, restored);
            CryptographicOperations.ZeroMemory(restored);

            var storeWrongPassword = new InMemoryEncryptionSecretStore();
            var wrongPasswordCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeWrongPassword));
            var wrongPasswordRejected = await ThrowsAsync<InvalidOperationException>(
                () => wrongPasswordCrypto.ImportCustomKeyAsync(exportPath, "senha-incorreta-456"));
            var existingKeyRejected = await ThrowsAsync<InvalidOperationException>(
                () => cryptoB.ImportCustomKeyAsync(exportPath, "senha-incorreta-456"));
            var retainedPlaintext = cryptoB.DecryptBytes(payload, context);
            var existingKeyRetained = existingKeyRejected && CryptographicOperations.FixedTimeEquals(original, retainedPlaintext);
            CryptographicOperations.ZeroMemory(retainedPlaintext);
            var storedKeyBeforeFailure = storeB.ReadEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey);
            storeB.FailWrites = true;
            var interrupted = await ThrowsAsync<IOException>(() => cryptoB.ImportCustomKeyAsync(exportPath, recoveryPassword));
            storeB.FailWrites = false;
            var persistedKeyRetained = interrupted &&
                storeB.ReadEncryptionSecret(EncryptionSettingKeys.ProtectedMasterKey) == storedKeyBeforeFailure;
            var afterFailure = cryptoB.DecryptBytes(payload, context);
            persistedKeyRetained &= CryptographicOperations.FixedTimeEquals(original, afterFailure);
            CryptographicOperations.ZeroMemory(afterFailure);

            var storeMissingPassword = new InMemoryEncryptionSecretStore();
            var missingPasswordCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeMissingPassword));
            var missingPasswordRejected = await ThrowsAsync<RecoveryPasswordRequiredException>(
                () => missingPasswordCrypto.ImportCustomKeyAsync(exportPath, null));

            var tamperedPath = Path.Combine(root.FullName, "tampered.organessonium-key");
            var tampered = exported.ToArray();
            tampered[40] ^= 0x01;
            await File.WriteAllBytesAsync(tamperedPath, tampered);
            var storeTampered = new InMemoryEncryptionSecretStore();
            var tamperedCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeTampered));
            var tamperRejected = await ThrowsAsync<InvalidOperationException>(
                () => tamperedCrypto.ImportCustomKeyAsync(tamperedPath, recoveryPassword));

            var truncatedPath = Path.Combine(root.FullName, "truncated.organessonium-key");
            await File.WriteAllBytesAsync(truncatedPath, exported[..^1]);
            var storeTruncated = new InMemoryEncryptionSecretStore();
            var truncatedCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeTruncated));
            var truncationRejected = await ThrowsAsync<InvalidOperationException>(
                () => truncatedCrypto.ImportCustomKeyAsync(truncatedPath, recoveryPassword));

            var shortPasswordPath = Path.Combine(root.FullName, "short-password.organessonium-key");
            var shortPasswordRejected = await ThrowsAsync<InvalidOperationException>(
                () => cryptoA.ExportCustomKeyAsync(shortPasswordPath, "curta")) && !File.Exists(shortPasswordPath);

            var oversizedPath = Path.Combine(root.FullName, "oversized.organessonium-key");
            await File.WriteAllBytesAsync(oversizedPath, new byte[8192]);
            var storeOversized = new InMemoryEncryptionSecretStore();
            var oversizedCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeOversized));
            var oversizedRejected = await ThrowsAsync<InvalidOperationException>(
                () => oversizedCrypto.ImportCustomKeyAsync(oversizedPath, recoveryPassword));

            var masterKey = providerA.CopyActiveKey();
            var legacy = new byte[70];
            "OEK1"u8.CopyTo(legacy);
            legacy[4] = 1;
            legacy[5] = 32;
            masterKey.CopyTo(legacy, 6);
            var legacyTag = HMACSHA256.HashData(masterKey, legacy.AsSpan(0, 38));
            legacyTag.CopyTo(legacy, 38);
            var legacyPath = Path.Combine(root.FullName, "legacy.organessonium-key");
            await File.WriteAllBytesAsync(legacyPath, legacy);
            var storeLegacy = new InMemoryEncryptionSecretStore();
            var legacyCrypto = new EncryptionService(new DefaultEncryptionKeyProvider(), new CustomEncryptionKeyProvider(storeLegacy));
            var legacyKind = await legacyCrypto.ImportCustomKeyAsync(legacyPath, null);
            var legacyRestored = legacyCrypto.DecryptBytes(payload, context);
            var legacyOk = legacyKind == CustomKeyImportKind.Legacy &&
                           CryptographicOperations.FixedTimeEquals(original, legacyRestored);

            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(restored);
            CryptographicOperations.ZeroMemory(legacyRestored);
            CryptographicOperations.ZeroMemory(exported);
            CryptographicOperations.ZeroMemory(tampered);
            CryptographicOperations.ZeroMemory(legacy);
            CryptographicOperations.ZeroMemory(legacyTag);
            CryptographicOperations.ZeroMemory(masterKey);

            return formatOk && roundTrip && wrongPasswordRejected && existingKeyRetained && persistedKeyRetained && missingPasswordRejected &&
                   tamperRejected && truncationRejected && shortPasswordRejected && oversizedRejected && legacyOk
                ? TestOutcome.Pass()
                : TestOutcome.Fail("a proteção por senha da chave de recuperação não rejeitou todos os casos inválidos");
        }
        finally
        {
            try { root.Delete(true); } catch { }
        }
    }

    private static async Task<bool> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
