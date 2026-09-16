# 当前运动驱动的 Motion Blur

`MotionBlurRenderer` 是独立的线性 HDR 当前图像重建模块；`SceneDeferredCamera` 提供可选的实际几何运动／可见性适配。默认关闭，不为旧摄影路径新增附件或绘制。

显式 SRP 宿主另有 [整帧 Motion Blur](desktop-host.md#可选整帧-motion-blur)：
`FrameMotionBlur` 转换角色／场景 Half4 运动与当前深度，分离透明／特效保护和
`ExcludeTaa` 分类，在 TAA／DOF 之后复用同一个滤波器。该接入的自制桌面
D3D11／Vulkan 控制与原生消费链已验证；完整角色动态质量仍待验收。

PPT 126 列出自研 MotionBlur，PDF 15／32 的顺序为 TAA → DOF → MotionBlur → Bloom → FSR → Diffusion → UI；两份资料没有公开滤波公式。本模块采用自己的 tile／邻域最大速度、局部第二方向与深度权重实现，参考 [McGuire 等，2012](https://casual-effects.com/research/McGuire2012Blur/index.html) 和 [Guertin 等，2013](https://research.nvidia.com/publication/2013-11_fast-and-stable-feature-aware-motion-blur-filter) 的公开技术思路。不附带论文图像、原版源码／shader／资产，也不宣称恢复了原版滤波器。

## 独立输入

```csharp
using GakumasPhotoMode;
using UnityEngine;

var settings = new MotionBlurSettings {
    enabled = true,
    shutterAngle = 180,
    samples = 32,
    maximumRadiusPixels = 24
};
var blur = new MotionBlurRenderer();
// currentHdr 和 visibleMotionDepth 都由宿主生产，不能传模块自己的输出。
var input = new MotionBlurInput(currentHdr, visibleMotionDepth, secondsSincePreviousSample);
if (blur.TryRender(input, settings, out var frame)) {
    Graphics.Blit(frame.color, destination);
}
// 宿主退出时释放；frame 仅为借用，不能 Release 或跨下一次调用保留使用权。
blur.Dispose();
```

| 输入 | 契约 |
| --- | --- |
| color | 已创建、线性 HDR 的 ARGBFloat／ARGBHalf／RGB111110Float；有限、加权求和不溢出的 RGB；alpha 原样保留 |
| motionDepth RG | 当前 raster UV − 上次对应顶点投影 UV，使用 shader 纹理坐标，不是像素速度或相反方向 |
| motionDepth B | **当前**正 view depth；不同于 SceneMotion 原始附件中的上次 depth |
| motionDepth A | 恰好 1 表示可消费；其余值、非有限分量、非正深度或转换后速度溢出均保护当前像素 |
| sampleInterval | 两次实际采样之间的秒数；0、过小或超过 maximumSampleInterval 时零曝光 |
| jitterDeltaUv | 当前 jitter − 上次 jitter；真实运动为 rawMotion + jitterDeltaUv |
| colorToGuideUv | 原始颜色为 0；若稳定颜色在 uv 采样原始颜色 uv−jitter，则传 −当前 jitter |
| noJitterFlags | 可选同尺寸 R8_UNorm，解码 `round(R×255)` 的 bit 4；在输出和映射后坐标均保护；不把 ExcludeTAA bit 2 当作模糊排除 |

两个必选输入必须不同、同尺寸、Tex2D、单层、无 MSAA／mip／动态缩放。guide 必须 RGBAFloat。每次调用使旧 Frame 失效；缺失／错误输入会释放模块附件并返回原因，不返回陈旧结果。输出、tile 与 neighborhood 任一附件丢失都会使 Frame 失效；两个实例不共享附件，不修改输入的 sampler／wrap／anisotropy。

## 接入实际相机

```csharp
scene.motion.enabled = true;           // 实际当前／上次 GPU 几何对应
scene.motionBlur.enabled = true;
scene.motionBlur.shutterAngle = 180;
scene.motionBlurTime = simulationTime; // 可选；不传时使用 Time.timeAsDouble
post.sceneMotionBlurSource = scene;    // 必须是同一相机的 SceneDeferredCamera
```

需要先按 [SceneDeferred 契约](scene-deferred.md) 登记表面并配置相机；不会自动登记整个场景或更改宿主 layer。适配器在 BeforeImageEffects 用实际 framebuffer 深度、LEqual、相同 alpha／cull／vertexScale 重绘可见性，读取已生产的 [SceneMotion](scene-motion.md)，写自己的当前深度 guide。未登记、不可见或被普通 Forward opaque 遮住的像素保留当前颜色。

`Surface.excludeMotionBlur` 独立排除该表面；`Surface.temporalFlags.NoJitter` 也保护混合坐标像素。普通 ExcludeTAA 仍可运动模糊。ZWriteOff 透明叠层不能仅靠这个不透明深度识别，需宿主提供保护 mask 或在模糊之后合成。

时间与上次成功的 Camera.Render 对齐，允许同一 Unity tick 手动多次渲染。首次、相机 cut、对应失效、暂停、倒退、长间隔、显式 ResetMotionBlurHistory 或 guide 重建时保留当前颜色。没有存上一帧颜色来拖影。seek／切镜可调用 `ResetMotionHistory()` 同时重置几何和曝光时钟。

`OriginalStyleRenderPipeline` 只在显式配置时，于 DOF 后、Bloom 前消费。与同一 scene 的 TAA 联用且本次 TAA 成功时自动采用去 jitter 坐标。也可在自己的后处理链中显式调用 `TryResolveMotionBlur(camera, current, sourceIsDejittered, classification, out output)`；每次场景渲染最多消费一次。准备或消费失败仅跳过 Motion Blur，保持当前颜色；不清掉基础场景。`MotionBlurUnavailableReason` 可查看原因。

## 独立算法与调节

- 180° 对应半径为两次采样位移的 1/4；360° 为 1/2，表示以当前时刻为中心的曝光。Seconds 模式半径为 `displacement × exposureSeconds / (2×sampleInterval)`。相同速度与曝光秒数不因采样间隔变化而改变半径。
- 半径钳制到 maximumRadiusPixels；小于半像素的曝光运动视为静止，避免放大栅格对应噪声的方向。静止且已登记的背景仍能接收前景运动扩散。
- tile 边长等于最大半径，取最长速度；再取 3×3 tile 邻域最长速度。分数相等保留扫描先到者。不同方向的浮点速度极接近时，float32 比较与 double 比较可能选择不同向量。
- 4–64 个偶数候选，默认 32；dualDirections 将预算均分给邻域方向和当前局部方向，局部静止时第二方向垂直于主方向。固定 uint seed 的空间 hash 扰动采样，不随 Unity 帧数隐式改种子。
- 使用前后深度 softDepthExtent、锥形／柱形覆盖与方向点积权重，中心权重随候选数缩放。所有颜色及 guide 为整数 Load，避免硬件线性采样跨深度边缘或受调用方各向异性状态影响；最后归一化 RGB，保留中心 alpha。

该近似只看当前可见表面，不恢复遮挡中／画外内容，不模拟滚动快门、曝光内弯曲轨迹或多层透明。没有实现论文的随机 tile 选择、对角邻域相关性剔除、方向方差调度或完整 DOF 联合积分。DOF 已扩散的颜色没有独立运动层；复杂近远 DOF 与运动重叠仍需专门质量验证。

### 可选短曝光重建

`settings.subpixelReconstruction = true` 为短曝光增加有保护的双线性积分，默认 `false`，保留原整数重建。此选项也可通过整帧宿主的 `configuration.motionBlur` 或 Built-in 场景的 `scene.motionBlur` 设置，不改变默认摄影。

启用后保留小于半像素的有效运动。邻域最大曝光半径不超过 1 像素时，沿当前像素的运动方向做 `samples` 次居中的均匀中点积分；每次通过四个整数 Load 显式组成双线性颜色，不依赖调用方纹理过滤状态。邻点须有有效运动、未受保护、深度差不超过 `softDepthExtent`，曝光位移差不超过 0.5 像素。被拒绝或越界的权重回到当前颜色，不读取受保护颜色来填洞，也不重新归一化剩余邻点。此兼容检查是深度／运动判断，没有独立的表面身份信息。

整数地址和双线性小数权重都从相对位移分解，最后再加像素坐标。接近轴向的极小运动若先加较大像素坐标再取整，可能与编译器优化后的 `frac(offset)` 丢失不同精度，产生错误邻点；原生反例和近轴解析检查专门覆盖这个边界。

邻域半径在 1–1.5 像素间平滑过渡到既有长运动重建；更大运动不使用短曝光积分。零曝光与无效中心继续原样返回，alpha 保留。短分支采用固定中点，不使用长分支的 `sampleJitter`／`noiseSeed`。附件数量不变，但每个短曝光候选最多检查四个邻点，不能据此宣称更快或已经满足移动预算。

这个模型积分的是当前可见图像的分段双线性重建，不能恢复已经丢失的亚像素几何、被遮挡颜色或透明多层运动。自制正／反方向、深度／速度边界、保护和无效 guide 的完整图像已对照闭式积分；提高 16→64 候选时误差降低。真实曝光参考与闭式图像积分是两种不同证据，下节分别记录。

D3D11 一个实际角色动画帧的原生输入／输出也逐像素对照了该闭式模型：最大分量误差约 6.58e−6，499 个受保护／无效像素精确保留，61,439 个像素发生可测变化。该检查覆盖捕获帧的全部 512×512 像素；它验证重建数值，不替代真实场景曝光参考或其他平台的原生检查。

## 验证状态与成本

已在 Tuanjie 2022.3.62t15／D3D11 隐藏 Player 中用实际 Camera.Render 验证：刚体／相机运动、两骨蒙皮与 blendshape、CPU-readable 顶点变形、cutout、普通 Forward opaque 遮挡、NoJitter／ExcludeTAA 区分、jitter 与场景 TAA 联用、暂停／倒退／切镜、双相机、附件丢失及尺寸恢复。独立模块覆盖 4–64 候选、三种 HDR 输入格式、单像素／单行／单列／奇数尺寸、NaN／Inf／速度溢出、半径钳制、alpha、调用方 sampler 状态和生命周期。

整图 CPU 参考不排除边缘像素。整数采样地址按原生 D3D11 的 float32／FMA 形成，最大速度比较显式物化 float32 中间值；Mono 的较宽浮点局部变量会改变近水平速度的并列选择。另保留翻转读回行序的负控制，避免把 `graphicsUVStartsAtTop` 当作 ReadPixels 必须再翻转的依据。这些局部数值模型不推广为跨平台保证。

自制匀速前景的 64 次真实曝光采样对照中，未模糊 RGB MAE 约 0.02438，滤波约 0.01620；只是该场景的改善，不是曝光积分精确解。原生抓帧验证了两条实际 guide → tile → neighborhood → HDR → Bloom／Diffusion／post 消费链；25,026 个重建像素全部比较，最大分量误差约 1.08e−6，alpha 精确保留。以下预算由资源布局计算，不能替代实机帧时。

设 `W×H` 为输入尺寸、`R` 为最大半径、`T=ceil(W/R)×ceil(H/R)`：独立模块拥有 `16WH + 32T` 字节的三张 RGBAFloat 附件，三次 Blit；Scene 适配另增一张 `16WH` guide 和每可见登记表面一次重绘。默认关闭不新增这些资源。几何对应快照、TAA、DOF、原始 HDR／深度、对齐及驱动开销另算。

1920×1080、R=24 时，仅上述 Motion Blur 附件约 63.4 MiB（含 scene guide）。零曝光当前仍运行三阶段并持有附件，不把 passthrough 误报为零成本。更少候选也不等于已经证明移动性能更快。

当前已有 Built-in／离屏 D3D11 SceneMotion 路径及桌面 D3D11／Vulkan 整帧宿主的有限控制；移动端、Metal、XR／MSAA、Memoryless、完整角色材质／发丝透明和整舞台动态画质仍未验收。所有画质结论须限定到具体参考帧，不从程序正确或自检数量推导原版外观一致。

## 真实角色曝光积分诊断

在已经配置好调用方本地资产的 Player 上，用 `--photo-mode --validate-srp-actor-character <输出目录> --validate-actor-shadows --validate-desktop-character --validate-desktop-exposure` 运行。可另传 `--costume-label <服装标签>`，并分别选择 `-force-d3d11`／`-force-vulkan`。这是显式离屏诊断，不替换默认摄影或开启默认 Motion Blur；角色、参考图片与原版资料不随仓库分发。

诊断使用前／侧／后视角的 512×512 近景，分别运行相机平移和 6 倍速片段采样。曝光时钟与姿态采样时间分开，相机轨迹不夹带角色动画。帧间隔 20 ms、180° 快门对应当前时刻前后各 5 ms；每个参考由 32 个实际子时刻、冷历史渲染在线性 HDR 中用 double 累加，再转换为 float32。另做独立 16 次积分以观察参考收敛，不能把 32 次积分称为解析真值。

关闭 TAA、DOF、FX、反射、FSR、Bloom 和调色；仅用零强度 Diffusion 转换为 float32 输出。分别保存未模糊、模糊、每个积分输入、16／32 次平均、重播、冷帧、暂停与移除角色对照的 PNG／Float4 RAW，以及 `character-exposure-diagnostics.json`。角色区域来自同视角角色可见／排除图差，轮廓为其边界向两侧扩展 3 像素；它们不是语义发丝标签。报告同时保留线性与 `max(RGB,0)/(1+max(RGB,0))` 映射后的 MSE，有限数值检查不表示画质达标。

一个自备服装在上述两种桌面 API 的实测结果：

- 相机轨迹中，角色区域相对未模糊图的参考 MSE 比值为约 0.070–0.090，轮廓约 0.201–0.225；这只是该曝光、速度和内容的改善。
- 动画轨迹的三个视角都未通过“超过 100 像素发生可测模糊”的正控制，整份报告保留失败，不能记为角色动画 Motion Blur 完成。16／32 次参考积分差异约为未模糊误差的 0.5%–0.7%，仍需更强运动和更多采样验证。
- 对 D3D11 正面动画帧的原生检查发现：guide／曝光时钟有效，最大曝光半径约 0.6695 像素；整数采样的非中心位置距离至少 1 像素，而该帧 Cone／Cylinder 的最大权重支持范围只有约 0.70295 像素，因此非中心候选权重为零，输出仅有约 1.79e−7 的累加舍入差异。这是当前短曝光整数重建的实际限制，不能单纯归因于显式 0.5 像素截止。尚未修改滤波器或放宽门限。

冷帧、暂停与重复轨迹的控制分别验证；即使这些控制通过，也不抵消上述原整数路径的动画正控制失败。该限制保留在兼容分支；透明保护留下的锐利叠层、复杂遮挡及 DOF 联合曝光也仍需继续处理。

随后加入上述可选短曝光重建。设置 `GAKUMAS_CHARACTER_EXPOSURE_SUBPIXEL=1` 再运行诊断，可在同一进程、同一曝光参考下同时记录原整数路径 `legacy`，并检查角色／轮廓区域相对未模糊图改善、相对 legacy 不退步。

同一服装、三视角、两种桌面 API 的新配对实测中，原有动画响应门限均通过；相对同进程 legacy，动画角色区域的映射 MSE 降低约 18.6%–25.6%，轮廓区域降低约 9.8%–18.7%。相机轨迹也保留改善和 legacy 对照；各 API 的 12 份积分平均和 72 项区域指标已从保存的 RAW 独立重建。该比例仅对应这六个动画配置，不能外推到全服装、全运动或原版观感。

旧失败报告仍保留，不能把开启新选项后的结果归到旧路径。完整角色透明／DOF 联合质量与移动性能仍未验收。
