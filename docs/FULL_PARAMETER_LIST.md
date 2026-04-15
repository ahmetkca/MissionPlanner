# Full Parameter List — How it works

Self-contained reference for how the **Config → Full Parameter List** view works in Mission Planner: where parameters come from, how they are displayed, how reads and writes are dispatched, how metadata (units/range/options/description) is fetched, and how the `.param` file format plays in.

All line numbers are against the repo state at commit time of this doc (`master` with HEAD `05dbb61da` at the time of writing). If files have drifted, the section headings and symbol names still point you to the right place; use the quick-navigation commands at the end of this doc to re-find them.

---

## 1. Mental model (one diagram)

```
┌──────────────────────┐   MAVLink (PARAM_REQUEST_LIST / PARAM_VALUE / PARAM_SET / PARAM_REQUEST_READ)
│   ArduPilot vehicle  │ ◄────────────────────────────────────────────────────────────────────────┐
└──────────────────────┘                                                                          │
         ▲  │                                                                                     │
         │  │ PARAM_VALUE (one per param)                                        PARAM_SET        │
         │  ▼                                                                    (one per write)  │
┌────────────────────────────────────────────────────────────────────────────────────────────────┤
│  MAVLinkInterface   (ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs)                            │
│    • getParamListAsync()   ─► fills MAVLinkParamList                                           │
│    • setParamAsync()       ─► sends PARAM_SET, waits for echo, updates MAVLinkParamList        │
└────────────────────────────────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────────────┐
│  MainV2.comPort.MAV.param      (MAVLinkParamList)       │   ← in-memory source of truth
│  MainV2.comPort.MAV.param_types (Dictionary<string,T>)  │     for all UI views
└─────────────────────────────────────────────────────────┘
         │                                     ▲
         │ reads                               │ edits (via _changes staging table)
         ▼                                     │
┌───────────────────────────────────────────────────────────────────────────────────────┐
│  ConfigRawParams   (GCSViews/ConfigurationView/ConfigRawParams.cs)                    │
│    • DataGridView "Params" with columns: Command Value Default Units Options Desc Fav │
│    • TreeView (prefix nav)                                                            │
│    • Search / filter / favorites                                                      │
│    • .param load/save/compare buttons                                                 │
│    • Frame-preset loader (GitHub → ArduPilot /Tools/Frame_params/)                    │
└───────────────────────────────────────────────────────────────────────────────────────┘
         │
         ▼ (joined at render time)
┌───────────────────────────────────────────────────────────────────────────────────────┐
│  ParameterMetaDataRepository   (ExtLibs/Utilities/ParameterMetaDataRepository.cs)     │
│    • MemoryCache (5-min sliding)                                                      │
│    • Delegates to:                                                                    │
│        ParameterMetaDataRepositoryAPMpdef  (primary — apm.pdef.xml from autotest)     │
│        ParameterMetaDataRepositoryAPM      (legacy/fallback)                          │
│        ParameterMetaDataRepositoryPX4      (when vehicleType == "PX4")                │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 2. Where the tab is registered

The "Config" tab is hosted by **`GCSViews/SoftwareConfig.cs`**, which uses a **`BackstageView`** control (Mission Planner's left-rail "tabbed" container). Each page is a `MyUserControl` subclass added via `AddBackstageViewPage(type, label, parent, advanced)`.

- Registration line:

  `GCSViews/SoftwareConfig.cs:229`
  ```csharp
  AddBackstageViewPage(typeof(ConfigRawParams), Strings.FullParameterList, null, false);
  ```

- Guard at `SoftwareConfig.cs:228`:
  ```csharp
  if(!MainV2.comPort.BaseStream.IsOpen || gotAllParams)
      AddBackstageViewPage(...);
  ```

  i.e. the page is added when either the vehicle is disconnected (so the user can work offline from a loaded `.param` file), **or** when all params have finished downloading from the vehicle (`gotAllParams` computed from `param.TotalReceived == param.TotalReported`).

- The tab label `Strings.FullParameterList` is a resource string in `Resources/strings.resx` and the locale-specific `strings.*.resx` files. Changing the displayed label is a resource edit, not a code edit.

---

## 3. Files that make up the view

| File | Purpose |
|---|---|
| `GCSViews/ConfigurationView/ConfigRawParams.cs` | All runtime logic. 1362 lines. Class `ConfigRawParams : MyUserControl, IActivate, IDeactivate`. |
| `GCSViews/ConfigurationView/ConfigRawParams.Designer.cs` | WinForms designer-generated layout (columns, buttons, grid, tree, split container). |
| `GCSViews/ConfigurationView/ConfigRawParams.resx` | Base (English) strings, icons. |
| `GCSViews/ConfigurationView/ConfigRawParams.<locale>.resx` | Translations (ar, az-Latn-AZ, de-DE, fr, id-ID, it-IT, ja-JP, ko-KR, pt, ru-KZ, tr, uk, zh-Hans/Hant, zh-TW). |

There is also an **Uno UI port** under `ExtLibs/uno/UnoUI/UnoUI.Shared/test/MissionPlanner.GCSViews.ConfigurationView.ConfigRawParams.xaml[.cs]`. That is an experimental cross-platform variant and not used by the desktop WinForms build.

---

## 4. UI layout (from `ConfigRawParams.Designer.cs`)

The form is a `SplitContainer`:

- **Panel 1 (left):** `TreeView treeView1` — prefix tree rebuilt from the params in the grid (`ATC_`, `GPS_`, `Q_`, etc.). Clicking a node sets `filterPrefix` and narrows the grid.
- **Panel 2 (right):** the main grid plus a button row.
  - Collapse toggle: `but_collapse` (`<` / `>`), persisted in `Settings.Instance["rawparam_panel1collapsed"]`.

**Grid** — `Params` is a `MissionPlanner.Controls.MyDataGridView` with 7 columns (all named constants in the Designer file):

| Column | Type | Meaning |
|---|---|---|
| `Command` | `DataGridViewTextBoxColumn` | Parameter name (e.g. `ATC_RAT_PIT_P`). Read-only. |
| `Value` | `DataGridViewTextBoxColumn` | Current/edited value. Editable. Math expressions accepted (see §7). |
| `Default_value` | `DataGridViewTextBoxColumn` | Default from firmware metadata, or `NaN`. Column visibility is toggled based on whether any defaults were observed. |
| `Units` | `DataGridViewTextBoxColumn` | Units from metadata (`m/s`, `deg`, `PWM`, ...). |
| `Options` | `DataGridViewTextBoxColumn` | `Range` and/or `Values`/`Bitmask` metadata, newline-joined. |
| `Desc` | `DataGridViewTextBoxColumn` | Long-form description from metadata. |
| `Fav` | `DataGridViewCheckBoxColumn` | User "favorite" flag; favorites sort to the top. Persisted in `Settings.Instance.GetList("fav_params")`. |

**Button row** (all `MissionPlanner.Controls.MyButton`):

| Control | Click handler | Effect |
|---|---|---|
| `BUT_writePIDS` | `BUT_writePIDS_Click` (line 257) | Write staged changes to the vehicle (`setParam` per row). |
| `BUT_rerequestparams` | `BUT_rerequestparams_Click` (line 421) | Re-download all params via `getParamList()`. |
| `BUT_reset_params` | `BUT_reset_params_Click` | Reset params to firmware defaults (`COMMAND_LONG` with `MAV_CMD_PREFLIGHT_STORAGE`). |
| `BUT_commitToFlash` | — | Send commit-to-flash command. Only visible if `MainV2.DisplayConfiguration.displayParamCommitButton`. |
| `BUT_load` | `BUT_load_Click` (line 127) | Load a `.param` file into the grid (and into `MAV.param` if offline). |
| `BUT_save` | `BUT_save_Click` (line 224) | Save current grid values as `.param`. |
| `BUT_compare` | `BUT_compare_Click` (line 396) | Diff current vehicle params against a `.param` file (`ParamCompare` form). |
| `BUT_paramfileload` | `BUT_paramfileload_Click` (line 946) | Load the `.param` file selected in the adjacent combobox (frame preset from ArduPilot GitHub). |
| `BUT_refreshTable` | — | Manual grid rebuild button, only shown when `Settings.Instance.GetBoolean("SlowMachine", false)` is true. |

**Filters:**

- `txt_search` — regex (case-insensitive) applied to all cell values. Empty resets. `filterList(...)` at line 889.
- `chk_modified` — show only rows in `_changes`.
- `chk_none_default` — show only rows where `Value != Default_value`.

---

## 5. In-memory data model

### 5.1 The authoritative snapshot

`MainV2.comPort` is the current `MAVLinkInterface` (see `ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs`). It holds a `MAVList` keyed by `(sysid, compid)`; the "currently focused" MAV is reached via `MainV2.comPort.MAV`.

- **`MainV2.comPort.MAV.param`** — a **`MAVLinkParamList`** (`ExtLibs/Mavlink/MAVLinkParamList.cs`), `List<MAVLinkParam> + INotifyPropertyChanged`, indexed by name. Carries `TotalReceived` and `TotalReported` counters used to detect missing-params conditions.
- **`MainV2.comPort.MAV.param_types`** — `Dictionary<string, MAV_PARAM_TYPE>`; maps a name to its on-the-wire type as reported by the vehicle (`UINT8`, `INT32`, `REAL32`, ...).

### 5.2 The per-parameter record

`MAVLinkParam` (`ExtLibs/Mavlink/MAVLinkParam.cs`) carries:

- `Name` (string)
- Raw byte payload (the wire encoding)
- Two MAVLink types: the **declared** on-wire type and the **effective** type (on ArduPilot, PARAM_FLOAT capability means everything is transmitted as float but the declared type still governs integer rounding/clamping).
- `default_value` (nullable `double`) — populated only when the metadata source provides one.
- Convenience accessors: `Value` (as `double`), `float_value`, `ToString()`, `default_value_to_string()`.

There is a second, generator-emitted copy at `ExtLibs/Mavlink/pymavlink/generator/CS/MAVLinkParam.cs` — that's the pymavlink C# generator's version. The *runtime* one the rest of the codebase uses is the hand-maintained `ExtLibs/Mavlink/MAVLinkParam.cs`.

### 5.3 The staging table for edits

`ConfigRawParams._changes` is a `Hashtable` keyed by parameter name, value is the `double` the user typed. Populated by `Params_CellValueChanged` (line 454). **Nothing is written to the vehicle until `BUT_writePIDS` is clicked.**

### 5.4 The row cache

Because building 1000+ `DataGridViewRow`s is expensive, `ConfigRawParams` keeps a static `rowlist` of rendered rows. On `Activate()`, a full rebuild happens only when:

- `rowlist.Count == 0`, or
- `SlowMachine` setting is false, or
- `rowlist.Count != MainV2.comPort.MAV.param.Count()` (vehicle changed or a feature was enabled that exposed new params).

Otherwise only the `Value` column is refreshed in place.

---

## 6. Read path — how params arrive

### 6.1 Call sites that trigger `getParamList()`

`getParamList()` is called from many places — it's how Mission Planner refreshes its snapshot:

- `MainV2.cs:1684` — during connect.
- `MainV2.cs:4100` — sysid/compid switch.
- `Controls/ConnectionControl.cs:141`, `Controls/ConnectionOptions.cs:43` — connection UI.
- `Log/MavlinkLog.cs:1165` — replaying a tlog.
- `GCSViews/ConfigurationView/ConfigRawParams.cs:433` — the **Refresh Params** button.
- `GCSViews/ConfigurationView/ConfigPlanner.cs:503`, `ConfigParamLoading.cs:52`, `ConfigOSD.cs:317`, `ConfigFriendlyParams.cs:223` — various other config views that need a fresh snapshot.

### 6.2 The MAVLink conversation (`MAVLinkInterface.cs:1945` `getParamListAsync`)

1. Send `PARAM_REQUEST_LIST` (`mavlink_param_request_list_t { target_system, target_component }`).
2. Subscribe to `PARAM_VALUE`. The vehicle streams back one message per parameter:
   ```
   mavlink_param_value_t {
     char[16] param_id;       // name, null-padded
     float    param_value;    // wire value (always float if PARAM_FLOAT capability)
     uint8_t  param_type;     // MAV_PARAM_TYPE enum
     uint16_t param_count;    // total number of params the vehicle will send
     uint16_t param_index;    // 0..param_count-1  (or 65535 for "unknown index")
   }
   ```
3. `TotalReported` is set from the first `param_count` observed.
4. For each response:
   - Decode name (strip the first NUL byte onwards).
   - Construct `MAVLinkParam` — two branches:
     - If vehicle advertises `MAV_PROTOCOL_CAPABILITY.PARAM_FLOAT` *or* `apname == ARDUPILOTMEGA`, treat the wire as `REAL32` but remember the declared type separately. `MAVLinkInterface.cs:2056-2062`.
     - Otherwise treat it as the declared type natively. Line 2066.
   - Insert into `newparamlist[paramID]` and record the type in `MAVlist[sysid,compid].param_types[paramID]`.
   - `indexsreceived.Add(par.param_index)` unless it was 65535.
5. If progress stalls (no new responses for a while) and some indices are still missing, fall back to **one-by-one** mode: send `PARAM_REQUEST_READ` for each missing index (name **or** index based path). Search the function for `onebyone` and `param_request_read` to locate that branch.
6. When `indexsreceived.Count == param_total` or all retries are exhausted, the async task returns the final list, which becomes `MAVlist[sysid,compid].param`.

The GUI-facing wrapper is `public void getParamList()` at `MAVLinkInterface.cs:1778` — it creates a progress dialog (`CreateIProgressReporterDialogue(Strings.GettingParams ...)`), runs the work in a background operation, and disposes.

### 6.3 Reference — MAVLink message definitions

MAVLink message definitions live in the submodule/generated XML:

- `ExtLibs/Mavlink/message_definitions/v1.0/` — original XML.
- `ExtLibs/Mavlink/MAVLink.cs` — generated C# (`enum MAVLINK_MSG_ID`, all `mavlink_*_t` structs). Search for `MAVLINK_MSG_ID.PARAM_VALUE` to find the generated struct layout.

Upstream protocol reference: <https://mavlink.io/en/services/parameter.html>.

---

## 7. Render path — how rows get their data

`ConfigRawParams.Activate()` → `processToScreen()` (line 563).

```csharp
if (startup) {
    foreach (string item in MainV2.comPort.MAV.param.Keys) list.Add(item);
    rowlist.Clear();

    Parallel.ForEach(list, value => {
        var row = new DataGridViewRow() { Height = 36 };
        lock (rowlist) rowlist.Add(row);
        row.CreateCells(Params);
        row.Cells[Command.Index].Value = value;
        row.Cells[Value.Index].Value   = MainV2.comPort.MAV.param[value].ToString();
        row.Cells[Fav.Index].Value     = fav_params.Contains(value);

        if (MainV2.comPort.MAV.param[value].default_value.HasValue) {
            has_defaults = true;
            row.Cells[Default_value.Index].Value =
                MainV2.comPort.MAV.param[value].default_value_to_string();
        } else {
            row.Cells[Default_value.Index].Value = "NaN";
        }

        // Pull metadata
        var desc    = ParameterMetaDataRepository.GetParameterMetaData(value, Description, firmware);
        var range   = ParameterMetaDataRepository.GetParameterMetaData(value, Range,       firmware);
        var options = ParameterMetaDataRepository.GetParameterMetaData(value, Values,      firmware);
        var units   = ParameterMetaDataRepository.GetParameterMetaData(value, Units,       firmware);

        row.Cells[Units.Index].Value   = units;
        row.Cells[Options.Index].Value = (range + "\n" + options.Replace(",", "\n")).Trim();
        row.Cells[Desc.Index].Value    = desc;
        row.Cells[Command.Index].ToolTipText = row.Cells[Value.Index].ToolTipText = AddNewLinesForTooltip(desc);
    });
}
Params.Rows.AddRange(rowlist.ToArray());
Params.Sort(Params.Columns[Command.Index], ListSortDirection.Ascending);
if (!Panel1Collapsed) BuildTree();
```

Notes:

- The loop is parallel but each row is isolated; `rowlist` is lock-guarded.
- The sort comparator (`OnParamsOnSortCompare`, line 833) sorts favorites to the top and uses a natural-string comparer (so `RC10_` sorts after `RC9_`).
- **Math expressions** in the `Value` cell: `ConfigRawParams.cs` imports `org.mariuszgromada.math.mxparser`. When the user types `=12*0.25` or similar, the value is evaluated. Search for `mXparser` usages in the file to see exactly where and how. Library: <https://mathparser.org/>.

### 7.1 Tree build

`BuildTree()` (line 688) walks the sorted list of parameter names and groups them by shared underscore-delimited prefix, producing a hierarchical `TreeView`. Favorites are not special-cased; the tree is prefix-only. Clicking a node sets `filterPrefix`; the grid is then filtered by `filterList` (line 889).

---

## 8. Write path — how edits reach the vehicle

### 8.1 Staging (per-cell edit)

`Params_CellValueChanged` (line 454):

1. Fires only on the `Value` column and after startup.
2. Applies small ad-hoc rules: e.g. RC/HS `*_REV` of `0` is coerced to `-1` (`0` is not a valid value for those params).
3. Looks up `Range` metadata (min/max) via `ParameterMetaDataRepository` and clamps/refuses the edit if out of range, displaying a warning.
4. Records the delta in `_changes[name] = double.Parse(newValue)`.
5. Paints the cell's `BackColor` so changed cells are visually distinct.

Nothing is sent yet — everything is staged.

### 8.2 Commit (the "Write Params" button) — `BUT_writePIDS_Click` (line 257)

1. `_changes.Keys.SortENABLE()` — ensures any `*_ENABLE` flags are written **last**; this matters because enabling a feature may expose additional sub-params, and disabling one would otherwise hide params you haven't written yet.
2. Confirmation dialog:
   - ≤ 20 changes: shows a detailed `{name}: {prev} -> {new}` list.
   - `> 20`: just asks "change N parameters?".
3. For each staged name:
   - Calls `MainV2.comPort.setParam(name, (double)_changes[name])` — see §8.3.
   - On success, removes from `_changes` and un-highlights the cell.
   - Tracks whether any written param had `RebootRequired: true` in its metadata (via `ParameterMetaDataRepository.GetParameterRebootRequired`, line 162 of the repository).
4. Shows a summary message: `"N parameters successfully saved"` / `"Not all parameters successfully saved"` / `"Reboot is required"`.
5. **Grow-check:** if after all writes `MAV.param.TotalReceived != TotalReported` (the vehicle started advertising more params because a feature was enabled), either:
   - Armed: shows a notice and pins `TotalReported = TotalReceived` so the table stays usable.
   - Disarmed: automatically clicks `BUT_rerequestparams` to do a full re-download.

### 8.3 The single-param MAVLink write (`MAVLinkInterface.cs:1620` `setParam`)

Resolves to `setParamAsync(sysid, compid, paramname, value, force)` at line 1635:

1. If the param isn't in the current snapshot, log & return `false` (we refuse to set params we've never seen).
2. If `Value == value && !force`, return `true` without sending.
3. Build `mavlink_param_set_t`:
   - `target_system`, `target_component` — the current MAV.
   - `param_id` — 16-char NUL-padded name.
   - `param_type` — the recorded type from `param_types[paramname]`.
   - `param_value`:
     - If `PARAM_FLOAT` capability or `apname == ARDUPILOTMEGA`: `new MAVLinkParam(name, value, REAL32).float_value`.
     - Else: encoded as the declared type.
4. Send `PARAM_SET` via `generatePacket(...)`.
5. Subscribe to `PARAM_VALUE` — expect an **echo** from the vehicle with the new stored value.
6. Up to **3 retries**, 700 ms between each.
7. On echo:
   - Update `MAVlist[sysid,compid].param[name]` with the echoed value.
   - `lastparamset = DateTime.UtcNow`.
   - If the vehicle's `param_count` grew, the code notes it for the caller to handle (currently the grow-check runs in `BUT_writePIDS_Click`, see §8.2).
8. Return `true`.

The `giveComport = true/false` wrapper (line 1651/1770) arbitrates exclusive use of the serial link so no other background work collides mid-transaction.

### 8.4 Reset to defaults and commit-to-flash

- `BUT_reset_params_Click` — sends `MAV_CMD_PREFLIGHT_STORAGE` with appropriate params. Search for `MAV_CMD_PREFLIGHT_STORAGE` in `MAVLinkInterface.cs` to find the helper.
- `BUT_commitToFlash` — optional, gated by `MainV2.DisplayConfiguration.displayParamCommitButton`. Writes the current RAM params to non-volatile storage. Relevant on autopilots where `setParam` updates RAM only.

---

## 9. Parameter metadata (description, units, range, values, bitmask, default, reboot)

### 9.1 The unified frontend — `ExtLibs/Utilities/ParameterMetaDataRepository.cs`

- Static class.
- `MemoryCache _cache` with **5-minute sliding expiration** per `(nodeKey + metaKey + vehicle)` tuple.
- Public accessors used by `ConfigRawParams`:
  - `GetParameterMetaData(nodeKey, metaKey, vehicleType) → string`
  - `GetParameterOptionsInt(nodeKey, vehicle) → List<KeyValuePair<int,string>>`  (for enum Values)
  - `GetParameterBitMaskInt(nodeKey, vehicle) → List<KeyValuePair<int,string>>`  (for Bitmask)
  - `GetParameterRebootRequired(nodeKey, vehicle) → bool`  (line 162)

### 9.2 Meta keys — `ExtLibs/Utilities/ParameterMetaDataConstants.cs`

The string keys recognised by the metadata system:

```
DisplayName, Description, Units, Range, Values, Increment, User,
RebootRequired, Bitmask, ReadOnly, Volatile, Calibration, Vector3Parameter,
... (plus internal markers: ParamDelimeter="@", PathDelimeter=",", NestedGroup regex)
```

### 9.3 Resolution order (non-PX4)

For ArduPilot vehicles (`vehicleType ∈ { ArduCopter, ArduPlane, ArduRover, ArduSub, AntennaTracker, Heli, Blimp, ... }`), the repository tries in this order:

1. **`ParameterMetaDataRepositoryAPMpdef`** for the requested vehicle.
2. Same, with `vehicleType = "SITL"`.
3. Same, with `vehicleType = "AP_Periph"`.
4. **`ParameterMetaDataRepositoryAPM`** (legacy embedded XML).
5. `String.Empty`.

The first non-empty result is cached and returned.

### 9.4 APMpdef — `ExtLibs/Utilities/ParameterMetaDataRepositoryAPMpdef.cs`

- Loads and parses `apm.pdef.xml` (a merged parameter definition file published by the ArduPilot build system).
- Sources (URLs hard-coded near the top of the file):
  - Latest per vehicle: `https://autotest.ardupilot.org/Parameters/{vehicle}/apm.pdef.xml.gz`
  - Versioned: `https://autotest.ardupilot.org/Parameters/versioned/{vehicle}/stable-{version}/apm.pdef.xml`
