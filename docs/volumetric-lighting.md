# 带动态遮挡的体积光

`VolumetricLightingSettings`／`VolumetricSpotLight`／`VolumetricLightingRenderer` 提供默认关闭的、有限均匀介质中的聚光灯单次散射。对应 PPT 131 的光束和动态 DepthShadow：宿主登记的真实几何先产生光源深度，沿相机射线积分时读取该深度，让移动物体遮住光束内部。没有附带 Volumetric Light Beam、HDRP 或原版 shader 代码。

## 独立接入

```csharp
using GakumasPhotoMode;
using UnityEngine;
using UnityEngine.Rendering;

var spot = new VolumetricSpotLight {
    position = new Vector3(0, 3, 0),
    rotation = Quaternion.LookRotation(new Vector3(0, -.2f, 1)),
    range = 12, innerAngle = 25, outerAngle = 45,
    linearRadiance = new Vector3(8, 6, 4),
    shadow = new SceneLightShadowInput { enabled = true }
};
var settings = new VolumetricLightingSettings {
    enabled = true,
    mediumCenter = new Vector3(0, 2, 6),
    mediumHalfSize = new Vector3(8, 4, 6),
    extinction = .08f, scatteringAlbedo = Vector3.one,
    anisotropy = .2f, samplesPerLight = 64,
    lights = new[] { spot },
    shadows = new SceneLightShadowSettings {
        tileResolution = 256,
        casters = new[] { new SceneShadowCaster {
            renderer = movingOccluder, cull = CullMode.Back
        } }
    }
};
var volume = new VolumetricLightingRenderer(); // 每相机持有并复用；结束时 Dispose。
// hdr、linearEyeDepth 是宿主本帧、同一相机/投影/尺寸的已创建目标。
if (volume.TryRender(hdr, new FogVolumeDepth(linearEyeDepth), camera,
        settings, out var frame)) {
    Graphics.Blit(frame.color, destination);
} else {
    Graphics.Blit(hdr, destination); // UnavailableReason 给出配置/资源失败原因。
}
```

每次渲染前更新 `spot.position`／`rotation` 和骨骼。模块不扫描场景、不读取全局阴影或原版材质；`SceneShadowCaster` 显式提供 MeshRenderer／SkinnedMeshRenderer、子网格、剔除方向、alpha 贴图／阈值／UV、顶点缩放。标准蒙皮由 Unity 绘制，任意自定义顶点动画和材质属性块不自动适配。未登记物体不会投射阴影。共享阴影输入的 `normalBias` 在体积内不产生偏移，因为这里没有表面法线；使用世界单位的 `depthBias`。

## 输入与生命周期

颜色接受线性 RGBAFloat／RGBAHalf／R11G11B10；输出两个 RGBAFloat 目标：`frame.scattering` 是相加的 RGB 散射，alpha 为 0；`frame.color` 是最终 HDR，alpha 精确保留输入。正向眼空间深度接受 RFloat／RHalf，0 表示天空；设备深度用 `new FogVolumeDepth(depth, FogDepthEncoding.Device)`，接受 RFloat／Depth，使用实际 GPU 投影解算清除值和反向 Z。深度与颜色必须来自同一帧、同一像素网格，包括 jitter；模块不能从纹理外观判断深度是否陈旧。

可选 `protection` 为同尺寸线性 R8／RFloat，R>0 或非有限值保留原色。非法逐像素深度（负值、非有限值、近裁面前的线性深度、超出 [0,1] 的设备深度）保留原色；线性深度超过远裁面则截到远裁面。`affectSky=false` 保留天空。拒绝输入互相别名或借用自己的输出，以及 MSAA、mipmap、动态尺寸、XR 和部分视口。

一次 `TryRender` 同步消费当前设置，不承诺跨线程快照。`Frame` 是借用结果：下一次调用（包括失败或关闭）、目标丢失、Dispose 后不可继续使用；先检查 `IsCurrent`。失败不发布陈旧帧，资源释放，调用恢复 `RenderTexture.active`。两个 renderer 互不借用目标。关闭时不分配目标；再次启用可重新创建。

