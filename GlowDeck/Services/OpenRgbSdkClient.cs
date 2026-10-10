using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using GlowDeck.Models;

namespace GlowDeck.Services;

public sealed record OpenRgbZone(int Index, string Name, int LedCount);

public sealed record OpenRgbController(
    uint DeviceId,
    int Index,
    string Name,
    string Vendor,
    string Description,
    int LedCount,
    IReadOnlyList<OpenRgbZone> Zones);

public sealed class OpenRgbSdkClient : IAsyncDisposable
{
    private const int HeaderLength = 16;
    private const uint MaxPacketSize = 8 * 1024 * 1024;
    private const uint RequestControllerCount = 0;
    private const uint RequestControllerData = 1;
    private const uint RequestProtocolVersion = 40;
    private const uint DeviceListUpdated = 100;
    private const uint SetClientName = 50;
    private const uint ResizeZone = 1000;
    private const uint SetCustomMode = 1100;
    private const uint UpdateLeds = 1050;
    private const uint UpdateZoneLeds = 1051;
    private const uint MaximumProtocolVersion = 4;
    private static readonly byte[] Magic = "ORGB"u8.ToArray();

    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly HashSet<uint> _customModeDevices = [];
    private readonly object _sessionLock = new();
    private CancellationTokenSource? _notificationCancellation;
    private Task? _notificationTask;
    private bool _isSessionValid = true;
    private string? _invalidationReason;
    private bool _disposed;

    private OpenRgbSdkClient(TcpClient tcpClient)
    {
        _tcpClient = tcpClient;
        _stream = tcpClient.GetStream();
    }

    public byte ProtocolVersion { get; private set; }
    public IReadOnlyList<OpenRgbController> Controllers { get; private set; } = [];
    public event EventHandler? SessionInvalidated;

    public bool IsSessionValid
    {
        get
        {
            lock (_sessionLock)
            {
                return _isSessionValid;
            }
        }
    }

    public string? InvalidationReason
    {
        get
        {
            lock (_sessionLock)
            {
                return _invalidationReason;
            }
        }
    }

    public static async Task<OpenRgbSdkClient> ConnectAsync(
        string host = "127.0.0.1",
        int port = 6742,
        CancellationToken cancellationToken = default)
    {
        var tcpClient = new TcpClient { NoDelay = true };
        OpenRgbSdkClient? session = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await tcpClient.ConnectAsync(host, port, timeout.Token);
            session = new OpenRgbSdkClient(tcpClient);
            await session.InitializeAsync(timeout.Token);
            return session;
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                tcpClient.Dispose();
            }

            throw new TimeoutException("Timed out while negotiating with the OpenRGB SDK server.", exception);
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            else
            {
                tcpClient.Dispose();
            }

