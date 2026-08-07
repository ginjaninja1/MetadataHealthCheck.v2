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

## Referencing a private host DLL only makes its types visible in that one project

A `<Reference HintPath="...">` to a DLL is not transitive through a
`ProjectReference` the way a NuGet `PackageReference`'s assets are. If two
separate executable projects (e.g. two different console harnesses) both need
to directly name a type from the same private host DLL in their own source
code, both need their own `<Reference>` entry to it - getting the DLL
copied to one project's output via the other's `ProjectReference` does not
make its types nameable in the second project's own compilation.