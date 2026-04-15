# C# concepts used in Config / Full Parameter List

A tour of the C# / .NET language and library features actually used in Mission Planner's Config tab and Full Parameter List page. Aimed at a polyglot reader (TypeScript, Python, C/C++, Go) who is new to C#.

Each section follows the same shape:

1. **What it is** in C# (one line).
2. **Rough analogue** in TS / Python / C++ / Go.
3. **Where it appears** in the Config / params code, with file + line.
4. **Gotchas / idioms** worth knowing.

Section order is roughly "structural first, then behavioural, then runtime tricks, then WinForms-specific."

---

## 0. Ground rules before we start

- C# is **statically typed, nominally typed, single-inheritance, garbage-collected** — closer to Java/Go in flavour than to TS/Python.
- The runtime is the **CLR**. The API surface this repo uses is **.NET Framework 4.7.2** (legacy Windows-only — older than .NET 5+/.NET Core). A few projects dual-target `netstandard2.0` or `netcoreapp3.1`. The language features are roughly C# 7.3 era plus a few newer bits the compiler permits.
- File organization is not enforced: one file may contain many types; one type may be split across many files (`partial`). **The filesystem and the type system are decoupled** — unlike Go (one package per dir) or Java (one public class per file).
- Visibility modifiers matter: `public`, `internal` (= "package-private" — same assembly), `protected`, `private`. Default for class members is `private`.

---

## 1. `partial class` + Designer pattern

**What it is.** `partial class Foo` in file A and `partial class Foo` in file B are compiled as one class. No runtime split; purely a source-level convenience.

**Analogue.**
- TS/JS: none. Closest is declaration merging for interfaces, but not classes.
- Python: you can monkey-patch at runtime. Not the same thing.
- C++: roughly "one class across multiple `.cpp` files," which every C++ project does implicitly. `partial` is the explicit spelling for classes.
- Go: a package can span many files — `partial class` is at the *type* level, not the *package* level, which is tighter.

**In this codebase.**

```csharp
// GCSViews/ConfigurationView/ConfigRawParams.cs:24
public partial class ConfigRawParams : MyUserControl, IActivate, IDeactivate
{
    // runtime logic: Activate(), processToScreen(), event handlers...
}

// GCSViews/ConfigurationView/ConfigRawParams.Designer.cs:5
partial class ConfigRawParams
{
    private System.ComponentModel.IContainer components = null;
    // all the control construction: this.Params = new MyDataGridView(); etc.
    private void InitializeComponent() { ... }
}
```

**Why the Designer file exists.** The WinForms visual designer *writes* and *re-writes* `ConfigRawParams.Designer.cs` whenever you drag something on the form. Splitting the generated layout from the human-authored logic prevents the tool from clobbering your code. The Designer file is essentially "machine output; do not hand-edit."

**Gotcha.** If you add a field in `ConfigRawParams.cs` that collides with a name the Designer wants to use, the Designer silently breaks. Convention in this repo: control fields (`BUT_writePIDS`, `Params`, `treeView1`) live only in the Designer file; "behaviour" fields (`_changes`, `filterPrefix`) live only in the main file.

---

## 2. Interface-based lifecycle (`IActivate`, `IDeactivate`)

**What it is.** An interface is a contract: `interface IActivate { void Activate(); }`. A class claims to implement it by listing it after `:`. Unlike C++ multiple inheritance, C# allows inheriting **one** class and **many** interfaces.

**Analogue.**
- TS: `interface IActivate { activate(): void }` + `class X implements IActivate`.
- Python: structural (duck typing) — no explicit declaration, or Protocol.
- Go: structural (duck typing) — no `implements` keyword.
- C++: closest is pure-virtual abstract classes.

**In this codebase.**

```csharp
// ConfigRawParams.cs:24
public partial class ConfigRawParams : MyUserControl, IActivate, IDeactivate
{
    public void Activate()  { ... }  // called when the backstage page is shown
    public void Deactivate(){ ... }  // called when leaving the page
}
```

`IActivate` / `IDeactivate` are defined in this repo (not .NET). The `BackstageView` container looks for these interfaces on the page's type via reflection/cast and invokes them at the right times — a **"mixin via interface"** pattern. This is how MP's multi-page Config tab gets a uniform page lifecycle without a base class.

**Idiom.** Because interfaces can't carry state or default bodies (pre-C# 8), .NET Framework code tends to prefer *many small interfaces* + a concrete base class (`MyUserControl`) that picks up the common state. Note the class's shape: `class ConfigRawParams : MyUserControl, IActivate, IDeactivate` — one base + two interface marker-contracts.

---

## 3. `namespace`, `using`, `extern alias`

**What it is.** A namespace is just a dotted label for grouping types; not tied to filesystem. `using Foo.Bar;` imports all types in that namespace. `extern alias X` is an escape hatch for two assemblies defining the same namespace+type name.

**Analogue.**
- TS/JS: ES modules. Completely different (file-based imports). C# namespaces are more like C++ `namespace`.
- Python: packages, but importing is file-based.
- C++: `namespace foo { ... }` + `using namespace foo;` — near-identical to C#.
- Go: `package foo` — one package per directory, more rigid.

**In this codebase.**

```csharp
// ConfigRawParams.cs:1-19
using log4net;
using MissionPlanner.Controls;
using MissionPlanner.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
...
using org.mariuszgromada.math.mxparser;

namespace MissionPlanner.GCSViews.ConfigurationView
{
    public partial class ConfigRawParams : MyUserControl, IActivate, IDeactivate { ... }
}
```

**`extern alias` — an unusual bit in this repo.** See `MissionPlanner.csproj` line 146–148:

```xml
<ProjectReference Include="ExtLibs\MissionPlanner.Drawing\MissionPlanner.Drawing.csproj">
  <Aliases>Drawing</Aliases>
</ProjectReference>
```

