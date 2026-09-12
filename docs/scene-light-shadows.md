# 场景灯光源阴影

`SceneDecalLight.shadow` 为默认关闭的 Spot／Point 阴影。它从光源视角绘制显式登记的遮挡物，再让场景直接光读取该深度；不使用宿主相机深度代替光源可见性，也不把静态烘焙 GI 当作动态阴影。

```csharp
spot.shadow.enabled = true;
spot.shadow.nearPlane = .05f;
spot.shadow.depthBias = .002f;
spot.shadow.filter = SceneShadowFilter.Pcf3x3;
scene.decalLighting.shadows.tileResolution = 256;
scene.decalLighting.shadows.casters = new[] {
    new SceneShadowCaster { renderer = movingObjectRenderer }
};
```

光源和 caster 的变换在每次宿主渲染前读取。接收器仍为 `SceneDeferredCamera.surfaces`；caster 不必是接收器，也不必在宿主相机可见层中。主方向光使用下方独立配置。Capsule／Area 阴影尚未实现：对这些场景灯形状显式请求阴影会拒绝场景帧，不会悄悄退化成无阴影。

## Spot 几何与采样约定

- Spot 局部 +Z 朝前，外锥完整角度作为透视 FOV，宽高比 1；near 为正轴向距离，far 为 light.range。灯光自身仍按径向球截断范围，不改为轴向衰减。
- atlas 为独立 RFloat 颜色深度和 24 位硬件深度附件。硬件裁剪／深度测试选择最近几何；颜色保存 `光源轴向距离 / range`，空白清为 1。颜色深度不随 reversed-Z 改变方向。
- 接收点先沿着色法线移动 `normalBias`，投影后使用 `(轴向距离-depthBias)/range <= 保存深度`。两种 bias 都以世界单位表示；默认 normalBias=0。过大 bias 会使阴影脱离物体。
- 投影 near/far 或 XY 范围之外的采样可见性为 1；near 以内的遮挡物不参与。将 near 设得过大会漏遮挡，不能把此范围外行为当作无限范围阴影。
- Hard 使用一个 point 深度比较；Pcf3x3 使用九个 point 比较的平均，逐 tap 限制在本 tile 的半 texel 内边界，避免读到相邻光源。它是固定分辨率滤波，不是物理面光源软阴影。
- `strength` 在完全可见与比较结果之间线性插值。可见性乘到当前灯的正向漫反射、镜面、可选背向漫反射；基础 GI、其他光源与 emission 不受该灯阴影压暗。GI 乘色只修饰直接光的颜色响应，顺序与原模块一致。

