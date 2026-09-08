# Godot CoreCLR browser-wasm smoke fixture

This fixture isolates the GodotSharp browser path from global NuGet packages and installed Godot SDK packages. It imports the top-level `Microsoft.NET.Sdk.WebAssembly` SDK plus the repository's local `Browser.props` and local `Sdk.targets` (which imports local `Browser.targets`), references the freshly built local Debug `GodotSharp.dll` and `Godot.SourceGenerators.dll`, and compiles every shipped `GodotSharp/SourceFiles/*.cs` file into the app. This is a local-import fixture, not a full packaged `Godot.NET.Sdk` export test.

The smoke scene covers `Color.FromOkHsl`, `Variant` colors and vectors, packed color/vector arrays, exception stack reporting through the source-generated Godot method dispatcher via `Call(MethodName.ThrowExpected)` (no `[Callable]` attribute), immediate quit while browser audio starts, and browser load followed by reload.

All restore, build, publish, generated-source, pack, and browser outputs stay under ignored `.artifacts/` or `.godot/` directories.

## Managed preparation

This pins .NET SDK `11.0.100-preview.7.26381.103`, restores into `.artifacts/nuget/packages`, and forces `WasmBuildNative=false` so no emcc link starts:

```powershell
./prepare-managed.ps1
```

## Native publish

Run only after the local Web static-template build is complete. The switch is an acknowledgement gate:

```powershell
./publish-native.ps1 -TemplateBuildCompleted
```

The publish uses `bin/.web_zip/libgodot/libgodot.a` plus `js_library`, `pre_js`, and `post_js` through local `Browser.targets`. That target supplies the final `-s BINARYEN_EXTRA_PASSES=translate-to-exnref` emcc flag. The script assembles the actual Godot engine loader, CoreCLR `_framework`, relinked `smoke.wasm`, audio worklets, and minimal project pack in `.artifacts/www`.

## Browser smoke

```powershell
./run-browser-smoke.ps1
```

The runner uses the existing Playwright CLI package in offline mode via `npx.cmd`, polls the harness status with `eval`, checks a full load and reload, and attempts to close its Playwright session and stop its local Node server even on failure. The harness waits one second after `onExit` before checking success so queued shutdown errors can surface. Page errors and unhandled promise rejections make failure sticky, including errors received after that check; the finite wait is not a guarantee against arbitrarily late errors.

## Validation status (September 8, 2026)

Normal native publish is blocked by the missing `_WasmBuildAppCore` target referenced by the installed Preview 7 `WasmNestedPublishAppDependsOn`. A diagnostic CLI override, `-p:WasmNestedPublishAppDependsOn=`, gets past that blocker and reaches generated bridge C++ compilation, but the installed `BrowserWasmApp.CoreCLR.targets` then requires the nonexistent `Sdk/coreclr_compat.h`. `WASM0067` no longer occurs in that diagnostic path; no final Wasm or browser load/reload run has been validated. The override is diagnostic only, not a supported publish fix; do not add fake targets or headers to bypass these blockers.
