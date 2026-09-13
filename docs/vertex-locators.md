# 当前顶点定位器

`VertexLocatorRig` 提供可供 [MotionEffect](motion-effects.md) 或自制物体使用的 Transform 挂点，对应 PPT 60 的动态顶点跟随。它只消费宿主明确提供的当前顶点，不自动搜索骨骼、材质名或原版定位器编号。

## 已有角色的接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var definitions = new[] {
    new VertexLocatorRig.Locator {
        id = "left-tear",
        a = myVertexIndex, b = myVertexIndex, c = myVertexIndex,
        alignToTriangle = false,
        positionOffset = new Vector3(0, 0, 0.003f)
    }
};
if (!VertexLocatorRig.TryCreate(definitions, out var rig, out var reason))
    throw new System.ArgumentException(reason);
if (!face.TryCreateVertexSource(rig.GetVertexIndices(), out var source, out reason)) {
    rig.Dispose();
    throw new System.ArgumentException(reason);
}

var effects = new MotionEffectSequence();
effects.TryBind(new[] { new MotionEffectSequence.Entry {
    id = "tear", prefab = myOwnPrefab,
    attachment = rig.GetAttachment("left-tear"), durationSeconds = 2, seed = 713
} }, 2);

// 每次由宿主按顺序执行：身体／头部动画，面部形变，定位器，特效。
face.ApplyCurrentWeights();
bool current = source.TryUpdate(rig, myExplicitShaderVertexScale, out reason);
effects.TrySample(myMotionSeconds); // rig 无效时挂点 inactive，实例立即移除。

// 结束时先释放使用挂点的对象，再释放挂点及采样来源。
effects.Dispose();
rig.Dispose();
source.Dispose();
```

`face` 为已初始化的 `FaceExpressionRenderer`。其 `VertexSource` 只读取最后实际应用的 `_lastWeights` 和蒙皮矩阵，遵守原有 `1e-4` 权重变化锁存、`1e-5` 近单位矩阵规则和无骨运动时保留原始顶点的路径。源会在 CPU／GPU 切换后继续工作；不会自动推进动作或修改模型权重。只改 driver 尚未调用 ApplyCurrentWeights 时，读到的仍是已经绘制的上一份应用结果。

GPU 写入顶点缓冲不会同步更新 Mesh 的 CPU 副本，见 [Unity Mesh.GetVertexBuffer](https://docs.unity3d.com/ja/2022.1/ScriptReference/Mesh.GetVertexBuffer.html)。此接口使用相同的当前形变输入计算选中的少量顶点，不在运行时调用 GetData、AsyncGPUReadback、BakeMesh 或读取 GPU 输出的 `.vertices`。原来的 CPU 形变、GPU compute shader 和默认更新逻辑不改动。

`TrySample` 返回 mesh-local 位置和当前 Renderer 的 localToWorld。`TryUpdate` 额外接收显式 shader 顶点缩放，实际坐标映射为 `renderer.localToWorldMatrix * Scale(vertexScale)`；不会猜读任意材质的顶点位移。原有 ActorToon 的对应输入可由宿主从选定接收材质读取 `_WardrobeScaleCorrection`。不同 submesh 使用不同缩放时分开创建来源／rig，任意自定义 shader 顶点动画需要专门 provider。

输出网格必须继续归 FaceExpressionRenderer 独占。源在角色销毁、拓扑重建、输出被外部替换、GPU 切换尚未完成 CPU 刷新或无有效 pose 时拒绝采样。源销毁不销毁角色；TryUpdate 失败会隐藏 rig，后续有效采样可恢复。宿主不能把没有更新定位器的一帧当成新的几何快照。

## 独立工具接口

`VertexDeformationSampler.TryCreate` 接收自己的 rest positions、形状数量、按 shape 排序的 `GpuFaceDeformer.Delta[]`、每顶点四个打包 influence、骨数量和选中的顶点索引。打包 influence 的高 16 位为 UNORM16 权重，低 16 位为骨索引，零权重和越界骨不参与。重复 delta 按输入顺序累计，正负权重均支持，骨权重总和归一化。

它复制选定拓扑与相关 delta，随后用 `TrySample(weights, matrices, out positions, out reason)` 消费显式当前 pose，不需要 GPU 或资源目录。每个骨矩阵将初始 mesh-local 坐标变换到当前输出 mesh-local 坐标。返回位置顺序对应 GetVertexIndices，调用方可把结果交给通用 rig。创建后修改原数组不会影响采样器。

`VertexLocatorRig` 的 a／b／c 是原始顶点索引。GetVertexIndices 按定义首次出现顺序去重；传给 TryUpdate 的数组必须严格对应这一顺序。每项有非负 barycentric，分量和须在 `1±1e-6` 内，不自动归一化。`(1,0,0)` 定位在顶点 a；其他权重定位在三角形内部。

alignToTriangle 为 true 时，局部 X 沿世界空间 b−a，Z 沿 `(b−a)×(c−a)`，Y 补成正交基。因此镜像和非均匀缩放后的朝向依实际顶点重新计算。为 false 时使用 surface matrix 的 +Z／+Y 构造正交朝向，可令 a=b=c 跟随单个顶点。positionOffset 使用该单位正交框架，单位是世界单位；rotationDegrees 在此后叠加。挂点不继承三角形面积或模型缩放。

rig 拥有自己的根及挂点，不移动宿主传入的外部 Transform。GetAttachment 为借用，不应被修改或重新挂父级。无效输入、退化三角形、丢失对象会隐藏整个 rig，避免部分新位置与部分旧位置混用。Dispose 先隐藏，再回收自己的层级；挂在下面的特效应先由其 owner 释放。

## 容量与边界

每个 rig 最多 256 个挂点，采样器最多 768 个不同顶点。源模型最多 262144 个顶点、4096 个形状、65536 个骨、4194304 条 delta；当前权重在 `[-64,64]`，骨矩阵要求有限 affine，分量不超过 `1e6`。每次返回独立结果数组，未宣称零分配或移动性能提升。

当前实现针对显式 sparse morph／四 influence 蒙皮和现有 FaceExpressionRenderer。普通 Unity SkinnedMeshRenderer、多帧 Unity blendshape、布料或任意 GPU 顶点程序尚需各自的当前几何 provider。世界轨迹粒子、Maya 自定义属性导入、完整制作动作／角色覆盖与移动帧时仍在技术清单中。

桌面验收包含独立几何公式、真实 GPU 顶点缓冲、自制挂点的实际 Camera.Render、无效输入及一个本地角色全部 108 个单独形状和五个身体／根变换的 CPU／GPU 对照。数值探针覆盖每个有位置差分的形状，同时确认其余形状不产生位置移动；不会仅凭三个可见挂点推断整个形变覆盖。附带标记的整幅浮点 RGBA 在 CPU／GPU 模式下完全相同，未屏蔽边缘像素。运行时采样器不做回读；只有验证程序读取实际 GPU 缓冲作为判据。原版模型、示例截图、原始定位器配置与讲演文件均不在公开仓库中。
