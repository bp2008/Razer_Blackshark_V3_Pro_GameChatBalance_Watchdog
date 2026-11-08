# BlackShark V3 Pro Game/Chat Balance Watchdog

A small Windows tray app for the Razer BlackShark V3 Pro. It keeps the **Game/Chat Balance** and the **mic sidetone** level where you put them. It works around a Razer Synapse bug that, after the headset powers on or reconnects, can:
- pin the balance to one extreme, which silences the Chat audio device (Teams, Discord, etc.);
- push sidetone to its maximum.

It also shows an on-screen panel, like the volume flyout, whenever something changes. The panel shows the balance slider, volume, mic level, sidetone, battery and link stability.

> Unofficial. Not affiliated with or endorsed by Razer.

## The bug

When the headset powers on or reconnects, Synapse queries its settings about 1.5 s after the dongle reports it connected. Sometimes the headset isn't ready yet and answers every query with `255`. Synapse range-checks some settings (it logs `Invalid mode: 255` for HyperSpeed) but not the balance or the sidetone level.

**Balance:**
1. Synapse reads the balance as `255` and converts it with `balanceMode = 20 - raw`, giving **-235**.
2. It saves `-235` to the profile, then writes `20 - (-235) = 255` back to the headset. It repeats this several times over about 15 seconds.
3. The headset firmware accepts 255 without clamping, so the balance is pinned and Chat goes silent until you move the slider by hand.

**Sidetone:** Synapse reads the level as `255` and writes `255` back. The firmware caps it at its maximum (15), so you hear your own mic at full volume.

When the link is flaky (at the edge of range), the headset can reconnect several times in a few seconds, and each reconnect is another chance to hit the race.

You can see it in Synapse's logs. Running `BalanceWatchdog scan` prints every balance and sidetone read and write Synapse has logged, and flags the invalid ones:

```
2026/11/04 17:31:47.059  read balance raw 255 from headset => balanceMode -235   <-- INVALID
2026/11/04 17:31:47.191  read balance raw 10 from headset => balanceMode 10
2026/11/04 17:31:47.831  re-applied stored balance (version 743) => balanceMode -235   <-- INVALID
2026/11/04 17:31:48.076  wrote balance raw 255 to headset => balanceMode -235   <-- INVALID
...
2026/11/08 14:46:51.877  read sidetone 255 from headset   <-- INVALID
2026/11/08 14:46:58.044  wrote sidetone 255 to headset   <-- INVALID
```

In the first example, the headset returned the correct value 130 ms after the bad one, but Synapse had already stored the bad one.

Observed with headset firmware 1.4.4.0 and Synapse 4.0.827 on Windows 11, using the HyperSpeed dongle (USB `1532:0577`). The issue has been reported to Razer.

## What the watchdog does

- **Watches live.** It keeps a read handle open on the dongle's vendor HID interface. Windows gives every input report to every open handle, so the app sees, the moment they happen:
  - the headset's own events: balance roller, battery, charging, mic mute, connect and disconnect;
  - the replies to Synapse's writes.

  Volume and mic level come from Windows Core Audio change notifications. Nothing busy-polls. A direct read every 60 s is kept as a safety net.
- **Remembers your settings.** A valid balance or sidetone change made on the headset or in Synapse becomes the new target, saved across restarts.
- **Reverts the bug.** It restores your target, once Synapse stops writing, when it sees any of these:
  - an out-of-range value (such as a balance of 255);
  - a suspicious one (a sidetone of 15 written by Synapse);
  - a Synapse write in the 30 s after a reconnect.

  An otherwise unexplained change is reverted after a 20 s wait for Synapse's log, which it flushes about 10 s late. If the log later shows you chose the value in Synapse, the app puts it back. If something keeps fighting it (5 fixes in 2 minutes), auto-fix pauses for 5 minutes.
- **Logs and notifies.** It writes these to `%LOCALAPPDATA%\BalanceWatchdog\balance-watchdog-YYYY-MM.log`, and fixes also show a Windows notification:
  - every change;
  - Synapse's own balance and sidetone traffic, tailed from its logs;
  - every fix, with its likely cause;
  - battery level, charging, and link drops.
- **Shows an overlay.** A panel appears in the centre of the primary display for about 2.5 s after any change. It shows:
  - a Synapse-style balance slider;
  - smaller volume, mic and sidetone bars, with the one that changed highlighted;
  - battery (with charging state) and link drops in the last hour.

  Clicks pass through it and it never takes focus.
