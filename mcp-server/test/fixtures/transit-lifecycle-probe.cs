using System;
using System.Collections.Generic;

// 只替代原生类型/请求接收器；生命周期方法及阶段枚举直接编译生产partial。
namespace Game.Tools { public enum ApplyMode { None, Clear, Apply } }
namespace Unity.Entities { public struct Entity { public int Index,Version; public static Entity Null => default(Entity); } }
namespace CS2MCP
{
    public sealed class BridgeResponse {
        public object Payload; public int Status;
        public static BridgeResponse Json(object payload,int status=200) => new BridgeResponse {Payload=payload,Status=status};
    }
    internal sealed class RequestProbe {
        public int Replies; public BridgeResponse Response;
        public void Complete(BridgeResponse response) {Replies++;Response=response;}
    }
    internal sealed class ToolManagerProbe { public object activeTool; }
    public sealed partial class BridgeTransitToolSystem
    {
        private Stage m_Stage;
        private OperationKind m_PendingKind;
        private bool m_Applied,m_HasLineColor;
        private Unity.Entities.Entity m_ResultEntity,m_PendingPrefabEntity;
        private RequestProbe m_PendingRequest;
        private object m_PendingPrefab,m_PreviousTool,m_DefaultToolSystem;
        private object[] m_LineStops;
        private string m_LineName,m_PendingTypeName;
        private int m_StopControlPoint,m_LineColor,m_DepotCount,m_DefinitionRounds;
        private DateTime m_Deadline;
        private ToolManagerProbe m_ToolSystem;
        private Game.Tools.ApplyMode applyMode;
        private static object Field(BridgeResponse r,string name) => r.Payload.GetType().GetProperty(name).GetValue(r.Payload);
        private static void Check(bool condition,string label) {if(!condition)throw new Exception(label);}
        private static BridgeTransitToolSystem Pending(Stage stage,bool applied=false) => new BridgeTransitToolSystem {
            m_Stage=stage,m_PendingKind=OperationKind.Line,m_Applied=applied,m_HasLineColor=true,
            m_ResultEntity=new Unity.Entities.Entity {Index=987,Version=4},m_PendingPrefabEntity=new Unity.Entities.Entity {Index=9,Version=2},
            m_PendingRequest=new RequestProbe(),m_PendingPrefab=new object(),m_PreviousTool=new object(),m_DefaultToolSystem=new object(),
            m_LineStops=new object[]{new object()},m_LineName="line",m_PendingTypeName="bus",m_StopControlPoint=8,m_LineColor=9,
            m_DepotCount=2,m_DefinitionRounds=3,m_Deadline=DateTime.UtcNow,m_ToolSystem=new ToolManagerProbe {activeTool=new object()}
        };
        public static object RunLifecycleProbe()
        {
            var cases=new List<string>();
            var p=Pending(Stage.CreateDefinitions);var request=p.m_PendingRequest;object player=p.m_ToolSystem.activeTool;
            p.m_ResultEntity=Unity.Entities.Entity.Null;
            p.ReconcileToolOwnership();p.ReconcileToolOwnership();
            Check(request.Replies==1 && request.Response.Status==409 && !(bool)Field(request.Response,"mayHaveApplied"),"before update reply once");
            Check(Field(request.Response,"target")==null,"unknown target is not a zero entity");
            Check(p.m_Stage==Stage.Idle && p.m_PendingRequest==null && ReferenceEquals(player,p.m_ToolSystem.activeTool),"player tool preserved");
            cases.Add("lost_before_first_update_releases_request_once");
            Check(p.m_PendingPrefab==null && p.m_LineStops==null && p.m_LineName==null && p.m_PendingTypeName==null && p.m_PreviousTool==null && p.m_ResultEntity.Index==0 && !p.m_Applied
                && p.m_PendingPrefabEntity.Index==0 && p.m_StopControlPoint==0 && p.m_LineColor==0 && !p.m_HasLineColor
                && p.m_DepotCount==0 && p.m_DefinitionRounds==0 && p.m_Deadline==default(DateTime) && p.applyMode==Game.Tools.ApplyMode.Clear,"all own state cleared");
            cases.Add("interruption_clears_all_owned_state");
            p=Pending(Stage.WaitForPaths);request=p.m_PendingRequest;p.ReconcileToolOwnership();
            Check(!(bool)Field(request.Response,"mayHaveApplied") && !(bool)Field(request.Response,"safeToRetry") && (string)Field(request.Response,"stage")=="WaitForPaths","preapply boundary");
            cases.Add("before_apply_is_unapplied_but_not_blind_retryable");
            p=Pending(Stage.Finish,true);request=p.m_PendingRequest;p.ReconcileToolOwnership();
            Check((bool)Field(request.Response,"mayHaveApplied") && (string)Field(request.Response,"failurePhase")=="tool_interrupted","submitted boundary");
            object target=Field(request.Response,"target");Check((int)target.GetType().GetProperty("index").GetValue(target)==987,"target snapshot survives clear");
            cases.Add("submitted_interruption_keeps_unknown_and_target");
            p=Pending(Stage.Apply);p.m_ToolSystem.activeTool=p;request=p.m_PendingRequest;p.ReconcileToolOwnership();
            Check(p.m_Stage==Stage.Apply && request.Replies==0 && p.m_PendingRequest!=null,"active tool untouched");
            cases.Add("owned_active_operation_is_not_interrupted");
            object previous=p.m_PreviousTool;p.CompletePending(BridgeResponse.Json(new {ok=true}));p.Deactivate();
            Check(request.Replies==1 && ReferenceEquals(previous,p.m_ToolSystem.activeTool) && p.m_PreviousTool==null,"normal restore");
            cases.Add("normal_finish_restores_previous_once");
            p=Pending(Stage.Finish);p.m_ToolSystem.activeTool=p;p.m_PreviousTool=p;var fallback=p.m_DefaultToolSystem;p.Deactivate();
            Check(ReferenceEquals(fallback,p.m_ToolSystem.activeTool),"self fallback");cases.Add("previous_self_uses_default_tool");
            p=Pending(Stage.Finish);var unknown=p.TransitFailure(409,"unverified","transit_commit_unverified",true);p.Deactivate();
            Check((bool)Field(unknown,"mayHaveApplied") && !(bool)Field(unknown,"safeToRetry") && (string)Field(unknown,"failurePhase")=="commit_readback","unverified not false");
            cases.Add("unverified_commit_forces_unknown_in_snapshot");
            return new {passed=cases.Count,cases,nativeToolUpdateAndCityAcceptanceVerified=false};
        }
    }
}