投影使用 [GL.GetGPUProjectionMatrix](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/GL.GetGPUProjectionMatrix.html) 的 RenderTexture 路径；局部投影 UV 按图形 API 翻转一次，tile 行号不再翻转。每次 [SetRenderTarget 后 viewport 恢复为完整目标](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Rendering.CommandBuffer.SetViewport.html)。平台的硬件 Z 约定见 [Unity 平台差异](https://docs.unity3d.com/cn/2022.2/Manual/SL-PlatformDifferences.html)。实际验收范围为本机 t15 / D3D11，其他 API 仍需实测。

## Point 六面阴影

Point 使用相同的 `light.shadow` 配置，不需要设置 Spot 锥角。源点固定为 `light.position`，向全部方向发光；rotation 不旋转或限制阴影范围。范围仍为原 Point 的径向 range／线性衰减。每个点光源绘制六个 90° 透视面，顺序为 +X、-X、+Y、-Y、+Z、-Z。±Y 面 up=+Z，其余 up=+Y。面局部 right=up×forward。

颜色图保存逐片元的 `length(world-position)/range`。顶点 shader 输出光源到世界顶点的向量，经透视插值后才求长度；没有把顶点长度线性插值当作平面径向深度。nearPlane／range 定义径向球壳，片元在球壳外丢弃。透视硬件 near=nearPlane/√3，避免球壳合法几何在三面角落被轴向近平面提前裁掉。硬件 far=range，最近硬件深度与同一射线上的最近径向深度一致。

接收点先加世界法线 normalBias，再比较 `(径向距离-depthBias)/range`；球壳外可见性为 1。以光源相对向量的最大绝对分量选面，同值优先 X，其次 Y。在最大分量的 `1e-5` 相对带内也按同值处理，稳定相机重建／矩阵浮点误差造成的精确接缝选面。该带不是 PCF 半径。

Hard 比较一个 texel。Pcf3x3 在选定面上偏移九个方向，将每个方向重新选面、投影，再读取正确 tile。跨边／角落的 tap 会读取相邻面；最后半 texel clamp 仅防止越界读到其他灯或 atlas 空白。PCF 是固定面分辨率的离散核，存在 texel 量化和有限核的方向依赖，不承诺数学连续、时域稳定或物理球光源软阴影。

Point 和 Spot 可任意混排。每灯阴影参数仍为 112 字节：Spot 的旧矩阵／参数字节不变，Point 矩阵存光源相对平移，options.w 存首 tile+1。此布局是内部实现，不是公开序列化 ABI。旧灯光参数的 144 字节布局不变。

## Caster、所有权与资源

Spot、Point 与主方向光共用以下 caster 与借用状态约定；附加灯 atlas 和主方向光各自持有深度目标，不共用可变材质或全局参数。

显式 `MeshRenderer`／`SkinnedMeshRenderer` triangle submesh，`materialIndex` 须同时存在于网格和材质槽。模块不读取源 shader 的变形、材质关键字或 Cutout 规则。需要裁切时提供 `alphaMap` 的 alpha、uvST、alpha 和 cutoff；`alphaMap.a * alpha < cutoff` 被裁掉。默认 cutoff=0，alpha=0 的几何也会写深度，需设置正 cutoff 才是空裁切。

`vertexScale` 是蒙皮后的局部顶点缩放，cull 默认 Back，可显式改 Off。禁用、inactive、forceRenderingOff 的 caster 不提交；不改这些借用状态，不创建 Unity Light，不写全局 shader 参数。PropertyBlock、非三角形、缺失 UV、无效子网格、奇异变换或未创建／MSAA alpha 纹理被明确拒绝。蒙皮流由 Unity 更新；离屏角色由调用方保证骨骼更新，例如在自己的角色上设置 `updateWhenOffscreen`。不保证任意原版 GPU 变形／发丝透明材质自动匹配。

资源归宿主相机独占。`maxShadowedLights` 最多 16，统计光源而非面数；Spot 占一 tile，Point 占六 tile，最多 96 面。atlas 为 `ceil(sqrt(面数)) × tileResolution` 的正方形；每 tile 32–2048 的 2 次幂，atlas 边长上限为 4096 及设备限制的较小值。例如一个 Point／256 tile 的 atlas 为 768²，一个 Point／2048 tile 则超限拒绝。最多 1024 个登记 caster。超预算拒绝，不丢灯。每个面绘制全部启用 caster，没有 caster 空间剔除／静态缓存或时间摊销；成本随面数与 caster 数相乘。

原灯数据保持 144 字节。仅存在可见且非零阴影灯时启用 shader 变体，Instanced 路径额外绑定每灯 112 字节的独立阴影矩阵／参数缓冲；Scalar 绑定等价逐灯参数。无阴影灯、强度为零、关闭或剔除后释放阴影目标／材质／缓冲。后端、尺寸及数量变化会刷新资源。没有阴影的旧路径不增加深度目标与采样。

`Frame.lightShadowAtlas` 是借用的诊断纹理，仅在 `Frame.IsCurrent` 为真时有效；不要缓存、销毁或当作已经完成的体积光 API。`LightShadowMapCount` 和 `LightShadowCasterDrawCalls` 说明提交量，不是 GPU 性能数据。

## 主方向光

`SceneDeferredCamera.mainLightShadow` 默认关闭，为已有 `lightDirection`／`lightRadiance` 提供一个固定范围的正交阴影图。它不创建 Unity Light，不使方向光产生距离衰减。

```csharp
scene.lightDirection = new Vector3(0, 0, -1); // 接收点朝向光源的方向
scene.mainLightShadow = new SceneDirectionalShadowSettings {
    enabled = true,
    origin = new Vector3(0, 0, -10),
    up = Vector3.up,
    halfSize = new Vector2(5, 3),
    nearPlane = .05f,
    farPlane = 20,
    resolution = 512,
    filter = SceneShadowFilter.Pcf3x3,
    casters = new[] { new SceneShadowCaster { renderer = movingObjectRenderer } }
};
```

`origin` 是阴影视图原点，不是方向光的有限发光位置。视图 +Z 取 `-scene.lightDirection.normalized`，up 决定 roll。up 必须非零且不平行于该方向；两者归一化后的叉积平方小于 `1e-6` 时拒绝配置，不隐式更换 up 造成画面跳转。halfSize 是光视图 XY 的半宽／半高，near/far 是相对于 origin 的正轴向距离。调用方更新 origin、up 和覆盖范围以覆盖所需场景。

构造 [Matrix4x4.Ortho](https://docs.unity3d.com/cn/2022.3/ScriptReference/Matrix4x4.Ortho.html) 后转换成 RenderTexture 的 GPU 投影。正交投影 `clip.w=1`，所以 producer 与 consumer 都以 `dot(世界位置-origin, forward)` 算轴向距离；不能沿用 Spot 的 clip.w 当深度。颜色图保存轴向距离/far，空白为 1，最近几何仍由硬件深度选择。depthBias、normalBias、strength 与 Hard／PCF 的定义与上方一致。

接收点在有限 XY、near/far 以外时，主灯可见性取 1；它不会因离开阴影图就完全不发光。近于 near 的遮挡物被裁掉。此实现没有自动包围场景、级联、边界淡出、texel snapping 或静态缓存，移动视域和分辨率会改变栅格化阴影边缘，不保证无限范围或时域稳定的太阳光阴影。

主灯只把自身直接漫反射、镜面及背向漫反射乘以阴影；预计算 GI 的基础项、环境漫反射、emission、Spot／其他灯不随主灯阴影一起变暗。GI 乘色仍在主灯直接光路径内。它与 Spot 阴影可同时运行，主灯深度绘制在场景 GBuffer 之前，最终 HDR resolve 读取主灯深度及独立的其他灯辐射。

resolution 为 32–2048 的 2 次幂，独立于宿主目标大小；最多 1024 个显式 caster。全部直接光 scale 为零、零 lightRadiance 或零 strength 时没有主灯深度目标／draw；启用但无效的设置仍拒绝场景帧。关闭不读取其非法配置。每次主灯渲染均刷新输入，目标丢失、分辨率改变会重建，组件禁用会释放。`Frame.mainLightShadowDepth` 为当前帧借用纹理；释放后该帧不再 current。`MainShadowTargetCount`／`MainShadowCasterDrawCalls` 是资源／提交统计。

## 验收与待补范围

资产无关 Player 自检使用不出现在接收 GBuffer 中的移动遮挡片、独立光源到平面的 CPU 射线、相机透视／正交、离轴光、near/far、depth／normal bias、Cutout／UV、PCF、GI／镜面／背光、跨帧单骨蒙皮以及四 tile 与逐灯独立渲染叠加对照。另检查遮挡片倾斜时的透视深度、前后剔除和倒置提交顺序下的最近深度。记录中明确保留排除的栅格边缘窄带。

蒙皮验收范围为均匀缩放根节点、单骨、三组跨帧平移／旋转；与 CPU 变形后的静态网格完整阴影和深度对照，并核对原生 post-VS 顶点。早期非均匀父级缩放用例中，Unity 的实际蒙皮流与直接相乘的骨骼世界矩阵存在可见差异，已通过原生顶点定位并保留失败证据，没有放宽阈值。一般非均匀缩放／剪切、多骨及原版材质 GPU 变形尚未完成独立验收。

原生捕获开关为 `GAKUMAS_SELFTEST_CAPTURE_LIGHT_SHADOWS=1`；单骨和静态参考对照可用 `GAKUMAS_SELFTEST_CAPTURE_LIGHT_SHADOW_SKIN=1`。只用于已注入 RenderDoc 的专用自检 Player，不与其他捕获开关混用。

主方向光另外检查独立平行光射线、斜光、roll、矩形覆盖、近远裁剪、视域外保持照明、与 Spot 同时消费、跨相机资源隔离和宿主尺寸变化。均匀根缩放单骨有三组跨帧三轴旋转／位移，与 CPU 变形静态网格比较完整深度和阴影。原生开关 `GAKUMAS_SELFTEST_CAPTURE_MAIN_SHADOW=1` 捕获主灯与 Spot 共存帧。

Point 自检覆盖六轴、十二条边和八个三面角落，逐像素几何射线检查阴影内部；另检查六面逐 texel 射线径向深度、倾斜平面、球壳近平面、跨面 PCF 完整图像、移动源点／caster、Cutout、GI、单骨三组姿态、最近深度／提交顺序、多点与 Spot 的逐灯求和、跨相机资源及 16 灯／96 面预算。`GAKUMAS_SELFTEST_CAPTURE_POINT_SHADOW=1` 捕获六个真实遮挡片、一个 Point、一个 Spot 和主方向光的混合帧，用于检查实际视口、逐面深度、112／144 字节缓冲与消费绑定。只对本机 D3D11 做过原生验收。

对应 PPT110–112 的场景实时光源遮挡基础及 PDF13–23 的光源阴影调度方向。未完成 PDF 的 Actor／ScreenShadow 完整调度、烘焙 ShadowMask 3:3:2 打包、Forward+／透明／角色接收器、其他光源形状、移动 Memoryless/subpass 与成本验收。PPT131 的体积 Spot 积分及体积中的动态 DepthShadow 仍待接入，不随本模块标记完成。
