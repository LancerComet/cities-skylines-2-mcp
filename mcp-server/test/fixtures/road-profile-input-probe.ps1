$ErrorActionPreference='Stop'
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not(Test-Path -LiteralPath (Join-Path $bridgeRoot 'RoadLayoutInputRules.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
Add-Type -Path @((Join-Path $bridgeRoot 'RoadLayoutInputRules.cs'),(Join-Path $PSScriptRoot 'road-profile-input-probe.cs'))
[CS2MCP.RoadProfileInputProbe]::Run()
