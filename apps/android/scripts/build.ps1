param(
    [ValidateSet('test', 'lint', 'debug', 'release', 'all')]
    [string]$Task = 'all'
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent $PSScriptRoot

if (-not $env:JAVA_HOME) {
    $JdkCandidates = @(
        'C:\Program Files\Android\openjdk\jdk-21.0.8',
        'C:\Program Files\Android\Android Studio\jbr'
    )
    $env:JAVA_HOME = $JdkCandidates | Where-Object {
        Test-Path -LiteralPath (Join-Path $_ 'bin\java.exe')
    } | Select-Object -First 1
}

if (-not $env:JAVA_HOME) {
    throw 'JDK 17 or newer was not found. Set JAVA_HOME before building.'
}

$TaskMap = @{
    test = @('test')
    lint = @('lint')
    debug = @('assembleDebug')
    release = @('assembleRelease')
    all = @('test', 'lint', 'assembleDebug', 'assembleRelease')
}

Push-Location $ProjectRoot
try {
    & .\gradlew.bat @($TaskMap[$Task]) --stacktrace
    if ($LASTEXITCODE -ne 0) { throw "Gradle failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}
