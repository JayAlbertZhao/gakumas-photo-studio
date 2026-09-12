# 场景预计算 GI 与背向补光

`SceneGiInput` 给 `SceneDeferredCamera.Surface` 提供独立的预计算漫反射响应。PPT 111 的讲者备注说明先用白光烘焙场景，再压低基础 GI，让动态灯光乘上预计算结果；PPT 112 的补光只增加反向漫反射。PDF 22–23 则将 Lightmap／LightProbe 结果单独输出到 GI 缓冲。本实现保留这些数据流，公式、输入结构和绘制实现独立编写，不包含原版资产、shader 或讲演图片。

## 接入与输入

先按 [场景后端](scene-deferred.md) 登记网格，随后选择来源：

```csharp
surface.gi = new SceneGiInput {
    source = SceneGiSource.Lightmap,
    lightmap = myLinearBakedResponse,
    lightmapST = new Vector4(1, 1, 0, 0),
    encoding = SceneGiEncoding.LinearRgb
};
scene.giBaseScale = .1f;
scene.directionalGiWeight = 1;
scene.directionalBacklight = .2f;
// 可选贴花灯各自控制，仍可共同实例化。
light.giWeight = 1;
light.backlightScale = .2f;
```

| source | 数据与约定 |
| --- | --- |
| None（默认） | 不使用 GI 输入；该表面继续使用原来的 ambientIrradiance，动态乘色为单位值 |
| Lightmap | 借用 `lightmap`，从网格 UV2／TEXCOORD1 经 `lightmapST` 采样；与材质 UV/ST 独立 |
| RendererLightmap | 每次提交读取 Renderer 的 lightmapIndex／lightmapScaleOffset 和 LightmapSettings.lightmaps；CombinedDirectional 必须同时有 lightmapDir |
| Probe | 显式 `SphericalHarmonicsL2 probe`，按该表面的逐像素世界法线求值 |
| SceneProbe | 从已生成的场景探针数据插值；采样位置依次取输入 probeAnchor、Renderer.probeAnchor、Renderer.bounds.center |

SceneProbe 没有场景探针时显式拒绝，避免把环境色回退误记为烘焙 GI。该来源通过 Unity 的 [GetInterpolatedProbe](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LightProbes.GetInterpolatedProbe.html) 读取，可能同时包含启用的实时探针分量；需要固定预计算输入时应关闭该分量或显式提供 Probe。每个 Renderer 只插值一组 SH，不是每片元空间插值或 LPPV。SH 通过 [CopySHCoefficientArraysFrom](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/MaterialPropertyBlock.CopySHCoefficientArraysFrom.html) 打包，随后复制到相机持有材质的私有名称，不写共享 unity_SH 全局或源 Renderer 的 property block。

运行时只读取已有结果；生产由独立 Editor 工具 `SceneGiBaker` 显式执行，不会在普通摄影启动时烘焙。自制真实场景的 Lightmap、45 个烘焙探针、空间插值及单骨蒙皮 GI 已在桌面离屏 Player 验证；这不覆盖全部角色或原版整场景。

## 生产白光参考烘焙

从自己的 Editor 脚本调用；使用 asmdef 时引用 `Gakumas.Toolkit.Editor`：

```csharp
var bake = GakumasPhotoMode.Editor.SceneGiBaker.BakeSceneCopy(
    "Assets/MyScene.unity", "Assets/MyReferenceBake-001",
    new GakumasPhotoMode.Editor.SceneGiBaker.Options {
        texelsPerUnit = 8, atlasSize = 256, bounces = 2,
        lightmapper = UnityEngine.LightingSettings.Lightmapper.ProgressiveGPU
    });
// bake.scene 是生成的 Reference.unity；bake.json 含输出路径、绑定与哈希。
```

