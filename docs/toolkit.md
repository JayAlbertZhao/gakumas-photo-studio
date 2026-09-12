# 在自己的应用中使用工具库

核心目录为 [com.digital-kotone.toolkit](../packages/com.digital-kotone.toolkit/README.md)，它是实际的 Unity Package Manager 包。Photo Studio 通过 `unity/Packages/manifest.json` 的 `file:../../packages/com.digital-kotone.toolkit` 引用同一份代码，没有第二套实现。

## 最短接入路径

1. 克隆仓库，用 Tuanjie 2022.3.62t15 创建自己的 Built-in 3D 项目，设置 Linear 色彩空间。
2. Package Manager → **Add package from disk** → 选择本仓库 `packages/com.digital-kotone.toolkit/package.json`。包声明所需依赖；当前 URP 14.2.0-t1 是已验证团结环境的版本，不宣称标准 Unity 2022.3 直接兼容。
3. 在该包的 Samples 中导入 **Minimal Character Host**。将示例组件挂到空场景的空物体；删除模板自带的 Camera / Light，避免与核心创建的相机/光源重复。
4. 填写自己准备的 runtime 数据目录和角色 ID，进入 Play Mode。使用 [资产接口](assets.md) 准备数据；可先在本仓库执行 `python -I studio.py doctor --data "<数据目录>"` 和 `catalog`。这些 Python 命令用于预检，不是工具包的运行依赖。

只需引用该包，不需要把 `unity/Assets/Applications/PhotoStudio` 或 `Assets/Editor` 复制进自己的项目。自己的代码若有 asmdef，添加程序集引用 `Gakumas.Toolkit`；使用 `GakumasPhotoMode` namespace。

可复制 [MinimalCharacterHost.cs](../packages/com.digital-kotone.toolkit/Samples~/MinimalHost/MinimalCharacterHost.cs) 作为应用入口。完整最小 C# 示例和生命周期限制见 [包说明](../packages/com.digital-kotone.toolkit/README.md)。

构建 Player 时使用包内 `GakumasPhotoMode.Editor.ToolkitBuildPipeline.BuildPlayer(BuildPlayerOptions)`：它复用原摄影构建器针对当前 URP 版本的临时设置与 finally 恢复逻辑，不切换渲染管线。直接使用默认 Build 按钮仍需自己处理 URP unused-variant stripping；详情见包说明。建议将 Windows 宿主项目放在较短路径，避免依赖包导入超出路径限制。

## API 与宿主职责

来自讲演的框架技术覆盖、可选自然风、TAA 表面分类和自制网格编码接口见 [框架技术清单](framework-techniques.md)。这些模块不要求 Photo Studio UI；未实现的场景／移动端能力保留明确状态。

