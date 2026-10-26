using FactoryTimer.Protocol;

namespace FactoryTimer.Protocol.Tests;

[TestClass]
public sealed class WifiReconnectTelemetryTests
{
    private const string BaseStatus =
        "FCT2|STATUS|ESP03|0123456789ABCDEF|FINISHED|0|-57|6|AA:BB:CC:DD:EE:FF|LOCKED|129|0.500|0|1|0.000000|0|25.25|1|7|0|1000000|1000000|0|0|0|0|0|0|64|0|0|1|220B2|1|3C|wifi|1|3C|1|0|0|1|0|deadbeef|00";

    [TestMethod]
    public void LegacyStatusRemainsCompatible()
    {
        Assert.IsTrue(FactoryProtocol.TryParseInbound(BaseStatus, out var packet, out var error));
        Assert.AreEqual(ProtocolParseError.None, error);
        var status = (StatusPacket)packet!;
        Assert.AreEqual(45, status.StatusFieldCount);
        Assert.IsNull(status.WifiDisconnectCount);
        Assert.IsNull(status.WifiReconnectCount);
        Assert.IsNull(status.WifiLastReconnectUptimeMs);
    }

    [TestMethod]
    public void ExtendedStatusReadsThreeHexFields()
    {
        Assert.IsTrue(FactoryProtocol.TryParseInbound(BaseStatus + "|A|8|1E240", out var packet, out var error));
        Assert.AreEqual(ProtocolParseError.None, error);
        var status = (StatusPacket)packet!;
        Assert.AreEqual(48, status.StatusFieldCount);
        Assert.AreEqual(10U, status.WifiDisconnectCount);
        Assert.AreEqual(8U, status.WifiReconnectCount);
        Assert.AreEqual(123456UL, status.WifiLastReconnectUptimeMs);
        Assert.AreEqual((byte)0, status.DiagnosticFlags);
    }

    [TestMethod]
    public void MalformedTelemetryIsRejectedAndWrongFieldCountsAreNotAccepted()
    {
        foreach (var (wire, expected) in new[] {
            (BaseStatus + "|G|8|1E240", ProtocolParseError.WifiDisconnectCount),
            (BaseStatus + "|A|-1|1E240", ProtocolParseError.WifiReconnectCount),
            (BaseStatus + "|A|8|NOTHEX", ProtocolParseError.WifiLastReconnectUptimeMs),
        })
        {
            Assert.IsFalse(FactoryProtocol.TryParseInbound(wire, out _, out var error));
            Assert.AreEqual(expected, error);
        }
        Assert.IsFalse(FactoryProtocol.TryParseInbound(BaseStatus + "|A|8", out _, out var wrongLength));
        Assert.AreEqual(ProtocolParseError.FieldCount, wrongLength);
    }
}
