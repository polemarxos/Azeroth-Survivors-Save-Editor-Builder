# Azeroth Survivors – Save Editor Builder

![Platform](https://img.shields.io/badge/platform-Windows-0078D6)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![Language](https://img.shields.io/badge/language-VB.NET-945DB7)
![Offline](https://img.shields.io/badge/works-offline-2ea44f)

A Windows desktop app that reads the **Azeroth Survivors** game executable and creates or updates the
[Azeroth Survivors Save Editor](https://polemarxos.github.io/Azeroth-Survivors-Save-Editor/) for you.

When the game gets an update with new zones, achievements, pets or gear, scan the new `AzerothSurvivors.exe`,
review what changed, and build a fresh `index.html` editor. Everything runs on your PC. It needs no
internet connection and no AI service.

---

## Features

- **Reads the game exe directly.** It finds the game's data tables inside the executable.
- **Detects what's new:**
  - achievements (name, description, icon and tab)
  - zones (taken from their zone achievements)
  - pets and tabards
  - account perks, with their max rank
  - codex rings, trinkets and armory items
  - codex legendary abilities for each class, in the game's order, read from the game's ability records
  - zones, with their real names, in the game's zone order
  - talents, with their tree, max rank, row and column
  - gear: new tiers, new rarity names and new set and weapon names
- **Detects what changed:**
  - new icons, names and descriptions for things the editor already has
  - talents that were moved or re-ranked
  - renamed gear
- **Gear icons, no WoW client needed.** The game stores a WoW display id for every gear piece: 5 tiers x 8 slots per class, all weapons, and PvP armour and weapons. The builder reads them all and turns them into icon names with the icon list built into the program (`Core/gear-icons.json`, 455 display ids). Empty slots are filled the way the game fills them. The WoW folder is only needed if a future update adds gear newer than that list.
- **Every kind of achievement:**
  - plain ones stored with their id
  - ids the code writes piece by piece (`codex:50`, `count:marks1k`, `legendaryset`…), read with a small code emulator
  - families the game builds for every zone or class (`zone:`, `zone9:`, `class:`, `classlast:`), filled in for every zone and class, including new zones
  - numbered families (`codex:10/50/100`, `talents:1/9`, `pieces:25/100/250`)
  - any achievement title and description the editor doesn't have, with its id taken from the code next to where the title is used. If the id is only built while the game runs, the row shows `?…` and you type the id in yourself.
- **Reads your save file too.** Give it a save and every codex item, zone, achievement, pet and tabard in it is checked. Any id the editor doesn't know is looked up in the exe: codex items get their name, icon and type (rings & trinkets or armory); new zones get their real name and go after the zone that comes before them in your save. A new legendary ability is ticked when your save has already seen it.
- **Detects what's gone.** Ids the editor has but the game no longer has are flagged as *Missing* (removed or renamed).
- **Finds the save seal key.** If the game changes it, the builder finds the new key and can confirm it with one of your saves.
- **Review before building.** Every change shows up as a row in a grid. You can tick or untick it, fix names, descriptions and icons, and reorder new zones.
- **One-file output.** It builds a single `index.html` editor that works offline, on PC and on phones. The version label is stamped with the build date and time.
- **Safe updates.** The previous editor is kept as `index.previous.html`. Each build starts from the last one, so updates add up.
- **Raw save tool.** It can open any `.sav` as plain text, let you edit it, and seal it again.
- **Uses the game's own icon.** The icon is copied from the game exe at build time.

---

## Download & build

### Requirements
- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), or Visual Studio 2026 with the *.NET desktop development* workload

### Build
1. Download or clone this repository.
2. Drag `AzerothSurvivors.exe` onto **`build.bat`**. This also gives the app the game's icon.
   You can also double-click `build.bat`, or open `AzsEditorBuilder.vbproj` in Visual Studio and press **F5**.
3. The app is created at `publish\AzsEditorBuilder.exe`.

```bat
dotnet publish AzsEditorBuilder.vbproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

---

## How to use

### 1. Scan the game
| Field | What to pick |
|---|---|
| **Game exe** | `AzerothSurvivors.exe` |
| **Save (recommended)** | A save made by this game version. It confirms the save seal key and is checked for codex items, zones and achievements the editor doesn't know yet. |
| **WoW folder (optional)** | Not needed. It's only used for gear that is newer than the built-in icon list. |
| **Editor to update** | Leave on **Automatic**. The first scan uses the built-in editor; later scans use your last build. |

Press **Scan game**.

### 2. Review changes
| Row colour | Meaning |
|---|---|
| 🟩 **New** | Will be added to the editor if ticked |
| 🟨 **New key** | The game's save seal key changed |
| 🟦 **Changed** | The game changed a value the editor already has (old value in *Notes*) |
| 🟥 **Missing** | In the editor but not in the game (information only) |
| ⬜ **Info** | Listed only (e.g. gear items in a new slot, which the editor has no room for) |

You can edit **Name**, **Description**, **Icon** (a WoW icon name, e.g. `INV_Misc_Idol_03`) and **Extra**
before building. **Extra** holds the achievement tab, a perk's max rank, a codex item's rarity (0–4),
or a talent's position as `tree|max rank|row|column` (e.g. `Warrior Arms|2|1|1`).
New zones are added after the last zone. Use **Move up / Move down** to change their order, because zone order decides what unlocks next.

### 3. Build the editor
Pick an output folder and press **Build save editor**. Then upload the new `index.html` to your GitHub Pages repo
to update the online editor.

---

## Troubleshooting

| Problem | Fix |
|---|---|
| "No key in the game file opens your save" | Make sure the save was made by the same game version as the exe you scanned. |
| Nothing new found after an update | Use **Export strings dump…** (Scan tab) and **Save report…** (Review tab), then open an issue with both files attached. |
| The app has the default icon | Delete `app.ico` (if there is one) and drag `AzerothSurvivors.exe` onto `build.bat` again. |
| The build fails | Install the .NET 10 SDK and run `dotnet --version` to check it. |

---

## How it works

1. **Exact strings.** Azeroth Survivors is a compiled (Rust) program. Its texts sit back to back in the exe with no separators, so the builder finds where each one starts and ends from the code and data that use it: `(pointer, length)` pairs in the data tables, and `lea` + length instructions in the code. Plain text files and script-based games are read directly instead.
2. **Learning the layout.** For each table (achievements, pets, talents and so on), the builder finds where the ids the editor already knows sit. It learns what an entry looks like from those: where the name, description and icon sit around the id, and which bytes of the game's data record hold the numbers (max rank, row, column, rarity). Everything it learns is checked against the values the editor already has.
3. **Finding new entries.** Unknown ids that sit in the same table with the same layout are reported as new, with their names, icons and numbers read from the same places. Tabards are read from their records (name + id number).
4. **Spotting changes.** The same rules are used to read every known entry again; values the game changed (renamed talents, new icons, re-ranked talents) are reported as *Changed*.
5. **Achievements built by code.** Some ids never appear as text: the code writes them into memory piece by piece. A small emulator replays those writes to recover the ids. Format strings (`zone9:` + zone) are recognised and expanded for every zone and class.
6. **Gear icons.** The game's gear tables hold WoW display ids. For each class there are set names, a weapon list pointer, then 5 tiers x 8 display ids; each weapon record has 5 names and 5 display ids; PvP records have Alliance and Horde rows. An empty armour id uses the same slot from a lower tier first, then a higher one; an empty PvP id uses regular tier 4. That is the game's own rule. The display ids become icons through the built-in list, which was made by joining the game's display ids with WoW 1.12's `ItemDisplayInfo.dbc` (data from the SurvivorsDragonUi project's extraction). There is an optional MPQ/DBC reader for newer ids.
7. **Gear.** Each of the editor's gear name lists (set names, slot names, weapons, rarities) is found in the game as a run of strings in the same order. A different string inside the run is a rename; extra strings right after it are new tiers.
8. **Seal key.** The game stores its save key pre-mixed for HMAC-SHA1 (key XOR 0x36 / 0x5C). The builder finds and decodes that block, and confirms the key against your save when you give it one.
9. **Save file pass.** The save is unsealed and its `seen`, zone and achievement lists are compared with the editor. Unknown ids are matched to their records in the exe (name, icon, zone name at record +16).
10. **Writing the editor.** Only the data tables inside `index.html` are changed (`const ACHS=…`, `const PETS=…` and so on), plus small fixes that let the Gear tab show extra tiers and item icons.

Tested on the real `AzerothSurvivors.exe` (141 MB): it finds 95,000+ exact strings in about 2 seconds. With entries deliberately removed from the editor, it found every one of them again with the right name, icon and numbers.

## Project structure

```
AzsEditorBuilder/
├── AzsEditorBuilder.vbproj   Windows Forms app (.NET 10)
├── build.bat                 One-click build (+ copies the game icon)
├── Program.vb
├── Core/
│   ├── ExeScanner.vb         Reads the exe, finds tables and the seal key
│   ├── ExeScanner.Props.vb   Reads icons, names, ranks and talent positions
│   ├── ExeScanner.Gear.vb    Gear names, new tiers, item ids and icons
│   ├── ExeScanner.Pe.vb      Compiled exe support: exact strings, data records, code emulator, tabards
│   ├── ExeScanner.Ach.vb     Achievement families and achievements found by title
│   ├── ExeScanner.Codex.vb   Codex legendary abilities per class (ability records)
│   ├── ExeScanner.Save.vb    Checks your save for ids the editor doesn't know
│   ├── gear-icons.json       Built-in display id -> icon list for all gear in the game
│   ├── WowData.vb            Optional WoW 1.12 MPQ + DBC reader (gear newer than the list)
│   ├── GameData.vb           What the current editor already knows
│   ├── EditorTemplate.vb     Reads/writes the data tables in index.html
│   ├── EditorBuilder.vb      Applies the ticked changes
│   ├── SaveSeal.vb           RSE1 save seal (unseal / seal / verify)
│   └── IconExtractor.vb      Copies the icon out of the game exe
├── UI/
│   ├── MainForm.vb           The window
│   └── Settings.vb           Remembers the last paths used
├── template/
│   └── index.html            The save editor the builder starts from
└── tools/IconTool/           Helper used by build.bat to make app.ico
```

---

## Links

- 🌐 **Online save editor:** https://polemarxos.github.io/Azeroth-Survivors-Save-Editor/

## Disclaimer

This is an unofficial fan tool. It is not affiliated with or endorsed by Blizzard Entertainment or the makers of Azeroth Survivors.
World of Warcraft and Warcraft are trademarks of Blizzard Entertainment, Inc.
Back up your saves before editing them.
