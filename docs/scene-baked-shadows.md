# 烘焙 ShadowMask 与逐灯可见性

`SceneBakedShadowInput` 为场景 Deferred、透明 Forward+ 和开启表面光照的 Low／Heavy FX 提供独立的四通道烘焙可见性。默认 `None`，不会切换摄影应用、角色 shader 或已有 GI 语义。

## 输入和灯光绑定

| source | 数据来源 | 宿主责任 |
|---|---|---|
| None | 全可见，不使用输入字段 | 默认；忽略未使用的纹理和常量 |
| Constant | `visibility` 的 RGBA | 四分量必须有限且在 0–1 内 |
| Texture | `texture`、`uvST` | 提供线性二维纹理和网格 UV2；ST 为 XY 缩放、ZW 偏移 |
| RendererLightmap | 当前 `LightmapSettings.lightmaps[renderer.lightmapIndex].shadowMask` | 保留 Renderer 的实际 lightmap index、scale/offset 和 UV2 |

输入纹理均为借用，不复制、不销毁。拒绝 sRGB、未创建／MSAA RenderTexture、缺失 UV2、缺失 lightmap 和当前输出反馈。显式 mesh/matrix 没有 Renderer 的 lightmap index，应使用 `Texture` 并显式提供 ST。

把输入赋给 `SceneDeferredCamera.Surface.bakedShadow`、`SceneForwardSurface.bakedShadow` 或 `FxSurfaceLighting.bakedShadow`。主光通过 `mainBakedShadowChannel` 选通道，局部灯通过 `SceneDecalLight.bakedShadowChannel` 选通道；二者默认均为 `None`，不受烘焙遮蔽。

```csharp
surface.bakedShadow = new SceneBakedShadowInput {
    source = SceneBakedShadowSource.RendererLightmap
};

// light 是对应烘焙的 Unity Mixed Light。-1 表示没有分配通道。
int channel = light.bakingOutput.occlusionMaskChannel;
if (channel < 0 || channel > 3)
    throw new System.InvalidOperationException("Light has no baked occlusion channel");
toolkitLight.bakedShadowChannel = (SceneBakedShadowChannel)(channel + 1);
```

不能按灯的数组顺序猜通道：真实四灯烘焙可能分配为 R／A／G／B。更换 lightmap 或重烘焙后，宿主需要同步灯和接收面的绑定。模块读取当前索引和 ST，不缓存一份旧 atlas。Unity 的对应契约见 [shadowMask](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LightmapData-shadowMask.html) 和 [occlusionMaskChannel](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/LightBakingOutput-occlusionMaskChannel.html)。

## 数据布局和光照合成

Deferred 的几何阶段生产一个可选的、Point 采样的 `R8G8_UNorm` 附件：第一字节为 R 的 8 位可见性；第二字节为 G／B／A 的 3／3／2 位量化值，`byte = G * 32 + B * 4 + A`。`dither=true` 使用固定 4×4 有序量化，没有时间种子；关闭时就近量化。`SceneBakedShadowEncoding` 提供相同公开字节契约，`Frame.bakedShadowMask` 是只在当前 Frame 有效的借用附件。

已有四个 GBuffer 的 coverage、receiver group、eye depth、emission 和 GI validity 保持原义。开启 mask 时需要 5 MRT；同时开启独立 GI 时需要 6 MRT，GI 位于第六个槽。平台缺少 RG8 render/sample 或 MRT 能力会明确拒绝，不静默丢失通道。关闭、尺寸变化或目标丢失使旧 Frame 失效。

Forward+ 和 Low／Heavy FX 在自身几何的 UV2 上直接采样全精度四通道，不读取不透明物体的屏幕 mask，也不执行 Deferred 的 3／3／2 量化。因此不能要求两个后端在中间可见性值上逐像素相同。局部灯通过独立的可选 int 通道表绑定，已有每灯 144 字节结构不变；输入和通道选择均为 None 时没有新增 mask 附件或通道 GPU buffer。

对选中的每盏灯，本实现使用 `min(realtimeVisibility, bakedVisibility)`，再乘一次直接光响应。Point、Spot 和主方向光可与现有实时深度阴影组合；GI、材质 emission 和其他未选择通道的灯不额外变暗。这是独立定义的遮蔽合成规则。PDF 没有披露具体混合公式、抖动表或字节顺序，不把这些约定宣称为原版 ABI。

## 生产输入与验证入口

Editor 的 `SceneGiBaker.BakeSceneCopy` 增加 `Options.shadowMask=true`：在新场景副本中使用 Mixed Shadowmask，同时保持现有白光、独立 GI 和源资产校验。默认 false 的原 GI 烘焙路径保留。结果记录实际 shadowmap、SHA-256 和逐灯通道；调用方仍需明确绑定消费端。

`SceneBakedShadowFixture.Build` 是可选的自制输入：四盏 Mixed Point Light、平面和方块，另烘焙移除方块的对照。设置新的 `GAKUMAS_BAKED_MASK_NAME` 和目录 `GAKUMAS_BAKED_MASK_OUTPUT` 后在 Editor 执行；输出实际场景 bundle。它只使用自制基本几何，不依赖原版资产。运行 Player 自测时可将 bundle 路径放入 `GAKUMAS_SELFTEST_BAKED_MASK_BUNDLE`；未提供时不会假称完成真实烘焙验证。

`ActorRenderingSelfTest.BakedMask` 检查全部 256 个打包字节、整图抖动／UV2／ST、五灯与 GI／emission 分离、Scalar／Instanced、Tiled／Brute、Full／Half／Quarter、实时 PCF 的 min 与乘法反例、输入拒绝、cutout／重叠、当前原生蒙皮及目标生命周期。实际烘焙分支读取真实 atlas、通道分配，比较 CPU UV2／双线性参考、显式输入与 Renderer 输入，并检查移除遮挡物后四盏灯的恢复。低尺寸 Heavy 的颜色边缘修复是非线性的，参考在修复前缩放未遮蔽灯输入，不把不同修复决策的图像直接求和。

## 边界

此附件是保留公开 GBuffer 契约的桌面实现，额外占用每像素 2 字节。它没有实现原 PDF 的 inline alpha 打包、Memoryless／Subpass 附件复用，也没有证明移动带宽收益；这些继续属于 [O02／O03](framework-techniques.md)。Water、Crowd、ActorSurface、动态 probe occlusion、胶囊／面实时阴影和完整场景画质也不由此阶段自动完成。
