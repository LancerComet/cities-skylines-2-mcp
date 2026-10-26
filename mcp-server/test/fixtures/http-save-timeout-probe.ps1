$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$OutputEncoding=[Console]::OutputEncoding
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
# 生产构建将SDK测试放在artifacts/sdk，C#冻结源码放在同一记录的source目录。
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'HttpBridgeServer.cs'))) {
    $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
$refs=@([IO.Directory]::GetFiles((Join-Path $PSHOME 'ref'),'*.dll'))
$refs+=Join-Path $PSHOME 'Newtonsoft.Json.dll'
# PowerShell自带Newtonsoft面向较早的.NET；宿主已加载同一程序集，仅屏蔽该已知引用版本警告。
Add-Type -Path @((Join-Path $bridgeRoot 'BridgeRequest.cs'),(Join-Path $bridgeRoot 'HttpBridgeServer.cs'),(Join-Path $bridgeRoot 'RuntimeMemory.cs'),(Join-Path $bridgeRoot 'MemoryOperationBudget.cs'),(Join-Path $bridgeRoot 'RequestMemoryAdmission.cs'),(Join-Path $PSScriptRoot 'http-save-timeout-probe.cs')) -ReferencedAssemblies $refs -CompilerOptions '/nowarn:1701'
[CS2MCP.SaveTimeoutProbe]::Run()
