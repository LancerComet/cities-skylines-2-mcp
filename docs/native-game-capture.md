# 正常游戏相机取景

0.10.5使用显式 `cs2_screenshot(source="gameCamera", width=1280)`启动一次取景并立即返回captureId。随后用`cs2_screenshot(source="gameCamera", captureId="原32位编号")`领取状态或PNG，不再渲染。领取不能同时指定width。它用于隐藏窗口下的道路、桥梁和完整设施实景核验；默认调用仍读取已完成的屏幕游戏帧，不静默改变旧行为。

本机游戏的HDRP没有实现通用`StandardRequest`处理，不能仅凭新版Unity文档宣称可用。实现参考同机`Game.UI.ScreenCaptureHelper.CaptureScreenshot`实际使用的公开相机路径：先完成原生灯光作业，在帧末、离开ECS请求和作业调度阶段，用当前游戏相机向本次临时目标执行一次`Camera.Render`，再沿正常预览合成着色器编码PNG。不执行该预览帮助器的八次渲染，不保存预览、不写城市实体或触发建造工具。

输出仅含当前游戏相机的场景。它不读取桌面，不包含原生UI面板，也不创建额外相机。既有工具的地图覆盖效果仍可能出现在场景中；图片必须实际查看，不能以PNG返回代替几何、净空、车道和运营验收。

- 仅正常城市场景就绪、原生游戏相机有效且可核资源余量时接受。宽度为64—3840整数，实际相机纵横比必须有效；高度不超过2160，像素总数不超过3840×2160。
- 若后台场景跳过绘制正在使用自有公开委托，只在这次同步取景内撤下自己的委托，结束或异常时恢复；不清空外来绘制。发现外来委托、相机切换或归属不明时拒绝，不覆盖，也不自动重试。
- 临时场景/输出纹理、本次像素和合成材质在成功和失败路径均释放或排队销毁；恢复原相机目标、forceIntoRenderTexture及全局当前目标。不改窗口可见性、分辨率、原玩家画质、模拟速度或渲染上限。
- `cs2_graphics_settings.nativeGameCapture`记录请求、编码数、实际Render调用数及清理/恢复状态；后台委托的`lastCaptureRestoreComplete`独立回读。编码成功、资源释放排队、实机图像有效、长期内存和瓦数分别验收。
- gameCamera及captureId需要0.10.5；显式currentFrame仍最低0.10.5。旧桥接在发送新请求前拒绝，不静默忽略参数。非法source/宽度在SDK及原生侧拒绝。旧默认调用仍可使用旧桥接。

本功能是按需的短时取景，不代表后台持续画场景，不证明内存增长或退出崩溃根因已修复。保存的`includePreview=false`链保持原状。

公开API参照：[Unity 2022.3 Camera.SubmitRenderRequest](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera.SubmitRenderRequest.html)。请求类型的通用描述不能替代本机HDRP实际实现及实机结果。

103同步入口已在本机实测触发截图及后续主线程查询超时，未通过实景验收。104立即返回queued编号，帧末再核同城GUID、相机及资源才开始；30秒未观测帧末则失败且不在迟到帧里再渲染。running不擅自取消或另启一次。结果领取只访问托管状态/字节，可在监听线程响应，避免再次等游戏更新；最多保留一份32MiB PNG，下一次显式取景或加载/退出清理。长取景中的模拟和资源状态仍须另验，不能由状态接口可达推导游戏已健康。
