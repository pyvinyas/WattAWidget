# Changelog

## 1.0.0 — 2026-07-30

Initial release.

- Desktop-pinned power widget: battery telemetry on battery, CPU+GPU sensor
  estimate on AC with a self-calibrating base-load offset
- Wall-draw estimate while charging; CPU/GPU split in the sub-line
- 30-minute sparkline, 24-hour and 7-day history views (double-click cycles)
- Hour bars colored by source (battery load colors vs. AC blue)
- kWh totals with optional electricity-cost estimate in local currency
- Light/dark theme, follows Windows by default
- Battery health dialog (design vs. full-charge capacity, cycle count)
- Survives Win+D, follows across virtual desktops, sits above the wallpaper
  (including Windows Spotlight) but below all app windows
- Single-file exe; history persisted as per-minute CSVs in
  `%LOCALAPPDATA%\WattAWidget`
