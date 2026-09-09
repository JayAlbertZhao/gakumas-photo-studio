# 使用指南：换电脑继续工作

目标结构是“源码由 Git 管理，私人素材由 ZIP 迁移”。另一台电脑克隆代码后，将自己的工作包解压到克隆根目录，资产即可就位；不需要重现旧电脑的盘符或用户名。首次仍需准备编辑器和 Python，并构建 Player。

本指南区分收到私人工作包的迁移流程和普通使用者自行准备数据的流程。公开仓库不提供原版素材下载，也不包含提取工具、密钥或官方剧情。

## 1. 新电脑准备

已验证环境是 Windows、Python 3.10+、已激活的 Tuanjie 2022.3.62t12，工程依赖 URP 14.2.0-t1。安装编辑器时需要 Windows Player 构建支持；构建还需要解析工程包依赖。不要直接升级 Unity/Tuanjie 版本来绕过提示。

```powershell
git --version
python --version
git clone https://github.com/JayAlbertZhao/gakumas-photo-studio.git
cd gakumas-photo-studio
```

后续命令都在这个目录执行。Python 工具只用标准库，无需 `pip install`。若机器只提供 `py`，确认版本后可将命令开头的 `python` 替换为 `py -3`。

## 2. 将私人 ZIP 解压到克隆根目录

先将 ZIP 通过自己的私人存储或移动硬盘传到新电脑。只解压到**新的、尚未配置的克隆**，避免覆盖已修改的 `studio.local.json` 或剧情。ZIP 顶层直接是 `LocalAssets`、`unity` 和 `studio.local.json`，没有额外套一层项目文件夹。

```powershell
$zip = Read-Host '已下载的私人资产 ZIP 完整路径'
Expand-Archive -LiteralPath $zip -DestinationPath .
python -I tools/verify_local_assets.py
```

校验应输出 `accepted: true`。这会核对工作包清单中的文件大小、SHA-256、路径边界及源码基线，不验证素材授权或画面正确性。应在第一次配置/编辑之前运行；之后你主动修改剧情或本机配置，再校验时出现对应内容差异是预期行为，不要为了恢复哈希覆盖自己的工作。

解压后的工作目录：

```text
gakumas-photo-studio/
  .git/                                  # 仅代码历史
  README.md
  studio.py                              # 配置、检查、构建、启动
  studio.local.json                      # 私人相对路径配置
  config/                                # Git：源码基线及空格式示例
  docs/                                  # Git：使用与格式文档
  examples/                              # Git：不含游戏内容的原创示例
  tools/                                 # Git：完整性和发布工具
  LocalAssets/
    README.md                            # 本目录唯一 Git 跟踪文件
    PACKAGE.json                         # 私人包文件清单与哈希
    START-HERE.md                         # 私人包简要步骤
    no-riverbed/README.txt                # 显式关闭对旧盘符河岸路径的依赖
    runtime/
      staging-manifest.json              # 相对路径清单
      bundles/                           # 模型、材质依赖与动作
      face-motions.json                  # 对应动作的面部曲线
      photo-facial-motions.json           # 摄影表情
      story-timeline.json                # 自行编排的示范剧情
      research/gpa/                      # 仅两份被旧代码固定路径读取的 LUT
      captures/                          # 截图后生成，不在初始包中
  unity/
    Assets/
      Scripts/                           # Git：冻结复现代码
      PrivateResources/Resources/        # 私人包：两份环境补充数据
    Packages/
    ProjectSettings/
    Library/                             # 构建生成，不迁移
    output/                              # 构建生成，不上传
```

模型、动作及运行输入集中在 `LocalAssets/runtime`。少量构建期数据放在 `unity/Assets/PrivateResources/Resources`，因为冻结代码通过 Unity `Resources.Load` 读取它们；ZIP 已放到正确位置，不需要再手动导入。旧 LUT 相对路径也保持原样，没有为目录美观改动 C#。

### 最小包的覆盖范围

私人最小包保留琴音的一套 face/body/hair、两个站立动作、对应面部曲线与摄影表情、递归材质/着色器依赖、两份 LUT 和两份环境补充数据。示范剧情是自行编排的 30 秒 A → B → A 序列，没有官方台词或配音。

不包含其他角色/服装、河岸场景几何、官方剧情、语音、APK、游戏源码/DLL、密钥、编辑器、Player、旧 Library、录像或研究转储。摄影模式使用代码生成的摄影棚，剧情示例不带场景、使用纯色底；不会复现旧电脑完整素材库中的所有画面与换装能力。

