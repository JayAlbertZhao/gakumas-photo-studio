# 场景几何输入与最小深度层级

`SceneDepthData` 是可选的 Built-in Forward 相机组件，位于 `Gakumas.Toolkit`。不依赖角色加载器或 Photo Studio UI，也不会自动挂到摄影相机。它为后续 SSR、遮蔽或贴花消费者提供几何输入；本身不计算反射、不改变主画面。

设计对应 QualiArts 管线讲演 PDF 第 17 页的无 normal map 几何法线／SSR mask，以及第 40–43 页的 Hi-Z 和角色排除思路。公开资料没有给出完整 MaterialID 编码或阈值；此模块定义自己的数据契约，不兼容性猜读原版资产。使用普通 raster min-reduction，不宣称实现了原版 ComputeShader、移动 RenderPass 或性能水平。

## 接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var geometry = camera.gameObject.AddComponent<SceneDepthData>();
geometry.excludedLayers = actorLayerMask; // 由宿主定义，无内置角色层号
geometry.smoothnessThreshold = 0.5f;
geometry.surfaces = new[] {
    new SceneDepthData.Surface { renderer = floorRenderer, smoothness = 0.8f },
    // 不反射的墙仍写入几何深度，防止穿墙命中。
    new SceneDepthData.Surface { renderer = wallRenderer, receiveReflections = false }
};
```

宿主须登记全部需要参与此场景深度的 opaque／alpha-cutout 表面，而不只是镜面。每个 Surface 对应一个 renderer 的一个 materialIndex。角色等排除层完全不进入这份深度，能够保留其后方背景几何；主相机仍照常绘制它们。不依靠主相机的 `_CameraDepthTexture`、ShadowCaster 或 ActorData。

可在 `OnRenderImage` 或显式 `Camera.Render()` 返回后消费：

```csharp
if (geometry.TryGetFrame(camera, targetWidth, targetHeight, out var frame)) {
    consumer.SetTexture("_SceneNormalMask", frame.normalMask);
    consumer.SetTexture("_SceneLinearDepth", frame.linearDepth);
    // frame.GetDepthLevel(i) 供层级遍历；第 0 层就是 linearDepth。
}
```

`targetWidth/Height` 是该相机实际目标尺寸；使用超采样时应传内部尺寸，不是窗口尺寸。返回值是借用的 GPU 纹理和当次 view／GPU projection 矩阵，只能用于同一相机当前渲染结果。不要持有到下一次渲染／配置变化，不要释放或写入它们。下一帧尚未渲染、其他相机、尺寸不符、目标更换、禁用等情况返回 false；消费者应跳过本阶段，不能偷偷使用旧图。

## 数据契约

| 输入 | 格式与含义 |
| --- | --- |
| `normalMask.rgb` | Linear RGBA8 UNorm；归一化世界空间网格法线映射到 [0,1]，不含 normal map；保留网格的平滑法线 |
| `normalMask.a` | 0／1 的 SSR 资格；`receiveReflections && smoothness × smoothnessMap.r >= smoothnessThreshold` |
| `linearDepth` | RFloat；正的观察空间深度（世界单位），空白像素为当次相机 farClip；不是反向 Z，也不是 [0,1] device depth |
| `GetDepthLevel(i)` | RFloat 最小深度，Point／Clamp；每级对前一级 2×2 取 min，边缘 clamp，尺寸向上取整，直到 1×1 |
| `worldToCamera / gpuProjection` | 生成该批纹理时使用的相机矩阵，projection 已按 RenderTexture 转换 |

空白像素的 normalMask 为全零，不能解码成表面法线。SSR 资格为零的真实表面仍有深度和法线。RGBA8 法线有量化误差，需要解码后归一化。这里的 alpha 与 ActorData 材质判别码、TAA 分类位没有任何复用关系。

层级使用独立纹理，不是硬件 mip 链；例如 5×3 → 3×2 → 2×1 → 1×1，保留奇数边缘。`buildDepthHierarchy = false` 时只提供基础深度和法线。层级还不是 SSR：历史颜色、射线遍历、命中失败回退与反射合成尚未接入。

## 表面与平台限制

- alphaMask 使用 A 通道；smoothnessMap 使用 R 通道，两者各有独立 UV0 scale/offset。不自动读取共享材质属性。
- `vertexScale` 可表达宿主的局部顶点缩放并相应修正法线；任意风摆、顶点动画、自定义位移或原版 stencil 不会自动复制。材质不得依赖本模块替其执行生产顶点 shader。
- 每个注册 renderer 仍须启用、活跃、未 forceRenderingOff，并处于相机 cullingMask 内。无效表面跳过；粒子、线段、Terrain 与透明混合不是此适配器支持范围。
- 本后端要求两个颜色附件、RFloat 和 RGBA8 支持、Built-in Forward、完整视口、固定尺寸非 MSAA／非 XR 的 2D 目标。其他配置释放目标并通过 `UnavailableReason` 返回原因，不切换项目管线。
- 模块拥有独立深度附件，额外绘制注册几何；层级另占额外纹理与 blit 成本。没有证明移动端性能，也未采用 memoryless／subpass 架构。
- 空登记／禁用会释放目标和辅助材质；禁用还会解除相机 command buffer。主材质、主相机深度模式和全局纹理绑定保持不变。主机应在渲染前更新登记数据。

实现使用 [Unity 相机渲染回调](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/MonoBehaviour.OnPostRender.html) 标记可消费帧，并遵守 [CommandBuffer 渲染目标恢复规则](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetRenderTarget.html)。可执行验收见 [渲染记录](rendering.md)；文档 API 存在不等于平台／性能已验收。
