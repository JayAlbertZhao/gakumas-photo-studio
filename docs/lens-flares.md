# 可配置 Flare 与 Ghost

`LensFlareSettings`／`LensFlareEmitter`／`LensFlareElement`／`LensFlareRenderer` 是默认关闭的独立光学效果模块，对应 PPT129 的 Flare／Ghost 和 PDF31 的低分辨率镜头光晕用途。原文使用 ProFlare，但未公开其数据或算法；本包不包含该插件、原版材质、贴图或参数。

## 接入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var emitter = new LensFlareEmitter {
    position = new Vector3(1, 3, 8),
    linearRadiance = new Vector3(3, 2, 1),
    occlusionRadius = .15f,
    elements = new[] {
        new LensFlareElement {
            shape = LensFlareShape.Disc,
            halfSize = new Vector2(.1f, .07f), softness = .8f
        },
        new LensFlareElement {
            shape = LensFlareShape.Ring, axisPosition = 2,
            halfSize = Vector2.one * .12f,
            linearTint = new Vector3(.2f, .6f, 1)
        },
        new LensFlareElement {
            shape = LensFlareShape.Star,
            halfSize = new Vector2(.3f, .03f), falloffExponent = 4
        }
    }
};
var settings = new LensFlareSettings {
    enabled = true, emitters = new[] { emitter },
    resolution = LensFlareResolution.Half, occlusionSamplesPerAxis = 4
};
var flares = new LensFlareRenderer(); // 每个相机复用，宿主结束时 Dispose。
// HDR 和正向眼空间深度必须是同一帧、同一相机、同一像素网格。
if (flares.TryRender(hdr, new FogVolumeDepth(linearDepth), camera,
        settings, timelineSeconds, out var frame)) {
    Graphics.Blit(frame.color, destination);
} else {
    Graphics.Blit(hdr, destination); // 失败原因见 UnavailableReason。
}
```

宿主更新 emitter 的世界位置、强度和元素即可，不要求场景中存在 Unity Light。模块不扫描发光材质，不读取截图、相机全局状态或原版 shader。若要把光源 Transform 与配置绑定，由宿主在调用前显式更新。

## 元素与动画

每个 emitter 可包含多个 Disc、Ring、Polygon、Star 或 Texture 元素，总预算 32 个 emitter、1024 个元素。`axisPosition` 沿源位置到画面中心的连线定位：0 在源位置，1 在中心，2 在对称 Ghost 位置，允许 −8 到 8。`halfSize` 与 `offset` 的两个分量都按视口高度计量，X 自动补偿宽高比；拉长 Disc／Star 可形成横向光条。`alignToAxis` 可跟随轴线旋转，再叠加 `rotationDegrees`。

`softness` 控制边缘，`falloffExponent` 控制曲线；Ring 有半径和宽度，Polygon／Star 使用 `sides`。Star 在局部半径 1e-4 内使用明亮中心，避免精确中心的角度奇点。所有形状独立解析生成，不需要本仓库提供位图。

Texture 元素引用调用者的线性、无 mipmap、最大 4096×4096 `Texture2D` atlas，`atlasRect` 为像素矩形。shader 用整数读取和显式双线性插值，限制在矩形内部，避免邻块渗色；RGB 乘 atlas 的覆盖 alpha，再加到 HDR，非有限 atlas 样本不产生光。源 `linearRadiance`、元素 `linearTint` 均按线性 RGB 使用，不隐式转换 sRGB。模块不读取 CPU 贴图数据，也不取得 atlas 所有权。

`TryRender` 接受显式 `timeSeconds`，不用全局时钟。元素 `rotationSpeed`（度／秒）、源 `pulseAmplitude`／`pulseFrequency`／`pulsePhaseDegrees` 可连续动画，暂停或回跳相同时刻可得到相同结果。更复杂的曲线／随机序列由宿主确定性地更新配置。配置不提供跨线程快照，调用期间不要修改它。

这是可编辑的屏幕光学近似，没有模拟镜片曲率、镀膜、波动光学或原版 ProFlare 处方。它与 Bloom 的亮部扩散独立，多个彩色 Ghost 不通过复制一张后处理亮度图伪造。

## 源可见性与遮挡

先投影源，再在朝向相机的世界半径圆盘上采样**当前完整分辨率深度**。`occlusionSamplesPerAxis` 可为 1／2／4／8，方格中落在单位圆内的样本分别为 1／4／12／52 个，使用可见比例缩放该源所有元素。没有跨帧随机抖动或陈旧可见性缓存。前景挡住一半源时，整组光晕相应变弱；在 Ghost 的输出位置不再做表面深度测试，因为它表示相机内部的光学效果。

`occlusionRadius` 和 `depthBias` 使用世界单位，后者降低被比较的源眼深度，避免光源被自身灯罩完全遮掉。配置过大同样会让真实遮挡失效，应按场景调节。NaN／Infinity／负数、近裁面前的深度不视为可见。天空可见。源在近裁面前、远裁面后或相机后方时整组隐藏。

默认画外深度未知，按遮挡处理。`offscreenMargin` 指定源离开画面后的淡出范围；`outsideScreenVisibility` 显式决定圆盘画外样本的权重，默认 0，设为 1 也不代表测得画外无遮挡。`occlusion=false` 可选择无深度遮挡的美术光晕。`fadeStartDistance`／`fadeEndDistance` 控制距离淡出；`directionalAttenuation`、rotation、inner／outerAngle 可让灯具背面不发出光晕，角度为完整角度。

把外观元素与源遮挡／淡出分开也是通用设计，参见 [Unity Lens Flare 组件说明](https://docs.unity3d.com/Packages/com.unity.render-pipelines.universal@14.0/manual/shared/lens-flare/lens-flare-component.html)。本模块不依赖该 SRP 组件，圆盘布点、profile 和动画均是自己的实现，未复制其源码。

## 输入与资源约定

HDR 接受线性 RGBAFloat／RGBAHalf／R11G11B10。`LinearEye` 深度接受 RFloat／RHalf，正数表示眼深度、0 表示天空；`Device` 接受 RFloat／Depth，按当前 GPU 投影和反向 Z 解算。源和深度必须同尺寸，调用者负责帧、jitter 和相机身份相符。可选同尺寸线性 R8／RFloat `protection` 中 R>0 或非有限值使输出保留原像素。

支持完整视口的普通透视／正交／偏轴相机，不支持 XR、MSAA、动态尺寸、mipmap、输入互相别名或使用自己的输出作为输入。源圆盘不支持带斜切的投影。当前实现面向标准刚性相机视图；任意非刚性自定义视图没有验收。

成功返回借用的 `Frame`，包含最终 `color`、只含 RGB 光学效果的 `artifacts`、逐源 `visibility`。最终 alpha 精确保留输入，artifacts 的 alpha 为 0。下一次调用（包括失败／关闭）、目标丢失或 Dispose 使旧帧无效，检查 `IsCurrent` 后再使用。失败释放自有目标／buffer，不返回陈旧结果；输入和 atlas 仍属于调用者。关闭或没有有效元素时返回 false 且无资源，后者没有错误原因。

## 分辨率与宿主顺序

`Full`／`Half`／`Quarter` 分别把效果附件的宽高除以 1／2／4 并向上取整；遮挡始终读取全分辨率深度。实例化包围矩形只界定覆盖区域，片元从实际像素中心反算旋转后的 profile，避免插值精度改变细光条或 atlas 边缘。全分辨率合成显式双线性重建效果，不继承 Trilinear／Repeat／强制各向异性采样。

Photo Studio 的显式桥接为 `OriginalStyleRenderPipeline.lensFlares`、`lensFlareDepthProvider`、`lensFlareTimeSeconds`。它在雾／体积光之后、TAA／DOF／Motion Blur／Bloom 前调用。默认关闭，缺少提供器或执行失败时保留当前输入并报告 `LensFlareUnavailableReason`。

这里已提供独立光晕的低分辨率绘制／上采样，没有自动拆分和排序任意透明粒子、折射、体积光与角色。PDF31 的完整 Downscale Transparent／Distortion 调度仍属于 P09。透明遮挡需宿主明确提供可用深度或自己的可见性策略；只有不透明深度时不能推断半透明多层遮光。光晕本身的时域对应／去噪也未单独实现。

## 验收与成本边界

公开自制 Player 夹具比较独立 CPU 解析 profile、圆盘深度可见率、低分辨率附件和 HDR 合成整图；另有真实 `Camera.Render` 和原生生产／消费字节验证。包含多源、atlas、动画 seek、部分／完全遮挡、近远／画外、投影／相机移动、全／半／四分尺寸、保护、目标生命周期与旧摄影回归。具体数值和保留的失败见 [渲染记录](rendering.md)。不从自制场景推导原版舞台整体画质一致。

每帧三个 draw：逐源可见性、所有元素的一次实例化绘制、HDR 合成。自有目标为全尺寸 RGBAFloat 输出、所选尺寸的 RGBAFloat 效果和最多 32×1 RFloat 可见性。1080p 的 Full／Half／Quarter 约为 63.3／39.6／33.6 MiB，不含输入／atlas／驱动开销；实例 buffer 最大约 82 KiB。低分辨率会损失细光条和小 Ghost 的采样质量，不保证提高步数或后续 TAA 能恢复。尚未测移动帧时、Vulkan／Metal、跨平台光栅精度、完整角色／复杂舞台与多层透明质量。
