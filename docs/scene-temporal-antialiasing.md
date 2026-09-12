# 场景运动与颜色 TAA

`SceneTemporalAntialiasingSettings` 是默认关闭、相机私有的 HDR 时域模块。它把 [实际场景运动](scene-motion.md) 接入颜色历史，独立实现 PDF33 的方差裁剪和 ExcludeTAA／NoJitter 语义。旧 `OriginalStylePost` TAA shader 和默认摄影路径保持不变；不附带原版源码、shader 或资产。

## 接入

先按 [场景后端](scene-deferred.md) 登记 opaque／cutout 表面、层和线性 HDR 相机目标，再显式选择：

```csharp
scene.motion.enabled = true;
scene.temporalAntialiasing = new SceneTemporalAntialiasingSettings {
    enabled = true,
    historyWeight = .95f,
    maximumHistory = 32,
    maximumFrameGap = 1,
    depthTolerance = .02f, // 世界空间切平面分离距离
    normalThreshold = .9f,
    varianceGamma = .9f,
    reactiveThreshold = .2f
};
// 同一 Camera 的 OriginalStyleRenderPipeline：替代旧 TAA，不做两遍。
post.sceneTemporalSource = scene;

// 已知 seek、曝光／输入颜色约定突变：
scene.ResetTemporalColorHistory();
// 也可递增 contentRevision，在下一次场景准备时拒绝旧颜色。
scene.temporalAntialiasing.contentRevision++;
```

独立宿主也可在这台相机完成渲染后调用 `TryResolveTemporalColor(camera, currentHdr, classification, out resolved)`。输入必须是对应本次场景帧、相同尺寸的线性 ARGBHalf／ARGBFloat、2D、单采样、非 dynamic-scale RenderTexture，不得与本模块八张目标重叠。接口能检查相机、尺寸、格式和帧身份，无法鉴定任意外部纹理内容的来源；宿主负责颜色／几何的对应关系。不要同时手动 resolve 又让同台后处理消费同一帧。

提供非空 `TemporalClassification` 时，必须有该相机本次有效、尺寸一致且未丢失的 mask，分类的 jitter 必须与场景准备时一致；否则拒绝，清除颜色历史。没有外部分类型号时传 `null`。后处理显式选择本模块而 resolve 失败时，透传当前 HDR，并通过 `TemporalColorUnavailableReason` 提供原因；不会回读无关的宿主 motion。

## 投影、可见性与分类

本模块不改变相机投影。宿主若采用 jitter，须同时设置实际投影及 `jitterUv`，单位为纹理 UV：稳定输出在 `uv - jitterUv` 处采样本次颜色。历史使用上次的完整 GPU 投影／view 和 jitter。未加 jitter 时保持零；不把像素单位、NDC 或平台翻转前的值直接当作纹理 UV。

颜色手动 bilinear 重采样；几何／运动仍是实际 jittered 栅格上的 Point 样本。深度必须在该样本的像素中心射线上重建，不能套用连续颜色坐标。历史几何也依据旧 jitter 找回其原始 Point 样本中心。这一点对斜面连续 jitter 尤其重要。

在 `BeforeImageEffects` 对已登记场景表面重新绘制两张可见性 guide，使用相机**实际 framebuffer depth** 的 LEqual 测试、相同 alpha／cull／vertexScale。挡在前面的普通 Forward opaque 不会借用后面的场景历史。背景和没有可见场景身份的像素不累积，只采用当前颜色。未登记的 ZWriteOff 透明／粒子层不能靠深度排除：宿主必须通过外部分类型号保护它们，或者在 TAA 之后合成。共面混合、任意材质顶点位移和完整角色分类仍需专用适配。

`SceneDeferredCamera.Surface.temporalFlags` 与外部分类按位合并，独立于 ActorData 和材质 discriminator：

| 标志 | 输出 |
| --- | --- |
| `Normal = 0` | 有效对应通过检查后允许累积 |
| `ExcludeTaa = 2` | 使用去 jitter 的当前颜色，不读取历史 |
| `NoJitter = 4` | 使用原始当前像素中心，不去 jitter、不累积；同时为 6 时优先 |

为避免边界把受保护表面混进历史，当前中心与去 jitter 位置的分类都会检查。当前颜色 alpha 不参与历史混合：普通／ExcludeTaa 使用本次重采样 alpha，NoJitter 使用本次中心 alpha。

## 重投影与生命周期

