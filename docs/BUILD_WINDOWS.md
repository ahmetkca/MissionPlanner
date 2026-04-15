# Building & Running Mission Planner on Windows 11

Running log of building ArduPilot Mission Planner from source on Windows 11 with Visual Studio 2026.

> **Note on VS version.** The official README prescribes Visual Studio 2022 (v17). This document tracks a build using **Visual Studio 2026 Community (v18.4.3)**, which is what the developer machine has installed. Any deviations from the README path that arise because of the VS version difference are called out inline.

---

## 1. Environment at start

| Item | Value |
|---|---|
| OS | Windows 11 Pro 10.0.26200 |
| Shell used | PowerShell 7.6.0-rc.1 (commands are also valid in Git Bash on the same machine) |
| Repo path | `C:\Users\ahmet\projects\MissionPlanner` |
| Git | 2.53.0.windows.2 |
| Visual Studio | Community 2026, v18.4.3 at `C:\Program Files\Microsoft Visual Studio\18\Community` |
| MSBuild | `C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe` |
| vswhere | `C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe` |
| .NET Framework 4.7.2 targeting pack | Installed (`C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2`) |
| .NET Framework 4.8 targeting pack | Installed (`...\.NETFramework\v4.8`) |
| .NET SDK (modern) | 10.0.201 (not required for this build; present on machine) |
| NuGet CLI | Not on `PATH`. Repo ships `.nuget\NuGet.exe`; MSBuild's `-restore` flag makes an external `nuget.exe` unnecessary anyway. |

---

## 2. What Mission Planner is (codebase at a glance)

Derived from reading `README.md`, `MissionPlanner.csproj`, `MissionPlanner.sln`, `.gitmodules`, `vs2022.vsconfig`, and `.github/workflows/main.yml`.

