using System.Text.Json.Serialization;

namespace GlowDeck.Models;

public enum SceneEffect
{
    Solid,
    Breathing,
    ColorCycle,
    Rainbow
}

public sealed class LightingScene
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New scene";
    public SceneEffect Effect { get; set; }
    public DeviceGroup? TargetGroup { get; set; }
    public List<RgbColor> Colors { get; set; } = [new(139, 92, 246)];
    public int Speed { get; set; } = 45;
    public int Brightness { get; set; } = 75;

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public string EffectName => Effect switch
    {
        SceneEffect.Solid => "Solid color",
        SceneEffect.Breathing => "Breathing",
        SceneEffect.ColorCycle => "Color cycle",
        SceneEffect.Rainbow => "Rainbow",
        _ => "Unknown"
    };

    [JsonIgnore]
    public string TargetName => TargetGroup?.ToDisplayName() ?? "All devices";

    [JsonIgnore]
    public string Summary => $"{EffectName}  |  {TargetName}";

    [JsonIgnore]
    public string PaletteText => string.Join(", ", Colors.Select(color => color.ToHex()));
}
