# Changelog

All notable changes to Inkubator are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

## [1.0.4] - 2026-08-01

### Changed

- Runs on Schedule I 0.4.6f11.
- Needs S1API 3.1.1, up from 3.0.5. Update it along with the mod.

## [1.0.3] - 2026-07-27

### Added

- **Full body** canvas mode. 1.0.2 framed the canvas on the selected body part, which fixed tattoos landing
  in the wrong place but also locked you out of everywhere the game has no stock tattoo for: forearms,
  hands, shoulders, belly, legs, back and the back of the head. A Part / Full body switch under the canvas
  now opens the whole skin, and the faint backdrop turns into a map of the body so you can see where you
  are aiming. The body part you picked still decides how the tattoo is exported - the switch only changes
  what you are looking at.

## [1.0.2] - 2026-07-27

### Fixed

- Tattoos now land on the body part you picked. Chest, left arm and right arm are three small islands in
  one shared texture wrapped around the whole body, so choosing a placement never actually moved an image
  and a new tattoo started on none of them. The canvas now frames the body part you are editing, a new
  tattoo starts on it, and dragging stays there.
- The faint "inked region" reference behind the canvas is finally visible. It never loaded outside a
  development build, so on a normal install you were placing tattoos on a blank square.
- "Hide clothes" actually hides the clothes. The game clears only six of its eight avatar layer slots, so
  a shirt and jeans could stay stuck on the character while everything else said they were gone.
- Tattoos no longer look darker than they should. The same slot leak could composite one layer several
  times over, which read as a tint that nothing removed.
- A tattoo no longer disappears without explanation. Past eight body layers the game silently drops one;
  the editor now hides one of the character's own tattoos instead and says so.
- The clothes and underwear buttons no longer double as a click on the character behind them, which
  turned the model and triggered a redundant re-bake on every toggle.

## [1.0.1] - 2026-07-10

### Fixed

- Nexus/Vortex: the download now ships the DLL under a mods/ folder so Vortex installs it correctly. The
  old flat archive could make Vortex deploy a stray "mods" file into the game folder and break the Mods
  directory - if Inkubator never showed up for you after a Vortex install, this was why. Manual installs
  and Thunderstore were never affected.

## [1.0.0] - 2026-06-24

Initial release.

### Added
- In-game 3D tattoo editor, launched from the main menu through the Side Hustle hub.
- Import PNGs and place, move, scale, rotate and flip tattoos on a per-body-part UV canvas (chest, arms, face).
- Live preview baked onto the real menu character, with turn and camera-zoom controls that follow the face when editing face tattoos.
- Per-tattoo name, shop price and auto-generated shop id.
- In-app icon picker that resizes the chosen cover image to Thunderstore's 256x256 on export.
- One-click export of a complete, ready-to-publish Inkorporated tattoo pack (manifest, baked textures, README, LICENSE and icon).
