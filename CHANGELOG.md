# Changelog

All notable changes to COI Save Editor — Ultimate are documented here.

---

## [1.1.0] — 2026-04-30

### Fixed

- **Game 0.8.3 compatibility — save version header upgrade**
  When using 0.8.3 game DLLs, the output save's header version is now updated to match the DLL's `CURRENT_SAVE_VERSION`. Previously, the header retained the original save's older version number, which caused the game to skip newly-added serialized fields (e.g. `TrainLinesManager.m_freeNetworkLineIds`, added in save version 289) on load, misaligning the byte stream and crashing with `CorruptedSaveException`.

- **Game 0.8.3 compatibility — resolver object count**
  `TrainNetworksManager` is a new manager class introduced in 0.8.3 that did not exist when 0.8.2 saves were created. When running with 0.8.3 DLLs, it was being inserted into the resolver's object list during deserialization, inflating the count from 212 to 213 objects. This shifted every BlobWriter object-ID by one and corrupted all back-references in the output save. The new `DllDeltaPurge` pass removes such version-added objects before re-serialization.

### Notes

- Saves processed with 0.8.2c DLLs continue to load correctly in the 0.8.3 game — the save version header is preserved as-is and the game's own backward-compatibility guards handle the rest.
- **v1.0.0 is broken for players on game version 0.8.3** and should not be used. Please upgrade to v1.1.0.

---

## [1.0.0] — 2026-04-28

Initial public release.

### Added

- Deep Edit engine: fully deserializes and re-serializes COI save files using the game's own DLLs, stripping objects, configs, and type references belonging to removed mods.
- Proto healing: COIExtended-style mods that override vanilla proto IDs are detected and healed back to their vanilla equivalents, preserving buildings, machines, and production lines.
- Vanilla entity recovery: vanilla entities (Shipyard, CaptainOffice, Settlement modules, Goals) whose primary proto was replaced by a mod are recovered rather than stripped.
- Machine recipe healing: machines left with an empty recipe list after stripping are assigned the nearest vanilla recipe for their machine type.
- Output validator: the produced payload is scanned for any remaining references to removed-mod assemblies before the `.save` file is written. If violations are found, the tool refuses to produce an unloadable save.
- In-place AQN patcher: as a last-resort pass, any stripped-mod assembly-qualified type name surviving in the byte stream is rewritten to a safe placeholder.
- Standard Export mode for config-only mods that don't require full re-serialization.
- RESOLVER Types tab for inspecting which assemblies have a footprint in the save.
