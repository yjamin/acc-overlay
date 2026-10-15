# AccOverlay

Shows live ACC pedal / steering / gear inputs as an overlay while driving. Also records them to csv and replays them in sync when you watch an ACC replay.

Run ACC in borderless window. Exclusive fullscreen can't be overlaid.

## Build

    dotnet build

Binaries end up in `bin/<Config>/net10.0-windows/`. `AccOverlayWatcher.exe` is built in the same folder.

Requires .NET 10, Windows, x64.

## Run

    AccOverlay.exe

Follows the ACC window by default (bottom center). Or pin it:

    AccOverlay.exe --fixed 860 680 --w 600 --h 250

## Options

    [--fixed x y] [--w width] [--h height]
    [--dump file.csv] [--no-record] [--play file.csv] [--diag diag.csv]
    [--follow-replay file.csv] [--follow-log file.csv] [--udp-port N] [--udp-pass pw]
    [--hidden] [--install-autostart] [--uninstall-autostart]

Defaults: udp port 9000, pass `asd`.

Live runs record to `recordings/session-<time>.csv` unless `--no-record` or `--dump` is given. `--play` plays back a file instead of live data. `--diag` dumps raw shared memory at 10Hz. `--udp-probe` logs broadcast traffic for debugging.

## Replay

No extra step. If a recording for the session exists, the overlay switches to it during replay.

Nudge sync with LEFT / RIGHT (0.1s, Shift = 1s), R resets.

In `--play` mode: SPACE play/pause, LEFT/RIGHT seek 5s, R restart, +/- speed.

## Autostart

    AccOverlay.exe --install-autostart
    AccOverlay.exe --uninstall-autostart

Install registers `AccOverlayWatcher.exe` in HKCU Run. The watcher starts `AccOverlay.exe --hidden` while `AC2-Win64-Shipping` runs and kills it when ACC exits.