- **Type:** C# WinForms desktop Ground Control Station (GCS) for ArduPilot.
- **Entry point:** `MissionPlanner.Program` (defined in root-level `.cs` files).
- **Target framework:** `net472` (see `MissionPlanner.csproj` line 3), but compiled output is written to `bin\<Config>\net461\` (csproj lines 43, 50) to keep path-compat with older scripts/CI. The `net461` here is a folder name, not an actual target framework.
- **Main solution:** `MissionPlanner.sln` at repo root. The secondary `MissionPlannerLib.sln` aggregates library projects; the `Updater/` and `APMPlannerXplanes/` solutions are separate.
- **Project graph:** dozens of `ExtLibs/*.csproj` referenced from `MissionPlanner.csproj` (GMap.NET, MAVLink, GDAL, GStreamer, DroneCAN, LibVLC, KMLib, etc.), plus a prebuilt `Updater\bin\Release\Updater.exe` reference.
- **Submodule:** `ExtLibs/mono` (`meee1/mono.git`, shallow) is the only git submodule.
- **Official build host:** Windows + Visual Studio 2022. README explicitly says building on other systems is "not supported." CI workflow (`.github/workflows/main.yml`) runs on `windows-latest` and calls MSBuild with `-restore -t:Build -p:Configuration=Release MissionPlanner.sln`.
- **Runtime-only native deps (not required to build):** GStreamer, GDAL, LibVLC, DirectShow. These are only needed when you actually exercise features that use them.

---

## 3. Steps executed

Each subsection records the exact command run, the outcome, and any surprises.

### 3.1 Initialize git submodule `ExtLibs/mono`

**State before:** `git submodule status` showed `-f76095b7f375a926c831c71e88fd28522c73ea0e ExtLibs/mono` (leading `-` = not initialized). Directory existed but was empty.

**Command (from repo root):**

```bash
git submodule update --init --progress
```

**Outcome:** Cloned `https://github.com/meee1/mono.git` (shallow, ~82 MiB, 57 075 objects) and checked out the pinned SHA `f76095b7f375a926c831c71e88fd28522c73ea0e`.

**Surprise — benign warnings:**

```
warning: ...:.gitmodules, multiple configurations found for 'submodule.external/aspnetwebstack.path'. Skipping second one!
```

These come from `.gitmodules` **inside** the mono submodule. Because `.gitmodules` says only `ExtLibs/mono` (no `--recursive`), mono's own nested submodules are never fetched — the warnings are about configuration git ignores anyway. Safe to ignore.

**Verification:**

```bash
git submodule status
# expected: ' f76095...'  (leading space, not '-')
```

---

### 3.2 Build `MissionPlanner.sln` (Debug)

**Strategy:** Match the CI recipe in `.github/workflows/main.yml` but use `Configuration=Debug` and the locally-detected MSBuild.

**Locate MSBuild (read-only probe):**

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -prerelease -latest `
    -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\amd64\MSBuild.exe"
```

On this machine this returned:

```
C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe
```

> **Note — `-prerelease` flag.** The GitHub Actions recipe does **not** pass `-prerelease` because `windows-latest` uses stable VS 2022. Our VS 2026 install reports itself as `isPrerelease: false` in `vswhere -format json`, so technically `-prerelease` is not needed. Kept here because VS 2026 was the GA-era target during this build and some machines may have preview builds; it's a safe superset.

**Build command (run from repo root):**

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe" `
    -v:m `
    -restore `
    -t:Build `
    -p:Configuration=Debug `
    MissionPlanner.sln
```

Equivalent Git Bash form (used during this run):

```bash
"C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/amd64/MSBuild.exe" \
    -v:m -restore -t:Build -p:Configuration=Debug MissionPlanner.sln
```

Flags explained:

| Flag | Meaning |
|---|---|
| `-v:m` | verbosity = minimal (errors, warnings, high-level progress only) |
| `-restore` | run NuGet package restore before build — replaces a separate `nuget restore` step |
| `-t:Build` | target = Build (the default, stated explicitly for clarity) |
| `-p:Configuration=Debug` | produce debuggable binaries with symbols |

**Outcome:** exit code `0`. Build succeeded on the first attempt. No prior `Updater.sln` build was needed because `Updater\bin\Release\Updater.exe` is already committed to the repo (referenced by `MissionPlanner.csproj`).

**Warnings observed (non-blocking):**

- Many `CS0168` (unused exception variable) and `CS0219` (assigned-but-unused local) warnings across `Plugins/*`, `ExtLibs/TestPlugin`, `ExtLibs/Flasher`. These predate this work and come from the upstream code.
- `AsyncifyInvocation` style suggestions from the Roslyn Async analyzer on `ExtLibs/TestPlugin/TelstraUTM.cs` and `ExtLibs/ParameterMetaDataGenerator/Program.cs`.
- `NETSDK1138` for `netcoreapp3.1` / `net6.0` / `net7.0` on a few secondary projects (`ExtLibs/Ntrip`, `ExtLibs/NMEA2000`, `ExtLibs/MockDroneID`). These frameworks are past EOL in the .NET 10 SDK we happen to have installed; warnings are informational — those sub-projects still compile fine. They are not needed for the WinForms desktop app to run.
- `System.Runtime.CompilerServices.Unsafe 6.1.2 doesn't support netcoreapp3.1` on `Ntrip.csproj` — same category as above.

None of these warnings affect `MissionPlanner.exe`.

---

### 3.3 Verify build output

```bash
ls bin/Debug/net461/MissionPlanner.exe
# -rwxr-xr-x ... 8069632 ... bin/Debug/net461/MissionPlanner.exe

ls bin/Debug/net461/*.exe
# MissionPlanner.exe, Updater.exe, adb.exe, version.exe

ls bin/Debug/net461/*.dll | wc -l
# 164
```

Plugin DLLs land in `bin/Debug/net461/plugins/` (TestPlugin, FaceMap, OpenDroneID, TerrainMakerPlugin, AltitudeAngelWings.Plugin, …).

---

### 3.4 Launch

**Command (from repo root):**

```powershell
Start-Process -FilePath .\bin\Debug\net461\MissionPlanner.exe -WorkingDirectory .\bin\Debug\net461
```

Or from within the output folder:

```powershell
.\MissionPlanner.exe
```

**Outcome:** Process started, window appeared, UI was interactive. Confirmed manually during this session.

**Note on first run.** Mission Planner creates per-user state under `C:\Users\<you>\Documents\Mission Planner\` and cross-user state under `C:\ProgramData\Mission Planner\`. First launch may trigger an update check to `https://firmware.oborne.me` (see README → "External Services Used" for the full list and how to disable).

---

## 4. Developing / debugging in the IDE

1. Open `MissionPlanner.sln` in Visual Studio 2026 (`C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe`).
2. Wait for the initial solution load (many projects — first load is slow).
3. Ensure **MissionPlanner** is set as the startup project (Solution Explorer → right-click `MissionPlanner` → *Set as Startup Project*).
4. Select configuration **Debug** / platform **Any CPU** from the top toolbar.
5. Press **F5** (or Debug → Start Debugging). The breakpoint-capable debugger will attach.

> If any `*.Designer.cs` file fails to open in the WinForms designer, right-click the `.cs` file → *View Code* first. The designer has occasionally been finicky with cross-`Drawing`-alias projects (`MissionPlanner.Drawing` is referenced with `<Aliases>Drawing</Aliases>` in `MissionPlanner.csproj`).

---

## 5. Troubleshooting (issues actually hit or likely)

| Symptom | Cause | Fix |
|---|---|---|
| `MSB4019` / missing `net472` reference assemblies | .NET Framework 4.7.2 targeting pack not installed | Install via VS Installer → *Individual components* → **.NET Framework 4.7.2 targeting pack**, or grab the standalone ndp472-devpack from Microsoft. |
| Build fails deep inside `ExtLibs/mono` paths with missing files | Submodule not initialized | `git submodule update --init` at repo root. |
| `MissionPlanner.csproj ... Updater, Version=...` not found | `Updater\bin\Release\Updater.exe` missing from working tree | Build `Updater\Updater.sln` in Release first, then retry the main build. On a fresh clone this file is already present. |
| `cd bin/Debug/net461` fails in a shell | Your shell is already inside `bin/Debug/net461` from a prior command, so the relative path no longer resolves. | Use `cd /c/Users/ahmet/projects/MissionPlanner` (absolute) or `cd ../../..` first. |
| Designer errors mentioning the `Drawing` alias | `MissionPlanner.Drawing` is imported under an `extern alias Drawing`. Designer sometimes needs the library built before it can render the form. | Build the solution once, then re-open the designer. |
| `System.IO.FileNotFoundException` at runtime for a native DLL (e.g. `gstreamer-1.0-0.dll`, `gdal_wrap.dll`, `libvlc.dll`) | Optional runtime dep, not installed | Ignore if you don't use that feature. Otherwise install the matching runtime separately (out of scope for this doc). |

---

## 6. Reference — one-shot scripts

### PowerShell (from repo root)

```powershell
# 1. Ensure submodules are present
git submodule update --init

# 2. Locate MSBuild
$msbuild = & "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe" `
    -prerelease -latest `
    -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\amd64\MSBuild.exe" `
  | Select-Object -First 1

# 3. Build Debug
& $msbuild -v:m -restore -t:Build -p:Configuration=Debug MissionPlanner.sln

# 4. Run
Start-Process -FilePath .\bin\Debug\net461\MissionPlanner.exe -WorkingDirectory .\bin\Debug\net461
```

### Git Bash (from repo root)

```bash
git submodule update --init

MSBUILD="C:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/amd64/MSBuild.exe"
"$MSBUILD" -v:m -restore -t:Build -p:Configuration=Debug MissionPlanner.sln

( cd bin/Debug/net461 && start "" MissionPlanner.exe )
```

---

## 7. Not in scope / deferred

- **Release build.** Same command with `-p:Configuration=Release`. CI does this and outputs to `bin\Release\net461\`.
- **Runtime native deps** (GStreamer, GDAL, LibVLC, DirectShow). Install only if/when a dependent feature is exercised.
- **Installer / MSI build.** See `Msi\` and `wix\` directories in the repo.
- **Android / macOS / Linux / iOS builds.** Separate workflows (`android.yml`, `mac.yml`) and out of scope here.

---

## 8. Known-good versions recorded at time of writing

| | Version |
|---|---|
| Mission Planner | `1.3.83` (`MissionPlanner.csproj` `<Version>`) |
| Mono submodule SHA | `f76095b7f375a926c831c71e88fd28522c73ea0e` |
| Visual Studio | Community 2026 `18.4.3+11626.88` |
| MSBuild | bundled with the above (`Current` branch) |
| .NET SDK on machine | `10.0.201` (not required for build) |
| Git | `2.53.0.windows.2` |
