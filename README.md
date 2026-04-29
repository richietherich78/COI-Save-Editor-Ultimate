<p align="center">
  <img src="COISaveEditor-Ultimate/Assets/logo256.png" alt="COI Save Editor Ultimate" width="160">
</p>

<h1 align="center">COI Save Editor — Ultimate</h1>

<p align="center">A Windows desktop tool for removing mods from <a href="https://store.steampowered.com/app/1594320/Captain_of_Industry/">Captain of Industry</a> save files without losing your progress.</p>

When you uninstall a mod, the game often refuses to load the save because it still references types, configs, and objects from that mod. This editor strips those references so the save can load cleanly on vanilla or with a different mod set.

---

## ⚠️ BACK UP YOUR SAVE FILES

**Always keep a backup copy of your original save before editing.**

Save editing is inherently risky. If something goes wrong, a corrupted save may be unrecoverable. Copy your `.save` file to a safe location before using this tool.

Save files are typically found at:
```
%APPDATA%\..\Roaming\Captain of Industry\Saves
```

---

## Requirements

- Windows 10/11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (x64)
- The game's installed DLL files (for Deep Edit)

---

## Getting Started

### 1. Configure Settings

Before editing any save, open **⚙ Settings** from the toolbar and configure:

#### Game Managed Folder
Point to the game's `Managed` folder containing `Mafi.dll`, `Mafi.Core.dll`, and all other game assemblies. This is required for Deep Edit.

Typical path:
```
C:\Program Files (x86)\Steam\steamapps\common\Captain of Industry\Captain of Industry_Data\Managed
```

#### Mod & DLC DLL Paths
Add the DLL for every **mod** and **DLC** that was active when the save was created. You can add individual `.dll` files or entire folders.

> **Don't forget DLC DLLs!** If the save uses the Trains DLC, Supporters Edition, or other official DLCs, their DLLs must be included (e.g. `Mafi.TrainsDlc.dll`). These are usually in the game's Managed folder.

Settings are saved automatically and persist between sessions.

---

### 2. Open a Save File

- Click **Open Save File…** in the toolbar, or
- Drag and drop a `.save` file onto the window.

The left panel shows all mods embedded in the save with category badges:

| Badge | Meaning |
|-------|---------|
| **Built-in** | Core game mod — cannot be removed |
| **Config Only** | Mod only stores config data — may be removable via Standard Export |
| **Has Entities** | Mod adds placeable buildings — safe only if none are placed on the map |
| **Cannot Remove** | Mod registers global services in the RESOLVER — requires Deep Edit |

Un-check any mod you want to remove.

---

### 3. Choose an Export Method

#### Standard Export (toolbar → Export Modified Save…)

Replaces removed mod IDs with sentinel placeholders in the MOD_TYPES chunk. The rest of the save is kept byte-for-byte identical.

**Best for:** Config-only mods or mods with no footprint in the RESOLVER chunk.

**Limitations:** Cannot remove mods that register serialisable types in the RESOLVER (labeled "Cannot Remove").

#### Deep Edit (Deep Edit tab → Run Deep Edit & Export…)

Loads the actual game and mod DLLs, fully deserialises every chunk using the game's own serialization engine, strips objects and configs belonging to removed mods, then re-serialises the entire save.

**Best for:** Any mod, including those marked "Cannot Remove" (like COIExtended-Core).

**Steps:**
1. Ensure Settings are configured (game folder + mod DLLs).
2. Open the **Deep Edit** tab.
3. Click **Load Game + Mod DLLs** and wait for assemblies to load.
4. Un-check mods to remove in the left panel.
5. Click **Run Deep Edit & Export…** and choose an output location.

A detailed log file (`.deep-edit.log`) is written alongside the exported save.

---

## Tabs

| Tab | Purpose |
|-----|---------|
| **RESOLVER Types** | Shows every assembly-qualified type name found in the RESOLVER chunk, grouped by assembly. Useful for understanding which mods have a deep footprint. |
| **Export & Info** | Explains how Standard Export works, its limitations, and provides an export button. |
| **Deep Edit** | Full re-serialisation workflow — load assemblies, run deep edit, view progress log. |

---

## Before You Strip — Disable Mod Features First

