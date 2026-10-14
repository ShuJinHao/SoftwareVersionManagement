$ErrorActionPreference = 'Stop'
$svmRoot = Split-Path $PSScriptRoot -Parent
$lock = Get-Content (Join-Path $svmRoot 'build/toolchain.lock.json') -Raw | ConvertFrom-Json
$node = Join-Path $svmRoot ".tools/win-x64/node/$($lock.node)/node.exe"
if (!(Test-Path $node)) { throw 'Run eng/bootstrap.ps1 first.' }
& $node (Join-Path $PSScriptRoot 'run.mjs') rabbitmq @args
exit $LASTEXITCODE
