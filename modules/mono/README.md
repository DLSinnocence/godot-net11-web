# How to build and run

The editor and desktop projects target .NET 8. Android, iOS, and Web export
projects target .NET 11 and use the experimental CoreCLR runtime packs (without
NativeAOT). Install the .NET 8 SDK for editor development; the .NET 11 preview
SDK is required when building mobile or Web export assemblies.

To build the Web native library used by a CoreCLR browser-wasm project, enable
the module and select a static library build:

```sh
scons platform=web target=template_release module_mono_enabled=yes library_type=static_library disable_crash_handler=yes
```

1. Build Godot with the module enabled: `module_mono_enabled=yes`.
2. After building Godot, use it to generate the C# glue code:
   ```sh
   <godot_binary> --generate-mono-glue ./modules/mono/glue
   ```
3. Build the C# solutions:
   ```sh
   ./modules/mono/build_scripts/build_assemblies.py --godot-output-dir ./bin
   ```

The paths specified in these examples assume the command is being run from
the Godot source root.

## Native interop ABI

The fork uses runtime interop ABI version 1. Struct-return callbacks use an
explicit trailing output pointer on the native boundary; the generated C#
methods retain their existing return types. This avoids platform-specific
aggregate-return differences between native opaque storage and managed fields.

Rebuild the engine, export templates, and GodotSharp packages together after
updating the interop ABI. Do not mix older engine binaries or templates with the
new managed assemblies. Initialization checks the callback table size and ABI
version and rejects mismatched builds before invoking runtime callbacks. The
version query occupies a fixed first slot with an invariant signature.

The callback generator also emits attributed delegate signatures for the .NET
Wasm trampoline generator. These describe the lowered native ABI, including
32-bit native error codes; the public `Godot.Error` enum remains unchanged.
The browser targets keep GodotSharp rooted so these signatures survive trimming.

Web targets normalize exception handling with `translate-to-exnref` at the final
emcc link. Build the native library with a compatible Emscripten toolchain, such
as the one installed by the .NET WebAssembly workload used for that final link.
Regression checks are documented in `tests/web/README.md`.

## Web runtime shutdown

After Godot finishes asynchronous cleanup and file synchronization, the managed
exit callback disposes the engine and requests a deferred loader exit through the
reserved `godot:runtime` JavaScript import. It then calls `Environment.Exit` to
enter CoreCLR shutdown. On the next JavaScript timer turn, the owning runtime's
public `exit` API releases its loader keepalive and completes normal zero-code
shutdown through Emscripten and `onExit`. Godot does not reset keepalive counters
or invoke `onExit` itself. Update the Web loader and GodotSharp source package
together; both sides of this shutdown bridge are required.

The fixed .NET 11 Preview 7 runtime takes an abort path for nonzero exit codes.
The bridge preserves those codes, but does not turn aborts into successful
`onExit` notifications. The `tests/web/runtime_exit` probe checks normal
zero-code shutdown against the real runtime, separately from a full game export.

# How to deal with NuGet packages

We distribute the API assemblies, our source generators, and our custom
MSBuild project SDK as NuGet packages. This is all transparent to the user,
but it can make things complicated during development.

In order to use Godot with a development of those packages, we must create
a local NuGet source where MSBuild can find them. This can be done with
the .NET CLI:

```sh
dotnet nuget add source ~/MyLocalNugetSource --name MyLocalNugetSource
```

The Godot NuGet packages must be added to that local source. Additionally,
we must make sure there are no other versions of the package in the NuGet
cache, as MSBuild may pick one of those instead.

In order to simplify this process, the `build_assemblies.py` script provides
the following `--push-nupkgs-local` option:

```sh
./modules/mono/build_scripts/build_assemblies.py --godot-output-dir ./bin \
    --push-nupkgs-local ~/MyLocalNugetSource
```

This option ensures the packages will be added to the specified local NuGet
source and that conflicting versions of the package are removed from the
NuGet cache. It's recommended to always use this option when building the
C# solutions during development to avoid mistakes.

# Double Precision Support (REAL_T_IS_DOUBLE)

Follow the above instructions but build Godot with the precision=double argument to scons

When building the NuGet packages, specify `--precision=double` - for example:
```sh
./modules/mono/build_scripts/build_assemblies.py --godot-output-dir ./bin \
    --push-nupkgs-local ~/MyLocalNugetSource --precision=double
```