源场景必须已保存，目标必须是全新的 `Assets/` 子目录，项目必须为 Linear／Built-in。调用前自行保存或放弃所有脏场景；已有输出、路径穿越、无效设置、播放中或正在烘焙均拒绝。工具默认 ProgressiveGPU，允许明确指定 ProgressiveCPU；引擎不接受所选后端时报告失败，不把自动回退称作 CPU 验证。调用实际同步 [Lightmapping.Bake](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/Lightmapping.Bake.html)，运行时长取决于场景、硬件和烘焙缓存。

工具复制场景后才修改灯光、网格和材质：激活的灯保持位置／强度／范围，转为白色 Baked 灯；关闭颜色温度、天空／环境／反射基础输入和实时 GI。克隆每个活动 MeshRenderer 的网格并生成 UV2，克隆材质并关闭标准 emission，保留 albedo 参与间接反弹。每个接收器登记为 ContributeGI／Lightmaps，生成自己的 LightingSettings 和 LightingDataAsset。源场景及磁盘依赖文件／meta 的前后 SHA-256 写入结果；正常或失败退出均恢复调用者的已保存场景集合，失败产物留在新目录供检查，不覆盖或自动删除旧资产。

仅对可信的静态场景运行。烘焙依赖材质有效的 Meta pass；当前真实验收使用 Standard，不能保证任意自定义 shader 的 emission／透明度／法线都符合该契约。脚本 `ExecuteAlways`、嵌套场景、Terrain、蒙皮烘焙贡献、多场景 GI 和 Light Probe Proxy Volume 没有在本工具中验收。移动角色可消费场景探针，但不会被此工具作为静态 MeshRenderer 烘焙。参考光照包含固定的直接照明与间接反弹；运行时乘色不会重新计算移动遮挡。

加载生成的场景（或自行构建的 scene AssetBundle）后，为其静态表面选择 RendererLightmap，为动态接收器选择 SceneProbe。相加加载／卸载探针场景时，由宿主按 Unity 的 [Tetrahedralize](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LightProbes.Tetrahedralize.html) 约定重建探针空间；工具不在每次绘制时重建全局拓扑。不要把源场景的旧网格、UV2 或 lightmapIndex 与新烘焙贴图混用。

仓库提供 `GakumasPhotoMode.Editor.SceneGiBakeFixture.Build` 自制验证入口。给 Editor 设置全新的 `GAKUMAS_GI_BAKE_NAME`（字母／数字／连字符）和 `GAKUMAS_GI_BAKE_OUTPUT`（本地绝对目录），以 `-executeMethod` 调用即可生成房间／红墙／遮挡物、实际烘焙和 `reference-gi` scene bundle。其 `guards.json` 记录输入保护与源场景还原验证。给已构建的 Player 设置 `GAKUMAS_SELFTEST_GI_BAKE_BUNDLE` 为该 bundle 路径，再运行 `-batchmode --self-test-actor-rendering <输出目录>`，才会在原有自检结束后追加真实烘焙验证。不提供 bundle 时不运行此阶段。生成的场景、贴图、bundle、日志和截图均为本地输出，不进入公开文件清单。

可追加 `GAKUMAS_GI_BAKE_CONTROLS=1`：分别移除遮挡物、将红墙换成灰墙，再各自执行一次真实烘焙。`controls.json` 比较同位置／法线的实际烘焙探针，要求移除遮挡后照度响应提高、移除彩色墙后 RGB 恢复中性；不会仅用同一贴图的亮暗区域作为因果证据。

## 解码与单位

项目必须为 Linear 色彩空间。纹理按自己的导入设置进行硬件 sRGB 解码；字面线性 HDR 数据应使用线性纹理。

- LinearRgb：直接取采样后的 RGB，忽略 alpha。
- Rgbm：`RGB * decodeMultiplier * pow(saturate(alpha), decodeExponent)`。
- DoubleLdr：`RGB * decodeMultiplier`，忽略 alpha。

