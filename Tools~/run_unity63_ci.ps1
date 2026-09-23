[CmdletBinding()]
param(
    [string]$EditorPath = $env:UNITY63_EDITOR_PATH,
    [ValidateSet('minimal', 'representative')][string]$Profile = 'minimal',
    [ValidatePattern('^[a-z0-9-]*$')][string]$RunName = '',
    [ValidateRange(1, 45)][int]$StageTimeoutMinutes = 20,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$fixtureName = if ($RunName) { "$Profile-$RunName" } else { $Profile }
$fixtureRoot = Join-Path $repository ".validation/unity63-ci/$fixtureName"
$project = Join-Path $fixtureRoot 'project'
$artifacts = Join-Path $fixtureRoot 'artifacts'
$packageSnapshot = Join-Path $fixtureRoot 'package-under-test'
if ((Test-Path -LiteralPath $project) -or (Test-Path -LiteralPath $packageSnapshot)) {
    throw "Fixture already exists: $fixtureRoot. Preserve its evidence and choose a new -RunName before rerunning."
}
if (-not $PrepareOnly -and (-not $EditorPath -or -not (Test-Path -LiteralPath $EditorPath -PathType Leaf))) {
    throw 'Provide UNITY63_EDITOR_PATH or -EditorPath pointing to a licensed Unity 6000.3.5f2 Editor executable. This script does not activate or acquire a license.'
}
foreach ($relative in @('Packages', 'ProjectSettings', 'Assets/Ci/Editor')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $project $relative) | Out-Null
}
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
New-Item -ItemType Directory -Force -Path $packageSnapshot | Out-Null
# Keep the local UPM package a sibling of the project, never its ancestor.
# An ancestor resolvedPath also contains Library/PackageCache; Unity can then
# misassociate another package's MonoScripts with this local package.
foreach ($entry in @('package.json', 'package.json.meta', 'Editor', 'Editor.meta', 'Runtime', 'Runtime.meta',
    'Tests', 'Tests.meta', 'README.md', 'README.md.meta', 'CHANGELOG.md', 'CHANGELOG.md.meta')) {
    Copy-Item -LiteralPath (Join-Path $repository $entry) -Destination $packageSnapshot -Recurse
}
$sourceManifest = Get-ChildItem -LiteralPath $packageSnapshot -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($packageSnapshot, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
}
$sourceManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $artifacts 'package-source-sha256.json') -Encoding utf8
$dependencies = [ordered]@{
    'com.maicon.scene-analyzer' = 'file:' + $packageSnapshot.Replace('\', '/')
    'com.unity.test-framework' = '1.6.0'
}
if ($Profile -eq 'representative') {
    $dependencies['com.unity.ai.navigation'] = '2.0.9'
    $dependencies['com.unity.inputsystem'] = '1.17.0'
    $dependencies['com.unity.probuilder'] = '6.0.9'
    $dependencies['com.unity.render-pipelines.universal'] = '17.3.0'
    $dependencies['com.unity.timeline'] = '1.8.10'
    $dependencies['com.unity.ugui'] = '2.0.0'
    foreach ($module in @('ai', 'animation', 'assetbundle', 'audio', 'cloth', 'director', 'imageconversion', 'imgui',
        'jsonserialize', 'particlesystem', 'physics', 'physics2d', 'screencapture', 'terrain', 'terrainphysics',
        'tilemap', 'ui', 'uielements', 'umbra', 'unitywebrequest', 'unitywebrequestaudio', 'unitywebrequesttexture',
        'vehicles', 'video', 'vr', 'wind', 'xr')) {
        $dependencies["com.unity.modules.$module"] = '1.0.0'
    }
}
[ordered]@{ dependencies = $dependencies; testables = @('com.maicon.scene-analyzer') } |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $project 'Packages/manifest.json') -Encoding utf8
@'
m_EditorVersion: 6000.3.5f2
m_EditorVersionWithRevision: 6000.3.5f2 (3fa8bc678cb0)
'@ | Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Unity63Ci/AgentEnvironmentCi.cs') -Destination (Join-Path $project 'Assets/Ci/Editor/AgentEnvironmentCi.cs')
[ordered]@{
    name = 'AgentEnvironmentCi.Editor'
    references = @('com.maicon.scene-analyzer.Editor')
    overrideReferences = $true
    precompiledReferences = @('Newtonsoft.Json.dll')
    includePlatforms = @('Editor')
    optionalUnityReferences = @('TestAssemblies')
    autoReferenced = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $project 'Assets/Ci/Editor/AgentEnvironmentCi.Editor.asmdef') -Encoding utf8
if ($PrepareOnly) {
    Write-Output "Prepared fixture only; Unity was not launched: $project"
    return
}

function Invoke-UnityStage([string]$Name, [string[]]$ExtraArguments) {
    $log = Join-Path $artifacts "$Name.log"
    $arguments = @('-batchmode', '-projectPath', $project, '-logFile', $log) + $ExtraArguments
    # Windows filenames cannot contain quotes; quote every argument for spaces.
    $quoted = $arguments | ForEach-Object { '"' + $_ + '"' }
    $process = Start-Process -FilePath $EditorPath -ArgumentList $quoted -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($StageTimeoutMinutes * 60 * 1000)) {
        $process.Kill($true)
        throw "Unity $Name timed out; inspect $log"
    }
    if ($process.ExitCode -ne 0) { throw "Unity $Name failed with exit $($process.ExitCode); inspect $log" }
}
function Assert-TestResult([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "Unity produced no test results: $Path" }
    [xml]$results = Get-Content -LiteralPath $Path -Raw
    $summary = $results.'test-run'
    # Unity/NUnit may label the aggregate Skipped:Ignored when optional tests
    # are ignored even though executed tests passed. Inspect the real counters.
    if ($null -eq $summary -or [int]$summary.failed -gt 0 -or [int]$summary.inconclusive -gt 0 -or [int]$summary.passed -lt 1 -or
        $summary.result -notmatch '^(Passed|Skipped)([:(].*)?$') {
        throw "Unity test run did not pass with executed tests: $Path"
    }
    Write-Output "Test results: aggregate=$($summary.result), $($summary.passed) passed, $($summary.skipped) skipped; $Path"
}
$priorReport = $env:UNITY_MCP_CI_REPORT
try {
    $env:UNITY_MCP_CI_REPORT = Join-Path $artifacts 'import-report.json'
    Invoke-UnityStage 'import' @('-quit', '-executeMethod', 'Unity63Ci.ImportGate.Verify')
    if (-not (Test-Path -LiteralPath $env:UNITY_MCP_CI_REPORT)) { throw 'Import verification did not produce its report.' }
    $editResults = Join-Path $artifacts 'editmode.xml'
    # Tests also live in Modeling, AI3D, NavMesh, ShaderGraph and PostFx namespaces.
    # The shared root includes every package suite while excluding the CI helper.
    Invoke-UnityStage 'editmode' @('-runTests', '-testPlatform', 'EditMode', '-testFilter', 'Maicon.SceneAnalyzer', '-testResults', $editResults)
    Assert-TestResult $editResults
    $smokeResults = Join-Path $artifacts 'play-reload-smoke.xml'
    Invoke-UnityStage 'play-reload-smoke' @('-runTests', '-testPlatform', 'EditMode', '-testFilter', 'Unity63Ci.SmokeTests', '-testResults', $smokeResults)
    Assert-TestResult $smokeResults
} finally {
    $env:UNITY_MCP_CI_REPORT = $priorReport
}
Write-Output "Unity 6000.3.5f2 $Profile validation completed. Evidence: $artifacts"
