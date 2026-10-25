using System;
using System.Collections.Generic;
using System.Reflection;
using Game.Settings;

namespace CS2MCP
{
    /// <summary>只用独立原生画质对象改变后台渲染；玩家配置和正常保存的数据保持原样。</summary>
    internal sealed class BackgroundRenderingPolicy
    {
        private static readonly Type[] Targets = {
            typeof(CloudsQualitySettings), typeof(ShadowsQualitySettings),
            typeof(SSAOQualitySettings), typeof(SSGIQualitySettings), typeof(SSRQualitySettings)
        };
        private readonly List<Type> m_Touched = new List<Type>();
        private bool m_Attempted, m_Applied;
        private bool? m_RollbackComplete;
        private string m_Failure;

        // 不带入前一世界的实例或应用标志；仅保留待恢复的类型，覆盖加载中切前台的情况。
        internal void ResetAfterLoad()
        {
            m_Attempted = m_Applied = false;
            m_RollbackComplete = null;
            m_Failure = null;
        }

        internal void Apply(GraphicsSettings settings)
        {
            if (m_Attempted) return;
            m_Attempted = true;
            try {
                var pending = new List<QualitySetting>();
                // 先核完整集合与正常档位；预检失败不能留下部分效果。
                foreach (Type type in Targets) {
                    QualitySetting original = settings?.GetQualitySetting(type);
                    if (original == null) throw new InvalidOperationException("native_quality_unavailable:" + type.Name);
                    bool supported = false;
                    foreach (QualitySetting.Level level in original.EnumerateAvailableLevels())
                        if (level == QualitySetting.Level.Disabled) supported = true;
                    if (!supported) throw new InvalidOperationException("native_disabled_level_unavailable:" + type.Name);
                    if (original.GetLevel() == QualitySetting.Level.Disabled) continue;
                    var detached = (QualitySetting)Activator.CreateInstance(type);
                    detached.TransferValuesFrom(original);
                    detached.SetLevel(QualitySetting.Level.Disabled, apply: false);
                    if (detached.GetLevel() != QualitySetting.Level.Disabled)
                        throw new InvalidOperationException("native_disabled_level_not_selected:" + type.Name);
                    pending.Add(detached);
                }
                foreach (object value in NativeValues())
                    if (value == null) throw new InvalidOperationException("native_render_components_unavailable");
                foreach (QualitySetting detached in pending) {
                    // 先记录尝试：Apply抛错时也可能已改变部分原生组件。
                    if (!m_Touched.Contains(detached.GetType())) m_Touched.Add(detached.GetType());
                    detached.Apply();
                }
                m_Applied = true;
            } catch (Exception error) {
                m_Failure = error.GetType().Name + ":" + error.Message;
                m_RollbackComplete = RestoreTouched(settings);
                m_Applied = false;
            }
        }

        internal void Restore(GraphicsSettings settings)
        {
            if (m_Touched.Count == 0) { ResetAfterLoad(); return; }
            bool restored = RestoreTouched(settings);
            if (restored) { m_Touched.Clear(); ResetAfterLoad(); }
            else {
                m_Applied = false;
                m_RollbackComplete = false;
                m_Failure = "native_quality_restore_incomplete";
            }
        }

        private bool RestoreTouched(GraphicsSettings settings)
        {
            bool complete = true;
            foreach (Type type in m_Touched) {
                try {
                    // 使用当前玩家对象，保留用户在后台期间改过的值；不回放旧快照。
                    QualitySetting original = settings?.GetQualitySetting(type);
                    if (original == null) { complete = false; continue; }
                    original.Apply();
                } catch { complete = false; }
            }
            return complete;
        }

        private static object NativeValue(Type type, string field, params string[] members)
        {
            try {
                object value = type.GetField(field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                foreach (string member in members) {
                    if (value == null) return null;
                    Type current = value.GetType();
                    var property = current.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
                    value = property != null ? property.GetValue(value)
                        : current.GetField(member, BindingFlags.Public | BindingFlags.Instance)?.GetValue(value);
                }
                if (value is bool || value is int || value is float || value is double) return value;
                return value?.GetType().IsEnum == true ? value.ToString() : null;
            } catch { return null; }
        }

        private static object[] NativeValues()
        {
            return new[] {
                NativeValue(typeof(CloudsQualitySettings), "m_VolumetricClouds", "enable", "value"),
                NativeValue(typeof(CloudsQualitySettings), "m_VisualEnvironment", "cloudType", "value"),
                NativeValue(typeof(CloudsQualitySettings), "m_VolumetricClouds", "shadows", "value"),
                NativeValue(typeof(CloudsQualitySettings), "m_CloudLayer", "layerA", "castShadows", "value"),
                NativeValue(typeof(ShadowsQualitySettings), "m_CascadeShadows", "active"),
                NativeValue(typeof(SSAOQualitySettings), "m_AOComponent", "intensity", "value"),
                NativeValue(typeof(SSAOQualitySettings), "m_AOComponent", "intensity", "overrideState"),
                NativeValue(typeof(SSGIQualitySettings), "m_SSGIComponent", "enable", "value"),
                NativeValue(typeof(SSRQualitySettings), "m_SSRComponent", "enabled", "value"),
                NativeValue(typeof(SSRQualitySettings), "m_SSRComponent", "enabledTransparent", "value")
            };
        }

        internal object Read(GraphicsSettings settings, bool requested)
        {
            var levels = new List<object>();
            foreach (Type type in Targets) {
                QualitySetting configured = settings?.GetQualitySetting(type);
                levels.Add(new { name = type.Name, configuredLevel = configured?.GetLevel().ToString() });
            }
            object[] values = NativeValues();
            object clouds = values[0], distant = values[1], cloudShadows = values[2], distantShadows = values[3],
                cascades = values[4], ao = values[5], aoOverride = values[6], gi = values[7], reflections = values[8], transparent = values[9];
            bool zeroAo = ao is float f && f == 0 || ao is double d && d == 0 || ao is int n && n == 0;
            bool observedOff = Equals(clouds, false) && Equals(distant, 0) && Equals(cloudShadows, false)
                && Equals(distantShadows, false) && Equals(cascades, false)
                && zeroAo && Equals(aoOverride, true)
                && Equals(gi, false) && Equals(reflections, false) && Equals(transparent, false);
            return new {
                requested, attempted = m_Attempted, nativeApplyCompleted = m_Applied,
                failure = m_Failure, rollbackComplete = m_RollbackComplete, configuredLevels = levels,
                nativeEffects = new { volumetricClouds = clouds, distanceCloudType = distant,
                    volumetricCloudShadows = cloudShadows, distanceCloudShadows = distantShadows,
                    shadowCascadesActive = cascades, ambientOcclusionIntensity = ao,
                    ambientOcclusionIntensityOverride = aoOverride, globalIllumination = gi,
                    screenSpaceReflections = reflections, transparentReflections = transparent },
                allSelectedEffectsVerifiedOff = requested && m_Applied && observedOff,
                playerQualityObjectsModified = false, persistentQualityWritten = false,
                actualPowerWatts = (double?)null,
                note = "独立原生画质对象临时Apply；原玩家配置保留。实际组件null表示未知；低渲染负担不等于已测功耗，也不证明内存增长或退出崩溃已修复。"
            };
        }
    }
}