显式纹理和 RendererLightmap 都使用此解码契约，不按亮度、alpha 或文件名猜测编码。Multiplier／Exponent 须与目标平台的烘焙导入设置匹配，默认 LinearRgb 不能自动覆盖所有移动格式。Unity 的 [光照贴图技术说明](https://docs.unity3d.com/2022.3/Documentation/Manual/Lightmaps-TechnicalInformation.html) 描述各平台 HDR／RGBM／dLDR 的差异。可选 directionality 使用 Unity 的方向光照贴图解码约定和几何阶段法线。

GI RGB 表示不含当前表面 albedo、metallic、AO 或 emission 的单位反射率漫反射响应。它已含 Lambert 积分，基础项为 `albedo * (1-metallic) * GI * giBaseScale * AO`，不再除以 π。原来的 ambientIrradiance 是入射照度，None 表面的旧 `albedo*(1-metallic)/π * ambientIrradiance * AO` 保持不变。

几何 pass 将 GI RGB 限于 0–65504，alpha=1 表示有效输入。无 GI 表面和没有几何覆盖的像素 alpha=0；有效的黑色 GI 仍为 alpha=1。材质贴花不改写此缓冲，之后使用更新后的材质颜色／法线计算光照。SH 和方向 Lightmap 在材质贴花之前求值，因此贴花法线不会重新定向已计算的 GI；这不是一个保存全部方向系数的缓冲。

## 光照组合

主方向光和每个贴花灯分别把自己的直接光乘以 `lerp(1, GI, giWeight)`，权重 0 保留旧结果，1 完全乘色。有效 GI 的颜色和明暗都会参与，缺少输入则保持单位倍率。基础 GI 可用 giBaseScale 单独压暗，不将 ambient 与完整 GI 重复相加。

乘色作用于该灯的正向漫反射、镜面和可选背向漫反射；各项范围明确，不将它称为动态光线追踪或新的间接反弹。背向项采用独立模型：

`(1-F0) * albedo * (1-metallic)/π * radiance * diffuseScale * backlightScale * saturate(-dot(N,L))`

贴花灯还乘原有形状衰减和 Monitor RGB。背向项不创建第二个镜面高光，不受 specularScale 控制；diffuseScale=0 时关闭。主方向光使用 directionalDiffuseScale／directionalSpecularScale／directionalBacklight，贴花灯使用对应逐灯字段。材质 emission 不参加这些运算，AO 只影响基础间接项。烘焙中已有的软遮蔽和多次漫反射保持固定；动态挡光、ShadowMask 和运行时重新计算间接反弹仍未实现。

## 所有权、成本与验证范围

只要登记的表面包含 GI 来源，就增加一个相机私有 ARGBHalf 第五 MRT，几何 pass 同时输出；无 GI 时保持原来四个 GBuffer，不分配第五目标。启用 GI 需要至少五个同时渲染目标；不支持时拒绝场景提交，不静默丢弃 GI。材质贴花仍只 ping-pong 四个原通道，不额外复制 GI。

`Frame.bakedDiffuseGi` 暴露当前借用帧，`GiTargetCount` 单独记录该目标；原 AllocatedTargets 仍统计四通道 GBuffer／scratch，灯光累加另由 LightTargetCount 统计。目标跟随相机尺寸重建，丢失重建，关闭释放；不会销毁借入纹理、原材质或宿主目标。UV2、贴图、索引、方向图、SH、数值或能力无效时 UnavailableReason 报错并拒绝提交。

桌面 D3D11 的实际渲染验证覆盖显式／Renderer 绑定 Lightmap、解码、逐法线 SH、GI 乘色与背光 CPU 数值、真实 Monitor 内容更新、110 灯 Scalar／Instanced 一致、材质贴花、默认还原及相机资源生命周期。原生截帧检查第五 MRT 和灯光对 GI 的实际消费；另以真实 Lightmapping 输出验证 UV2／ST 的 CPU 射线与双线性采样、探针空间响应、优先锚点和移动蒙皮法线。详细证据见 [渲染验收](rendering.md)。不据此宣称全部场景／角色、真实方向图烘焙、CPU 后端、移动 Memoryless/subpass、五 MRT 不支持设备回退或 Vulkan／Metal 性能已验收。
