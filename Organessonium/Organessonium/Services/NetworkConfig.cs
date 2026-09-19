using System.Net;
using System.Net.Http;
using Organessonium.Models;

namespace Organessonium.Services;

public sealed class NetworkConfig
{
    public static NetworkConfig Current { get; } = new();

    private readonly object _lock = new();
    private ProxySettings _settings = new();
    private IWebProxy? _proxy;
    private bool _useSystemProxy = true;
    private bool _direct;

    public ProxySettings Settings
    {
        get
        {
            lock (_lock)
            {
                return _settings.Clone();
            }
        }
    }

    public void Apply(ProxySettings settings)
    {
        lock (_lock)
        {
            _settings = (settings ?? new ProxySettings()).Clone();
            BuildFromLocked();
        }
    }

    public HttpClient CreateClient()
    {
        return new HttpClient(CreateHandler(), disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public HttpMessageHandler CreateHandler()
    {
        lock (_lock)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = !_direct,
                Proxy = _proxy,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            };
            return handler;
        }
    }

    public IWebProxy? CreateWebProxyForDiscordNet()
    {
        lock (_lock)
        {
            return CloneProxyForExternal(_proxy, _direct, _useSystemProxy);
        }
    }

    private void BuildFromLocked()
    {
        _direct = false;
        _useSystemProxy = true;
        _proxy = null;

        if (_settings.Mode == ProxyMode.Disabled)
        {
            _useSystemProxy = false;
            _direct = true;
            return;
        }

        if (_settings.Mode != ProxyMode.Manual)
        {
            return;
        }

        if (!_settings.IsValidManual(out _) || _settings.Port is <= 0 or > 65535)
        {
            return;
        }

        _useSystemProxy = false;
        var scheme = ProxyUriScheme(_settings.Protocol);
        _proxy = new WebProxy($"{scheme}://{_settings.Host}:{_settings.Port}");
        if (_settings.AuthenticationEnabled)
        {
            _proxy.Credentials = new NetworkCredential(_settings.Username, _settings.Password);
        }
    }

    private static string ProxyUriScheme(ProxyProtocol protocol)
    {
        return protocol switch
        {
            ProxyProtocol.Http => "http",
            ProxyProtocol.Https => "https",
            ProxyProtocol.Socks5 => "socks5",
            ProxyProtocol.Socks4a => "socks4a",
            ProxyProtocol.Socks4 => "socks4",
            _ => "http"
        };
    }

    private static IWebProxy? CloneProxyForExternal(IWebProxy? proxy, bool direct, bool useSystemProxy)
    {
        if (direct)
        {
            return new DirectProxy();
        }

        if (useSystemProxy)
        {
            return null;
        }

        return proxy;
    }

    private sealed class DirectProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) => null;

        public bool IsBypassed(Uri host) => true;
    }
}
