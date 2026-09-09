# 原创时间线结构示例

两个 JSON 只包含自行编写的时序和占位符，无官方剧情、动画曲线或资产。

- `01-single-motion.timeline.json`：12 秒单动作。
- `02-motion-transition.timeline.json`：20 秒，在第 10 秒切换动作，过渡 0.5 秒。

将 YOUR_*_MOTION_ID 替换为自己数据集中的动作 ID，再复制为自己的 runtime/story-timeline.json。不要覆盖未备份的文件。启动不带 --photo-mode 的 Player；旧入口从 8.45 秒起播，可通过剧情面板 RESTART。

它们是格式用例，无法在无资产仓库中直接渲染。Python 只检查字段和时序，不把它们计作实机动画验收。旧三个 *.scene.json 及新导出工具随回退撤回，保留在私人备份中。
