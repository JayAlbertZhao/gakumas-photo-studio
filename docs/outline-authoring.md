# 自动生成描边向量

`ActorOutlineAuthoring` 是显式 CPU 网格制作工具，供 `ActorSupplemental` 等将 `TANGENT.xyz` 当作物体空间挤出向量的角色 shader 使用。它不会注册导入器、遍历角色、修改共享网格或启用默认效果。

PPT 73–74 描述了硬边法线导致描边／落影断裂，以及自动生成专用法线、经两个 UVSet 传递后转换为 Unity Tangent 的流程；没有公开生成公式。本模块独立采用**三角形角度加权、同位置接缝合并**。没有复制 Maya 工具、专用法线数据或原版实现，也没有声称恢复其 UVSet 编码。

## 接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

// 只计算数组，不改原 mesh。positions / triangles 数组重载也可单独使用。
var result = ActorOutlineAuthoring.Generate(sourceMesh);
Vector3[] objectSpaceVectors = result.directions;

// 创建由宿主管理的副本：替换基础 tangent 和所有 blend-frame tangent delta。
// 顶点位置、着色法线、UV、颜色、骨权重、bindpose、submesh 保留。
Mesh authored = ActorOutlineAuthoring.CreateMesh(sourceMesh);
skinnedRenderer.sharedMesh = authored;
// 解除绑定后由宿主 Destroy(authored)，不要销毁 sourceMesh。
```

同位置且同 `weldGroups[i]` 的顶点共同累加三角形单位面法线，权重是对应顶点的内角，最后归一化。默认 `weldGroups = null`，所有顶点属于一个组。硬边、UV 接缝和材质槽造成的重复顶点因此获得相同描边方向，原本用于着色的 `NORMAL` 不变。加权按几何角度计算，不采用三角形数量投票或面积权重；同一平面换对角线或细分不会人为偏向某个面。

```csharp
// 每个原始顶点一个组 ID。不同衣片、贴合的独立壳体由制作端划组。
// 组只阻止合并，不会强行合并空间上不重合的顶点。
var vectors = ActorOutlineAuthoring.Generate(sourceMesh, vertexShellIds);
var mesh = ActorOutlineAuthoring.CreateMesh(sourceMesh, vertexShellIds);
```

位置比较使用有限 float 分量的精确相等，正负零视为相等。没有隐含距离容差、邻域链式合并或按 submesh 自动隔离。靠近但不同位置的顶点不合并；相触壳体若未划组可能错误平滑。该工具不修补裂缝、翻面或非流形拓扑。

## 形变和数据边界

`CreateMesh` 在每个 blend-shape 关键帧的 `base position + delta position` 上重新生成向量，写入 `target direction - base direction` 作为 tangent delta；保留 shape 名称、帧权重、位置和法线 delta。原 tangent delta 会被有意替换。关键帧之间由 Unity 线性插值，蒙皮继续走 Unity 原生路径；多个 shape 组合或骨变形后不实时重新计算三角形法线。原先共点的顶点若使用不同骨权重／形变位移，运动中仍可能分离。

该 Tangent 是专用挤出通道，不能同时作为普通法线贴图的切线空间基。不要对依赖标准 TBN 的 PBR 材质直接替换 tangent。已有手绘向量和原版导入网格也不会自动覆盖。

副本保留源 bounds。使用大幅描边挤出或自定义形变时，宿主仍需提供足够的剔除范围（例如 `SkinnedMeshRenderer.localBounds`）；向量生成工具不会替宿主改变相机或可见性规则。

输入必须 CPU 可读且全部 submesh 为三角形。支持 submesh `baseVertex`。上限是 1,000,000 顶点、6,000,000 索引，克隆的全部 blend 帧累计不超过 8,000,000 帧顶点；这是制作工作量边界，不是实际耗时承诺。不建议每帧调用或在不可信输入上绕过这些边界。

非法索引、非有限位置、计数不符、不可读网格和其他拓扑抛出 `ArgumentException`；null 输入抛出 `ArgumentNullException`。退化三角形不贡献法线。孤立顶点和相反面抵消的组返回零向量，并计入 `unresolvedVertices`，不猜测兜底方向；相对抵消阈值为总角权重的 `1e-12`。`weldedVertices` 统计重复位置组成员数，`degenerateTriangles` 统计零面积三角形。

## 验证范围

Player 自检使用自制的多 submesh／硬边立方体、独立角度公式、不同三角化与形变输入，并通过真实 `ActorSupplemental` pass 和原生蒙皮消费生成数据。整图参考采用独立预挤出几何与不读取 tangent 的 shader；同时保留普通硬边法线的反例。既有摄影应用仍使用原输入。

Maya 导出插件、完整生产角色／服装与原版逐像素效果仍需单独验收。此阶段没有新增 GPU 算法或移动端性能完成声明。
