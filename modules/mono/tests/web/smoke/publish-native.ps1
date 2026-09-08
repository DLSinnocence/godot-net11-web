[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [switch] $TemplateBuildCompleted
)

$ErrorActionPreference = 'Stop'
if (!$TemplateBuildCompleted) {
    throw 'Native publish is gated until the local web static template build is confirmed complete.'
}

$fixture = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $fixture '../../../../..')).Path
$project = Join-Path $fixture 'GodotWebSmoke.csproj'
$artifacts = Join-Path $fixture '.artifacts'
$publish = Join-Path $artifacts 'publish/ExportDebug/AppBundle'
$www = Join-Path $artifacts 'www'
$libGodot = Join-Path $repo 'bin/.web_zip/libgodot/libgodot.a'
$engine = Join-Path $repo 'bin/godot.web.template_release.wasm32.nothreads.mono.wrapped.js'
$godot = Join-Path $repo 'bin/godot.windows.editor.x86_64.mono.console.exe'

foreach ($required in @($libGodot, $engine, $godot)) {
    if (!(Test-Path -LiteralPath $required)) { throw "Missing required local build output: $required" }
}

& dotnet publish $project --configuration ExportDebug --runtime browser-wasm --no-restore -p:WasmBuildNative=true
if ($LASTEXITCODE -ne 0) { throw 'CoreCLR browser-wasm native publish failed.' }

New-Item -ItemType Directory -Force -Path $www | Out-Null
Copy-Item -LiteralPath (Join-Path $fixture 'harness.html') -Destination (Join-Path $www 'harness.html') -Force
Copy-Item -LiteralPath $engine -Destination (Join-Path $www 'smoke.js') -Force
Copy-Item -LiteralPath (Join-Path $publish '_framework') -Destination $www -Recurse -Force
Copy-Item -LiteralPath (Join-Path $publish '_framework/dotnet.native.wasm') -Destination (Join-Path $www 'smoke.wasm') -Force
Copy-Item -LiteralPath (Join-Path $repo 'bin/.web_zip/godot.audio.worklet.js') -Destination (Join-Path $www 'smoke.audio.worklet.js') -Force
Copy-Item -LiteralPath (Join-Path $repo 'bin/.web_zip/godot.audio.position.worklet.js') -Destination (Join-Path $www 'smoke.audio.position.worklet.js') -Force

& $godot --headless --path $fixture --import
if ($LASTEXITCODE -ne 0) { throw 'Godot fixture import failed.' }
& $godot --headless --path $fixture --export-pack Web (Join-Path $www 'smoke.pck')
if ($LASTEXITCODE -ne 0) { throw 'Godot fixture pack export failed.' }

$wasm = Join-Path $www 'smoke.wasm'
$wasmHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $wasm).Hash
Write-Host "Native publish assembled at $www"
Write-Host "smoke.wasm SHA256: $wasmHash"