Two assemblies both define `System.Drawing.Bitmap`-ish types (the BCL's and this repo's port). The project sets an alias so code can write:

```csharp
extern alias Drawing;
using SysBmp  = System.Drawing.Bitmap;
using SkiaBmp = Drawing::System.Drawing.Bitmap;
```

to disambiguate. You usually won't run into `extern alias` in regular C#; mentioned because it's sitting in the MP build and can confuse new readers.

**Gotcha.** The newer "file-scoped namespace" syntax (`namespace Foo;` with no braces) is C# 10+. This repo uses the older braced form everywhere.

---

## 4. Properties vs. fields

**What it is.** Fields are raw storage (`public int X;`). Properties are getter/setter pairs that *look* like fields at the call site (`obj.X = 3`). Auto-properties (`public int X { get; set; }`) get the compiler to emit a backing field for you. Properties are the default idiom for anything public.

**Analogue.**
- TS: `get`/`set` accessors on classes — same idea, same call-site syntax.
- Python: `@property` / `@x.setter`. Same mental model.
- C++: no language-level properties; you write explicit methods or use macro/trait tricks.
- Go: no properties; exported fields or explicit methods.

**In this codebase.**

```csharp
// ExtLibs/Mavlink/MAVLinkParamList.cs:13-17
public int TotalReported { get; set; }       // auto-property, both get+set auto-generated

public int TotalReceived                      // read-only property with a body
{
    get { return this.Count; }
}
```

```csharp
// GCSViews/SoftwareConfig.cs:55-58
public bool isConnected
{
    get { return MainV2.comPort.BaseStream.IsOpen; }
}
```

**Idiom.** Always write properties, not raw public fields, even for data-bag types. Frameworks like the WinForms designer, data binding, serializers, and LINQ-to-XML all bind to properties, not fields.

**Gotcha for Go/JS people.** `obj.Foo = 3` *might* be a field assignment *or* a setter call. Reading the type declaration is the only way to know. Usually this is irrelevant; occasionally it matters (e.g. a setter that throws).

---

## 5. Value vs. reference types; `Nullable<T>` / `?`

**What it is.**
- **Value types** (`struct`, `int`, `double`, `bool`, enums, `DateTime`, ...) are **copied** on assignment and **stack-friendly**.
- **Reference types** (`class`, `string`, arrays, delegates) are heap-allocated and passed by reference (the reference itself is copied).
- `T?` where `T` is a value type means `Nullable<T>` — a struct wrapper that adds a `HasValue` flag.

**Analogue.**
- TS/JS: everything is a reference; there is no value-type/reference-type split.
- Python: everything is a boxed object reference.
- C++: value vs. pointer/reference is explicit (`T`, `T&`, `T*`). C#'s `class`/`struct` matches that intuition.
- Go: `struct` is value-typed, slices/maps/interfaces are reference-ish. Closest analogue.

**In this codebase.**

```csharp
// ExtLibs/Mavlink/MAVLinkParam.cs (conceptually)
public double? default_value { get; set; }   // nullable value type

// ConfigRawParams.cs:598-603
if (MainV2.comPort.MAV.param[value].default_value.HasValue) {
    has_defaults = true;
    row.Cells[Default_value.Index].Value =
        MainV2.comPort.MAV.param[value].default_value_to_string();
} else {
    row.Cells[Default_value.Index].Value = "NaN";
}
```

`double?` is how you say "a `double` that may be absent" without using a magic sentinel value like `NaN` or `-1`.

**Gotcha 1.** `string` is a reference type but behaves like a value (immutable, compares by content — `==` is overloaded). That's why `name == "WP_TOTAL"` works as you'd hope.

**Gotcha 2.** `default_value.Value` throws `InvalidOperationException` if `HasValue == false`. Check `HasValue` first, or use `?.`, `??`, or pattern matching (`if (x is double v)`).

**Gotcha 3.** Reference nullability. Before C# 8, **any** reference could be `null` implicitly. This repo doesn't enable nullable reference types (`#nullable enable`), so `string foo = null;` compiles silently. Expect `NullReferenceException` as the runtime equivalent of JS's `TypeError: x is undefined`.

---

## 6. Collections: `Hashtable`, `Dictionary<K,V>`, `List<T>`, custom collections, indexers

**What it is.** C# has both the old non-generic `System.Collections.*` types (`Hashtable`, `ArrayList`) and the modern generic `System.Collections.Generic.*` (`Dictionary<K,V>`, `List<T>`). Indexers are operator-overload for `obj[key]`.

**Analogue.**
- TS/JS: `Map`, `Set`, `Array`. Closer to the generic versions.
- Python: `dict`, `list`. Untyped at runtime (like the old `Hashtable`).
- C++: `std::unordered_map`, `std::vector`. Generic version matches.
- Go: `map[K]V`, `[]T`.

**In this codebase.**

Both styles coexist:

```csharp
// ConfigRawParams.cs:34   (legacy non-generic — just a bag of objects, keys as string)
private readonly Hashtable _changes = new Hashtable();

// ConfigRawParams.cs:237-252   (using the Hashtable as { string → double })
var data = new Hashtable();
foreach (DataGridViewRow row in Params.Rows) {
    data[row.Cells[Command.Index].Value.ToString()] = double.Parse(...);
}

// ExtLibs/Utilities/ParameterMetaDataRepositoryAPMpdef.cs:21
private static Dictionary<string, XDocument> _parameterMetaDataXML = new Dictionary<string, XDocument>();
```

```csharp
// ExtLibs/Mavlink/MAVLinkParamList.cs:9
public class MAVLinkParamList : List<MAVLinkParam>, INotifyPropertyChanged { ... }
```

