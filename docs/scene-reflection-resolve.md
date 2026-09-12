# 统一场景反射

`SceneReflectionResolve` 将 [Planar](planar-reflection.md)、[SSR](screen-space-reflection.md) 与 Probe 组成一个间接镜面项，再乘以接收面的材质响应。它不扫描或修改共享材质，不自动挂载到相机，也不要求 Photo Studio UI。对应 PDF 第 46–50 页的 Planar 区域优先、跳过 SSR 与高分辨率法线差分扭曲。

## 输入边界

传入的 HDR 主颜色必须已经去掉**已登记接收面**原有的间接镜面项，保留漫反射、直接光、发光及其他对象的正常渲染。`inputExcludesIndirectSpecular` 默认 false，未明确声明契约时不会分配目标或执行合成。声明只表示宿主承诺，组件不能从一张主图自动检测是否重复计算了 Probe。

本包 `AdvEnvironmentFallback` 已有 `_EnvironmentReflections` 开关。宿主可在自己拥有的接收面材质实例上关闭该项，保持 `_SpecularHighlights` 等直接光设置，然后通过此模块恢复新的间接光。不要修改共享资产或全局关闭其他未登记对象的反射。`PlanarCapture` 等本来没有 Probe 镜面项的材质也可作为宿主输入。

## 接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var resolve = camera.gameObject.AddComponent<SceneReflectionResolve>();
resolve.receivers = new[] {
    new SceneReflectionResolve.Receiver {
        surface = new SceneDepthData.Surface {
            renderer = floorRenderer,
            smoothness = 0.9f
        },
        f0 = new Vector3(0.04f, 0.04f, 0.04f), // 线性反射率
        probe = reflectionProbe.texture,
        decodeProbeHdr = true,
        probeHdrDecode = reflectionProbe.textureHDRDecodeValues
    }
};
resolve.screenSpaceReflection = ssr; // 可为 null
resolve.planarReflection = planar;  // 可为 null
// 先让接收面源材质不再输出旧间接镜面项，再声明此契约。
resolve.inputExcludesIndirectSpecular = true;
resolve.reflectionsEnabled = true;
camera.GetComponent<OriginalStyleRenderPipeline>().sceneReflectionResolve = resolve;
```

自己的 HDR 管线可在同一相机的 `OnRenderImage` 中调用 `TryComposite(camera, sourceWithoutIndirect, out result)`；成功时消费借用结果，失败时使用 source。一次渲染只能消费一次，不要同时绑定两个消费者。Photo Studio 的显式入口在雾、TAA、DOF、Bloom 之前执行；统一入口成功时不会再调用旧 SSR 加法入口。

`TryGetRadiance(camera, out texture)` 返回消费后当前批次的线性 HDR 辐射，A=1 表示接收面，空白 A=0。它尚未乘材质响应。借用结果只属于该相机当前渲染，不可释放、改写或跨下一次渲染持有。组件关闭、资源丢失或目标更换后不提供旧结果。

## 合成和法线

接收面用主 framebuffer 的真实深度绘制三个全分辨率输入：Probe 辐射、材质响应、法线差分 UV 偏移／接收面 ID。未登记或被前景遮挡的位置全零，因此不会把地面反射加到角色上。

扭曲只作用于 Planar／SSR 图像采样。Probe 按当前像素的着色法线采样 cubemap，不随屏幕 UV 再次扭曲。采样越界或进入另一接收面 ID 时回退本像素 Probe，不把边缘像素向外拉伸。SSR 仍以几何法线追踪，不把 normal map 的细小起伏送进深度射线。

合成权重如下，`P` 是 Planar coverage，`S` 是 SSR confidence：

- 有 Planar 覆盖：`radiance = Probe * (1-P) + Planar * P`，该区域的 SSR 跳过追踪。
- 无 Planar 覆盖：`radiance = Probe * (1-S) + SSR * S`。
- SSR 冷历史、失败或未启用：`S=0`，回退 Probe；Probe 未指定则为黑。
- 最后仅一次 `base.rgb + radiance * response`，保留输入 alpha。

部分 Planar 覆盖也优先于 SSR，未覆盖权重交给 Probe，避免同一位置同时累计两个屏幕反射项。`ScreenSpaceReflection.TryTrace` 提供无加法中间结果的路径，统一消费者通过它共享 SSR 历史推进和每次 render 的所有权检查。

`TryTrace` 不使用旧加法入口的非零 `intensity` 作为辐射倍率；统一路径的强度来自 Receiver.specularScale。SSR.intensity=0 仍会禁用并释放 SSR，改用 Probe／Planar。不要用两个入口同时消费同次渲染。

材质响应沿用本包背景 fallback 的独立模型：`(f0 + (1-f0)*(1-NdotV)^5) * smoothness * occlusion * specularScale`。`f0` 和 f0Map 是线性反射率；normalMap 是线性 tangent-space RGB，解码到 [-1,1]，不自动猜测 Unity 的 DXT5nm／BC5 通道。smoothnessMap.r 与 Surface.smoothness 相乘。此模型支持直接数值核对，但不是完整 GGX 多重散射或原版未公开 BRDF 的等价证明。

法线贴图要求网格提供有效 tangent.xyz 与 ±1 handedness；缺少切线时使用几何法线，不凭空构造 UV 基。Unity 压缩法线输入须由宿主先转换成上述 RGB 契约。镜像变换的 handedness 从实际 object-to-world 矩阵行列式和显式 vertexScale 推导，不依赖 `DrawRenderer` 未可靠填充的 WorldTransformParams；切线 X／Y、负缩放及零强度已有实际 GPU 数值对照。

## 平台和成本

后端需要 Built-in Forward、三个颜色附件、ARGBHalf、固定尺寸完整视口，当前不支持 MSAA／XR／SRP／动态分辨率。五份全分辨率 HDR 目标加上 SSR 和 Planar 的独立捕获都有显著内存／带宽成本；移动端后端与 RenderPass／subpass 优化仍在技术清单中。

宿主必须保持 Surface 几何、alpha cutout、UV、顶点缩放和实际主材质一致，且明确登记每个 materialIndex。任意顶点 shader、透明混合、stencil 和自动材质动画映射尚未覆盖。多个 Probe 的自动选择、box projection、探针更新调度与完整角色捕获适配也不由本组件自动处理。

Unity Probe 输入接口见 [texture](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ReflectionProbe-texture.html) 与 [textureHDRDecodeValues](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ReflectionProbe-textureHDRDecodeValues.html)。执行范围见 [渲染记录](rendering.md)，未完成项目见 [技术清单](framework-techniques.md)。
