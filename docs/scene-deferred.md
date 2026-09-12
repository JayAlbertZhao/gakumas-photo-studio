# 可选场景 Deferred 与材质贴花

`SceneDeferredCamera` 是 Built-in Forward 相机之前的显式场景绘制模块。输入自己的网格与 PBR 材质数据，先绘制 GBuffer，再按数组顺序投影贴花，最后计算 HDR 光照并写入主目标的颜色和深度。角色与透明物体继续走宿主 Forward，并接受已写入的场景深度测试。默认 Photo Studio 不挂载该组件。

参考技术范围是 PPT 116–120 与 PDF 22–29。实现不包含原版 shader、Def ABI、资产或讲演图。这里的方向光 GGX/Smith/Schlick、Lambert 间接漫反射与高度密度函数是独立模型。可选的点／胶囊／面光源见 [场景贴花灯](scene-decal-lights.md)。尚未实现源管线的完整移动 Deferred/Forward+、ShadowMask、GI 生产、反射整合或 RenderPass/Memoryless 复用。

## 宿主接入

表面可选 `SceneGiInput` 将预计算 Lightmap／SH 输出到独立第五 MRT，再控制基础 GI、动态乘色和背向补光。默认不增加该目标；完整输入与单位见 [GI 接入](scene-gi.md)，烘焙生产仍需独立准备。

```csharp
using GakumasPhotoMode;
using UnityEngine;

// 由宿主管理并最终 Release/Destroy；组件不会修改相机或 Renderer 状态。
camera.targetTexture = new RenderTexture(1280, 720, 24,
    RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
camera.targetTexture.Create();
camera.allowMSAA = false;
camera.renderingPath = RenderingPath.Forward;
camera.clearFlags = CameraClearFlags.SolidColor;
const int sceneLayer = 25;
sceneRenderer.gameObject.layer = sceneLayer;
camera.cullingMask &= ~(1 << sceneLayer);

var scene = camera.gameObject.AddComponent<SceneDeferredCamera>();
scene.sceneLayers = 1 << sceneLayer;
scene.surfaces = new[] {
    new SceneDeferredCamera.Surface {
        renderer = sceneRenderer, materialIndex = 0,
        inputs = new SceneDeferredCamera.MaterialInputs {
            albedo = new Vector3(.4f, .3f, .2f),
            mos = new Vector3(0, 1, .5f)
        }
    }
};
scene.decals = new[] {
    new SceneDeferredCamera.Decal {
        localToWorld = decalTransform.localToWorldMatrix,
        inputs = new SceneDeferredCamera.MaterialInputs {
            albedoMap = yourDecalTexture, alpha = .8f
        },
        albedoWeight = 1
    }
};
scene.sceneEnabled = true;
// 也可在自己的显式离屏工作流中调用 camera.Render()。
// 渲染后 TryGetFrame(out frame) 返回只读借用的材质附件。
```

宿主须把所有登记场景表面放在 `sceneLayers`，并从此相机的 `cullingMask` 排除这些层。这保证同一表面不会再经原材质 Forward 光照。重叠层、重复子网格、无效输入拒绝整次场景提交，`UnavailableReason` 给出原因。此模块不偷偷改变相机层、材质、Renderer enabled、全局 shader 参数或其他相机。

开启时原场景材质并不参与绘制。材质输入需由宿主明确提供，不能把已经包含光照的背景 RGB 当作 albedo。禁用组件只停止独立场景链并释放其资源；是否改回原场景层与材质由宿主决定。未注册且位于已排除层的对象不会自动出现在画面中。

## 材质与投影契约

| 输入 | 采样／混合约定 |
| --- | --- |
| `albedoMap` RGB、`albedo` | RGB 相乘并限制到 0–1；颜色纹理的 sRGB 解码由 Unity 纹理导入设置决定，数值系数为线性值 |
| `albedoMap` A、`alpha` | 相乘；几何按 `alphaCutoff` 裁剪，贴花作为覆盖率，仅混合一次 |
| `normalMap` | **线性 RGB** 编码的切线法线 `rgb*2-1`；不接受 DXT5nm/Unity normal import 的隐式打包，空值为 (0,0,1) |
| `mosMap` RGB、`mos` | 线性 metallic/occlusion/smoothness，逐分量相乘，空图为白色；不是原版 Def 排列 |
| `emissionMap` RGB、`emission` | 线性 HDR 辐射相乘，最大 65504；既不乘第二次 alpha，也不再接受直接光照 |
| `uvST` | 表面 UV0 或贴花局部 XY+0.5 的 scale/bias，四类纹理共用 |
| `receiverGroup` | 表面 0 拒绝贴花，1–255 与投影器精确匹配；角色不在 GBuffer 中 |
| 每通道 weight | albedo、normal、emission 及 MOS 三个分量独立 0–1；未选择通道保持原值，贴花数组顺序决定重叠顺序 |
| `localToWorld` | 局部 [-0.5,0.5] 单位体积，XY 投影；支持旋转、非均匀缩放；变换由宿主逐帧更新 |

