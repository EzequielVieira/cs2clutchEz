using CS2HG.Core.Models;

namespace CS2HG.VideoProcessing;

public sealed record EffectContext(int Width, int Height, double Fps, double DurationSeconds);

/// <summary>
/// Extension point for future post-processing (auto zoom, motion tracking, kill overlays, music...).
/// An effect contributes an FFmpeg video filter that is appended after scaling and overlays.
/// </summary>
public interface IVideoEffect
{
    string Name { get; }

    string? BuildVideoFilter(EffectSpec spec, EffectContext context);
}

public sealed class EffectRegistry
{
    private readonly Dictionary<string, IVideoEffect> _effects = new(StringComparer.OrdinalIgnoreCase);

    public EffectRegistry(IEnumerable<IVideoEffect>? effects = null)
    {
        foreach (var e in effects ?? [new VignetteEffect(), new ColorBoostEffect()]) Register(e);
    }

    public IReadOnlyCollection<string> Names => _effects.Keys;

    public void Register(IVideoEffect effect) => _effects[effect.Name] = effect;

    public IEnumerable<string> BuildFilters(IEnumerable<EffectSpec> specs, EffectContext context)
    {
        foreach (var spec in specs)
        {
            if (_effects.TryGetValue(spec.Name, out var effect) && effect.BuildVideoFilter(spec, context) is { Length: > 0 } f)
                yield return f;
        }
    }
}

/// <summary>Darkened corners, cinematic look.</summary>
public sealed class VignetteEffect : IVideoEffect
{
    public string Name => "vignette";

    public string? BuildVideoFilter(EffectSpec spec, EffectContext context) => "vignette=PI/5";
}

/// <summary>Slight saturation/contrast boost, common in frag movies.</summary>
public sealed class ColorBoostEffect : IVideoEffect
{
    public string Name => "colorboost";

    public string? BuildVideoFilter(EffectSpec spec, EffectContext context)
    {
        var sat = spec.Parameters.TryGetValue("saturation", out var s) && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 1.25;
        return $"eq=saturation={FfmpegRunner.F(sat)}:contrast=1.05";
    }
}
