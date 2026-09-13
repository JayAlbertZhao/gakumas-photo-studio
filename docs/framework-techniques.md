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
| A05 | 顶点 nibble 打包、专用描边法线；PPT 67–74 | runtime 解码与切线挤出、ActorVertexEncoding；新增显式 [描边向量自动生成](outline-authoring.md)：同位置分组、角度加权及独立网格／blend-frame tangent 副本 | 自制三角化、接缝和原生蒙皮整图有对照；Maya 导出插件、原 UVSet 交换格式、生产角色／服装及动态组合的视觉一致性仍待完成 |
| A06 | 稀疏面部形变、骨与视角修形；PPT 54–65 | 默认 CPU 保留；新增可选 [GraphicsBuffer GPU 后端](gpu-face-deformation.md)，原有权重／眨眼／视角修形共用；另有只读的当前选中顶点来源 | 桌面逐顶点、原生 compute→draw 及一个真实角色已对照，定位器覆盖全部 108 个单独形状；完整角色／服装、其他 CPU 几何消费者、GPU 帧时和移动驱动仍待完成 |
| A07 | 可动画面部贴花；PPT 58、62；PDF 29 | 新增默认关闭的 [独立可动画贴花](animated-face-decals.md)：显式接收面、投影／UV／混合、AnimationClip／类型化曲线及 Planar；可消费 [共享制作数据](performance-authoring.md) | 桌面全图、实际蒙皮／GPU 顶点及一个角色全部 108 个单独形状已对照；其他角色／服装／制作动作、Maya 客户端制作／导出实测及移动成本仍待完成 |
| A08 | MotionEffect 材质／Prefab／粒子／定位器；PPT 52、59–62 | 已有 [预制体／Local 粒子／Playable](motion-effects.md) 与 [当前面部顶点定位器](vertex-locators.md)；新增 [共享制作曲线、材质区间与 FBX／sidecar 转换](performance-authoring.md)，显式绑定和统一时钟 | 桌面粒子／顶点／整图重放已验证；Blender 原生关键帧、自定义属性 FBX 导入及 Player 联合消费已实测；通用蒙皮 provider、世界轨迹／制作特效、Maya 客户端和生产 rig、移动成本仍待完成 |
| A09 | 辅助骨、链、碰撞、参考角度、滑动、跨轴力；PPT 75–78、90–93 | 已有多个求解模块 | 全服装范围、极端动作、坐姿边界与动态对照 |
| A10 | 自然风、阵风和停歇；PPT 94 | 新增 NaturalWindSettings 及角色接入 | 见下方接口；原版资产参数映射和全动作视觉一致性未验收 |
| E01 | 背景 PBR 及其 Def 通道；PPT 108 | 已有背景 fallback | 特殊材质、更多场景输入与 shader 变体 |
| E02 | 线性灯光衰减、可调镜面、GI 乘色、背向补光；PPT 110–112 | 独立 GI、基础 GI 压暗、背向漫反射、白光烘焙、Spot 与实例化；默认关闭的 Spot／Point 六面／主方向光深度阴影 | 桌面 GI、三种光源投影及移动 caster 已验收；胶囊／面阴影、完整角色／场景和一般蒙皮仍待完成，见 [GI](scene-gi.md) 与 [光源阴影](scene-light-shadows.md) |
| E03 | DepthID 几何法线及 SSR mask；PDF 17 | 独立 SceneDepthData：世界网格法线、smoothness 资格、线性深度；min hierarchy 可选 raster／Compute | 显式 opaque／cutout 表面与角色层排除已验证；奇数边缘及 CPU／GPU 缩减一致；不猜读原版 MaterialID，不复用 ActorData／TAA 位，见 [数据契约](scene-depth-data.md) |
| E04 | 背景 Deferred／透明 Forward+／Actor Forward；PDF 13、22–29、69 | 可选 SceneDeferredCamera、[全分辨率透明 Forward+](scene-forward-plus.md)；[LowResolutionFx／HeavyFx 当前光照](fx-forward-lighting.md) 在真实 Full／Half／Quarter 几何中消费相同 GPU bitset、材质／GI／Monitor和主光／局部阴影；角色保留 Forward | 默认不启用；特效介质与表面光照独立组合，桌面整图和当前原生蒙皮有对照；透明反射／复杂介质排序、ShadowMask 打包、胶囊／面阴影与移动附件仍待完成，另见 [场景后端](scene-deferred.md)、[ScreenShadow](scene-screen-shadow.md) 与 [光源阴影](scene-light-shadows.md) |
| E05 | Hi-Z SSR、排除角色；PDF 37–43 | 已有可选 Hi-Z trace、角色排除历史、重投影拒绝、统一间接光消费／Planar skip、Compute／RandomWrite；新增接收面感知的粗糙度辐射过滤 | 桌面 GPU／回退／生命周期及独立 CPU 过滤对照已验收，默认关闭过滤保留旧结果；物理 GGX、复杂场景画质与移动性能仍待完成，见 [过滤契约](ssr-roughness.md)、[计算后端](scene-compute-backend.md) 和 [统一反射](scene-reflection-resolve.md) |
| E06 | Planar 角色／发光网格及区域 mask；PDF 44–51、PPT 114 | 已有镜像／裁剪、区域覆盖、smoothness mip、法线差分扭曲与统一合成；新增 ActorToon 简化材质快照与眼部／头发匹配覆盖 | 合成材质 GPU 和一个真实角色／服装四方向、蒙皮跳转成立；全角色画质、GGX 过滤、多 Probe 调度和移动性能仍待完成，见 [角色适配](actor-planar-capture.md)、[Planar](planar-reflection.md) 与 [统一反射](scene-reflection-resolve.md) |
| E07 | PBR GBuffer 贴花、MAOS／法线／高度遮蔽／水面贴花；PPT 116–120、PDF 24–25 | 新增独立 albedo／normal／MOS／emission 分通道投影、接收组与高度 AO，结果实际进入场景光照；无有效贴花无 scratch／贴花 pass | 自制场景链已实现；原版 Def 映射、完整场景／动态水面、反射桥接与移动带宽仍待完成，见 [材质契约](scene-deferred.md) |
| E08 | HDR Monitor 与发光网格；PDF 56–58 | 新增 HdrMonitor 专用相机生产、HDR Canvas、内容／时间更新调度、UV／LED 发光网格消费 | 自制真实 UGUI／网格链路已执行；原版整场舞台、视频编解码及移动帧时未验收，见 [Monitor 接入](hdr-monitor.md) |
| E09 | 点／胶囊／面贴花灯及 instancing；PDF 59–62、PPT 119 | SceneDecalLightSettings：Monitor、PBR、Scalar／GPU Instanced、float32 累加；Spot／Point 可选光源阴影，144 字节旧布局保留 | 桌面 Monitor／GI、Spot 与 Point 六面／跨面 PCF 已验收；胶囊／面阴影、原版整舞台参数和移动带宽仍待验收，见 [贴花灯](scene-decal-lights.md) |
| E10 | 天空、植被、水、折射、荧光棒等专用表面；PPT 109 | 新增默认关闭的 [独立水面](scene-water.md)：当前几何与共享 Forward 灯光／GI／阴影、波纹法线、吸收、前景保护的屏幕透射、当前 Planar／Cube | 自制桌面整图、当前蒙皮、真实深度／光照／反射及水平水池有对照；天空、植被、钻石折射和荧光棒等其余表面、完整制作水面、相交介质／时域接入和移动成本仍待完成。资料未公开水面公式，不假定原版参数 |
| P01 | 方差裁剪 TAA、ExcludeTAA、NoJitter；PDF 33 | 新增可选 [场景颜色 TAA](scene-temporal-antialiasing.md)：消费实际运动／可见性，HDR 方差裁剪、Point 深度射线与显式后处理桥接；旧默认 shader 不变 | 自制桌面场景的 jitter／运动／蒙皮／生命周期已验证；完整角色分类、复杂透明层、全舞台动态画质及移动成本仍待完成，不引入默认投影 jitter |
| P02 | Bokeh DOF 范围、分辨率无关散景、30 次采样；PPT 127–128 | 默认关闭的独立 [DOF 模块](bokeh-depth-of-field.md)：显式线性深度、手动清晰范围／物理镜头、自定义完整 30／43 点孔径；原生 GPU 采样数、整图及分辨率控制已验收；旧摄影路径不变 | 全动态角色／发丝透明与复杂近远遮挡质量、完整深度适配、移动内存与实际帧时；未恢复原文未公开的 30 点布局 |
| P03 | Bloom、Diffusion、Paraffin、色调／颜色处理；PPT 126 | 已有合成路径；新增不依赖私有文件的 profile／JSON／资产、Bradford 白平衡、颜色调整、LGG、八条曲线、GT 映射与 16³／32³／64³ LUT 制作／消费，见 [自主调色](authored-color-grading.md) | 独立模块及实际 Camera.Render 桥接已验收，默认路径不变；全场景外观、Unity Volume 参数／曲线语义适配、更广合成配置和移动性能仍待完成 |
| P04 | Motion Blur；PPT 126、PDF 15 | 新增可选 [Motion Blur](motion-blur.md)：实际几何对应／当前可见性、曝光时钟、tile／双方向深度重建，显式 DOF 后／Bloom 前消费；默认摄影不启用 | 自制刚体／蒙皮／blendshape、遮挡／暂停／切镜／jitter 及桌面整图／原生链已验证；完整角色发丝透明、复杂重叠方向与 DOF 联合质量、移动内存／帧时仍待完成；不宣称恢复原文未公开滤波器 |
| P05 | GTAO、脚部 Capsule AO；PDF 19、PPT126 | 显式胶囊、视轴 GTAO、Half 几何引导重建；新增可选运动对应消费、六相旋转、几何／身份／reactive 拒绝、方差裁剪与累积，见 [时域 GTAO](scene-gtao-temporal.md)、[GTAO](scene-gtao.md) 与 [Capsule AO](scene-screen-shadow.md) | 桌面自制运动／反遮挡已验证，默认不变；自动脚部拟合／全角色接触、薄面复杂动态场景质量、画外几何与移动内存／帧时仍待完成 |
| P06 | 距离雾／球形雾；PPT 130、PDF 29 | 默认关闭的 [多介质雾](fog-volumes.md)：距离＋八球联合积分、相机快照、真实深度与后处理桥接、逐表面透明雾；旧距离／单球路径不变 | 桌面整图、重叠／分离／相机内外、质量收敛和双透明层深度测试已验收；原版未公开参数、完整角色透明／折射、复杂排序及移动成本仍待完成 |
| P07 | 体积光及动态 DepthShadow；PPT 131 | 默认关闭的 [体积光](volumetric-lighting.md) 与 [低尺寸积分](volumetric-reconstruction.md)；新增 [联合重特效](heavy-fx.md) 中按不透明及各表面实际深度分别计算透射／散射 | 桌面整图、动态双光源阴影及共享重建路径已验证；原版参数、相交层／折射后深度、半透明光源遮挡、复杂舞台质量与移动成本仍待完成 |
| P08 | Flare／Ghost；PPT 129、PDF 31 | 默认关闭的 [Flare／Ghost](lens-flares.md)；另可直接写入 [联合重特效](heavy-fx.md) 共享附件，当前源可见性与显式时间不变 | 桌面整图、共享批次和实际相机链已验证；原版处方、半透明多层遮光、专用时域稳定性、复杂舞台与移动帧时仍待完成 |
| P09 | 低分辨率透明／扭曲／重特效及上采样；PDF 15、31 | [独立几何](low-resolution-fx.md) 与默认关闭的 [联合重特效](heavy-fx.md) 共用介质／有序几何／镜头附件、R8 修复决策和整批重画；可选 [当前表面 Forward 光照](fx-forward-lighting.md) 保留混合批次和材质 alpha | 桌面整图、当前蒙皮、介质／表面独立阴影有对照；启用光照的折射材质明确不支持，相交层排序、折射介质输运、完整舞台及移动成本仍待完成 |
| O01 | FSR 与高质量 TAA 输入；PDF 10、69 | 默认关闭的 [FSR1](fsr.md)：通过 UPM Core 的 AMD 算法执行 EASU／RCAS，独立 Compute／Raster；真实低尺寸相机、四档、当前 TAA、Bloom 后／Diffusion 前桥接，UI 保持完整分辨率 | 自制桌面全图数值、抗锯齿时序、真实 UGUI 次序及 720p／1080p 四档原生 GPU 事件计时已对照。完整角色／舞台、HDR 极亮部、其他图形后端、移动内存及实机净收益待完成；不替换默认摄影超采样 |
| O02 | Memoryless／RenderPass／SubPass／MRT 复用；PDF 11–12、22、27 | 部分场景 MRT 与 Compute 算法已有，未实现同级移动架构 | 独立移动后端、附件预算、负载测量、平台回退；不得由桌面 Compute 输出相同推导性能完成 |
| O03 | 移动端能力与驱动差异；PDF 8、64–65 | 当前验证集中于桌面 D3D11 | Vulkan／Metal 实机、能力探测、质量回退与长时间运行 |
| C01 | 观众 LOD、模型预算、四向 runtime billboard、Compute；PPT 99–103 | 默认关闭的 [独立 Crowd](crowd.md)：八类共享当前骨／shape pose、GPU 最近模型预算、稳定排序／压缩、四向动态材质图集和 PBR／Toon 间接绘制；显式 CPU 回退 | 桌面原生几何／阴影／遮挡／透明次序、26 视角及 65,536 边界已对照，万人真实 compute／间接绘制有捕获；四视图仍为有限角度近似，完整制作观众、独立动作变化、时域／反射／阴影投射接入及移动成本待完成；不捆绑观众资产 |

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