几何支持显式 MeshRenderer/SkinnedMeshRenderer 子网格、UV0、alpha cutout、cull、`vertexScale`、切线法线和非均匀变换。网格必须有法线，使用 normal map 时还必须有切线。带 MaterialPropertyBlock 的 Renderer 会被拒绝，避免原 block 的同名参数覆盖显式输入；请把所需参数放入 `MaterialInputs`。不自动复制原 shader 的自定义顶点动画、Stencil、LOD 选择或不同 pass 的特殊几何。

投影从同一相机的正线性深度重建世界坐标，再测试体积和 receiver group。`minimumFacing` 与投影器局部 -Z 对应的世界法线比较。贴花法线把投影 X/Y 方向投到接收面切平面中，再混合并归一化；投影 X 与法线平行时保留原法线。这里不声称等同于原版法线混合公式。

`heightOcclusion` 仅调整 AO 覆盖率：

```text
heightCoverage = saturate((height * heightMap.r - (localZ + 0.5)) / heightFade)
aoWeight = textureAlpha * alpha * mosWeight.y * heightCoverage
aoOut = lerp(aoIn, decalAO, aoWeight)
```

这让移动表面随高度淡出局部阴影；没有高度图时取 1。其他材质通道不受高度开关影响。PPT 展示了效果但未给出该公式，所以保留独立命名与数值契约。水坑可以通过法线、MOS 和颜色组合表达材质变化；动态水波、折射、反射整合仍属于后续专用表面阶段。

## 光照、附件与生命周期

四个相机私有附件存 albedo+coverage、世界着色法线+receiver group、MOS+正眼空间深度、HDR emission。深度使用 float32 通道，其余使用 half；另有私有 24-bit 几何深度。无有效贴花时只有基础 GBuffer 和光照，不分配贴花 scratch，也不执行贴花 pass。开启贴花额外分配一组附件并 ping-pong，全屏 pass 成本随有效贴花数线性增长，尚未做体积裁剪/instancing/移动带宽优化。

直接光使用显式世界表面朝光方向 `lightDirection` 和线性 `lightRadiance`。`ambientIrradiance` 是宿主输入的统一间接漫反射照度；可选 [场景 GI](scene-gi.md) 使用显式 Lightmap／SH 输入，AO 只作用于间接漫反射。可选 [贴花灯](scene-decal-lights.md) 在 float32 目标累加实际直接光；主方向光及 Spot／Point 可使用 [光源阴影](scene-light-shadows.md)，不自动读取 Unity 全局灯光。输出在场景照明完成后只写入一次，然后允许宿主 Forward 几何进行正常深度测试。

可选 `screenShadow` 在材质 GBuffer 之前增加实际几何预通道和 RG8 resolve。只保存网格法线与正视空间深度，R 供主灯直接光、G 供间接漫反射；后者由显式世界胶囊的射线半球积分产生。主灯／Spot／Point 源深度在该预通道之前提交，附加灯不重复绘制阴影。两张借用目标通过 `Frame.screenGeometry`／`Frame.shadowOcclusion` 暴露，跟随相机生命周期。关闭时保留原来的主灯深度直接采样与调度。几何选择、量化、内存和未完成 GTAO 的边界见 [ScreenShadow](scene-screen-shadow.md)。

仅支持 Built-in Forward、完整视口、非 XR/MSAA/动态分辨率、带深度的固定线性 ARGBHalf/ARGBFloat 2D 目标。暂不支持直接 backbuffer、相机叠加保留旧深度或 SRP。`TryGetFrame` 应在 `Camera.Render` 之后或 `OnRenderImage` 中读取。附件为借用对象，不得写入、Release 或保存跨渲染的使用权，`IsCurrent` 检查帧序和目标生命周期。尺寸变动、目标丢失会重建；禁用或无效输入释放私有资源，不释放宿主目标。

## 验收范围

每相机限制 1–4096 个登记表面、最多 128 个贴花及每轴 4096 像素。此限制只约束输入上界，不表示如此配置适合移动设备。现有 `SceneDepthData`／SSR／Planar／统一反射的接收器检查仍遵守旧宿主层约定，不能把其已有接口直接绑到本模块排除的场景层并宣称整合完成；需要后续明确的材质／几何数据桥接。

应用 `--self-test-actor-rendering` 包含自制场景 GPU 检查，覆盖各材质通道实际光照、CPU 数值对照、投影／高度／重叠／接收组、Forward 前后遮挡、cutout 深度孔、相机投影及资源生命周期。完整执行结果记入渲染记录；源场景、全部服装、移动驱动和性能并不能由这些检查推导。

实现使用显式 [CommandBuffer.DrawRenderer](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.DrawRenderer.html) 与 [BeforeForwardOpaque](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CameraEvent.html)；Unity 不为这种手工绘制建立标准灯光数据，因此光照输入由本模块显式管理。
