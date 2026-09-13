# 稀疏 GPU 面部形变

`GpuFaceDeformer` 是不依赖角色资源格式、摄影应用或场景全局状态的可选工具：稀疏位置差分→四骨线性蒙皮→直接写入 Mesh 的 GraphicsBuffer。普通 MeshRenderer、附加角色 pass、阴影和 Planar 可以继续消费同一个网格，无需改材质 shader。默认 Photo Studio 仍用 CPU。

对应 PPT54–57、63–65 的面部组合与视角修形。这里独立实现数据布局和 GPU 算法，不附带原版形状、模型、曲线、源码或 shader。原文没有公开完整 kernel；功能相近不代表恢复原版实现。

## 独立宿主

```csharp
using GakumasPhotoMode;
using UnityEngine;

// mesh 是自己有权使用的可读网格；这个例子只移动第 0 个顶点。
var deltas = new[] { new GpuFaceDeformer.Delta(0, 0, Vector3.up * .01f) };
if (!GpuFaceDeformer.TryCreate(mesh, 1, deltas, null, 0,
    out var face, out var reason)) {
    Debug.LogWarning(reason); // 宿主选择自己的 CPU 路径。
    return;
}
// 每次更新在任何相机绘制前调用。0 骨也要传空数组。
if (face.TryDispatch(new[] { .5f }, System.Array.Empty<Matrix4x4>()))
    meshFilter.sharedMesh = face.Mesh;
else
    meshFilter.sharedMesh = mesh; // 不能继续使用上一次输出冒充当前结果。

// 停用时，先解除所有借用，再释放；也适用于换模型。
meshFilter.sharedMesh = mesh;
face.Dispose();
```

`Mesh` 仅在最近一次提交成功时非 null。它是借用的 GPU 输出，不能由宿主写入或销毁；重新提交会覆盖其形变通道。`Dispose` 可重复调用。两个实例分别拥有 compute shader、输入缓冲和输出网格，不修改共享资产。

## 数据契约

- `Delta[]` 按 shape 索引非递减排列；每项包含 shape、vertex、位置差分。同一顶点的重复项按输入顺序累加，不合并或原子散写。权重绝对值小于 `1e-4` 的项不应用。
- 蒙皮输入为每顶点四个 uint：高 16 位 UNORM16 权重，低 16 位骨索引。零权重、超出骨表的索引跳过；有效权重重新归一化。没有移动的有效骨影响时保留已形变位置与原始方向，不额外归一化。
- 矩阵是从源 Mesh 坐标到输出 Mesh 坐标的 affine 变换；通常为 `renderer.worldToLocalMatrix * bone.localToWorldMatrix * bindpose`。距离单位矩阵各分量不超过 `1e-5` 时按单位矩阵处理。
- 差分只改变位置。移动骨对原始 normal/tangent 使用现有 CPU 的线性方向变换及归一化，保留 tangent.w；不会重新计算接缝法线，也没有悄悄替换为逆转置语义。
- 支持 1–4 个流，position/normal/tangent 要求 Float32、维度 3/3/4、四字节对齐。normal/tangent 可缺失。UV、颜色等其余字节保持不变，不要求流内固定 offset。
- 上限：262144 顶点、4096 形状、65536 骨、4194304 条差分；形变数据和输出顶点流预算 256 MiB。位置、方向、矩阵分量须有限且在 ±1e6 内，权重在 ±64 内。无效差分索引、顺序、布局、资源或非有限数据拒绝，不把错误输入自动夹到另一个动作。

初始化复制输入，随后只上传当前权重和骨矩阵。`BufferBytes` 包含自己的结构化数据、占位 UAV 及输出顶点流；不包含 Mesh 索引、CPU 副本、shader、驱动开销或调用者资源。每次取得当前顶点缓冲，避免 CPU mesh 更新使旧句柄失效。边界由所有形状的分量包络和骨变换保守计算，避免用未更新的 CPU 顶点计算剔除边界。

矩阵点积用 float32 的补偿乘积／求和保留低位，不要求 GPU FP64；固定骨权重的逆总和在初始化时显式按 float32 计算。这样避免本机 Mono 的较宽中间值与逐步舍入的 GPU 点积在轮廓上造成可见采样差异。法线归一化仍有浮点近似误差；不承诺各平台逐位相同，也不改旧 CPU 算法。补偿算法有额外指令成本，需要与实际 GPU 帧时分开测量。

## 现有角色接入

```csharp
var face = characterRoot.GetComponentInChildren<FaceExpressionRenderer>();
face.GpuDeformationEnabled = true; // 默认 false。
// IsGpuDeformationActive 才表示实际使用 GPU；请求启用不等于成功。
Debug.Log(face.GpuDeformationUnavailableReason);
face.GpuDeformationEnabled = false; // 同帧恢复并计算 CPU 输出。
```

Player 也接受显式 `--gpu-face-deformation`。原有表情、眨眼、目光、视角修形和权重变化阈值继续由同一 CPU 控制器计算，只替换逐顶点执行。初始化／提交失败会恢复当前 CPU 输出；失败后关闭再开启可重试。

GPU 不回读仅供显示的最大骨位移：`CpuBoneDisplacementIsCurrent` 为 false 时，`MaximumBoneDisplacement` 是 NaN，诊断 JSON 的对应字段为 null。面部贴花仍在 fragment 中投影，但不再把静止 CPU 顶点的覆盖日志当作当前 GPU 覆盖。

## 平台和验证边界

PPT56 的 `MarkDynamic` 是特定 Android 情况的备注。直接写 Raw 顶点缓冲的桌面路径不能照搬：本机原生捕获显示 MarkDynamic 时实际顶点 UAV 未绑定，移除后才得到真实 compute→draw 消费；Unity 的 [UUM-9352](https://issuetracker.unity.com/issues/7174/vertex-buffer-doesnt-work-with-meshes-when-they-are-marked-as-dynamic) 也记录了 Windows 上的此类问题。当前 GPU 输出不调用 MarkDynamic。

已执行的桌面 D3D11 对照包含 137 个自制形状、重复稀疏项、无效骨索引、单位／移动／缩放骨、分流、跳转、双实例、错误输入与现有 CPU 控制器；独立原生检查验证实际 UAV 输出就是后续 draw 的 vertex buffer。整图比较包含真实 Camera.Render，不依赖隐藏窗口的普通帧呈现。

一个本地角色／服装的 5206 个面部顶点、108 个形状及六个表情／动作／视角组合也做了 CPU/GPU 对照。这不覆盖全部角色服装或完整舞台。提交耗时、实际 GPU 帧时、移动功耗和跨平台兼容分开验收；不能用桌面输出一致推出移动加速。

GPU 写入不会同步到 `mesh.vertices`，参见 [Unity Mesh.GetVertexBuffer](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Mesh.GetVertexBuffer.html)。因此 CPU MeshCollider、导出器、逐顶点定位器和其他 CPU 几何消费者不能直接读这个输出作为当前形状；需要独立适配，或保持 CPU 后端。动态网格 hint、Vulkan／Metal／Android、XR／移动驱动及更广动作仍需要各自的实际验证。
