# 代码地图

保持源文件、命名空间、序列化类型、GUID 和调用顺序，不用移动或拆类整理运行时代码。下列 Assets 路径相对于 unity；未列目录的 C# 均在 Assets/Scripts。

| 位置 | 职责 |
| --- | --- |
| `Assets/Scenes/PhotoMode.unity` | 自建空场景，仅挂载 PhotoModeApp |
| `Assets/Editor/Phase1Builder.cs` | 原 PhotoStudioBuilder 构建及本地验证入口 |
| `PhotoModeApp.cs` | 角色装配、动作调度、UI、摄影和剧情协调 |
| `BundleCatalog.cs`、`Phase1Manifest.cs` | 外部数据清单和 bundle 加载 |
| `StoryTimelinePlayer.cs` | 时间线 JSON 数据类型、采样及事件分发 |
| `FaceExpressionRenderer.cs`、`FaceMotionLibrary.cs`、`CapturedLookAtRuntime.cs` | 表情、曲线及视线 |
| `CrossCharacterCostumeAssembler.cs`、`Quartz*System.cs` | 换装、辅助骨骼及形变 |
| `HairDynamicsSystem.cs`、`BreastDynamicsSystem.cs`、`BodySoftTissueDynamicsSystem.cs` | 动态解算 |
| `OriginalStyleRenderPipeline.cs`、`CapturedActorShadowMap.cs`、`SupersamplePresenter.cs` | 后处理、阴影和呈现 |
| `AdvStory*Runtime.cs`、`OriginalRiverbedEnvironment.cs` | 背景与剧情视觉事件 |
| `Assets/Resources/*.shader` | 重建 shader，非原始 shader 字节码 |
| `ActorAnimationStub/`、`CampusCommonStub/`、`VLStub/` | 数据兼容类型和复现组件，非官方程序集 |
| `tools/`、`tests/` | 不参与 Player 的源码基线、发布及格式检查 |
| `studio.py` | 外围本地配置、数据预检、构建/启动；只调用已有运行时，不参与 Player |
| `examples/prepare_demo.py` | 将原创时间线模板绑定到自己的动作 ID；只写新 JSON，不改运行时 |
| `tools/verify_local_assets.py` | 只读验证私人迁移包的文件哈希、路径和冻结基线，不验证视觉效果 |

ClothDynamicsSystem、PhotoColorGrade 和旧演示/诊断分支按基线保留；不以“未使用”为由删运行时代码。提取/反编译/采集探针仅在私人归档，不进入公开工程。
