using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using Organessonium.Models;

namespace Organessonium.Services;

public enum ProxyTestErrorKind
{
    None,
    InvalidConfiguration,
    ProxyUnreachable,
    ProxyRefused,
    ProxyAuthenticationRequired,
    TargetUnreachable,
    TimedOut,
    NoProtocolDetected
}

public sealed record ProxyTestResult(
    bool Success,
    bool Detected,
    ProxyProtocol? DetectedProtocol,
    ProxyTestErrorKind Kind,
    string Message);

public static class ProxyTester
{
    private const string DiscordProbeUrl = "https://discord.com/api/v10/gateway";
    private const int PerAttemptTimeoutMs = 8000;
    private const int DetectOverallTimeoutMs = 20000;

    public static string AssertValidManual(ProxySettings settings)
    {
        return settings.IsValidManual(out var error) && settings.Port is > 0 and <= 65535
            ? ""
            : error;
    }

    public static Task<ProxyTestResult> TestAsync(
        ProxySettings settings,
        bool includeDiscordCheck,
        CancellationToken cancellationToken = default)
    {
        return TestAsync(settings, includeDiscordCheck, cancellationToken, null);
    }

internal static async Task<ProxyTestResult> TestAsync(
        ProxySettings settings,
        bool includeDiscordCheck,
        CancellationToken cancellationToken,
        string? probeUrl)
    {
        if (settings.Mode != ProxyMode.Manual || settings.Port is <= 0 or > 65535)
        {
            return new ProxyTestResult(
                false,
                false,
                null,
                ProxyTestErrorKind.InvalidConfiguration,
                settings.IsValidManual(out var error) ? "Configuração de proxy inválida." : error);
        }

        if (settings.Protocol != ProxyProtocol.Auto)
        {
            var (ok, kind, message) = await TryProbeAsync(settings, settings.Protocol, cancellationToken, probeUrl);
            if (!ok)
            {
                return new ProxyTestResult(false, false, null, kind, message);
            }

            if (includeDiscordCheck)
            {
                var (discordOk, discordKind, discordMessage) = await TryProbeAsync(settings, settings.Protocol, cancellationToken, probeUrl ?? DiscordProbeUrl);
                if (!discordOk)
                {
                    return new ProxyTestResult(
                        false,
                        false,
                        settings.Protocol,
                        discordKind == ProxyTestErrorKind.ProxyAuthenticationRequired
                            ? ProxyTestErrorKind.ProxyAuthenticationRequired
                            : ProxyTestErrorKind.TargetUnreachable,
                        "Proxy conectado, mas o Discord nÃ£o pÃ´de ser acessado via este proxy.");
                }
            }

            return new ProxyTestResult(true, false, settings.Protocol, ProxyTestErrorKind.None, "Conectado com sucesso.");
        }

        return await DetectAsync(settings, includeDiscordCheck, cancellationToken, probeUrl);
    }

    private static async Task<ProxyTestResult> DetectAsync(ProxySettings settings, bool includeDiscordCheck, CancellationToken cancellationToken, string? probeUrl)
    {
        var candidates = new[]
        {
            ProxyProtocol.Socks5,
            ProxyProtocol.Http,
            ProxyProtocol.Https
        };

        var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(DetectOverallTimeoutMs);
        try
        {
            var tasks = candidates.Select(protocol => Task.Run(
                () => TryProbeAsync(settings, protocol, overall.Token, probeUrl),
                overall.Token));
            var results = await Task.WhenAll(tasks);

            foreach (var pair in results.Select((value, index) => (Value: value, Index: index)))
            {
                var (ok, _, _) = pair.Value;
                if (ok)
                {
                    var protocol = candidates[pair.Index];
                    if (includeDiscordCheck)
                    {
                        var (discordOk, _, _) = await TryProbeAsync(
                            settings,
                            protocol,
                            overall.Token,
                            probeUrl ?? DiscordProbeUrl);
                        if (discordOk)
                        {
                            return new ProxyTestResult(
                                true,
                                true,
                                protocol,
                                ProxyTestErrorKind.None,
                                "Conectado com sucesso (protocolo detectado: " + ProtocolLabel(protocol) + ").");
                        }
                    }
                    else
                    {
                        return new ProxyTestResult(
                            true,
                            true,
                            protocol,
                            ProxyTestErrorKind.None,
                            "Conectado com sucesso (protocolo detectado: " + ProtocolLabel(protocol) + ").");
                    }
                }
            }

            var firstFailure = results.FirstOrDefault(r => !r.Ok);
            if (firstFailure.Ok == false &&
                firstFailure.Kind == ProxyTestErrorKind.ProxyAuthenticationRequired)
            {
                return new ProxyTestResult(
                    false,
                    true,
                    null,
                    ProxyTestErrorKind.ProxyAuthenticationRequired,
                    "AutenticaÃ§Ã£o necessÃ¡ria ou incorreta.");
            }

            return new ProxyTestResult(
                false,
                true,
                null,
                ProxyTestErrorKind.NoProtocolDetected,
                "NÃ£o foi possÃ­vel detectar automaticamente o tipo de proxy.");
        }
        catch (OperationCanceledException) when (overall.IsCancellationRequested)
        {
            return new ProxyTestResult(
                false,
                true,
                null,
                ProxyTestErrorKind.TimedOut,
                "A detecÃ§Ã£o automÃ¡tica expirou.");
        }
        finally
        {
            overall.Dispose();
        }
    }

