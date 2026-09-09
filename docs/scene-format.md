# 转换后剧情时间线

格式对应 StoryTimelinePlayer.cs 中的 StoryTimeline 和事件类型，不是原始学马 Lua，也不是已撤回的 photo-studio.scene.v1 包装格式。

从数据目录的 `story-timeline.json` 读取。旧入口从 **8.45 秒**起播，剧情面板可 RESTART 到开头；`--photo-mode` 不自动播放剧情。这些历史行为未修改。

根字段包括：

- `schema_version`、`story_id`、`duration`：标签、标识和秒数；旧播放器不严格校验 schema。
- `body_motions`、`face_motions`：事件字段为 time、duration、order、motion、clipIn、transition、facialTransition、ease。
- `face_overrides`、`eye_blinks`、`look_targets`：表情、眨眼及视线。
- `voices`、`messages`：本地音频引用及自行编写的文字。
- actors、actor_layouts、actor_renderers、backgrounds、cameras、props 及其他视觉事件：部分支持，以对应类型和 Evaluate 路径为准。

motion 要与本地数据集资源 ID 匹配，不是任意显示名。按时间与事件顺序编排，只加载可信内容。官方剧情转换结果也属于私人输入，不提交。

[示例](../examples/README.md)只演示手工编排已有动作片段，不表示支持逐骨骼手 K，不带资源或官方剧情。
