# 剧情时间线 JSON

这是冻结播放器实际读取的 `StoryTimeline` 数据结构，既可由使用者手写，也可由其自己的流程转换生成。不是原始学马 Lua，也不是已撤回的 `photo-studio.scene.v1` 包装格式。仓库没有通用 Lua 转换器或逐骨骼关键帧编辑器。

## 文件与生命周期

播放器固定从数据目录加载 `story-timeline.json`。保存为 UTF-8 JSON，字段大小写按本文；不要添加 JSON 注释或尾随逗号，也不要外包一层 `timeline` 对象。

`studio.py story` 预检查文件后启动；`photo` 不自动播放剧情。旧剧情入口从 **8.45 秒**起播，RESTART 回到 0 秒但不解除暂停，根时长结束后自动循环。修改文件后需重启 Player。

## 最小结构

下面是原创格式示例；`YOUR_MOTION_ID` 必须替换为自己的资源 name，不能直接用于渲染：

```json
{
  "schema_version": "photo-studio-example",
  "story_id": "my-motion-study",
  "duration": 12.0,
  "body_motions": [
    {
      "time": 0.0,
      "duration": 12.0,
      "order": 0,
      "motion": "YOUR_MOTION_ID",
      "clipIn": 0.0,
      "transition": 0.0,
      "facialTransition": 0.0,
      "ease": "Linear"
    }
  ],
  "messages": [],
  "voices": []
}
```

`schema_version` 是说明性标签，不是严格版本门禁；`story_id` 用于标识输入及日志。根 `duration` 是整个时间线的循环长度，单位秒，须为正有限数。`body_motions` 为非空数组。

## 动作事件字段

`body_motions` 和 `face_motions` 使用同一组字段，但实际资源由不同采样路径处理。

| 字段 | 单位 / 类型 | 当前行为 |
| --- | --- | --- |
| `time` | 秒，非负 | 事件开始时刻 |
| `duration` | 秒，非负 | 格式记录的片段长度；**动作选择不使用它作为停止条件** |
| `order` | 整数 | 保存事件顺序标记；当前选择函数不按它重新排序 |
| `motion` | 字符串 | bundle 资源 name / 面部曲线标识；不是 label 或文件路径 |
| `clipIn` | 秒 | 动作采样起点；当前局部采样时间为 clipIn + 时间线时间 − time |
| `transition` | 秒 | 从前一个动作切换到本事件的混合时长；接近 0 时直接切换 |
| `facialTransition` | 秒 | 没有独立 face_motions 接管时，身体配对面部曲线的过渡时长 |
| `ease` | 字符串 | 过渡曲线，推荐显式指定 |

当前明确处理 `Linear`、`InSine`、`OutSine`、`InQuad`、`OutQuad`、`InOutQuad`；`InOutSine`、空值或未知值走 InOutSine 默认。拼错不会报错，因此不要依赖未知字符串表示新曲线。

选择动作时取 `time` 不晚于当前时间的最近事件；同一时间出现多条时，数组中后出现的条目会覆盖前面的条目。请将数组按时间排列，不要指望 order 字段修正乱序。当前动作一直采样到下一个事件替换它，或整个时间线循环；片段自身的循环/末帧行为取决于实际 AnimationClip，脚本不保证其长度。

独立面部事件一旦开始，也会按最近事件持续接管。仅缩短其 duration 不会自动退回身体配对表情。身体切换的 `transition` 与 `facialTransition` 可以分别控制；第二个公开示例只设置身体过渡，第三个示例同时设置两者。

## 文字与声音

`messages` 中的事件使用 `time`、`duration`、`order`、`name`、`text`。当前 UI 显示活动事件的 text；name 被保存但不作为说话人标签显示。文字中的标签会被去除，过长文字会被截短，不是完整富文本字幕系统。同一时刻多个活动条目取数组中的最后一个。

`voices` 中的事件使用 `time`、`duration`、`order`、`voice`。voice 对应数据清单 voices 记录的 **label**，音频路径来自该记录的 `output_relative_path`。当前加载路径使用 WAV；不要直接填网络地址。示例默认不配音，不会合成或下载声音。定位/RESTART 不自动重播定位点语音。

所有台词和音频都应是自己有权使用的输入；官方剧情转换结果也属于私人数据，不提交到本仓库。

## 其他事件与校验范围

模型中还包含 face_overrides、eye_blinks、look_targets、actors、actor_layouts、actor_renderers、actor_colors、backgrounds、background_layouts、background_transforms、cameras、dofs、props、prop_layouts、fades、foregrounds、shakes、actor_lighting 和 paraffins。存在字段不代表任意场景均兼容；本次入门模板只覆盖身体/配对表情和文字，不把高级视觉事件列为已完整验收能力。

`studio.py story --dry-run` 检查根时长、非空身体动作数组、身体动作引用及非负 time/duration；它不完整校验高级事件，也不打开 bundle 检查其内容。模板生成器另外要求所选身体动作的 role 为 motion。仅加载可信输入，旧运行时路径解析不是安全沙箱。

数据类型和 `Latest` / `Previous` / `Active` / `Evaluate` 的实际逻辑见 [StoryTimelinePlayer.cs](../packages/com.digital-kotone.toolkit/Runtime/StoryTimelinePlayer.cs)；声音和身体 clip 加载见 [CharacterSceneRuntime.cs](../packages/com.digital-kotone.toolkit/Runtime/CharacterSceneRuntime.cs)。可运行的参数替换步骤见[示范脚本](../examples/README.md)。