1. 使用实际 GPU 运动 RG（当前 UV − 上次 UV）、上次正视深度及上次网格法线，求旧颜色像素位置。整数 SV_POSITION 像素计算避免 fullscreen 插值 UV 往返误差。
2. 手动检查四个旧 tap 的范围、有效年龄、表面身份、分类、法线与双切平面分离距离；有效支持归一化。网格法线不受 normal map／贴花扰动。
3. 旧／当前颜色经亮度压缩后的最大通道差超过 `reactiveThreshold` 时拒绝历史。该启发式能响应部分灯光和遮挡突变，也可能拒绝合法采样噪声。
4. 当前 3×3 同身份／分类、相容法线与切平面的邻域，决定 min/max 与均值 ± `varianceGamma × σ` 的交集。使用中心化矩稳定计算方差，再将旧 RGB 沿中心射线裁剪到包围盒。
5. `weight = min(historyWeight, age / (age + 1)) × 有效支持`；在亮度压缩空间混合，稳定代数形式避免高 HDR 数值消减。参与累积的 RGB 限制到 `0..65504`，不做自动曝光补偿。

首帧／历史拒绝时年龄为 1、实际权重为 0。成功颜色 resolve 后才交换历史，同一 `Camera.Render` 只能消费一次；重复消费被拒绝，但不会使已有成功输出失效。允许同一 Unity tick 多次完整渲染。漏掉一次颜色 resolve、超过 `maximumFrameGap`、运动重置／切镜、颜色配置／contentRevision 改变、目标丢失／尺寸变化、GPU 投影矩阵最大元素变化达到 `.1` 时拒绝旧历史。较小投影变化使用完整矩阵重投影。暂停或 seek 不一定反映在帧号／几何中，宿主仍须显式 reset。

`TryGetTemporalColorFrame` 返回借用输出，`IsCurrent` 检查相机成功序列及全部目标存活。不得写入、Release 或跨渲染持有使用权。`ResetTemporalColorHistory` 立即使颜色 lease 失效；不重置独立 GTAO 历史。`OriginalStyleRenderPipeline.ResetTemporalHistory` 会同步重置显式引用的场景颜色。关闭 TAA 后下一次渲染释放八张目标、材质和可见性 command buffer；motion 由其自己的开关管理。

## 目标与成本

| `TemporalColorFrame` 附件 | RGBAFloat 内容 |
| --- | --- |
| `color` | 本次过滤 RGB 与当前 alpha；ping-pong |
| `geometry` | 对应去 jitter 输出位置所选 Point 样本的世界网格法线 RGB、正视深度 A；ping-pong |
| `identityAgeFlagsWeight` | R 稳定身份、G 年龄、B 分类、A 实际历史权重；ping-pong |
| `visibleGeometry` | 原始 jittered 栅格的可见网格法线 RGB、正视深度 A |
| `visibleIdentityFlags` | R 可见场景身份、G 分类、B=0、A=1；无可见表面全零 |

共八张全分辨率 Point／Clamp float4，无额外深度目标，名义颜色附件成本 `128 × width × height` 字节，1920×1080 约 **253.1 MiB**。尚未计入 motion／顶点快照、场景 GBuffer、GTAO 及原后处理附件；后处理的旧 history 分配也未优化。每个可见已登记表面增加一次 guide draw，另加一次三 MRT fullscreen resolve。这是桌面正确性后端，不构成内存节省或加速声明。

`TemporalColorTargetCount`／`TemporalColorVisibilityDrawCalls`／`TemporalColorResolveDrawCalls` 暴露数量。要求三个 float4 MRT 及 Render／Sample 能力，并继承 motion 的 readable triangles、非 static-batch、geometry shader 和预算限制。未开启时无八张目标或新 draw；不改变旧 shader 变体。参数须有限，范围见 `SceneTemporalAntialiasingSettings`，仅在开启时验证。

## 验收边界

自制 D3D11 场景检验真实投影 jitter 与密采样空间参考，同时使用只去 jitter 的 bilinear 负控，避免把一次空间模糊当作时域收益；还检验斜面、刚体／混合双骨／blendshape、alpha 内容更新、异身份反遮挡、灯光响应、HDR／alpha、两相机、连续／漏消费／隔帧、目标丢失、极小尺寸、分类及真实后处理接入。

阶段执行证据见 [渲染记录](rendering.md)。完整动态舞台、全部角色材质／服装、复杂透明边缘、任意顶点位移、FSR 输入质量及 Vulkan／Metal／移动设备成本不由这些局部控制代验。
