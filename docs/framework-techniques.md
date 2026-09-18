# 参考技术与工具包实现

本清单把两份 QualiArts 讲演中适用于可复用角色／场景工具框架的技术，转成独立实现与验收条目。范围包含 Photo Studio、沙盒和 Live 宿主可复用的能力。未完成项目保留在清单中，不因当前摄影应用用不到就删除。

参考资料：

- **PPT**：《神は細部に宿る！「学園アイドルマスター」のこだわり抜いた3Dキャラクター・背景制作》，杉村貴之、見原朋也，共 134 页。
- **PDF**：《学園アイドルマスターにおけるモバイルの性能を限界まで引き出すレンダリングパイプライン》，渡邉俊光，CEDEC 2024，共 69 页。

讲演原件、嵌图、原版模型、shader 和字节码均不随本仓库分发。这里记录自己的代码接口与技术概括。原文未公开的算法／参数使用明确标注的独立模型，不用功能同名作为画面或性能一致的证明。原文提及的 ProFlare、Amplify Occlusion、Volumetric Light Beam 等第三方产品也不是本包附带依赖。

状态中的“已有”只表示执行路径存在。完整角色、场景、平台和性能验收分别记录，不能由局部自检推导。

P04 的 Motion Blur 现已可选接入 [桌面整帧宿主](desktop-host.md#可选整帧-motion-blur)，
消费角色／场景 Half4、显式曝光时钟和透明／特效保护，排列在 TAA／DOF 之后。
已验证自制桌面几何、数值 guide 与原生消费链；完整角色动态外观、联合遮挡质量、
移动成本和完整后处理动态质量仍在下列未完成范围内。

新增 [真实角色曝光积分诊断](motion-blur.md#真实角色曝光积分诊断)，在 D3D11／Vulkan
用显式 32 个子时刻的线性积分对照相机与动画轨迹。一个服装的三视角相机轨迹有改善，
原整数路径的动画模糊正控制全部失败；原生帧已定位短曝光整数采样的零非中心权重限制。
另提供默认关闭的 [短曝光重建](motion-blur.md#可选短曝光重建)，对有兼容深度／运动的
邻点做显式双线性曝光积分，保留原路径和透明保护。两种桌面 API 的同服装三视角
新配对动画中，角色区域曝光参考 MSE 相对 legacy 降低约 18.6%–25.6%，原有动画响应门限
通过；有限角色诊断不代表完整 P04 画质验收。

进一步的 [透明／景深联合曝光诊断](motion-blur.md#透明特效景深与曝光的联合诊断)
已复现明确缺口：透明特效独立运动时，两种桌面 API 的 Motion Blur 整图仍精确透传，
没有产生透明层曝光；侧面动画 FX 区域及背面动画轮廓的严格改善检查也保留失败。
参考逐子时刻执行当前 DOF，并非物理孔径积分。此诊断不计为联合画质修复或 P02／P04 完成。

随后加入默认关闭的 [透明几何快门积分](heavy-fx.md#实验性透明几何曝光)：
共享子时刻的多层有序合成、实际 GPU 顶点历史和积分附件，已修复上述限定案例中
透明片独立运动无曝光的问题。两种桌面 API 的自制几何／蒙皮对照通过；真实角色
三视角 FX 区域误差明显下降，但相机轨迹和部分动画区域仍保留联合质量失败。
几何积分路径仍可独立使用，P02／P04 未整体完成。

另新增默认关闭的 [同相位不透明重投影](heavy-fx.md#可选不透明背景的同相位重投影)：
在每个共享快门时刻，先重建未混合的不透明颜色／深度，再合成该时刻透明几何，
最后平均完整合成结果；宿主不再对积分结果重复做 Motion Blur。
两种桌面 API 的独立自制颜色／深度／遮挡对照已执行；限定真实角色三视角相机轨迹中，
角色区域参考 MSE 相对同次运行 legacy 降低约 52.45%–60.63%，透明独立运动控制保持通过。
相机轮廓仍有两项严格质量失败／API，Vulkan 另有两项背面动画轮廓失败。
当前可见面重投影不能恢复隐藏表面，DOF 仍仅用当前深度执行一次，不支持该模式与
TAA／jitter 同开；未测移动内存与帧时，不计为联合画质或 P02／P04 完成。

后续增加 [同相位 DOF 回调与正向可见面归属](heavy-fx.md#可选正向可见面归属)，
均需显式开启。DOF 在每个完整子时刻合成之后、曝光平均之前执行；颜色可以插值，
镜头深度取选中的表面，避免在前后景之间混出不存在的深度平面。
最终构建的同服装／三视角／三轨迹检查在 D3D11 和 Vulkan 均通过原有严格门限，
包括先前失败的相机轮廓和背面动画轮廓；历史失败记录保留。
原生 D3D11 捕获核对 16 相位的 48 次 owner dispatch、176 次 DOF 绘制及最终积分。
这解决了该限定诊断的缺口，仍不等于完整角色／舞台画质、物理镜头或移动性能追平。

P02 新增 [显式全身清晰范围](bokeh-depth-of-field.md#显式全身清晰范围) helper，
只消费调用方的当前包围盒，不扫描角色或修改相机。两套自备服装／两桌面 API 的
全身、脸部与前伸手臂配对诊断，确认范围对焦保持主体清晰而背景虚化；包含实际蒙皮
变化与脸部不被手臂遮挡的非空控制。限定 512²／仅远景 DOF，不关闭动态透明与移动成本缺口。

P03 新增不依赖私有 profile 的 [自主 Bloom](desktop-host.md#可选自主-bloom)，以明确的
软阈值、归一化金字塔和一次 HDR 合成接入 Motion Blur 后。自制桌面数值参考和
原生资源链已验证；其模型与旧摄影累加金字塔分别保留，不推导原版公式或整体画质相同。
新增独立 [Diffusion](desktop-host.md#可选自主-diffusion)，在 FSR 后、调色前以明确的
完整输出像素半径、归一化二项式核与正向 HDR 扩散合成。PPT 仅公布功能、PDF 公布
顺序；该核和参数是本项目自己的选择，不包含旧摄影私有 profile。

O01 的 [整帧 FSR](desktop-host.md#可选整帧-fsr) 使用实际低尺寸几何与前置后处理，
再放大并执行全尺寸调色。复用 AMD EASU／RCAS，另提供显式、默认关闭的近等亮度
梯度稳定化扩展；原始变体仍保留。自制 D3D11／Vulkan 数值和资源链验证不等同于
完整角色动态画质或移动性能达标，相关缺口仍列在下表。

[桌面整帧宿主](desktop-host.md) 显式组合场景、角色、自阴影／背景投影、Planar／SSR、透明特效、可选运动／TAA、DOF 与自主调色，提供不需要私人资产的已编译示例，以及可选 GBuffer2／GBuffer4／硬件深度原位复用。它不替换默认摄影路径；复杂动态画质、其余后处理串联与移动实机验收仍未完成，不把模块组合计为整个清单完成。

## 技术覆盖清单

补充角色整链诊断见 [桌面宿主](desktop-host.md#角色整链诊断)。验证使用调用方提供的
角色／服装，不随公开仓库分发；前、侧、后视图的低尺寸几何、逐效果开关、冷帧／暂停、
重播及后端差异分别记录。对调色过滤阶跃提供默认关闭的显式浮点 LUT 插值选项。
功能响应与数值一致性不代替发丝、遮挡和实际投影抖动的动态画质验收。

P01 进一步提供 [显式投影抖动](desktop-host.md#显式投影抖动)：宿主管理相位及历史，
helper 返回离屏投影与相反方向的纹理 UV 修正。两套自备服装、三视角及三种实际
几何尺寸的静态近景已用独立空间积分、冷历史、错误符号／漏传负控检验收敛。
默认摄影保持不变；该静态结果不关闭动态发丝／遮挡缺口，也不关闭已有 Vulkan
可选运动 MRT 的严格颜色失败。

新增 [动态近景诊断与可选混合表面历史拒绝](desktop-host.md#动态近景诊断)，把
相机移动、动画及不透明遮挡板分开，并在每个时刻独立做空间积分参考。混合采样的
历史身份问题已有默认关闭的保守策略及原生机制证据；它降低部分轮廓误差，同时
减少边界累积，该策略的动态轮廓严格改善门限仍有失败。
另有默认关闭的 [覆盖率重建](desktop-host.md#可选覆盖率重建)，使用最近表面运动、
完整历史采样资格及有界三次重建。D3D11／Vulkan、两套服装的正面 Quality 配对序列中，
三种运动均通过累计角色／轮廓参考改善检查，仍存在局部取舍。P01 的完整动态画质与
平台缺口继续保留。初始覆盖率版本扩展到一个自备服装的 D3D11 前／侧／后、Native／Quality／Performance
动态矩阵后，后视 Native 动画的角色区域仍有失败：warm MSE 比 cold 高约 9.1%，
虽然比旧策略低约 12.9%。该严格门限没有放宽，正面 Quality 的结论不能外推到全矩阵。

随后加入 [重采样历史置信度](desktop-host.md#重采样后的历史置信度)，降低非整数重投影
反复过滤后的历史年龄。当前一个角色／服装在两种桌面 API 的上述完整动态矩阵通过
原有角色／轮廓门限，后视 Native 动画相对同进程 cold 的角色 MSE 降低约 13.6%／14.2%。
保留各区域取舍及旧失败，不把本次有限内容矩阵记为整个 P01、全部服装或移动画质完成。

P01／P02 串联新增 [时域深度对齐](desktop-host.md#taa-与景深的深度对齐)：仅在
TAA＋DOF＋非零修正下，为 DOF 选择与 TAA 运动锚点对应的完整 R32 深度。两种桌面
API 的自制整图控制及实际深度→CoC 链已验证；不改默认摄影，不恢复多层透明深度。
联合 DOF 已扩展到同一服装的前／侧／后、Native／Quality／Performance 三条动态轨迹，
两种 API 的角色／轮廓改善子项通过；Vulkan 整份报告仍保留已有运动 MRT 严格颜色失败。

| 编号 | 技术与来源 | 工具包现状 | 尚需完成的验收或实现 |
| --- | --- | --- | --- |
| A01 | 九类角色表面、深度／透明／stencil 规则；PPT 35–39 | ActorSurface / MaterialRepairer / ActorSupplemental 已有 | 全变体、多角度、不同服装动态对照 |
| A02 | Def、肌肤／非肌肤双 Ramp、质感 Ramp、发高光；PPT 40–49 | 已有复现；新增 [自制材质输入约定](actor-material-inputs.md) 与 30 组、150 张全图独立数值对照，包含四类材质、Point／Bilinear、Clamp／Repeat、显式 mip 和讲演尺寸双 Ramp；原生纹理／采样器／混合阶段已核对 | 已验证本机 NVIDIA／D3D11 精度模型；压缩／sRGB 导入、透明头发覆盖、完整角色／服装／动作组合、其他驱动与原版画质仍待验证，不能由合成材质对照推导全部追平 |
| A03 | UV2 Layer 同时控制颜色与 Def；PPT 50–51 | 已有材质路径及可逆控制 | 更广服装、动作和光照组合 |
| A04 | 主光、附加光、rim、环境镜面；PPT 27–32 | 原有材质光照保留；新增 [独立角色阴影](actor-shadows.md) 与显式 [Point／Spot 制作光](srp-actor-forward.md#explicit-actor-point--spot-authoring)：可压暗主光而保留局部加亮，沿用主光 toon 明暗，局部镜面仍使用自身半角向量 | 自制全图数值对照；一个角色两套服装／两桌面 API／三视角的主光压暗、锥体拒绝、双色叠加和完整 temporal pass 已验证。附加光使用独立衰减模型，不含局部灯阴影；更广制作内容、原版参数映射和移动质量／成本仍待完成 |
| A05 | 顶点 nibble 打包、专用描边法线；PPT 67–74 | runtime 解码与切线挤出、ActorVertexEncoding；新增显式 [描边向量自动生成](outline-authoring.md)：同位置分组、角度加权及独立网格／blend-frame tangent 副本 | 自制三角化、接缝和原生蒙皮整图有对照；Maya 导出插件、原 UVSet 交换格式、生产角色／服装及动态组合的视觉一致性仍待完成 |
| A06 | 稀疏面部形变、骨与视角修形；PPT 54–65 | 默认 CPU 保留；新增可选 [GraphicsBuffer GPU 后端](gpu-face-deformation.md)，原有权重／眨眼／视角修形共用；另有只读的当前选中顶点来源 | 桌面逐顶点、原生 compute→draw 及一个真实角色已对照，定位器覆盖全部 108 个单独形状；完整角色／服装、其他 CPU 几何消费者、GPU 帧时和移动驱动仍待完成 |
| A07 | 可动画面部贴花；PPT 58、62；PDF 29 | 新增默认关闭的 [独立可动画贴花](animated-face-decals.md)：显式接收面、投影／UV／混合、AnimationClip／类型化曲线及 Planar；可消费 [共享制作数据](performance-authoring.md) | 桌面全图、实际蒙皮／GPU 顶点及一个角色全部 108 个单独形状已对照；其他角色／服装／制作动作、Maya 客户端制作／导出实测及移动成本仍待完成 |
| A08 | MotionEffect 材质／Prefab／粒子／定位器；PPT 52、59–62 | 已有 [预制体／Local 粒子／Playable](motion-effects.md) 与 [当前面部顶点定位器](vertex-locators.md)；新增 [共享制作曲线、材质区间与 FBX／sidecar 转换](performance-authoring.md)，显式绑定和统一时钟 | 桌面粒子／顶点／整图重放已验证；Blender 原生关键帧、自定义属性 FBX 导入及 Player 联合消费已实测；通用蒙皮 provider、世界轨迹／制作特效、Maya 客户端和生产 rig、移动成本仍待完成 |
| A09 | 辅助骨、链、碰撞、参考角度、滑动、跨轴力；PPT 75–78、90–93 | 已有多个求解模块；[显式时钟与重播](#辅助动态骨的显式推进) 修复双侧重置首帧冲量；可选 [分层衣物参考角度](#分层衣物的参考角度) 支持类型安全的组件引用与跨求解器约束 | 两套服装／两桌面 API 的 180 帧重播已验证；另对一套服装的坐姿执行参考限制开／关、重播及三视角实际蒙皮颜色／深度对照。全服装范围、极端动作、坐姿穿模边界、完整碰撞质量仍待验收 |
| A10 | 自然风、阵风和停歇；PPT 94 | NaturalWindSettings 及角色接入；显式推进可同时驱动风场与既有物理 | 自制阵风／停歇与两套服装实际骨骼、颜色、深度响应已验证；原版资产参数映射和全动作视觉一致性未验收 |
| E01 | 背景 PBR 及其 Def 通道；PPT 108 | 已有背景 fallback；透明／特效共享 [法线贴图基](forward-normal-basis.md) 按实际矩阵计算，支持显式镜像／非均匀变换 | 特殊材质、更多场景输入与 shader 变体；本次法线基修正不代表原版 Def ABI 或全场景画质一致 |
| E02 | 线性灯光衰减、可调镜面、GI 乘色、背向补光；PPT 110–112 | 独立 GI、基础 GI 压暗、背向漫反射、白光烘焙、Spot 与实例化；可选实时深度阴影、[四通道烘焙可见性](scene-baked-shadows.md) 及显式 [Capsule／Area 有限源覆盖](extended-source-shadows.md) | 桌面 GI、三种光源投影及移动 caster 已验收；Mixed Shadowmask 使用新场景副本与显式逐灯通道。扩展光源使用独立等权几何可见性模型，保留原有 GI／背光响应；连续面积光积分、完整角色／场景和一般蒙皮仍需单独实现或验收，见 [GI](scene-gi.md) 与 [光源阴影](scene-light-shadows.md) |
| E03 | DepthID 几何法线及 SSR mask；PDF 17 | SceneDepthData 保留；[Tile 几何预处理](tile-decals.md) 的 R32 eye depth／RGBA8 未映射世界法线及资格已接入真实材质投影、[Tile SSR](tile-reflections.md) 与 [SRP Planar](tile-planar-reflections.md)，与贴花后映射法线分开消费 | 当前几何／cutout／资格、反射接收组与原生字段已对照；一般蒙皮组合和移动验收仍待完成。不猜读原版 MaterialID，不复用 ActorData／TAA 位 |
| E04 | 背景 Deferred／透明 Forward+／Actor Forward；PDF 13、22–29、69 | 可选 SceneDeferredCamera、[全分辨率透明 Forward+](scene-forward-plus.md)；[Low／Heavy 当前光照](fx-forward-lighting.md) 共享灯、GI、Monitor 与阴影；[ShadowMask](scene-baked-shadows.md) 保留独立附件。[完整 SRP Actor Forward](srp-actor-forward.md) 提供完整材质／描边／前发 stencil、逐 renderer 球谐、反射之后合成及可选 [角色阴影](actor-shadows.md)。新增实际 GPU 顶点流的 Half4 运动／当前深度／独立身份，以及场景读者之后的 GBuffer2／GBuffer4／D32S8 可选原位消费 | 默认不启用。桌面自制材质及自备角色有普通 Forward 和同格式独立输出对照；复用路径有原生资源身份、读写次序、完整颜色及生命周期证据。Half4 使用自己的 ID／标志约定，另需 R32 预期上一帧深度；不宣称与原版附件成本一致。已有双骨骼／表情／服装缩放／描边与深度偏移的独立数值对照；Vulkan 可选运动路径的严格颜色一致性仍有已知失败，按桌面预览提供。广泛制作 rig、完整角色／场景投影联调、更多面部贴花／法线、复杂透明、全部角色和移动附件仍待完成；名义存储减少不代表移动性能验收。另见 [场景后端](scene-deferred.md)、[ScreenShadow](scene-screen-shadow.md) 与 [光源阴影](scene-light-shadows.md) |
| E05 | Hi-Z SSR、排除角色；PDF 37–43 | 既有 Built-in Hi-Z／Planar skip 保留；显式 [Tile SSR／Probe](tile-reflections.md) 消费当前材质、Hi-Z 与 scene-only 历史；当前 [SRP Planar](tile-planar-reflections.md) 票据按同一扰动像素执行 Planar → SSR → Probe；[完整 Actor](srp-actor-forward.md) 消费同源反射票据，不污染场景输入／历史 | 桌面真实 SSR 命中／覆盖排除、部分覆盖保留 SSR、Probe 回退和生命周期已对照；新增真实反射之后的角色合成、独立历史 owner 以及主动注入角色历史的负对照。默认应用不变；完整角色／舞台反射组合、物理 GGX 和移动性能仍待完成，见 [过滤](ssr-roughness.md)、[旧统一反射](scene-reflection-resolve.md) |
| E06 | Planar 角色／发光网格及区域 mask；PDF 44–51、PPT 114 | Built-in 路径保留；新增 [SRP Planar 独立 producer](tile-planar-reflections.md)：镜像相机／oblique 裁剪、显式角色／发光 Draw、颜色与 depth／stencil coverage 重放、完整 mip、当前 Tile 深度／接收组投影及反射优先 | D3D11／Vulkan 的 42 组配置、84 份原生捕获，以及一个自备角色／服装四方向、蒙皮跳转和材质负对照已验证；单平面／单组、廉价角色材质，仍缺全角色画质、GGX、多平面／Probe 调度及移动性能，见 [角色适配](actor-planar-capture.md) 与 [旧 Planar](planar-reflection.md) |
| E07 | PBR GBuffer 贴花、MAOS／法线／高度遮蔽／水面贴花；PPT 116–120、PDF 24–25 | 既有 Deferred 分通道投影保留；新增 [Tile 原位贴花](tile-decals.md)：几何法线投影、精确 receiver stencil、三组独立 MOS 权重、HDR UI 发光输入；与几何同 subpass，保留遮罩／身份／GI，不分配 DBuffer | 桌面 34 组自制配置、逐绘制完整通道与光照对照；原版 Def 映射、完整场景／动态水面、一般蒙皮、反射桥接与移动带宽仍待完成；Tile Half 方向混合不声称等同旧逐层法线归一化 |
| E08 | HDR Monitor 与发光网格；PDF 56–58 | HdrMonitor 保留 Built-in；SrpHdrMonitor 显式 prepare-before-SRP／record，HDR Canvas、内容／时间调度与 UV／LED 发光网格消费 | 自制真实 WorldSpace／ScreenSpaceCamera UGUI、SRP 网格及三类实时采样灯链路已执行；原版整场舞台、视频编解码及移动帧时未验收，见 [Monitor 接入](hdr-monitor.md) |
| E09 | 点／胶囊／面贴花灯及 instancing；PDF 59–62、PPT 119 | SceneDecalLightSettings：Monitor、PBR、Scalar／GPU Instanced、float32 累加；Spot／Point 实时阴影、显式 [胶囊／面采样源阴影](extended-source-shadows.md) 和可选 [baked channel](scene-baked-shadows.md)，保留每灯 144 字节与独立 112 字节阴影布局 | 桌面 Monitor／GI、Spot 与 Point 六面／跨面 PCF 已验收；新增扩展源模型为当前线／矩形的等权可见性，不重加权 Monitor 辐射。连续软阴影、原版整舞台参数和移动带宽仍待实现或验收，见 [贴花灯](scene-decal-lights.md) |
| E10 | 天空、植被、水、折射、荧光棒等专用表面；PPT 109 | 默认关闭的 [独立水面](scene-water.md)、[自主天空](scene-sky.md)、[观众荧光棒](crowd-lightsticks.md)、[植被风场](vegetation-wind.md) 和 [薄叶材质](vegetation-leaf.md)；新增 [闭合凸体折射](scene-refraction.md)：自有凸体、多界面 Snell／Fresnel、全内反射、RGB IOR／吸收、当前 HDR Cube 和剩余能量附件 | 自制桌面凸体与现有专用表面有独立对照；完整制作植被／水面／观众／宝石、近场视差、粗糙／相交／嵌套折射介质、焦散、其他专用消费者与移动成本仍待完成。资料只列用途，未公开对应算法，不假定原版参数或整体画质一致 |
| P01 | 方差裁剪 TAA、ExcludeTAA、NoJitter；PDF 33 | 可选 [场景颜色 TAA](scene-temporal-antialiasing.md) 保留；新增 [整帧 TAA](desktop-host.md#可选整帧运动与-taa)，消费角色与场景的 Half4 运动附件，在特效之后／DOF 之前执行当前色包含的 HDR 方差裁剪与亮度加权；[显式投影 helper](desktop-host.md#显式投影抖动) 提供离屏矩阵和纹理修正，旧默认 shader 不变 | 自制桌面运动、分类、HDR 稳定性、历史拒绝和生命周期有对照；真实角色多视角的完整链路与同格式存储复用有控制。双骨骼／表情／描边、实际投影 jitter 与角色揭露场景的独立数值控制已覆盖；新增两套服装／三视角／三尺寸静态近景的空间收敛和负控。可选运动路径的 Vulkan 严格颜色失败仍保留。动态发丝／复杂透明归属、全舞台动态画质及移动成本仍待完成，不引入默认投影 jitter |
| P02 | Bokeh DOF 范围、分辨率无关散景、30 次采样；PPT 127–128 | 默认关闭的独立 [DOF 模块](bokeh-depth-of-field.md)：显式线性深度、手动清晰范围／物理镜头、自定义完整 30／43 点孔径；原生 GPU 采样数、整图及分辨率控制已验收；旧摄影路径不变 | 全动态角色／发丝透明与复杂近远遮挡质量、完整深度适配、移动内存与实际帧时；未恢复原文未公开的 30 点布局 |
| P03 | Bloom、Diffusion、Paraffin、色调／颜色处理；PPT 126 | 已有合成路径；新增不依赖私有文件的 profile／JSON／资产、Bradford 白平衡、颜色调整、LGG、八条曲线、GT 映射与 16³／32³／64³ LUT 制作／消费，见 [自主调色](authored-color-grading.md) | 独立模块及实际 Camera.Render 桥接已验收，默认路径不变；全场景外观、Unity Volume 参数／曲线语义适配、更广合成配置和移动性能仍待完成 |
| P04 | Motion Blur；PPT 126、PDF 15 | 新增可选 [Motion Blur](motion-blur.md)：实际几何对应／当前可见性、曝光时钟、tile／双方向深度重建，显式 DOF 后／Bloom 前消费；默认摄影不启用 | 自制刚体／蒙皮／blendshape、遮挡／暂停／切镜／jitter 及桌面整图／原生链已验证；完整角色发丝透明、复杂重叠方向与 DOF 联合质量、移动内存／帧时仍待完成；不宣称恢复原文未公开滤波器 |
| P05 | GTAO、脚部 Capsule AO；PDF 19、PPT126 | 显式胶囊、视轴 GTAO、Half 几何引导重建；新增可选运动对应消费、六相旋转、几何／身份／reactive 拒绝、方差裁剪与累积，见 [时域 GTAO](scene-gtao-temporal.md)、[GTAO](scene-gtao.md) 与 [Capsule AO](scene-screen-shadow.md) | 桌面自制运动／反遮挡已验证，默认不变；自动脚部拟合／全角色接触、薄面复杂动态场景质量、画外几何与移动内存／帧时仍待完成 |
| P06 | 距离雾／球形雾；PPT 130、PDF 29 | 默认关闭的 [多介质雾](fog-volumes.md)：距离＋八球联合积分、相机快照、真实深度与后处理桥接、逐表面透明雾；旧距离／单球路径不变 | 桌面整图、重叠／分离／相机内外、质量收敛和双透明层深度测试已验收；原版未公开参数、完整角色透明／折射、复杂排序及移动成本仍待完成 |
| P07 | 体积光及动态 DepthShadow；PPT 131 | 默认关闭的 [体积光](volumetric-lighting.md) 与 [低尺寸积分](volumetric-reconstruction.md)；新增 [联合重特效](heavy-fx.md) 中按不透明及各表面实际深度分别计算透射／散射 | 桌面整图、动态双光源阴影及共享重建路径已验证；原版参数、相交层／折射后深度、半透明光源遮挡、复杂舞台质量与移动成本仍待完成 |
| P08 | Flare／Ghost；PPT 129、PDF 31 | 默认关闭的 [Flare／Ghost](lens-flares.md)；另可直接写入 [联合重特效](heavy-fx.md) 共享附件，当前源可见性与显式时间不变 | 桌面整图、共享批次和实际相机链已验证；原版处方、半透明多层遮光、专用时域稳定性、复杂舞台与移动帧时仍待完成 |
| P09 | 低分辨率透明／扭曲／重特效及上采样；PDF 15、31 | [独立几何](low-resolution-fx.md) 与默认关闭的 [联合重特效](heavy-fx.md) 共用介质／有序几何／镜头附件、R8 修复决策和整批重画；可选 [当前表面 Forward 光照](fx-forward-lighting.md) 保留混合批次和材质 alpha | 桌面整图、当前蒙皮、介质／表面独立阴影有对照；启用光照的折射材质明确不支持，相交层排序、折射介质输运、完整舞台及移动成本仍待完成 |
| O01 | FSR 与高质量 TAA 输入；PDF 10、69 | 默认关闭的 [FSR1](fsr.md)：通过 UPM Core 的 AMD 算法执行 EASU／RCAS，独立 Compute／Raster；真实低尺寸相机、四档、当前 TAA、Bloom 后／Diffusion 前桥接，UI 保持完整分辨率 | 自制桌面全图数值、抗锯齿时序、真实 UGUI 次序及 720p／1080p 四档原生 GPU 事件计时已对照。完整角色／舞台、HDR 极亮部、其他图形后端、移动内存及实机净收益待完成；不替换默认摄影超采样 |
| O02 | Memoryless／RenderPass／SubPass／MRT 复用；PDF 11–12、22、27 | 已有 [TileRenderPass](tile-render-pass.md) 与 [TileSceneRenderer](tile-scene.md)：五 MRT、GI 复用、256-bit 保守颜色预算、显式位置光照／阴影／实时 Monitor；[几何 DepthID 与原位贴花](tile-decals.md) 同 subpass 有序混合、不增加贴花附件或 subpass | 预处理与 D32S8 单独计费，启用贴花名义合计 44 bytes/pixel；已有显式 SSR／Probe／Planar 后续消费及 G0／G1 可选 Store，Planar 捕获／mip／深度单独计费；Actor／运动完整整合、生产内容、Metal／移动驻留和负载测量仍待完成。旧 Deferred ABI／默认摄影不变；格式量化与 Direct 退出警告仍保留，未宣布完整 O02 或移动收益 |
| O03 | 移动端能力与驱动差异；PDF 8、64–65 | 现有完整管线验证集中于桌面 D3D11；新增 TileRenderPass 的桌面 Vulkan 运行与原生附件捕获，具备显式格式／MRT／预算拒绝 | Android Vulkan／iOS Metal 实机、整管线质量回退、实际内存／帧时与长时间运行；桌面 Vulkan 不等于移动验收 |
| C01 | 观众 LOD、模型预算、四向 runtime billboard、Compute；PPT 99–103 | 默认关闭的 [独立 Crowd](crowd.md)：八类共享当前骨／shape pose、GPU 最近模型预算、稳定排序／压缩、四向动态材质图集和 PBR／Toon 间接绘制；显式 CPU 回退。新增独立 [当前几何投影](crowd-shadows.md)：显式 Low／High、方向光和局部光、当前 pose、间接绘制及工作量上限，不复用主相机可见列表／billboard | 桌面原生几何／阴影／遮挡／透明次序、26 视角及 65,536 边界已对照，万人真实 compute／间接绘制有捕获；投影新增整图与原生骨／shape、万人及 65,536 放置验证。四视图仍为有限角度近似，完整制作观众、独立动作变化、时域／反射接入、制作场景投影成本及移动实测待完成；不捆绑观众资产 |

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

## 辅助动态骨的显式推进

`HairDynamicsSystem`（包含外衣／裙摆实例）、`BreastDynamicsSystem` 和
`BodySoftTissueDynamicsSystem` 默认继续在 Unity `LateUpdate` 中推进。
需要逐帧导出或离线对照的宿主，可以分别接管已有实例：

```csharp
hair.automaticSimulation = false;
breast.automaticSimulation = false;
softTissue.automaticSimulation = false;
// 每帧先施加动画和辅助骨姿势，再按项目原有依赖顺序推进各求解器。
hair.AdvanceSimulation(deltaSeconds, timelineSeconds);
breast.AdvanceSimulation(deltaSeconds);
softTissue.AdvanceSimulation(deltaSeconds);
```

外衣／裙摆是独立的 `HairDynamicsSystem` 实例，需要宿主同样接管；这个接口
不自动查找角色，也不驱动 Animator、Quartz 辅助骨或旧 `ClothDynamicsSystem`。
保留既有固定步长和每调用最多四个子步的追赶上限，大时间差不能替代逐帧重放。
同次调用内所有子步取相同风时刻，保持既有按帧采样约定。显式
`naturalWindTimeOverride` 优先于传入时刻；否则环境风使用 double 时刻，旧诊断风
沿用 float 相位。自动路径仍分别使用 `Time.timeAsDouble` 和 `Time.time`。

手动调用要求先关闭 `automaticSimulation`，否则抛出异常以避免一帧双重推进。
负数／非有限 delta、非有限或绝对值大于 `1e12` 的风时刻也抛出异常；零 delta
不改姿态、速度或累积时间。切换驱动方式不隐式清空已有状态，`ResetSimulation()`
仍显式执行既有预热。回退时间只改变风采样，不恢复历史姿态／速度；任意跳转需要
宿主恢复初始姿势并重放。当前摄影应用不自动启用这一接口。

同一 Unity update 内推进多个姿态并手动渲染时，还要给明确交给宿主管理的
`SkinnedMeshRenderer` 设置 `forceMatrixRecalculationPerRender = true`，结束时
恢复原值，否则骨骼变化可能没有进入本次蒙皮绘制。这个开关不属于物理时钟，
求解器不会擅自搜索或修改 renderer。参见 [Unity 的多次手动蒙皮渲染约定](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/SkinnedMeshRenderer-forceMatrixRecalculationPerRender.html)。

这为 PPT 75–78／90–94 的动作与风场对照提供可控时钟，不引入新的物理模型，
也不代表全服装碰撞、坐姿边界或原版视觉一致性已经验收。

实际角色诊断由 `--validate-srp-actor-character <directory> --validate-secondary-motion`
显式启用，需调用方自己的资产。当前两套服装／D3D11 与 Vulkan 对照包含
180 帧动画＋风／无风／重播、所有骨骼的位置与旋转，以及前／侧／后三个最终视图。
重播要求输出完全一致，风必须改变实际颜色和几何深度；只改变 Transform、不进入
蒙皮绘制不能通过验收。生成式控制另覆盖六秒重播、阵风／停歇、错误输入、时间覆盖
优先级及默认 Unity 时钟的一致性。双侧求解器重置时同步髋部缓存，避免首帧虚假的
根位移补偿；其余既有积分公式和追赶上限保持不变。

## 分层衣物的参考角度

PPT 77 的外层衣物参考内层裙摆角度由 `HairDynamicsSystem.useExternalReferenceLimits`
显式开启，默认 `false`，不会自动改变 Photo Studio 的现有物理。参考限制仍在
自己的局部硬限位之后执行，沿用有符号世界 ZXY Euler 分量的 max → min 顺序。
它约束骨骼方向，不是三角形级衣物碰撞，也不保证任意姿态没有穿模。

`SwingReferenceLimitInfo.bone` 接受明确指定的 `Transform` 或
`ActorSwingDynamicBone` 组件，以 `UnityEngine.Object` 保存类型安全的对象指针。
不要把组件的序列化指针声明成 `Transform`；对象名称可读并不能证明其 native 类型正确。
同求解器的旧 Transform 引用仍优先使用内部状态；启用后，组件引用也先解析到本求解器
节点，跨求解器引用才读指定对象的 Transform。空引用和其他对象类型不增加参考约束。
引用应在初始化前配置；修改拓扑／归属后需要重新初始化。

宿主负责有向无环的推进顺序：先动画与辅助骨，再内层裙摆，最后引用它的外衣。
需要跨求解器确定性时，对参与实例关闭 `automaticSimulation`，使用上述显式时钟；
不要依赖相同 MonoBehaviour 执行顺序，也不要建立相互引用的求解器环。
`ExternalReferenceLimitCorrections` 记录最近一次积分子步中实际改变角度的外部约束次数，
不是碰撞次数或累计帧数；不推进的调用不会产生新的测量。

生成式数值诊断：`--self-test-actor-rendering <directory> --self-test-reference-limits-only`。
自备角色诊断：`--photo-mode --validate-srp-actor-character <directory>
--validate-secondary-reference-limits`，可同时指定 `--motion-label home-sit-001`。
后者要求实际跨求解器约束生效、180 帧骨骼有限、开启／重播和关闭／原行为一致，
并检查三视角实际蒙皮颜色响应。诊断恢复自己接管的开关与渲染状态；它不启用默认应用功能。
当前一套自备服装／坐姿已在 D3D11 和 Vulkan 上完成上述对照，并独立核对整图
颜色、几何深度与背景不变；只证明该输入下约束进入实际蒙皮，不代表全部坐姿穿模已解决。

## 连续裙摆辅助骨

PPT 76 的腿部驱动裙摆辅助骨通过 `HairDynamicsSystem.useAuthoredSkirtHelpers`
显式开启，默认 `false`。应用默认行为不变；新路径仍在 Swing 积分之前提供辅助骨基准，
不替代碰撞求解器或自动安排跨求解器的执行顺序。

在 `InitializeSkirt` 前配置开关及 `QuartzSkirtSetting.referenceBone`。
引用接受明确指定的 `GameObject` 或 `Transform`，不按 Left/Right 名称猜测输入。
初始化保存参考骨局部旋转；之后以当前旋转乘初始旋转的逆得到父坐标系变化，
按 `rotationOrder`（0–5）分解，再应用各轴内外增益：
`outer*x + (inner-outer)*clamp(x, limitMin, limitMax)`。
区间内斜率为 inner、区间外为 outer，两端连续；这里的限值是增益过渡区间，
不是最终输出的硬夹紧范围。调用方应提供有序的有限限值和增益。

结果直接写专用辅助骨的局部旋转，不再乘辅助骨旧的 rest rotation；请勿把任意普通
关节当作这种辅助输出。新路径不使用旧版 `connectionAxis` 的整组正负号增益切换。
空引用或不支持的引用类型不驱动该辅助骨；无效旋转顺序明确报错。
引用、初始姿势或拓扑变化后重新初始化。反向轴极点及 Euler 分支仍有坐标奇异性，
本接口不承诺任意极端姿势全局连续。

生成式诊断：`--self-test-actor-rendering <directory> --self-test-skirt-helpers-only`，
覆盖六种旋转顺序、单轴几何、非单位初始坐标系、复合姿态边界、连续双增益、
显式对象引用、旧默认及关闭恢复。自备角色的局部边界诊断使用
`--photo-mode --validate-srp-actor-character <directory> --validate-skirt-helper-boundary
--validate-authored-skirt-helpers`；省略最后的开关可保留旧算法反例。
一套自备坐姿的同一 0.04° 输入扰动中，辅助骨跳变从约 79.44° 降至约 0.0011°，
另以大角度输入验证辅助骨仍有响应。该扰动是受控边界探针，不表示原始动作自然跨过此边界。

整链诊断使用 `--validate-secondary-reference-limits --validate-authored-skirt-helpers`：
所有比较组保持外衣参考约束开启，只切换新辅助骨路径，执行 180 帧动画与风、三视角、
逐帧骨骼重播和关闭恢复，并独立检查颜色／深度。当前 D3D11／Vulkan 坐姿、D3D11 另一套坐姿动作对照通过；
这是有限输入下的连续性与实际蒙皮证据，仍不能代替全服装接触质量、原版引擎一致性或移动端验收。

## 衣物位置式辅助骨

PPT 78 区分袖部的位置式运动与头发／外衣的旋转式运动。
在 `InitializeGarment` 或 `Initialize` 前设置
`HairDynamicsSystem.useAuthoredSlideDynamics = true` 可启用位置式分支；默认仍为 `false`，
不自动改变 Photo Studio 的配置。该分支只处理子条目 `dynamicType == 1` 的边，
其他边保留原有 Swing 路径。腿部软组织仍归 `BodySoftTissueDynamicsSystem`，不要重复注册。

位置式边可以是重合骨，不投影到固定骨长，也不为了追逐端点而旋转父骨。
重力、风、阻尼、刚度及轴向附加仍由子条目控制，使用相同的固定积分步。
`limitInfo` 在父坐标系中限制相对初始局部位置的**位移**：整数单位为毫米，
例如 `axisY = (-10, 10)` 表示上下各 1 cm，不是角度。
`useLimit == 0` 不夹紧；动态碰撞器 `type == 4` 不启用身体球体后备碰撞。
该独立实现让位置式附件继承动画／上游姿态，位移本身不额外制造 Swing 旋转。

宿主先应用动画和辅助骨，再按生产者到消费者顺序显式推进求解器。
关闭自动推进后调用 `AdvanceSimulation`，不要同时让 `LateUpdate` 积分。
改变模式后调用 `ResetSimulation`；改变拓扑、初始位置或归属后重新初始化。
此路径沿用单位尺度的骨架刚体坐标约定；未验证非均匀缩放及镜像层级。
碰撞形状支持与此前相同，并不新增布料网格碰撞、自碰撞或完整接触求解。

生成式诊断：`--self-test-actor-rendering <directory> --self-test-garment-slide-only`。
覆盖重合／非重合骨的解析位移、无额外旋转、正负轴毫米边界、旋转父坐标系、
禁用碰撞器、轴向附加、位置恢复力、动画附件、确定性重播及关闭恢复。
自备角色诊断：`--photo-mode --validate-srp-actor-character <directory>
--validate-secondary-reference-limits --validate-authored-garment-slides`。
它固定开启既有外衣参考约束和连续裙摆辅助骨，只切换衣物位置式模式，
核对实际条目归属、180 帧局部位移与毫米边界、三视角颜色／深度、重播及关闭恢复。
当前两套自备服装的 D3D11 对照及其中一套的 Vulkan 对照已通过；这些是有限输入的几何与实际蒙皮证据，
不代表已完成原版输出一致性、全部衣物接触质量或移动端验收。

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
