# 分层透明与低分辨率特效

`LowResolutionFxRenderer` 接收当前 HDR、不透明深度、相机和显式排序的几何列表。`LowResolutionFxSettings.enabled` 默认关闭。独立桌面几何路径已执行整图与真实相机验证，完整舞台和移动性能仍需单独验收。

## 顺序与混合

提交 `LowResolutionFxSurface[]` 时按宿主需要的远到近顺序排列。每个表面选择 `Full`、`Half` 或 `Quarter`，分别将工作附件宽高除以 1、2、4 并向上取整。相邻同分辨率的 Alpha／Additive 表面组成一批；分辨率变化或 Distortion 表面形成合成边界。模块不为减少 draw 而把所有半分辨率表面移到最后，不隐式按材质或实例 ID 重排。

颜色附件保存预乘 RGB 与累计覆盖率。Alpha 使用 `C*a + background*(1-a)`，Additive 使用 `C*a + background`。加法表面的覆盖 alpha 为零，所以它可以和 Alpha 共用累积目标：位于后方的加法发光仍会被后方之后提交的透明面衰减。最终 HDR 的 alpha 始终保持输入值；低分辨率附件的 alpha 专门表示本批透过覆盖。

每个折射表面读取它之前所有批次已经合成的 HDR。折射场存储覆盖加权的 UV 偏移、眼深度和覆盖率，解析后按覆盖率混合原色与折射采样。不会采样自己正在写入的目标，也不会让后提交的颜色倒流到先前的折射背景。

提交顺序是显式契约。交叉网格、单个网格内部三角形和不同深度的透明层不能靠对象中心排序保证正确。宿主应拆分相交表面、排序，或将需完整精度的表面保留为 Full。本模块不实现逐像素透明排序。

## 深度与边缘

每个使用的工作尺寸都从**当前完整深度**生成眼空间 min／max 范围，奇数尺寸的缩减覆盖实际对应的完整像素，不丢弃末行末列。低分辨率几何按最近不透明深度测试。

上采样逐一检查四个双线性样本及完整像素深度。深度范围混杂、样本不相容、深度无效，或效果颜色／覆盖变化超过阈值时，先保留该像素背景，再以原始几何和原始提交顺序在完整分辨率重画该像素。其它区域使用低分辨率效果。重画在材质着色前拒绝无需修复的片元，不使用调用者的 stencil 或改写不透明深度。

这是独立的保守重建策略。PDF15／30 展示 Full Transparent、Downscale Transparent、Downscale Distortion、Upscale Transparent、TAA 的顺序；PDF31 指出低分辨率透明网格用于较重粒子、光晕和体积光，但没有给出重建公式。原文“1/4 分辨率”的全部尺寸约定未公开，因此本 API 明确使用宽高除数。

