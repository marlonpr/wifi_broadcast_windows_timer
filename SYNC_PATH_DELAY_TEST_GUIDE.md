# SYNC path-delay test guide

This build replaces the old "delay ESP02 session/START delivery" test with a controlled NTP-path experiment.

## Test setup

ESP01, ESP03, ESP04, and ESP05 are always no-delay controls. ESP02 receives the selected artificial delay only during the 8 calibration exchanges. The 8 verification exchanges are always delay-free, and START_AT uses the normal three-copy broadcast with no artificial delay.

## Mode 1: NONE

Select `NONE — 0 / 0 ms`, press **SYNC CLOCKS**, then **START_AT**. Record all five devices' best sync RTT, verify RTT, clock error, verification-corrected START error and fleet spread. This is the baseline.

## Mode 2: SYMMETRIC

Select `SYMMETRIC — 250 / 250 ms`. ESP02 calibration low-RTT consensus should rise by roughly 500 ms relative to normal Wi-Fi RTT, but its delay-free verification residual should remain close to the normal few-millisecond range. START should remain close to the baseline.

## Mode 3: ASYMMETRIC

Select `ASYMMETRIC — 250 / 0 ms`. ESP02 calibration RTT should rise by roughly 250 ms and its applied offset should be biased by approximately -125 ms. Because verification is delay-free, ESP02 clock error should expose roughly -125 ms residual. The verification-corrected START estimate should show ESP02 starting roughly +125 ms late relative to the requested Master target.

Exact values will include Windows scheduling and Wi-Fi jitter; the experiment is intended to demonstrate the direction and scale of the NTP asymmetry effect, not synthesize an exact laboratory delay.


## Mode-change safety

Changing the SYNC path-delay selector now clears all five devices' synchronization state. This prevents a START_AT test from reusing an offset measured under a previous delay mode. After selecting NONE, SYMMETRIC, or ASYMMETRIC, press **SYNC CLOCKS** before START_AT.
