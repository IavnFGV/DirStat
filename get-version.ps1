# Prints the full version: <VERSION>.<build>.
# Build = 1 + number of non-documentation commits after the last commit that changed VERSION.
# Changing VERSION resets the counter to 1. Needs full git history (fetch-depth: 0 in CI).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$prefix = (Get-Content -LiteralPath (Join-Path $root 'VERSION') -Raw).Trim()
$base = git -C $root log -1 --format=%H -- VERSION
if (-not $base) { throw 'VERSION is not committed yet' }
$count = git -C $root rev-list --count "$base..HEAD" -- . ':(exclude)*.md' ':(exclude)docs'
"$prefix.$([int]$count + 1)"