    private static async Task<(bool Ok, ProxyTestErrorKind Kind, string Message)> TryProbeAsync(
        ProxySettings settings,
        ProxyProtocol protocol,
        CancellationToken cancellationToken,
        string? url = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(PerAttemptTimeoutMs);

        var config = settings.Clone();
        config.Protocol = protocol;
        var network = new NetworkConfig();
        network.Apply(config);

        try
        {
            using var http = network.CreateClient();
            http.Timeout = Timeout.InfiniteTimeSpan;
            using var response = await http.GetAsync(url ?? DiscordProbeUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if ((int)response.StatusCode >= 500)
            {
                return (false, ProxyTestErrorKind.TargetUnreachable, "O proxy conectou, mas o destino não respondeu (HTTP " + (int)response.StatusCode + ").");
            }

            return (true, ProxyTestErrorKind.None, "");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return (false, ProxyTestErrorKind.TimedOut, "O proxy nÃ£o respondeu no tempo esperado.");
        }
        catch (Exception ex)
        {
            var kind = ClassifyProxyFailure(ex);
            return (false, kind, ExMessageFor(kind, protocol));
        }
    }

    internal static ProxyTestErrorKind ClassifyProxyFailure(Exception ex)
    {
        var current = (Exception?)ex;
        while (current != null)
        {
            var full = current.GetType().FullName ?? "";
            var message = current.Message ?? "";

            if (current is SocketException sock)
            {
                if (sock.SocketErrorCode == SocketError.ConnectionRefused ||
                    sock.SocketErrorCode == SocketError.ConnectionReset ||
                    sock.SocketErrorCode == SocketError.ConnectionAborted)
                {
                    return ProxyTestErrorKind.ProxyRefused;
                }

                if (sock.SocketErrorCode == SocketError.HostNotFound ||
                    sock.SocketErrorCode == SocketError.NoData ||
                    sock.SocketErrorCode == SocketError.TryAgain)
                {
                    return ProxyTestErrorKind.ProxyUnreachable;
                }

                if (sock.SocketErrorCode == SocketError.TimedOut)
                {
                    return ProxyTestErrorKind.TimedOut;
                }
            }

            if (current is HttpRequestException hre)
            {
                var status = hre.StatusCode;
                if (status == HttpStatusCode.ProxyAuthenticationRequired)
                {
                    return ProxyTestErrorKind.ProxyAuthenticationRequired;
                }

                if (hre.InnerException is AuthenticationException)
                {
                    return ProxyTestErrorKind.ProxyAuthenticationRequired;
                }

                if (hre.InnerException is OperationCanceledException)
                {
                    return ProxyTestErrorKind.TimedOut;
                }
            }

            if (current is AuthenticationException)
            {
                return ProxyTestErrorKind.ProxyAuthenticationRequired;
            }

            if (message.Contains("407", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("proxy authentication", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("socks", StringComparison.OrdinalIgnoreCase) && message.Contains("auth", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyTestErrorKind.ProxyAuthenticationRequired;
            }

            if (message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection aborted", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("anulada", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyTestErrorKind.ProxyRefused;
            }

            if (message.Contains("premature", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("unexpected end", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("connection closed", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("ended prematurely", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyTestErrorKind.ProxyRefused;
            }

            if (message.Contains("nodename nor servname provided", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("no such host", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("failed to resolve", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("enterprise", StringComparison.OrdinalIgnoreCase) && message.Contains("host", StringComparison.OrdinalIgnoreCase))
            {
                return ProxyTestErrorKind.ProxyUnreachable;
            }

            if (full.IndexOf("SocketsHttpHandler", StringComparison.OrdinalIgnoreCase) >= 0 ||
                full.IndexOf("SocksException", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (message.Contains("connection", StringComparison.OrdinalIgnoreCase) &&
                    message.Contains("refused", StringComparison.OrdinalIgnoreCase))
                {
                    return ProxyTestErrorKind.ProxyRefused;
                }

                return ProxyTestErrorKind.ProxyUnreachable;
            }

            current = current.InnerException;
        }

        return ProxyTestErrorKind.ProxyUnreachable;
    }

    private static string ExMessageFor(ProxyTestErrorKind kind, ProxyProtocol protocol)
    {
        return kind switch
        {
            ProxyTestErrorKind.ProxyRefused => "O proxy recusou a conexÃ£o.",
            ProxyTestErrorKind.ProxyAuthenticationRequired => "AutenticaÃ§Ã£o necessÃ¡ria ou incorreta.",
            ProxyTestErrorKind.TimedOut => "O proxy nÃ£o respondeu no tempo esperado.",
            ProxyTestErrorKind.ProxyUnreachable => "NÃ£o foi possÃ­vel conectar ao proxy (" + ProtocolLabel(protocol) + ").",
            _ => "Falha ao conectar ao proxy."
        };
    }

    private static string ProtocolLabel(ProxyProtocol protocol)
    {
        return protocol switch
        {
            ProxyProtocol.Http => "HTTP",
            ProxyProtocol.Https => "HTTPS/CONNECT",
            ProxyProtocol.Socks5 => "SOCKS5",
            ProxyProtocol.Socks4a => "SOCKS4a",
            ProxyProtocol.Socks4 => "SOCKS4",
            _ => "automÃ¡tico"
        };
    }
}