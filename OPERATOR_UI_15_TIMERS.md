# Operator UI — 15 timers

This controller build replaces the diagnostic-heavy main window with an operator-only view.

Visible controls:

- countdown MM:SS and state;
- duration (00:01 through 99:59);
- panel brightness for selected timers;
- TIMER01 through TIMER15 individual selection;
- SELECT ALL / DESELECT ALL;
- START and RESET;
- per-timer ONLINE/OFFLINE, state, and remaining MM:SS;
- physical network-interface selector and operator status messages.

The diagnostic functions remain in source for engineering builds but are not exposed in the operator window.

## Identity compatibility

The operator names are TIMER01..TIMER15. On the wire the controller still expects ESP01..ESP15, so existing ESP01..ESP05 firmware is compatible without reflashing merely for the UI rename. TIMER06..TIMER15 require devices whose firmware identity is ESP06..ESP15.

For a safe upgrade from the current five-device installation, TIMER01..TIMER05 start selected and TIMER06..TIMER15 start deselected. The operator can then select any timer individually or use SELECT ALL.
