# AION 2 DPS Meter

A free, open-source damage and healing meter for **AION 2** (Global and other regions), shown as an
always-on-top overlay panel. Written in C# / WPF for Windows 10 and 11.

**[⬇ Download the latest version](https://github.com/domel1998/Aion2DPSMeter/releases/latest)** ·
**[☕ Ko-fi](https://ko-fi.com/domel1998)** ·
**[PayPal](https://paypal.me/DominikMat12)**

- Live **DPS and HPS for your whole party**, with each player's share, crit rate and max hit
- **Automatic fight splitting**: pulling a boss starts a fresh fight, a boss kill or a cleared pack ends it,
  as do an idle gap, a zone change and a manual reset
- **Fight history** saved locally, browsable per monster (fights, kills, best kill time, your best DPS)
- Per-skill breakdown: damage, hits, crit / back / perfect / double rates, DoTs and summons (summon damage
  goes to its owner)
- Global **hotkeys** that work while the game has focus
- Movable panel, including onto a second monitor; click-through mode; adjustable transparency
- Copy a fight summary for Discord, export a fight as JSON

## How it works

The meter **passively reads the game's network traffic** with [Npcap](https://npcap.com/). The game's
combat stream is not encrypted. The meter never sends anything to the game or its servers, never touches the
game's memory or files and injects nothing into the game process. (The only thing it contacts is GitHub, to look
for new versions, and that can be turned off.) It finds the game connection by itself, so it works on any server or
region and through VPNs and ping reducers.

> Using third-party tools may be against the game's terms of service. This project is not affiliated with
> NCSOFT. Use it at your own risk.

## Requirements

1. **Windows 10/11 (x64)**
2. **Npcap**: download it from <https://npcap.com/#download> and during installation tick
   **"Install Npcap in WinPcap API-compatible Mode"**.
   If you also tick "Restrict Npcap driver's access to Administrators only", the meter must be run as administrator.

## Fullscreen

The panel is an ordinary always-on-top window, so it is drawn over the game in **borderless / windowed
fullscreen**. In *exclusive* fullscreen Windows does not allow any other window on top. (The only way around
that is hooking the game's DirectX rendering, which anti-cheat software treats as cheating, so this meter does
not do it.) In that case:

- switch the game to borderless fullscreen (looks the same, and alt-tabbing is faster), or
- drag the panel onto a second monitor. It is a normal window and can sit anywhere.

The panel never takes keyboard focus from the game, even when you click it.

## Hotkeys (default)

| Action | Keys |
|---|---|
| Reset meter (the current fight is saved) | `Ctrl+Shift+R` |
| Show / hide panel | `Ctrl+Shift+H` |
| Click-through on / off | `Ctrl+Shift+K` |
| Damage / healing view | `Ctrl+Shift+M` |

They can be changed in Settings. The tray icon also has every command, so a hidden or click-through panel can always be brought back.

## Who is "you"

The game states your character name when you enter a zone, so a meter started mid-session would not know it at
first. The meter recognises you during the first fight anyway: the server sends a certain record (`06 38`) only
about the local player, and loot records name who got the drop. Your name is remembered for the next session and
replaced as soon as the game states it (for example when you play another character).

## Updates

The meter checks the GitHub releases list at start and every 6 hours and shows an *Update* button when a newer
version exists. It never downloads or installs anything by itself. Turn it off in Settings.

## Fight history

Boss fights are always saved. Fights with normal monsters and training dummies are saved when they last at
least 15 seconds (configurable). The history is an SQLite database in `%LOCALAPPDATA%\Aion2DpsMeter\history.db`.
Settings are kept in `%APPDATA%\Aion2DpsMeter\settings.json`.

## After a game patch

Patches sometimes renumber protocol opcodes (the June 2026 update shifted several by one). Settings → *Edit
opcodes…* writes `%APPDATA%\Aion2DpsMeter\opcodes.json`. Every opcode there is a list, so old and new values can be
accepted side by side, and a fix can be shared without a new release. Restart the meter afterwards.

For bug reports, Settings → *Record game packets* saves the game stream as `.pcap` files, which *Replay a
recording…* plays back through the meter. Recordings contain the names of characters around you, so share them only with people you trust.

## Building

```
dotnet build Aion2DpsMeter.slnx
dotnet test
dotnet run --project src/Aion2DpsMeter.App -- --demo
```

`--demo` runs the panel with a made-up party fight (nothing is captured) and keeps its history separate.

A self-contained single executable:

```
dotnet publish src/Aion2DpsMeter.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Ship the `publish` folder: `Aion2DpsMeter.exe` plus the `Data` folder.

### Layout

| Path | What |
|---|---|
| `src/Aion2DpsMeter.Core/Protocol` | Framing (varint lengths, LZ4 bundles), packet parser, party roster, opcodes |
| `src/Aion2DpsMeter.Core/Capture` | Npcap capture, TCP reassembly, game-connection detection, pcap recording/replay |
| `src/Aion2DpsMeter.Core/Combat` | Entities, damage/heal aggregation, fight splitting, fight records |
| `src/Aion2DpsMeter.Core/History` | SQLite fight history |
| `src/Aion2DpsMeter.App` | WPF overlay, history, details and settings windows, hotkeys, tray |
| `data/` | Skill, NPC and dungeon names (English), DoT skill list |
| `tests/` | Unit tests, including byte samples from live captures |

## Support the project

The meter is free. If it helps you and you want to say thanks, you can buy me a coffee:

- **Ko-fi:** [ko-fi.com/domel1998](https://ko-fi.com/domel1998)
- **PayPal:** [paypal.me/DominikMat12](https://paypal.me/DominikMat12)

## License and credits

Made by **domel1998 (aka Morfito)**. GPL-3.0-or-later, see [LICENSE](LICENSE).

The protocol knowledge (packet framing, opcodes, record layouts) and the data tables in `data/` come from
[A2Tools DPS Meter](https://github.com/taengu/A2Tools-DPS-Meter) (GPL-3.0), whose authors worked them out
from live captures. This project is an independent C# implementation built on that work.
