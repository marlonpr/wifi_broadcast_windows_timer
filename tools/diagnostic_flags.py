"""Compile-time diagnostic flags carried by v6.23.19 STATUS / health CSVs."""

FLAG_NAMES = (
    (0x01, "TASK_CANARY_ENABLED"),
    (0x02, "ISR_CANARY_ENABLED"),
    (0x04, "START_TASK_DELAY_TEST"),
    (0x08, "GPIO_PROBE / timing diagnostic"),
    (0x10, "MONITOR_OFF_A_B"),
)


def parse_flags(text: str | None) -> int | None:
    if text is None or not text.strip():
        return None
    value = text.strip()
    if value[:2].lower() == "0x":
        value = value[2:]
    if len(value) != 2 or any(c not in "0123456789abcdefABCDEF" for c in value):
        raise ValueError(f"invalid DiagnosticFlags {text!r}; expected a hex byte")
    return int(value, 16)


def describe_flags(flags: int | None) -> str:
    if flags is None:
        return "DiagnosticFlags unavailable; v6.23.19-or-newer STATUS required"
    names = [name for bit, name in FLAG_NAMES if flags & bit]
    if flags & 0xE0:
        names.append(f"UNKNOWN(0x{flags & 0xE0:02X})")
    return f"DiagnosticFlags=0x{flags:02X} ({', '.join(names) if names else 'NONE'})"
