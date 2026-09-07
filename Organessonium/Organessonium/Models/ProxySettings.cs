using System.Text.Json.Serialization;

namespace Organessonium.Models;

public enum ProxyMode
{
    [JsonPropertyName("automatic")]
    Automatic = 0,

    [JsonPropertyName("disabled")]
    Disabled = 1,

    [JsonPropertyName("manual")]
    Manual = 2
}

public enum ProxyProtocol
{
    [JsonPropertyName("auto")]
    Auto = 0,

    [JsonPropertyName("http")]
    Http = 1,

    [JsonPropertyName("https")]
    Https = 2,

    [JsonPropertyName("socks5")]
    Socks5 = 3,

    [JsonPropertyName("socks4a")]
    Socks4a = 4,

    [JsonPropertyName("socks4")]
    Socks4 = 5
}

public sealed class ProxySettings
{
    [JsonPropertyName("proxyMode")]
    public ProxyMode Mode { get; set; } = ProxyMode.Automatic;

    [JsonPropertyName("proxyHost")]
    public string Host { get; set; } = "";

    [JsonPropertyName("proxyPort")]
    public int Port { get; set; }

    [JsonPropertyName("proxyProtocol")]
    public ProxyProtocol Protocol { get; set; } = ProxyProtocol.Auto;

    [JsonPropertyName("proxyAuthenticationEnabled")]
    public bool AuthenticationEnabled { get; set; }

    [JsonPropertyName("proxyUsername")]
    public string Username { get; set; } = "";

    [JsonPropertyName("proxyPassword")]
    public string Password { get; set; } = "";

    public bool IsManualEnabled => Mode == ProxyMode.Manual;

    public bool IsValidManual(out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(Host))
        {
            error = "Informe o endereço do proxy.";
            return false;
        }

        if (Port is <= 0 or > 65535)
        {
            error = "Informe uma porta válida (1 a 65535).";
            return false;
        }

        if (AuthenticationEnabled && string.IsNullOrWhiteSpace(Username))
        {
            error = "Informe o usuário do proxy.";
            return false;
        }

        return true;
    }

    public ProxySettings Clone()
    {
        return new ProxySettings
        {
            Mode = Mode,
            Host = Host,
            Port = Port,
            Protocol = Protocol,
            AuthenticationEnabled = AuthenticationEnabled,
            Username = Username,
            Password = Password
        };
    }
}