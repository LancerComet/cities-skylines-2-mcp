using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CS2MCP
{
    public static class MemoryPressureProbe
    {
        public static string Run()
        {
            var cases = new List<string>();
            Action<bool,string> check = (ok,name) => { if (!ok) throw new Exception(name); cases.Add(name); };
            const ulong MiB = 1024UL * 1024, GiB = 1024UL * MiB;
            Func<ulong, RuntimeMemory.Snapshot> sample = available =>
            {
                var s = new RuntimeMemory.Snapshot();
                s.SetSystemCommit(50151120896, 50151120896 - available);
                s.SetProcessAvailability(50151120896, available, 1000 * GiB);
                s.privateMemoryBytes = 22427668480; s.Recalculate(); return s;
            };
            var actualOld = sample(4594065408);
            check(actualOld.constructionAllowed && actualOld.pressureLevel == "warning", "本城22.43GB/4.59GB真实旧误拒样本放行有界操作");
            var privateLarge = sample(4 * GiB); privateLarge.privateMemoryBytes = 40L * (long)GiB; privateLarge.Recalculate();
            check(privateLarge.constructionAllowed && privateLarge.privateCommitCeilingBytes == null, "大私有提交不因65%份额拒绝");
            var small = sample(128 * MiB); small.physicalMemoryLow = true; small.Recalculate();
            var one = new BridgeRequest { Path = "/build/road-layout", Body = "{\"segments\":[{}]}" };
            var manyRows = new JArray(); for (int i=0;i<250;i++) manyRows.Add(new JObject());
            var many = new BridgeRequest { Path = one.Path, Body = new JObject { ["segments"] = manyRows }.ToString(Formatting.None) };
            var oneBudget = RequestMemoryAdmission.Estimate(one); var manyBudget = RequestMemoryAdmission.Estimate(many);
            check(oneBudget.estimatedAdditionalBytes < manyBudget.estimatedAdditionalBytes, "真实道路JSON规模参与预算");
            check(RequestMemoryAdmission.Check(small,oneBudget,false)==null, "低物理压力但有提交容量允许小批");
            var rejected = RequestMemoryAdmission.Check(small,manyBudget,false);
            var payload = JObject.Parse(System.Text.Encoding.UTF8.GetString(rejected.Body));
            check(rejected.Status==409 && (string)payload["reason"]=="operation_commit_capacity", "同容量大批给出操作容量原因");
            check((bool)payload["enqueued"]==false && (bool)payload["mayHaveApplied"]==false && (string)payload["failurePhase"]=="prequeue", "入队前拒绝不冒充已应用");
            check((bool)payload["admission"]["preserveCurrentInstance"] && !(bool)payload["admission"]["automaticRestartRecommended"] && !(bool)payload["admission"]["automaticPauseRecommended"], "拒绝仍保留游戏且不自动退出重启暂停");
            rejected = RequestMemoryAdmission.Check(small,manyBudget,true); payload = JObject.Parse(System.Text.Encoding.UTF8.GetString(rejected.Body));
            check((bool)payload["enqueued"] && !(bool)payload["handlerStarted"] && (string)payload["failurePhase"]=="before_handler", "主线程复核保持已排队但未开始语义");
            var removal = new BridgeRequest { Path="/build/road-layout/remove", Body=new JObject { ["roads"]=manyRows }.ToString(Formatting.None) };
            check(RequestMemoryAdmission.Check(small,RequestMemoryAdmission.Estimate(removal),false)==null,"可释放资源的删除不套建造大批预算");
            foreach(var path in new[]{"/game/save","/sim/control","/diagnostics/resources","/build/cancel"})
                check(RequestMemoryAdmission.Estimate(new BridgeRequest{Path=path})==null,"控制读取保存取消保持可用:"+path);
            var cancel = new BridgeRequest{Path="/build/mod/action"}; cancel.Query["action"]="cancel";
            check(RequestMemoryAdmission.Estimate(cancel)==null,"模组取消保持可用");
            var bad = new BridgeRequest{Path=one.Path,Body="not json"};
            check(RequestMemoryAdmission.Estimate(bad)==null,"无效JSON交正常处理器校验");
            manyRows.Add(new JObject());many.Body=new JObject{["segments"]=manyRows}.ToString(Formatting.None);
            check(RequestMemoryAdmission.Estimate(many)==null,"超原生批量上限交400校验");
            check(MemoryOperationBudget.Capture(3840,2160).estimatedAdditionalBytes > MemoryOperationBudget.Capture(1280,720).estimatedAdditionalBytes,"取景按真实像素规模分流");
            check(!sample(0).constructionAllowed,"实际无可提交容量不放行分配");
            var noCounters = new RuntimeMemory.Snapshot(); noCounters.Recalculate();
            check(!noCounters.constructionAllowed && noCounters.availableCommitBytes==null,"计数全未知保留未知而非零");
            var constrained=sample(4*GiB);constrained.SetProcessAvailability(8*GiB,16*MiB,1000*GiB);constrained.Recalculate();
            check(!constrained.constructionAllowed && constrained.systemAvailableCommitBytes==4*GiB && constrained.availableCommitBytes==16*MiB,"系统与进程可提交分开且遵守真实较小额度");
            var fallback=new RuntimeMemory.Snapshot(); fallback.SetSystemCommit(10*GiB,8*GiB);fallback.Recalculate();
            check(fallback.constructionAllowed && fallback.privateMemoryBytes==null,"私有计数未知不丢有效系统容量");
            return JsonConvert.SerializeObject(new{passed=cases.Count,cases,productionSourcesLinked=true,gameStarted=false,systemSettingsChanged=false});
        }
    }
}
