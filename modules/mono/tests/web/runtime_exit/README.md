# Real CoreCLR runtime exit probe

Requires Windows PowerShell, Node.js, a .NET 11 SDK with its browser-wasm
workload, installed Chrome, and a cached `@playwright/cli` npm package. No npm
dependencies are added or downloaded by the scripts.

From this directory:

```powershell
./prepare.ps1
./run-browser.ps1 -Port 8132
```

Preparation uses `dotnet build -c Release -r browser-wasm
-p:WasmBuildNative=false -o .artifacts/www/_framework`, not `publish`, to avoid
the installed SDK's missing `_WasmBuildAppCore` target. It uses the prebuilt
runtime without native relinking or SDK modifications. The project compiles
only `Program.cs`; intermediates, output, logs, and Playwright artifacts stay
under `.artifacts/`.

The production `modules/mono/web/mono_bridge.js` is copied unchanged and
hash-checked. Configuration lists the built assemblies and native/runtime
assets, preserves the built runtimeconfig, and resolves resource URLs against
the browser location, so the port is configurable.

The runner reuses `../smoke/serve.mjs`, starts its own hidden server, and closes
only its uniquely named browser session and server in `finally`. An occupied
port fails without touching the other server. `-TimeoutSeconds` bounds each
navigation check (default 60).

Each run observes a one-second settle window after the callback unwinds:

- `?bridge=false`: managed ready/cleanup markers, no native `onExit`.
- `?bridge=true`, then reload: exactly one real `onExit(0)` per navigation,
  after `native-callback-left`, with no recorded errors.

Results are saved as `baseline.json`, `bridge.json`, and `reload.json` under
`.artifacts/`. The harness uses real SDK keepalive push/pop calls to represent
an outstanding native callback. Only zero-code graceful shutdown is covered;
nonzero upstream abort behavior is not a supported `onExit` contract. This is
a real loader/C# shutdown probe, not a full Godot scene or native-engine test.
