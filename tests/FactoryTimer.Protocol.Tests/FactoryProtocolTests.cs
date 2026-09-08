using System.Text;
using FactoryTimer.Protocol;

namespace FactoryTimer.Protocol.Tests;

[TestClass]
public sealed class FactoryProtocolTests
{
    [TestMethod]
    public void StartSerializationIsExactAndRoundTrips()
    {
        var original = new CommandPacket(CommandType.Start, 0x0123456789abcdef, 20, 1000);

        string serialized = FactoryProtocol.SerializeCommand(original);

        Assert.AreEqual("FCT1|CMD|START|0123456789ABCDEF|20|1000", serialized);
        Assert.IsTrue(FactoryProtocol.TryParseCommand(serialized, out CommandPacket? parsed, out _));
        Assert.AreEqual(original, parsed);
    }


    [TestMethod]
    public void AbsoluteStartSerializationIsExactAndRoundTrips()
    {
        var original = new CommandPacket(
            CommandType.StartAt,
            0x1111222233334444,
            20,
            0,
            5_000_000);

        string serialized = FactoryProtocol.SerializeCommand(original);

        Assert.AreEqual(
            "FCT2|CMD|START_AT|1111222233334444|20|5000000",
            serialized);
        Assert.IsTrue(FactoryProtocol.TryParseCommand(serialized, out CommandPacket? parsed, out _));
        Assert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void SynchronizationPacketsSerializeAndParse()
    {
        Assert.AreEqual(
            "FCT2|SYNC|0123456789ABCDEF|100000",
            FactoryProtocol.SerializeSyncRequest(
                new SyncRequestPacket(0x0123456789abcdef, 100_000)));

        Assert.AreEqual(
            "FCT2|SYNC|0123456789ABCDEF|100000|250000",
            FactoryProtocol.SerializeSyncRequest(
                new SyncRequestPacket(0x0123456789abcdef, 100_000, 250_000)));

        Assert.AreEqual(
            "FCT2|SYNC_SET|0123456789ABCDEF|60001|10000",
            FactoryProtocol.SerializeSyncSet(
                new SyncSetPacket(0x0123456789abcdef, 60_001, 10_000)));

        Assert.IsTrue(FactoryProtocol.TryParseInbound(
            "FCT2|SYNC_REPLY|ESP01|0123456789ABCDEF|100000|40004|40006",
            out InboundPacket? reply,
            out _));
        Assert.AreEqual(
            new SyncReplyPacket("ESP01", 0x0123456789abcdef, 100_000, 40_004, 40_006),
            reply);

        Assert.IsTrue(FactoryProtocol.TryParseInbound(
            "FCT2|SYNC_APPLIED|ESP01|0123456789ABCDEF|60001|10000",
            out InboundPacket? applied,
            out _));
        Assert.AreEqual(
            new SyncAppliedPacket("ESP01", 0x0123456789abcdef, 60_001, 10_000),
            applied);

        Assert.IsTrue(FactoryProtocol.TryParseInbound(
            "FCT2|STARTED|ESP01|1111222233334444|2000000|5000002|5000000",
            out InboundPacket? started,
            out _));
        Assert.AreEqual(
            new StartedPacket("ESP01", 0x1111222233334444, 2_000_000, 5_000_002, 5_000_000),
            started);
    }

    [TestMethod]
    public void ResetSerializationIsExactAndRoundTrips()
    {
        var original = new CommandPacket(CommandType.Reset, 0xfedcba9876543210, 20, 0);

        string serialized = FactoryProtocol.SerializeCommand(original);

        Assert.AreEqual("FCT1|CMD|RESET|FEDCBA9876543210|20|0", serialized);
        Assert.IsTrue(FactoryProtocol.TryParseCommand(serialized, out CommandPacket? parsed, out _));
        Assert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void StatusRequestSerializationIsExactAndRoundTrips()
    {
        var original = new CommandPacket(
            CommandType.StatusRequest,
            0x0fedcba987654321,
            0,
            0);

        string serialized = FactoryProtocol.SerializeCommand(original);

        Assert.AreEqual(
            "FCT1|CMD|STATUS_REQUEST|0FEDCBA987654321|0|0",
            serialized);
        Assert.IsTrue(FactoryProtocol.TryParseCommand(serialized, out CommandPacket? parsed, out _));
        Assert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void ParsesAckAndStatus()
    {
        Assert.IsTrue(FactoryProtocol.TryParseInbound(
            "FCT1|ACK|ESP01|0123456789ABCDEF|START|ACCEPTED", out InboundPacket? ack, out _));
        Assert.AreEqual(
            new AckPacket("ESP01", 0x0123456789abcdef, CommandType.Start, AckResult.Accepted), ack);

        Assert.IsTrue(FactoryProtocol.TryParseInbound(
            Encoding.ASCII.GetBytes("FCT1|STATUS|ESP02|0123456789ABCDEF|RUNNING|19"),
            out InboundPacket? status,
            out _));
        Assert.AreEqual(
            new StatusPacket("ESP02", 0x0123456789abcdef, TimerState.Running, 19), status);

        Assert.IsTrue(FactoryProtocol.TryParseInbound(
            Encoding.ASCII.GetBytes("FCT2|STATUS|ESP03|0123456789ABCDEF|RUNNING|19|-57|6|AA:BB:CC:DD:EE:FF"),
            out InboundPacket? extendedStatus,
            out _));
        Assert.AreEqual(
            new StatusPacket("ESP03", 0x0123456789abcdef, TimerState.Running, 19, -57, 6, "AA:BB:CC:DD:EE:FF"),
            extendedStatus);
    }

    [TestMethod]
    [DataRow("", ProtocolParseError.Empty)]
    [DataRow("FCT2|ACK|ESP01|0123456789ABCDEF|START|ACCEPTED", ProtocolParseError.Version)]
    [DataRow("FCT1|NOPE|ESP01|0123456789ABCDEF|START|ACCEPTED", ProtocolParseError.PacketType)]
    [DataRow("FCT1|ACK|esp01|0123456789ABCDEF|START|ACCEPTED", ProtocolParseError.DeviceId)]
    [DataRow("FCT1|ACK|ESP01|0000000000000000|START|ACCEPTED", ProtocolParseError.CommandId)]
    [DataRow("FCT1|ACK|ESP01|0123456789ABCDEF|PAUSE|ACCEPTED", ProtocolParseError.CommandType)]
    [DataRow("FCT1|ACK|ESP01|0123456789ABCDEF|START|MAYBE", ProtocolParseError.AckResult)]
    [DataRow("FCT1|STATUS|ESP01|0123456789ABCDEF|BROKEN|19", ProtocolParseError.State)]
    [DataRow("FCT1|STATUS|ESP01|0123456789ABCDEF|RUNNING|-1", ProtocolParseError.Remaining)]
    [DataRow("FCT1|STATUS|ESP01|0123456789ABCDEF|RUNNING", ProtocolParseError.FieldCount)]
    [DataRow("FCT2|STATUS|ESP01|0123456789ABCDEF|RUNNING|19|-200|6|AA:BB:CC:DD:EE:FF", ProtocolParseError.Rssi)]
    [DataRow("FCT2|STATUS|ESP01|0123456789ABCDEF|RUNNING|19|-50|999|AA:BB:CC:DD:EE:FF", ProtocolParseError.Channel)]
    [DataRow("FCT2|STATUS|ESP01|0123456789ABCDEF|RUNNING|19|-50|6|NOT-A-BSSID", ProtocolParseError.Bssid)]
    public void RejectsMalformedOrUnsupportedInbound(string text, ProtocolParseError expected)
    {
        Assert.IsFalse(FactoryProtocol.TryParseInbound(text, out _, out ProtocolParseError actual));
        Assert.AreEqual(expected, actual);
    }

    [TestMethod]
    public void RejectsNonAsciiAndOversizedPackets()
    {
        Assert.IsFalse(FactoryProtocol.TryParseInbound([0x46, 0x00], out _, out ProtocolParseError binaryError));
        Assert.AreEqual(ProtocolParseError.NonPrintable, binaryError);
        Assert.IsFalse(FactoryProtocol.TryParseInbound(new string('X', 192), out _, out ProtocolParseError longError));
        Assert.AreEqual(ProtocolParseError.TooLong, longError);
    }

    [TestMethod]
    [DataRow("FCT1|CMD|START|0123456789ABCDEF|0|1000", ProtocolParseError.Duration)]
    [DataRow("FCT1|CMD|START|0123456789ABCDEF|20|99", ProtocolParseError.StartDelay)]
    [DataRow("FCT1|CMD|RESET|0123456789ABCDEF|20|1000", ProtocolParseError.StartDelay)]
    [DataRow("FCT1|CMD|STATUS_REQUEST|0123456789ABCDEF|1|0", ProtocolParseError.Duration)]
    [DataRow("FCT1|CMD|STATUS_REQUEST|0123456789ABCDEF|0|1", ProtocolParseError.StartDelay)]
    [DataRow("FCT1|CMD|START|0000000000000000|20|1000", ProtocolParseError.CommandId)]
    [DataRow("FCT1|CMD|PAUSE|0123456789ABCDEF|20|1000", ProtocolParseError.CommandType)]
    public void RejectsInvalidCommands(string text, ProtocolParseError expected)
    {
        Assert.IsFalse(FactoryProtocol.TryParseCommand(text, out _, out ProtocolParseError actual));
        Assert.AreEqual(expected, actual);
    }
}
