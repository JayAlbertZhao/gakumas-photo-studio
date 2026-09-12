# Character and Scene Toolkit

面向 Unity / Tuanjie 的实验性角色与场景运行工具包。包含角色装配、动画、表情、语音、转换后的剧情时间线和重建渲染；不包含 Photo Studio 应用或原版游戏资产。

## 接入

可选框架扩展：`NaturalWindSettings`、`TemporalClassification`、`ActorVertexEncoding`。来源页码、最小调用及支持边界见 [框架技术清单](../../docs/framework-techniques.md)。风和 TAA 分类均需显式启用，不改变默认角色行为。

1. 使用 Tuanjie 2022.3.62t15、Windows Player 支持；当前固定依赖 URP 14.2.0-t1、Mathematics 1.3.2。其他编辑器/平台尚未验证。默认运行路径使用 Built-in Render Pipeline；声明 URP 依赖是为保留原有可选研究路径，不要求将项目切到 URP。
2. 在 Package Manager 选择 **Add package from disk**，选择本目录的 `package.json`。不要把 Runtime 又复制一份到 Assets；兼容程序集和类型不能重复。
3. 将项目 Color Space 设置为 Linear。在空场景中使用下方入口；场景不需要额外相机。核心创建自身相机、光源和默认环境。
4. 准备可信的兼容数据目录：至少有 `staging-manifest.json` 和完整的 face/costume/hair/motion bundle 及依赖。资产由宿主提供，不会下载；任意 FBX 或游戏缓存不能直接使用。

```csharp
using GakumasPhotoMode;
using UnityEngine;

public sealed class MyCharacterApp : MonoBehaviour
{
    public string dataRoot;
    public string characterId;
    private CharacterSceneRuntime runtime;

    private void Start()
    {
        runtime = gameObject.AddComponent<CharacterSceneRuntime>();
        runtime.Initialize(new CharacterSceneOptions {
            DataRoot = dataRoot,
            CharacterId = characterId,
            EnableOrbitInput = true
        });
    }
}
```

Package Manager 的 Samples 中提供相同用途的 **Minimal Character Host**；导入后将组件挂到空物体并填写 dataRoot / characterId。若应用使用 asmdef，在 references 中加入 `Gakumas.Toolkit`。namespace 仍为 `GakumasPhotoMode`；不需要引用 `Gakumas.PhotoStudio`、Python 启动器或 PhotoModeApp。

## 构建自己的 Player

当前团结版本的 URP 包会在 Built-in 构建时拒绝空 URP Asset 列表。包内提供从原摄影构建器原样抽出的 Editor 帮助类：在自己的 Editor 构建脚本中用 `GakumasPhotoMode.Editor.ToolkitBuildPipeline.BuildPlayer(options)` 代替 `BuildPipeline.BuildPlayer(options)`，options 仍为标准 `BuildPlayerOptions`。若使用 Editor asmdef，引用 `Gakumas.Toolkit.Editor`。

该帮助类仅在构建期间关闭 URP 的 unused-variant stripping，并在 finally 中恢复原设置；不会切换渲染管线。直接用默认 Build 按钮没有这个包装；也可自行管理相应 URP Global Settings。Windows 工程路径宜短，过长路径可能导致依赖包的 shadergraph 导入失败。

## 控制入口

通过 `CharacterSceneRuntime` 的 `CharacterIds`、`Costumes`、`Motions` 获取当前清单；调用 `SelectCharacter(id)`、`SelectMotion(label)`、`SelectCostume(index)`、`SelectExpression(index)`、`SelectExpressionMotion(name)`、`PlayVoice(index)`。`SetPlaybackPaused(bool)` 管理当前摄影/剧情暂停，`EvaluateMotion(seconds)` 可手动采样已有动作。

`Timeline` 暴露已有 StoryTimelinePlayer：`StartStory(seconds)`、`Seek(seconds, playVoice)`、`StopStory()`。`PreviewCamera`、`CharacterRoot`、`RenderControls` 供宿主接入相机/角色/光照控制。调用前先完成初始化；未初始化的清单和对象可能为 null。

核心不自动读取 Photo Studio 的快捷键或创建摄影面板，也不设置宿主窗口、帧率、VSync 和质量策略。`EnableOrbitInput` 默认 false；启用时保留原有右键旋转、中键平移、滚轮缩放相机辅助控制。

## 可选球形雾

相机上的 `OriginalStyleRenderPipeline.sphereFog` 是独立的 `SphereFogSettings`，默认关闭，不需要角色、原版 shader AB 或 Volume 资产。可在 Inspector 中配置，也可在宿主完成初始化后设置：

