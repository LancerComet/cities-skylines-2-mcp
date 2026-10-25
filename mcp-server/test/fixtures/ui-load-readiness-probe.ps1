$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../CS2MCP.Bridge'))
if(-not (Test-Path -LiteralPath (Join-Path $bridgeRoot 'UiLoadReadinessPolicy.cs'))) {
 $bridgeRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../source/CS2MCP.Bridge'))
}
$policy=[IO.File]::ReadAllText((Join-Path $bridgeRoot 'UiLoadReadinessPolicy.cs'))
Add-Type -TypeDefinition ($policy + @'
namespace CS2MCP {
 public static class UiLoadReadinessProbe {
  public static int Run() {
   if (!UiLoadReadinessPolicy.IsReady("None",1f,1f,1f)) throw new System.Exception("completed");
   if (UiLoadReadinessPolicy.IsReady("Loading",1f,1f,1f)) throw new System.Exception("overlay-still-loading");
   if (UiLoadReadinessPolicy.IsReady("Error",1f,1f,1f)) throw new System.Exception("other-overlay");
   if (UiLoadReadinessPolicy.IsReady(null,1f,1f,1f)) throw new System.Exception("unknown-overlay");
   if (UiLoadReadinessPolicy.IsReady("None",.999f,1f,1f)) throw new System.Exception("group1-pending");
   if (UiLoadReadinessPolicy.IsReady("None",1f,.999f,1f)) throw new System.Exception("group2-pending");
   if (UiLoadReadinessPolicy.IsReady("None",1f,1f,.999f)) throw new System.Exception("group3-pending");
   if (UiLoadReadinessPolicy.IsReady("None",1f,1f,float.NaN)) throw new System.Exception("unknown-progress");
   if (UiLoadReadinessPolicy.IsReady("None",1f,float.PositiveInfinity,1f)) throw new System.Exception("invalid-progress");
   return 9;
  }
 }
}
'@)
@{passed=[CS2MCP.UiLoadReadinessProbe]::Run();nativeLoadOrPauseBarrierBypassed=$false} | ConvertTo-Json -Compress