            throw;
        }
    }

    public async Task ApplyColorsAsync(
        uint deviceId,
        int? zoneIndex,
        IReadOnlyList<RgbColor> colors,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfSessionInvalid();
        if (colors.Count is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(colors), "A color frame must contain between 1 and 65535 LEDs.");
        }

        if (!Controllers.Any(controller => controller.DeviceId == deviceId))
        {
            throw new InvalidOperationException($"OpenRGB device {deviceId} is no longer in the connected device list.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfSessionInvalid();
            if (_customModeDevices.Add(deviceId))
            {
                await SendPacketAsync(deviceId, SetCustomMode, [], cancellationToken);
            }

            var payload = zoneIndex.HasValue
                ? BuildZoneColorPayload(zoneIndex.Value, colors)
                : BuildColorPayload(colors);
            await SendPacketAsync(deviceId, zoneIndex.HasValue ? UpdateZoneLeds : UpdateLeds, payload, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<OpenRgbController> ConfigureZonesAsync(
        uint deviceId,
        IReadOnlyList<(int ZoneIndex, int Size)> zoneSizes,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(zoneSizes);
        ThrowIfSessionInvalid();
        if (zoneSizes.Count == 0)
        {
            throw new ArgumentException("Configure at least one OpenRGB zone.", nameof(zoneSizes));
        }

        var controller = Controllers.FirstOrDefault(candidate => candidate.DeviceId == deviceId)
            ?? throw new InvalidOperationException($"OpenRGB device {deviceId} is no longer in the connected device list.");
        var knownZoneIndexes = controller.Zones.Select(zone => zone.Index).ToHashSet();
        var requestedZoneIndexes = new HashSet<int>();
        foreach (var (zoneIndex, size) in zoneSizes)
        {
            if (!knownZoneIndexes.Contains(zoneIndex))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(zoneSizes),
                    $"OpenRGB device {deviceId} does not have zone {zoneIndex}.");
            }

            if (size < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(zoneSizes), "Zone sizes cannot be negative.");
            }

            if (!requestedZoneIndexes.Add(zoneIndex))
            {
                throw new ArgumentException($"Zone {zoneIndex} was specified more than once.", nameof(zoneSizes));
            }
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ThrowIfSessionInvalid();
            await StopNotificationMonitoringAsync();

            try
            {
                ThrowIfSessionInvalid();
                foreach (var (zoneIndex, size) in zoneSizes)
                {
                    await SendPacketAsync(
                        deviceId,
                        ResizeZone,
                        BuildZoneResizePayload(zoneIndex, size),
                        cancellationToken);
                }

                await SendPacketAsync(
                    deviceId,
                    RequestControllerData,
                    UInt32Payload(ProtocolVersion),
                    cancellationToken);

                var response = await ReadControllerDataAfterZoneResizeAsync(deviceId, cancellationToken);
                var updatedController = ParseControllerData(response.Payload, ProtocolVersion, deviceId);
                Controllers = Controllers
                    .Select(candidate => candidate.DeviceId == deviceId ? updatedController : candidate)
                    .ToArray();
                return updatedController;
            }
            catch (IOException exception)
            {
                InvalidateSession($"OpenRGB zone configuration failed: {exception.Message}");
                throw;
            }
            catch (SocketException exception)
            {
                InvalidateSession($"OpenRGB zone configuration failed: {exception.Message}");
                throw;
            }
            catch (InvalidDataException exception)
            {
                InvalidateSession($"OpenRGB zone configuration failed: {exception.Message}");
                throw;
            }
            catch (OperationCanceledException)
            {
                InvalidateSession("OpenRGB zone configuration was interrupted. Refresh devices before continuing.");
                throw;
            }
            finally
            {
                if (IsSessionValid)
                {
                    StartNotificationMonitoring();
                }
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await SendPacketAsync(
            0,
            RequestProtocolVersion,
            UInt32Payload(MaximumProtocolVersion),
            cancellationToken);

        var versionResponse = await ReadExpectedPacketAsync(RequestProtocolVersion, cancellationToken);
        if (versionResponse.Payload.Length < sizeof(uint))
        {
            throw new InvalidDataException("OpenRGB returned an incomplete protocol version response.");
        }

        var serverVersion = BinaryPrimitives.ReadUInt32LittleEndian(versionResponse.Payload);
        if (serverVersion < 1)
        {
            throw new NotSupportedException("This OpenRGB SDK server does not support protocol version 1 or later.");
        }

        ProtocolVersion = (byte)Math.Min(serverVersion, MaximumProtocolVersion);
        await SendPacketAsync(
            0,
            SetClientName,
            Encoding.UTF8.GetBytes("GlowDeck\0"),
            cancellationToken);

        await SendPacketAsync(0, RequestControllerCount, [], cancellationToken);
        var countResponse = await ReadExpectedPacketAsync(RequestControllerCount, cancellationToken);
        if (countResponse.Payload.Length < sizeof(uint))
        {
            throw new InvalidDataException("OpenRGB returned an incomplete controller count response.");
        }

        var controllerCount = BinaryPrimitives.ReadUInt32LittleEndian(countResponse.Payload);
        if (controllerCount > 128)
        {
            throw new InvalidDataException($"OpenRGB reported an unreasonable controller count: {controllerCount}.");
        }

        var controllers = new List<OpenRgbController>(checked((int)controllerCount));
        for (uint deviceId = 0; deviceId < controllerCount; deviceId++)
        {
            await SendPacketAsync(
                deviceId,
                RequestControllerData,
                UInt32Payload(ProtocolVersion),
                cancellationToken);
            var response = await ReadExpectedPacketAsync(RequestControllerData, cancellationToken);
            controllers.Add(ParseControllerData(response.Payload, ProtocolVersion, deviceId));
        }

        Controllers = controllers;
        StartNotificationMonitoring();
    }

    private async Task<OpenRgbPacket> ReadControllerDataAfterZoneResizeAsync(
        uint expectedDeviceId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await ReadPacketAsync(cancellationToken);
            if (packet.PacketId == DeviceListUpdated)
            {
                continue;
            }

            if (packet.PacketId != RequestControllerData)
            {
                throw new InvalidDataException(
                    $"OpenRGB returned packet {packet.PacketId} while refreshing the configured controller.");
            }

            if (packet.DeviceId != expectedDeviceId)
            {
                throw new InvalidDataException(
                    $"OpenRGB refreshed device {packet.DeviceId} instead of device {expectedDeviceId}.");
            }

            return packet;
        }
    }

    private async Task StopNotificationMonitoringAsync()
    {
        var cancellation = _notificationCancellation;
        var task = _notificationTask;
        _notificationCancellation = null;
        _notificationTask = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        try
        {
            if (task is not null)
            {
                await task;
            }
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void StartNotificationMonitoring()
    {
        if (_disposed || !IsSessionValid || _notificationTask is not null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _notificationCancellation = cancellation;
        _notificationTask = Task.Run(() => MonitorNotificationsAsync(cancellation.Token));
    }

    private async Task MonitorNotificationsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            OpenRgbPacket packet;
            try
            {
                packet = await ReadPacketAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                if (!_disposed)
                {
                    InvalidateSession("The OpenRGB SDK connection was closed unexpectedly.");
                }

                return;
            }
            catch (IOException exception)
            {
                if (!_disposed)
                {
                    InvalidateSession($"The OpenRGB SDK connection was lost: {exception.Message}");
                }

                return;
            }
            catch (SocketException exception)
            {
                if (!_disposed)
                {
                    InvalidateSession($"The OpenRGB SDK connection was lost: {exception.Message}");
                }

                return;
            }
            catch (InvalidDataException exception)
            {
                InvalidateSession($"OpenRGB sent an invalid SDK notification: {exception.Message}");
                return;
            }

            if (packet.PacketId == DeviceListUpdated)
            {
                InvalidateSession("The OpenRGB device list changed. Refresh devices before sending more lighting commands.");
                return;
            }

            InvalidateSession(
                $"OpenRGB sent an unexpected SDK notification ({packet.PacketId}). Refresh devices before sending more lighting commands.");
            return;
        }
    }

    private void InvalidateSession(string reason)
    {
        lock (_sessionLock)
        {
            if (!_isSessionValid)
            {
                return;
            }

            _isSessionValid = false;
            _invalidationReason = reason;
        }

        SessionInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void ThrowIfSessionInvalid()
    {
        lock (_sessionLock)
        {
            if (_isSessionValid)
            {
                return;
            }

            throw new InvalidOperationException(
                _invalidationReason ?? "The OpenRGB SDK session is no longer valid. Refresh devices before continuing.");
        }
    }

    private async Task<OpenRgbPacket> ReadExpectedPacketAsync(uint expectedPacketId, CancellationToken cancellationToken)
    {
        var packet = await ReadPacketAsync(cancellationToken);
        if (packet.PacketId != expectedPacketId)
        {
            throw new InvalidDataException(
                $"OpenRGB returned packet {packet.PacketId} while waiting for packet {expectedPacketId}.");
        }

        return packet;
    }

    private async Task<OpenRgbPacket> ReadPacketAsync(CancellationToken cancellationToken)
    {
        var header = new byte[HeaderLength];
        await _stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The OpenRGB SDK response has an invalid packet signature.");
        }

        var deviceId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        var packetId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
        if (payloadSize > MaxPacketSize)
        {
            throw new InvalidDataException($"OpenRGB returned a packet that is too large: {payloadSize} bytes.");
        }

        var payload = new byte[checked((int)payloadSize)];
        if (payload.Length > 0)
        {
            await _stream.ReadExactlyAsync(payload, cancellationToken);
        }

        return new OpenRgbPacket(deviceId, packetId, payload);
    }

    private async Task SendPacketAsync(
        uint deviceId,
        uint packetId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        if (payload.Length > MaxPacketSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "The OpenRGB SDK packet exceeds the protocol size limit.");
        }

        var packet = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)payload.Length);
        payload.CopyTo(packet, HeaderLength);
        await _stream.WriteAsync(packet, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    private static byte[] BuildColorPayload(IReadOnlyList<RgbColor> colors)
    {
        var payload = new byte[checked(sizeof(uint) + sizeof(ushort) + (colors.Count * sizeof(uint)))];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)(payload.Length - sizeof(uint))));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(sizeof(uint), sizeof(ushort)), checked((ushort)colors.Count));
        WriteColors(payload, sizeof(uint) + sizeof(ushort), colors);
        return payload;
    }

    private static byte[] BuildZoneColorPayload(int zoneIndex, IReadOnlyList<RgbColor> colors)
    {
        if (zoneIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(zoneIndex));
        }

        var payload = new byte[checked(sizeof(uint) + sizeof(uint) + sizeof(ushort) + (colors.Count * sizeof(uint)))];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)(payload.Length - sizeof(uint))));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(sizeof(uint), sizeof(uint)), checked((uint)zoneIndex));
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(sizeof(uint) * 2, sizeof(ushort)), checked((ushort)colors.Count));
        WriteColors(payload, sizeof(uint) + sizeof(uint) + sizeof(ushort), colors);
        return payload;
    }

    private static byte[] BuildZoneResizePayload(int zoneIndex, int size)
    {
        var payload = new byte[sizeof(int) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(payload, zoneIndex);
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(sizeof(int)), size);
        return payload;
    }

    private static void WriteColors(byte[] payload, int offset, IReadOnlyList<RgbColor> colors)
    {
        for (var index = 0; index < colors.Count; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(offset + (index * sizeof(uint)), sizeof(uint)),
                colors[index].ToOpenRgbValue());
        }
    }

    private static byte[] UInt32Payload(uint value)
    {
        var payload = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return payload;
    }

    private static OpenRgbController ParseControllerData(byte[] payload, byte protocolVersion, uint deviceId)
    {
        var reader = new PacketReader(payload);
        var dataSize = reader.ReadUInt32();
        if (dataSize > payload.Length)
        {
            throw new InvalidDataException("OpenRGB controller data reports an invalid payload size.");
        }

        _ = reader.ReadInt32();
        var name = reader.ReadString16();
        var vendor = protocolVersion >= 1 ? reader.ReadString16() : string.Empty;
        var description = reader.ReadString16();
        _ = reader.ReadString16();
        _ = reader.ReadString16();
        _ = reader.ReadString16();

        var modeCount = reader.ReadUInt16();
        _ = reader.ReadInt32();
        for (var index = 0; index < modeCount; index++)
        {
            SkipMode(ref reader, protocolVersion);
        }

        var zoneCount = reader.ReadUInt16();
        var zones = new List<OpenRgbZone>(zoneCount);
        var totalZoneLeds = 0;
        for (var index = 0; index < zoneCount; index++)
        {
            var zoneName = reader.ReadString16();
            _ = reader.ReadInt32();
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            var zoneLedCount = checked((int)reader.ReadUInt32());
            totalZoneLeds = checked(totalZoneLeds + zoneLedCount);

            var matrixLength = reader.ReadUInt16();
            reader.Skip(matrixLength);

            if (protocolVersion >= 4)
            {
                var segmentCount = reader.ReadUInt16();
                for (var segment = 0; segment < segmentCount; segment++)
                {
                    _ = reader.ReadString16();
                    _ = reader.ReadInt32();
                    _ = reader.ReadUInt32();
                    _ = reader.ReadUInt32();
                }
            }

            zones.Add(new OpenRgbZone(index, zoneName, zoneLedCount));
        }

        var ledCount = reader.ReadUInt16();
        for (var index = 0; index < ledCount; index++)
        {
            _ = reader.ReadString16();
            if (protocolVersion < 6)
            {
                _ = reader.ReadUInt32();
            }
        }

        var colorCount = reader.ReadUInt16();
        reader.Skip(checked(colorCount * sizeof(uint)));

        var effectiveLedCount = ledCount > 0 ? ledCount : totalZoneLeds;
        return new OpenRgbController(deviceId, checked((int)deviceId), name, vendor, description, effectiveLedCount, zones);
    }

    private static void SkipMode(ref PacketReader reader, byte protocolVersion)
    {
        _ = reader.ReadString16();
        if (protocolVersion < 6)
        {
            _ = reader.ReadInt32();
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        if (protocolVersion >= 3)
        {
            _ = reader.ReadUInt32();
            _ = reader.ReadUInt32();
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        if (protocolVersion >= 3)
        {
            _ = reader.ReadUInt32();
        }

        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();
        var colorCount = reader.ReadUInt16();
        reader.Skip(checked(colorCount * sizeof(uint)));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _writeGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _notificationCancellation?.Cancel();
            _stream.Dispose();
            _tcpClient.Dispose();
        }
        finally
        {
            _writeGate.Release();
        }

        if (_notificationTask is not null)
        {
            try
            {
                await _notificationTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (InvalidDataException)
            {
            }
        }

        _notificationCancellation?.Dispose();
    }

    private readonly record struct OpenRgbPacket(uint DeviceId, uint PacketId, byte[] Payload);

    private ref struct PacketReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _offset;

        public PacketReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _offset = 0;
        }

        public uint ReadUInt32()
        {
            Ensure(sizeof(uint));
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(_offset, sizeof(uint)));
            _offset += sizeof(uint);
            return value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ushort ReadUInt16()
        {
            Ensure(sizeof(ushort));
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(_offset, sizeof(ushort)));
            _offset += sizeof(ushort);
            return value;
        }

        public string ReadString16()
        {
            var length = ReadUInt16();
            Ensure(length);
            var value = Encoding.UTF8.GetString(_data.Slice(_offset, length)).TrimEnd('\0');
            _offset += length;
            return value;
        }

        public void Skip(int length)
        {
            Ensure(length);
            _offset += length;
        }

        private void Ensure(int length)
        {
            if (length < 0 || length > _data.Length - _offset)
            {
                throw new InvalidDataException("OpenRGB returned truncated controller data.");
            }
        }
    }
}
