using FactoryTimer.Protocol;

namespace FactoryTimer.Protocol.Tests;

[TestClass]
public sealed class DiagnosticFlagsTests
{
    private const string ProductionStatus =
        "FCT2|STATUS|ESP01|0123456789ABCDEF|READY|0|-57|6|AA:BB:CC:DD:EE:FF|LOCKED|129|0.500|0|1|0.000000|0|25.25|1|7|0|1000000|1000000|0|0|0|0|0|0|64|0|0|1|220B2|1|3C|wifi|1|3C|1|0|0|1|0|deadbeef|00";

    [TestMethod]
    public void DiagnosticByteIsIndependentOfRuntimeHealthFlags()
    {
        foreach (byte flags in new byte[] { 0, 1, 2, 4, 8, 16, 31, 128, 255 })
        {
            string[] fields = ProductionStatus.Split('|');
            fields[44] = flags.ToString("X2");
            Assert.IsTrue(FactoryProtocol.TryParseInbound(string.Join('|', fields), out InboundPacket? packet, out ProtocolParseError error));
            Assert.AreEqual(ProtocolParseError.None, error);
            var status = (StatusPacket)packet!;
            Assert.AreEqual(45, status.StatusFieldCount);
            Assert.AreEqual(flags, status.DiagnosticFlags);
            Assert.AreEqual((byte)7, status.HealthFlags);
        }
    }

    [TestMethod]
    public void MalformedDiagnosticBytesAreRejected()
    {
        foreach (string flags in new[] { "", "0", "000", "0x04", "GG", " 0", "+4", "-1" })
        {
            string[] fields = ProductionStatus.Split('|');
            fields[44] = flags;
            Assert.IsFalse(FactoryProtocol.TryParseInbound(string.Join('|', fields), out _, out ProtocolParseError error));
            Assert.AreEqual(ProtocolParseError.DiagnosticFlags, error);
        }
    }

    [TestMethod]
    public void QueueDropsUseHexOnlyInTheNewLayoutAndLegacyFlagsRemainUnknown()
    {
        string[] fields = ProductionStatus.Split('|');
        fields[12] = "FFFFFFFF";
        Assert.IsTrue(FactoryProtocol.TryParseInbound(string.Join('|', fields), out InboundPacket? packet, out _));
        Assert.AreEqual(uint.MaxValue, ((StatusPacket)packet!).RtcQueueDrops);

        fields[12] = "4294967295";
        Assert.IsTrue(FactoryProtocol.TryParseInbound(string.Join("|", fields.Take(44)), out packet, out _));
        var legacy = (StatusPacket)packet!;
        Assert.AreEqual(uint.MaxValue, legacy.RtcQueueDrops);
        Assert.AreEqual(44, legacy.StatusFieldCount);
        Assert.IsNull(legacy.DiagnosticFlags);

        fields[12] = "10";
        Assert.IsTrue(FactoryProtocol.TryParseInbound(string.Join('|', fields), out packet, out _));
        Assert.AreEqual(16U, ((StatusPacket)packet!).RtcQueueDrops);
        Assert.IsTrue(FactoryProtocol.TryParseInbound(string.Join("|", fields.Take(44)), out packet, out _));
        Assert.AreEqual(10U, ((StatusPacket)packet!).RtcQueueDrops);
    }

    [TestMethod]
    public void ActualFirmwareMaximumWidthPacketParsesWithoutTruncation()
    {
        string wire = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "status_v62319_max_width.txt")).TrimEnd('\r', '\n');
        Assert.AreEqual(511, wire.Length);
        Assert.IsTrue(FactoryProtocol.TryParseInbound(wire, out InboundPacket? packet, out ProtocolParseError error));
        Assert.AreEqual(ProtocolParseError.None, error);
        var status = (StatusPacket)packet!;
        Assert.AreEqual(45, status.StatusFieldCount);
        Assert.AreEqual((byte)255, status.DiagnosticFlags);
        Assert.AreEqual(uint.MaxValue, status.RtcQueueDrops);
        Assert.AreEqual(ulong.MaxValue, status.RtcAcceptedEdges);
        Assert.AreEqual(long.MaxValue, status.SyncEpochLocalMicroseconds);
        Assert.AreEqual(long.MinValue, status.StartErrorMicroseconds);
    }
}
