# Changelog

All notable changes to Inkubator are documented here. This project adheres to
[Semantic Versioning](https://semver.org/).

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
