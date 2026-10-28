using FactoryTimer.Protocol;
namespace FactoryTimer.Protocol.Tests;
[TestClass]
public sealed class LogoOutputProtocolTests
{
    private static CommandPacket Command()=>new(CommandType.StartAt,11,20,0,9_000_000,PlanId:0x123,SegmentIndex:1,
        RepeatCount:3,FirstRepeat:1,TotalRepeats:3,ExpectedBootId:0x456,LogoSeconds:4);
    [TestMethod]
    public void LogoCommandIsExplicitAndLegacyPacketsStayUnchanged()
    {
        var c=Command();var wire=FactoryProtocol.SerializeCommand(c);
        Assert.AreEqual(14,wire.Split('|').Length);Assert.IsTrue(wire.EndsWith("|4",StringComparison.Ordinal));
        Assert.IsTrue(FactoryProtocol.TryParseCommand(wire,out var parsed,out _));Assert.AreEqual(c,parsed);
        Assert.AreEqual(72u,parsed!.SegmentDurationSeconds);
        Assert.AreEqual(13,FactoryProtocol.SerializeCommand(c with { LogoSeconds=0 }).Split('|').Length);
        foreach(uint value in new uint[]{1,3,5,uint.MaxValue})
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>FactoryProtocol.SerializeCommand(c with { LogoSeconds=value }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>FactoryProtocol.SerializeCommand(c with { PlanId=0 }));
    }
    [TestMethod]
    public void ExecutionWireFixtureMatchesFirmwareAndAnnouncesSupport()
    {
        const string wire="FCT2|EXEC_STATUS|ESP01|0000000000000789|0000000000000456|0000000000000123|1|000000000000000B|RUNNING|20|3|1|3|1|20|9000000|6000000|129|20000500|4";
        Assert.IsTrue(FactoryProtocol.TryParseInbound(wire,out var packet,out _));
        var e=(ExecutionStatusPacket)packet!;Assert.IsTrue(e.SupportsLogoOutput);Assert.AreEqual(72u,e.SegmentDurationSeconds);
        Assert.IsFalse(FactoryProtocol.TryParseInbound(wire[..wire.LastIndexOf('|')],out _,out _));
        Assert.IsFalse(FactoryProtocol.TryParseInbound(wire[..^1]+"3",out _,out _));
        const string legacy="FCT2|EXEC_STATUS|ESP01|0000000000000789|0000000000000456|0000000000000123|1|000000000000000B|RUNNING|5|30|1|30|2|5|9000000|6000000|129|5000500";
        Assert.IsTrue(FactoryProtocol.TryParseInbound(legacy,out packet,out _));Assert.IsFalse(((ExecutionStatusPacket)packet!).SupportsLogoOutput);
        Assert.IsTrue(FactoryProtocol.TryParseInbound(legacy+"|0",out packet,out _));Assert.IsTrue(((ExecutionStatusPacket)packet!).SupportsLogoOutput);
    }
    [TestMethod]
    public void MaximumCountdownIsPreservedButInternalGapsCannotExceedCap()
    {
        var c=Command() with { DurationSeconds=5999,RepeatCount=1,TotalRepeats=1 };
        Assert.AreEqual(6003u,c.SegmentDurationSeconds);Assert.IsTrue(FactoryProtocol.TryParseCommand(FactoryProtocol.SerializeCommand(c),out _,out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>FactoryProtocol.SerializeCommand(Command() with { DurationSeconds=3000,RepeatCount=2,TotalRepeats=2 }));
    }
}
