# Gakumas Photo Studio

使用 Unity / Tuanjie 渲染类似《学园偶像大师》的角色动画。目前是 **研究预览版**：面向能够自行准备兼容资产的开发者，不是下载后直接使用的完整动画制作软件。

项目提供可复用的**角色与场景工具库**，Photo Studio 是使用该工具库的首个应用。核心以独立 UPM 包提供；其他应用可以引用它，不必引入摄影 UI、快捷键或应用构建入口。开放沙盒、番茄钟是后续应用方向，本仓库尚未交付这些应用。领域用语见 [项目术语](CONTEXT.md)。

[使用指南](docs/user-guide.md) · [工具库接入](docs/toolkit.md) · [示范脚本](examples/README.md) · [时间线格式](docs/scene-format.md) · [资产接口](docs/assets.md)

仓库只提供本项目的复现代码、工程配置和原创格式示例，不附带原版模型、贴图、动画、语音、剧情文本、游戏 DLL、原始源码或解包/采集工具。复现存在差异，与官方无隶属关系。

## 工具模块与应用

| 位置 | 用途 |
| --- | --- |
| `packages/com.digital-kotone.toolkit/` | 可独立引用的 UPM 包：角色装配、动画/表情/语音、剧情时间线和渲染；程序集 `Gakumas.Toolkit` |
| `unity/Assets/Applications/PhotoStudio/` | 摄影应用：窗口与画质策略、UI、快捷键、命令行及诊断；程序集 `Gakumas.PhotoStudio` |
| `unity/Assets/Editor/`、`studio.py` | 摄影应用的构建与启动工具，不是核心依赖 |

开发自己的应用：克隆后，在同版本编辑器的 Package Manager 中选择 **Add package from disk**，打开 `packages/com.digital-kotone.toolkit/package.json`。导入包内 **Minimal Character Host** 示例，将组件挂到空物体，填写自己的兼容数据目录即可接入。步骤、C# API 和当前限制见 [工具库接入](docs/toolkit.md)。使用现有摄影应用则继续下面的流程，原命令和默认行为不变。

## 当前能做什么

| 输入 | 支持情况 |
| --- | --- |
| UI 操作 | 角色、服装、动作、表情、视线、相机、播放与截图；仍有研究期按钮和默认值 |
| 转换后的剧情时间线 JSON | 可以播放动作、表情、配音及部分镜头/背景/效果事件 |
| 手 K / 原始学马 Lua 剧情脚本 | 内置关键帧编辑器、通用自制动画导入和原始 Lua 解析器尚未实现 |

不会下载或生成缺少的游戏资产。不保证任意 AssetBundle、FBX、全部角色/服装/剧情或完整 Live 兼容。详见 [实现边界](docs/status.md)。

## 首次使用（Windows）

需要 Git、Python 3.10+、已安装且完成许可激活的 **Tuanjie 2022.3.62t15**（含 Windows Player 构建支持），以及自己的兼容数据集。URP 依赖为 14.2.0-t1；当前修订已在该环境完成 Player 构建与渲染检查，其他环境不在本轮验证范围内。Python 工具仅用标准库，无需 pip install。Linux CI 只验证 Python 工具与源码边界。

```powershell
git clone https://github.com/JayAlbertZhao/gakumas-photo-studio.git
cd gakumas-photo-studio
python -I studio.py --help
```

1. 将已准备的兼容数据放入 `LocalAssets/runtime/`，或保留在仓库外。必须有 `staging-manifest.json`；见 [资产准备条件](docs/assets.md)。仅有原始游戏缓存或任意模型文件还不够。
2. 配置本机路径。下方尖括号内容需替换，路径带空格也可以：

```powershell
python -I studio.py configure --editor "<编辑器可执行文件完整路径>" --data "<自己的数据目录>"
python -I studio.py doctor
```

配置只写入忽略的 `studio.local.json`。doctor 检查清单、文件路径、必需角色部件和依赖缺口；报错时先处理错误，警告不等于视觉兼容保证。

