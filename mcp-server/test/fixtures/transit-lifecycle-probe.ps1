$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Console]::OutputEncoding
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'BridgeTransitToolSystem.Lifecycle.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
Add-Type -Path @((Join-Path $bridgeRoot 'BridgeTransitToolSystem.Lifecycle.cs'),(Join-Path $PSScriptRoot 'transit-lifecycle-probe.cs'))
[CS2MCP.BridgeTransitToolSystem]::RunLifecycleProbe() | ConvertTo-Json -Depth 10 -Compress
