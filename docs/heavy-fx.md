# 联合重特效调度

`HeavyFxRenderer` 将连续介质、有序几何与镜头光学效果接入同一套工作附件。它是默认关闭的独立桌面后端；宿主显式提供相机、当前 HDR／深度、参数与时间，不依赖角色应用或私人参考数据。

PDF15／30／31 给出低分辨率透明、扭曲、上采样及随后 TAA 的顺序，并列举重粒子、光晕和体积光。PPT129／131 列出光学与动态光体积效果，但未公开联合混合公式。本模块采用独立规则，不附带原版 shader、VLB、ProFlare 或参考资料。

## 实验性透明几何曝光

`HeavyFxSettings.exposure` 默认关闭。它给每个显式提交的表面保存 GPU 顶点端点，
在共享的快门时刻移动几何、按原有顺序合成全部 alpha／additive 层，最后平均完整
合成结果。没有把多个透明层塞进一个像素速度，也没有先分别平均每层后再合成；
后者会丢失遮挡覆盖的时间相关性。

```csharp
settings.exposure.enabled = true;
settings.exposure.samples = 8;              // 2、4、8、16、32
settings.exposure.shutterAngle = 180;       // 当前时刻为中心，0–360 度
settings.exposure.maximumTrackedVertices = 1000000;
// TryRender 的 timelineSeconds 必须与几何采样时间一致。
// 跳转、换镜头、跳过本相机的渲染之后：
fx.ResetMotionHistory();
```

这是有明确边界的独立桌面实现：

- 只接受无光照的 alpha／additive 三角形，网格拓扑须可读；支持显式 DrawMesh 或
  Renderer 的实际 GPU 顶点流。拒绝静态合批、Renderer property block、扭曲、
  联合介质／镜头光学以及启用的几何雾，不静默借用不匹配的 shader。
  不复刻源材质自己的顶点位移程序；程序生成／修改的 UV、颜色及纹理内容须由宿主管理版本。
- 两个端点之间按顶点线性外推；不能恢复瞬时加速度、旋转曲线或突变拓扑。材质、
  纹理版本、拓扑和表面实例变化会冷启动。原地修改 UV／顶点颜色等程序数据时，
  调用方须递增 `surface.motionRevision`。不要每帧重新创建表面实例。
- 冷帧、暂停、时间倒退／长间隔、投影变化和超过阈值的相机切换返回当前几何。
  仅启用几何积分时仍使用**当前不透明颜色和深度**，DOF 仍只在之后执行一次。相机运动、
  移动遮挡物、透明景深和时间变化的光照尚未实现联合积分。真实角色联合诊断已
  发现相机轨迹相对旧路径的局部误差增加；此选项不应作为已验收的全场景曝光默认值。
- 需要 geometry shader、float32 渲染／混合目标；没有移动平台验收。每个表面需要
  两张 float4 顶点快照，每顶点至少 32 字节并包含纹理尺寸填充；另有全分辨率
  float4 积分附件。`maximumTargetMiB`／`TargetBytes` 是本模块目标的逻辑预算／
  当前分配，不是驱动峰值、CPU 拓扑数组或整个场景显存预算。
- 基础模式只有 FX 几何和合成按 samples 重复，不重复整场景／CPU 蒙皮／后处理；
  下述显式同相位后处理回调会按 samples 重复其过滤工作。
  静止但历史连续的几何也可能执行多个样本，因此开启后的成本并非自动为零。
  `ExposureSamples`、`ExposureSnapshotDrawCalls`、`ExposureSnapshotBytes` 和
  `DrawCalls` 可供宿主记录。快照绘制另提供分项计数，也包含在 `DrawCalls` 总数中。
- `frame.color` 是曝光结果，alpha 和受保护像素直接保留当前源值。
  `TryGetLastBatch` 的 effect／range 以及 `repairMask` 只是**最后一个子时刻**的
  工作附件，不能当成时间积分后的独立透明层复用。

