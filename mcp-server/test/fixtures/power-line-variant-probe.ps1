$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'NativePowerLineVariantRules.cs'))){$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))}
$refs=@([IO.Directory]::GetFiles((Join-Path $PSHOME 'ref'),'*.dll'))
$refs+=Join-Path $PSHOME 'Newtonsoft.Json.dll'
Add-Type -Path @((Join-Path $bridgeRoot 'NativePowerLineVariantRules.cs'),(Join-Path $PSScriptRoot 'power-line-variant-probe.cs')) -ReferencedAssemblies $refs -CompilerOptions '/nowarn:1701'
[CS2MCP.PowerLineVariantProbe]::Run()
