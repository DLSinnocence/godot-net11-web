[CmdletBinding()]
param(
    [ValidateRange(1024, 65535)]
    [int] $Port = 8132,
    [ValidateRange(5, 300)]
    [int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$artifacts = Join-Path $PSScriptRoot '.artifacts'
$www = Join-Path $artifacts 'www'
$serverScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../smoke/serve.mjs'))
$bridge = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../web/mono_bridge.js'))
$session = "runtime-exit-$PID-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$url = "http://127.0.0.1:$Port/"
$pw = @('--offline', '--yes', '--package', '@playwright/cli', 'playwright-cli', "-s=$session", '--raw')
$cliLog = Join-Path $artifacts 'playwright.log'
$server = $null
$sessionStarted = $false

foreach ($required in @('harness.html', 'mono_bridge.js', 'config.json', '_framework/RuntimeExitProbe.dll',
        '_framework/dotnet.js', '_framework/dotnet.native.wasm')) {
    if (!(Test-Path -LiteralPath (Join-Path $www $required) -PathType Leaf)) {
        throw "Missing $required. Run prepare.ps1 first."
    }
}
if ((Get-FileHash -LiteralPath $bridge).Hash -ne (Get-FileHash -LiteralPath (Join-Path $www 'mono_bridge.js')).Hash) {
    throw 'The staged bridge differs from production. Run prepare.ps1 again.'
}
$node = (Get-Command node -ErrorAction Stop).Source
$npx = (Get-Command npx.cmd -ErrorAction Stop).Source

function Invoke-ProbeBrowser([string[]] $Command) {
    $output = & $npx @pw @Command 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    Add-Content -LiteralPath $cliLog -Value $output -Encoding utf8
    if ($exitCode -ne 0) { throw "Playwright failed ($($Command[0])): $output" }
    return $output.Trim()
}

function Wait-Probe([string] $Name, [bool] $ExpectedBridge, [int] $ExpectedRun) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $output = Invoke-ProbeBrowser -Command @('eval', '() => globalThis.probe ?? null')
        $state = $output | ConvertFrom-Json
        if ($null -ne $state) {
            $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $artifacts "$Name.json") -Encoding utf8
            if ($state.errors.Count -ne 0) { throw "$Name reported errors: $($state.errors -join '; ')" }
            if ($state.settled) {
                if ($state.run -ne $ExpectedRun -or $state.bridgeEnabled -ne $ExpectedBridge -or $state.code -ne 0) {
                    throw "$Name returned the wrong navigation or exit mode."
                }
                foreach ($marker in @('PROBE:READY', 'PROBE:MANAGED_CLEANUP')) {
                    if (@($state.log | Where-Object { $_ -eq $marker }).Count -ne 1) {
                        throw "$Name did not emit exactly one $marker."
                    }
                }
                $expectedEvents = @('created', 'main-returned', 'native-callback-enter', 'native-callback-left')
                if ($ExpectedBridge) {
                    $expectedEvents += 'onExit'
                    if ($state.exitCodes.Count -ne 1 -or $state.exitCodes[0] -ne 0) {
                        throw "$Name did not receive exactly one real onExit(0)."
                    }
                } elseif ($state.exitCodes.Count -ne 0) {
                    throw 'Baseline unexpectedly completed native shutdown without the loader bridge.'
                }
                $expectedEvents += 'settled'
                if (($state.events -join ',') -ne ($expectedEvents -join ',')) {
                    throw "$Name callback order was wrong: $($state.events -join ',')"
                }
                Write-Host "$Name passed: onExit=[$($state.exitCodes -join ',')], errors=[], run=$($state.run)."
                return
            }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Name did not settle within $TimeoutSeconds seconds."
}

# Refuse occupied ports rather than attaching to or stopping someone else's server.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
try {
    $listener.Start()
} finally {
    $listener.Stop()
}

Push-Location $artifacts
try {
    $server = Start-Process -FilePath $node -ArgumentList @("`"$serverScript`"", "`"$www`"", "$Port") `
        -WorkingDirectory $artifacts -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $artifacts 'server.stdout.log') `
        -RedirectStandardError (Join-Path $artifacts 'server.stderr.log')
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($server.HasExited) { throw 'The probe server exited. See .artifacts/server.stderr.log.' }
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 1 | Out-Null
            $ready = $true
            break
        } catch {
            Start-Sleep -Milliseconds 100
        }
    }
    if (!$ready -or $server.HasExited) { throw 'The probe server did not start.' }

    $sessionStarted = $true
    Invoke-ProbeBrowser -Command @('open', "${url}?bridge=false", '--browser=chrome') | Out-Null
    Wait-Probe -Name 'baseline' -ExpectedBridge $false -ExpectedRun 1
    Invoke-ProbeBrowser -Command @('goto', "${url}?bridge=true") | Out-Null
    Wait-Probe -Name 'bridge' -ExpectedBridge $true -ExpectedRun 2
    Invoke-ProbeBrowser -Command @('reload') | Out-Null
    Wait-Probe -Name 'reload' -ExpectedBridge $true -ExpectedRun 3
    Write-Host 'Real zero-code loader/C# shutdown probe passed; this is not a full Godot scene test.'
} finally {
    try {
        if ($sessionStarted) { Invoke-ProbeBrowser -Command @('close') | Out-Null }
    } finally {
        try {
            if ($null -ne $server -and !$server.HasExited) {
                Stop-Process -Id $server.Id -Force
                $server.WaitForExit()
            }
        } finally {
            Pop-Location
        }
    }
}