**Custom collection.** `MAVLinkParamList` extends `List<MAVLinkParam>` and layers name-indexed access (`list["WP_TOTAL"]`). That "string indexer" is implemented via a custom `this[string]` indexer on the class:

```csharp
// conceptually
public MAVLinkParam this[string name] {
    get { return this.FirstOrDefault(p => p.Name == name); }
    set { /* replace or add */ }
}
```

Now the rest of the code can write `MAV.param["RC1_MIN"]` like a dictionary, even though the storage is really a `List<T>` with `PropertyChanged` notifications.

**Idiom.** `Hashtable` vs. `Dictionary<string, double>` — the latter is preferred in modern C#. This repo still uses `Hashtable` in a few places (`_changes`, `tooltips`, the save/load helper) mostly for historical reasons. Treat `Hashtable` as "`Dictionary<object, object>` you shouldn't write new code with."

**Gotcha for Python people.** You can't `hashtable["missing"]` and get `None`. `Hashtable[key]` returns `null` for missing, but `Dictionary<K,V>[key]` throws `KeyNotFoundException`. Guard with `.ContainsKey(key)` or `.TryGetValue(key, out value)`.

---

## 7. Events & delegates (`Action`, `Func`, lambdas, `+=`/`-=`)

**What it is.**
- A **delegate** is a typed function pointer / first-class function type.
- An **event** is a delegate field with `+=` / `-=` restricted visibility, so outsiders can subscribe but not fire or clear it.
- `Action` / `Action<T>` / `Func<T1,TResult>` are built-in delegate types.

**Analogue.**
- TS: functions are first-class. Events in DOM: `el.addEventListener("click", fn)` — same mental model, different spelling.
- Python: callables + `observers.append(fn)` or similar.
- C++: `std::function<T(...)>`. Events ≈ the "observer" idiom.
- Go: function types + channels, or explicit callbacks. No `+=` syntax.

**In this codebase — WinForms handler signatures.**

```csharp
// ConfigRawParams.cs:454
private void Params_CellValueChanged(object sender, DataGridViewCellEventArgs e) { ... }
```

All WinForms handlers follow `(object sender, TEventArgs e)`. The Designer wires them up:

```csharp
// ConfigRawParams.Designer.cs   (paraphrased)
this.Params.CellValueChanged += new DataGridViewCellEventHandler(this.Params_CellValueChanged);
```

**Lambda + `Action` cast** — needed when invoking on another thread:

```csharp
// ConfigRawParams.cs:875-881
BeginInvoke((Action)delegate {
    CMB_paramfiles.DataSource = paramfiles.ToArray();
    CMB_paramfiles.DisplayMember = "name";
    CMB_paramfiles.Enabled = true;
    BUT_paramfileload.Enabled = true;
});
```

`BeginInvoke` takes a `Delegate`, but `delegate { … }` (an anonymous method) is ambiguous — hence the `(Action)` cast. Equivalent to `BeginInvoke(() => { ... })` with a cast.

**Event subscribe / unsubscribe in a coroutine-ish pattern:**

```csharp
// MAVLinkInterface.cs:1680-1772   (setParamAsync, abbreviated)
var sub1 = SubscribeToPacketType(MAVLINK_MSG_ID.PARAM_VALUE, buffer => {
    // ... process the echo
    complete = true;
    return true;
});

try {
    generatePacket(...);
    while (!complete && !timedOut) await readPacketAsync();
    return true;
}
finally {
    UnSubscribeToPacketType(sub1);
}
```

Register a callback, wait for the flag, always unsubscribe. The `try`/`finally` pattern for cleanup is universal in C# (same intent as Python's `with` or Go's `defer`).

---

## 8. `IDisposable` and `Dispose(bool disposing)`

**What it is.** A standard interface `IDisposable { void Dispose(); }` for deterministic cleanup of unmanaged resources. The `using` statement (`using (var x = new File()) { ... }`) auto-calls `Dispose` at scope end. The two-arg `Dispose(bool disposing)` is a template for classes that also hold unmanaged state.

**Analogue.**
- TS/JS: nothing built-in. `Symbol.dispose` / `using` landed in TS 5.2, similar intent.
- Python: `__enter__`/`__exit__` and `with`.
- C++: destructors; RAII makes it automatic. C# is less automatic since it's GC'd.
- Go: `defer close()`.

**In this codebase.**

```csharp
// ConfigRawParams.Designer.cs:10-23
private System.ComponentModel.IContainer components = null;

protected override void Dispose(bool disposing)
{
    if (disposing && (components != null))
    {
        components.Dispose();
    }
    base.Dispose(disposing);
}
```

`components` holds timers, tooltips, etc. (non-visual components the designer tracks). `disposing == true` means "called from user code" → OK to touch other managed objects. `disposing == false` means "called from the finalizer" → can only touch unmanaged resources. This template is a .NET convention, not a language rule.

```csharp
// ConfigRawParams.cs:129-146   (using for OpenFileDialog)
using (var ofd = new OpenFileDialog {
    AddExtension = true,
    DefaultExt = ".param",
    ...
})
{
    var dr = ofd.ShowDialog();
    ...
}
// ofd.Dispose() called automatically here
```

**Gotcha.** C# has two flavours of `using`:
- `using System;` — import statement.
- `using (resource) { ... }` — disposal scope.
Same keyword, very different roles. New readers trip on this.

---

## 9. `async`/`await`, `Task<T>`, `AwaitSync`

**What it is.** Same idea as TS/Python/Rust. `async` on a method makes it return `Task` / `Task<T>`. `await` suspends until the task completes. The CLR generates a state machine under the hood.

**Analogue.**
- TS: identical syntax. `async function foo(): Promise<T>`.
- Python: `async def` / `await`.
- C++: `std::future` / coroutines.
- Go: there's no `async` — it's CSP (goroutines + channels). C# is closer to TS here.