真实角色开关与保留的失败见 [联合曝光诊断](motion-blur.md#透明特效景深与曝光的联合诊断)。

### 可选不透明背景的同相位重投影

默认 `settings.exposure.reprojectOpaque = false`。开启后，显式提供未混入透明层的
当前 HDR、当前运动／置信度，以及当前表面在前次采样中的视深度：

```csharp
settings.exposure.reprojectOpaque = true;
var motion = new MotionBlurInput(opaqueHdr, motionDepthConfidence, sampleInterval);
fx.TryRender(opaqueHdr, new FogVolumeDepth(currentEyeDepth), camera, settings,
    timelineSeconds, out var frame, opaqueMotion: motion,
    expectedPreviousDepth: previousEyeDepthOfCurrentSurface);
```

`motionDepthConfidence` 为 float4：RG 是当前 UV 减前次 UV，B 为当前正视深度，
A 为 1 表示有效对应；其他值保留当前像素。两个深度附件均为 RFloat。
`expectedPreviousDepth` 是**当前可见表面**在前次相机／姿态下的深度，不能传上一帧
屏幕深度图。输入采样间隔须与 FX 几何端点一致，且此路径目前要求无 jitter。
核心输入不依赖角色应用；`SrpActorForward.Frame` 已提供适配所需的端点附件。

每个共享快门时刻先由 `OpaqueExposureRenderer` 重投影未混合颜色及深度，重新生成
低分辨率深度范围，再按原顺序渲染透明几何并合成。积分发生在完整合成之后。
透视使用前／当前 clip W 比例；正交使用单独分支。反向流求解固定执行四次，
缺失／越界对应和不兼容的深度／运动样本回退当前值，不编造看不见的表面。
颜色可进行双线性重建；输出眼深度取选定可见表面的相位深度，不混合前／背景深度。
旧的深度加权在轮廓处可能产生不存在的中间平面，进一步改变透明遮挡及景深 CoC。
这项修正仍不能恢复真正的隐藏表面或保证逆向对应在遮挡边界选中了正确表面。
新增颜色／深度工作附件名义大小为每像素 20 字节，纳入原有目标预算。

在 `DesktopFrameRenderer.Settings` 中还须启用 `actorMotion`、需要时启用
`includeSceneMotion`，并启用 `motionBlur`，使它与 FX 使用相同的 ShutterAngle
及 maximumSampleInterval。当前集成明确拒绝 TAA／jitter；不会暗中关闭调用方选项。
`FrameMotionBlur.TryPrepareOpaqueInput` 只生成未混合的 guide 和时钟，不先做一次
无用的颜色模糊。宿主在联合积分后执行一次 DOF，不再对结果二次 Motion Blur；
返回帧的 `coherentOpaqueExposure` 为 true，传统 `motionBlur` 分项为空。

独立 GPU 对照覆盖解析运动平面、透视／正交深度、双透明面相反运动及遮挡穿越，
包含 Full／Half／Quarter 和“冻结不透明背景”的负控制。D3D11／Vulkan 各新增
61 项控制；联合颜色／深度的最大误差小于 5e-7。真实角色 D3D11 原生帧确认
16 个相同快门相位的颜色／深度附件实际流入对应 FX，完整合成积分再流入 DOF，
没有二次 Motion Blur。与上一几何积分相机捕获相比，draw 数从 179 增至 207；
512² 时两张额外不透明工作附件共 5 MiB。此处是资源／调用计数，不是 GPU 帧时收益。

这仍是当前可见图像的重建，不是完整场景的逐时刻重渲染。遮挡显露处缺少隐藏颜色，
基础配置的 DOF 使用当前深度且只执行一次；其相机轮廓和少量动画区域仍有严格质量失败。
默认应用保持旧路径，不把此选项标作全场景、物理镜头或移动性能验收完成。

## 可选正向可见面归属

可另选 `settings.exposure.forwardOpaqueOwnership = true`，在当前可见像素正向投影
发生重叠时，按正眼深度选择最近表面，等深度按源像素编号稳定决定。没有投影中心
落入的像素沿用受限逆向重建；无效 guide／NoJitter 像素仍保护。它只使用当前可见
数据，不能补出新露出的背面或屏幕外信息。

此选项默认关闭，要求 compute／R32UInt load-store 能力；额外两张 uint 图共 `8*N`
字节，计入分配前的联合预算和 `TargetBytes`。每个非零相位有清空、最小深度、稳定
owner 三次 dispatch，分别由 `OpaqueExposureRenderer.DispatchCalls` 和联合生产者的
`OpaqueDispatchCalls` 报告，不冒充 draw，也不据此宣称帧时改善。冷启动／零相位
跳过 dispatch；关闭会释放这两张图。独立生产者对应可选参数 `forwardOwnership`。

## 同相位后处理回调

`TryRender(..., sampleFilter: filter)` 可在每个快门时刻的有序 FX 合成之后、
曝光平均之前处理该时刻完整颜色。此回调仅支持 `reprojectOpaque` 模式，收到
对应的颜色、眼深度与有符号快门相位；它不读取全局时间。

```csharp
bool Filter(RenderTexture color, RenderTexture eyeDepth, float phase,
    out RenderTexture filtered, out string error)
{
    filtered = null;
    error = null;
    if (!lens.TryRender(color, eyeDepth, lensSettings, out var sample)) {
        error = lens.UnavailableReason;
        return false;
    }
    filtered = sample.color;
    return true;
}
```

输出必须是相同尺寸、已存储的线性 RGBAFloat。可以原样返回传入颜色；其他输出
须由调用者管理，不能返回生产者的其他附件。输入只借用到本次回调结束，不得修改、
释放、保留供异步使用或重入生产者；输出在下一次回调前即被消费。回调失败、异常、
重入或非法输出会使整次曝光失败，不发布部分积分。冷启动／暂停也调用一次零相位
回调；最终恢复原始 alpha／保护像素并返回生产者自己的目标。

回调的资源、预算和 draw 数由调用者另计，`HeavyFxRenderer.TargetBytes/DrawCalls`
仅统计其自身工作；回调不得将自己的分配误报为零成本。桌面宿主提供默认关闭的
`depthOfFieldDuringExposure`，在联合不透明曝光与 DOF 同时启用时，复用宿主已有
DOF 工作目标逐相位处理，跳过最后一次 DOF。它不增加一套每相位永久保留的目标，
但会按采样数增加 DOF 工作量。

返回 `Frame.depthOfFieldExposureSamples` 为实际 DOF 相位数；此时
`Frame.postEyeDepth` 与 `encodedCoC` 为 null，避免把最后一个相位的深度／CoC
冒充积分图像的唯一对应附件。`eyeDepth` 仍表示当前几何深度。该模式沿用不透明
表面的 lens depth，未解决透明层独立镜头深度、隐藏表面或物理孔径积分。

## 输入和顺序

```csharp
var fx = new HeavyFxRenderer(); // 每个相机持有，结束时 Dispose。
var settings = new HeavyFxSettings {
    enabled = true,
    medium = authoredMedium,     // VolumetricLightingSettings
    geometry = orderedGeometry, // LowResolutionFxSettings
    optics = authoredOptics      // LensFlareSettings
};
if (fx.TryRender(opaqueHdr, new FogVolumeDepth(opaqueEyeDepth), camera,
        settings, timelineSeconds, out var frame)) {
    Graphics.Blit(frame.color, destination);
} else {
    Graphics.Blit(opaqueHdr, destination);
}
```

各子配置的 `enabled` 决定是否参与。连续介质只出现一次，先计算至当前不透明深度；几何按显式提交顺序绘制；镜头效果最后加入。相邻、同分辨率、无折射边界的不同类型直接写入**同一张预乘颜色／覆盖附件**，之后执行一次上采样。没有先为三个独立 renderer 各分配并合成完整 HDR。

分辨率变化结束当前批次。每个 Distortion 面单独构成边界，只读取已经完成的先前 HDR。镜头效果表示相机内的光学现象，不按 Ghost 输出位置的表面深度遮挡；源可见性仍使用当前完整深度。接口不允许把连续介质插在任意前景面之后，也不为了合批重排几何。

`Full`／`Half`／`Quarter` 均明确表示宽高分别除以 1／2／4 并向上取整。每个使用尺寸共用一张 RGBAFloat 效果和一张 RGFloat 不透明 min／max 眼深度。完整 HDR 使用两张 RGBAFloat 乒乓附件。Flare 仅额外持有逐源 RFloat 可见性和实例数据，不分配单独的光学颜色／HDR。

## 介质与透明层

介质背景种子写入散射 `S(D)` 和不透明度 `1-T(D)`，D 为本帧不透明深度。Alpha 面的颜色先变为 `T(d)*surfaceColor + S(d)`，再按该面的透明度混合，d 为该片元实际眼深度。Additive 面只乘 `T(d)`。多个灯的散射相加，消光不按灯数重复。

这使前方介质散射在透明面混合中得以保留；不把已经合并的所有透明颜色统一按远处不透明深度雾化。表面自身的 `fog=false` 是明确的美术旁路。`attenuateBackground=false` 取消背景及表面颜色的介质消光，但散射积分仍沿用其独立模型，属于美术加法模式。

本模块不推断透明几何对光源的半透明遮挡，不解决相交表面的逐像素排序，也不恢复折射后的多层深度。折射读取已合成背景，因此不能当作完整折射介质输运。连续体积与距离／球形逐表面雾同时启用会返回错误，防止对同一颜色重复施加不同介质模型。

## 重建与生命周期

低尺寸效果累积完成后，以完整深度、低深度范围、效果 RGBA 变化和低采样中心覆盖范围决定完整像素是否需要重画。每批只生成一次完整尺寸 R8 决策图，合成及所有介质／几何／光学重画共同读取它，不在每个重画片元重复检查整组低尺寸采样。接受区执行手动双线性重建；拒绝区保留先前 HDR，并按原顺序重画整个混合批次。拒绝分支在积分／材质着色之前。保护像素保留原色，最终 HDR alpha 始终保留最初输入；效果附件 alpha 只表示组合不透明度。

联合重建默认绝对／相对眼深度阈值为 .001／.0001，RGBA 变化阈值为 .001。它们是保守的独立质量参数，不保证任意细碎几何、极端光束和所有艺术配置的误差上界。需要完整精度的元素应使用 Full。保守重画可能接近全图，附件共享与 draw 数不能证明 GPU 加速。

`maximumTargetMiB` 默认 512，在分配前检查联合颜色／效果／深度范围／R8 决策图及光学可见性预算；明确不包含输入、网格、纹理、阴影图及驱动成本。子 renderer 的独立目标预算和重建阈值不用于联合路径，统一使用 `HeavyFxSettings`。输入契约沿用独立模块的线性 HDR／当前深度／相机／保护限制，并限制每维不超过 16384。没有全局深度猜读、场景扫描或时钟。

`Frame` 是借用，下一次调用、任意自有附件丢失、失败／关闭及 Dispose 后失效。`TryGetBatchInfo` 返回当前计划；`TryGetLastBatch` 只返回该尺寸最后一次使用后的附件，`repairMask` 只保留最后一个批次的完整尺寸决策，不保存历史批次。Full 的决策图全零。所有失败保留调用者输入，不发布部分完成的 HDR。

设完整尺寸像素数为 N，每个实际使用分辨率的像素数为 L，未启用曝光时 `TargetBytes = 33*N + 24*ΣL + 4*可见性源数`。33 来自两张 RGBAFloat HDR 和一张 R8 决策图；启用曝光另计积分附件及顶点历史，同相位不透明重投影再增加 `20*N`；阴影图仍另计。这个桌面正确性后端不以附件总数或分辨率缩小宣称移动端性能收益。

## 摄影宿主

显式桥接为 `OriginalStyleRenderPipeline.heavyFx`、`heavyFxDepthProvider`、`heavyFxProtection` 与 `heavyFxTimeSeconds`，位于不透明反射合成之后、TAA 之前。默认关闭。联合模式与旧几何／体积／光学、距离／球形雾桥接互斥；同时启用会保留不透明输入并报告 `HeavyFxUnavailableReason`。关闭联合模式后原路径恢复。

已有自制混合场景的整图数值、真实相机生产消费链，以及默认摄影／真实角色 Planar 回归。有限纹理／顶点色软表面和一个单骨蒙皮的移动／回跳另有联合介质控制。完整舞台、多骨服装、复杂相交透明、移动端内存与实际帧时仍需分别验收；具体执行范围见 [渲染验证](rendering.md)。
