# Inkubator - Design and Export Custom Tattoo Mods In-Game

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

> Make a tattoo pack without ever leaving the game. Inkubator opens a 3D tattoo editor right from
> the main menu: import your PNGs, place and scale them on the character, preview them live on the
> real menu rig, and export a complete, ready-to-publish [Inkorporated](https://github.com/DooDesch-Mods/ScheduleOne-Inkorporated)
> tattoo mod - manifest, license and a resized icon included. Built on
> [S1API](https://github.com/ifBars/S1API) and launched through the
> [Side Hustle](https://github.com/DooDesch-Mods/ScheduleOne-SideHustle) hub.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)
![Side Hustle](https://img.shields.io/badge/Side%20Hustle-required-orange)
![Status](https://img.shields.io/badge/status-working-brightgreen)

## Features

- **In-game tattoo editor.** No image software, no JSON, no rebuilds - design a whole pack from the main menu.
- **Import and place.** Drop PNGs into the import folder, then place, move, scale, rotate and flip each tattoo on a UV canvas.
- **Live preview on the real character.** Every change is baked onto the menu rig instantly, with turn and camera-zoom controls (the camera follows the face when you edit a face tattoo).
- **Chest, arms and face.** Author tattoos per body part, each with its own name, shop price and auto-generated shop id.
- **One-click export.** Produces a complete Inkorporated pack: a `manifest.json`, the baked textures, a `README`, a `LICENSE` and a 256x256 `icon.png` (auto-resized from any source image), zipped and ready to publish.
- **Pick your icon in-app.** Choose a cover image from your imports; it is resized to Thunderstore's 256x256 on export.

## Requirements

| Component | Version / Source |
|-----------|------------------|
| Schedule I | IL2CPP, current Steam public build |
| MelonLoader | 0.7.3+ |
| S1API | [ifBars/S1API_Forked](https://github.com/ifBars/S1API) (pulled in as a dependency) |
| Side Hustle | [DooDesch-Mods/Side Hustle](https://github.com/DooDesch-Mods/ScheduleOne-SideHustle) - the main-menu hub Inkubator launches from |
| Inkorporated | [DooDesch-Mods/Inkorporated](https://github.com/DooDesch-Mods/ScheduleOne-Inkorporated) - only needed to *use* the packs you export, not to run the editor |

## Installation

### Recommended: a Thunderstore mod manager

Install with a Schedule I mod manager (Thunderstore Mod Manager, r2modman or Gale). It pulls in MelonLoader,
S1API and Side Hustle automatically.

### Manual

1. Install MelonLoader 0.7.3+ for Schedule I.
2. Install S1API and Side Hustle.
3. Drop `Inkubator.dll` into your Schedule I `Mods/` folder.
4. Drop `SixLabors.ImageSharp.dll` into your Schedule I `UserLibs/` folder (needed for WebP/GIF imports).

## How to use it

1. From the main menu, open **Side Hustle** and launch **Inkubator**.
2. Use **Open import folder** and drop your tattoo PNGs in there, then refresh.
3. Pick a body part, add a tattoo, place your image on the canvas and watch it appear on the character.
4. Set each tattoo's name and price, fill in the pack details (name, author, version, license, cover icon).
5. Hit **Export mod**. Your finished pack lands in `UserData/Inkubator/Exports/<pack>/`, ready to zip and upload.

Anyone who installs your pack together with Inkorporated gets your tattoos in the in-game tattoo shop.

## Configuration

Inkubator has no settings to configure - it is a self-contained editor. Developer tools (self tests,
auto-open flags) exist only in Debug builds and never ship.

## Compatibility

IL2CPP build only. The editor runs in menu space and loads no save, so it does not interact with an
active game.

## Credits

- DooDesch - mod author.
- Built on [S1API](https://github.com/ifBars/S1API) by ifBars.
- Launched through the [Side Hustle](https://github.com/DooDesch-Mods/ScheduleOne-SideHustle) hub.
- Exports packs for [Inkorporated](https://github.com/DooDesch-Mods/ScheduleOne-Inkorporated).
- UI icons from [Lucide](https://lucide.dev) (ISC).

## License

[MIT](LICENSE.md).
