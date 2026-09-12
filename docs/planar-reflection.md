# 可选平面反射

`PlanarReflection` 在工具包中提供独立的平面反射捕获和可见接收面投影。它用于补充 SSR 看不到的角色／发光网格，或作为镜面宿主的输入。组件默认关闭，不自动创建摄影 UI、修改主材质或增加 image effect。

设计依据为管线 PDF 第 44–51 页的单独角色／发光网格捕获、区域 alpha mask，以及 PPT 第 114 页的画外镜像和 smoothness 支持。这里使用镜像相机与独立绘制，不创建或分发原版镜像模型。算法接口是独立实现，不能推断原版未公开的材质参数。

## 接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var planar = camera.gameObject.AddComponent<PlanarReflection>();
planar.planePoint = mirror.position;
planar.planeNormal = mirror.up; // 法线指向主相机允许使用的一侧
planar.reflectedLayers = LayerMask.GetMask("Actor", "Emissive");
planar.reflectedSurfaces = new[] {
    new PlanarReflection.Draw {
        surface = new SceneDepthData.Surface { renderer = actorRenderer },
        material = reducedForwardMaterial,
        shaderPass = 0
    }
};
planar.receivers = new[] {
    new PlanarReflection.Receiver {
        surface = new SceneDepthData.Surface {
            renderer = floorRenderer,
            smoothness = 0.9f
        }
    }
};
planar.reflectionsEnabled = true;
```

在相同相机的 `OnRenderImage` 或显式 `Camera.Render()` 之后调用 `TryGetReflection(camera, width, height, out texture)`。width／height 是主相机真实渲染目标尺寸。返回纹理是组件所有的借用资源，不要释放、写入或持有到下一次渲染。相机不符、目标更换、尺寸不符、未渲染或禁用时返回 false。

RGB 是线性 HDR 反射颜色，A 是反射覆盖与 Receiver.strength；它不是透明材质的 opacity，也不包含 Fresnel 或镜面反射率。宿主应将此纹理与 SSR、Probe 的独立间接光项组合，再按自身材质响应合成。直接把 RGB 加到已经包含 Probe 的主颜色可能重复计算间接光，因此本组件不自动接入 Photo Studio 的加法 SSR 入口。

包内 [SceneReflectionResolve](scene-reflection-resolve.md) 提供显式统一消费者：优先 Planar、跳过其覆盖区域的 SSR、失败回退 Probe，并以全分辨率法线差分扭曲屏幕反射。宿主须登记相同接收面并保证其主材质已去掉旧间接镜面项；捕获组件本身仍不改主材质。

## 捕获绘制契约

- 每个 Draw 显式指定 renderer、materialIndex、材质和一个 pass。只绘制注册项且位于 reflectedLayers 内的对象，不受主相机可见范围限制。允许生成的 SkinnedMeshRenderer；画外蒙皮更新策略由宿主设置。
- 捕获相机自身 cullingMask 为 0，避免再绘制一次完整场景。注册材质必须适用于 `CommandBuffer.DrawRenderer`：显式提供光照输入，ZWrite On，opaque／cutout，并与 Surface 的顶点缩放、剔除、alphaMask／UV／cutoff 一致。不会自动执行所有 pass、附加光、阴影生成、角色材质修复或主相机的后处理。
- 默认并不从主材质猜测这些输入。普通依赖 Unity 自动光照参数的材质不构成该契约；可提供自己的廉价 Forward 材质。附带 `GakumasPhotoMode/PlanarCapture` 提供单方向 Lambert、环境颜色、HDR 自发光和 cutout，参数均显式绑定。这是通用简化材质，不是原版角色着色替代品。
- alpha coverage 单独按同一几何与捕获深度重画，原材质输出 A=0 的不透明物体仍有反射覆盖。未匹配的顶点位移、stencil 或透明混合会破坏颜色／区域一致性，当前不支持。
- 接收面只在主相机实际 framebuffer depth 可见的位置投影，前景物体无需 ShadowCaster 也能遮挡反射。`receiverPlaneTolerance` 限定顶点插值后的世界位置接近平面；不允许把同一投影贴到任意弯曲或离面网格。

## 平面、质量与资源

主相机必须位于 planeNormal 正侧，距平面大于 clipOffset。反射矩阵保留源相机显式 view／projection；oblique near plane 裁掉平面背侧，镜像绘制临时反转剔除，finally 恢复原值。相机与矩阵不会写回源相机。

`resolutionScale` 为 0.25–1，默认 0.5；捕获尺寸向上取整，输出仍是主目标分辨率。捕获的空白 RGBA 清零，再生成颜色／覆盖共用的 mip 链。接收面 smoothness 和 smoothnessMap.r 决定粗糙度，取 `(1-smoothness)^2 * maximumRoughnessMip`；过滤后 RGB 除以 coverage，避免边缘重复乘 alpha 导致黑边。该 box-mip 模型不等于 GGX 预过滤或原版公式。

每相机拥有捕获 HDR／深度、mip、主尺寸输出、命令缓冲和辅助材质。无有效绘制、禁用、错误配置或不支持的平台会释放资源，不返回旧图。当前后端限定 Built-in Forward、完整视口、固定尺寸、非 MSAA／XR；不宣称已经完成移动端带宽和帧时验收。

对应 Unity API 约束见 [oblique projection](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera.CalculateObliqueMatrix.html)、[镜像剔除](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/GL-invertCulling.html) 和 [显式 DrawRenderer](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.DrawRenderer.html)。执行证据见 [渲染记录](rendering.md)，完整开项见 [技术清单](framework-techniques.md)。
