using System.IO;
using System.Net.Http;

namespace Organessonium.Services;

internal sealed class DiscordAttachmentHandler(HttpMessageHandler transport) : DelegatingHandler(transport)
{
    private const int MaxRedirects = 5;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var destination = request.RequestUri;
        for (var redirects = 0; ; redirects++)
        {
            if (!IsAllowed(destination))
                throw new InvalidDataException("Destino de anexo do Discord inválido.");

            using var next = new HttpRequestMessage(request.Method, destination)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers)
            {
                if (!SensitiveHeaders.Contains(header.Key))
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            var response = await base.SendAsync(next, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
                return response;

            using (response)
            {
                if (redirects == MaxRedirects || response.Headers.Location is not Uri location)
                    throw new InvalidDataException("Redirecionamento de anexo do Discord inválido.");
                destination = new Uri(destination!, location);
            }
        }
    }

    private static bool IsAllowed(Uri? uri) =>
        uri is { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo.Length: 0 } &&
        uri.IdnHost is "cdn.discordapp.com" or "media.discordapp.net" or "cdn.discord.com";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Proxy-Authorization", "Host"
    };
}
