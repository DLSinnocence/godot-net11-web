# CoreCLR Web regression checks

Run from the repository root. The managed editor tests use .NET 8; the Wasm
checks require the .NET 11 SDK and its CoreCLR WebAssembly workload.

```sh
dotnet test modules/mono/editor/Godot.NET.Sdk/Godot.SourceGenerators.Tests/Godot.SourceGenerators.Tests.csproj
node --test modules/mono/tests/web/test_loader_audio.js
python modules/mono/tests/web/test_web_targets.py
dotnet run --project modules/mono/tests/web/test_shutdown.csproj -- modules/mono/glue/GodotSharp/GodotSharp/SourceFiles/LibGodotMain.cs
dotnet build modules/mono/tests/web/test_shutdown.csproj -r browser-wasm --self-contained
```

The managed tests check aggregate-return lowering, scalar return widths,
trampoline signatures, and ABI-version rejection. The JavaScript tests exercise
loader defaults, deferred public runtime exit, and delayed audio initialization
during shutdown and restart. The CoreCLR Web CI job runs these JavaScript checks.
The shutdown project compiles the production browser entry point with test
stubs and checks its shutdown contract; it is not a browser execution test.

`runtime_exit/` is a separate browser probe using the unmodified, prebuilt .NET
runtime and the production Godot loader. It compares native `Environment.Exit(0)`
alone with the deferred loader exit, then checks page reload. It avoids native
Godot relinking and does not replace the full game smoke test below.

After building GodotSharp, run the installed SDK's real managed-to-native
generator. Set `WasmSdkDir` to the versioned `Microsoft.NET.Runtime.WebAssembly.Sdk`
pack directory and `WasmRuntimeDir` to `runtimes/browser-wasm` within the matching
`Microsoft.NETCore.App.Runtime.browser-wasm` pack:

```sh
dotnet msbuild modules/mono/tests/web/verify_interop.proj -p:WasmSdkDir="<sdk-pack-dir>" -p:WasmRuntimeDir="<runtime-pack-dir>/runtimes/browser-wasm"
```

`GodotSharpAssembly` optionally selects a different GodotSharp DLL. The default
is its local Debug build. The task must complete without WASM0067 and generate
the Color, packed-array, and int32 stack-info signatures. An untrimmed scan may
warn about unused macOS and Windows P/Invokes (WASM0066); these are not browser
callback signatures. Generated output stays under the ignored `obj` directory.

Rebuild the engine, Web native library, GodotSharp packages, and GodotTools as a
matched set. Compile-only and mocked tests do not substitute for exporting and
loading a real browser game, reloading it, and checking shutdown diagnostics.

The `smoke/` fixture covers these browser scenarios. Its README records the
current native-publish blockers separately from the passing managed build and
call-helper generation checks. It must not be counted as a passing browser test
until native publish and both browser runs complete.
