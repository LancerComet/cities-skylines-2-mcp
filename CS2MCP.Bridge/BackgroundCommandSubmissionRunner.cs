using System;
using UnityEngine;

namespace CS2MCP
{
    /// <summary>在Unity LateUpdate而非ECS请求中提交；仅拥有自己的运行组件。</summary>
    internal sealed class BackgroundCommandSubmissionRunner : MonoBehaviour
    {
        private static BackgroundCommandSubmissionRunner s_Instance;
        private static readonly Action s_Submit = GL.Flush;
        private BackgroundCommandSubmission m_Policy;
        private Func<bool> m_Ready;

        internal static void Bind(BackgroundCommandSubmission policy, Func<bool> ready)
        {
            if (s_Instance != null && s_Instance.m_Policy != null && !ReferenceEquals(s_Instance.m_Policy, policy))
                throw new InvalidOperationException("native_command_submission_owned_by_another_world");
            if (s_Instance == null) {
                var host = new GameObject("CS2MCP.BackgroundCommandSubmission") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(host);
                s_Instance = host.AddComponent<BackgroundCommandSubmissionRunner>();
            }
            s_Instance.m_Policy = policy; s_Instance.m_Ready = ready; s_Instance.enabled = true;
        }

        internal static void Release(BackgroundCommandSubmission policy)
        {
            if (s_Instance == null || !ReferenceEquals(s_Instance.m_Policy, policy)) return;
            s_Instance.m_Policy = null; s_Instance.m_Ready = null;
            var host = s_Instance.gameObject; s_Instance = null;
            Destroy(host);
        }

        private void LateUpdate()
        {
            if (m_Policy == null || m_Ready == null) return;
            try { m_Policy.Tick(Time.frameCount, m_Ready(), s_Submit); }
            catch (Exception e) { m_Policy.SetupFailed(e); }
            if (m_Policy.Failure != null) enabled = false;
        }
    }
}
