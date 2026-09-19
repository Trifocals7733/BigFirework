# 🎆 Big Firework

[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Supported game](https://img.shields.io/badge/Big%20Walk%20%7C%20BepInEx%206-supported-6f42c1?style=flat-square)](https://store.steampowered.com/app/1478500/Big_Walk/)

**Host-synchronized sky firework shows, interactive launcher pads, cinematic night transitions, and authentic flare audio for Big Walk.**

Big Firework lets the host light up the island sky with choreographed firework volleys that replicate across the entire lobby via Mirror RPCs. Unmodded players see the exact same sky show without installing anything.

---

## ✨ Features

- **Lobby-Wide Multiplayer Sync**: Bursts use the game's native Mirror `PeckEffectParticleNetworked.RpcFire(pos, rot)`, so all vanilla and modded players in the session see the exact same fireworks in real time.
- **Player Show & Follow Movement**: In **Self** mode, the show follows the host dynamically as you walk, sprint, or fly across the island. Alternatively, select **Everyone** to round-robin bursts across all lobby players.
- **Cinematic Sky Director**:
  - Automatically fast-forwards time smoothly to midnight (0.0h) over 2.5 seconds when a show begins.
  - Locks night for the duration of the show.
  - Smoothly restores daylight with a configurable `RestoreDelay` after the finale.
- **Firework Launcher Buttons**:
  - Hooks into physical in-world firework launcher pads across the island.
  - Set to **Off** (vanilla), **Accompany** (pad triggers its own rockets plus sky volleys), or **Replace** (swaps button with custom pad shows: FlareVolley, RocketSalvo, or GrandFinale).
- **Authentic Flare Audio Engine**:
  - Ingests native `AudioAsset` collections from the game's `ParticleOneShotSound`.
  - Silent at launch—detonates in the air at apex via the game's compiled `AudioPlayHelper` engine.
  - Real-time acoustic propagation delay: sound arrives delayed by distance at the speed of sound ($\Delta t = \text{distance} / 343\text{ m/s}$).
- **Show Styles**:
  - **Sky**: Majestic high-altitude bursts with customizable launch height and spread angle.
  - **Fountain**: Festive ground-level spray bursting from the anchor's feet with a wide cone.
- **Color Compositions**: Fine-tune the burst count for Green, Yellow, Blue, Red, and Teal flares.
- **Zero-Allocation Core**: Zero garbage collection pressure during gameplay using cached emitter lookups and pre-allocated player registries.
- **In-Game Settings**: Full integration with [ModSettingsMenu](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/).

---

## 🎮 Controls

| Key | Action |
|---|---|
| <kbd>F10</kbd> | Toggle / Start a **Player Show** volley (Host only) |
| Physical Button | Press in-world launcher switches to trigger configured launcher shows |

*(The toggle key can be rebound in the Mod Settings menu under the **Input** section).*

---

## ⚙️ Settings

Configurable via **Mod Settings** in the pause or main menu (or in `BepInEx/config/walker.bigfireworks.cfg`):

### Colors etc.
| Setting | Default | Description |
|---|---|---|
| `Green` | `2` | Number of green flare bursts per volley. |
| `Yellow` | `2` | Number of yellow flare bursts per volley. |
| `Blue` | `2` | Number of blue flare bursts per volley. |
| `Red` | `2` | Number of red flare bursts per volley. |
| `Teal` | `2` | Number of teal flare bursts per volley. |
| `LaunchHeight` | `15.0` | Height above player/anchor where bursts spawn (5m to 40m). |
| `SpreadAngle` | `12.0` | Random angular cone spread from straight up in degrees (0° to 45°). |
| `Interval` | `0.25` | Delay in seconds between consecutive bursts (0.05s to 2.0s). |

### Player Show
| Setting | Default | Description |
|---|---|---|
| `Enabled` | `true` | Master toggle for Big Firework mod functionality. |
| `Target` | `Self` | **Self** (launches from host) or **Everyone** (round-robins across lobby players). |
| `Follow` | `true` | In Self mode, dynamically updates launch origin to host's position on every beat. |
| `Style` | `Sky` | **Sky** (high-altitude cone) or **Fountain** (wide ground-level spray). |
| `FlareSounds` | `true` | Enables authentic distant flare explosion bang audio with speed-of-sound delay. |
| `Fast Night` | `true` | Fast-forwards solar time to midnight during shows with smooth easing. |
| `RestoreDelay` | `5.0` | Time in seconds to hold night after a show before restoring daylight (0s to 30s). |

### Firework Launcher
| Setting | Default | Description |
|---|---|---|
| `Mode` | `Accompany` | **Off** (vanilla), **Accompany** (vanilla + volleys), or **Replace** (custom shows). |
| `Show` | `GrandFinale` | Launcher show type: **FlareVolley**, **RocketSalvo**, or **GrandFinale**. |
| `PadFrom` | `Pressed` | **Pressed** (launches from pressed pad) or **All** (cycles across all island pads). |

### Input
| Setting | Default | Description |
|---|---|---|
| `Player Show` | `F10` | Keybind to trigger/restart the Player Show volley. |

---

## 📦 Requirements

- **Big Walk** ([Steam](https://store.steampowered.com/app/1478500/Big_Walk/)) with **[BepInEx 6 (IL2CPP)](https://builds.bepinex.dev/projects/bepinex_be)** installed
- [**ModSettingsMenu**](https://thunderstore.io/c/big-walk/p/Ice_Box_Studio_BigWalk/ModSettingsMenu/) ≥ 1.1.0

---

## 🚀 Installation

1. Close Big Walk.
2. Build or download `BigFirework.dll`.
3. Place `BigFirework.dll` into your game install:
   ```
   Big Walk/BepInEx/plugins/BigFirework/BigFirework.dll
   ```
4. Launch Big Walk as Host.
5. Press <kbd>F10</kbd> to launch your first show!

---

## 💖 Credits

- **M4TR!X GG** — Thumbnail artwork
- **Dexter** — Testing and feedback
- Developed with BepInEx 6 IL2CPP & Harmony for Big Walk
