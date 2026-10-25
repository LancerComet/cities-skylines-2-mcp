using System;
using System.Collections.Generic;
using Game.Settings;

// 原生适配替身只模拟设置对象和渲染组件。被验证的策略直接编译生产C#源码；不把此测试当GPU验收。
namespace Game.Settings
{
    public class Parameter<T> { public T value; public bool overrideState; public Parameter(T v) { value=v; } }
    public class NativeFeatures {
        public Parameter<bool> enable=new Parameter<bool>(true), shadows=new Parameter<bool>(true),
            castShadows=new Parameter<bool>(true), enabled=new Parameter<bool>(true), enabledTransparent=new Parameter<bool>(true);
        public Parameter<int> cloudType=new Parameter<int>(1);
        public Parameter<float> intensity=new Parameter<float>(1f);
        public NativeFeatures layerA;
        public bool active=true;
    }
    public abstract class QualitySetting {
        public enum Level { Disabled, VeryLow, Low, Medium, High, Colossal, Custom }
        public bool enabled=true;
        public int customValue=37;
        public int setCalls;
        public static readonly Dictionary<Type,int> Calls=new Dictionary<Type,int>();
        public static readonly Dictionary<Type,int> LastValue=new Dictionary<Type,int>();
        public static Type FailDisabled, FailRestore;
        public virtual Level GetLevel() { return enabled?Level.Custom:Level.Disabled; }
        public virtual IEnumerable<Level> EnumerateAvailableLevels() { return new[]{Level.Disabled,Level.Low,Level.Custom}; }
        public virtual void TransferValuesFrom(QualitySetting other) { enabled=other.enabled; customValue=other.customValue; }
        public virtual void SetLevel(Level level,bool apply=true) { setCalls++; enabled=level!=Level.Disabled; if(apply)throw new Exception("persistent_apply_forbidden"); }
        public virtual void Apply() {
            Type t=GetType();Calls[t]=Calls.ContainsKey(t)?Calls[t]+1:1;LastValue[t]=customValue;
            UpdateNative();
            if ((!enabled && FailDisabled==t) || (enabled && FailRestore==t)) throw new InvalidOperationException("native_partial_apply");
        }
        protected abstract void UpdateNative();
        public static void ClearCounters() { Calls.Clear();LastValue.Clear();FailDisabled=FailRestore=null; }
    }
    public class CloudsQualitySettings:QualitySetting {
        private static NativeFeatures m_VolumetricClouds=new NativeFeatures(),m_VisualEnvironment=new NativeFeatures(),
            m_CloudLayer=new NativeFeatures { layerA=new NativeFeatures() };
        protected override void UpdateNative() {
            if(m_VolumetricClouds==null)return;
            m_VolumetricClouds.enable.value=enabled;m_VolumetricClouds.shadows.value=enabled;
            m_VisualEnvironment.cloudType.value=enabled?1:0;m_CloudLayer.layerA.castShadows.value=enabled;
        }
        public static void SetAvailable(bool available) { m_VolumetricClouds=available?new NativeFeatures():null; }
    }
    public class ShadowsQualitySettings:QualitySetting {
        private static NativeFeatures m_CascadeShadows=new NativeFeatures();
        protected override void UpdateNative() { m_CascadeShadows.active=enabled; }
    }
    public class SSAOQualitySettings:QualitySetting {
        private static NativeFeatures m_AOComponent=new NativeFeatures();
        protected override void UpdateNative() { m_AOComponent.intensity.value=enabled?1:0;m_AOComponent.intensity.overrideState=!enabled; }
    }
    public class SSGIQualitySettings:QualitySetting {
        private static NativeFeatures m_SSGIComponent=new NativeFeatures();
        protected override void UpdateNative() { m_SSGIComponent.enable.value=enabled; }
    }
    public class SSRQualitySettings:QualitySetting {
        private static NativeFeatures m_SSRComponent=new NativeFeatures();
        protected override void UpdateNative() { m_SSRComponent.enabled.value=enabled;m_SSRComponent.enabledTransparent.value=enabled; }
    }
    public class GraphicsSettings {
        public readonly Dictionary<Type,QualitySetting> Items=new Dictionary<Type,QualitySetting>();
        public GraphicsSettings() {
            foreach(var q in new QualitySetting[]{new CloudsQualitySettings(),new ShadowsQualitySettings(),new SSAOQualitySettings(),new SSGIQualitySettings(),new SSRQualitySettings()})Items[q.GetType()]=q;
        }
        public QualitySetting GetQualitySetting(Type t) { QualitySetting q;return Items.TryGetValue(t,out q)?q:null; }
        public void ApplyAll() { foreach(var q in Items.Values)q.Apply(); }
    }
}
namespace CS2MCP
{
    public static class BackgroundRenderingProbe
    {
        static object Field(object state,string name) { return state.GetType().GetProperty(name).GetValue(state); }
        static void Check(bool value,string name) { if(!value)throw new Exception(name); }
        static GraphicsSettings Fresh() {
            CloudsQualitySettings.SetAvailable(true);QualitySetting.ClearCounters();var s=new GraphicsSettings();s.ApplyAll();QualitySetting.ClearCounters();return s;
        }
        static int Total() { int n=0;foreach(int x in QualitySetting.Calls.Values)n+=x;return n; }
        public static object Run() {
            var passed=new List<string>();
            var s=Fresh();var policy=new BackgroundRenderingPolicy();
            var originals=new List<QualitySetting>(s.Items.Values);
            policy.Apply(s);
            Check(Total()==5,"five_native_detached_applies");
            foreach(var q in originals)Check(q.enabled&&q.customValue==37&&q.setCalls==0,"player_values_untouched");
            Check((bool)Field(policy.Read(s,true),"allSelectedEffectsVerifiedOff"),"actual_native_fields_off");
            passed.Add("独立原生Apply与玩家对象不变");
            for(int i=0;i<20;i++){policy.Apply(s);policy.Read(s,true);}
            Check(Total()==5,"no_per_frame_or_read_reapply");passed.Add("重复处理和只读不再应用");
            s.GetQualitySetting(typeof(ShadowsQualitySettings)).customValue=91;
            policy.Restore(s);Check(Total()==10,"restore_once");
            Check(QualitySetting.LastValue[typeof(ShadowsQualitySettings)]==91,"restore_latest_player_value");
            foreach(var q in originals)Check(q.enabled&&q.setCalls==0,"source_untouched_after_restore");
            passed.Add("前台恢复当前玩家值");
            policy.ResetAfterLoad();s.ApplyAll();QualitySetting.ClearCounters();policy.Apply(s);
            Check(Total()==5&&(bool)Field(policy.Read(s,true),"allSelectedEffectsVerifiedOff"),"fresh_load_reapplies_once");passed.Add("新加载重置后应用一次");
            var nextSettings=new GraphicsSettings();nextSettings.GetQualitySetting(typeof(ShadowsQualitySettings)).customValue=103;
            QualitySetting.ClearCounters();policy.ResetAfterLoad();policy.Restore(nextSettings);
            Check(Total()==5&&QualitySetting.LastValue[typeof(ShadowsQualitySettings)]==103,"foreground_during_loading_restores_new_settings");
            passed.Add("加载中改前台在就绪后恢复新配置");
            s=Fresh();policy=new BackgroundRenderingPolicy();s.Items.Remove(typeof(SSGIQualitySettings));policy.Apply(s);
            Check(Total()==0&&Field(policy.Read(s,true),"failure")!=null,"preflight_failure_zero_apply");passed.Add("完整预检失败零应用");
            s=Fresh();policy=new BackgroundRenderingPolicy();QualitySetting.FailDisabled=typeof(SSRQualitySettings);policy.Apply(s);
            var failed=policy.Read(s,true);Check(!(bool)Field(failed,"nativeApplyCompleted")&&(bool)Field(failed,"rollbackComplete"),"partial_failure_rollback");
            Check(Total()==10,"failed_step_also_rolled_back");int afterFailure=Total();for(int i=0;i<20;i++)policy.Apply(s);Check(Total()==afterFailure,"failed_no_auto_retry");
            passed.Add("原生部分失败包含当前步恢复且不重试");
            s=Fresh();policy=new BackgroundRenderingPolicy();QualitySetting.FailDisabled=typeof(SSRQualitySettings);QualitySetting.FailRestore=typeof(CloudsQualitySettings);policy.Apply(s);
            Check(!(bool)Field(policy.Read(s,true),"rollbackComplete"),"rollback_failure_visible");passed.Add("恢复失败不伪装成功");
            s=Fresh();policy=new BackgroundRenderingPolicy();CloudsQualitySettings.SetAvailable(false);policy.Apply(s);
            var unknown=policy.Read(s,true);Check(!(bool)Field(unknown,"nativeApplyCompleted")&&!(bool)Field(unknown,"allSelectedEffectsVerifiedOff")&&Total()==0,"missing_native_evidence_not_success");
            passed.Add("缺少实际组件不冒充已验证");
            CloudsQualitySettings.SetAvailable(true);QualitySetting.ClearCounters();
            return new {passed=passed.Count,cases=passed,nativeRendererAndWattsVerified=false};
        }
    }
}
