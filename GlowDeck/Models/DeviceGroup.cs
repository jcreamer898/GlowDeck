namespace GlowDeck.Models;

public enum DeviceGroup
{
    CaseLighting,
    Motherboard,
    Memory,
    Razer,
    Other
}

public static class DeviceGroupExtensions
{
    public static string ToDisplayName(this DeviceGroup group) => group switch
    {
        DeviceGroup.CaseLighting => "Case lighting",
        DeviceGroup.Motherboard => "Motherboard",
        DeviceGroup.Memory => "Memory",
        DeviceGroup.Razer => "Razer devices",
        _ => "Other devices"
    };

    public static bool IsInInitialControlScope(this DeviceGroup group) =>
        group is DeviceGroup.CaseLighting or DeviceGroup.Motherboard or DeviceGroup.Memory;
}