## 积分模型与数值预算

介质是世界空间 AABB，半尺寸 0.001–10000；射线从每个像素的近裁面到当前不透明表面或远裁面，支持透视、正交、偏轴和相机在介质内部。聚光灯使用有限球形 range 与前向角锥的交集；内外角为完整角度，内角 0–外角、外角 0.1–179 度。不是把一个透明锥面直接叠到场景上。

`extinction` 为世界单位倒数的均匀消光系数（0–100），RGB `scatteringAlbedo`（0–1）确定散射系数。光源 `linearRadiance` 为显式非负线性 RGB（0–65504），不做 sRGB 转换；距离衰减为 `max(1-distance/range,0)^falloffExponent`，指数 1–8，内外角之间按余弦线性过渡。这是可调的有限支撑灯光模型，不是按瓦特／流明校准的逆平方点光源。

相函数采用 Henyey–Greenstein，`anisotropy` 范围 −0.95–0.95，0 为各向同性；正值增强顺着入射光传播方向的散射。方向约定和分母符号必须配套，参见 [PBRT 相函数说明](https://pbr-book.org/4ed/Volume_Scattering/Phase_Functions)。这里入射方向沿光子传播，出射方向朝相机，因此分母包含 `1+g²-2g*cosine`。

每灯先解出射线与有限角锥的交段，再划分 8–256 步（默认 64）。每步在中点采样相函数、灯光衰减、光源到采样点的介质透射和实际 Hard／3×3 PCF 阴影；眼方向的均匀透射在该步内解析积分。最终为 `source * T + scattering`。`attenuateBackground=false` 保留 source 并仅加散射，适合美术叠加；零消光得到精确的原图。多个灯共用一个介质，散射相加，背景只消光一次，最多 16 灯。

该模型不包含多次散射、空间变化密度、噪声／cookie、方向光／点光体积、时域降噪或原文未公开的参数。硬阴影边缘是数值积分的不连续点；增加步数能改善采样，但不保证任意细小遮挡或极端前向散射都有固定误差上界。

## 应用桥接与透明层

Photo Studio 可显式设置 `OriginalStyleRenderPipeline.volumetricLighting` 和 `volumetricDepthProvider`。提供器在旧／新雾之后、TAA／DOF／Motion Blur／Bloom 之前调用，须返回当前输入对应深度；缺失、抛异常或资源失败时保留输入并报告 `VolumetricLightingUnavailableReason`。默认关闭，不改变原摄影路径。

这是一层当前可见深度的体积积分，不能恢复已经合成的多层透明深度。宿主负责在恰当的透明／后处理阶段接入；尚无逐透明表面的光体积积分和自动特效重排。不要让 P06 的距离雾与此模块对**同一物理介质重复消光**。选择一个消光模型，或明确使用 additive-only 的美术叠加；两种近似串联不构成统一输运解。完整低分辨率透明／扭曲调度仍在 P09。

## 验收与成本

公开自制渲染夹具覆盖独立密集世界空间积分、内外相机／正交／偏轴、窄锥／硬边、正负相函数、步数收敛、天空／保护／无效深度、多个彩色灯及跨行阴影图集、刚体／骨骼／alpha 遮挡、尺寸／别名／双实例／丢失目标与默认恢复。实际相机生产深度并经后处理桥接消费；原生证据核对阴影→散射→HDR 合成→下游消费的真实字节，而不只检查有无绘制调用。量化记录见 [渲染记录](rendering.md)。

两个全尺寸 RGBAFloat 目标在 1080p 约 63.3 MiB，不含输入和阴影附件。`DrawCalls` 是每个有效灯一次全屏积分加一次合成；`ShadowCasterDrawCalls` 单独计数。源阴影每灯一 tile，分辨率 32–2048、图集边长最多 4096，还需深度附件；体积端每步 Hard 一次或 PCF 九次读取。未实现低分辨率／froxel 加速，未测手机、Vulkan／Metal、XR 或实际 GPU 帧时；资源数量不等于性能验收。原版整体画质和全角色材质仍需单独对照。
