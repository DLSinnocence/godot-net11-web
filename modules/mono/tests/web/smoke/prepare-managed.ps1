[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$fixture = $PSScriptRoot
$project = Join-Path $fixture 'GodotWebSmoke.csproj'
$artifacts = Join-Path $fixture '.artifacts'
$packages = Join-Path $artifacts 'nuget/packages'
$expectedSdk = '11.0.100-preview.7.26381.103'

$actualSdk = (& dotnet --version).Trim()
if ($actualSdk -ne $expectedSdk) {
    throw "Expected .NET SDK $expectedSdk from global.json, got $actualSdk."
}

New-Item -ItemType Directory -Force -Path $packages | Out-Null

& dotnet restore $project --runtime browser-wasm --packages $packages --force-evaluate
if ($LASTEXITCODE -ne 0) { throw 'Managed restore failed.' }

# Browser.props enables native relinking by default. This global property prevents the
# preparation step from starting emcc before the local static template is ready.
& dotnet build $project --configuration ExportDebug --runtime browser-wasm --no-restore -p:WasmBuildNative=false
if ($LASTEXITCODE -ne 0) { throw 'Managed browser-wasm build failed.' }

& dotnet msbuild $project -nologo -target:PrintSmokeConfiguration -property:Configuration=ExportDebug -property:RuntimeIdentifier=browser-wasm -property:WasmBuildNative=false
if ($LASTEXITCODE -ne 0) { throw 'Smoke configuration evaluation failed.' }

$assembly = Join-Path $artifacts 'bin/ExportDebug/GodotWebSmoke.dll'
$generated = Join-Path $artifacts 'generated/ExportDebug'
if (!(Test-Path -LiteralPath $assembly)) { throw "Missing managed smoke assembly: $assembly" }

$generatedNames = Get-ChildItem -LiteralPath $generated -File -Recurse | ForEach-Object Name
foreach ($required in @('GodotPlugins.Game.generated.cs', 'Smoke_ScriptMethods.generated.cs', 'Smoke_ScriptPath.generated.cs')) {
    if ($generatedNames -notcontains $required) {
        throw "Source generator output is missing $required."
    }
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $assembly).Hash
Write-Host 'Managed browser-wasm fixture built without native relinking.'
Write-Host "Assembly: $assembly"
Write-Host "SHA256: $hash"
