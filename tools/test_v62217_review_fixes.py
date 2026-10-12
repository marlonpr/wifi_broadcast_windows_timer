#!/usr/bin/env python3
"""Execute the live-identity logger against serial mocks and check controller wiring.

The actual C# state-machine scenarios are in RtcWarmupHistoryTests.cs and require
dotnet test; these portable checks do not claim to execute C#.
"""
from pathlib import Path
import contextlib
import importlib.util
import io
import sys
import tempfile
import types
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
TOOL = ROOT.parent / "firmware_v6_23_16/tools/monitor_board.py"
spec = importlib.util.spec_from_file_location("monitor_board_review", TOOL)
monitor = importlib.util.module_from_spec(spec); spec.loader.exec_module(monitor)

GOOD = b"I (100) rtc_discipline: RTC_QUAL device=ESP01 command=1 rate_ppm=4.225000\r\n"
BAD = GOOD.replace(b"ESP01", b"ESP03")


class SerialMock:
    def __init__(self, chunks, *, quiet=False):
        self.chunks = iter(chunks)
        self.in_waiting = 0
        self.quiet = quiet
        self.opened = False
        self.closed = False
        self.dtr = True
        self.rts = True
        self.rtscts = True
        self.dsrdtr = True
        self.port = None

    def read(self, _size):
        try:
            return next(self.chunks)
        except StopIteration:
            if self.quiet:
                return b""
            raise KeyboardInterrupt

    def open(self):
        assert self.dtr is False and self.rts is False
        assert self.rtscts is False and self.dsrdtr is False
        self.opened = True

    def close(self):
        self.closed = True


def capture(chunks):
    output = []
    try:
        monitor.capture_verified(SerialMock(chunks), "ESP01", output.append, clock=lambda: 0)
    except KeyboardInterrupt:
        pass
    return b"".join(output)


# Every byte split of a CRLF identity line verifies and preserves exact raw bytes.
for n in range(1, len(GOOD)):
    data = [b"earlier buffered output\r\n", GOOD[:n], GOOD[n:], b"later output\n"]
    assert capture(data) == b"".join(data)
assert capture([GOOD + GOOD + b"partial final line"]) == GOOD + GOOD + b"partial final line"
for n in range(1, len(BAD)):
    writes = []
    try:
        monitor.capture_verified(SerialMock([b"boot\n", BAD[:n], BAD[n:]]), "ESP01", writes.append, clock=lambda: 0)
    except monitor.BoardIdentityError as exc:
        assert "expected=ESP01 observed=ESP03" in str(exc)
    else:
        raise AssertionError("wrong live port accepted")
    assert writes == []
# Even after successful verification, a later different identity stops capture.
writes = []
try:
    monitor.capture_verified(SerialMock([GOOD, BAD]), "ESP01", writes.append, clock=lambda: 0)
except monitor.BoardIdentityError as exc:
    assert "PORT_IDENTITY_MISMATCH" in str(exc)
else:
    raise AssertionError("identity change ignored")
assert writes == [GOOD]
ticks = iter([0, 1, 35])
try:
    monitor.capture_verified(SerialMock([b"plain output\n"], quiet=True), "ESP01", lambda _: None,
                             clock=lambda: next(ticks))
except monitor.BoardIdentityError as exc:
    assert "PORT_IDENTITY_UNVERIFIED" in str(exc)
else:
    raise AssertionError("unidentified port did not time out")


class BinaryStdout:
    def __init__(self): self.buffer = io.BytesIO()


# Exercise main(), including lazy log creation, return codes and DTR/RTS ordering.
with tempfile.TemporaryDirectory() as td:
    td = Path(td); build = td / "build-esp01"; (build / "config").mkdir(parents=True)
    (build / "config/sdkconfig.h").write_text('#define CONFIG_FACTORY_DEVICE_ID "ESP01"\n')
    (build / "project_description.json").write_text('{"app_elf":"factory.elf"}')
    (build / "factory.elf").write_bytes(b"test ELF")
    for valid in (False, True):
        uart = SerialMock([GOOD if valid else BAD])
        fake_serial = types.SimpleNamespace(Serial=lambda **_: uart)
        log = td / ("good.log" if valid else "wrong-port.log")
        args = [str(TOOL), "--board", "ESP01", "--build", str(build), "--port", "COM14", "--log", str(log)]
        stdout = BinaryStdout(); stderr = io.StringIO()
        with patch.dict(sys.modules, {"serial": fake_serial}), patch.object(sys, "argv", args), \
                patch.object(sys, "stdout", stdout), contextlib.redirect_stderr(stderr):
            result = monitor.main()
        assert uart.opened and uart.closed
        assert result == (0 if valid else 2)
        if valid:
            assert log.read_bytes() == GOOD and stdout.buffer.getvalue() == GOOD
            assert "PORT_IDENTITY_VERIFIED" in stderr.getvalue()
        else:
            assert not log.exists() and stdout.buffer.getvalue() == b""
            assert "expected=ESP01 observed=ESP03" in stderr.getvalue()

# Supplement pending .NET tests with cross-file integration checks.
app = ROOT / "windows-controller/FactoryTimer.Controller"
view = (app / "MainViewModel.cs").read_text()
post_sync = view[view.index("private async Task<IReadOnlyList<StartBlock>> RefreshAndValidatePostSyncStatusAsync("):]
post_sync = post_sync[:post_sync.index("return blocks;")]
assert post_sync.index("BeginPostSyncPoll") < post_sync.index("RequestFreshStatusesAsync")
assert "qualificationStart1Rates.TryAdd(status.DeviceId, rate)" in view
assert "RtcRateChangeSinceStart1Ppm" in view and "Start1RtcRatePpmVsRtc" in view
assert "{Binding QualificationSecondStartRateText}" in (app / "MainWindow.xaml").read_text()
assert 'new Dictionary<string, double>(qualificationStart1Rates, StringComparer.Ordinal)' in view
core = (ROOT / "windows-controller/FactoryTimer.Controller.Core/RtcWarmupHistory.cs").read_text()
assert "if (discoveryReply)\n            goodFitPolls" in core
assert "postSyncPollConsumed = true" in core
assert "24_000_000" in core and "48_000_000" in core and "600_000_000" in core
tests = (ROOT / "tests/FactoryTimer.Controller.Core.Tests/RtcWarmupHistoryTests.cs").read_text()
assert "PostSyncReplyRefreshesHistoryAfterSlowSyncBeforeDiscoveryCanReply" in tests
assert "ForegroundRefreshesCannotManufactureGoodFitPollsOrConcealContinuityFailures" in tests
assert "Start2RateChangeUsesActualStart1ReadingAndIsTelemetryOnly" in tests
print("V6_22_17_REVIEW_FIXES=PASS: logger split-line identity, mismatch/timeout, no wrong-port log, "
      "DTR/RTS-before-open and close; post-sync history / START2 rate audit wiring; C# scenarios included (not executed)")
