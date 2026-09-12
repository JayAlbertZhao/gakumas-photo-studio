# 角色简化反射材质

`ActorPlanarCaptureSet` 把本工具包已经修复为 `GakumasPhotoMode/ActorToon` 的角色材质适配成独立的 reduced Forward 绘制，供 [PlanarReflection](planar-reflection.md) 使用。不读取原版 shader AB，不复制原版源码，不自动挂载或替换主相机材质。

PDF 44–45 描述角色／部分发光网格单独捕获、昂贵处理省略及区域 alpha；PPT 35–49 描述材质族与 Base／Shade／Def／Ramp、Layer、头发和眼部输入。讲演没有公开 reduced pass 的完整公式，本实现保留这些输入并采用自己的简化光照。它不承诺与主角色完整 BRDF 或原版 shader 逐像素一致。

## 生命周期与调用

宿主拥有一个 capture set，为每个反射视图显式刷新；示例假定 `planar` 已登记接收面、平面、层并启用。普通相机应在动画完成之后、该相机开始渲染之前刷新；显式离屏可按以下顺序调用：

```csharp
var capture = new ActorPlanarCaptureSet();
var light = new ActorPlanarLighting {
    lightDirection = worldDirectionToLight,
    lightColor = new Vector3(0.8f, 0.8f, 0.8f), // 线性 RGB
    ambientColor = new Vector3(0.2f, 0.2f, 0.2f),
    head = headTransform
};

// 每次渲染，动作／材质动画完成之后：
Matrix4x4 reflectedView = camera.worldToCameraMatrix *
    PlanarReflection.ReflectionMatrix(planar.planePoint, planar.planeNormal);
bool ready = capture.TryRefresh(selectedActorRenderers, reflectedView, light, out var error);
planar.reflectedSurfaces = capture.Draws; // 失败也是空数组，避免旧帧继续使用
if (!ready) Debug.LogWarning(error);
camera.Render();

// 会话结束，先解除消费者，再释放 owned 材质：
planar.reflectedSurfaces = System.Array.Empty<PlanarReflection.Draw>();
capture.Dispose();
```

`Draws` 和其中材质是借用对象，不应由调用者释放、长期保存或编辑。重复刷新复用材质，移除 renderer／submesh 时释放；`Dispose` 后不能重启同一实例。换装后重新获取 renderer 列表。输入只接受显式选择的 MeshRenderer／SkinnedMeshRenderer；画外骨骼更新策略由宿主安排，例如在自己控制的生命周期内设置并恢复 `updateWhenOffscreen`。

每个反射相机用独立 set；不能把同一个可变 set 同时交给几个相机。绘制先按 source renderQueue，透明队列再按镜像 view 的距离排序；同队列其余项使用稳定 renderer／submesh 顺序。头发覆盖在主 pass 后追加，不包含描边。

## 输入与保留范围

| 输入 | 简化捕获中的作用 |
| --- | --- |
| 原始类型 0、1、2、3、4、5、6、8、9 | 不透明、cutout、透明、眉／眼／高光、头发、脸部的颜色与覆盖语义；类型 7 拒绝 |
| Base、Shade、Def、Ramp | Base 色／clip，Shade 的皮肤标志，Def 的明暗偏移、面部反射法线遮罩和头发高光权重，Ramp RGB／A 的皮肤与非皮肤分支 |
| UV2 Layer、材质 Ramp、头发 Highlight | 层颜色／Def 混合、打包顶点 G 的材质行、独立简化的视角高光；不等于主 shader 的相机基底高光公式 |
| ActorTextureFrame、MainTex ST、眼高光 BaseMap ST | 动画图集与纹理坐标变换 |
| Color、ActorColor、Emission | 字面向量染色、角色 dither fade、自发光；线性光照参数不读取主相机全局变量 |
| Head、WardrobeScaleCorrection | 当前头部方向、同色／mask 的顶点缩放与法线处理 |
| Cull、ZWrite、Blend、Stencil | 保留支持的主材质状态，眼部与补画头发使用匹配覆盖 pass |

材质和 MaterialPropertyBlock 在刷新时读取，不被写回。存在非空 per-material block 时使用它而非 per-renderer block，与 Unity 的整体覆盖规则一致；不是逐属性合并。[GetPropertyBlock](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Renderer.GetPropertyBlock.html)

ShaderLab Color 材质值与字面 Vector 区分处理；block 中的 `SetColor`／`SetVector` 值以 shader 空间读取。注意同一属性先 SetColor 再 SetVector 仍保留 Unity 的颜色转换标志；需要独立的字面 Vector 输入时先清空／新建 block。`_Cap*` 为捕获专用保留名，调用方不得在 renderer property block 使用这些名字。光照均为显式线性 Vector3，包括主光、环境色、阴影染色和加色；方向不能为零。[SetVector 的既有颜色属性规则](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/MaterialPropertyBlock.SetVector.html)、[GetVector 的 shader 空间值](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/MaterialPropertyBlock.GetVector.html)

支持的 RGB 混合为 `One` 到 `Zero`／`One`／`OneMinusSrcAlpha`，以及 `SrcAlpha` 到 `One`／`OneMinusSrcAlpha`；要求 ColorMask 含完整 RGB（14 或 15）。partial channel、未知 shader、非法枚举／非有限值、非二维贴图等配置失败时整个 set 清空，并返回原因。render state 从材质读取，不把 MPB 当作状态覆盖。

## 颜色与区域 alpha

颜色 pass 只写 RGB。之后清 alpha，保留 RGB 并清 depth／stencil，再按相同顺序重放覆盖 pass；几何、clip、fade、ZWrite 和 stencil 相同。普通 opaque 覆盖为 1；透明／眼部／头发补画按其实际贡献做覆盖并集，避免把 Base A 当作所有材质的 opacity，也避免在最终深度上补画 mask 导致眼部漏色。

捕获 RGB 在透明区域是已加权贡献；Planar 接收面按共用 mip 的覆盖归一化，统一反射消费者再乘覆盖一次。不要绕过这一契约重复预乘。自定义 `Draw.coverageMaterial` 同样必须遵守此规则；不提供时维持原有 opaque／cutout 深度相等 mask 路径。

## 简化及未验收部分

有意省去完整 specular／environment BRDF、阴影采样、附加光、normal map、描边、面部 decal 投影和相机后处理；金属像素保留 diffuse，不凭空用缺失的 specular 代偿。可降低捕获分辨率，但增加匹配的覆盖重绘及材质快照成本，不能仅凭少了 shader 操作声称更快。

当前实际证据限于 t15／D3D11 的合成材质 GPU 检查，以及本地一个角色／服装、四方向、蒙皮时间跳转的离屏捕获。没有验收全角色／全服装、原版视觉一致性、移动设备、Vulkan／Metal 或帧时。独立检查命令为 `--photo-mode --validate-planar-character <output-directory>`；需要使用者自己提供兼容资产，不进入默认摄影流程。详细执行记录见 [渲染记录](rendering.md)。
