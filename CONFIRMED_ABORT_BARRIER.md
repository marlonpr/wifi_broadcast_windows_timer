# Confirmed abort barrier

This controller revision removes the unsafe assumption that sending RESET is equivalent to proving cancellation.

## Timeline

- `T* - 3.5 s`: begin final STATUS barrier after all START_AT ACKs are present.
- `T* - 2.5 s`: normal ARM preparation deadline. Any unresolved ARM/final-status condition aborts.
- `T* - 2.0 s`: successful-run session guard closes, preserving the established autonomous final two seconds for runs that passed.
- `T* - 1.0 s`: absolute latest cancellation-confirmation deadline for failed preparations. Each confirmation attempt is also capped at 1.5 s so an early/manual cancel cannot block the controller for many seconds.

## Failed preparation

The controller creates one new RESET CommandId for the entire frozen set and then:

1. sends RESET to participants whose RESET ACK is missing;
2. accepts RESET `ACCEPTED` or `DUPLICATE` as ACK evidence;
3. requests STATUS when ACK exists but the RESET's immediate STATUS is missing;
4. requires a STATUS observed after cancellation began that reports `READY` and either the RESET CommandId or `0` after a reboot;
5. reports `ABORT CONFIRMED` only when both RESET ACK and safe STATUS evidence exist for every participant.

If any participant is still missing either proof at `T* - 1.0 s`, the result is `AbortUnconfirmed` and the UI warns that an abandoned START_AT may still execute. A final redundant RESET burst is attempted but is not counted as confirmation.

## Scope

No ESP32 firmware or protocol change is required. Existing firmware already ACKs RESET, immediately returns STATUS, cancels the high-resolution start timer, clears scheduled START metadata, and returns the timer state to `READY`. The same confirmation helper is also used by manual START_AT cancel/abort paths.
