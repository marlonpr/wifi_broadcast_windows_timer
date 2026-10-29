using FactoryTimer.Protocol;
namespace FactoryTimer.Protocol.Tests;
[TestClass]
public sealed class RepetitionStartProtocolTests
{
    private static CommandPacket Command()=>new(CommandType.StartAt,11,20,0,9_000_000,PlanId:0x123,SegmentIndex:1,
        RepeatCount:3,FirstRepeat:1,TotalRepeats:3,ExpectedBootId:0x456,LogoSeconds:4,RepeatStartSeconds:4);
    [TestMethod]
    public void OptionalStartSignalIsExplicitAndCannotSilentlyFallBackToOldGeometry()
    {
        var c=Command();var wire=FactoryProtocol.SerializeCommand(c);
        Assert.AreEqual(15,wire.Split('|').Length);Assert.IsTrue(wire.EndsWith("|4|4",StringComparison.Ordinal));
        Assert.IsTrue(FactoryProtocol.TryParseCommand(wire,out var parsed,out _));Assert.AreEqual(c,parsed);
        Assert.AreEqual(80u,parsed!.SegmentDurationSeconds);
        foreach(string suffix in new[]{"0","3","5","4294967295","4|4"})
            Assert.IsFalse(FactoryProtocol.TryParseCommand(wire[..^1]+suffix,out _,out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>FactoryProtocol.SerializeCommand(c with{LogoSeconds=0}));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>FactoryProtocol.SerializeCommand(c with{RepeatStartSeconds=3}));
    }
    [TestMethod]
    public void NewExecutionFixtureMatchesFirmwareAndKeepsLegacyCapabilitiesDistinct()
    {
        const string wire="FCT2|EXEC_STATUS|ESP01|0000000000000789|0000000000000456|0000000000000123|1|000000000000000B|RUNNING|20|3|1|3|2|24|9000000|6000000|129|24000500|4|4";
        Assert.IsTrue(FactoryProtocol.TryParseInbound(wire,out var packet,out _));
        var e=(ExecutionStatusPacket)packet!;
        Assert.IsTrue(e.SupportsLogoOutput && e.SupportsRepetitionStart);Assert.AreEqual(80u,e.SegmentDurationSeconds);
        Assert.AreEqual(2u,e.CurrentRepeat);
        Assert.IsFalse(FactoryProtocol.TryParseInbound(wire.Replace("|3|2|24|","|3|1|24|",StringComparison.Ordinal),out _,out _));
        Assert.IsFalse(FactoryProtocol.TryParseInbound(wire[..^1]+"3",out _,out _));
        const string idle="FCT2|EXEC_STATUS|ESP01|0000000000000789|0000000000000456|0000000000000000|0|0000000000000000|READY|20|1|1|1|1|0|0|0|129|0";
        foreach(var (suffix,logo,start) in new[]{("",false,false),("|0",true,false),("|0|0",true,true)}){
            Assert.IsTrue(FactoryProtocol.TryParseInbound(idle+suffix,out packet,out _));
            e=(ExecutionStatusPacket)packet!;Assert.AreEqual(logo,e.SupportsLogoOutput);Assert.AreEqual(start,e.SupportsRepetitionStart);
        }
    }
    [TestMethod]
    public void CountdownsMustStillFitTheAbsoluteSegmentCapWithBothGaps()
    {
        var c=Command() with{DurationSeconds=2996,RepeatCount=2,TotalRepeats=2};
        Assert.AreEqual(6004u,c.SegmentDurationSeconds);
        Assert.IsTrue(FactoryProtocol.TryParseCommand(FactoryProtocol.SerializeCommand(c),out _,out _));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>FactoryProtocol.SerializeCommand(c with{DurationSeconds=2997}));
        Assert.AreEqual(6003u,(Command() with{DurationSeconds=5999,RepeatCount=1,TotalRepeats=1}).SegmentDurationSeconds);
    }
}
