$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'HttpBridgeServer.cs'))){$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))}
$refs=@([IO.Directory]::GetFiles((Join-Path $PSHOME 'ref'),'*.dll'))
$refs+=Join-Path $PSHOME 'Newtonsoft.Json.dll'
Add-Type -Path @((Join-Path $bridgeRoot 'BridgeRequest.cs'),(Join-Path $bridgeRoot 'HttpBridgeServer.cs'),(Join-Path $bridgeRoot 'RuntimeMemory.cs'),(Join-Path $PSScriptRoot 'http-security-probe.cs')) -ReferencedAssemblies $refs -CompilerOptions '/nowarn:1701'
[CS2MCP.HttpSecurityProbe]::Run((Join-Path $PSScriptRoot 'http-security-routes.json'))