```csharp
var pipeline = runtime.PreviewCamera.GetComponent<OriginalStyleRenderPipeline>();
pipeline.sphereFog = new SphereFogSettings {
    enabled = true,
    center = new Vector3(0f, 1.5f, 4f), // 世界坐标；不随相机移动
    radius = 3f,                       // 世界单位
    density = 0.25f,                   // 每世界单位的中心消光系数
    color = new Color(0.35f, 0.5f, 0.8f), // sRGB，可使用 HDR RGB
    maximumOpacity = 0.6f,
    affectSky = true
};
// 关闭并恢复既有渲染路径：pipeline.sphereFog.enabled = false;
```

这是一个相机对应一个球体的独立实现，不查询场景或写全局参数；该项隔离不代表整个协调器已支持多实例。密度向边缘按抛物线衰减，沿近裁面到最近不透明深度积分；相机在球内、正交相机和偏移投影均使用同一路径。透明物若不写深度，不会独立截断雾。半径、密度、上限等无效时跳过；关闭时不新增临时渲染目标。详见 [渲染说明](../../docs/rendering.md)。

## 边界

`ScreenSpaceReflection` 可独立用于 Built-in Forward 场景，默认关闭。它捕获不含角色的背景颜色和深度，用 Hi-Z 追踪与历史深度检查产生反射；通过 `OriginalStyleRenderPipeline.screenSpaceReflection` 或自己的 HDR 回调接入。保留旧显式加法入口，另有无加法 TryTrace 供统一消费者使用。可选 [粗糙度过滤](../../docs/ssr-roughness.md) 按接收面扩散辐射并保留 miss／confidence 语义；物理 GGX 与移动优化仍未完成。详见 [SSR 接入与限制](../../docs/screen-space-reflection.md)。

SSR.backend 或独立 SceneDepthData.hierarchyBackend 可选择 `SceneShaderBackend.Compute`，使用相机私有 kernel 实例进行追踪／最小深度 RandomWrite。默认 Raster，支持显式能力降级与严格模式；桌面同输入结果有实际 GPU 对照，尚未证明性能提升。见 [计算后端](../../docs/scene-compute-backend.md)。

`PlanarReflection` 提供默认关闭的镜像捕获与接收面投影，适用于角色／发光网格和镜面宿主。显式指定单 pass 材质和几何输入，输出独立的 HDR 反射与覆盖，支持画外网格、oblique 裁剪、实际深度遮挡及 smoothness mip。它不自动给已有材质增加反射。详见 [Planar 接入](../../docs/planar-reflection.md)。

`ActorPlanarCaptureSet` 可为本工具包的 ActorToon 创建独立简化反射材质，读取当前 Base／Shade／Def／Ramp、细节层、图集、头发／眼部状态和 property block，提供匹配的覆盖重绘。`ActorPlanarLighting` 显式接受线性光照和头部方向；宿主在动画之后刷新，结束时 Dispose。已做一个本地角色／服装的四方向和蒙皮离屏检查，全角色画质与移动成本仍未验收。见 [角色捕获契约](../../docs/actor-planar-capture.md)。

`SceneReflectionResolve` 将 Planar、SSR 和 Probe 组成单个间接镜面项，并提供全分辨率法线差分扭曲及显式材质响应。默认关闭；通过 `OriginalStyleRenderPipeline.sceneReflectionResolve` 或自己的 HDR 回调消费。宿主必须让已登记接收面的主材质不再输出旧间接镜面项，组件不会自动改材质或检测重复光照。全角色捕获画质、GGX 过滤、多 Probe 调度和移动端优化仍未验收。详见 [统一反射输入契约](../../docs/scene-reflection-resolve.md)。

场景消费者可单独接入 `SceneDepthData`，获得不含 normal map 的世界网格法线、显式 SSR 资格、独立线性深度和可选最小深度层级。宿主登记 opaque／cutout 表面与排除层，不自动扫描角色或改变主画面；它只负责几何输入，SSR trace／历史／合成由独立的 ScreenSpaceReflection 消费者负责。相机所有权、调用时机与平台限制见 [SceneDepthData 接入](../../docs/scene-depth-data.md)。

这是从现有应用抽出的第一版 Unity 包，仍含单主角/单场景协调器、全局 shader / RenderSettings 状态和少量既有诊断环境变量/进程参数。尚不保证同进程多实例隔离，不是纯 C# 或引擎无关 SDK。宿主若已有相机/环境，需要自行安排场景；初版不提供通用世界管理器。

`Shutdown()` 沿用旧运行时的资源释放，不保证销毁所有独立创建的场景物体或还原全局渲染设置；建议一个运行会话对应一个承载场景，结束时卸载该场景。不要依赖反复初始化或并发场景的完整隔离。

可选外部环境、可读网格、LUT 等补充内容仍由宿主提供。不改变旧动作、物理、shader 算法或资源命名规则。原始 Lua 解析、手 K 编辑器、沙盒移动规则和番茄计时规则不在包内。

来源与许可边界见 [NOTICE](NOTICE.md)。包的技术可接入性不表示整个项目已获得统一开源再许可授权。
