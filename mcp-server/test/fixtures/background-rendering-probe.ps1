$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Console]::OutputEncoding
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'BackgroundRenderingPolicy.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
Add-Type -Path @((Join-Path $bridgeRoot 'BackgroundRenderingPolicy.cs'),(Join-Path $PSScriptRoot 'background-rendering-probe.cs'))
[CS2MCP.BackgroundRenderingProbe]::Run() | ConvertTo-Json -Depth 10 -Compress