**In this codebase.**

```csharp
// MAVLinkInterface.cs:1635
public async Task<bool> setParamAsync(byte sysid, byte compid, string paramname, double value, bool force = false)
{
    ...
    while (true) {
        if (complete) return true;
        ...
        await readPacketAsync().ConfigureAwait(false);
    }
}
```

`.ConfigureAwait(false)` tells the runtime "don't resume on the captured synchronization context" (i.e. the UI thread). Used in library code to avoid forcing callers' threads. Skip it in UI code, use it in non-UI library code. MP code uses it sporadically.

### The `AwaitSync` helper (sync-over-async)

Mixing async and sync code in legacy codebases is painful. This repo papers over it with a helper:

```csharp
// ExtLibs/Utilities/Extensions.cs:110
// https://medium.com/rubrikkgroup/understanding-async-avoiding-deadlocks-e41f8f2c6f5d
public static T AwaitSync<T>(this Task<T> infunc)
{
    var tsk = Task.Run<T>(async () => await infunc);
    tsk.Wait();
    return tsk.Result;
}
```

Used like:

```csharp
// MAVLinkInterface.cs:1627
public bool setParam(byte sysid, byte compid, string paramname, double value, bool force = false)
{
    return setParamAsync(sysid, compid, paramname, value, force).AwaitSync();
}
```

**Translation: "I have an async API but my caller wants a synchronous return. Run the task on a pool thread, block this thread until done."**

**Gotcha.** This is the classic **sync-over-async anti-pattern**. In a pure modern codebase you'd make the caller async too. Here it exists because huge swaths of WinForms button handlers were sync before async was added. Don't reach for this in new code — make the call chain async end-to-end instead.

---

## 10. Parallel / threading primitives: `Parallel.ForEach`, `lock`, `ThreadPool`, `BeginInvoke`

**What it is.**
- `Parallel.ForEach(items, body)` — TPL's parallel loop. Runs `body(item)` concurrently on the thread pool, then waits.
- `lock (obj) { ... }` — syntactic sugar for `Monitor.Enter(obj) / Monitor.Exit(obj)`. Critical section.
- `ThreadPool.QueueUserWorkItem(cb)` — fire-and-forget work on the pool. Pre-dates `Task.Run`.
- `control.BeginInvoke(delegate)` — marshals a delegate back to the thread that owns the control (the UI thread for forms).

**Analogue.**
- TS/JS: worker threads, but mostly single-threaded event loop. The UI marshalling concept is new.
- Python: `threading.Thread`, `queue.Queue`, GIL caveats.
- C++: `std::thread`, `std::mutex`.
- Go: goroutines + `sync.Mutex`.

**In this codebase.**

```csharp
// ConfigRawParams.cs:584-651  (parallel row construction)
Parallel.ForEach(list, value =>
{
    var row = new DataGridViewRow() { Height = 36 };
    lock (rowlist)        // rowlist is shared across iterations
        rowlist.Add(row);
    row.CreateCells(Params);
    row.Cells[Command.Index].Value = value;
    ...
    // metadata lookups etc.
});
```

```csharp
// ConfigRawParams.cs:66
ThreadPool.QueueUserWorkItem(updatedefaultlist);
// ... work finishes ...

// ConfigRawParams.cs:875-881
BeginInvoke((Action)delegate {
    CMB_paramfiles.DataSource = paramfiles.ToArray();   // UI work — must be on UI thread
    ...
});
```

**The WinForms rule.** WinForms controls are NOT thread-safe. Any UI mutation must happen on the thread that created the control (the main thread). Background worker → `control.BeginInvoke(...)` (async marshal) or `control.Invoke(...)` (synchronous marshal). Violating this throws `InvalidOperationException: Cross-thread operation not valid`.

**Idiom for new async+UI code.** Prefer `async/await` + `ConfigureAwait(true)` (or omit — default is `true`) — the continuation naturally runs on the UI thread without explicit `BeginInvoke`.

---

## 11. LINQ (method-chain form) and LINQ-to-XML

**What it is.** LINQ = Language Integrated Query. Two flavours:
- **Query syntax**: `from x in xs where x > 3 select x * 2;`
- **Method syntax**: `xs.Where(x => x > 3).Select(x => x * 2);`

Everything is `IEnumerable<T>` under the hood — lazy iteration by default.

**Analogue.**
- TS: `arr.filter(…).map(…)`.
- Python: list comprehensions + `filter/map/reduce`.
- C++: Ranges (C++20) or boost.range.
- Go: no standard LINQ-like fluent API; you write loops.

**In this codebase — method syntax is dominant.**

```csharp
// ConfigRawParams.cs:260
var temp = _changes.Keys.Cast<string>().ToList();
// _changes is a Hashtable → .Keys is untyped IEnumerable → .Cast<string>() → .ToList()

// ConfigRawParams.cs:264
bool enable = temp.Any(a => a.EndsWith("_ENABLE"));
```

**LINQ-to-XML** — the parser for `apm.pdef.xml`:

```csharp
// conceptually, in ParameterMetaDataRepositoryAPMpdef.cs
using System.Xml.Linq;

XDocument doc = XDocument.Load(path);
var node = doc.Descendants("param")
              .FirstOrDefault(p => (string)p.Attribute("name") == paramName);
string desc = (string)node?.Element("Description");
```

**Gotcha.** `IEnumerable<T>` is **lazy** — the query isn't run until you iterate. `.ToList()` / `.ToArray()` forces evaluation. Forgetting this is a common source of "why is my filter re-running four times?" bugs.

**Gotcha for TS people.** LINQ methods return a new sequence, like `Array.prototype.map`. But `List<T>` also has **in-place** mutators (`Sort`, `RemoveAll`). Be sure which you're calling.

