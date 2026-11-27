# Produces the private Windows JRE shipped next to DBooyvol.exe.
# jlink output is platform-specific: run this on Windows, with a JDK 17+.
#
#   powershell -ExecutionPolicy Bypass -File runtime\make-jlink.ps1
#
# The module set is the smallest one the JDBC drivers need:
#   java.sql              JDBC
#   java.naming           Oracle LDAP / JNDI
#   java.security.jgss    Kerberos-capable Oracle authentication
#   java.management       driver MBeans
#   java.logging          slf4j bridge
#   java.xml              Oracle/ClickHouse XML handling
#   jdk.crypto.ec         TLS ECDHE (PostgreSQL, ClickHouse, Oracle)
#   jdk.unsupported       sun.misc.Unsafe used by some drivers
#   jdk.charsets          legacy server encodings
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $here "jre"

if (Test-Path $target) { Remove-Item $target -Recurse -Force }

$modules = @(
    "java.base", "java.sql", "java.naming", "java.security.jgss",
    "java.management", "java.logging", "java.xml",
    "jdk.crypto.ec", "jdk.unsupported", "jdk.charsets"
) -join ","

$jlink = if ($env:JAVA_HOME) { Join-Path $env:JAVA_HOME "bin\jlink.exe" } else { "jlink.exe" }

& $jlink `
    --add-modules $modules `
    --strip-debug `
    --no-header-files `
    --no-man-pages `
    --compress=zip-6 `
    --output $target
if ($LASTEXITCODE -ne 0) { throw "jlink failed" }

& (Join-Path $target "bin\java.exe") -version
Write-Host "Runtime written to $target"
