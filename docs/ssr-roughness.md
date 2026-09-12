# 可选 SSR 粗糙度过滤

SSR 可在追踪后按接收面的 smoothness 扩散反射辐射，供加法接入和统一 Planar／SSR／Probe 消费者共用。默认关闭，不改变摄影应用默认配置。

PPT 113–114 页区分多种反射并展示 smoothness 变化；管线 PDF 44–51 页描述 Planar 覆盖、SSR 资格与统一采样。这些页没有公开 SSR 粗糙度核公式。本包采用独立的、接收面感知的两遍 tent 过滤，不宣称 GGX 预过滤、锥追踪或原版画质等价。

```csharp
ssr.roughness.enabled = true;
ssr.roughness.maximumRadiusPixels = 8;
ssr.roughness.referenceHeight = 1080;
ssr.roughness.planeTolerance = 0.05f; // 场景世界单位
ssr.roughness.normalThreshold = 0.95f;
ssr.roughness.smoothnessTolerance = 0.2f;
// 每个 Surface 的 smoothness × smoothnessMap.r 决定局部半径。
```

配置在相机渲染前修改。`roughness=null`、关闭或有效配置下半径为零均不分配过滤目标，也不执行过滤。启用配置中的 NaN、越界参数会拒绝本次 SSR 并释放目标，不沿用旧结果。

## 过滤契约

目标高度为 H，参考高度为 R，参数半径为 M，像素 smoothness 为 s：

`radius = min(12, M × H / R) × (1 − s)²`

先水平再垂直，每遍至多 25 个候选采样，权重为 `max(0, radius + 1 − abs(offset))`。s=1 完全保留该中心；非整数半径连续改变邻居权重。实际半径上限 12 像素，超过上限时不再保持分辨率比例，低分辨率下扩散也会减弱。它是屏幕空间近似，没有按反射距离计算物理粗糙度锥，也没有 GGX 能量／瓣形保证。

邻居必须同时满足：

- 主 framebuffer 深度下可见、SSR 资格有效，与中心属于同一登记接收面。
- smoothness 差不超过 `smoothnessTolerance`，几何法线点积不小于 `normalThreshold`。
- 邻居与中心的位移在双方几何法线方向的投影均不超过 `planeTolerance`。因此透视下同一倾斜平面的深度梯度不会单因深度差被拒绝，错层或拐角则可拒绝。
- 不在 Planar 覆盖区，未越过屏幕边界。

几何有效但 trace miss 的邻居只降低有效置信度，不作为黑色辐射参与平均。辐射按 `tent × confidence` 归一化；输出 confidence 不超过该遍中心输入。零置信度中心始终为零，不用邻居填补缺失历史。统一消费者继续以 confidence 回退到 Probe，Planar 优先级不变。两遍局部过滤并非任意复杂曲面上的全局测地过滤；保守接收面边界可能产生拼接缝。

## 输入、输出与资源

启用后，内部可见面附件从原 R8（无 R8 时 ARGB32）改为线性 ARGBHalf，保存资格 R、smoothness G、接收面 ID B。最多接受 1024 个 Surface 条目，避免半浮点 ID 精度混淆；不同登记条目不互相过滤。SceneDepthData 的法线／资格 alpha 以及 ActorData 的语义保持不变。alphaMask、UV、顶点缩放、cull 和真实前景深度沿用原可见面绘制。

新增两个全分辨率 ARGBHalf 目标，额外 16 字节／像素；可见面相对 R8 再增加 7 字节／像素。1920×1080 时合计约 45.5 MiB 逻辑附件数据，不含对齐、驱动开销和原 SSR 目标。关闭或零半径释放它们，开关过滤不重建场景颜色历史。尺寸、后端、丢失目标和禁用按组件生命周期处理。

`TryTrace`、`TryGetReflection`、`TryComposite` 均使用最终过滤结果。`TryGetRawReflection` 只提供未过滤 trace 的借用结果，便于诊断；它与其他结果一样不能跨渲染持有、写入或释放。过滤目标丢失时最终结果拒绝读取，仍存在的 raw trace 可用于诊断，不自动伪装为有效过滤结果。

Raster 用追加的两个 blit，Compute 用同一 HLSL 的两个独立 dispatch，源／目标不别名。Compute 必须同时支持追踪和过滤 kernel，失败按 `allowComputeFallback` 整体退回 Raster 或拒绝。开启时 SSR 的 `ComputeDispatchCount` 为 3（trace＋两遍过滤），深度层级次数仍单独记录。不承诺 Compute 更快。

## 验收边界

自制输入包含 HDR 高频、常量、miss／部分 confidence、接收面 ID、可见性、法线／平面／smoothness 边界、Planar 覆盖、倾斜平面和奇数／单行列尺寸。用独立 CPU 标量实现检查两遍 GPU 输出，再检查 Raster／Compute 一致。真实生成墙面条纹验证反射颜色确实扩散，不能只降低亮度冒充粗糙度。

当前运行范围为桌面 t15／D3D11／Built-in Forward；移动 GPU、复杂角色／全场景画质、物理 GGX 和运行成本仍需单独验证。执行结果见 [渲染记录](rendering.md)，接口基础见 [SSR](screen-space-reflection.md) 与 [统一反射](scene-reflection-resolve.md)。
