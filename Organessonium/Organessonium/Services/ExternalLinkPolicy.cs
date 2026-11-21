namespace Organessonium.Services;

internal static class ExternalLinkPolicy
{
    private const int MaximumUrlLength = 2048;

    /// <summary>
    /// Aceita somente links absolutos que podem ser abertos pelo navegador
    /// padrão. Conteúdo local e esquemas desconhecidos não saem do aplicativo.
    /// </summary>
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = "";
        var raw = value?.Trim();
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaximumUrlLength)
        {
            return false;
        }

        foreach (var character in raw)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        {
            if (string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            normalized = uri.AbsoluteUri;
            return normalized.Length <= MaximumUrlLength;
        }

        if (uri.Scheme == Uri.UriSchemeMailto)
        {
            normalized = raw;
            return true;
        }

        return false;
    }
}
