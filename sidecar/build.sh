#!/usr/bin/env bash
# Builds the unchanged Java sidecar against the bundled JDBC drivers.
# Requires JDK 11+ (javac and jar on PATH or JAVA_HOME set).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/.." && pwd)"
src="$here/src/main/java"
out="$here/build/classes"
jar="$here/build/sidecar.jar"

javac="${JAVA_HOME:+$JAVA_HOME/bin/}javac"
jar_tool="${JAVA_HOME:+$JAVA_HOME/bin/}jar"

classpath=""
for f in "$root"/drivers/*.jar; do
  [ "$(basename "$f")" = "sidecar.jar" ] && continue
  classpath="${classpath:+$classpath:}$f"
done

rm -rf "$out"
mkdir -p "$out"

find "$src" -name '*.java' -print0 | xargs -0 "$javac" \
  --release 11 -encoding UTF-8 -cp "$classpath" -d "$out"

"$jar_tool" --create --file "$jar" --main-class silver.dbooyvol.Main -C "$out" .

echo "Built $jar"
