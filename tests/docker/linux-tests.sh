#!/usr/bin/env bash
# Runs inside the Linux test rig (tests/docker/linux-tests.Dockerfile).
# Usage: linux-tests.sh [all|storage|mcp|roslyn|blazor|integration|restore-probe|concurrency|harness|<suite> ...]
# (`integration` = the whole Integration project, Concurrency included; not part of `all`.)
#   extra args after "--" are passed to every MTP test run (e.g. -- --filter-class X).
# Writes one log per suite to /out (mounted by run-linux-tests.ps1) and exits non-zero on failure.
set -uo pipefail

out=/out
mkdir -p "$out"
suites=()
extra=()
seen_dashdash=0
for a in "$@"; do
  if [[ $seen_dashdash == 1 ]]; then extra+=("$a"); elif [[ $a == "--" ]]; then seen_dashdash=1; else suites+=("$a"); fi
done
[[ ${#suites[@]} -eq 0 || ${suites[0]} == all ]] && suites=(storage mcp roslyn blazor concurrency harness)

failed=()
run() { # name, command...
  local name=$1; shift
  echo "===== $name: $*"
  "$@" 2>&1 | tee "$out/$name.log" | grep -E "total:|failed:|succeeded:|skipped:|^failed |^skipped |Passed!|Failed!|PASS|FAIL"
  local rc=${PIPESTATUS[0]}
  [[ $rc -ne 0 ]] && failed+=("$name (exit $rc)")
}
measure() { # like run, but a measurement: the exit code is reported, never fails the rig
  local name=$1; shift
  echo "===== $name (measurement): $*"
  "$@" 2>&1 | tee "$out/$name.log" | grep -E "PASS|FAIL|▶"
  echo "      exit ${PIPESTATUS[0]} (informational: shared mode always has F3 setup failures)"
}

for s in "${suites[@]}"; do
  case $s in
    storage) run storage dotnet run --no-build --project tests/CodeMap.Storage.Engine.Tests -- "${extra[@]}" ;;
    mcp) run mcp dotnet run --no-build --project tests/CodeMap.Mcp.Tests -- "${extra[@]}" ;;
    roslyn)
      run roslyn-lock dotnet run --no-build --project tests/CodeMap.Roslyn.Tests -- \
        --filter-class "CodeMap.Roslyn.Tests.CheckoutBuildLockTests" \
        --filter-class "CodeMap.Roslyn.Tests.SameCheckoutConcurrentCompileTests" "${extra[@]}" ;;
    blazor)
      run blazor dotnet run --no-build --project tests/CodeMap.Integration.Tests -- \
        --filter-class "CodeMap.Integration.Tests.Regression.Razor.BlazorIndexingTests" \
        --filter-class "CodeMap.Integration.Tests.Regression.Razor.GeneratorLoadFailureTests" "${extra[@]}" ;;
    integration) run integration dotnet run --no-build --project tests/CodeMap.Integration.Tests -- "${extra[@]}" ;;
    restore-probe)
      run restore-probe dotnet run --no-build --project tests/CodeMap.Roslyn.Tests -- \
        --filter-class "CodeMap.Roslyn.Tests.RestoreOutputProbeTests" "${extra[@]}" ;;
    concurrency)
      run concurrency dotnet run --no-build --project tests/CodeMap.Integration.Tests -- \
        --filter-trait "Category=Concurrency" "${extra[@]}" ;;
    harness)
      for mode in isolated shared; do
        measure "harness-$mode" dotnet run --no-build --project tests/CodeMap.Harness -- \
          concurrency --repo micro --agents 1,4 --duration 30 --idle 0 --workspace-mode "$mode" \
          --output json --out-file "$out/harness-$mode.json"
      done ;;
    *) echo "unknown suite: $s"; failed+=("$s (unknown)") ;;
  esac
done

echo "===== summary"
if [[ ${#failed[@]} -eq 0 ]]; then echo "ALL PASSED"; exit 0; fi
printf 'FAILED: %s\n' "${failed[@]}"
exit 1
