using GlowDeck.Models;

namespace GlowDeck.Services;

public static class SceneAnimator
{
    public static IReadOnlyList<RgbColor> Render(LightingScene scene, int ledCount, TimeSpan elapsed)
    {
        if (ledCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ledCount), "A lighting zone must contain at least one LED.");
        }

        var colors = scene.Colors.Count == 0 ? [new RgbColor(255, 255, 255)] : scene.Colors;
        var brightness = Math.Clamp(scene.Brightness, 0, 100) / 100d;
        var speed = 0.08d + (Math.Clamp(scene.Speed, 1, 100) / 100d * 0.9d);
        var seconds = elapsed.TotalSeconds;
        var frame = new RgbColor[ledCount];

        switch (scene.Effect)
        {
            case SceneEffect.Solid:
                Array.Fill(frame, colors[0].Scale(brightness));
                break;

            case SceneEffect.Breathing:
                var wave = (Math.Sin(seconds * speed * Math.Tau) + 1d) / 2d;
                var intensity = brightness * (0.12d + (wave * 0.88d));
                Array.Fill(frame, colors[0].Scale(intensity));
                break;

            case SceneEffect.ColorCycle:
                var position = (seconds * speed) % colors.Count;
                var firstIndex = (int)position;
                var secondIndex = (firstIndex + 1) % colors.Count;
                var color = RgbColor.Lerp(colors[firstIndex], colors[secondIndex], position - firstIndex).Scale(brightness);
                Array.Fill(frame, color);
                break;

            case SceneEffect.Rainbow:
                for (var index = 0; index < frame.Length; index++)
                {
                    var hue = ((double)index / Math.Max(frame.Length, 1)) + (seconds * speed * 0.3d);
                    frame[index] = RgbColor.FromHsv(hue, 0.9d, brightness);
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scene), "The selected lighting effect is not supported.");
        }

        return frame;
    }
}
