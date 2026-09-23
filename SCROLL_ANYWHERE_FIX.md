# Full-window mouse-wheel scrolling

The operator page keeps its centered `MaxWidth="1180"` content layout, but the root `ScrollViewer` now explicitly fills the complete window and has a transparent background.

This makes the otherwise empty left/right margins hit-testable by the page `ScrollViewer`, so the mouse wheel can scroll while the pointer is over any blank area of the window, including the right side beyond the centered operator card.

The change is UI-only. Timer selection, synchronization, START/RESET, brightness, UDP protocol, device identities, and firmware behavior are unchanged.
