using System.Linq;

namespace Iris_MediaPlayer.Services;

internal sealed record MpvTrack(long Id, string Type, string? Title, string? Language, bool IsSelected)
{
    public string DisplayName
    {
        get
        {
            var details = string.Join(" · ", new[] { Language, Title }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct());
            return string.IsNullOrWhiteSpace(details) ? $"Track {Id}" : details;
        }
    }
}
