using Discord;
using Discord.Net;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace Organessonium.Services;

public enum ConnectionErrorKind
{
    NoConnectivity,
    DnsFailure,
    TimedOut,
    ProxyUnreachable,
    ProxyRefused,
    ProxyAuthenticationRequired,
    TargetUnreachable,
    InvalidToken,
    InvalidChannelConfiguration,
    RateLimited,
    Unknown
}

public sealed record ConnectionIssue(ConnectionErrorKind Kind, string Message);

public static class ConnectionErrorClassifier
{
    private const string DiscordApiBase = "discord.com";

    public static ConnectionIssue Classify(Exception ex)
    {
        var chain = Flatten(ex);
        var texts = chain
            .SelectMany(level => new[] { level.Message ?? "", level.GetType().FullName ?? "" })
            .ToList();
        var text = string.Join(" ", texts);

        var kind = IdentifyHttpStatus(texts, ex);
        if (kind.HasValue)
        {
            return new ConnectionIssue(kind.Value, MessageFor(kind.Value));
        }

        if (ContainsAny(text, "invalid token", "improper token", "token inválido"))
            return new ConnectionIssue(ConnectionErrorKind.InvalidToken, MessageFor(ConnectionErrorKind.InvalidToken));
        if (ContainsAny(text, "Configure os canais", "precisam ser diferentes", "não é um canal de", "não encontrado ou"))
            return new ConnectionIssue(ConnectionErrorKind.InvalidChannelConfiguration, MessageFor(ConnectionErrorKind.InvalidChannelConfiguration));

        var proxyInPlay = NetworkConfig.Current.Settings.IsManualEnabled;
        var hasProxySignal =
            text.Contains("proxy", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("socks", StringComparison.OrdinalIgnoreCase);

        if (text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("task canceled", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("canceled", StringComparison.OrdinalIgnoreCase))
        {
            return new ConnectionIssue(ConnectionErrorKind.TimedOut, "A conexão expirou. Verifique sua internet ou o proxy.");
        }

        if (hasProxySignal && ContainsAny(text, "refused", "connection reset", "connection closed"))
        {
            return new ConnectionIssue(ConnectionErrorKind.ProxyRefused, "O proxy recusou a conexão.");
        }

        if (hasProxySignal && ContainsAny(text, "resolve", "nodename", "no such host", "dns", "cannot connect", "unreachable"))
        {
            return new ConnectionIssue(
                proxyInPlay ? ConnectionErrorKind.ProxyUnreachable : ConnectionErrorKind.NoConnectivity,
                proxyInPlay ? "Não foi possível conectar ao proxy." : "Sem conexão com a internet.");
        }

        if (ContainsAny(text, "refused", "connection reset", "reset by peer", "connection closed"))
        {
            return new ConnectionIssue(ConnectionErrorKind.NoConnectivity, "Sem conexão com a internet.");
        }

        if (ContainsAny(text, "nodename", "no such host", "resolve", "dns", "failed to resolve", "host not found"))
        {
            return new ConnectionIssue(ConnectionErrorKind.DnsFailure, "Não foi possível resolver o endereço (DNS).");
        }

        if (containsAuthenticationSignal(text))
        {
            return new ConnectionIssue(
                ConnectionErrorKind.ProxyAuthenticationRequired,
                "O proxy exige autenticação ou as credenciais estão incorretas.");
        }

        if (proxyInPlay)
        {
            return new ConnectionIssue(
                ConnectionErrorKind.ProxyUnreachable,
                "Não foi possível conectar ao proxy.");
        }

        return new ConnectionIssue(ConnectionErrorKind.Unknown, "Falha de rede ao falar com o Discord.");
    }

    private static ConnectionErrorKind? IdentifyHttpStatus(List<string> texts, Exception ex)
    {
        var current = (Exception?)ex;
        while (current != null)
        {
            if (current is HttpRequestException request)
            {
                if (request.StatusCode == HttpStatusCode.Unauthorized) return ConnectionErrorKind.InvalidToken;
                if (request.StatusCode == HttpStatusCode.NotFound) return ConnectionErrorKind.InvalidChannelConfiguration;
            }
            if (current is DiscordApiException discord)
            {
                if (discord.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return ConnectionErrorKind.InvalidToken;
                }

                if (discord.StatusCode == HttpStatusCode.NotFound)
                {
                    return ConnectionErrorKind.InvalidChannelConfiguration;
                }

                if (discord.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    return ConnectionErrorKind.RateLimited;
                }

                if (discord.Body.Contains("401", StringComparison.OrdinalIgnoreCase) ||
                    discord.Body.Contains("unauthorized", StringComparison.OrdinalIgnoreCase))
                {
                    return ConnectionErrorKind.InvalidToken;
                }

                if (discord.Body.Contains("404", StringComparison.OrdinalIgnoreCase) ||
                    discord.Body.Contains("unknown channel", StringComparison.OrdinalIgnoreCase))
                {
                    return ConnectionErrorKind.InvalidChannelConfiguration;
                }
            }

            if (current is HttpException http)
            {
                if (http.HttpCode == HttpStatusCode.Unauthorized)
                {
                    return ConnectionErrorKind.InvalidToken;
                }

                if (http.HttpCode == HttpStatusCode.NotFound)
                {
                    return ConnectionErrorKind.InvalidChannelConfiguration;
                }

                if (http.HttpCode == HttpStatusCode.TooManyRequests)
                {
                    return ConnectionErrorKind.RateLimited;
                }

                if (ContainsAny(http.Message, "401", "unauthorized", "invalid token", "missing access"))
                {
                    return ConnectionErrorKind.InvalidToken;
                }
            }

            if (IsProxyAuthSignal(texts))
            {
                return ConnectionErrorKind.ProxyAuthenticationRequired;
            }

            current = current.InnerException;
        }

        return null;
    }

    private static bool IsProxyAuthSignal(List<string> texts)
    {
        return texts.Any(t =>
            t.Contains("407", StringComparison.OrdinalIgnoreCase) || 
            t.Contains("proxy authentication", StringComparison.OrdinalIgnoreCase) ||
            t.Contains("socks", StringComparison.OrdinalIgnoreCase) && t.Contains("auth", StringComparison.OrdinalIgnoreCase));
    }

    private static bool containsAuthenticationSignal(string text)
    {
        return IsProxyAuthSignal([text]);
    }

    private static string MessageFor(ConnectionErrorKind kind)
    {
        return kind switch
        {
            ConnectionErrorKind.InvalidToken => "Token inválido ou expirado. Verifique o token do bot.",
            ConnectionErrorKind.InvalidChannelConfiguration => "Configuração de servidor/canais inválida. Verifique os IDs do canal de índice e do fórum.",
            ConnectionErrorKind.RateLimited => "O Discord está limitando as requisições. Tente novamente em alguns instantes.",
            ConnectionErrorKind.ProxyAuthenticationRequired => "O proxy exige autenticação ou as credenciais estão incorretas.",
            _ => "Falha de rede."
        };
    }

    internal static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static List<Exception> Flatten(Exception ex)
    {
        var output = new List<Exception>();
        var current = (Exception?)ex;
        while (current != null)
        {
            output.Add(current);
            current = current.InnerException;
        }

        return output;
    }
}
