# Inkubator - In-Game Tattoo Pack Editor for Schedule I

> 🛟 **Need help or found a bug?** Get support at [support.doodesch.de](https://support.doodesch.de).

**Make a tattoo pack without ever leaving the game.** Inkubator opens a 3D tattoo editor right from the main
menu: import your PNGs, place and scale them on the character, preview them live on the real menu rig, and
export a complete, ready-to-publish [Inkorporated](https://thunderstore.io/c/schedule-i/p/DooDesch/Inkorporated/)
tattoo mod - manifest, license and a resized icon included. Built on [S1API](https://github.com/ifBars/S1API)
and launched through the Side Hustle hub.

![Version](https://img.shields.io/badge/version-1.0.0-blue)
![Game](https://img.shields.io/badge/game-Schedule%20I-purple)
![MelonLoader](https://img.shields.io/badge/MelonLoader-0.7.3+-green)
![S1API](https://img.shields.io/badge/S1API-required-orange)

## Features

- **In-game tattoo editor** - design a whole pack from the main menu, no image software or JSON needed.
- **Import and place** - drop PNGs in a folder, then place, move, scale, rotate and flip each tattoo on a UV canvas.
- **Live preview** - every change is baked onto the menu character instantly, with turn and camera-zoom controls.
- **Chest, arms and face** - author tattoos per body part, each with its own name, shop price and shop id.
- **One-click export** - produces a complete Inkorporated pack (manifest, baked textures, README, LICENSE and a 256x256 icon, auto-resized from any source image).

## Requirements

- Schedule I (IL2CPP, current Steam public build)
- MelonLoader 0.7.3+
- S1API (ifBars/S1API_Forked) - pulled in as a dependency
- Side Hustle - the main-menu hub Inkubator launches from
- Inkorporated - only needed to *use* the packs you export, not to run the editor

## How it works

Open Side Hustle from the main menu and launch Inkubator. Drop your PNGs in the import folder, place them on
the character per body part, set names and prices, fill in the pack details and hit Export. Your finished
pack lands in `UserData/Inkubator/Exports/`, ready to zip and upload. Anyone who installs your pack together
with Inkorporated gets your tattoos in the in-game tattoo shop.

## Credits

DooDesch - mod author. Built on [S1API](https://github.com/ifBars/S1API) by ifBars. UI icons from
[Lucide](https://lucide.dev). MIT licensed.
