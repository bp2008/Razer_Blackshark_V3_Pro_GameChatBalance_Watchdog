# BlackShark V3 Pro Game/Chat Balance Watchdog

A small Windows tray app that keeps the **Game/Chat Balance** of a Razer BlackShark V3 Pro where you put it. It works around a Razer Synapse bug that sometimes pins the balance to one extreme when the headset powers on, which silences the Chat audio device (Teams, Discord, etc.).

It also shows an on-screen slider, like the volume flyout, whenever the balance changes.

> Unofficial. Not affiliated with or endorsed by Razer.

## The bug

When the headset powers on or reconnects, Synapse queries its settings about 1.5 s after the dongle reports it connected. Sometimes the headset isn't ready yet and answers every query with `255`. Synapse range-checks some settings (it logs `Invalid mode: 255` for HyperSpeed) but not the balance:

1. Synapse reads the balance as `255` and converts it with `balanceMode = 20 - raw`, giving **-235**.
2. It saves `-235` to the profile, then writes `20 - (-235) = 255` back to the headset. It repeats this several times over about 15 seconds.
3. The headset firmware accepts 255 without clamping, so the balance is pinned and Chat goes silent until you move the slider by hand.

You can see it in Synapse's logs. Running `BalanceWatchdog scan` prints every balance read and write Synapse has logged and flags the invalid ones:

```
2026/11/04 17:31:47.059  read raw 255 from headset => balanceMode -235   <-- INVALID
2026/11/04 17:31:47.191  read raw 10 from headset => balanceMode 10
2026/11/04 17:31:47.831  re-applied stored profile value (version 743) => balanceMode -235   <-- INVALID
2026/11/04 17:31:48.076  wrote raw 255 to headset => balanceMode -235   <-- INVALID
```

The headset returned the correct value 130 ms after the bad one, but Synapse had already stored the bad one.

Observed with headset firmware 1.4.4.0 and Synapse 4.0.827 on Windows 11, using the HyperSpeed dongle (USB `1532:0577`). The issue has been reported to Razer.

## What the watchdog does

- **Watches live.** It keeps a read handle open on the dongle's vendor HID interface. Windows gives every input report to every open handle, so the app sees, the moment they happen:
  - balance changes made on the headset itself (unsolicited events);
  - replies to Synapse's balance writes;
  - headset connect and disconnect events.

  The read blocks until data arrives, so there's no busy polling. A direct read every 60 s is kept as a safety net.
- **Remembers your setting.** A valid change made on the headset or with the Synapse slider becomes the new target, saved across restarts.
- **Reverts the bug.** An out-of-range value (such as 255) is restored to your target once Synapse stops writing. So is an unexplained change, after a 15 s wait for Synapse's log, which it flushes about 10 s late. If something keeps fighting it (5 fixes in 2 minutes), auto-fix pauses for 5 minutes.
- **Logs and notifies.** Every change, Synapse's own balance traffic (tailed from its logs), and every fix with its likely cause is written to `%LOCALAPPDATA%\BalanceWatchdog\balance-watchdog-YYYY-MM.log`. Fixes also show a Windows notification.
- **Shows an overlay.** A Synapse-style slider appears in the centre of the primary display for about 2.5 s after any change. Clicks pass through it and it never takes focus.
- **Recovers from crashes.** Unhandled exceptions are logged and the app relaunches itself (at most 5 times in a row).

It uses about 15 MB of RAM and essentially no CPU at idle.

### Tray menu

Left-click the icon to show the current balance. The menu has Check now, Re-centre now, Auto-fix on/off, Show balance on change, Open log, Open log folder, Start with Windows (an `HKCU\...\Run` entry), and Exit.

Icon colours: green means on target, grey means headset off or dongle missing, red means a mismatch that couldn't be fixed or auto-fix is off.

## Usage

```
BalanceWatchdog                 run in the tray: live watch, overlay, log, notify, auto-revert
BalanceWatchdog --interval 60   tray mode with a safety-net poll every N seconds (default 60)
BalanceWatchdog status          print link state and the raw balance byte
BalanceWatchdog set 10          write a raw balance value (0..20)
BalanceWatchdog scan            print the balance history found in Synapse's existing logs
BalanceWatchdog listen 30       dump the dongle's HID traffic for N seconds
BalanceWatchdog simulate-bug    write raw 255 the way Synapse's bug does (for testing)
```

Raw values run from `0` (all game) to `20` (all chat), with `10` as the centre. Synapse's `balanceMode` is `20 - raw`.

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

  | Command | Meaning |
  |---|---|
  | `92,0,0` | Get balance |
  | `220,0,1` | Set balance |
  | `32,0,0` | Get wireless link status (dongle; `1` = connected) |

  Unsolicited events use sub-command `2`: `92,2,…` means the balance changed on the headset, and `32,2,…` means the link changed.

## License

MIT
