namespace Organessonium.Services;

// Edit these values for local tests, then rebuild. Route quotas come from Discord headers.
public static class DiscordLimits
{
    public const int MessageContentCharacters = 2000;
    public const int ThreadNameCharacters = 100;
    public const int MessagesPerPage = 100;

    // Per-file upload limit, including the encrypted envelope.
    public const int AttachmentBytes = 20 * 1024 * 1024;
    public const int AttachmentsPerMessage = 10;
    public const int PlaintextChunkBytes = AttachmentBytes - EncryptionService.EnvelopeOverheadBytes;

    // Discord's global limit is per SECOND, not per minute. Leave headroom.
    public const int ApiRequestsPerSecond = 50;
    public const int TargetRequestsPerSecond = 45;
    public const int RateLimitSafetyMilliseconds = 50;
    public const int RateLimitFallbackMilliseconds = 1000;

    internal static void ValidateMessage(string content)
    {
        if (content.Length > MessageContentCharacters)
            throw new ArgumentException($"O texto excede {MessageContentCharacters} caracteres permitidos pelo Discord.", nameof(content));
    }
}
