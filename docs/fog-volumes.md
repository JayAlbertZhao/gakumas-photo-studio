# 距离雾与多球介质

`FogVolumeSettings`／`FogVolumeBinding`／`FogVolumeRenderer` 是默认关闭的独立模块。对应 PPT 130 的距离雾／球状雾和 PDF 29 的 Emission 阶段用途。讲演未公布密度函数、混色公式与数值参数；下面是本项目自己的模型，不是原版 shader／参数复原。

## 单深度接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var settings = new FogVolumeSettings { enabled = true };
settings.distance.enabled = true;
settings.distance.density = .025f;
settings.spheres = new[] {
    new FogVolumeSettings.SphereMedium {
        center = new Vector3(0, 2, 5), radius = 3, density = .2f,
        linearColor = new Color(.2f, .4f, 1.2f, 1)
    }
};
var fog = new FogVolumeRenderer(); // 宿主持有、复用，结束时 Dispose。
if (FogVolumeBinding.TryCreate(settings, camera, hdr.width, hdr.height,
        out var binding, out var reason) &&
    fog.TryRender(hdr, new FogVolumeDepth(linearEyeDepth), binding, out var frame)) {
    Graphics.Blit(frame.color, destination);
}
```

输入是已经创建、相同尺寸的线性 HDR 颜色和深度目标。颜色接受 RGBAFloat／RGBAHalf／R11G11B10，输出 RGBAFloat，保留原始 alpha。`LinearEye` 接受 RFloat／RHalf 的正向眼空间深度：0 为天空，负数、NaN、Infinity、近裁面前的深度保留原色；超过远裁面截断。`Device` 接受 Depth／RFloat 的实际设备深度，使用相机实际 GPU 投影（包含 off-axis／jitter）解投影，清除值表示天空。模块不自行发现相机全局深度。

可选 `protection` 是同尺寸线性 R8／RFloat；R>0 或非有限值跳过雾。它只保护一个已经合成的像素，不能恢复被透明层遮住的多个深度。

`FogVolumeBinding` 复制设置和相机矩阵，之后修改原设置不影响此快照。相机、投影、尺寸、介质变化时重新创建；模块不猜测快照是否过期。禁止 XR、部分视口、MSAA、mipmap、动态尺寸目标和输入／自有输出互相别名。不支持的配置返回 false／原因，不发布陈旧帧。帧只在下一次调用、目标丢失或 Dispose 前有效；两个 renderer 独立持有目标。调用恢复原先的 `RenderTexture.active`。

## 密度、颜色和积分

所有距离使用世界单位，射线从**该像素的近裁面**到当前表面／远裁面；正交相机也从自己的近裁面像素开始。距离介质仅在沿射线的 `[startDistance, endDistance]` 中提供常数消光系数。球介质使用 `density * max(1 - distanceToCenter² / radius², 0)`，最多八个非零有效球；没有强制半径顺序，重叠时系数相加。

`linearColor` 是每个介质自己的非负线性 HDR 源颜色，alpha 不参与积分；接口不隐式做 sRGB 转换。透射率遵循光学厚度的指数衰减，参考 [PBRT 对透射率及沿射线相乘性质的说明](https://pbr-book.org/4ed/Volume_Scattering/Transmittance)。这里使用指定颜色代替真实多次散射求解，不实现光照相函数。

先排序所有进入／离开边界，再沿深度积分。每个区间分为 1–32 步（默认 8）；每步分别精确积分常数／抛物线密度，再用光学厚度加权的源颜色更新累积辐射。单色介质的模型积分精确到浮点误差；**不同颜色的重叠介质仍是分段均匀近似**，提高步数可减小误差。介质规范排序使输入数组排列不影响算术次序。不同前后位置的有色雾应产生不同结果，不能把所有球当成可任意排序的 alpha 层。

`maximumOpacity` 在最终透射率上施加美术上限并同比缩放入射颜色；默认 1，0 保留原图。上限小于 1 时不再具有物理介质的区间相乘性质。天空资格逐介质设置。数值预算：位置分量 ±1e6、半径 1e-4–1e6、密度 0–1e4、沿射线距离 0–1e6、RGB 0–65504；超界配置明确拒绝。

## 透明表面

先渲染不透明颜色与硬件深度，再运行一次雾，最后把透明表面从后向前绘制到雾后的颜色目标，深度测试使用原不透明深度附件。透明片元使用**自身世界位置**求雾，而不是后台不透明深度。`Resources/FogVolumeSurface.shader` 是可用的无光照示范材质；为宿主自有材质包含 `FogVolume.hlsl`，调用 `FogNear`／`FogIntegrate`，并通过同一 `binding.Apply(ownedMaterial)` 绑定设置和视图。

```csharp
binding.Apply(transparentMaterial);
transparentMaterial.SetVector("_LinearColor", new Vector4(.2f, 1.5f, .3f, .65f));
// 相同投影、尺寸与完整视口；opaqueDepthOwner 必须真的保存了 opaque 深度。
overlayCamera.clearFlags = CameraClearFlags.Nothing;
overlayCamera.SetTargetBuffers(frame.color.colorBuffer, opaqueDepthOwner.depthBuffer);
overlayCamera.Render(); // 仅透明层；之后才运行 TAA／DOF／Bloom 等后处理。
```

Unity 允许为相机分别指定颜色和深度附件，参见 [Camera.SetTargetBuffers](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Camera.SetTargetBuffers.html)。有图像后处理的相机常使用内部中间目标，不能默认认定最终 targetTexture 的 depthBuffer 保留了刚才的不透明深度。

示范表面 `_LinearColor` 使用 Vector 属性和 SetVector，避免 Unity 对 Color 属性的隐式 sRGB 转换。普通覆盖混合为 `alpha * foggedSurface + (1-alpha) * foggedBackground`；目标 alpha 使用覆盖合成。不要再对这些已经求雾的透明结果跑一次全屏雾。宿主负责正确的透明排序、深度附件、蒙皮／自定义顶点位移与材质自身照明；示范 shader 不代替原版头发 stencil、折射或有厚度的吸收介质。复杂相交透明排序、折射层、多重散射未解决。

## Photo Studio 的可选桥接

设置 `OriginalStyleRenderPipeline.fogVolumes` 和 `fogVolumeDepthProvider`。提供器在反射合成后、TAA／DOF／Motion Blur／Bloom 前被调用，必须返回当前颜色对应的深度。启用时替代旧距离雾＋单球雾；失败保留当前输入并报告 `FogVolumeUnavailableReason`，不偷偷使用旧参数。关闭时执行原旧路径，不创建新目标。

这个图像后处理桥接处理单深度输入，**不会自动重排已经画完的透明材质**。需要正确的逐表面透明雾时使用上面的显式不透明→雾→透明流程，再进入宿主后处理。完整低分辨率 FX 分层调度仍属于 P09。

## 验收与成本边界

实际 t15／D3D11 Player 新增 83 项雾的命名渲染／生命周期控制，含 perspective／ortho／off-axis、相机在球内、重叠及分离八球、天空／保护像素、非法输入、质量收敛、快照／尺寸／别名／双实例与后处理关闭恢复。整图与独立 2048 步世界空间积分比较；混色样例的 1／8／32 步最大误差依次为 0.107833／0.001966／0.000124，不能把低档数值记录当成高档精度验收。

原生捕获验证五组雾的生产／合成、实际下游消费和两次 Forward 表面绘制。五组完整 transfer／composite 共 63050 像素，无排除；4096 步独立积分对 transfer 最大差 0.000116，合成最大差 5.96e-8、alpha 精确。真实双透明层整图最大差 7.80e-5，同时验证 459 个后台透明片元被不透明深度挡住、698 个无不透明遮挡的双层重叠像素。保留的失败及默认回归见 [渲染记录](rendering.md)。这些是自制场景验收，不覆盖所有游戏角色材质。

模块创建两个全尺寸 RGBAFloat 目标、两次全屏绘制；1080p 目标约 63.3 MiB，不含输入和透明层。最多 20 个射线边界，积分成本随相交球数与每区间步数增加。没有声称达到讲演中的移动附件、性能或全场景画质；原版参数、完整角色透明与 Vulkan／Metal 实机仍待验证。