- Cached on disk under `Settings.GetDataDirectory()` (typically `C:\ProgramData\Mission Planner\`).
- Redownload cadence: file skipped if `LastWriteTime.AddDays(7) > Now`.
- Parsed lazily into a `Dictionary<string, XDocument> _parameterMetaDataXML` keyed by vehicle name.
- Vehicle list:
  - `SITL`, `AP_Periph`, `ArduSub`, `Rover`, `ArduCopter`, `ArduPlane`, `AntennaTracker`, `Blimp`, `Heli`
  - Versioned: `Copter`, `Plane`, `Rover`, `Sub`, `Tracker`

Upstream publishing pipeline for `apm.pdef.xml` is documented at
<https://ardupilot.org/dev/docs/parameter-generation.html>.

### 9.5 APM (legacy) — `ExtLibs/Utilities/ParameterMetaDataRepositoryAPM.cs`

A flat XML file embedded/updated in the MP install. Used as a final fallback when `apm.pdef.xml` lookup misses. Format is similar in spirit — elements like `<Description>`, `<Units>`, `<Range>`, `<Values>` nested per parameter.

### 9.6 PX4 — `ExtLibs/Utilities/ParameterMetaDataRepositoryPX4.cs`

Separate path used only when the connected stack is PX4, not ArduPilot. Not exercised by ArduPilot users.

---

## 10. `.param` file format (load / save / compare)

### 10.1 File format

Plain text. One parameter per line, `NAME value` separated by whitespace (and/or `,`). Comments begin with `#`. Accepted extensions: `.param`, `.parm` (see `ParamFile.FileMask`).

### 10.2 Implementation

`ExtLibs/Utilities/ParamFile.cs` exposes:

- `static string FileMask` — the OpenFileDialog filter (`"Param List|*.param;*.parm"`).
- `static Hashtable loadParamFile(string filename)` — parses a file to `name → double`.
- `static void SaveParamFile(string filename, Hashtable data)` — writes each entry.

### 10.3 Load flow (`BUT_load_Click`, line 127 → `loadparamsfromfile`, line 149)

- For each entry in the file:
  - Skip a small list of volatile/system-only params: `SYSID_SW_MREV`, `WP_TOTAL`, `CMD_TOTAL`, `FENCE_TOTAL`, `SYS_NUM_RESETS`, `ARSPD_OFFSET`, `GND_ABS_PRESS`, `GND_TEMP`, `CMD_INDEX`, `LOG_LASTFILE`, `FORMAT_VERSION`.
  - If the row exists in the grid: update the `Value` cell (which fires `CellValueChanged` → stages into `_changes`).
  - If `offline && !set`: also insert a new `MAVLinkParam` into `MainV2.comPort.MAV.param` so the user can work on the file without a live connection.
- Missing params (names in file with no matching grid row) are reported.

### 10.4 Save flow (`BUT_save_Click`, line 224)

- Iterate every grid row, parse `Value.Cells[..].Value.ToString()` as `double`, accumulate into a `Hashtable`, then call `ParamFile.SaveParamFile`.
- **Important:** save reads from the **grid**, not from `MAV.param`. So unsaved staged changes are included in the file. (This is intentional — it lets you export "what you're about to write".)

### 10.5 Compare (`BUT_compare_Click`, line 396)

Opens a `ParamCompare` form (`Controls/ParamCompare.cs` or similar — search for `class ParamCompare`). It shows a three-column diff: `name | current MAV value | file value`, with a selection column so the user can bulk-apply differences.

---

## 11. Frame presets (pre-canned `.param` files from GitHub)

`ConfigRawParams.updatedefaultlist` (line 860) and `BUT_paramfileload_Click` (line 946).

- On `Activate()`, a background `ThreadPool.QueueUserWorkItem(updatedefaultlist)` enumerates `/Tools/Frame_params/` (or `/Tools/Frame_params/QuadPlanes/` if `Q_ENABLE >= 1`) in the ArduPilot GitHub repo, via `GitHubContent.GetDirContent("ardupilot","ardupilot","/Tools/Frame_params/",".param")`.
- Results are bound to `CMB_paramfiles` (combobox) → user picks → `BUT_paramfileload` downloads the selected file to `Settings.GetUserDataDirectory()` and feeds it through `loadparamsfromfile`.

GitHub helper: `ExtLibs/Utilities/GitHubContent.cs` (uses the GitHub REST API `https://api.github.com/repos/{owner}/{repo}/contents/{path}`).

---

## 12. Persistence (user settings)

Per-user settings the view reads/writes via `MissionPlanner.Utilities.Settings` (`Settings.Instance[...]`):

| Key | Meaning |
|---|---|
| `rawparam_{columnName}_width` | Remembered per-column widths. |
| `rawparam_splitterdistance` | Tree vs grid split. |
| `rawparam_panel1collapsed` | Whether the tree panel is collapsed. |
| `fav_params` | Comma-separated list of favorite param names. |
| `SlowMachine` | If true, skip parallel rebuild on every Activate; show `BUT_refreshTable`. |

Settings live on disk under the user data dir:
`C:\Users\<user>\Documents\Mission Planner\config.xml` on Windows.

---

## 13. Related config views (reference — not part of "Full Parameter List")

These share data sources but present curated subsets:

- **`ConfigFriendlyParams`** / **`ConfigFriendlyParamsAdv`** — the "Standard Parameters" / "Advanced Parameters" views. Reads the same `MAV.param` and the same metadata, but uses a curated list of "friendly" parameter names with friendlier labels. Label source: a *separate* file `ParameterMetaDataBackup.xml` + `.xml` in MP install dir.
- **`ConfigPlanner`** / **`ConfigPlannerAdv`** — Mission Planner's own (not vehicle) settings. These edit a `Settings` store, not MAVLink params.
- **`ConfigOSD`**, **`ConfigAC_Fence`**, **`ConfigFailSafe`**, etc. — task-specific UIs that read a handful of params each and use the same `setParam` plumbing internally.

---

## 14. Quick-navigation commands

Commands below assume you're in the repo root. Adjust paths if your checkout differs.

```bash
# Where the Full Parameter List tab is registered
grep -n "FullParameterList" GCSViews/SoftwareConfig.cs

# The main view implementation
ls -la GCSViews/ConfigurationView/ConfigRawParams.*

# UI columns / layout
grep -n "this\.(Command|Value|Default_value|Units|Options|Desc|Fav) *=" \
    GCSViews/ConfigurationView/ConfigRawParams.Designer.cs

# The staging table and commit button
grep -n "_changes\b\|BUT_writePIDS_Click\|processToScreen" \
    GCSViews/ConfigurationView/ConfigRawParams.cs

# MAVLink transport layer
grep -n "public .* setParam\b\|public .* getParamList\|setParamAsync\|getParamListAsync" \
    ExtLibs/ArduPilot/Mavlink/MAVLinkInterface.cs

# Metadata frontend and keys
ls -la ExtLibs/Utilities/ParameterMetaData*
grep -n "public const string" ExtLibs/Utilities/ParameterMetaDataConstants.cs

# Generated MAVLink message structs
grep -n "PARAM_REQUEST_LIST\|PARAM_VALUE\|PARAM_SET\|PARAM_REQUEST_READ" \
    ExtLibs/Mavlink/MAVLink.cs | head

# .param file I/O
cat ExtLibs/Utilities/ParamFile.cs | head -80

# Who calls getParamList()
grep -rn "getParamList()" --include="*.cs"
```

---

## 15. External references

- **MAVLink parameter protocol** — <https://mavlink.io/en/services/parameter.html>
- **ArduPilot parameter metadata generation** — <https://ardupilot.org/dev/docs/parameter-generation.html>
- **ArduPilot frame presets source** — <https://github.com/ArduPilot/ardupilot/tree/master/Tools/Frame_params>
- **ArduPilot parameter docs (human-readable)** — <https://ardupilot.org/copter/docs/parameters.html> (and the equivalent per-vehicle pages)
- **`apm.pdef.xml` publishing location** — <https://autotest.ardupilot.org/Parameters/>
- **`mxparser` math expressions library** (used for `=expr` in the `Value` cell) — <https://mathparser.org/>

---

## 16. Glossary

| Term | Meaning |
|---|---|
| **GCS** | Ground Control Station — Mission Planner itself. |
| **Param** | A vehicle-side configuration value stored in ArduPilot's parameter table (EEPROM / flash). |
| **MAVLink** | The binary message protocol between GCS and vehicle. |
| **`MAV_PARAM_TYPE`** | MAVLink enum declaring the native type of a param (`UINT8`, `INT32`, `REAL32`, ...). |
| **`PARAM_FLOAT` capability** | Capability bit advertising that the link transports all param values as floats regardless of declared type. ArduPilot always sets it. |
| **`apm.pdef.xml`** | The merged parameter-definition XML generated by the ArduPilot build; the canonical metadata source for MP. |
| **Backstage view** | MP's custom left-rail tabbed container (see `ExtLibs/BSE.Windows.Forms`). |
| **`.param`** | Plain-text parameter dump/restore file. |
| **Frame params** | Pre-canned `.param` files published in ArduPilot's `Tools/Frame_params/` that set good starting values for specific airframes. |
