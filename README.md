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
  - talents (listed for information only)
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
| **Save (optional)** | A save made by this game version. It is used to confirm the save seal key. |
| **Editor to update** | Leave on **Automatic**. The first scan uses the built-in editor; later scans use your last build. |

Press **Scan game**.

### 2. Review changes
| Row colour | Meaning |
|---|---|
| 🟩 **New** | Will be added to the editor if ticked |
| 🟨 **New key** | The game's save seal key changed |
| 🟥 **Missing** | In the editor but not in the game (information only) |
| ⬜ **Info** | Listed only (talents: their tree position can't be read automatically) |

You can edit **Name**, **Description**, **Icon** (a WoW icon name, e.g. `INV_Misc_Idol_03`) and **Extra**
before building. **Extra** holds the achievement tab, a perk's max rank, or a codex item's rarity (0–4).
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

1. **Text extraction.** The exe is read as bytes, and every string is pulled out. That includes UTF-8 and UTF-16 text, and quoted literals inside embedded code.
2. **Learning the layout.** For each table (achievements, pets and so on), the builder finds where the ids the editor already knows sit in the file. It then learns what those entries look like: the characters around each id, and where the name and description follow it.
3. **Finding new entries.** Unknown ids in the same area with the same layout are reported as new, together with their names.
4. **Seal key.** The `RSE1:` saves are sealed with HMAC-SHA1. The builder collects key candidates from the exe and tests them against your save's seal until one matches.
5. **Writing the editor.** Only the data tables inside `index.html` are changed (`const ACHS=…`, `const PETS=…` and so on). The editor's code and layout are left untouched.

---

## Project structure

```
AzsEditorBuilder/
├── AzsEditorBuilder.vbproj   Windows Forms app (.NET 10)
├── build.bat                 One-click build (+ copies the game icon)
├── Program.vb
├── Core/
│   ├── ExeScanner.vb         Reads the exe, finds tables and the seal key
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
