[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$fixture = $PSScriptRoot
$artifacts = Join-Path $fixture '.artifacts'
$www = Join-Path $artifacts 'www'
$framework = Join-Path $www '_framework'
$project = Join-Path $fixture 'RuntimeExitProbe.csproj'
$bridge = [IO.Path]::GetFullPath((Join-Path $fixture '../../../web/mono_bridge.js'))

function New-Resource([string] $Name) {
    return [ordered]@{ name = $Name; virtualPath = $Name; resolvedUrl = "_framework/$Name" }
}

New-Item -ItemType Directory -Path $framework -Force | Out-Null
Push-Location $fixture
try {
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^11\.') {
        throw 'This probe requires a .NET 11 SDK with the browser-wasm workload.'
    }
    & dotnet build $project -c Release -r browser-wasm -p:WasmBuildNative=false -o $framework --nologo |
        Tee-Object -FilePath (Join-Path $artifacts 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime exit probe build failed. See .artifacts/build.log.' }

    foreach ($name in @('RuntimeExitProbe.dll', 'RuntimeExitProbe.runtimeconfig.json', 'System.Private.CoreLib.dll',
            'dotnet.js', 'dotnet.native.js', 'dotnet.runtime.js', 'dotnet.native.wasm')) {
        if (!(Test-Path -LiteralPath (Join-Path $framework $name) -PathType Leaf)) {
            throw "The build did not provide the required prebuilt runtime asset: $name"
        }
    }

    $assemblies = @(Get-ChildItem -LiteralPath $framework -Filter '*.dll' -File |
        Sort-Object Name | ForEach-Object { New-Resource $_.Name })
    $runtimeConfig = Get-Content -LiteralPath (Join-Path $framework 'RuntimeExitProbe.runtimeconfig.json') -Raw |
        ConvertFrom-Json
    $config = [ordered]@{
        mainAssemblyName = 'RuntimeExitProbe.dll'
        resources = [ordered]@{
            coreAssembly = $assemblies
            assembly = @()
            jsModuleNative = @(New-Resource 'dotnet.native.js')
            jsModuleRuntime = @(New-Resource 'dotnet.runtime.js')
            wasmNative = @(New-Resource 'dotnet.native.wasm')
        }
        environmentVariables = @{ DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1' }
        runtimeConfig = $runtimeConfig
    }
    $json = $config | ConvertTo-Json -Depth 32
    [IO.File]::WriteAllText((Join-Path $www 'config.json'), "$json`n", [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $bridge -Destination (Join-Path $www 'mono_bridge.js') -Force
    Copy-Item -LiteralPath (Join-Path $fixture 'harness.html') -Destination (Join-Path $www 'harness.html') -Force
    if ((Get-FileHash -LiteralPath $bridge).Hash -ne (Get-FileHash -LiteralPath (Join-Path $www 'mono_bridge.js')).Hash) {
        throw 'The copied bridge does not match production.'
    }
    Write-Host "Prepared $www with SDK $sdk and $($assemblies.Count) assemblies. No native relink or SDK patch."
} finally {
    Pop-Location
}
