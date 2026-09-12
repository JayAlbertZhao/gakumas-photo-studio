# 参考技术与工具包实现

本清单把两份 QualiArts 讲演中适用于可复用角色／场景工具框架的技术，转成独立实现与验收条目。范围包含 Photo Studio、沙盒和 Live 宿主可复用的能力。未完成项目保留在清单中，不因当前摄影应用用不到就删除。

参考资料：

- **PPT**：《神は細部に宿る！「学園アイドルマスター」のこだわり抜いた3Dキャラクター・背景制作》，杉村貴之、見原朋也，共 134 页。
- **PDF**：《学園アイドルマスターにおけるモバイルの性能を限界まで引き出すレンダリングパイプライン》，渡邉俊光，CEDEC 2024，共 69 页。

讲演原件、嵌图、原版模型、shader 和字节码均不随本仓库分发。这里记录自己的代码接口与技术概括。原文未公开的算法／参数使用明确标注的独立模型，不用功能同名作为画面或性能一致的证明。原文提及的 ProFlare、Amplify Occlusion、Volumetric Light Beam 等第三方产品也不是本包附带依赖。

状态中的“已有”只表示执行路径存在。完整角色、场景、平台和性能验收分别记录，不能由局部自检推导。

## 技术覆盖清单

| 编号 | 技术与来源 | 工具包现状 | 尚需完成的验收或实现 |
| --- | --- | --- | --- |
| A01 | 九类角色表面、深度／透明／stencil 规则；PPT 35–39 | ActorSurface / MaterialRepairer / ActorSupplemental 已有 | 全变体、多角度、不同服装动态对照 |
| A02 | Def、肌肤／非肌肤双 Ramp、质感 Ramp、发高光；PPT 40–49 | 已有复现 | 对照各输入的数值、采样器及混合顺序 |
| A03 | UV2 Layer 同时控制颜色与 Def；PPT 50–51 | 已有材质路径及可逆控制 | 更广服装、动作和光照组合 |
| A04 | 主光、附加光、rim、环境镜面；PPT 27–32 | 已有 | 独立自阴影／背景投影及附加光角度语义完整对照 |
| A05 | 顶点 nibble 打包、专用描边法线；PPT 67–74 | runtime 解码与切线挤出已有；新增 ActorVertexEncoding | 已有编码／解码与向量转换接口；Maya 导出插件和自动平滑法线生成未实现 |
| A06 | 稀疏面部形变、骨与视角修形；PPT 54–65 | CPU 路径已有 | GraphicsBuffer GPU 后端、CPU/GPU 等价及成本对照 |
| A07 | 可动画面部贴花；PPT 58、62 | 已有部分支持 | 投影、参数动画与全表情范围 |
| A08 | MotionEffect 材质／Prefab／粒子／定位器；PPT 52、59–62 | 材质替换与图集动画已有 | Prefab 生命周期、种子、逐顶点定位与 seek 清理 |
| A09 | 辅助骨、链、碰撞、参考角度、滑动、跨轴力；PPT 75–78、90–93 | 已有多个求解模块 | 全服装范围、极端动作、坐姿边界与动态对照 |
| A10 | 自然风、阵风和停歇；PPT 94 | 新增 NaturalWindSettings 及角色接入 | 见下方接口；原版资产参数映射和全动作视觉一致性未验收 |
| E01 | 背景 PBR 及其 Def 通道；PPT 108 | 已有背景 fallback | 特殊材质、更多场景输入与 shader 变体 |
| E02 | 线性灯光衰减、可调镜面、GI 乘色、背向补光；PPT 110–112 | 独立 Lightmap／SH GI 缓冲、方向光及场景灯 GI 乘色、基础 GI 压暗和漫反射背光；白光烘焙；新增 Spot 内／外锥与共享实例化 | 桌面真实烘焙、探针／单骨蒙皮 GI 和 Spot 整图 CPU／原生实例输入已验收；全角色／场景、动态光源遮挡仍待完成，见 [GI](scene-gi.md) 与 [Spot](scene-decal-lights.md#spot-聚光灯) |
| E03 | DepthID 几何法线及 SSR mask；PDF 17 | 独立 SceneDepthData：世界网格法线、smoothness 资格、线性深度；min hierarchy 可选 raster／Compute | 显式 opaque／cutout 表面与角色层排除已验证；奇数边缘及 CPU／GPU 缩减一致；不猜读原版 MaterialID，不复用 ActorData／TAA 位，见 [数据契约](scene-depth-data.md) |
| E04 | 背景 Deferred／透明 Forward+／Actor Forward；PDF 13、22–29 | 新增可选 SceneDeferredCamera：场景材质 GBuffer、贴花、HDR 光照／深度在宿主 Forward 之前提交 | 默认不启用；完整 Forward+、阴影／GI／反射整合与移动附件复用仍待完成，见 [场景后端](scene-deferred.md) |
| E05 | Hi-Z SSR、排除角色；PDF 37–43 | 已有可选 Hi-Z trace、角色排除历史、重投影拒绝、统一间接光消费／Planar skip、Compute／RandomWrite；新增接收面感知的粗糙度辐射过滤 | 桌面 GPU／回退／生命周期及独立 CPU 过滤对照已验收，默认关闭过滤保留旧结果；物理 GGX、复杂场景画质与移动性能仍待完成，见 [过滤契约](ssr-roughness.md)、[计算后端](scene-compute-backend.md) 和 [统一反射](scene-reflection-resolve.md) |
| E06 | Planar 角色／发光网格及区域 mask；PDF 44–51、PPT 114 | 已有镜像／裁剪、区域覆盖、smoothness mip、法线差分扭曲与统一合成；新增 ActorToon 简化材质快照与眼部／头发匹配覆盖 | 合成材质 GPU 和一个真实角色／服装四方向、蒙皮跳转成立；全角色画质、GGX 过滤、多 Probe 调度和移动性能仍待完成，见 [角色适配](actor-planar-capture.md)、[Planar](planar-reflection.md) 与 [统一反射](scene-reflection-resolve.md) |
| E07 | PBR GBuffer 贴花、MAOS／法线／高度遮蔽／水面贴花；PPT 116–120、PDF 24–25 | 新增独立 albedo／normal／MOS／emission 分通道投影、接收组与高度 AO，结果实际进入场景光照；无有效贴花无 scratch／贴花 pass | 自制场景链已实现；原版 Def 映射、完整场景／动态水面、反射桥接与移动带宽仍待完成，见 [材质契约](scene-deferred.md) |
| E08 | HDR Monitor 与发光网格；PDF 56–58 | 新增 HdrMonitor 专用相机生产、HDR Canvas、内容／时间更新调度、UV／LED 发光网格消费 | 自制真实 UGUI／网格链路已执行；原版整场舞台、视频编解码及移动帧时未验收，见 [Monitor 接入](hdr-monitor.md) |
| E09 | 点／胶囊／面贴花灯及 instancing；PDF 59–62、PPT 119 | 新增 SceneDecalLightSettings：点／线／面 Monitor 采样、实际 PBR 照明、Scalar／GPU Instanced 与 float32 累加；110 灯原生 draw 已核对 | 桌面自制场景、Monitor 动画与预计算 GI 乘色成立；原版整舞台参数、光源阴影和移动重叠带宽仍待验收，见 [贴花灯](scene-decal-lights.md) |
| E10 | 天空、植被、水、折射、荧光棒等专用表面；PPT 109 | 未覆盖完整集合 | 资料仅列用途，需独立约定输入与验收，不能假定原版公式 |
| P01 | 方差裁剪 TAA、ExcludeTAA、NoJitter；PDF 33 | 时域主体已有；新增显式表面分类 | 见下方接口；不自动给全部原版材质分类，不引入默认投影 jitter |
| P02 | Bokeh DOF 范围、分辨率无关散景、30 次采样；PPT 127–128 | 可控 DOF 已有，现为 43 次采样 | 先核对版本／质量档，再做 30 次采样质量与成本对照 |
| P03 | Bloom、Diffusion、Paraffin、色调／颜色处理；PPT 126 | 已有部分复现和配置 | 更广配置、无私有 LUT 时的自主制作流程与完整阶段一致性 |
| P04 | Motion Blur；PPT 126、PDF 15 | 未接入完整阶段 | 相机／物体运动、暂停、切镜与遮挡边界 |
| P05 | GTAO、脚部 Capsule AO；PDF 19 | 有屏幕空间遮蔽，未覆盖该完整组合 | 明确算法边界、薄面与接触区域、性能 |
| P06 | 距离雾／球形雾；PPT 130、PDF 29 | 已有距离雾、单球独立模型 | 多球、双雾组合、透明层与原版参数仍未验收 |
| P07 | 体积光及动态 DepthShadow；PPT 131 | 未接入 | 光体积积分、动态遮挡与质量档；不附带第三方插件 |
| P08 | Flare／Ghost；PPT 129 | 未覆盖讲演列举的完整能力 | 可替换实现、可见性与遮挡、动画配置 |
| P09 | 低分辨率透明／扭曲／重特效及上采样；PDF 15、31 | 未接入完整分层调度 | 深度相容、边缘合成、排序与目标释放 |
| O01 | FSR 与高质量 TAA 输入；PDF 10、69 | 未接入；现有超采样不是 FSR | 合规可选依赖、画质档、分辨率切换与 GPU 成本 |
| O02 | Memoryless／RenderPass／SubPass／MRT 复用；PDF 11–12、22、27 | 部分场景 MRT 与 Compute 算法已有，未实现同级移动架构 | 独立移动后端、附件预算、负载测量、平台回退；不得由桌面 Compute 输出相同推导性能完成 |
| O03 | 移动端能力与驱动差异；PDF 8、64–65 | 当前验证集中于桌面 D3D11 | Vulkan／Metal 实机、能力探测、质量回退与长时间运行 |
| C01 | 观众 LOD、模型预算、四向 runtime billboard、Compute；PPT 99–103 | 未接入 | 独立 crowd 模块、视角切换、万人规模成本；不捆绑观众资产 |

模型雕刻、服装审美、场景布置和实体舞台配线属于资产制作工作。工具包提供对应输入能力，不复制讲演中的成品，也不把这些工作伪装成一个运行时开关。

## 自然风接口

```csharp
using GakumasPhotoMode;
using UnityEngine;

var wind = new NaturalWindSettings {
    enabled = true,
    seed = 17,
    steadyForce = new Vector3(0.015f, 0f, 0f),
    gustSeconds = 4f,
    calmSeconds = 3f
};
characterScene.SetNaturalWind(wind);
// 可选：宿主管理时间。设置相同时间会取得相同风力。
characterScene.SetNaturalWindTime(2.0);
// null 恢复 scaled Unity time；不会重置已有物理速度。
characterScene.SetNaturalWindTime(null);
// 解除本角色的环境风绑定。
characterScene.SetNaturalWind(null);
```

也可单独调用 `wind.Sample(seconds)`，不需要角色、场景、资产清单或随机数全局状态。稳态、正弦与平滑随机向量按世界空间相加，再乘阵风包络。包络在每段风的两端平滑回到 calmStrength；随机种子控制不同阵风的强度与包络扰动。它是独立的可复现模型，不是原版序列的重放。

角色入口把同一配置绑定到头发、外衣与裙摆的既有求解器，换装后重绑。每个动态子节点的 `wind` 与 `useWindGlobalForce` 决定是否接受环境风。不向身体软组织添加风力。数值采用现有求解器的 force 输入单位，不声称物理 SI 标定。默认 null／disabled，旧 UI 诊断风保持原样；两种风同时启用时相加。时间采样的确定性不代表整个有状态物理求解可以无缓存任意跳转。

## TAA 分类接口

```csharp
var classification = camera.gameObject.AddComponent<TemporalClassification>();
classification.surfaces = new[] {
    new TemporalClassification.Surface {
        renderer = emissiveRenderer,
        materialIndex = 0,
        flags = TemporalPixelFlags.ExcludeTaa
    }
};
camera.GetComponent<OriginalStyleRenderPipeline>().temporalClassification = classification;
```

分类缓冲独立于 ActorData。它在相机的 BeforeImageEffects 阶段以当前深度测试生成，只给显式指定的可见表面分类；不改变共享材质。`ExcludeTaa` 跳过历史混合并按宿主提供的 `jitterUv` 还原当前颜色采样，`NoJitter` 直接使用当前像素，二者共存时后者优先。默认没有分类组件／绑定，不创建分类目标，也不启用这些分支。

此适配器针对 Built-in、单相机完整视口、非 MSAA／非 XR 目标。alphaMask、UV、cutoff、cull 与 vertexScale 由宿主显式配置。复杂原版前发 stencil、材质自定义顶点位移和不写深度的透明层相互遮挡未自动复刻。若宿主自行引入投影 jitter，必须让其标成 NoJitter 的几何使用对应的无 jitter 绘制路径；分类器不会修改几何投影。当前 Photo Studio 未引入默认投影 jitter。

## 实施与验收边界

自制网格可使用 `ActorVertexEncoding.Pack/Unpack` 编辑 0–15 的 nibble 字段。RGBA 分别存储描边 R/G、描边 B/材质 Ramp、描边深度/宽度、rim/reserved。编码使用整数，不偷偷执行颜色空间转换；深度字段是 0–15 的计数，不能按 RGB 强度再除一次 15。`OutlineTangents` 返回新数组，保持自制挤出向量的长度和零向量，不改源网格，也不自动平滑法线。应用端决定何时将结果赋给自己的 `mesh.colors32` / `mesh.tangents`。

按 A/P 的现有角色路径先补可独立接入模块，再补 E 的场景管线，O 的移动性能后端与 C 的 crowd 单独验证。所有新增阶段须保留关闭时的基线，避免把框架扩展变成摄影应用默认画面的大换血。

每项记录源码、真实执行证据、开／关或错误输入对照、使用限制。通过无资产单元／GPU 检查后仍要检查真实宿主；不能以模块检查数量替代完整角色、场景或博客视觉验收。详见 [渲染记录](rendering.md) 与 [工具包接入](toolkit.md)。