---

## 12. Extension methods

**What it is.** A static method in a static class whose first parameter is marked `this T x`. Compiles as a regular static call but looks like an instance method at the call site.

**Analogue.**
- TS/JS: prototype patching, or helper functions.
- Python: can't; monkey-patching is frowned upon.
- C++: free functions; can't be called with method syntax (well, deduction + ADL, but not the same).
- Go: methods on named types; a close analogue — but you can only add methods to types you declared.

**In this codebase.**

```csharp
// ExtLibs/Utilities/ListExtension.cs:8-10
public static class ListExtension
{
    public static void SortENABLE(this List<string> list) { ... }
}

// ExtLibs/Utilities/Extensions.cs:499
public static string RemoveFromEnd(this string s, string suffix) { ... }

// ExtLibs/Utilities/Extensions.cs:1089
public static string ToInvariantString(this object obj) { ... }

// ExtLibs/Utilities/Extensions.cs:110
public static T AwaitSync<T>(this Task<T> infunc) { ... }
```

Used like they're instance methods:

```csharp
// ConfigRawParams.cs:262
temp.SortENABLE();                        // on a List<string>

// MAVLinkInterface.cs:1627
setParamAsync(...).AwaitSync();           // on a Task<bool>
```

**Rule of thumb.** Extensions are great for adding "missing" convenience to types you don't own (BCL types, third-party libraries). Overusing them on your own types blurs where behaviour lives.

**Gotcha.** Extensions are not polymorphic — they're resolved at compile time by static type. `IEnumerable<T>.Count()` is always called by static type, even if the runtime object is a `List<T>` with a fast `Count` property. The BCL special-cases this, but it's a foot-gun in custom extensions.

---

## 13. Reflection idioms

**What it is.** Runtime access to type metadata: list types/methods/properties, create instances, invoke members by name.

**Analogue.**
- TS: `Reflect.*`, but limited (no type erasure bypass).
- Python: `getattr`, `type(x)`, `inspect`.
- C++: no portable reflection (pre-C++23); `typeid`.
- Go: `reflect.TypeOf`, `reflect.ValueOf`.

**In this codebase — the log4net logger idiom:**

```csharp
// ConfigRawParams.cs:29-30
private static readonly ILog log =
    LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
```

