using System.Globalization;
using System.Management;
using GlowDeck.Models;

namespace GlowDeck.Services;

public sealed class HardwareInventoryService
{
    public Task<IReadOnlyList<PhysicalLightingDevice>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Scan, cancellationToken);

    private static IReadOnlyList<PhysicalLightingDevice> Scan()
    {
        var devices = new Dictionary<string, PhysicalLightingDevice>(StringComparer.OrdinalIgnoreCase);

        using (var searcher = new ManagementObjectSearcher(
                   "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB%'"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var instanceId = Convert.ToString(item["PNPDeviceID"], CultureInfo.InvariantCulture) ?? string.Empty;
                    var name = Convert.ToString(item["Name"], CultureInfo.InvariantCulture) ?? string.Empty;
                    AddUsbLightingDevice(devices, instanceId, name);
                }
            }
        }

        using (var searcher = new ManagementObjectSearcher(
                   "SELECT Manufacturer, PartNumber, Capacity, DeviceLocator FROM Win32_PhysicalMemory"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    var manufacturer = Convert.ToString(item["Manufacturer"], CultureInfo.InvariantCulture) ?? string.Empty;
                    var partNumber = Convert.ToString(item["PartNumber"], CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
                    if (!manufacturer.Contains("Corsair", StringComparison.OrdinalIgnoreCase) &&
                        !partNumber.StartsWith("CMW", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var slot = Convert.ToString(item["DeviceLocator"], CultureInfo.InvariantCulture)?.Trim();
                    var capacity = item["Capacity"] is null ? 0UL : Convert.ToUInt64(item["Capacity"], CultureInfo.InvariantCulture);
                    var capacityGb = capacity == 0 ? "Memory module" : $"{capacity / 1024d / 1024d / 1024d:0} GB";
                    var location = string.IsNullOrWhiteSpace(slot) ? "DIMM" : slot;
                    var id = $"corsair:{location}:{partNumber}";
                    devices[id] = new PhysicalLightingDevice(
                        id,
                        "Corsair Vengeance RGB Pro",
                        $"{partNumber}  |  {capacityGb}  |  {location}",
                        partNumber,
                        DeviceGroup.Memory);
                }
            }
        }

        return devices.Values.OrderBy(device => device.Group).ThenBy(device => device.Name).ToArray();
    }

    private static void AddUsbLightingDevice(
        IDictionary<string, PhysicalLightingDevice> devices,
        string instanceId,
        string friendlyName)
    {
        if (instanceId.Contains("VID_0B05&PID_18F3", StringComparison.OrdinalIgnoreCase))
        {
            devices.TryAdd(
                "usb:0B05:18F3",
                new PhysicalLightingDevice(
                    "usb:0B05:18F3",
                    "ASUS AURA LED Controller",
                    "ROG STRIX X570-E motherboard lighting controller",
                    "USB VID:PID 0B05:18F3",
                    DeviceGroup.Motherboard));
            return;
        }

        if (instanceId.Contains("VID_0CF2&PID_A100", StringComparison.OrdinalIgnoreCase))
        {
            devices.TryAdd(
                "usb:0CF2:A100",
                new PhysicalLightingDevice(
                    "usb:0CF2:A100",
                    "Lian Li UNI FAN SL Controller",
                    "USB VID:PID 0CF2:A100; fan lighting controller",
                    "USB VID:PID 0CF2:A100",
                    DeviceGroup.CaseLighting));
            return;
        }

        if (!instanceId.Contains("VID_1532&PID_", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var (id, model) = instanceId.Contains("PID_0099", StringComparison.OrdinalIgnoreCase)
            ? ("razer:basilisk-v3", "Razer Basilisk V3")
            : instanceId.Contains("PID_025A", StringComparison.OrdinalIgnoreCase)
                ? ("razer:blackwidow-v3-pro", "Razer BlackWidow V3 Pro")
                : instanceId.Contains("PID_007E", StringComparison.OrdinalIgnoreCase)
                    ? ("razer:mouse-dock", "Razer Mouse Dock")
                    : (string.Empty, string.Empty);

        if (id.Length == 0)
        {
            return;
        }

        var details = string.IsNullOrWhiteSpace(friendlyName) ? model : friendlyName;
        devices.TryAdd(
            id,
            new PhysicalLightingDevice(
                id,
                model,
                $"{details}  |  {id.Replace("razer:", "USB VID:PID 1532:", StringComparison.OrdinalIgnoreCase)}",
                instanceId,
                DeviceGroup.Razer));
    }
}
