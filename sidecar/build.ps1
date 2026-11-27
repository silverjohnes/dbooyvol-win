# Builds the unchanged Java sidecar against the bundled JDBC drivers.
# Requires JDK 11+ (javac.exe and jar.exe on PATH or JAVA_HOME set).
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$src = Join-Path $here "src\main\java"
$out = Join-Path $here "build\classes"
$jar = Join-Path $here "build\sidecar.jar"

$javac = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME "bin\javac.exe" } else { "javac.exe" }
$jarTool = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME "bin\jar.exe" } else { "jar.exe" }

$classpath = (Get-ChildItem (Join-Path $root "drivers\*.jar") |
    Where-Object { $_.Name -ne "sidecar.jar" } |
    ForEach-Object { $_.FullName }) -join ";"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out -Force | Out-Null

$sources = Get-ChildItem $src -Recurse -Filter *.java | ForEach-Object { $_.FullName }
& $javac --release 11 -encoding UTF-8 -cp $classpath -d $out $sources
if ($LASTEXITCODE -ne 0) { throw "javac failed" }

& $jarTool --create --file $jar --main-class silver.dbooyvol.Main -C $out .
if ($LASTEXITCODE -ne 0) { throw "jar failed" }

Write-Host "Built $jar"
