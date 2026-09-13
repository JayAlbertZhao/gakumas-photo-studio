# 透明／特效的法线贴图基

`SceneForwardLightingCamera`、开启光照的 `LowResolutionFxRenderer` 和 `HeavyFxRenderer` 共用物体空间到世界空间的法线贴图转换。材质继续接受线性 RGB 编码的切线法线 `rgb * 2 - 1`，没有新增参数或自动修改材质。

## 变换契约

`NORMAL` 通过物体矩阵的逆转置转换，`TANGENT.xyz` 作为方向转换，并投影到法线的切平面。副切线使用：

```text
B = normalize(cross(N, T))
    * mesh.tangent.w
    * sign(det(objectToWorld))
    * sign(vertexScale.x * vertexScale.y * vertexScale.z)
```

随后将法线图的 XYZ 分量按 `T / B / N` 组合并归一化。原切线的 `w`、物体镜像和显式顶点缩放的镜像分别保留；偶数次镜像会抵消。FX 的 mesh/matrix 接口没有独立 `vertexScale`，该项为一，宿主通过实际矩阵表达缩放。

手工 `CommandBuffer.DrawRenderer`／`DrawMesh` 不能依赖引擎总能填写 `unity_WorldTransformParams.w`。本模块现在从本次实际物体矩阵计算方向符号，与工具包已有 Deferred／Reflection 的处理一致。Water 和 Crowd 已有各自显式处理，继续保持。

## 修正范围

此前共享 Forward 顶点路径依赖上述引擎字段。在实际 D3D11 检查中，该值缺失会使副切线消失：法线图 X 分量仍能改变画面，Y 分量却未正确参与。仅检查“贴图开关有变化”或把两个共享同一错误的 Forward 路径互相比对，无法发现这个问题。

此次修正有意改变**已开启这些可选模块并使用法线贴图**时的错误结果。不新增兼容开关来保留缺失的副切线。没有启用法线贴图的路径、摄影应用默认配置和 `ActorSurface` 均未因此切换逻辑。

## 独立验证

`ActorRenderingSelfTest.ForwardBasis` 在 C# 中独立计算世界 TBN，将预期着色法线直接写入另一份世界空间网格的 `NORMAL`，并关闭参考材质的法线贴图分支。真实摄像机、Low／Heavy、Renderer／显式 Mesh、Full／Half／Quarter 输出和低尺寸工作附件都与该参考整图比较。

输入覆盖非均匀缩放、旋转、奇偶镜像、正负 tangent.w、正负／零法线图 Y 分量；另外检查 SH GI、使用法线偏移的 Point／主方向光阴影，以及当前原生蒙皮／blendshape。缺失副切线的参考作为明确反例保留。未修改常规 RGB normal map 的编码，也没有用原版 shader 或资产作公开依赖。

蒙皮检查在绑定前制作正负切线符号的独立网格，再显式切换并等待原生蒙皮更新。它不承诺就地修改已绑定网格的 `tangent.w` 会立即更新引擎的蒙皮输入缓存。

这项检查针对共享法线变换正确性，不证明完整舞台画质、透明排序、移动驱动或 GPU 帧时。复杂骨缩放、空间变化的材质组合及其他角色路径仍需对应输入的独立验收。
