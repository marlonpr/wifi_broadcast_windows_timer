using FactoryTimer.Controller.Core;

namespace FactoryTimer.Controller.Core.Tests;

[TestClass]
public sealed class InitialRosterAdmissionTests
{
    [TestMethod]
    public void MissingTimerBlocksWithoutCreatingAProgram()
    {
        IReadOnlyList<StartBlock> blocks = InitialRosterAdmission.Evaluate([
            new("ESP01", false, false), new("ESP02", true, true),
            new("ESP04", false, false), new("ESP05", true, true)]);

        Assert.AreEqual(2, blocks.Count);
        CollectionAssert.AreEquivalent(new[] { "ESP01", "ESP04" },
            blocks.Select(b => b.DeviceId).ToArray());
        Assert.IsTrue(blocks.All(b => b.Reason == StartBlockReason.MissingExpectedDevice));
    }

    [TestMethod]
    public void CachedIpOfOfflineTimerIsNotSufficient()
    {
        IReadOnlyList<StartBlock> blocks = InitialRosterAdmission.Evaluate([
            new("ESP03", true, false)]);
        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(StartBlockReason.Offline, blocks[0].Reason);
    }

    [TestMethod]
    public void OnlineRosterPassesAdmission()
    {
        IReadOnlyList<StartBlock> blocks = InitialRosterAdmission.Evaluate([
            new("ESP02", true, true), new("ESP03", true, true)]);
        Assert.AreEqual(0, blocks.Count);
    }

    [TestMethod]
    public void EmptySelectionIsRejected()
    {
        IReadOnlyList<StartBlock> blocks = InitialRosterAdmission.Evaluate([]);
        Assert.AreEqual(1, blocks.Count);
        Assert.AreEqual(StartBlockReason.MissingExpectedDevice, blocks[0].Reason);
    }
}
