using Discord;
using Discord.Net.Rest;
using Discord.Net.WebSockets;
using Discord.Rest;
using Discord.WebSocket;
using Organessonium.Models;
using System.Net;

namespace Organessonium.Services;

public sealed class DiscordSession : IAsyncDisposable
{
    private DiscordRestClient? _restClient;
    private DiscordSocketClient? _socketClient;

    public DiscordApiClient? Api { get; private set; }
    public AppSettings Settings { get; private set; } = new();

    public async Task ConnectAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.BotToken))
        {
            throw new InvalidOperationException("Insira o token do bot.");
        }

        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("Configure os canais names, folders, files e serial.");
        }

        var channelIds = new HashSet<string>(StringComparer.Ordinal)
        {
            settings.NamesChannelId.Trim(),
            settings.FoldersForumId.Trim(),
            settings.AppsForumId.Trim(),
            settings.SerialChannelId.Trim()
        };
        if (channelIds.Count != 4)
        {
            throw new InvalidOperationException("Os canais names, folders, files e serial precisam ser diferentes.");
        }

        Settings = settings.Clone();
        var proxy = NetworkConfig.Current.CreateWebProxyForDiscordNet();
        _restClient = new DiscordRestClient(new DiscordRestConfig
        {
            RestClientProvider = DefaultRestClientProvider.Create(proxy == null, proxy)
        });
        await _restClient.LoginAsync(TokenType.Bot, settings.BotToken.Trim());

        Api = new DiscordApiClient(settings.BotToken.Trim());
        var names = await Api.GetChannelAsync(settings.NamesChannelId.Trim(), cancellationToken);
        if (names.Type is not (0 or 5))
        {
            throw new InvalidOperationException("Canal names não encontrado ou não é um canal de texto.");
        }

        var foldersForum = await Api.GetChannelAsync(settings.FoldersForumId.Trim(), cancellationToken);
        if (foldersForum.Type is not (15 or 16))
        {
            throw new InvalidOperationException("Fórum folders não encontrado ou não é um canal de fórum.");
        }

        var filesForum = await Api.GetChannelAsync(settings.AppsForumId.Trim(), cancellationToken);
        if (filesForum.Type is not (15 or 16))
        {
            throw new InvalidOperationException("Fórum files não encontrado ou não é um canal de fórum.");
        }

        var serial = await Api.GetChannelAsync(settings.SerialChannelId.Trim(), cancellationToken);
        if (serial.Type is not (0 or 5))
        {
            throw new InvalidOperationException("Canal serial não encontrado ou não é um canal de texto.");
        }

        await StartPresenceAsync(settings.BotToken.Trim(), proxy, cancellationToken);
    }

    private async Task StartPresenceAsync(string token, IWebProxy? proxy, CancellationToken cancellationToken)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var socket = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds,
            AlwaysDownloadUsers = false,
            MessageCacheSize = 0,
            WebSocketProvider = DefaultWebSocketProvider.Create(proxy)
        });

        Task OnReady()
        {
            ready.TrySetResult();
            return Task.CompletedTask;
        }

        socket.Ready += OnReady;
        try
        {
            await socket.LoginAsync(TokenType.Bot, token);
            await socket.StartAsync();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            await socket.SetStatusAsync(UserStatus.Online);
            _socketClient = socket;
        }
        catch
        {
            await StopSocketAsync(socket);
            throw;
        }
        finally
        {
            socket.Ready -= OnReady;
        }
    }

    public DiscordApiClient RequireApi()
    {
        return Api ?? throw new InvalidOperationException("Discord ainda não conectado.");
    }

    public async ValueTask DisposeAsync()
    {
        var socket = Interlocked.Exchange(ref _socketClient, null);
        if (socket != null)
        {
            await StopSocketAsync(socket);
        }

        Api?.Dispose();
        Api = null;
        if (_restClient != null)
        {
            try
            {
                await _restClient.LogoutAsync();
            }
            catch
            {
                // Ignore shutdown errors.
            }

            _restClient.Dispose();
            _restClient = null;
        }
    }

    private static async Task StopSocketAsync(DiscordSocketClient socket)
    {
        try { await socket.SetStatusAsync(UserStatus.Invisible); } catch { }
        try { await socket.StopAsync(); } catch { }
        try { await socket.LogoutAsync(); } catch { }

        // Discord.Net can finish ConnectionManager.StartAsync shortly after StopAsync.
        // Avoid disposing its synchronization primitives while that continuation drains.
        try { await Task.Delay(250); } catch { }
    }
}
