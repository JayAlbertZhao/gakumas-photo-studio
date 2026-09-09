# 使用指南

本指南面向首次使用 Photo Studio 的开发者，介绍环境准备、资产配置、构建、摄影操作和时间线播放。当前项目需要使用者自行准备兼容素材；公开仓库只提供复现代码、工程配置及原创格式示例，不提供原版游戏资产、剧情或提取工具。

## 1. 准备环境

已验证的 Player 构建环境为 Windows、Tuanjie 2022.3.62t12 和 URP 14.2.0-t1。需要已激活的编辑器及 Windows Player 构建支持、Git、Python 3.10+。构建时还需要解析工程包依赖；其他 Unity/Tuanjie 版本和操作系统的 Player 构建尚未验证。

~~~powershell
git --version
python --version
git clone https://github.com/JayAlbertZhao/gakumas-photo-studio.git
cd gakumas-photo-studio
python -I studio.py --help
~~~

后续命令均在仓库根目录的 PowerShell 中执行。Python 工具只用标准库，无需安装额外包。若机器只提供 `py`，确认版本后可将命令开头的 `python` 替换为 `py -3`。

## 2. 准备兼容资产

将自己有权使用的兼容数据放入 `LocalAssets/runtime/`，或保存在仓库外。数据目录必须包含 `staging-manifest.json`，清单中每个相对路径都应指向实际文件。

~~~text
LocalAssets/
  README.md                    # 本目录唯一公开文件
  runtime/
    staging-manifest.json      # 必需：资源 ID、角色部件与文件路径
    bundles/                   # 实际路径以清单为准
    voices/                    # 可选：本地音频
    story-timeline.json        # 剧情模式输入
    face-motions.json          # 可选：配对面部曲线
    photo-facial-motions.json  # 可选：摄影表情
  resources/                   # 可选：构建期环境及可读网格补充
~~~

至少需要 face、costume、hair、motion 四类记录及相应依赖。头发应匹配服装变体，或提供角色的 base-0000 头发供现有逻辑回退。具体命名、字段和兼容要求见[资产接口](assets.md)。

空格式清单、原始游戏缓存或任意 FBX 都不是准备好的数据集。文件存在也不能保证骨架、蒙皮、材质或动画兼容；当前没有通用资产转换或自动重定向流程。没有兼容素材时，可以阅读代码、运行测试或构建工程，但无法显示角色。

## 3. 配置与预检查

`--data` 指向包含清单的目录，不是 bundles 子目录。按提示输入真实路径，带空格也可以：

~~~powershell
$editor = Read-Host 'Tuanjie 编辑器可执行文件完整路径'
$data = Read-Host '含 staging-manifest.json 的数据目录完整路径'
python -I studio.py configure --editor "$editor" --data "$data"
python -I studio.py doctor
~~~

配置保存到 Git 忽略的 `studio.local.json`；configure 不安装编辑器、转换素材或生成清单。请逐条执行，前一步失败时先处理错误。

| doctor 字段 | 如何判断 |
| --- | --- |
| `errors` | 应为 `[]`；非空时先修正数据，命令返回 2 |
| `warnings` | 逐条检查；依赖缺口可能影响加载和画面 |
| `characters` | 数据集识别到的角色 ID，启动时可显式选择 |
| `roles` | 清单记录数量，不是兼容性或视觉验证结果 |
| `editor_configured` | 仅表示编辑器文件存在，不核验版本、许可或构建模块 |
| `player_built` | 仅表示预期 EXE 存在；首次构建前为 false 正常，不核验 EXE 是否最新 |

doctor 不检查剧情。剧情预检查使用 `story --dry-run`，且需要已经构建 Player。

路径优先级为命令行 → `studio.local.json` → 已有环境变量 → `LocalAssets/runtime`。命令行相对路径相对于终端，配置相对路径相对于仓库。临时使用 `--data` 不会改写配置。环境变量和可选场景数据见[资产接口](assets.md)。

## 4. 构建 Player