- **Recovers from crashes.** Unhandled exceptions are logged and the app relaunches itself (at most 5 times in a row).

It uses about 16 MB of RAM and essentially no CPU at idle.

**Signal strength:** neither the headset nor the dongle reports RSSI or link quality (Synapse has no such command for this model either), so the overlay shows link drops instead.

### Tray menu

Left-click the icon to show the overlay. The menu has:
- Show overlay
- Check now
- Re-centre balance now
- Auto-fix on/off
- Show overlay on change
- Open log
- Open log folder
- Start with Windows (an `HKCU\...\Run` entry)
- Exit

Icon colours: green means on target, grey means headset off or dongle missing, red means a mismatch that couldn't be fixed or auto-fix is off.

## Download

Get `BalanceWatchdog.exe` from [Releases](https://github.com/bp2008/Razer_Blackshark_V3_Pro_GameChatBalance_Watchdog/releases). It needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (x64).

1. Put the exe in a permanent folder, for example `%LOCALAPPDATA%\BalanceWatchdog\app`.
2. Run it.
3. Optionally, tick **Start with Windows** in the tray menu.

## Usage

```
BalanceWatchdog                 run in the tray: live watch, overlay, log, notify, auto-revert
BalanceWatchdog --interval 60   tray mode with a safety-net poll every N seconds (default 60)
BalanceWatchdog status          print link, balance, sidetone and battery
BalanceWatchdog set 10          write a raw balance value (0..20)
BalanceWatchdog set-sidetone 4  write a sidetone level (0..15)
BalanceWatchdog scan            print the balance/sidetone history found in Synapse's existing logs
BalanceWatchdog probe           read-only dump of the headset settings this tool knows about
BalanceWatchdog listen 30       dump the dongle's HID traffic for N seconds
BalanceWatchdog simulate-bug [sidetone]   write 255 the way Synapse's bug does (for testing)
```

Balance raw values run from `0` (all game) to `20` (all chat), with `10` as the centre. Synapse's `balanceMode` is `20 - raw`. Sidetone runs from `0` to `15`.

## Building

Requires the .NET 10 SDK (Windows).

```
dotnet publish BalanceWatchdog -c Release -o dist
```

Run `dist\BalanceWatchdog.exe`. Before enabling "Start with Windows", put the exe somewhere permanent, because the startup entry points at its current path.

## Protocol notes

These come from Synapse's own JavaScript (minified, but readable) and logs.

- The interface is the dongle's vendor collection with 64-byte input and output reports (`MI_05`, usage page `0xFFxx`).
- The report layout is:

  | Byte | Contents |
  |---|---|
  | 0 | report ID `2` |
  | 2 | transaction ID `0x60` |
  | 6 | `len(cmd) + len(data) + 1` |
  | 9 | `0x80` to forward to the headset, `0x00` for the dongle itself |
  | 10–12 | command |
  | 13+ | data |
  | 62 | XOR of bytes 0–61 |

  Replies echo the header: byte 1 is the status (`2` = OK) and data starts at byte 13.
- The commands used are:

  | Get | Set | Meaning |
  |---|---|---|
  | `92,0,0` | `220,0,1` | Game/chat balance (raw 0–20) |
  | `25,0,0` | `153,0,1` | Sidetone level (0–15) |
  | `24,0,0` | `152,0,1` | Sidetone on/off |
  | `33,0,0` | | Battery % |
  | `42,0,0` | | Charging (`1` = charging) |
  | `85,0,0` | | Mic status (`0` = unmuted) |
  | `106,0,0` | | Roller function (`0` = game/chat balance) |
  | `95,0,0` | | HyperSpeed mode (`0` = Range, `1` = Ultra Low Latency) |
  | `32,0,0` | | Wireless link status (sent to the dongle; `1` = connected) |

  Unsolicited events use the same command with sub-command `2`. For example, `92,2,…` means the balance changed on the headset (roller), `33,2,…` is a battery update, and `32,2,…` means the link changed.
- Volume and mic level in Synapse are the Windows endpoint volumes of "Speakers (BlackShark V3 Pro - Game)" and "Microphone (BlackShark V3 Pro - Chat)".

## License

MIT
