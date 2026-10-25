$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Console]::OutputEncoding
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'BackgroundSceneRenderingPolicy.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
Add-Type -Path @((Join-Path $bridgeRoot 'BackgroundSceneRenderingPolicy.cs'),(Join-Path $PSScriptRoot 'background-scene-rendering-probe.cs'))
[CS2MCP.BackgroundSceneRenderingProbe]::Run() | ConvertTo-Json -Depth 10 -Compress