这是一份私人工作输入包，不是公开发布资产包。不要上传到公开仓库、GitHub Release 或公共 Issue。

## 3. 设置本机编辑器并构建

ZIP 中的 `dataRoot` 为 `LocalAssets/runtime`，`riverbedRoot` 为 `LocalAssets/no-riverbed`，都是相对克隆根目录的路径；编辑器路径留空，不携带旧电脑的盘符。

```powershell
$editor = Read-Host 'Tuanjie 编辑器可执行文件完整路径'
python -I studio.py configure --editor "$editor"
python -I studio.py doctor
```

doctor 的 `errors` 应为 `[]`，最小包应识别一个角色、一个 face、一个 costume、一个 hair、两个 motion 和三个 dependency。构建前 `player_built: false` 正常；`editor_configured` 只核对文件存在，不检查版本、许可或构建模块。

关闭占用同一工程的编辑器实例，再构建：

```powershell
python -I studio.py build
```

构建调用原 `PhotoStudioBuilder.BuildPlayerOnly`，不迁移源码。终端打印独立进度日志路径。默认超时 1200 秒，首次包解析或导入较慢时可使用 `build --timeout 2400`。

可在另一终端读取日志：

```powershell
$log = Read-Host '构建命令打印的日志完整路径'
Get-Content -LiteralPath $log -Encoding UTF8 -Tail 60
```

成功时生成 `unity/output/KotonePhotoStudio.exe`。保留完整输出目录，不要只移动 EXE。私人 Resources 会被嵌入构建，**本地 Player 不可作为当前源码项目的公开发行包上传**。

若要在编辑器内继续开发，编辑器 Play 不读取 Python 的本地配置。可从设置好数据环境变量的 PowerShell 启动同一工程：

```powershell
$env:GAKUMAS_PHOTO_STAGING = (Resolve-Path -LiteralPath './LocalAssets/runtime').Path
$env:GAKUMAS_RIVERBED_STAGING = (Resolve-Path -LiteralPath './LocalAssets/no-riverbed').Path
$project = (Resolve-Path -LiteralPath './unity').Path
& $editor -projectPath $project
```

原构建场景是 `Assets/Scenes/PhotoMode.unity`。源码基线校验会拒绝修改过的 Unity 代码；要继续开发运行时，需要明确进入新的开发阶段并审阅基线政策，不要随手刷新基线文件掩盖改动。

## 摄影与截图

```powershell
python -I studio.py photo --dry-run
python -I studio.py photo
```

逐条执行；dry-run 只检查输入和启动参数，不打开窗口，而且需要已有 Player。真正启动后会打印 PID 和独立运行日志路径，终端立即返回不表示窗口已加载完成。

| 操作 | 摄影模式 | 剧情模式 |
| --- | --- | --- |
| 右键拖动 / 中键拖动 / 滚轮 | 旋转 / 平移 / 缩放 | 调整镜头偏移 |
| A / D | 上一个 / 下一个动作 | 不生效 |
| W / S | 上一套 / 下一套服装 | 不生效 |
| Q / E | 上一个 / 下一个角色 | 不生效 |
| Space | 暂停 / 继续 | 暂停 / 继续剧情 |
| P 或 CAPTURE | 截图 | 截图 |
| F1 / Tab | 显示 / 隐藏面板 | 显示 / 隐藏面板 |
| R | 复位摄影镜头 | 复位剧情镜头偏移 |

最小包只有一个角色和一套服装，相应切换不会出现其他选项。表情、视线等在侧边面板调整。截图保存到 `LocalAssets/runtime/captures/`；如果改用外部数据目录，则保存到那个目录的 `captures/`。

当前没有通用的 UI 工程保存/载入、逐骨骼关键帧编辑、任意 FBX 导入或一键视频导出接口。

## 播放和修改示范脚本

私人最小包已将公开原创模板绑定到包内两个动作，直接运行：

```powershell
python -I studio.py story --dry-run
python -I studio.py story
```

固定读取 `LocalAssets/runtime/story-timeline.json`。旧入口从 8.45 秒起播，到根 `duration` 后从 0 循环。RESTART 回到开头；若已暂停，另按 RESUME。面板某些标题仍是旧研究故事的固定文字，不用于判断当前 JSON 身份；日志中的 `[Story] Loaded` 会给出实际 `story_id`。

