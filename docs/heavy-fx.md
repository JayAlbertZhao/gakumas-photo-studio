# 联合重特效调度

`HeavyFxRenderer` 将连续介质、有序几何与镜头光学效果接入同一套工作附件。它是默认关闭的独立桌面后端；宿主显式提供相机、当前 HDR／深度、参数与时间，不依赖角色应用或私人参考数据。

PDF15／30／31 给出低分辨率透明、扭曲、上采样及随后 TAA 的顺序，并列举重粒子、光晕和体积光。PPT129／131 列出光学与动态光体积效果，但未公开联合混合公式。本模块采用独立规则，不附带原版 shader、VLB、ProFlare 或参考资料。

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

设完整尺寸像素数为 N，每个实际使用分辨率的像素数为 L，则 `TargetBytes = 33*N + 24*ΣL + 4*可见性源数`。33 来自两张 RGBAFloat HDR 和一张 R8 决策图；阴影图仍另计。这个桌面正确性后端不以附件总数或分辨率缩小宣称移动端性能收益。

## 摄影宿主

显式桥接为 `OriginalStyleRenderPipeline.heavyFx`、`heavyFxDepthProvider`、`heavyFxProtection` 与 `heavyFxTimeSeconds`，位于不透明反射合成之后、TAA 之前。默认关闭。联合模式与旧几何／体积／光学、距离／球形雾桥接互斥；同时启用会保留不透明输入并报告 `HeavyFxUnavailableReason`。关闭联合模式后原路径恢复。

已有自制混合场景的整图数值、真实相机生产消费链，以及默认摄影／真实角色 Planar 回归。有限纹理／顶点色软表面和一个单骨蒙皮的移动／回跳另有联合介质控制。完整舞台、多骨服装、复杂相交透明、移动端内存与实际帧时仍需分别验收；具体执行范围见 [渲染验证](rendering.md)。
