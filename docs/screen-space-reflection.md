# 可选场景 SSR

`ScreenSpaceReflection` 在工具包内提供桌面 Built-in Forward 的 SSR 路径。它使用 [SceneDepthData](scene-depth-data.md) 的几何法线／最小深度层级，沿反射方向查找当前场景交点，把交点投回上一批场景颜色，检查历史深度后输出 HDR 反射与置信度。

本实现采用 PDF 第 37–43 页描述的屏幕空间反射、Hi-Z 和角色排除思路。当前后端采用 pixel shader 追踪与 raster 最小深度层级；Compute／RandomWrite 后端、粗糙度过滤和移动端性能尚待后续阶段。Planar／SSR／Probe 的可选统一消费者见 [SceneReflectionResolve](scene-reflection-resolve.md)，不能仅凭 SSR 有画面就记为全部完成。

## Photo Studio 管线接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var ssr = camera.gameObject.AddComponent<ScreenSpaceReflection>();
ssr.sceneLayers = LayerMask.GetMask("Environment"); // 宿主自己的背景层，必须排除角色
ssr.surfaces = new[] {
    new SceneDepthData.Surface { renderer = floorRenderer, smoothness = 0.8f },
    new SceneDepthData.Surface { renderer = wallRenderer, receiveReflections = false }
};
ssr.reflectionsEnabled = true;
camera.GetComponent<OriginalStyleRenderPipeline>().screenSpaceReflection = ssr;
```

组件和管线引用都不自动创建。`reflectionsEnabled` 默认 false，`sceneLayers` 默认空；默认摄影路径保持不变。启用后，管线在距离雾／球形雾／TAA／DOF／Bloom 之前使用合成结果。

对于自己的 HDR 管线，在同一相机的 `OnRenderImage` 中调用 `TryComposite(camera, source, out result)`，将返回的借用纹理继续传给后续阶段。返回 false 时沿用 source。不要再同时绑定 OriginalStyleRenderPipeline，重复消费同一次相机渲染会被拒绝。

## 输入一致性

- 一个内部相机以 `主相机 cullingMask & sceneLayers` 捕获背景颜色，并用同一相机生成 SceneDepthData。显式 view／projection 矩阵与相机 Skybox 设置也会传递。不复制摄影 UI、角色控制器或其他 image effect。
- `sceneLayers` 必须只包含预期的背景几何，不能包含角色。颜色捕获按层绘制普通材质；几何深度按 surfaces 登记。两者须对应同一批 opaque／cutout 几何，因此 surfaces 应登记非镜面墙等遮挡物，不能只登记地板。
- Surface 的 alphaMask、UV、cull、vertexScale 必须与实际颜色 shader 一致。不会自动重放任意顶点位移、原版 stencil 或透明混合。没有登记到深度的透明特效／Terrain／粒子也不能混入这份颜色输入。
- 角色不进入场景深度和颜色历史，但仍按原样显示在主画面。主相机真实 framebuffer depth 决定可见反射面，避免把角色后面的地板反射叠到角色身上；这里不依赖 sampled depth texture 是否具有 ShadowCaster。

## 输出与历史

`TryGetReflection(camera, out texture)` 提供当前已消费渲染的借用 RGBAHalf 纹理：RGB 是线性场景反射色，A 是有效性与边缘／距离衰减后的置信度。不要保存到下一批渲染，也不要释放或写入组件拥有的纹理。

首次捕获、显式 `ResetHistory()`、切镜、投影大幅变化、场景层／登记集合改变、尺寸变化或渲染中断时不使用旧颜色，保持源图并重新建立历史。正常命中会投影到上次 view/projection，用历史深度拒绝遮挡变化、离屏交点和不可信颜色。颜色来自前一次背景捕获，不使用主图、已含反射的结果或 TAA 历史，因此不会形成 SSR 自反馈。

无命中或历史失效时反射贡献为零，已有材质结果保留。`TryComposite` 内置合成为 `source.rgb + reflection.rgb × confidence × intensity`，保留 alpha。这是显式的加法接入方式，不会自动扣掉材质里已存在的 Reflection Probe 项，也不代表完整 PBR 间接光混合。

`TryTrace(camera, source, planarCoverage, out reflection)` 只生成辐射／置信度并推进同源历史，不产生加法合成。可选 planarCoverage 必须是同尺寸 2D 纹理；A>1e-5 的区域在追踪前被跳过。冷历史生成零置信度，统一消费者此时使用 Probe；输入／生命周期检查失败才返回 false、输出 null。非零 intensity 不作为此输出的辐射倍率，0 仍禁用 SSR。`TryTrace` 与 `TryComposite` 共用每次渲染只能消费一次的限制。[统一反射模块](scene-reflection-resolve.md) 使用此入口按接收面材质合成，并明确要求主输入已去掉旧间接镜面项。

## 参数

| 参数 | 语义 |
| --- | --- |
| `smoothnessThreshold` | 与 SceneDepthData 一致的 SSR 资格阈值 |
| `maximumDistance` | 反射射线最长世界距离 |
| `thickness` | 屏幕空间相交的线性深度厚度，世界单位 |
| `normalBias` | 沿法线和反射方向的起点偏移，减少自交 |
| `maximumSteps` | 最多迭代次数，默认 128，范围 8–512；预算耗尽按 miss 处理 |
| `useHierarchy` | 使用 min-depth 加速；关闭可作全分辨率追踪对照 |
| `historyDepthTolerance` | 历史交点深度容差，世界单位 |
| `edgeFade` | 屏幕边缘 UV 衰减带宽 |
| `cameraCutDistance / cameraCutAngle` | 自动切镜识别阈值；宿主仍可明确调用 ResetHistory |
| `intensity` | 当前加法合成权重；0 时释放可选阶段资源 |

参数应在渲染之前修改，不能在同次 render 回调中途改写输入。容差、步数和偏移需要按场景尺度配置；厚度过大会接受错误表面，过小会漏掉栅格化交点。无效配置、其他相机、错误尺寸、未就绪结果和重复消费均拒绝，不沿用旧结果掩盖问题。

## 平台和成本边界

当前验证针对 Tuanjie 2022.3.62t15、D3D11、Shader Model 4.5、固定尺寸完整视口、非 MSAA／XR 的 Built-in Forward。它额外绘制一次场景颜色和一次登记几何，保留 HDR 颜色／历史／结果、独立深度和层级目标；不是移动端零成本扩展。动态分辨率、SRP、Compute 后端、粗糙度锥追踪／过滤、万人场景成本和整套反射视觉一致性仍需继续完成。

裁剪空间与纹理空间的 Y 方向显式遵守 [Unity 平台差异](https://docs.unity3d.com/2022.3/Documentation/Manual/SL-PlatformDifferences.html)，矩阵来自 [GetGPUProjectionMatrix](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/GL.GetGPUProjectionMatrix.html)。方向规则需用真实 GPU 图像验证，不能靠上下对称夹具证明。各阶段执行证据及未完成项见 [渲染记录](rendering.md) 和 [技术清单](framework-techniques.md)。
