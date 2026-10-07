using FactoryTimer.Protocol;
namespace FactoryTimer.Protocol.Tests;

[TestClass]
public sealed class RunDiagnosticTests
{
    private const string ValidPacket = "FCT2|RUN_DIAG|ESP01|0123456789ABCDEF|1|251|50|1000000|6000000|36000000|100|220B2|220B2|0|1|0|1|0|1|0|0";

    [TestMethod]
    public void RunDiagnosticParsesActualThirtyFiveSecondWindow()
    {
        Assert.IsTrue(FactoryProtocol.TryParseInbound(ValidPacket, out InboundPacket? packet, out ProtocolParseError error));
        Assert.AreEqual(ProtocolParseError.None, error);
        Assert.IsInstanceOfType<RunDiagnosticPacket>(packet);
        var diagnostic = (RunDiagnosticPacket)packet!;
        Assert.AreEqual(35_000_000L, diagnostic.MonitorElapsedMicroseconds);
        Assert.AreEqual(-5_000_000L, diagnostic.MonitorStartToTStarMicroseconds);
        Assert.AreEqual(139442UL, diagnostic.ExpectedPeriods);
        Assert.AreEqual(1U, diagnostic.WifiEventsGe50);
        Assert.AreEqual(0U, diagnostic.CommitGe300);
        Assert.IsTrue(ValidPacket.Length <= FactoryProtocol.MaximumPacketLength);
    }

    [TestMethod]
    public void RunDiagnosticRejectsWrongEndpointAndImpossibleTaskCounters()
    {
        string[] fields = ValidPacket.Split('|');
        fields[9] = "35000000";
        Assert.IsFalse(FactoryProtocol.TryParseInbound(string.Join('|', fields), out _, out _));
        fields = ValidPacket.Split('|');
        fields[15] = "2";
        Assert.IsFalse(FactoryProtocol.TryParseInbound(string.Join('|', fields), out _, out _));
        Assert.IsFalse(FactoryProtocol.TryParseInbound(ValidPacket + "|1", out _, out ProtocolParseError error));
        Assert.AreEqual(ProtocolParseError.FieldCount, error);
    }
}
