[CmdletBinding()]
param(
    [int] $Port = 8123
)

$ErrorActionPreference = 'Stop'
$fixture = $PSScriptRoot
$www = Join-Path $fixture '.artifacts/www'
$serverScript = Join-Path $fixture 'serve.mjs'
$session = "godot-smoke-$PID"
$url = "http://127.0.0.1:$Port/"

foreach ($required in @('harness.html', 'smoke.js', 'smoke.wasm', 'smoke.pck', '_framework/dotnet.js')) {
    $path = Join-Path $www $required
    if (!(Test-Path -LiteralPath $path)) { throw "Browser fixture is not published: $path" }
}

$server = Start-Process -FilePath node -ArgumentList @($serverScript, $www, $Port) -PassThru -WindowStyle Hidden
try {
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        try {
            Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 1 | Out-Null
            break
        } catch {
            if ($attempt -eq 49) { throw 'Timed out waiting for the local smoke server.' }
            Start-Sleep -Milliseconds 100
        }
    }

    $pw = @('--offline', '--yes', '--package', '@playwright/cli', 'playwright-cli', "-s=$session")
    & npx.cmd @pw open $url
    if ($LASTEXITCODE -ne 0) { throw 'Playwright failed to open the smoke fixture.' }

    function Wait-SmokePass([int] $expectedRun) {
        for ($attempt = 0; $attempt -lt 180; $attempt++) {
            $output = (& npx.cmd @pw eval "() => document.body.dataset.smokeStatus + ':' + document.body.dataset.smokeRun" 2>&1 | Out-String)
            # Match only the returned value, not logged markers or the echoed evaluation code.
            if ($LASTEXITCODE -eq 0) {
                if ($output -match ('(?m)^"PASS:' + $expectedRun + '"\r?$')) { return }
                if ($output -match ('(?m)^"FAIL:' + $expectedRun + '"\r?$')) { break }
            }
            Start-Sleep -Milliseconds 500
        }
        & npx.cmd @pw console error
        & npx.cmd @pw snapshot
        throw "Browser smoke run $expectedRun did not pass."
    }

    Wait-SmokePass 1
    & npx.cmd @pw reload
    if ($LASTEXITCODE -ne 0) { throw 'Playwright reload failed.' }
    Wait-SmokePass 2
    & npx.cmd @pw console
    Write-Host 'Browser load and reload smoke passed.'
} finally {
    try {
        & npx.cmd --offline --yes --package '@playwright/cli' playwright-cli "-s=$session" close 2>$null | Out-Null
    } finally {
        if (!$server.HasExited) {
            Stop-Process -Id $server.Id -Force
            $server.WaitForExit()
        }
    }
}
