$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Console]::OutputEncoding
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'BackgroundFrameOutputPolicy.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
Add-Type -Path @((Join-Path $bridgeRoot 'BackgroundFrameOutputPolicy.cs'),(Join-Path $PSScriptRoot 'background-frame-output-probe.cs'))
[CS2MCP.BackgroundFrameOutputProbe]::Run() | ConvertTo-Json -Depth 10 -Compress