| 需要做的事 | 入口 |
| --- | --- |
| 显式初始化 | `Initialize(CharacterSceneOptions)`；DataRoot 必填 |
| 初始资产选择 | options 的 CharacterId、CostumeLabel、OutfitOwner、HairLabel、MotionLabel、FaceMotionName |
| 查询 / 切换角色与服装 | `CharacterIds`、`Costumes`、`CurrentCharacter`、`SelectCharacter(id)`、`SelectCostume(index)` |
| 动作与表情 | `Motions`、`SelectMotion(label)`、`SelectExpression(index)`、`SelectExpressionMotion(name)` |
| 暂停 / 手动采样 | `SetPlaybackPaused(bool)`、`EvaluateMotion(seconds)` |
| 语音 | `VoiceCount`、`PlayVoice(index)`；读取外部清单，不合成语音 |
| 剧情播放 / 跳转 | `Timeline.StartStory(seconds)`、`Timeline.Seek(seconds, playVoice)`、`Timeline.StopStory()` |
| 相机 / 角色 / 渲染状态 | `PreviewCamera`、`CharacterRoot`、`DefaultEnvironmentRoot`、`RenderControls` |
| 可选局部球形雾 | 相机的 `OriginalStyleRenderPipeline.sphereFog`；每相机一个 `SphereFogSettings`，默认关闭 |
| 可选场景几何与 Hi-Z 输入 | `SceneDepthData`；显式登记表面，读取相机自己的法线／SSR mask／线性深度及最小深度层级，见 [输入契约](scene-depth-data.md) |
| 可选场景 SSR | `ScreenSpaceReflection`；绑定到 HDR 管线，使用不含角色的场景颜色／深度历史，见 [SSR 接入](screen-space-reflection.md) |
| 可选平面反射输入 | `PlanarReflection`；显式简化 Forward 绘制、画外网格、区域覆盖和粗糙度 mip，主相机读取独立反射纹理，见 [Planar 接入](planar-reflection.md) |
| 可选角色反射适配 | `ActorPlanarCaptureSet`／`ActorPlanarLighting`；独立的 ActorToon 材质快照、简化光照及匹配眼部／头发覆盖，见 [角色捕获](actor-planar-capture.md) |
| 可选 HDR Monitor | `HdrMonitor`／`MonitorEmissionMaterial`；专用 UI 相机、HDR 纹理、显式更新调度与 UV 分区／LED 发光消费，见 [Monitor 接入](hdr-monitor.md) |
| 可选场景 Deferred 与材质贴花 | `SceneDeferredCamera`；显式场景层、PBR GBuffer、分通道投影与 HDR／深度输出，宿主角色保留 Forward，见 [场景后端](scene-deferred.md) |
| 可选 Monitor 场景照明 | `SceneDecalLightSettings`；点／胶囊／面／Spot、UV 采样、GPU 实例化与 Scalar 对照，见 [贴花灯](scene-decal-lights.md)；Spot 可选 [动态光源阴影](scene-light-shadows.md) |
| 可选场景 GI | `SceneGiInput`；UV2 Lightmap／显式 SH／场景绑定、独立 GI 缓冲、逐灯乘色及背向漫反射，已有数据的消费链见 [GI 接入](scene-gi.md) |
| 可选统一间接反射 | `SceneReflectionResolve`；Planar 优先、SSR 失败回退 Probe、全分辨率法线扭曲和材质响应；宿主须去除接收面原间接镜面项，见 [输入与合成契约](scene-reflection-resolve.md) |
| 可选反射计算后端 | `SceneShaderBackend.Compute`；SSR trace 与 Hi-Z RandomWrite，独立深度组件也可使用，能力回退与状态见 [计算后端](scene-compute-backend.md) |
| 可选 SSR 粗糙度 | `SsrRoughnessSettings`；接收面感知的两遍辐射过滤，默认无额外目标，保留 Planar 优先与 Probe 置信度回退，见 [过滤契约](ssr-roughness.md) |

字符串选择接口找不到 ID/label 时返回 false；索引接口沿用已有行为。需先初始化，部分清单在初始化前为空引用。`CharacterSceneOptions.StartStory` 默认 false；true 沿用旧自动剧情起播点 **8.45 秒**。若要从头播放，初始化后显式调用 `Timeline.StartStory(0f)`。

核心只负责已有角色/场景能力。窗口尺寸、帧率、VSync、画质、应用 UI、快捷键、移动控制、计时规则由宿主决定。`EnableOrbitInput` 默认 false，示例组件默认启用便于观察角色；禁用后可以直接驱动 `PreviewCamera`。

本轮不加入新的资产格式、事件算法、游戏规则或自动迁移逻辑。Photo Studio 原有命令、UI、默认角色、剧情时间、摄影截图路径保持原样。核心仍有共享渲染状态和默认场景构建，尚不承诺多实例/多角色系统；接入时先使用单承载场景。

## 私人资产与生命周期

UPM 包只包含代码和重建 shader。runtime 外部清单与 bundle 不随包发布；可选环境/网格 Resources 要放在自己的应用项目中。Photo Studio 的资源导入器只针对仓库中的摄影工程，不能把它当作任意宿主项目的安装器。

原有 Shutdown 不完整销毁所有独立场景对象、不还原全部全局渲染状态。这次保留该行为；结束运行会话时卸载宿主承载场景，不宣称可安全反复初始化或并发多个完整协调器。读取不可信路径不是当前接口的安全保证。

源码边界与回归方式见 [基线记录](baseline.md)、[代码地图](code-map.md)。来源/许可限制见 [NOTICE](../NOTICE.md)，本次拆包不自动改变许可证状态。