Verbatim recipe: "give me a logger named after the class I'm declared in, without me hard-coding the class name." `MethodBase.GetCurrentMethod()` returns info about the method being compiled (here, the static field initializer → belongs to the class's static constructor), and `.DeclaringType` is the `Type` of that class.

**Another reflection pattern** — dynamically matching a child control by name (cross-field wiring):

```csharp
// ConfigRawParams.cs:332-336
var textControls = Controls.Find(value, true);
if (textControls.Length > 0)
    ThemeManager.ApplyThemeTo(textControls[0]);
```

`Controls.Find(name, recurse)` walks the form's control tree looking for controls whose `Name` equals `value` (a parameter name like `RC1_MIN`). If the form happens to have a dedicated control for that param, it gets restyled. Think of it as `document.querySelector("#" + name)`.

---

## 14. Interop / marshalling: `Marshal.OffsetOf`, `ToStructure<T>`

**What it is.** Mapping raw `byte[]` data into a `struct` (and back) — the way C# talks to C APIs or binary wire protocols. Types used:
- `System.Runtime.InteropServices.Marshal` — low-level memory ops.
- `[StructLayout(LayoutKind.Sequential, Pack=1)]` — layout attributes on the struct.

**Analogue.**
- TS/JS: `DataView` / `Buffer` with manual offsets.
- Python: `struct.unpack` / `ctypes`.
- C++: memcpy into a `struct` with `#pragma pack`.
- Go: `encoding/binary` + struct tags, or `unsafe.Pointer`.

**In this codebase.**

```csharp
// MAVLinkInterface.cs:2059-2068
var offset = Marshal.OffsetOf(typeof(mavlink_param_value_t), "param_value");
newparamlist[paramID] = new MAVLinkParam(paramID,
    BitConverter.GetBytes(par.param_value),     // float → byte[]
    MAV_PARAM_TYPE.REAL32,
    (MAV_PARAM_TYPE) par.param_type);
```

And:

```csharp
// MAVLinkInterface.cs:1685 — the other direction
mavlink_param_value_t par = buffer.ToStructure<mavlink_param_value_t>();
```

`ToStructure<T>()` is an extension (in `ExtLibs/Mavlink/MavlinkUtil.cs` or similar) that reads the `byte[]` payload of a received packet and re-interprets it as a `struct T` with the right `[StructLayout]` attributes. This is how the generated `mavlink_*_t` structs round-trip over the wire.

**Gotcha.** The MAVLink generator emits sequential-layout structs with explicit sizes. Don't add fields or change types by hand — they must match the wire protocol exactly.

---

## 15. Static classes, static constructors, the "repository" idiom

**What it is.**
- `static class Foo` — can only contain static members; cannot be instantiated. Analogue of a namespace-scoped grouping of free functions.
- **Static constructor** `static Foo() { ... }` — runs once, on first access of the type. Used for lazy init.
- A static class with state = a **process-global**. Common for config, caches, service locators.

**Analogue.**
- TS: a module with exported top-level `let` + functions.
- Python: module-level constants + functions.
- C++: a namespace + file-scope globals.
- Go: package-level `var` and functions.

**In this codebase.**

```csharp
// ExtLibs/Utilities/ParameterMetaDataRepository.cs:13-19
public static class ParameterMetaDataRepository
{
    private static MemoryCache _cache =
        new MemoryCache(new MemoryCacheOptions() { /*SizeLimit = ...*/ });
    ...
    public static string GetParameterMetaData(string nodeKey, string metaKey, string vehicleType) { ... }
}

// ExtLibs/Utilities/ParameterMetaDataRepositoryAPMpdef.cs:38-41
static ParameterMetaDataRepositoryAPMpdef()
{
    GetMetaData();          // runs once, first time anything in this class is touched
}
```

**Idiom.** "Repository pattern" is a DDD term; here it's just a static class behind which all metadata lookup funnels. Combined with `MemoryCache`, it acts as both API and caching layer.

**Gotcha.** Static constructors run *lazily*. If the constructor throws, every subsequent access to the class throws `TypeInitializationException` — and the inner exception is what you actually care about.

---

## 16. `MemoryCache` + sliding expiration

**What it is.** `Microsoft.Extensions.Caching.Memory.MemoryCache` — a thread-safe in-process key/value store with TTLs.

**Analogue.**
- TS: `lru-cache` from npm.
- Python: `functools.lru_cache` or `cachetools`.
- C++: nothing standard; roll your own.
- Go: nothing standard; `groupcache` etc.

**In this codebase.**

```csharp
// ParameterMetaDataRepository.cs:14-64
private static MemoryCache _cache = new MemoryCache(new MemoryCacheOptions() { ... });

public static string GetParameterMetaData(string nodeKey, string metaKey, string vehicleType)
{
    lock (_cache) {
        var ans = _cache.Get(nodeKey + metaKey + vehicleType) as string;
        if (ans != null) return ans;
    }

    var answer = /* ... do the lookup ... */;

    lock (_cache) {
        try {
            var ci = _cache.CreateEntry(nodeKey + metaKey + vehicleType);
            ci.Value = answer;
            ci.Size = ((string)ci.Value).Length;
            ci.SlidingExpiration = TimeSpan.FromMinutes(5);
            ci.Dispose();   // the dispose is what actually commits the entry!
        } catch { }
    }
    return answer;
}
```

**Two gotchas worth pointing out.**

1. **`ci.Dispose()` commits the entry.** `MemoryCacheEntry` is a builder; disposing it is the signal "I'm done configuring, put me in the cache." If you forget, nothing is cached. Counter-intuitive if you're used to `.Save()` / `.Commit()`.
2. **The `lock (_cache)` is defensive.** `MemoryCache` itself is thread-safe, but the *compound* read-then-write pattern here is not. A naked `Get` + `CreateEntry` would let two threads duplicate work. The lock is for the *logical* read-through-cache operation, not for the cache's internal data structures.

**Sliding vs absolute expiration.** Sliding resets the countdown on every access; absolute is a fixed deadline. Sliding is used here so "in-use" metadata stays warm.

---

## 17. WinForms essentials you'll meet here

### 17.1 `.resx` and `ComponentResourceManager`

`.resx` files are XML (one per locale). At build time they become satellite assemblies (`en/MissionPlanner.resources.dll`, `ko-KR/MissionPlanner.resources.dll`, ...).

```csharp
// ConfigRawParams.Designer.cs:34
System.ComponentModel.ComponentResourceManager resources =
    new System.ComponentModel.ComponentResourceManager(typeof(ConfigRawParams));
...
resources.ApplyResources(this.BUT_compare, "BUT_compare");   // sets text, size, image, etc.
```

At runtime, the current thread's `CurrentUICulture` chooses which satellite to load. Fall-back order: specific (`zh-Hant`) → neutral (`zh`) → invariant.

**Gotcha.** `Text`, `Location`, `Size`, `Image` for every control are stored in the `.resx`, not in the Designer code. When you translate a form, you edit the `.resx`. Don't hand-edit the invariant `.resx` — use the Designer.

### 17.2 `DataGridView` column/row model

- Columns are declared in the Designer, exposed as fields (`Command`, `Value`, `Units`, ...).
- Each column has an `.Index` — typed `int`.
- Rows are `DataGridViewRow`; cells reached by `row.Cells[columnIndex]` or `dataGrid[columnIndex, rowIndex]`.

```csharp
// ConfigRawParams.cs:586-596
var row = new DataGridViewRow() { Height = 36 };
row.CreateCells(Params);                                   // initialize all cells
row.Cells[Command.Index].Value = value;                    // set param name
row.Cells[Value.Index].Value = MainV2.comPort.MAV.param[value].ToString();
```

**Tri-state indexing.** `dataGrid[colIndex, rowIndex]` is column-first, row-second. Hand-rolling `grid[x, y]` where x/y feel like coordinates is the opposite convention most people expect. Prefer the explicit `.Rows[r].Cells[c]` form for readability.

### 17.3 Custom control subclasses for theming

Mission Planner uses `MyButton : Button`, `MyDataGridView : DataGridView`, `MyUserControl : UserControl`. Each adds theme / DPI / localization hooks.

```csharp
// ExtLibs/Controls/MyUserControl.cs:12
public class MyUserControl : System.Windows.Forms.UserControl
{
    public event FormClosingEventHandler FormClosing;
    ...
}
```

This is why every Config view derives from `MyUserControl`, not `UserControl` directly.

---

## 18. `Settings.Instance[...]` — singleton + indexer

**What it is.** A per-user config store, accessed through a **singleton** (`Settings.Instance`) with a **string indexer** for raw access and typed helpers for parsed access.

**Analogue.**
- TS: a config module with typed getters.
- Python: `configparser` + a global.
- C++/Go: roll your own.

**In this codebase.**

```csharp
// ExtLibs/Utilities/Settings.cs (signatures)
public static Settings Instance { get; }
public string this[string key] { get; set; }
public string this[string key, string defaultvalue] { get; set; }
public int    GetInt32(string key, int defaulti = 0);
public bool   GetBoolean(string key, bool defaultb = false);
public List<string> GetList(string key);
```

Usage in the Full Parameter List view:

```csharp
// ConfigRawParams.cs:80-85
col.Width = (int)Math.Max(5, Settings.Instance.GetInt32("rawparam_" + col.Name + "_width"));
...
splitContainer1.SplitterDistance = Settings.Instance.GetInt32("rawparam_splitterdistance", 180);
splitContainer1.Panel1Collapsed  = Settings.Instance.GetBoolean("rawparam_panel1collapsed", false);

// ConfigRawParams.cs:595
var fav_params = Settings.Instance.GetList("fav_params");

// ConfigRawParams.cs:109
Settings.Instance["rawparam_" + col.Name + "_width"] = col.Width.ToString("0", CultureInfo.InvariantCulture);
```

**Idioms to notice.**

- Typed convenience methods (`GetInt32`, `GetBoolean`, `GetList`) on top of a string-string store.
- Writes go through the indexer's setter, so persistence (XML on disk) happens transparently.
- Keys are stringly-typed (`"rawparam_" + col.Name + "_width"`). No type safety; typos silently reset values to defaults. Trade-off for flexibility.

---

## 19. `try`/`catch` idioms — including the many empty catches

**What it is.** Same as Java/TS/Python. `catch (Exception)` catches everything; `throw;` rethrows preserving the stack. You can catch-and-log, catch-and-swallow, catch-and-transform.

**In this codebase — three patterns you'll see repeatedly.**

**(a) Log-and-continue** (most common):

```csharp
// ConfigRawParams.cs:646-650
try {
    var metaDataDescription = ParameterMetaDataRepository.GetParameterMetaData(...);
    ...
}
catch (Exception ex) {
    log.Error(ex);
}
```

**(b) Empty catch (swallow everything)** — in hot paths or around "best-effort" updates:

```csharp
// ConfigRawParams.cs:338-340
try {
    var textControls = Controls.Find(value, true);
    if (textControls.Length > 0)
        ThemeManager.ApplyThemeTo(textControls[0]);
}
catch { }
```

```csharp
// ConfigRawParams.cs:342-357
try {
    // update matching row in the grid
}
catch { }
```

**(c) Retry-with-throw-on-exhaustion:**

```csharp
// MAVLinkInterface.cs:1744-1763
int retrys = 3;
while (true) {
    if (complete) return true;
    if (!(start.AddMilliseconds(700) > DateTime.Now)) {
        if (retrys > 0) { generatePacket(...); retrys--; continue; }
        throw new TimeoutException("Timeout on read - setParam " + paramname);
    }
    await readPacketAsync().ConfigureAwait(false);
}
```

**Comment on (b).** Empty catches are usually a smell. Here the rationale is usually "this is UI glue and a failure shouldn't crash the whole dialog." The price is that real bugs can be masked. When debugging a puzzling no-op, put breakpoints inside the empty `catch` to see what you're losing.

---

## 20. Type conversions & formatting gotchas

**What it is.** C# is static but very happy to convert between numeric types; parsing to/from strings is **culture-aware by default** (a landmine).

### 20.1 `double.Parse`, `int.Parse`

```csharp
// ConfigRawParams.cs:242
var value = double.Parse(row.Cells[Value.Index].Value.ToString());
```

**Default culture.** `double.Parse("3.14")` works in `en-US` but **fails** in `de-DE` (where `,` is the decimal separator). For file formats, wire formats, or anything non-UI, you should almost always pass `CultureInfo.InvariantCulture`:

```csharp
double.Parse(text, CultureInfo.InvariantCulture);
```

The MP codebase is inconsistent here. Some code passes it, some doesn't. A historical source of bug reports from non-English locales.

### 20.2 Writing culture-safe output

```csharp
// ConfigRawParams.cs:109
Settings.Instance["rawparam_" + col.Name + "_width"] =
    col.Width.ToString("0", CultureInfo.InvariantCulture);
```

**Idiom:** `value.ToString(format, CultureInfo.InvariantCulture)` for any stringification that round-trips (persistence, wire).

### 20.3 Casts vs `as`

- `(Foo)x` — explicit cast; throws `InvalidCastException` if the cast is wrong.
- `x as Foo` — returns `null` if the cast fails; only works for reference/nullable types.
- `is Foo f` — pattern match; works in any type; assigns to `f` if matched.

```csharp
// ParameterMetaDataRepository.cs:31
var ans = _cache.Get(nodeKey + metaKey + vehicleType) as string;
if (ans != null) return ans;
```

`Get` returns `object`. `as string` gives us `null` if the cached value wasn't a string, so the `if` covers both miss and wrong-type.

### 20.4 Implicit numeric conversions

```csharp
// MAVLinkInterface.cs:1622
return setParam((byte) sysidcurrent, (byte) compidcurrent, paramname, value, force);
```

C# won't implicitly narrow `int` → `byte`, hence the explicit `(byte)`. Unlike C/C++ where it silently narrows with a warning (or not), C# forces your hand here. Similarly `double` → `int` requires a cast.

---

## 21. Putting it all together — a tour of `processToScreen`

To concretise these concepts, here's the method annotated with the C# techniques it uses:

```csharp
// GCSViews/ConfigurationView/ConfigRawParams.cs:563
internal void processToScreen()                          // internal = assembly-visible
{
    toolTip1.RemoveAll();
    Params.Rows.Clear();
    log.Info("processToScreen");                         // (§13) log4net; logger from DeclaringType

    var list = new List<string>();                       // (§6) generic List<T>

    if (startup)
    {
        foreach (string item in MainV2.comPort.MAV.param.Keys)
            list.Add(item);                              // MAV.param is the MAVLinkParamList (§6 custom collection)

        rowlist.Clear();

        bool has_defaults = false;

        Parallel.ForEach(list, value =>                  // (§10) parallel + (§7) lambda
        {
            if (value == null || value == "")
                return;

            var row = new DataGridViewRow() { Height = 36 };  // (§17.2) WinForms object initializer
            lock (rowlist)                               // (§10) critical section on shared list
                rowlist.Add(row);
            row.CreateCells(Params);
            row.Cells[Command.Index].Value = value;
            row.Cells[Value.Index].Value = MainV2.comPort.MAV.param[value].ToString();
            var fav_params = Settings.Instance.GetList("fav_params");  // (§18) settings singleton
            row.Cells[Fav.Index].Value = fav_params.Contains(value);

            if (MainV2.comPort.MAV.param[value].default_value.HasValue) {  // (§5) Nullable<T>
                has_defaults = true;
                row.Cells[Default_value.Index].Value =
                    MainV2.comPort.MAV.param[value].default_value_to_string();
            } else {
                row.Cells[Default_value.Index].Value = "NaN";
            }

            try                                          // (§19) try/catch — log and continue
            {
                var desc = ParameterMetaDataRepository.GetParameterMetaData(  // (§15) static class
                    value, ParameterMetaDataConstants.Description,
                    MainV2.comPort.MAV.cs.firmware.ToString());
                if (!string.IsNullOrEmpty(desc))
                {
                    row.Cells[Command.Index].ToolTipText = AddNewLinesForTooltip(desc);
                    row.Cells[Value.Index].ToolTipText   = AddNewLinesForTooltip(desc);
                    // ... more lookups for Range/Values/Units
                    row.Cells[Desc.Index].Value = desc;
                }
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        });

        Default_value.Visible   = has_defaults;
        chk_none_default.Visible = has_defaults;
    }

    // ...
    Params.Rows.AddRange(rowlist.ToArray());
    Params.SortCompare += OnParamsOnSortCompare;         // (§7) event subscription
    Params.Sort(Params.Columns[Command.Index], ListSortDirection.Ascending);

    if (splitContainer1.Panel1Collapsed == false)
        BuildTree();
}
```

Every `§n` tag above cross-references a section in this doc. If you're reading this and want to understand just that method, those sections are the minimum set of C# ideas you need.

---

## 22. Further reading, in order of usefulness for this codebase

- **WinForms developer guide** — <https://learn.microsoft.com/en-us/dotnet/desktop/winforms/>
- **C# language reference** — <https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/>
- **`Task` / async deep dive** — <https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/>
- **LINQ** — <https://learn.microsoft.com/en-us/dotnet/csharp/linq/>
- **`IDisposable` guidelines** — <https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/implementing-dispose>
- **Marshalling structs** — <https://learn.microsoft.com/en-us/dotnet/standard/native-interop/type-marshalling>
- **log4net** — <https://logging.apache.org/log4net/>
- **`MemoryCache` overview** — <https://learn.microsoft.com/en-us/dotnet/core/extensions/caching>

For the "why MP does it this way": search for the specific symbol in the repo and read the handful of call sites. Most unusual idioms (AwaitSync, SortENABLE, empty catches) have concrete rationale in nearby code or comments.

---

## 23. Cheat-sheet: cross-language translation table

| Concept | C# | TS | Python | C/C++ | Go |
|---|---|---|---|---|---|
| Interface | `interface I` + `class C : I` | `interface I` + `implements` | Protocol / duck | pure-virtual abstract | structural |
| Generics | `List<T>` | `Array<T>` | `list[T]` (3.9+) | templates | generics (1.18+) |
| Nullable value | `int?` | `number \| undefined` | `Optional[int]` | `std::optional<int>` | pointer |
| Lambda | `x => x + 1` | `x => x + 1` | `lambda x: x+1` | `[](int x){ return x+1; }` | `func(x int) int { return x+1 }` |
| Async/await | `async Task<T>` + `await` | same | same | `std::future` / coroutines | goroutines + channels |
| Resource cleanup | `using` / `IDisposable` | `using` (TS 5.2+) | `with` | RAII (dtor) | `defer` |
| Property | `public T X { get; set; }` | getter/setter | `@property` | getter+setter methods | explicit methods |
| Extension method | `static void Foo(this T x)` | prototype patching | n/a | free function | method on named type |
| Event | `event EventHandler E; E += h;` | `addEventListener` | observer pattern | signals/slots / std::function | channels / callbacks |
| Indexer | `T this[K key] { get; set; }` | bracket access via proxy | `__getitem__`/`__setitem__` | `operator[]` | n/a (funcs) |
| Static class | `static class X` | module + top-level fns | module | namespace | package |
| Runtime reflection | `Type`, `MethodBase`, `PropertyInfo` | `Reflect.*` | `inspect` | RTTI only | `reflect` |
| Cast with null | `x as T` | `x as T` (unchecked) | `isinstance` | `dynamic_cast<T*>` | type assertion `v.(T)` |
| Pattern match | `if (x is T t)` | `typeof` / discriminated union | `match` (3.10+) | n/a | type switch |

---

## 24. If you remember only five things

1. **`partial class` + a `.Designer.cs`** means the class is split across two files: the designer owns the layout, you own the logic.
2. **WinForms is single-threaded.** Background work goes to `ThreadPool` / `Task.Run` / `Parallel.ForEach`; UI mutation must come back via `BeginInvoke` or `async/await` on the UI thread.
3. **Properties, not fields.** Frameworks bind to properties; the MP Designer, settings, data binding, XML serialization all ignore public fields.
4. **`using (x) { ... }` vs `using Foo;`** — the first is disposal scope, the second is import. Same keyword, unrelated.
5. **Everything that talks to the outside world should use `CultureInfo.InvariantCulture`.** `double.Parse("3.14")` fails for users in half of Europe unless you say so.
