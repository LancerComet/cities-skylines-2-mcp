using System;
using System.Collections;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>在帧末执行公开原相机渲染；不占用前台，不复制相机或逐帧创建图片。</summary>
    internal sealed class BackgroundSceneFrameRunner : MonoBehaviour
    {
        private static BackgroundSceneFrameRunner s_Instance;
        private static readonly WaitForEndOfFrame s_EndOfFrame = new WaitForEndOfFrame();
        private BackgroundSceneFrameLoop m_Policy;
        private Func<bool> m_Ready;
        private Func<int, bool> m_Render;
        private Action m_Release;
        private Coroutine m_Loop;

        internal static void Bind(BackgroundSceneFrameLoop policy, Func<bool> ready, Func<int, bool> render, Action release)
        {
            if (s_Instance != null && s_Instance.m_Policy != null && !ReferenceEquals(s_Instance.m_Policy, policy))
                throw new InvalidOperationException("native_scene_frame_owned_by_another_world");
            if (s_Instance == null) {
                var host = new GameObject("CS2MCP.BackgroundSceneFrames") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host); s_Instance = host.AddComponent<BackgroundSceneFrameRunner>();
            }
            s_Instance.m_Policy = policy; s_Instance.m_Ready = ready; s_Instance.m_Render = render; s_Instance.m_Release = release;
            if (s_Instance.m_Loop == null) s_Instance.m_Loop = s_Instance.StartCoroutine(s_Instance.Run());
        }

        internal static void Release(BackgroundSceneFrameLoop policy)
        {
            if (s_Instance == null || !ReferenceEquals(s_Instance.m_Policy, policy)) return;
            var instance = s_Instance; s_Instance = null;
            if (instance.m_Loop != null) instance.StopCoroutine(instance.m_Loop);
            instance.m_Loop = null; instance.m_Policy = null; instance.m_Ready = null; instance.m_Render = null; instance.m_Release = null;
            Destroy(instance.gameObject);
        }

        private IEnumerator Run()
        {
            while (m_Policy != null) {
                yield return s_EndOfFrame;
                if (m_Policy == null) yield break;
                try { m_Policy.Tick(Time.frameCount, m_Ready(), m_Render, m_Release); }
                catch (Exception e) { m_Policy.FailAndRelease(e, m_Release); }
                if (m_Policy.Failure != null) { m_Loop = null; yield break; }
            }
            m_Loop = null;
        }
    }
}
