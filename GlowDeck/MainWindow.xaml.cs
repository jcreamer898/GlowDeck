using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GlowDeck.Models;
using GlowDeck.Services;

namespace GlowDeck;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<LightingDevice> _devices = [];
    private readonly ObservableCollection<LightingScene> _scenes = [];
    private readonly HardwareInventoryService _hardwareInventory = new();
    private readonly OpenRgbBackendManager _openRgbBackendManager = new();
    private readonly SceneStore _sceneStore = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ICollectionView _deviceView;
    private readonly DispatcherTimer _animationTimer;

    private OpenRgbSdkClient? _openRgbClient;
    private LightingScene? _activeScene;
    private IReadOnlyList<LightingDevice> _activeTargets = [];
    private long _animationStartedAt;
    private bool _frameInProgress;
    private bool _isClosing;

    private const int LianLiLedsPerFan = 16;

    public MainWindow()
    {
        InitializeComponent();

        DeviceList.ItemsSource = _devices;
        ScenesList.ItemsSource = _scenes;
        _deviceView = CollectionViewSource.GetDefaultView(_devices);
        _deviceView.Filter = IsVisibleDevice;
        DeviceList.ItemsSource = _deviceView;

        foreach (var scene in SceneStore.BuiltInScenes)
        {
            _scenes.Add(scene);
        }

        _animationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _animationTimer.Tick += AnimationTimer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadCustomScenesAsync();
        ScenesList.SelectedIndex = 0;
        await RefreshDevicesAsync();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        e.Cancel = true;
        _isClosing = true;
        _lifetimeCancellation.Cancel();
        StopAnimation();
        if (_openRgbClient is not null)
        {
            _openRgbClient.SessionInvalidated -= OpenRgbClient_SessionInvalidated;
            await _openRgbClient.DisposeAsync();
            _openRgbClient = null;
        }

        await _openRgbBackendManager.DisposeAsync();
        _lifetimeCancellation.Dispose();
        Close();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDevicesAsync();

    private async void InstallBackendButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            "GlowDeck will download the official OpenRGB 1.0 portable release (about 22 MB) from Codeberg and store it under your user profile. GlowDeck will start it as a background SDK process only while GlowDeck is open; it will not add a startup task or install a driver.\n\n" +
            "Discovery does not send lighting commands. You still need to press Apply to change colors. OpenRGB may require the separate PawnIO driver and administrator or service access for RAM and some motherboard lighting; GlowDeck will not install or elevate that driver automatically.\n\n" +
            "Continue with the download?",
            "Set up RGB backend",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        InstallBackendButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        BackendStatusText.Text = "Downloading OpenRGB 1.0...";
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (!_isClosing)
                {
                    BackendStatusText.Text = $"Downloading OpenRGB 1.0 ({value:P0})";
                }
            });
            await OpenRgbBackendInstaller.InstallPortableAsync(
                progress,
                _lifetimeCancellation.Token);
            InstallBackendButton.Visibility = Visibility.Collapsed;
            ActionStatusText.Text = "OpenRGB is installed. Starting the managed background backend...";
            await RefreshDevicesAsync();
        }
        catch (HttpRequestException exception)
        {
            ActionStatusText.Text = $"Could not download OpenRGB: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            ActionStatusText.Text = $"The OpenRGB release archive was invalid: {exception.Message}";
        }
        catch (IOException exception)
        {
            ActionStatusText.Text = $"Could not install the OpenRGB backend: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            ActionStatusText.Text = $"Could not install the OpenRGB backend: {exception.Message}";
        }
        catch (CryptographicException exception)
        {
            ActionStatusText.Text = $"OpenRGB download verification failed: {exception.Message}";
        }
        catch (OperationCanceledException) when (_isClosing)
        {
        }
        catch (OperationCanceledException exception)
        {
            ActionStatusText.Text = $"OpenRGB download timed out: {exception.Message}";
        }
        finally
        {
            InstallBackendButton.IsEnabled = true;
            if (!_isClosing)
            {
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private async Task RefreshDevicesAsync()
    {
        RefreshButton.IsEnabled = false;
        ActionStatusText.Text = "Scanning connected lighting hardware. No RGB commands are sent during discovery.";
        StopAnimation();

        try
        {
            if (_openRgbClient is not null)
            {
                _openRgbClient.SessionInvalidated -= OpenRgbClient_SessionInvalidated;
                await _openRgbClient.DisposeAsync();
                _openRgbClient = null;
            }

            IReadOnlyList<PhysicalLightingDevice> physicalDevices = [];
            string? inventoryWarning = null;
            try
            {
                physicalDevices = await _hardwareInventory.ScanAsync();
            }
            catch (ManagementException exception)
            {
                inventoryWarning = $"Windows hardware inventory failed: {exception.Message}";
            }
            catch (UnauthorizedAccessException exception)
            {
                inventoryWarning = $"Windows hardware inventory is unavailable: {exception.Message}";
            }

            string? backendWarning = null;
            try
            {
                _openRgbClient = await _openRgbBackendManager.ConnectAsync(_lifetimeCancellation.Token);
                _openRgbClient.SessionInvalidated += OpenRgbClient_SessionInvalidated;
                InstallBackendButton.Visibility = Visibility.Collapsed;
            }
            catch (FileNotFoundException exception)
            {
                backendWarning = exception.Message;
                InstallBackendButton.Visibility = Visibility.Visible;
            }
            catch (SocketException exception)
            {
                backendWarning = $"OpenRGB SDK server is not available at 127.0.0.1:6742 ({exception.SocketErrorCode}).";
            }
            catch (IOException exception)
            {
                backendWarning = $"OpenRGB SDK connection failed: {exception.Message}";
            }
            catch (TimeoutException exception)
            {
                backendWarning = $"OpenRGB SDK did not respond: {exception.Message}";
            }
            catch (NotSupportedException exception)
            {
                backendWarning = exception.Message;
            }
            catch (InvalidOperationException exception)
            {
                backendWarning = exception.Message;
            }
            catch (Win32Exception exception)
            {
                backendWarning = $"OpenRGB could not start: {exception.Message}";
            }
            catch (InvalidDataException exception)
            {
                backendWarning = $"OpenRGB returned invalid device data: {exception.Message}";
            }
            catch (OperationCanceledException) when (_isClosing)
            {
                return;
            }

            RebuildDeviceList(physicalDevices, _openRgbClient?.Controllers ?? []);
            if (_openRgbClient is null)
            {
                BackendStatusText.Text = "OpenRGB SDK offline";
                BackendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(224, 184, 107));
                ActionStatusText.Text = inventoryWarning ?? backendWarning ??
                    "Hardware is visible, but RGB controls are unavailable until a compatible backend is connected. No hardware was changed.";
            }
            else
            {
                if (_openRgbClient.IsSessionValid)
                {
                    var backendKind = _openRgbBackendManager.IsManagedBackendRunning ? "managed" : "connected";
                    BackendStatusText.Text = $"OpenRGB {backendKind} ({_openRgbClient.Controllers.Count} controllers)";
                    BackendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(94, 214, 164));
                    ActionStatusText.Text = inventoryWarning ??
                        "OpenRGB is connected. RGB commands are only sent when you press an Apply button.";
                }
                else
                {
                    MarkControllerDevicesReadOnly(_openRgbClient);
                    BackendStatusText.Text = "OpenRGB refresh required";
                    BackendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(224, 184, 107));
                    ActionStatusText.Text = _openRgbClient.InvalidationReason ??
                        "The OpenRGB device list changed. Refresh devices before sending more lighting commands.";
                }
            }
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _deviceView.Refresh();
            UpdateDeviceCount();
        }
    }

    private void RebuildDeviceList(
        IReadOnlyList<PhysicalLightingDevice> physicalDevices,
        IReadOnlyList<OpenRgbController> controllers)
    {
        _devices.Clear();
        var matchedPhysicalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var controller in controllers)
        {
            var group = ClassifyController(controller);
            foreach (var physical in physicalDevices.Where(device => MatchesController(device, controller, group)))
            {
                matchedPhysicalIds.Add(physical.Id);
            }

            var zones = controller.Zones.Where(zone => zone.LedCount > 0).ToArray();
            if (zones.Length > 1)
            {
                foreach (var zone in zones)
                {
                    AddControllerDevice(controller, group, zone, zones.Length);
                }
            }
            else if (zones.Length == 1)
            {
                AddControllerDevice(controller, group, zones[0], 1);
            }
            else
            {
                _devices.Add(new LightingDevice
                {
                    Id = $"openrgb:{controller.DeviceId}",
                    Name = controller.Name,
                    Details = string.IsNullOrWhiteSpace(controller.Vendor) ? "OpenRGB controller" : controller.Vendor,
                    Status = GetControllerStatus(controller, group),
                    Group = group,
                    ControllerId = controller.DeviceId,
                    LedCount = controller.LedCount,
                    SupportsControl = group.IsInInitialControlScope()
                });
            }
        }

        var backendConnected = _openRgbClient is not null;
        foreach (var physical in physicalDevices)
        {
            if (matchedPhysicalIds.Contains(physical.Id))
            {
                continue;
            }

            _devices.Add(new LightingDevice
            {
                Id = physical.Id,
                Name = physical.Name,
                Details = physical.Details,
                Status = GetPhysicalDeviceStatus(physical, backendConnected),
                Group = physical.Group
            });
        }

        _deviceView.Refresh();
        UpdateDeviceCount();
        UpdateLianLiSetupControls(controllers);
    }

    private void UpdateLianLiSetupControls(IReadOnlyList<OpenRgbController> controllers)
    {
        var controller = controllers.FirstOrDefault(IsLianLiSlHub);
        LianLiSetupPanel.Visibility = controller is null ? Visibility.Collapsed : Visibility.Visible;
        if (controller is null)
        {
            ConfigureLianLiButton.IsEnabled = false;
            return;
        }

        var controls = new (int ZoneIndex, int ChannelNumber, ComboBox FanCount)[]
        {
            (0, 1, LianLiChannel1FanCountCombo),
            (1, 2, LianLiChannel2FanCountCombo),
            (3, 4, LianLiChannel4FanCountCombo)
        };
        var currentSizes = new List<string>(controls.Length);
        var supportedLayout = true;
        foreach (var (zoneIndex, channelNumber, fanCountCombo) in controls)
        {
            var zone = controller.Zones.FirstOrDefault(candidate => candidate.Index == zoneIndex);
            if (zone is null)
            {
                currentSizes.Add($"Channel {channelNumber}: unavailable");
                supportedLayout = false;
                continue;
            }

            if (zone.LedCount == 0)
            {
                currentSizes.Add($"Channel {channelNumber}: not configured");
                continue;
            }

            if (zone.LedCount % LianLiLedsPerFan != 0 ||
                zone.LedCount / LianLiLedsPerFan is < 1 or > 4)
            {
                currentSizes.Add($"Channel {channelNumber}: unexpected LED count ({zone.LedCount})");
                supportedLayout = false;
                continue;
            }

            var fanCount = zone.LedCount / LianLiLedsPerFan;
            fanCountCombo.SelectedIndex = fanCount - 1;
            currentSizes.Add($"Channel {channelNumber}: {fanCount} fan(s)");
        }

        LianLiSetupStatusText.Text =
            $"SignalRGB shows three fans on Channels 1, 2, and 4. OpenRGB reports {string.Join(", ", currentSizes)}.";
        ConfigureLianLiButton.IsEnabled = supportedLayout && _openRgbClient?.IsSessionValid == true;
    }

    private void OpenRgbClient_SessionInvalidated(object? sender, EventArgs e)
    {
        if (sender is not OpenRgbSdkClient client)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || !ReferenceEquals(_openRgbClient, client))
            {
                return;
            }

            StopAnimation();
            MarkControllerDevicesReadOnly(client);
            BackendStatusText.Text = "OpenRGB refresh required";
            BackendStatusText.Foreground = new SolidColorBrush(Color.FromRgb(224, 184, 107));
            ActionStatusText.Text = client.InvalidationReason ??
                "The OpenRGB session changed. Refresh devices before sending more lighting commands.";
        });
    }

    private void MarkControllerDevicesReadOnly(OpenRgbSdkClient client)
    {
        var reason = client.InvalidationReason ??
            "The OpenRGB session changed. Refresh devices before sending more lighting commands.";
        foreach (var device in _devices.Where(device => device.ControllerId.HasValue && device.SupportsControl))
        {
            device.IsSessionValid = false;
            device.Status = reason;
        }

        _deviceView.Refresh();
        UpdateDeviceCount();
        UpdateLianLiSetupControls(client.Controllers);
    }

    private void AddControllerDevice(
        OpenRgbController controller,
        DeviceGroup group,
        OpenRgbZone zone,
        int zoneCount)
    {
        var name = string.IsNullOrWhiteSpace(zone.Name)
            ? zoneCount > 1 ? $"{controller.Name} - Zone {zone.Index + 1}" : controller.Name
            : zoneCount > 1 ? $"{controller.Name} - {zone.Name}" : controller.Name;

        _devices.Add(new LightingDevice
        {
            Id = $"openrgb:{controller.DeviceId}:zone:{zone.Index}",
            Name = name,
            Details = string.IsNullOrWhiteSpace(controller.Vendor) ? "OpenRGB zone" : controller.Vendor,
            Status = GetControllerStatus(controller, group),
            Group = group,
            ControllerId = controller.DeviceId,
            ZoneIndex = zone.Index,
            LedCount = zone.LedCount,
            SupportsControl = group.IsInInitialControlScope()
        });
    }

    private static DeviceGroup ClassifyController(OpenRgbController controller)
    {
        var identity = $"{controller.Name} {controller.Vendor} {controller.Description}";
        if (ContainsAny(identity, "lian li", "lianli", "uni fan", "fan controller", "commander core", "commander pro", "lighting node", "case lighting"))
        {
            return DeviceGroup.CaseLighting;
        }

        if (ContainsAny(identity, "vengeance", "memory", "ram"))
        {
            return DeviceGroup.Memory;
        }

        if (ContainsAny(identity, "asus", "aura", "motherboard"))
        {
            return DeviceGroup.Motherboard;
        }

        if (ContainsAny(identity, "razer", "basilisk", "blackwidow", "mouse dock"))
        {
            return DeviceGroup.Razer;
        }

        return DeviceGroup.Other;
    }

    private static bool IsLianLiSlHub(OpenRgbController controller)
    {
        var identity = $"{controller.Name} {controller.Vendor} {controller.Description}";
        return ContainsAny(identity, "lian li uni hub - sl", "lian li uni hub sl", "lianli uni hub sl");
    }

    private static string GetControllerStatus(OpenRgbController controller, DeviceGroup group)
    {
        if (group == DeviceGroup.Razer)
        {
            return "Razer lighting control is deferred; GlowDeck will not send commands to this device yet.";
        }

        if (group == DeviceGroup.Other)
        {
            return "This device is outside the initial case-device control scope.";
        }

        return controller.LedCount > 0
            ? "Connected through the OpenRGB SDK."
            : "OpenRGB reports no controllable LEDs.";
    }

    private static bool MatchesController(
        PhysicalLightingDevice physical,
        OpenRgbController controller,
        DeviceGroup controllerGroup)
    {
        if (physical.Group != controllerGroup)
        {
            return false;
        }

        var identity = $"{controller.Name} {controller.Vendor} {controller.Description}";
        return physical.Group switch
        {
            DeviceGroup.Memory => ContainsAny(identity, "corsair", "vengeance", "memory", "ram"),
            DeviceGroup.Motherboard => ContainsAny(identity, "asus", "aura", "motherboard"),
            DeviceGroup.CaseLighting => ContainsAny(identity, "lian li", "lianli", "uni fan", "fan controller"),
            DeviceGroup.Razer => physical.Id switch
            {
                "razer:basilisk-v3" => ContainsAny(identity, "basilisk"),
                "razer:blackwidow-v3-pro" => ContainsAny(identity, "blackwidow"),
                "razer:mouse-dock" => ContainsAny(identity, "mouse dock"),
                _ => false
            },
            _ => false
        };
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static string GetPhysicalDeviceStatus(PhysicalLightingDevice device, bool backendConnected)
    {
        if (device.Id.Equals("usb:0CF2:A100", StringComparison.OrdinalIgnoreCase))
        {
            return backendConnected
                ? "OpenRGB 1.0 supports UNI HUB SL v1.8, but the backend did not enumerate this controller."
                : "OpenRGB 1.0 supports UNI HUB SL v1.8; set up or connect the backend to enable control.";
        }

        if (device.Group is DeviceGroup.Memory or DeviceGroup.Motherboard)
        {
            var exposure = backendConnected
                ? "Detected by Windows, but not exposed by OpenRGB."
                : "Detected by Windows; control needs OpenRGB 1.0.";
            return $"{exposure} This device may require PawnIO and administrator or service access.";
        }

        if (device.Group == DeviceGroup.Razer)
        {
            return "Razer lighting control is deferred; GlowDeck will not send commands to this device yet.";
        }

        return backendConnected
            ? "Detected by Windows, but not exposed by the connected RGB backend."
            : "Detected by Windows. Connect a compatible RGB backend to enable control.";
    }

    private bool IsVisibleDevice(object item)
    {
        if (item is not LightingDevice device)
        {
            return false;
        }

        var group = GetSelectedGroup();
        return group is null || device.Group == group;
    }

    private void GroupFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_deviceView is null)
        {
            return;
        }

        _deviceView.Refresh();
        UpdateDeviceCount();
    }

    private DeviceGroup? GetSelectedGroup()
    {
        if (GroupFilterCombo.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string value ||
            !Enum.TryParse<DeviceGroup>(value, out var group))
        {
            return null;
        }

        return group;
    }

    private void UpdateDeviceCount()
    {
        var visible = _deviceView.Cast<object>().OfType<LightingDevice>().ToArray();
        var visibleCount = visible.Length;
        var readyCount = visible.Count(device => device.IsControllable);
        DeviceCountText.Text = $"{visibleCount} shown  |  {readyCount} ready for control";
    }

    private void SelectVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _deviceView.Cast<object>().OfType<LightingDevice>())
        {
            device.IsSelected = device.IsControllable;
        }
    }

    private void SelectAllReady_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _devices)
        {
            device.IsSelected = device.IsControllable;
        }
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var device in _devices)
        {
            device.IsSelected = false;
        }
    }

    private void PaletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string color })
        {
            ColorHexText.Text = color;
        }
    }

    private void ColorHexText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ColorPreview is null)
        {
            return;
        }

        if (RgbColor.TryParse(ColorHexText.Text, out var color))
        {
            ColorPreview.Background = ToBrush(color);
        }
        else
        {
            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(120, 54, 54));
        }
    }

    private void AddColor_Click(object sender, RoutedEventArgs e)
    {
        if (!RgbColor.TryParse(ColorHexText.Text, out var color))
        {
            ActionStatusText.Text = "Enter a six-digit hex color such as #8B5CF6.";
            return;
        }

        var existing = PaletteTextBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!existing.Contains(color.ToHex(), StringComparer.OrdinalIgnoreCase))
        {
            PaletteTextBox.Text = string.Join(", ", existing.Append(color.ToHex()));
        }

        ActionStatusText.Text = $"{color.ToHex()} added to the scene palette.";
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SpeedValueText is not null)
        {
            SpeedValueText.Text = Math.Round(e.NewValue).ToString();
        }
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessValueText is not null)
        {
            BrightnessValueText.Text = $"{Math.Round(e.NewValue)}%";
        }
    }

    private async void ConfigureLianLiButton_Click(object sender, RoutedEventArgs e)
    {
        if (_openRgbClient is null || !_openRgbClient.IsSessionValid)
        {
            ActionStatusText.Text = "Refresh OpenRGB before configuring the Lian Li fan channels. No RGB commands were sent.";
            return;
        }

        var controller = _openRgbClient.Controllers.FirstOrDefault(IsLianLiSlHub);
        if (controller is null)
        {
            ActionStatusText.Text = "OpenRGB did not report a Lian Li UNI HUB SL controller. No changes were made.";
            return;
        }

        (int ZoneIndex, int ChannelNumber, int FanCount)[] requestedSizes;
        try
        {
            requestedSizes =
            [
                (0, 1, GetSelectedFanCount(LianLiChannel1FanCountCombo)),
                (1, 2, GetSelectedFanCount(LianLiChannel2FanCountCombo)),
                (3, 4, GetSelectedFanCount(LianLiChannel4FanCountCombo))
            ];
        }
        catch (InvalidOperationException exception)
        {
            ActionStatusText.Text = exception.Message;
            return;
        }

        var zoneChanges = new List<(int ZoneIndex, int Size)>();
        foreach (var (zoneIndex, channelNumber, fanCount) in requestedSizes)
        {
            var zone = controller.Zones.FirstOrDefault(candidate => candidate.Index == zoneIndex);
            if (zone is null)
            {
                ActionStatusText.Text =
                    $"OpenRGB did not report Lian Li Channel {channelNumber}. No changes were made.";
                return;
            }

            if (zone.LedCount != fanCount * LianLiLedsPerFan)
            {
                zoneChanges.Add((zoneIndex, fanCount));
            }
        }

        if (zoneChanges.Count == 0)
        {
            ActionStatusText.Text =
                "Lian Li Channels 1, 2, and 4 already match the selected fan counts. Channel 3 was left untouched; no color command was sent.";
            return;
        }

        DevicesView.IsEnabled = false;
        try
        {
            var updatedController = await _openRgbClient.ConfigureZonesAsync(
                controller.DeviceId,
                zoneChanges,
                _lifetimeCancellation.Token);
            if (!requestedSizes.All(requested =>
                    updatedController.Zones.Any(zone =>
                        zone.Index == requested.ZoneIndex &&
                        zone.LedCount == requested.FanCount * LianLiLedsPerFan)))
            {
                throw new InvalidDataException(
                    "OpenRGB did not report the requested fan counts after configuring the Lian Li hub.");
            }

            await RefreshDevicesAsync();
            var readyChannels = _devices.Count(device =>
                device.ControllerId == controller.DeviceId &&
                device.IsControllable);
            var configuredCounts = string.Join(
                ", ",
                requestedSizes.Select(request => $"Channel {request.ChannelNumber}: {request.FanCount}"));
            ActionStatusText.Text =
                $"OpenRGB confirmed {readyChannels} ready Lian Li fan channels ({configuredCounts}). Channel 3, the front strip, was not changed. No color command was sent; choose Case lighting and use Apply to group when ready.";
        }
        catch (OperationCanceledException) when (_isClosing)
        {
        }
        catch (InvalidDataException exception)
        {
            ActionStatusText.Text = $"Could not verify the Lian Li hub configuration: {exception.Message}";
        }
        catch (IOException exception)
        {
            ActionStatusText.Text = $"Could not configure the Lian Li hub: {exception.Message}";
        }
        catch (SocketException exception)
        {
            ActionStatusText.Text = $"Could not configure the Lian Li hub: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            ActionStatusText.Text = $"Could not configure the Lian Li hub: {exception.Message}";
        }
        catch (ArgumentException exception)
        {
            ActionStatusText.Text = $"Could not configure the Lian Li hub: {exception.Message}";
        }
        finally
        {
            if (!_isClosing)
            {
                DevicesView.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                if (_openRgbClient is not null)
                {
                    UpdateLianLiSetupControls(_openRgbClient.Controllers);
                }
            }
        }
    }

    private async void ApplySelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = _devices.Where(device => device.IsSelected).ToArray();
        await ApplySceneFromControlsAsync(selected, "selected devices");
    }

    private async void ApplyGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = GetSelectedGroup();
        var targets = _devices
            .Where(device => device.IsControllable && (group is null || device.Group == group))
            .ToArray();
        await ApplySceneFromControlsAsync(targets, group?.ToDisplayName() ?? "all groups");
    }

    private async void ApplyAll_Click(object sender, RoutedEventArgs e)
    {
        await ApplySceneFromControlsAsync(_devices.Where(device => device.IsControllable).ToArray(), "all compatible devices");
    }

    private async Task ApplySceneFromControlsAsync(IReadOnlyList<LightingDevice> targets, string targetDescription)
    {
        try
        {
            var scene = CreateSceneFromControls(SceneNameTextBox.Text.Trim(), GetSelectedGroup());
            await ApplySceneAsync(scene, targets, targetDescription);
        }
        catch (FormatException exception)
        {
            ActionStatusText.Text = exception.Message;
        }
        catch (IOException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"RGB command failed: {exception.Message}";
        }
        catch (SocketException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"RGB command failed: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"RGB command failed: {exception.Message}";
        }
    }

    private LightingScene CreateSceneFromControls(string name, DeviceGroup? targetGroup)
    {
        var effectTag = (EffectCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (!Enum.TryParse<SceneEffect>(effectTag, out var effect))
        {
            throw new InvalidOperationException("Choose a supported lighting effect.");
        }

        var palette = effect == SceneEffect.Solid && RgbColor.TryParse(ColorHexText.Text, out var selectedColor)
            ? [selectedColor]
            : ParsePalette();

        return new LightingScene
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Quick look" : name,
            Effect = effect,
            TargetGroup = targetGroup,
            Colors = palette,
            Speed = checked((int)Math.Round(SpeedSlider.Value)),
            Brightness = checked((int)Math.Round(BrightnessSlider.Value))
        };
    }

    private static int GetSelectedFanCount(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, out var fanCount) &&
            fanCount is >= 1 and <= 4)
        {
            return fanCount;
        }

        throw new InvalidOperationException("Choose a fan count between 1 and 4 for each Lian Li channel.");
    }

    private List<RgbColor> ParsePalette()
    {
        var values = PaletteTextBox.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            if (RgbColor.TryParse(ColorHexText.Text, out var fallback))
            {
                return [fallback];
            }

            throw new FormatException("Add at least one six-digit hex color to the scene palette.");
        }

        var colors = new List<RgbColor>(values.Length);
        foreach (var value in values)
        {
            if (!RgbColor.TryParse(value, out var color))
            {
                throw new FormatException($"'{value}' is not a valid six-digit hex color.");
            }

            colors.Add(color);
        }

        return colors;
    }

    private async Task ApplySceneAsync(
        LightingScene scene,
        IReadOnlyList<LightingDevice> targets,
        string targetDescription)
    {
        StopAnimation();
        if (_openRgbClient is null)
        {
            ActionStatusText.Text = "No RGB commands were sent. Connect an OpenRGB SDK server first.";
            return;
        }

        var readyTargets = targets.Where(device => device.IsControllable).DistinctBy(device => device.Id).ToArray();
        if (readyTargets.Length == 0)
        {
            ActionStatusText.Text = "No compatible devices are selected in this target. Connect a supported RGB backend first.";
            return;
        }

        if (scene.Effect == SceneEffect.Solid)
        {
            foreach (var device in readyTargets)
            {
                var colors = SceneAnimator.Render(scene, device.LedCount, TimeSpan.Zero);
                await _openRgbClient.ApplyColorsAsync(
                    device.ControllerId!.Value,
                    device.ZoneIndex,
                    colors);
            }

            ActionStatusText.Text =
                $"Static color sent to {readyTargets.Length} device(s) in {targetDescription}. Device confirmation depends on the controller.";
            return;
        }

        _activeScene = scene;
        _activeTargets = readyTargets;
        _animationStartedAt = Stopwatch.GetTimestamp();
        await ApplyAnimationFrameAsync();
        _animationTimer.Start();
        ActionStatusText.Text =
            $"{scene.EffectName} running on {readyTargets.Length} device(s). Keep GlowDeck open; no device confirmation is available.";
    }

    private async void AnimationTimer_Tick(object? sender, EventArgs e)
    {
        if (_frameInProgress || _activeScene is null || _openRgbClient is null)
        {
            return;
        }

        _frameInProgress = true;
        try
        {
            await ApplyAnimationFrameAsync();
        }
        catch (IOException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"Animation stopped: {exception.Message}";
        }
        catch (SocketException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"Animation stopped: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"Animation stopped: {exception.Message}";
        }
        finally
        {
            _frameInProgress = false;
        }
    }

    private async Task ApplyAnimationFrameAsync()
    {
        if (_activeScene is null || _openRgbClient is null)
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(_animationStartedAt);
        foreach (var device in _activeTargets)
        {
            var colors = SceneAnimator.Render(_activeScene, device.LedCount, elapsed);
            await _openRgbClient.ApplyColorsAsync(
                device.ControllerId!.Value,
                device.ZoneIndex,
                colors);
        }
    }

    private void StopAnimation()
    {
        _animationTimer.Stop();
        _activeScene = null;
        _activeTargets = [];
    }

    private async void SaveScene_Click(object sender, RoutedEventArgs e)
    {
        var name = SceneNameTextBox.Text.Trim();
        if (name.Length == 0)
        {
            ActionStatusText.Text = "Give the scene a name before saving it.";
            return;
        }

        try
        {
            var scene = CreateSceneFromControls(name, GetSelectedGroup());
            var previous = _scenes.FirstOrDefault(
                existing => !existing.IsBuiltIn && existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (previous is not null)
            {
                scene.Id = previous.Id;
            }

            var customScenes = _scenes
                .Where(existing => !existing.IsBuiltIn && existing.Id != scene.Id)
                .Append(scene)
                .ToArray();
            await _sceneStore.SaveCustomScenesAsync(customScenes);

            if (previous is not null)
            {
                _scenes.Remove(previous);
            }

            _scenes.Add(scene);
            ScenesList.SelectedItem = scene;
            ActionStatusText.Text = $"Saved scene '{scene.Name}'.";
        }
        catch (FormatException exception)
        {
            ActionStatusText.Text = exception.Message;
        }
        catch (IOException exception)
        {
            ActionStatusText.Text = $"Could not save the scene: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            ActionStatusText.Text = $"Could not save the scene: {exception.Message}";
        }
    }

    private async Task LoadCustomScenesAsync()
    {
        try
        {
            var customScenes = await _sceneStore.LoadCustomScenesAsync();
            foreach (var scene in customScenes)
            {
                scene.IsBuiltIn = false;
                _scenes.Add(scene);
            }
        }
        catch (IOException exception)
        {
            ActionStatusText.Text = $"Could not load saved scenes: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            ActionStatusText.Text = $"Could not load saved scenes: {exception.Message}";
        }
        catch (InvalidDataException exception)
        {
            ActionStatusText.Text = $"Could not load saved scenes: {exception.Message}";
        }
    }

    private void ScenesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ScenesList.SelectedItem is not LightingScene scene)
        {
            return;
        }

        SceneDetailsName.Text = scene.Name;
        SceneDetailsSummary.Text = $"{scene.Summary}  |  Speed {scene.Speed}  |  Brightness {scene.Brightness}%";
        SceneDetailsPalette.Text = scene.PaletteText;
        ScenePreview.Background = MakeSceneBrush(scene.Colors);
        DeleteSceneButton.IsEnabled = !scene.IsBuiltIn;
    }

    private void LoadScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not LightingScene scene)
        {
            ActionStatusText.Text = "Select a scene first.";
            return;
        }

        LoadSceneIntoControls(scene);
        ShowDevices_Click(sender, e);
        ActionStatusText.Text = $"Loaded '{scene.Name}' into the controls. Apply it when you are ready.";
    }

    private async void ApplyScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not LightingScene scene)
        {
            ActionStatusText.Text = "Select a scene first.";
            return;
        }

        var selected = _devices.Where(device => device.IsSelected).ToArray();
        var group = scene.TargetGroup ?? GetSelectedGroup();
        var targets = selected.Length > 0
            ? selected
            : _devices.Where(device => device.IsControllable && (group is null || device.Group == group)).ToArray();

        try
        {
            await ApplySceneAsync(scene, targets, group?.ToDisplayName() ?? "all groups");
        }
        catch (IOException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"RGB command failed: {exception.Message}";
        }
        catch (SocketException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"RGB command failed: {exception.Message}";
        }
        catch (InvalidOperationException exception)
        {
            StopAnimation();
            ActionStatusText.Text = $"RGB command failed: {exception.Message}";
        }
    }

    private async void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (ScenesList.SelectedItem is not LightingScene { IsBuiltIn: false } scene)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Delete the custom scene '{scene.Name}'?",
            "Delete scene",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var updated = _scenes.Where(existing => existing.Id != scene.Id).ToArray();
        try
        {
            await _sceneStore.SaveCustomScenesAsync(updated);
            _scenes.Remove(scene);
            ActionStatusText.Text = $"Deleted scene '{scene.Name}'.";
        }
        catch (IOException exception)
        {
            ActionStatusText.Text = $"Could not delete the scene: {exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            ActionStatusText.Text = $"Could not delete the scene: {exception.Message}";
        }
    }

    private void LoadSceneIntoControls(LightingScene scene)
    {
        PaletteTextBox.Text = scene.PaletteText;
        if (scene.Colors.Count > 0)
        {
            ColorHexText.Text = scene.Colors[0].ToHex();
        }

        EffectCombo.SelectedIndex = scene.Effect switch
        {
            SceneEffect.Solid => 0,
            SceneEffect.Breathing => 1,
            SceneEffect.ColorCycle => 2,
            SceneEffect.Rainbow => 3,
            _ => 0
        };
        SpeedSlider.Value = Math.Clamp(scene.Speed, SpeedSlider.Minimum, SpeedSlider.Maximum);
        BrightnessSlider.Value = Math.Clamp(scene.Brightness, BrightnessSlider.Minimum, BrightnessSlider.Maximum);
        GroupFilterCombo.SelectedIndex = scene.TargetGroup switch
        {
            DeviceGroup.CaseLighting => 1,
            DeviceGroup.Motherboard => 2,
            DeviceGroup.Memory => 3,
            DeviceGroup.Razer => 4,
            DeviceGroup.Other => 5,
            _ => 0
        };
        SceneNameTextBox.Text = scene.IsBuiltIn ? $"{scene.Name} copy" : scene.Name;
    }

    private void ShowDevices_Click(object sender, RoutedEventArgs e)
    {
        DevicesView.Visibility = Visibility.Visible;
        ScenesView.Visibility = Visibility.Collapsed;
        DevicesNavButton.Background = new SolidColorBrush(Color.FromRgb(39, 38, 63));
        DevicesNavButton.BorderBrush = new SolidColorBrush(Color.FromRgb(83, 76, 133));
        ScenesNavButton.Background = Brushes.Transparent;
        ScenesNavButton.BorderBrush = Brushes.Transparent;
        PageTitleText.Text = "Your devices";
        PageSubtitleText.Text = "Choose a group, set a look, and apply it to compatible devices.";
    }

    private void ShowScenes_Click(object sender, RoutedEventArgs e)
    {
        DevicesView.Visibility = Visibility.Collapsed;
        ScenesView.Visibility = Visibility.Visible;
        DevicesNavButton.Background = Brushes.Transparent;
        DevicesNavButton.BorderBrush = Brushes.Transparent;
        ScenesNavButton.Background = new SolidColorBrush(Color.FromRgb(39, 38, 63));
        ScenesNavButton.BorderBrush = new SolidColorBrush(Color.FromRgb(83, 76, 133));
        PageTitleText.Text = "Lighting scenes";
        PageSubtitleText.Text = "Save a favorite palette or play a scene across your selected devices.";
    }

    private static Brush ToBrush(RgbColor color) =>
        new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));

    private static Brush MakeSceneBrush(IReadOnlyList<RgbColor> colors)
    {
        if (colors.Count < 2)
        {
            return ToBrush(colors.Count == 0 ? new RgbColor(45, 55, 72) : colors[0]);
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        for (var index = 0; index < colors.Count; index++)
        {
            brush.GradientStops.Add(new GradientStop(
                Color.FromRgb(colors[index].R, colors[index].G, colors[index].B),
                (double)index / (colors.Count - 1)));
        }

        return brush;
    }
}
