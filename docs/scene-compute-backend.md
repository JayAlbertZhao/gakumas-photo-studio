# 可选 Compute 反射后端

按管线 PDF 第 40 页的 Hi-Z／ComputeShader RandomWrite 技术补充独立实现。后端沿用本包的角色排除场景输入、历史拒绝与统一反射契约，不复制 HDRP 或原版 shader 源码，也不切换默认摄影管线。

## 选择与回退

```csharp
ssr.backend = SceneShaderBackend.Compute;
ssr.allowComputeFallback = true; // 默认允许不支持时退回 raster

// 独立深度消费者也可只启用计算缩减。
geometry.hierarchyBackend = SceneShaderBackend.Compute;
geometry.allowComputeFallback = false; // 严格要求计算后端
```

默认都是 `Raster`。SSR 会把所选后端和回退策略传给自己的 SceneDepthData，不需要宿主发现或编辑内部相机。SSR 关闭时不分配反射目标或私有 compute 实例，也不 dispatch。

- `ssr.ActiveBackend` / `ActiveHierarchyBackend` 表示追踪和层级实际所选后端；两者可能不同。
- `geometry.ActiveHierarchyBackend` 表示独立层级的实际后端。`buildDepthHierarchy=false` 时不执行计算缩减。
- `ComputeFallbackReason` 记录降级原因，`UnavailableReason` 记录整个阶段拒绝执行的原因。
- `ComputeDispatchCount` 是当前渲染已提交的 kernel 数，不是 GPU 完成计时或性能指标。

检查 compute 支持、输出格式的随机写支持、资源／kernel 是否存在和 kernel 支持情况。SSR 输出需要 ARGBHalf UAV，层级需要 RFloat UAV。允许回退时，不满足某项能力或目标创建失败会走原 raster；严格模式不提供旧结果掩盖失败。不支持的设备仍须满足原 raster 后端的基本能力要求。

配置应在渲染前修改。切换 SSR 后端会重建其目标和历史，第一次渲染置信度为零；后续重新建立反射。禁止把 `ActiveBackend` 的默认枚举值当作“已有可消费帧”，仍以 TryTrace／TryComposite／TryGetFrame 返回值为准。

## 执行路径

几何法线与基础深度仍通过登记表面的 MRT 绘制产生。计算路径只把后续最小深度缩减与 SSR 追踪改为 dispatch：

1. 解除基础深度的渲染附件绑定，再在同一 graphics queue 按层读取前一级，写入独立 RFloat UAV。
2. 每级 2×2 取 min，尺寸向上取整、边缘 clamp；1×N、N×1 和奇数最后一行／列不丢失。不使用平均 mip。
3. SSR kernel 读取相同的几何、可见面、Planar 覆盖和背景历史，以同一 HLSL 函数追踪，写入 HDR RGB／confidence A。每个有效像素都写出结果，包括冷历史与 miss 的零值。
4. 8×8 thread group 向上取整 dispatch，显式检查越界线程。所有资源与尺寸明确绑定，不依靠越界读返回零。

历史重投影坐标距整数像素边界小于 0.0001 像素时，两后端统一吸附到该边界，再执行相同的历史深度拒绝，越界仍返回 miss。此规则处理 shader 阶段末位舍入导致的不同邻像素选择，不放宽世界深度容差或夹住离屏射线。它可能改变旧 SSR 极少数恰好位于投影边界的命中。共享算法使维护一致；独立解析墙面、CPU 深度缩减和真实角色排除检查仍有必要，两个实现输出相同本身不能证明正确。

每个组件拥有自己的 ComputeShader 实例和目标，不向共享资源写参数。没有异步 compute、跨队列同步、wave/subgroup、memoryless 或 subpass 优化。SSR 的绑定目前复用已准备好的 raster 材质输入集合，有额外 CPU API 调用；仅将工作搬到 Compute 不保证更快。

## 当前限制

当前执行验收限定 t15／D3D11／Built-in Forward，沿用非 MSAA／XR、完整视口、固定尺寸限制。可选 [粗糙度过滤](ssr-roughness.md) 另加两次同源 HLSL dispatch，并要求过滤 kernel 可用。本文不宣称移动性能改善、Vulkan／Metal 已测、HDRP 原算法等价或全角色画面追平；物理 GGX 质量与移动端预算仍是独立开项。

实际运行记录与保留失败见 [渲染记录](rendering.md)。API 依据见 Unity [Compute Shader 平台与资源约束](https://docs.unity3d.com/2022.3/Documentation/Manual/class-ComputeShader.html)、[kernel 支持检查](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ComputeShader.IsSupported.html)；输入契约见 [场景深度](scene-depth-data.md)、[SSR](screen-space-reflection.md) 与 [统一反射](scene-reflection-resolve.md)。
