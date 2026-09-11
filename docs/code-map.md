# 代码地图

核心工具包与摄影应用通过程序集边界分开。依赖方向为 `Gakumas.PhotoStudio → Gakumas.Toolkit → 兼容程序集 / Unity`；核心不引用 Photo Studio。namespace `GakumasPhotoMode` 保留，避免无关的类型命名迁移。

下列以 `unity/` 开头的路径相对于仓库根目录；其余运行时 C# 文件在 `packages/com.digital-kotone.toolkit/Runtime/`。

| 位置 | 职责 |
| --- | --- |
| `unity/Assets/Scenes/PhotoMode.unity` | 原空场景，仍挂载同 GUID 的 PhotoModeApp |
| `unity/Assets/Editor/Phase1Builder.cs` | 原 PhotoStudioBuilder 构建及本地验证入口 |
| `packages/com.digital-kotone.toolkit/Editor/ToolkitBuildPipeline.cs`（仓库根目录起） | 复用原有 Built-in/URP 构建包装，构建后恢复设置，不参与 Player |
| `unity/Assets/Applications/PhotoStudio/PhotoModeApp.cs` | 继承核心协调器；保留摄影 UI、快捷键、命令行、窗口/画质策略及原诊断入口 |
| `CharacterSceneRuntime.cs`、`CharacterSceneRuntime.Api.cs`、`CharacterSceneOptions.cs` | 核心角色/场景协调及显式初始化、选择、暂停接口，不自动启动摄影应用 |
| `BundleCatalog.cs`、`Phase1Manifest.cs` | 外部数据清单和 bundle 加载 |
| `StoryTimelinePlayer.cs` | 时间线 JSON 数据类型、采样及事件分发 |
| `FaceExpressionRenderer.cs`、`FaceMotionLibrary.cs`、`CapturedLookAtRuntime.cs` | 表情、曲线及视线 |
| `CrossCharacterCostumeAssembler.cs`、`Quartz*System.cs` | 换装、辅助骨骼及形变 |
| `HairDynamicsSystem.cs`、`BreastDynamicsSystem.cs`、`BodySoftTissueDynamicsSystem.cs` | 动态解算 |
| `OriginalStyleRenderPipeline.cs`、`CapturedActorShadowMap.cs`、`SupersamplePresenter.cs` | 后处理、阴影和呈现 |
| `AdvStory*Runtime.cs`、`OriginalRiverbedEnvironment.cs` | 背景与剧情视觉事件 |
| `Resources/*.shader` | 原有重建 shader，非原始 shader 字节码，资源名和公式未改 |
| `ActorRenderControls.cs` | 可由应用控制的渲染状态与补充 pass 调度，不含 F8 面板 |
| `unity/Assets/Applications/PhotoStudio/PhotoActorRenderControls.cs`、`ActorRenderingValidation.cs` | 原 F8 面板与显式运行的实景渲染探针 |
| `unity/Assets/Applications/PhotoStudio/ActorRenderingSelfTest.cs`、`Captured*State.cs`、`RenderDocCaptureBridge.cs` | 合成 GPU 测试及诊断抓帧连接，不是核心加载前提 |
| `Resources/ActorSurface.cginc`、`ActorOutline.cginc`、`ActorSupplemental.shader` | 共用角色着色、平滑法线描边和眼部前发覆盖 |
| `ActorShaderReferenceFeature.cs` | 私人原版 shader 的研究调度桥；不提供原版 shader，不保证兼容 |
| `Compatibility/ActorAnimationStub/`、`CampusCommonStub/`、`VLStub/` | 数据兼容类型和复现组件，保留原程序集标识，非官方 DLL |
| `packages/com.digital-kotone.toolkit/Samples~/MinimalHost/`（仓库根目录起） | 不引用摄影应用的最小接入示例 |
| `tools/`、`tests/` | 不参与 Player 的源码基线、发布及格式检查 |
| `studio.py` | 外围本地配置、数据预检、构建/启动；只调用已有运行时，不参与 Player |
| `examples/prepare_demo.py` | 将原创时间线模板绑定到自己的动作 ID；只写新 JSON，不改运行时 |
| `tools/verify_local_assets.py` | 可选的本地数据清单完整性校验；不参与正常构建/启动，也不验证视觉效果 |

ClothDynamicsSystem、PhotoColorGrade 和旧演示/诊断分支按基线保留；不以“未使用”为由删运行时代码。提取/反编译工具与原版截帧仅在私人目录；公开渲染探针只运行本项目并保存使用者自己的图像。