If you have mods with cheat or gameplay-modifier features enabled (instant build, free recipes, speed multipliers, unlimited resources, etc.), **disable those features inside the game before running the editor**.

Here is why: when you enable "instant build" (for example), the mod applies that effect directly to entity data in your save — completing constructions, zeroing build requirements, or setting flags on individual buildings. That modified data is serialized into the save file. When the editor strips the mod config and manager objects, it removes the *instruction* to keep applying the effect, but the *already-applied changes* to entity state remain — because those changes are stored on the entities themselves, not in the mod config.

**Recommended steps when your mod has cheat or modifier settings:**

1. Launch the game **with the mods still installed and active**.
2. Open the mod's in-game settings and **turn off** every cheat or modifier you want gone (instant build, free production, speed boosts, etc.).
3. Let the game simulate for at least one in-game day so the mod can unapply the effects.
4. **Save the game.**
5. Now run the Save Editor to strip the mods.

Skipping these steps is safe from the editor's perspective — the save will load — but any effects already written into entity state will persist in the stripped save.

---

## After Stripping — What to Expect

The save will load and your island will be playable. A few things may need attention the first time you load:

### What works automatically
- **Storage stats and production graphs** are fully preserved — product tracking picks up from where it left off.
- **Buildings and machines** that existed in vanilla (and were also added by the mod with a different proto ID) are healed to their nearest vanilla equivalent — tiers are matched where possible.
- **Proto healing** covers the most common case: a mod that re-exports vanilla protos under new IDs (e.g. COIExtended). These are swapped back to the real vanilla proto.

### What may need manual fixes in-game

**Recipes** — Machines that were running a mod-only recipe are assigned the nearest vanilla recipe the editor can find for that machine type. That guess may be wrong for your situation. Do a sweep of your production lines and reassign any machine running an unexpected recipe.

**Vehicles and rolling stock** — Trucks, excavators, and train cars added exclusively by a mod are removed from the map. You will need to build or purchase replacements in-game. Vanilla equivalents are not automatically placed. Specifically:
- Haul trucks and excavators with mod-only protos will be gone — check your mine/dump vehicle pools.
- Train locomotives and tender cars with mod-only protos will be removed from any consist they were in — rebuild those train sets.

**Smokestacks / visual-only buildings** — Decorative or atmosphere buildings added by a mod are stripped. The slots they occupied are cleared; the terrain and connected infrastructure remain.

**Mod-exclusive products** — Any resource that exists only in the mod (not in vanilla) is removed from storage tanks and conveyor networks. Downstream machines waiting for that input will idle until you reconfigure them with a vanilla recipe.

### General advice
Run the game, check the in-game log for any remaining "Missing proto" warnings, then do a quick tour of your key production lines. Most issues are a few recipe re-assignments and vehicle replacements.

---

## Troubleshooting

- **"Game DLLs not configured"** — Open ⚙ Settings and set the game Managed folder path.
- **"Assemblies have not been loaded yet"** — Click "Load Game + Mod DLLs" in the Deep Edit tab before running.
- **Assembly load failures** — Expand the Assembly Load Log to see which DLLs failed. Make sure you're pointing to the correct game version's Managed folder and that all active mod DLLs are listed in Settings.
- **Game still won't load the save** — Some mods have very deep integration. Check the deep edit log for stripped object counts. If the game crashes, restore from your backup and try a different approach (e.g. removing mods one at a time).

---

## Building from Source

Requires [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). The project uses WPF (`net8.0-windows`) and references game DLLs at runtime via reflection — no game DLLs are needed at compile time.

### Quick build

```bash
dotnet build COISaveEditor-Ultimate\COISaveEditor-Ultimate.csproj
```

### Run tests

```bash
dotnet test COISaveEditor-Ultimate.Tests\COISaveEditor-Ultimate.Tests.csproj
```

### Publish (self-contained)

Use the included build script, which restores packages, **runs all unit tests** (aborting on failure), then publishes a self-contained build:

```powershell
.\COISaveEditor-Ultimate\build.ps1                # portable folder
.\COISaveEditor-Ultimate\build.ps1 -SingleFile     # single .exe
.\COISaveEditor-Ultimate\build.ps1 -Configuration Debug
```

Output is placed in `COISaveEditor-Ultimate\publish\` by default.

---

## License

This project is not affiliated with MaFi Games or Captain of Industry.
