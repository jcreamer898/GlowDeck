using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GlowDeck.Models;
using GlowDeck.Services;
using Xunit;

namespace GlowDeck.Tests;

public sealed class LightingTests
{
    [Fact]
    public void RgbColorParsesHexAndUsesOpenRgbChannelOrder()
    {
        Assert.True(RgbColor.TryParse("#1A2B3C", out var color));
        Assert.Equal(new RgbColor(0x1A, 0x2B, 0x3C), color);
        Assert.Equal("#1A2B3C", color.ToHex());
        Assert.Equal(0x003C2B1Au, color.ToOpenRgbValue());
        Assert.False(RgbColor.TryParse("#12FG56", out _));
    }

    [Theory]
    [InlineData(DeviceGroup.CaseLighting, true)]
    [InlineData(DeviceGroup.Motherboard, true)]
    [InlineData(DeviceGroup.Memory, true)]
    [InlineData(DeviceGroup.Razer, false)]
    [InlineData(DeviceGroup.Other, false)]
    public void InitialControlScopeOnlyAllowsCaseDevices(DeviceGroup group, bool expected)
    {
        var isInScope = group.IsInInitialControlScope();
        var device = new LightingDevice
        {
            Id = "test",
            Name = "Test device",
            Details = "Test device",
            Status = string.Empty,
            Group = group,
            ControllerId = 1,
            LedCount = 1,
            SupportsControl = isInScope
        };

        Assert.Equal(expected, isInScope);
        Assert.Equal(expected, device.IsControllable);
        if (!expected)
        {
            Assert.Equal("Read only", device.ConnectionStateLabel);
        }
    }

    [Fact]
    public void SolidSceneAppliesConfiguredBrightnessToEveryLed()
    {
        var scene = new LightingScene
        {
            Effect = SceneEffect.Solid,
            Colors = [new RgbColor(255, 100, 20)],
            Brightness = 50
        };

        var frame = SceneAnimator.Render(scene, 3, TimeSpan.Zero);

        Assert.Equal(
            [new RgbColor(128, 50, 10), new RgbColor(128, 50, 10), new RgbColor(128, 50, 10)],
            frame);
    }

    [Fact]
    public void ColorCycleInterpolatesBetweenPaletteColors()
    {
        var scene = new LightingScene
        {
            Effect = SceneEffect.ColorCycle,
            Colors = [new RgbColor(255, 0, 0), new RgbColor(0, 0, 255)],
            Speed = 100,
            Brightness = 100
        };

        var frame = SceneAnimator.Render(scene, 1, TimeSpan.FromSeconds(0.25 / 0.98));

        Assert.Equal(new RgbColor(191, 0, 64), Assert.Single(frame));
    }

    [Fact]
    public async Task SdkDiscoversControllerAndSendsZoneColorPacketToMockServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockSdkServerAsync(listener, timeout.Token);