低分辨率绘制与局部完整分辨率重画也见 [NVIDIA GPU Gems 3 第 23 章](https://developer.nvidia.com/gpugems/gpugems3/part-iv-image-effects/chapter-23-high-speed-screen-particles)。本实现使用自己的 min／max 深度相容判断、预乘覆盖和显式排序，不复制该章节代码或原版 shader。

边缘修复仍不能发现所有在低分辨率完全消失的细小几何。细碎高频特效应选 Full。过小的深度容差会让斜面附近更多像素重画，效果阈值过大则可能保留明显模糊；这些设置影响质量和成本，不能从 draw 数量推导性能提升。

折射双线性采样逐 tap 排除画外、保护像素、无效深度和比折射面更近的不透明几何，再归一化剩余权重；没有有效 tap 时保留原色。它不会恢复被遮住的背景，也不能从一个不透明深度推断已合成的半透明层深度。

## 几何与材质输入

每个表面提供下列一种几何：

- 调用者拥有的三角网格 `mesh`、`localToWorld`、`submesh`。
- `MeshRenderer` 或 `SkinnedMeshRenderer` 和 `submesh`，由 Unity 提交当前几何。模块不修改共享材质和 Renderer 状态。

调用者负责将这些特效排除在相机常规绘制之外，避免重复显示。蒙皮更新、离屏更新策略和骨骼动画由宿主管理；模块不强制修改 `updateWhenOffscreen`。直接 ParticleSystemRenderer、原版顶点动画和任意第三方材质不自动适配；宿主可以提交生成后的粒子网格。

材质输入包含线性 `linearRadiance`、`opacity`、可选顶点颜色、UV 圆盘渐隐、软交点距离，以及调用者拥有的线性无 mipmap `Texture2D`。纹理上限 4096×4096，UV 使用 ST 变换后 Clamp，整数读取加显式双线性，不继承纹理的 Repeat、Trilinear 或各向异性设置。折射偏移的两个分量均以视口高度计量；贴图 RG 可以贡献附加偏移，alpha 提供覆盖。

可选 `settings.fog` 为 Alpha 表面提供逐表面的入射雾光与衰减，为 Additive 表面只提供衰减。折射直接读取已经合成的背景，不另加雾。宿主先对不透明背景计算对应雾，再绘制这些透明面，避免把已合并的颜色统一按不透明深度再次雾化。

## 宿主与生命周期

```csharp
using GakumasPhotoMode;

var fx = new LowResolutionFxRenderer(); // 每个相机复用，结束时 Dispose。
var settings = new LowResolutionFxSettings {
    enabled = true,
    surfaces = orderedSurfaces
};
if (fx.TryRender(currentHdr, new FogVolumeDepth(currentEyeDepth), camera,
        settings, out var frame)) {
    UnityEngine.Graphics.Blit(frame.color, destination);
} else {
    UnityEngine.Graphics.Blit(currentHdr, destination);
}
```

输入只接受同尺寸、完整视口、线性、非 MSAA／非 XR／非动态尺寸的当前 HDR 与深度。HDR 支持 RGBAFloat／RGBAHalf／R11G11B10；眼深度支持 RFloat／RHalf，0 为天空；Device 深度支持 RFloat／Depth，按提供的相机投影解码。无效深度不充当天空。可选线性 R8／RFloat protection 中 R>0 或非有限值保护输出。

输入的时间、相机、抖动和像素网格身份由宿主保证。没有全局时钟、场景搜索或截图输入。材质配置与几何在一次调用期间不得修改。

Photo Studio 的显式接入点为 `lowResolutionFx` 与 `lowResolutionFxDepthProvider`，位于背景雾之后、体积光／光晕与 TAA 之前。默认未绑定、未启用时不分配资源；提供器缺失、异常或渲染失败时保留当前输入，报告 `LowResolutionFxUnavailableReason`，不复用旧结果。已经合成透明层后再使用单深度体积雾仍有遮挡限制，不能由此宣称完成联合介质传输。

返回的 `Frame` 是借用。下一次调用、任何目标丢失或 Dispose 会使旧帧失效。`TryGetLastBatch` 只返回对应尺寸最后一次使用后的附件，不是每批历史。模块持有两张完整 RGBAFloat HDR 和每个使用尺寸的一张 RGBAFloat 效果／一张 RGFloat 深度范围；未使用尺寸及时释放。`maximumTargetMiB` 在分配前检查这些目标的字节预算，最多提交 256 个表面。该预算不包含输入、网格、贴图和驱动成本。运行前检查 Float 附件绘制、采样与颜色混合能力，失败时不假设平台支持。

## 已执行范围与剩余工作

实际 Player 比较独立的逐层颜色／折射参考、带纹理的有限旋转平面、顶点颜色、软交点和逐面雾。透视／正交／偏轴、移动相机、单像素遮挡、不同分辨率／HDR 格式、目标丢失／关闭／释放均有控制。当前单骨蒙皮与独立 CPU 顶点形变得到相同整图，64 个有序软粒子组成一个动态网格，动画回跳可恢复同一画面。

纹理软平面的完整分辨率解析误差最大约 2.12e-5，Half／Quarter 最大约 0.00175／0.00623；64 粒子 Half 与完整分辨率解析参考的最大差约 0.0141。这些是自制场景的线性 RGB 测量，不是原版舞台质量分数。原生 D3D11 对照另检查深度缩减、加法／Alpha、折射背景与重画、完整 HDR 和后续消费；完整记录见 [渲染记录](rendering.md)。

仍需完整角色透明、复杂相交层和所有美术参数覆盖，以及移动端内存／实际 GPU 帧时验证。P07 的连续体积积分、P08 的镜头光学效果仍各自拥有绘制调度，尚未合并为本模块的同一批次；本阶段不据此宣称所有低分辨率重特效都已统一。完整 Forward+ 和逐透明层介质传输同样属于独立的后续工作。
