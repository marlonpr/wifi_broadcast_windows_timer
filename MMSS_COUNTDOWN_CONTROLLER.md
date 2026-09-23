# Minutes + seconds countdown control

The controller now accepts countdown duration as two fields:

- Minutes: `0..99`
- Seconds: `0..59`
- Valid combined range: `00:01..99:59`

The app converts the two fields to total seconds before creating the existing FCT1/FCT2 START / START_AT packet, so the wire protocol is unchanged.

The large controller timer and each device card Remaining field are formatted as `MM:SS`. Brightness control and all synchronization/start orchestration are unchanged.

## Start timing policy

Countdown duration is independent of the production synchronization readiness gate. The gate uses the accepted synchronization residual plus the conservative 50 ppm ageing term only up to the common absolute start instant `T*`. For two or more participants, the two largest per-device start uncertainties must sum to no more than 20 ms.

After `T*`, devices run the requested countdown locally. Therefore `00:01` and `99:59` use the same START acceptance rule. The 20 ms guarantee is a start-instant guarantee; it is not a claim that every later second transition or the final `00:00` remains within 20 ms across the fleet.

