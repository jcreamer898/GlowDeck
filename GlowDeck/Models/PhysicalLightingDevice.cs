namespace GlowDeck.Models;

public sealed record PhysicalLightingDevice(
    string Id,
    string Name,
    string Details,
    string InstanceId,
    DeviceGroup Group);