3. 如需环境/可读网格补充，先按 [资产说明](docs/assets.md) 导入私人 Resources，再构建：

```powershell
python -I studio.py build
python -I studio.py catalog
$character = Read-Host 'catalog 列出的角色 ID'
python -I studio.py photo --character "$character"
```

build 调用已有 Unity 构建入口，打印独立进度日志位置；不会改写运行时代码。产物为 `unity/output/KotonePhotoStudio.exe`。需要检查启动命令而不打开程序时，用 `python -I studio.py photo --dry-run`。

photo/story 也会打印各自独立的本地运行日志路径，便于排查启动或加载异常。

只有源码也能构建，但没有角色可显示。这里不提供含素材的可执行发行包；私人 Resources 会被嵌入本地 Player，不能上传该产物。

## UI 与剧情

**F8** 打开角色渲染面板：描边、眼部前发覆盖、汗液／凌乱 Layer 强度、相机/世界空间主光、明暗边界、环境光及附加灯光。用法、可重复图像检查及限制见 [角色渲染](docs/rendering.md)。

兼容面部动作中的材质效果可自动驱动星星眼等图集动画，跟随动作时间暂停、跳转并在切换表情后恢复；需要自行准备对应效果资产。

右键旋转、中键平移、滚轮缩放；A/D 换动作，W/S 换服装，Q/E 换角色，Space 暂停，P 截图，F1/Tab 显隐 UI，R 复位镜头。截图写入自己数据目录的 `captures/`。

```powershell
# 枚举已有角色以及资源 ID / role / label
python -I studio.py catalog
# 默认角色不是自己数据集中的角色时，显式选择
python -I studio.py photo --character <角色ID>
# 读取自己数据目录中的 story-timeline.json
python -I studio.py story
```

剧情输入必须是 [转换后时间线格式](docs/scene-format.md)，不能直接传原始 Lua。三个 [原创格式示例](examples/README.md) 提供单动作、动作过渡及带文字提示的 A → B → A 序列，不附带资产。可用 `examples/prepare_demo.py` 将自己的动作 ID 写入新 JSON；它拒绝覆盖已有文件，不下载素材、不启动 Player。

旧剧情入口从 **8.45 秒**起播，到结尾会循环。面板 RESTART 回到开头；如果已暂停，另按 RESUME。旧摄影默认角色为 fktn。这些运行时行为保持不变。手工编排动作事件不等于逐骨骼手 K。

## 故障排查

- `staging-manifest.json is missing`：data 指错目录，或还没有准备好的数据集。
- `Player is missing`：先运行 build；仅克隆不会生成 EXE。
- `Editor not configured`：configure --editor 必须指向已安装的编辑器可执行文件。
- `dependency IDs are not listed`：数据不完整，对应角色/背景可能出错；不要当作渲染验收通过。
- 黑图、错误骨架或缺贴图：先核对引擎版本、bundle 内容、依赖和可选 Resources。当前自动后台截图存在黑图问题，不能以进程退出 0 认定画面正确。不会为了通过检查自动替换动画或修正骨架。
- 原始 Lua、任意 FBX 不被识别：不属于当前已实现的输入接口。

## 开发与发布边界

整理前基线保留为历史证据，角色渲染增量单独登记；动作、物理和剧情调度沿用恢复版本。外围启动器不编译进 Player。详见 [源码基线](docs/baseline.md) 和 [代码地图](docs/code-map.md)。

```powershell
python -I -m unittest discover -s tests -v
python -I tools/verify_baseline.py
python -I tools/release.py audit
```

公开文件由 `public-files.json` 精确允许；未知文件及私人资产默认不提交。维护者流程见 [发布检查单](docs/release.md)。来源和授权边界见 [NOTICE](NOTICE.md)：这是源码预览，目前没有为整个项目统一授予开源许可证，也不宣称独立洁净室开发。
