# Low-RTT consensus synchronization (v3)

This revision keeps the existing ±3 ms quality gate, five-attempt retry policy, 150 ms retry quiet interval, and timing-quiet STATUS behavior. It changes only how calibration and verification offsets are estimated.

For each synchronization attempt:

1. Collect 8 calibration timestamp exchanges.
2. Discard invalid samples, sort valid samples by network RTT, and keep the 3 lowest-RTT samples.
3. Apply the median Master-minus-local offset of those 3 samples. `BestSyncRttUs` remains the minimum RTT observed in that low-RTT set.
4. Collect 8 delay-free verification exchanges.
5. Again keep the 3 lowest-RTT samples and use their median offset. `VerifyRttUs` remains the minimum RTT in that set.
6. Compute residual = applied calibration offset - verification consensus offset and run the unchanged quality policy.

Using an odd count of 3 means the median offset is an actual measured sample, so the `SYNC_SET` packet can keep a real sync ID. One isolated asymmetric low-RTT exchange can no longer determine the applied or verification offset by itself.

The artificial asymmetric-delay experiment remains observable: when the forward path is persistently delayed by 250 ms, all three low-RTT calibration candidates carry approximately the same -125 ms NTP offset bias, so their median preserves that bias. Delay-free verification then measures the expected residual near -125 ms.
