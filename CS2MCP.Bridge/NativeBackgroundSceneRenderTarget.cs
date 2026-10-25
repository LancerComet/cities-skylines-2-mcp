using System;
using System.Reflection;
using Game.Rendering;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

namespace CS2MCP
{
    /// <summary>仅使用本机HDRP的公开customRender事件；不改IL、不写核心、不碰他人的委托。</summary>
    internal sealed class NativeBackgroundSceneRenderTarget : IBackgroundSceneRenderTarget
    {
        private readonly Camera m_Camera;
        private readonly Component m_Data;
        private readonly EventInfo m_Event;
        private readonly PropertyInfo m_HasCustom;
        private readonly FieldInfo m_Passthrough;
        private readonly Delegate m_Handler;
        private Action m_OnRender;
        private bool m_AttachAttempted;

        public int CameraInstanceId { get; }
        public string CameraName { get; }
        public bool IsAlive => m_Camera != null && m_Data != null;
        public bool IsGameCamera => m_Camera.cameraType == CameraType.Game;
        public bool Enabled => m_Camera.enabled;
        public bool HasCustomRenderer => (bool)m_HasCustom.GetValue(m_Data);
        public bool FullscreenPassthrough => (bool)m_Passthrough.GetValue(m_Data);

        private NativeBackgroundSceneRenderTarget(Camera camera, Component data, Type type)
        {
            m_Camera = camera; m_Data = data;
            CameraInstanceId = camera.GetInstanceID(); CameraName = camera.name;
            m_Event = type.GetEvent("customRender", BindingFlags.Instance | BindingFlags.Public);
            m_HasCustom = type.GetProperty("hasCustomRender", BindingFlags.Instance | BindingFlags.Public);
            m_Passthrough = type.GetField("fullscreenPassthrough", BindingFlags.Instance | BindingFlags.Public);
            if (m_Event?.GetAddMethod() == null || m_Event.GetRemoveMethod() == null
                || m_HasCustom?.PropertyType != typeof(bool) || m_HasCustom.GetGetMethod() == null
                || m_Passthrough?.FieldType != typeof(bool))
                throw new InvalidOperationException("native_public_scene_render_api_unavailable");
            Type handler = m_Event.EventHandlerType;
            if (!handler.IsGenericType || handler.GetGenericTypeDefinition() != typeof(Action<,>))
                throw new InvalidOperationException("native_scene_render_callback_type_mismatch");
            Type[] args = handler.GetGenericArguments();
            if (args[0] != typeof(ScriptableRenderContext)
                || args[1].FullName != "UnityEngine.Rendering.HighDefinition.HDCamera")
                throw new InvalidOperationException("native_scene_render_callback_arguments_mismatch");
            var method = typeof(NativeBackgroundSceneRenderTarget).GetMethod(nameof(RenderWithoutScene), BindingFlags.Instance | BindingFlags.NonPublic);
            m_Handler = Delegate.CreateDelegate(handler, this, method.MakeGenericMethod(args[1]));
        }

        internal static IBackgroundSceneRenderTarget Resolve(World world)
        {
            Camera camera = world.GetExistingSystemManaged<CameraUpdateSystem>()?.activeCamera;
            if (camera == null) throw new InvalidOperationException("native_active_game_camera_unavailable");
            Type type = Type.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime", false);
            if (type == null) throw new InvalidOperationException("native_hdrp_camera_data_type_unavailable");
            Component data = camera.GetComponent(type);
            if (data == null) throw new InvalidOperationException("native_hdrp_camera_data_component_unavailable");
            return new NativeBackgroundSceneRenderTarget(camera, data, type);
        }

        public void InstallOwn(Action onRender)
        {
            m_OnRender = onRender ?? throw new ArgumentNullException(nameof(onRender));
            m_AttachAttempted = true;
            m_Event.AddEventHandler(m_Data, m_Handler);
        }

        public void RemoveOwn()
        {
            // 公开事件的生成remove访问器只操作托管委托。即使Unity对象已销毁也不调用其原生属性。
            if (m_AttachAttempted && !ReferenceEquals(m_Data, null)) m_Event.RemoveEventHandler(m_Data, m_Handler);
            m_AttachAttempted = false;
            m_OnRender = null;
        }

        private void RenderWithoutScene<TCamera>(ScriptableRenderContext context, TCamera camera)
        {
            // 本机HDRP在此回调之后自行Submit/EndCameraRendering；这里不重复提交，不分配或绘制。
            m_OnRender?.Invoke();
        }
    }
}
