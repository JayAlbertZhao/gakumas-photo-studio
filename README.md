# Gakumas Photo Studio

使用 Unity / Tuanjie 渲染类似《学园偶像大师》的角色动画。目前是 **研究预览版**：面向能够自行准备兼容资产的开发者，不是下载后直接使用的完整动画制作软件。

仓库只提供本项目的复现代码、工程配置和原创格式示例，不附带原版模型、贴图、动画、语音、剧情文本、游戏 DLL、原始源码或解包/采集工具。复现存在差异，与官方无隶属关系。

## 当前能做什么

| 输入 | 支持情况 |
| --- | --- |
| UI 操作 | 角色、服装、动作、表情、视线、相机、播放与截图；仍有研究期按钮和默认值 |
| 转换后的剧情时间线 JSON | 可以播放动作、表情、配音及部分镜头/背景/效果事件 |
| 手 K / 原始学马 Lua 剧情脚本 | 内置关键帧编辑器、通用自制动画导入和原始 Lua 解析器尚未实现 |

不会下载或生成缺少的游戏资产。不保证任意 AssetBundle、FBX、全部角色/服装/剧情或完整 Live 兼容。详见 [实现边界](docs/status.md)。

## 首次使用（Windows）

需要 Git、Python 3.10+、已安装且完成许可激活的 **Tuanjie 2022.3.62t12**，以及自己的兼容数据集。URP 依赖为 14.2.0-t1；其他编辑器版本和操作系统的 Player 构建尚未验证。Python 工具仅用标准库，无需 pip install。

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
python -I studio.py photo
```

build 调用已有 Unity 构建入口，打印独立进度日志位置；不会改写运行时代码。产物为 `unity/output/KotonePhotoStudio.exe`。需要检查启动命令而不打开程序时，用 `python -I studio.py photo --dry-run`。

photo/story 也会打印各自独立的本地运行日志路径，便于排查启动或加载异常。

只有源码也能构建，但没有角色可显示。这里不提供含素材的可执行发行包；私人 Resources 会被嵌入本地 Player，不能上传该产物。

## UI 与剧情

右键旋转、中键平移、滚轮缩放；A/D 换动作，W/S 换服装，Q/E 换角色，Space 暂停，P 截图，F1/Tab 显隐 UI，R 复位镜头。截图写入自己数据目录的 `captures/`。

```powershell
# 枚举已有角色以及资源 ID / role / label
python -I studio.py catalog
# 默认角色不是自己数据集中的角色时，显式选择
python -I studio.py photo --character <角色ID>
# 读取自己数据目录中的 story-timeline.json
python -I studio.py story
```

剧情输入必须是 [转换后时间线格式](docs/scene-format.md)，不能直接传原始 Lua。两个 [原创格式示例](examples/README.md) 提供单动作和动作过渡模板，需替换动作 ID，不附带资产。请备份现有剧情文件后自行放入数据目录，启动器不会覆盖它。

旧剧情入口从 **8.45 秒**起播，可在面板 RESTART 从头播放；旧摄影默认角色为 fktn。这些运行时行为保持不变。手工编排动作事件不等于逐骨骼手 K。

## 故障排查

- `staging-manifest.json is missing`：data 指错目录，或还没有准备好的数据集。
- `Player is missing`：先运行 build；仅克隆不会生成 EXE。
- `Editor not configured`：configure --editor 必须指向已安装的编辑器可执行文件。
- `dependency IDs are not listed`：数据不完整，对应角色/背景可能出错；不要当作渲染验收通过。
- 黑图、错误骨架或缺贴图：先核对引擎版本、bundle 内容、依赖和可选 Resources。当前自动后台截图存在黑图问题，不能以进程退出 0 认定画面正确。不会为了通过检查自动替换动画或修正骨架。
- 原始 Lua、任意 FBX 不被识别：不属于当前已实现的输入接口。

## 开发与发布边界

Unity 代码冻结于经核对的整理前基线，外围启动器不改动画、物理、骨骼、渲染或 UI。详见 [源码基线](docs/baseline.md) 和 [代码地图](docs/code-map.md)。

```powershell
python -I -m unittest discover -s tests -v
python -I tools/verify_baseline.py
python -I tools/release.py audit
```

公开文件由 `public-files.json` 精确允许；未知文件及私人资产默认不提交。维护者流程见 [发布检查单](docs/release.md)。来源和授权边界见 [NOTICE](NOTICE.md)：这是源码预览，目前没有为整个项目统一授予开源许可证，也不宣称独立洁净室开发。