如需可选环境/可读网格，先按[环境和网格补充](assets.md#环境和网格补充)导入自己的数据，再构建。无这些数据也能编译，但不代表所有渲染或换装效果一致。

关闭占用同一工程的编辑器实例，然后执行：

~~~powershell
python -I studio.py build
~~~

构建调用已有 `PhotoStudioBuilder.BuildPlayerOnly` 入口，不改写运行时代码。终端打印独立进度日志路径。默认超时 1200 秒，首次包解析或导入较慢时可使用 `build --timeout 2400`。

可在另一终端查看日志：

~~~powershell
$log = Read-Host '构建命令打印的日志完整路径'
Get-Content -LiteralPath $log -Encoding UTF8 -Tail 60
~~~

成功时生成 `unity/output/KotonePhotoStudio.exe`。保留完整输出目录，不要只移动 EXE。构建失败时先查日志，避免误用旧产物。`build --dry-run` 仅检查源码基线、编辑器路径并打印命令，不实际构建。

导入的 Resources 会被嵌入本地 Player；含未获发布许可素材的构建产物不能上传。本仓库的源码发布流程不包含本地 Player。

## 5. 摄影与截图

先列出当前数据集中的角色与资源：

~~~powershell
python -I studio.py catalog
$character = Read-Host 'characters 中的角色 ID'
python -I studio.py photo --character "$character" --dry-run
~~~

确认预检查无错误后去掉 dry-run：

~~~powershell
python -I studio.py photo --character "$character"
~~~

启动器打印 Player PID 和独立日志路径，随后返回终端；这不代表角色已经加载完成，请查看实际窗口和日志。未指定角色时，现有运行时默认 fktn；数据集中没有它时须显式选择，不会自动猜测其他角色。

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

表情、视线及其他参数在侧边面板调整，可选内容取决于数据集。截图保存到**数据目录**的 `captures/`，不是仓库根目录。

当前没有通用 UI 工程保存/载入、逐骨骼关键帧编辑、任意 FBX 导入或一键视频导出接口。

## 6. 编写和播放示范脚本

仓库提供三个原创时间线模板：单动作、动作过渡、A → B → A 序列与文字提示。它们只含时序、文字及占位 ID，没有素材，不能未经绑定就直接渲染。

先按[示范脚本教程](../examples/README.md)从 catalog 选择 `role: motion` 的资源 name，使用生成器创建新 JSON；备份已有剧情后，将需要播放的文件放到数据目录的 `story-timeline.json`。

~~~powershell
python -I studio.py story --character "$character" --dry-run
# 确认上一条命令没有错误后，再启动
python -I studio.py story --character "$character"
~~~

输入是[时间线 JSON](scene-format.md)，不能直接传原始 Lua。当前没有 `--script` 或 `--start-time` 参数，固定读取数据目录中的 `story-timeline.json`。

剧情从 8.45 秒起播，到根 duration 后从 0 循环。RESTART 回到开头但不解除暂停；已暂停时另按 RESUME。面板某些标题仍是历史固定文字，实际 story_id 以日志中的 `[Story] Loaded` 为准。

修改 JSON 后需关闭并重新启动 Player，尚无热重载。RESTART/时间滑块定位不自动重播定位点语音；配音需在正常连续播放中检查。当前脚本编排的是已有动作片段，不是逐骨骼手 K。

## 在 Unity 编辑器中打开工程

编辑器 Play 不读取 Python 的本地配置。可从已设置数据环境变量的终端启动工程，沿用前面输入的 editor 和 data：

~~~powershell
$env:GAKUMAS_PHOTO_STAGING = (Resolve-Path -LiteralPath $data).Path
$project = (Resolve-Path -LiteralPath './unity').Path
& $editor -projectPath $project
~~~

如需可选河岸环境，启动编辑器前另将 `GAKUMAS_RIVERBED_STAGING` 指向自己准备的数据目录。打开场景 `Assets/Scenes/PhotoMode.unity`。F8 渲染控制与图像检查见[角色渲染](rendering.md)。源码校验会拒绝未登记在历史基线及运行时修订清单中的修改；维护者流程见[源码基线](baseline.md)和[代码地图](code-map.md)。

## 排错与验证边界

| 现象 | 检查项 |
| --- | --- |
| `staging-manifest.json is missing` | data 应指向含清单的目录，不是 bundles 子目录 |
| `dependency IDs are not listed` | 依赖缺失，可能影响加载和画面 |
| `Player is missing` | 先成功构建；仅克隆不会生成 EXE |
| 编辑器构建失败或超时 | 查日志、版本、许可、包解析、构建模块和工程占用 |
| 找不到角色或动作 | 用 catalog 中的角色 ID / 资源 name，不要用 label 或文件路径代替 |
| `No hair bundle for character` | 检查匹配服装变体或 base-0000 的头发记录 |
| `Story body clip missing` | bundle 内可能没有可用 AnimationClip，需查实际素材 |
| 示例生成器拒绝已有输出 | 备份或改名旧文件，或使用新输出路径；不提供强制覆盖参数 |
| 修改脚本不生效 | 核对固定文件名和 data，关闭 Player 后重启 |
| 黑图、缺贴图、骨架或物理异常 | 核对素材、依赖、引擎与 Resources；退出码不能代表画面正确 |

现有 batchmode 后台截图存在黑图问题。构建成功、预检查成功和进程正常退出均不能代替对角色、动作、物理及截图的实际观察。

报告问题时说明编辑器版本、命令、操作步骤和相关错误片段。移除个人路径、密钥及其他非公开内容，不上传整份素材或含未获发布许可资源的 Player。

## 命令与实现依据

完整参数以 `python -I studio.py <命令> --help` 为准。`--dry-run` 仅用于 build/photo/story；`--character` 仅用于 doctor/photo/story；`--timeout` 仅用于 build。

入口与路径规则对应 [studio.py](../studio.py)，剧情起播、面板及快捷键对应 [PhotoModeApp.cs](../unity/Assets/Scripts/PhotoModeApp.cs)，事件选择与循环对应 [StoryTimelinePlayer.cs](../unity/Assets/Scripts/StoryTimelinePlayer.cs)。素材边界和授权现状见 [NOTICE](../NOTICE.md)。
