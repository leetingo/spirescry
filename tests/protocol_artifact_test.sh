#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tool="$repo/tools/ProtocolArtifact/ProtocolArtifact.csproj"
artifact="$repo/protocol.json"
tmp="$(mktemp -d "${TMPDIR:-/tmp}/spirescry-protocol-test.XXXXXX")"
trap 'rm -rf "$tmp"' EXIT

dotnet run --project "$tool" -- \
    --check "$artifact" --output "$tmp/protocol.json"
cmp -s "$artifact" "$tmp/protocol.json" || {
    echo "generated protocol artifact differs from the checked artifact" >&2
    exit 1
}

cp "$artifact" "$tmp/drifted.json"
printf ' ' >> "$tmp/drifted.json"
if dotnet run --project "$tool" -- --check "$tmp/drifted.json"; then
    echo "protocol checker accepted a drifted artifact" >&2
    exit 1
fi

dotnet msbuild "$repo/src/Spirescry.csproj" \
    -target:EmitProtocolArtifact \
    -property:ProtocolArtifactOutput="$tmp/build/protocol.json" &
mod_target_pid=$!
dotnet msbuild "$repo/headless/Host/Host.csproj" \
    -target:EmitProtocolArtifact \
    -property:ProtocolArtifactOutput="$tmp/host/protocol.json"
wait "$mod_target_pid"
cmp -s "$artifact" "$tmp/build/protocol.json" || {
    echo "mod build target did not emit the checked protocol artifact" >&2
    exit 1
}
cmp -s "$artifact" "$tmp/host/protocol.json" || {
    echo "host build target did not emit the checked protocol artifact" >&2
    exit 1
}

if dotnet msbuild "$repo/headless/Host/Host.csproj" \
    -target:EmitProtocolArtifact \
    -property:ProtocolArtifactSource="$tmp/drifted.json" \
    -property:ProtocolArtifactOutput="$tmp/host/protocol.json"; then
    echo "host build target accepted a drifted artifact" >&2
    exit 1
fi

if dotnet msbuild "$repo/src/Spirescry.csproj" \
    -target:EmitProtocolArtifact \
    -property:ProtocolArtifactSource="$tmp/drifted.json" \
    -property:ProtocolArtifactOutput="$tmp/build/protocol.json"; then
    echo "mod build target accepted a drifted artifact" >&2
    exit 1
fi

echo "ok - protocol artifact generation, build emission, and drift check"
