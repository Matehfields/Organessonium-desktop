namespace Organessonium.Models;

internal sealed record VideoPlayerPreferences
{
    public double Volume { get; init; } = 1;
    public bool Muted { get; init; }
    public double Rate { get; init; } = 1;
    public bool Captions { get; init; }
    public int CaptionSize { get; init; } = 100;
    public string CaptionColor { get; init; } = "white";
    public int CaptionBackground { get; init; } = 70;
    public bool AutoNext { get; init; }
    public bool Repeat { get; init; }
    public bool Ambient { get; init; }
    public bool Remaining { get; init; }
    public bool Resume { get; init; } = true;
    public bool Initialized { get; init; }

    internal VideoPlayerPreferences Normalize() => this with
    {
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 1,
        Rate = double.IsFinite(Rate) ? Math.Clamp(Rate, 0.25, 3) : 1,
        CaptionSize = Math.Clamp(CaptionSize, 75, 200),
        CaptionColor = CaptionColor is "yellow" or "cyan" ? CaptionColor : "white",
        CaptionBackground = Math.Clamp(CaptionBackground, 0, 100)
    };
}
