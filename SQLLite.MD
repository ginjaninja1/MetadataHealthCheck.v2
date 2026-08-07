# Emby Plugin Development - Learnings

Generic, reusable lessons about developing Emby Server plugins, independent of
any specific project. Add to this file whenever a debugging session surfaces
something that would save real time on a future Emby plugin.

---

## SQLite in Emby plugins: there is no public-NuGet path

Emby Server does not use the public `SQLitePCL.pretty`/`SQLitePCLRaw` NuGet
packages at all, at any version. It ships its own private, renamed fork:

- `SQLitePCL.pretty.dll` - a private build of the open-source SQLitePCL.pretty
  wrapper API (same public type/method surface as the NuGet package of a
  similar name, so application code written against the public package's API
  shape still compiles unchanged against this one).
- `SQLitePCLRawEx.core.dll` - the actual divergent piece. Same *role* as the
  public `SQLitePCLRaw.core` package (defines `ISQLite3Provider` and the
  static `raw` dispatcher class), but in its own `SQLitePCLEx` namespace, with
  its own independent API surface and its own independent version history.
  It is NOT a drop-in-compatible fork at the binary level: its named-parameter
  bind implementation and other details differ from the public package's
  internals, apparently by design or accident of independent evolution, not by
  a documented version constraint anywhere.
- The actual class implementing `ISQLite3Provider` (e.g. `SQLite3Provider_sqlite3`)
  is not in either of the above two DLLs - it lives inside the host
  application's own main assembly (for Emby specifically: `EmbyServer.dll`,
  not `EmbyServer.exe` - the `.exe` is a native launcher stub with no managed
  metadata, the actual managed app is the neighboring `.dll`).
- The host process registers the provider itself, once, at startup
  (`raw.SetProvider(new SQLite3Provider_sqlite3())`), before any plugin code
  runs. A real plugin never needs to call this itself.

**Practical consequence:** a plugin's own SQLite code should reference these
private DLLs (via `HintPath` to the real Emby Server install's `system`
folder) rather than any public NuGet package, even though a public package
with a very similar-sounding name and API surface exists and *appears* to
work at compile time. Compiling successfully against the public package is
not evidence it is compatible - the failure mode is a runtime
`MissingMethodException` deep inside a specific call path (e.g. named
parameter binds, custom collations), not a build error, so it can pass a
quick smoke test and still be wrong.

**How this was actually confirmed** (worth repeating as a method, not just a
conclusion): a version-pin guessing approach produced two failures that
directly contradicted each other - one public version was missing an
overload the wrapper needed, an older public version was missing a
*different* overload the same wrapper needed. Two failures on opposite sides
of a version range, both against the same package, is strong evidence no
version of that package is actually compatible - worth recognizing as a
stop-guessing signal rather than continuing to bisect a version range that
cannot contain a working point.

## Standalone test harnesses for Emby plugins need to do the host's job themselves

Any functionality that a plugin normally gets "for free" from the running
Emby host (SQLite provider registration is one example; there may be others -
worth checking for wherever a plugin's dependency appears to work with zero
setup code of its own) needs an explicit, one-time setup call in a standalone
console harness, because there is no host process performing it. This is easy
to miss precisely because the real plugin code never needs to think about it
- the absence of setup code in a working reference plugin does not mean no
setup is required, it means the setup already happened elsewhere before the
plugin's own code runs.

## SQLite in Emby plugins: `Clone()`-based connection sharing needs whole-method locking, not just around schema changes

The Emby.AutoOrganize reference `BaseSqliteRepository` pattern opens one real
native connection once, then hands out `Clone()`d wrappers to callers on every
subsequent request. **`Clone()` does not open an independent native
connection** - confirmed by decompiling the actual `SQLiteDatabaseConnection.Clone`
method: it constructs a new managed wrapper object that stores a reference to
the *same* underlying native `sqlite3` handle the original connection already
held. Every "connection" handed out this way, across every thread, is really
the same one native handle wearing different managed costumes.

Combined with `ConnectionFlags.NoMutex` (used by the reference pattern
specifically to opt OUT of SQLite's own internal thread-safety, on the
assumption the application will serialize access itself), this means genuinely
concurrent calls into that shared native handle from multiple threads - even
two ordinary, unrelated `SELECT`s, not just schema changes - can produce a
native `AccessViolationException` (real memory corruption, not a catchable
managed exception or a benign `SQLITE_BUSY`).

The reference plugin's own repository never hits this because **every single
one of its methods wraps its entire body** - connection creation through
statement execution - in `using (WriteLock.Write())` (or `.Read()`, which,
notably, calls the exact same `EnterWriteLock()` under the hood in the
reference's own extension methods; there is no real concurrent-reader path at
all). An initial, narrower fix that only serialized schema-creation DDL
(reasoning: "DDL is the operation most sensitive to concurrency") turned out
to be insufficient - the crash reappeared on an ordinary `SELECT` once the DDL
race was closed, because the underlying shared-handle problem is general, not
specific to schema changes.

**Practical rule:** when borrowing this exact `Clone()`-based repository
pattern, replicate the *whole-method* `WriteLock.Read()`/`.Write()` wrapping
on every single data-access method from day one, not incrementally added
after each failure mode surfaces. Partial adoption of a proven-safe pattern
(taking the connection-sharing mechanism but not the locking discipline that
makes it safe) will pass a low-concurrency smoke test and then fail
unpredictably under real parallel load - exactly the shape of bug that's easy
to under-diagnose as "add a narrower lock here" when the actual fix is "lock
the whole operation, matching what the trusted reference actually does
everywhere, not just where the first crash happened to occur."

## Referencing a private host DLL only makes its types visible in that one project

A `<Reference HintPath="...">` to a DLL is not transitive through a
`ProjectReference` the way a NuGet `PackageReference`'s assets are. If two
separate executable projects (e.g. two different console harnesses) both need
to directly name a type from the same private host DLL in their own source
code, both need their own `<Reference>` entry to it - getting the DLL
copied to one project's output via the other's `ProjectReference` does not
make its types nameable in the second project's own compilation.