        await using var sdk = await OpenRgbSdkClient.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            timeout.Token);
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sdk.SessionInvalidated += (_, _) => invalidated.TrySetResult();

        var controller = Assert.Single(sdk.Controllers);
        Assert.Equal(0u, controller.DeviceId);
        Assert.Equal("Mock controller", controller.Name);
        Assert.Equal(3, controller.LedCount);
        Assert.Equal(new OpenRgbZone(0, "Mock zone", 3), Assert.Single(controller.Zones));

        var colors = new[]
        {
            new RgbColor(0x11, 0x22, 0x33),
            new RgbColor(0x44, 0x55, 0x66),
            new RgbColor(0x77, 0x88, 0x99)
        };
        await sdk.ApplyColorsAsync(controller.DeviceId, 0, colors, timeout.Token);
        await sdk.ApplyColorsAsync(controller.DeviceId, null, colors, timeout.Token);

        var writes = await serverTask;
        await invalidated.Task.WaitAsync(timeout.Token);
        Assert.False(sdk.IsSessionValid);
        Assert.Contains("device list changed", sdk.InvalidationReason, StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sdk.ApplyColorsAsync(controller.DeviceId, 0, colors, timeout.Token));

        Assert.Equal(3, writes.Count);
        Assert.Equal(1100u, writes[0].PacketId);
        Assert.Empty(writes[0].Payload);
        Assert.Equal(1051u, writes[1].PacketId);
        Assert.Equal(0u, writes[1].DeviceId);
        Assert.Equal(1050u, writes[2].PacketId);

        var payload = writes[1].Payload;
        Assert.Equal((uint)(payload.Length - sizeof(uint)), BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(sizeof(uint), sizeof(uint))));
        Assert.Equal((ushort)colors.Length, BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(8, sizeof(ushort))));
        for (var index = 0; index < colors.Length; index++)
        {
            var actual = BinaryPrimitives.ReadUInt32LittleEndian(
                payload.AsSpan(10 + index * sizeof(uint), sizeof(uint)));
            Assert.Equal(colors[index].ToOpenRgbValue(), actual);
        }

        var controllerPayload = writes[2].Payload;
        Assert.Equal(
            (uint)(controllerPayload.Length - sizeof(uint)),
            BinaryPrimitives.ReadUInt32LittleEndian(controllerPayload));
        Assert.Equal(
            (ushort)colors.Length,
            BinaryPrimitives.ReadUInt16LittleEndian(controllerPayload.AsSpan(sizeof(uint), sizeof(ushort))));
    }

    [Fact]
    public async Task SdkConfiguresZoneSizesAndRefreshesControllerData()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = RunMockSdkZoneResizeServerAsync(listener, timeout.Token);

        await using var sdk = await OpenRgbSdkClient.ConnectAsync(
            IPAddress.Loopback.ToString(),
            port,
            timeout.Token);

        var controller = Assert.Single(sdk.Controllers);
        Assert.Equal(0, controller.LedCount);

        var updatedController = await sdk.ConfigureZonesAsync(
            controller.DeviceId,
            [(0, 3), (1, 3), (3, 3)],
            timeout.Token);
        var requests = await serverTask;

        Assert.Equal(4, requests.Count);
        var expectedZoneIndexes = new[] { 0, 1, 3 };
        for (var index = 0; index < 3; index++)
        {
            var request = requests[index];
            Assert.Equal(1000u, request.PacketId);
            Assert.Equal(0u, request.DeviceId);
            Assert.Equal(8, request.Payload.Length);
            Assert.Equal(expectedZoneIndexes[index], BinaryPrimitives.ReadInt32LittleEndian(request.Payload));
            Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(request.Payload.AsSpan(sizeof(int))));
        }

        var refreshRequest = requests[3];
        Assert.Equal(1u, refreshRequest.PacketId);
        Assert.Equal(0u, refreshRequest.DeviceId);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(refreshRequest.Payload));
        Assert.Equal([48, 48, 0, 48], updatedController.Zones.Select(zone => zone.LedCount).ToArray());
        Assert.Equal(144, updatedController.LedCount);
    }

    private static async Task<IReadOnlyList<Packet>> RunMockSdkServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var versionRequest = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(40u, versionRequest.PacketId);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(versionRequest.Payload));
        await WritePacketAsync(stream, 0, 40, UInt32Payload(4), cancellationToken);

        var clientName = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(50u, clientName.PacketId);
        var countRequest = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(0u, countRequest.PacketId);
        await WritePacketAsync(stream, 0, 0, UInt32Payload(1), cancellationToken);

        var controllerRequest = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(1u, controllerRequest.PacketId);
        Assert.Equal(0u, controllerRequest.DeviceId);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(controllerRequest.Payload));
        await WritePacketAsync(stream, 0, 1, BuildControllerPayload(), cancellationToken);

        var modeRequest = await ReadPacketAsync(stream, cancellationToken);
        var colorRequest = await ReadPacketAsync(stream, cancellationToken);
        var controllerColorRequest = await ReadPacketAsync(stream, cancellationToken);
        await WritePacketAsync(stream, 0, 100, [], cancellationToken);
        return [modeRequest, colorRequest, controllerColorRequest];
    }

    private static async Task<IReadOnlyList<Packet>> RunMockSdkZoneResizeServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var versionRequest = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(40u, versionRequest.PacketId);
        await WritePacketAsync(stream, 0, 40, UInt32Payload(4), cancellationToken);

        _ = await ReadPacketAsync(stream, cancellationToken);
        var countRequest = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(0u, countRequest.PacketId);
        await WritePacketAsync(stream, 0, 0, UInt32Payload(1), cancellationToken);

        var controllerRequest = await ReadPacketAsync(stream, cancellationToken);
        Assert.Equal(1u, controllerRequest.PacketId);
        await WritePacketAsync(stream, 0, 1, BuildZoneResizeControllerPayload([0, 0, 0, 0]), cancellationToken);

        var requests = new List<Packet>(4);
        for (var index = 0; index < 3; index++)
        {
            requests.Add(await ReadPacketAsync(stream, cancellationToken));
        }

        var refreshRequest = await ReadPacketAsync(stream, cancellationToken);
        requests.Add(refreshRequest);
        Assert.Equal(1u, refreshRequest.PacketId);
        await WritePacketAsync(stream, 0, 100, [], cancellationToken);
        await WritePacketAsync(
            stream,
            0,
            1,
            BuildZoneResizeControllerPayload([48, 48, 0, 48]),
            cancellationToken);
        return requests;
    }

    private static async Task<Packet> ReadPacketAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, cancellationToken);
        Assert.Equal("ORGB", Encoding.ASCII.GetString(header, 0, 4));

        var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4)));
        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            await stream.ReadExactlyAsync(payload, cancellationToken);
        }

        return new Packet(
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)),
            payload);
    }

    private static async Task WritePacketAsync(
        NetworkStream stream,
        uint deviceId,
        uint packetId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var packet = new byte[16 + payload.Length];
        Encoding.ASCII.GetBytes("ORGB").CopyTo(packet, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), (uint)payload.Length);
        payload.CopyTo(packet, 16);
        await stream.WriteAsync(packet, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static byte[] BuildControllerPayload()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(0u);
        writer.Write(0);
        WriteString16(writer, "Mock controller");
        WriteString16(writer, "GlowDeck test");
        WriteString16(writer, "SDK protocol fixture");
        WriteString16(writer, string.Empty);
        WriteString16(writer, string.Empty);
        WriteString16(writer, string.Empty);
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write((ushort)1);
        WriteString16(writer, "Mock zone");
        writer.Write(0);
        writer.Write(0u);
        writer.Write(3u);
        writer.Write(3u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)3);
        for (var index = 0; index < 3; index++)
        {
            WriteString16(writer, $"LED {index + 1}");
            writer.Write(0u);
        }

        writer.Write((ushort)3);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(0u);
        writer.Flush();

        var payload = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)payload.Length);
        return payload;
    }

    private static byte[] BuildZoneResizeControllerPayload(IReadOnlyList<int> zoneLedCounts)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(0u);
        writer.Write(0);
        WriteString16(writer, "Mock Lian Li Uni Hub - SL");
        WriteString16(writer, "Lian Li");
        WriteString16(writer, "SDK protocol fixture");
        WriteString16(writer, string.Empty);
        WriteString16(writer, string.Empty);
        WriteString16(writer, string.Empty);
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write(checked((ushort)zoneLedCounts.Count));
        for (var index = 0; index < zoneLedCounts.Count; index++)
        {
            WriteString16(writer, $"Channel {index + 1}");
            writer.Write(0);
            writer.Write(0u);
            writer.Write(64u);
            writer.Write(checked((uint)zoneLedCounts[index]));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
        }

        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Flush();

        var payload = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(payload, checked((uint)payload.Length));
        return payload;
    }

    private static void WriteString16(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(checked((ushort)bytes.Length));
        writer.Write(bytes);
    }

    private static byte[] UInt32Payload(uint value)
    {
        var payload = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        return payload;
    }

    private sealed record Packet(uint DeviceId, uint PacketId, byte[] Payload);
}
