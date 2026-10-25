$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'NativeCaptureOperation.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
$refs=@([IO.Directory]::GetFiles((Join-Path $PSHOME 'ref'),'*.dll'))
$refs+=Join-Path $PSHOME 'Newtonsoft.Json.dll'
Add-Type -Path @((Join-Path $bridgeRoot 'BridgeRequest.cs'),(Join-Path $bridgeRoot 'HttpBridgeServer.cs'),(Join-Path $bridgeRoot 'RuntimeMemory.cs'),(Join-Path $bridgeRoot 'NativeCaptureOperation.cs'),(Join-Path $bridgeRoot 'NativeCaptureResultReader.cs'),(Join-Path $PSScriptRoot 'native-capture-operation-probe.cs')) -ReferencedAssemblies $refs -CompilerOptions '/nowarn:1701'
[CS2MCP.NativeCaptureOperationProbe]::Run() | ConvertTo-Json -Depth 4 -Compress