修改 JSON 后需关闭并重新启动 Player，尚无热重载。RESTART 和时间滑块定位不会自动重播定位点的语音；配音验收需正常连续播放。最小示例不带配音。

要从其他模板生成新脚本，见[示范脚本教程](../examples/README.md)。先保存新文件，不要覆盖包内或自己已有的剧情。格式细节见[时间线说明](scene-format.md)，尤其是 `duration` 和动作切换的实际行为。

## 没有这份私人 ZIP 时

自行准备含 `staging-manifest.json` 的兼容数据，具体约定见[本地资产接口](assets.md)。可以放进 `LocalAssets/runtime`，也可保持在仓库外：

```powershell
$data = Read-Host '自己的兼容数据目录完整路径'
python -I studio.py configure --data "$data" --editor "$editor"
python -I studio.py doctor
python -I studio.py catalog
$character = Read-Host 'catalog 列出的角色 ID'
python -I studio.py photo --character "$character"
```

这里假设已完成前面的构建。空格式清单、原始游戏缓存或任意 FBX 都不是准备好的数据集。目录中每个清单路径必须有实际文件；文件存在仍不能保证素材版本、骨架、材质和动画兼容。没有准备好的素材时，本仓库还不能完成端到端动画制作。

路径优先级：命令行 → `studio.local.json` → 已有环境变量 → `LocalAssets/runtime`。命令行相对路径相对于终端；配置相对路径相对于仓库。临时 `--data` 不改保存的配置。清空配置值后仍可能使用已有环境变量，排错时也应检查环境。

## 排错与验证边界

| 现象 | 检查项 |
| --- | --- |
| 找不到 `LocalAssets/PACKAGE.json` | 是否多解压了一层目录？是否收到对应格式的私人包？ |
| 包哈希不符 | 初次解压时检查下载完整性；开始工作后检查是否自己改过剧情/配置 |
| `staging-manifest.json is missing` | data 应指向含清单的目录，不是 bundles 子目录 |
| `dependency IDs are not listed` | 依赖缺失，可能影响画面；本私人最小包应无此警告 |
| `Player is missing` | 先成功构建；仅克隆和解压不产生 EXE |
| 编辑器构建失败或超时 | 检查进度日志、版本、许可、包解析、Windows 构建模块及工程占用 |
| 找不到角色或动作 | 用 catalog 中的角色 ID / 资源 name，不要用 label 或文件路径代替 |
| `Story body clip missing` | 清单存在但 bundle 内没有可用 AnimationClip，需要查素材内容 |
| 开始时在中段 / 结尾循环 | 保留的 8.45 秒起播与循环行为，不是脚本复制失败 |
| 修改脚本不生效 | 核对固定文件名和 data 路径，关闭 Player 后重启 |
| 黑图、缺贴图、骨架或物理异常 | 核对素材、依赖、引擎与 Resources；不要仅依据退出码判断画面 |

doctor 检查清单与文件、报告编辑器和 EXE 是否存在；不核验它们的版本，也不检查剧情。剧情预检查使用 `story --dry-run`。启动成功、构建成功、哈希一致均不能代替实际观察角色、动作过渡和截图。

本最小包已在独立克隆中校验文件、构建 Player，摄影与示范剧情采样均完成；人工查看窗口帧截图能看到角色，剧情在 10.25 秒使用第二个动作与第一个动作混合。该检查不覆盖所有姿态、物理、交互或另一台机器。已有 batchmode 后台截图出现过黑图，不宣称全面画面验收通过。报告问题时说明编辑器版本、命令、操作步骤和错误片段，移除个人路径、密钥与私人内容，不要上传整份素材或 Player。

## 命令与实现依据

`configure / doctor / catalog / build / photo / story` 的完整参数以 `python -I studio.py <命令> --help` 为准。`--dry-run` 仅用于 build/photo/story；`--character` 仅用于 doctor/photo/story；`--timeout` 仅用于 build。

入口与路径规则对应 [studio.py](../studio.py)，剧情起播、面板及快捷键对应 [PhotoModeApp.cs](../unity/Assets/Scripts/PhotoModeApp.cs)，事件选择与循环对应 [StoryTimelinePlayer.cs](../unity/Assets/Scripts/StoryTimelinePlayer.cs)。公开素材边界及授权现状见 [NOTICE](../NOTICE.md)。
