# Character and Scene Toolkit

面向 Unity / Tuanjie 的实验性角色与场景运行工具包。包含角色装配、动画、表情、语音、转换后的剧情时间线和重建渲染；不包含 Photo Studio 应用或原版游戏资产。

## 接入

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

## 边界

这是从现有应用抽出的第一版 Unity 包，仍含单主角/单场景协调器、全局 shader / RenderSettings 状态和少量既有诊断环境变量/进程参数。尚不保证同进程多实例隔离，不是纯 C# 或引擎无关 SDK。宿主若已有相机/环境，需要自行安排场景；初版不提供通用世界管理器。

`Shutdown()` 沿用旧运行时的资源释放，不保证销毁所有独立创建的场景物体或还原全局渲染设置；建议一个运行会话对应一个承载场景，结束时卸载该场景。不要依赖反复初始化或并发场景的完整隔离。

可选外部环境、可读网格、LUT 等补充内容仍由宿主提供。不改变旧动作、物理、shader 算法或资源命名规则。原始 Lua 解析、手 K 编辑器、沙盒移动规则和番茄计时规则不在包内。

来源与许可边界见 [NOTICE](NOTICE.md)。包的技术可接入性不表示整个项目已获得统一开源再许可授权。
