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

这些模式读取已有结果，不调用 Lightmapping.Bake，不生成 UV2 或修改场景灯光。自制测试纹理与显式 SH 验证了消费链；完整白光烘焙流程、真实场景探针空间插值及其动态网格画质仍需独立生产／验收，不能从绑定成功推导已经完成烘焙。

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

贴花灯还乘原有形状衰减和 Monitor RGB。背向项不创建第二个镜面高光，不受 specularScale 控制；diffuseScale=0 时关闭。主方向光使用 directionalDiffuseScale／directionalSpecularScale／directionalBacklight，贴花灯使用对应逐灯字段。材质 emission 不参加这些运算，AO 只影响基础间接项。GI 中已有的软遮蔽保持固定，动态挡光、ShadowMask 和真正的二次反射仍未实现。

## 所有权、成本与验证范围

只要登记的表面包含 GI 来源，就增加一个相机私有 ARGBHalf 第五 MRT，几何 pass 同时输出；无 GI 时保持原来四个 GBuffer，不分配第五目标。启用 GI 需要至少五个同时渲染目标；不支持时拒绝场景提交，不静默丢弃 GI。材质贴花仍只 ping-pong 四个原通道，不额外复制 GI。

`Frame.bakedDiffuseGi` 暴露当前借用帧，`GiTargetCount` 单独记录该目标；原 AllocatedTargets 仍统计四通道 GBuffer／scratch，灯光累加另由 LightTargetCount 统计。目标跟随相机尺寸重建，丢失重建，关闭释放；不会销毁借入纹理、原材质或宿主目标。UV2、贴图、索引、方向图、SH、数值或能力无效时 UnavailableReason 报错并拒绝提交。

桌面 D3D11 的实际渲染验证覆盖显式／Renderer 绑定 Lightmap、解码、逐法线 SH、GI 乘色与背光 CPU 数值、真实 Monitor 内容更新、110 灯 Scalar／Instanced 一致、材质贴花、默认还原及相机资源生命周期。原生截帧检查第五 MRT 和灯光对 GI 的实际消费。详细证据见 [渲染验收](rendering.md)。不据此宣称实际烘焙生产、全部场景／角色、移动 Memoryless/subpass、五 MRT 不支持设备回退或 Vulkan／Metal 性能已验收。
