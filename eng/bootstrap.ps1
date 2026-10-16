$ErrorActionPreference = 'Stop'
$svmRoot = Split-Path $PSScriptRoot -Parent
$lock = Get-Content (Join-Path $svmRoot 'build/toolchain.lock.json') -Raw | ConvertFrom-Json
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'Windows x64 is required.' }
$archive = $lock.platforms.'win-x64'.node
$node = Join-Path $svmRoot ".tools/win-x64/node/$($lock.node)/node.exe"
if (Test-Path $node) { & $node (Join-Path $PSScriptRoot 'bootstrap.mjs') @args; exit $LASTEXITCODE }
$download = Join-Path $svmRoot ".cache/tool-downloads/node-v$($lock.node)-win-x64.zip"
New-Item -ItemType Directory -Force (Split-Path $download) | Out-Null
if (!(Test-Path $download)) { Invoke-WebRequest -Uri $archive.url -OutFile $download }
if ((Get-FileHash -Algorithm SHA256 $download).Hash.ToLowerInvariant() -ne $archive.hash) { throw 'Node archive checksum mismatch.' }
$stage = Join-Path $svmRoot ".cache/node-bootstrap-$PID"
try {
  Expand-Archive -LiteralPath $download -DestinationPath $stage
  & (Join-Path $stage "node-v$($lock.node)-win-x64/node.exe") (Join-Path $PSScriptRoot 'bootstrap.mjs') @args
  if ($LASTEXITCODE -ne 0) { throw 'Repository tool bootstrap failed.' }
} finally { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
