# 示范脚本：编排已有动作

这里提供当前播放器能读取的时间线 JSON，以及一个只用 Python 标准库的模板生成器。时序、文字和脚本均为本项目原创；不含官方剧情、动画曲线、模型或配音。它们不是原始 Lua，也不是逐骨骼手 K 数据。

| 模板参数 | 文件 | 时序与用途 |
| --- | --- | --- |
| `single` | [01-single-motion.timeline.json](01-single-motion.timeline.json) | 12 秒单动作，先检查一个动作能否加载 |
| `transition` | [02-motion-transition.timeline.json](02-motion-transition.timeline.json) | 20 秒，10 秒时切换动作，身体过渡 0.5 秒 |
| `sequence` | [03-motion-sequence.timeline.json](03-motion-sequence.timeline.json) | 30 秒，A → B → A，每段 10 秒；身体/配对面部过渡 0.5 秒，附原创文字提示 |

第三个示例的预期观察点是 0 秒的 A、10～10.5 秒的 A/B 混合、20～20.5 秒的 B/A 混合和 30 秒后的整体循环。素材本身的动作内容、长度和循环模式不由模板生成；请选与自己角色兼容的动作。

## 已有私人最小工作包

包内已把 sequence 模板绑定到自带的两个动作，并放入 `LocalAssets/runtime/story-timeline.json`。按[使用指南](../docs/user-guide.md)完成解压、配置和构建后，直接 `python -I studio.py story`，不用再运行下面的生成器。

## 生成并播放

以下命令在仓库根目录执行，使用已经配置的数据集。先按[首次使用指南](../docs/user-guide.md)完成 doctor 和 build，再查找动作：

```powershell
python -I studio.py catalog
$motionA = Read-Host 'role 为 motion 的第一个资源 name'
$motionB = Read-Host 'role 为 motion 的第二个资源 name'
python -I examples/prepare_demo.py --template sequence --motion "$motionA" --next-motion "$motionB" --output ./LocalAssets/demos/sequence.timeline.json
```

应填写清单中的完整 `name`，不是 `label`、文件路径或动作描述。生成器检查清单和文件，要求所选记录的 role 为 motion；不加载 Unity，也不能判定骨架或画面兼容。用 `--data "<目录>"` 可以临时指定其他数据集，不改保存的配置。

生成器只创建新的 JSON，拒绝覆盖已有输出，也不复制素材或启动 Player。加 `--dry-run` 会只打印 JSON，不创建目录或文件。单动作示例只传 `--motion`；transition/sequence 还必须传 `--next-motion`：

```powershell
python -I examples/prepare_demo.py --template single --motion "$motionA" --output ./LocalAssets/demos/single.timeline.json
python -I examples/prepare_demo.py --template transition --motion "$motionA" --next-motion "$motionB" --output ./LocalAssets/demos/transition.timeline.json
```

播放器只读取 `<数据目录>/story-timeline.json`。先关闭 Player，将已有同名文件备份或改名，再把新文件复制过去。下面的命令会在目标已存在时停止，避免覆盖：

```powershell
$data = Read-Host '生成示例时使用的数据目录完整路径'
$target = Join-Path $data 'story-timeline.json'
if (Test-Path -LiteralPath $target) { throw '请先备份或改名已有 story-timeline.json，未复制。' }
Copy-Item -LiteralPath ./LocalAssets/demos/sequence.timeline.json -Destination $target
python -I studio.py story --data "$data" --dry-run
```

如果数据集没有默认角色 fktn，需要在 story 命令中另加 `--character "<catalog 中的角色 ID>"`。确认 dry-run 无错误后，去掉 `--dry-run` 启动。

旧入口从 8.45 秒起播，面板 RESTART 回到 0；暂停时再按 RESUME。时间线到结尾会循环。修改 JSON 后重启 Player，不会热重载。示例旁白显示在剧情面板中，不是独立字幕渲染或语音合成。

## 如何改成自己的脚本

先保留工作副本。编辑 `body_motions` 数组中的 `time`、`motion`、`clipIn` 和过渡字段；根 `duration` 控制整体循环长度。新增事件后按 time 排序，尽量避免同一轨道同一时刻有多条事件。`messages[].text` 可替换为自己编写的短文字。

特别注意：动作事件的 `duration` 不会在该时刻自动停止动作；冻结播放器选择最近开始的动作并持续采样，直到被下一事件替换。不要用留空时间段的方式表达“停止”。完整语义见[时间线格式](../docs/scene-format.md)。

```powershell
python -I -m unittest discover -s tests -v
```

测试覆盖三个模板的字段、时序与动作绑定、生成器拒绝覆盖和 dry-run 不写入等行为。测试夹具没有真实游戏资产，因此不作为实机动画或视觉验收。
