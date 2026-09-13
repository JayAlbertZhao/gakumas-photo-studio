# 自主天空与 HDR 环境输入

`SceneSkySettings`、`SceneSkyMaterial`、`SceneSkyCamera`、`SceneSkyCapture` 是独立的 Built-in 工具模块，默认不启用，也不接管 Photo Studio 的旧背景。无需角色、私有配置或原版天空贴图。

PPT 第 109 页列出了天空等专用 shader 的用途，但没有公开天空算法。这里提供自己的渐变、纹理和太阳盘定义；不宣称恢复原版大气、云层或环境参数。

## 相机接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

// 相机及其 clear flags 由宿主拥有；组件不会替你改动这些设置。
camera.clearFlags = CameraClearFlags.Skybox;
var sky = camera.gameObject.AddComponent<SceneSkyCamera>();
sky.settings.enabled = true;
sky.settings.zenith = new Vector3(.12f, .3f, 1.2f);
sky.settings.horizon = new Vector3(.7f, .8f, 1f);
sky.settings.ground = new Vector3(.05f, .04f, .03f);
sky.settings.sunEnabled = true;
sky.settings.sunDirection = new Vector3(.3f, .8f, .2f);
// 可立即准备；之后 OnPreCull 会读取当前配置。
if (!sky.TryApply()) Debug.LogWarning(sky.UnavailableReason);
```

模块使用原生 `UnityEngine.Skybox` 材质绑定，不写 `RenderSettings`、全局 shader 参数或 GI。天空位于远深度、不写深度，已写入深度的不透明／Cutout 前景保留。普通屏幕或 RenderTexture 的实际画质需在各自宿主中验证。

当前契约是 Built-in、非 XR、完整视口、普通或偏轴投影与刚性相机视图；不支持 URP/HDRP、自定义投影剪切或局部视口。透视射线按真实像素中心计算；正交天空使用平行视线。原生天空网格只负责覆盖，避免其顶点栅格化误差进入小太阳盘和经纬图接缝坐标。

`enabled=false`、无效输入和组件禁用都会解除绑定并释放自己的材质。已有 Skybox 的材质与启用状态会恢复；检测到外部替换材质／禁用后，自动相机绘制不会再抢回绑定。显式 `TryApply()` 或组件禁用后再启用可以重新申请。组件自行创建的 Skybox 在解除时保持空材质、禁用，直到控制组件销毁；这样同帧开关不会与 Unity 的延迟 `Destroy` 冲突。外部已接管的组件不会被删除。

已有自己绑定流程的宿主可直接使用 `SceneSkyMaterial.TryUpdate(settings, out material)`。材质是借用结果，有效至更新失败或 `Dispose`；相机契约仍须由宿主遵守。不要释放输入纹理，也不要将该材质的生命周期交给别的模块。

## 自有输入

所有 RGB 为线性辐射值；`Vector3` 不承担 UI sRGB 转换。渐变以源空间 Y 为轴，从 horizon 朝 zenith／ground 插值，权重为 `abs(y)^gradientPower`。`rotation` 旋转整个源，包括太阳；`sunDirection` 位于源空间，半径为角度，软边为盘内的平滑过渡。随后逐通道乘 tint 与 `2^exposure`，输出限制在 RGBAHalf 的有限非负范围，alpha 为 1。

- `Gradient` 不需要纹理。
- `Cubemap` 借用线性 Cube 纹理，支持普通 Cubemap 和已创建的单采样 Cube RenderTexture。
- `Equirectangular` 借用线性 2D 纹理，要求 Repeat U、Clamp V；模块不会修改共享采样器。+Z 对应 U=0.5，+X 对应 U=0.75，北极 V=1。

`sourceMip` 必须位于已有 mip 链中；纹理过滤方式由输入决定。sRGB 纹理、错误维度、未创建／丢失或动态缩放的 RenderTexture 会拒绝。输入纹理应提供有限线性像素，不自动猜测 RGBM、曝光编码或原版资源通道。

## 生成反射用 Cube

保持一个长寿命 `SceneSkyCapture`，在内容变化时显式更新；不要在每次相机绘制时无条件重建。

```csharp
var capture = new SceneSkyCapture(); // 由宿主保存并管理生命周期。
if (capture.TryCapture(sky.settings, 128, true, out var frame))
{
    // 两个已有消费者直接读取实际 GPU 输出，无 CPU 回读或纹理复制。
    reflectionReceiver.probe = frame.radiance;
    reflectionReceiver.probeMaximumMip = frame.radiance.mipmapCount - 1;
    waterSurface.reflectionCapture = frame.radiance;
    waterSurface.probeMaximumMip = frame.radiance.mipmapCount - 1;
}
else Debug.LogWarning(capture.UnavailableReason);
```

生产者须至少存活到消费者绘制结束。绘制前检查 `frame.IsCurrent`；更新失败或旧代失效时解除对应引用。宿主结束时再执行清理：

```csharp
reflectionReceiver.probe = null;
waterSurface.reflectionCapture = null;
capture.Dispose();
```

实际调用原生 `Camera.RenderToCubemap(..., 63)`，以专属禁用相机、零场景层生成六面 RGBAHalf 纹理，可选 GPU mip；不收集场景几何或触发烘焙。原生接口的六面选择与失败返回见 [Unity RenderToCubemap API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera.RenderToCubemap.html)。

尺寸为 4–2048 的二次幂。64² 完整 mip 链仅颜色约 256 KiB，128² 约 1 MiB；深度、原生临时目标与驱动开销另外计算，2048² 颜色约 256 MiB。失败、禁用或释放会清空自己的资源。`Frame.IsCurrent` 只表示最近一次成功生产的当前代；下一次尝试、重建、纹理丢失或释放后，旧代失效。外部改变 settings 不会自动重新捕获。禁止将当前输出回填为同一捕获器的输入。

水面保留原 `Cubemap reflectionProbe` 字段与序列化类型；可选 `RenderTexture reflectionCapture` 优先于它。提供了捕获目标但目标无效时会拒绝，只有宿主显式清空此字段才恢复旧 Cube。水面与统一反射均拒绝错误维度及丢失、MSAA、动态缩放的 Cube RT。宿主继续承担 [水面几何与透射](scene-water.md) 和 [统一反射去重](scene-reflection-resolve.md) 的原有输入契约。

普通 mip 下采样没有变成 GGX 卷积；这张 Cube 也不代表 Lightmap、SH 或全场景 GI 已更新。物理大气／云、原版参数／成品画面对齐、完整舞台以及移动端帧时与内存仍是独立工作。

## 验证范围

公开 `ActorRenderingSelfTest.SceneSky.cs` 使用生成的渐变、不同 HDR 面色与经纬纹理、真实相机、Cutout 前景和反射几何。检查太阳盘、源旋转／mip、完整图像、六面捕获及当前统一反射／水面消费，另有错误输入、外部所有权、资源丢失及恢复控制。Python 的 `test_scene_sky_contract.py` 只守护公开架构契约，不替代实际 Player／原生 GPU 验收。

本阶段在 Tuanjie 2022.3.62t15／D3D11 Player 中执行了 98 个新增功能／图像／错误输入控制；此前 14,344 条完整检查记录及 1,523 张 PNG 保持逐条、逐字节一致。原生捕获确认六面实际写入、GPU mip、两种消费者读取相同的 42 个 Cube 子资源及当前水面 Fresnel 混合。相机颜色仍按宿主原有转换处理，不把 UI Color 当作未转换的线性背景。

这不等于 E10 整体、原版成品画面或移动平台验收完成；公开资料未披露的算法仍标为独立模型，未随仓库发布原版贴图或 shader。
