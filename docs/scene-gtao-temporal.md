# GTAO 时域累积

`SceneGtaoTemporalSettings` 是默认关闭的场景 AO 历史模块，消费 [实际场景运动对应](scene-motion.md)，不读取宿主 `_CameraMotionVectorsTexture`。参考 PDF33 的历史拒绝／方差裁剪要求及 [公开 GTAO 技术报告 §4.1](https://www.iryoku.com/downloads/Practical-Realtime-Strategies-for-Accurate-Indirect-Occlusion.pdf) 的多方向时域采样；旋转序列、几何阈值、reactive 判定和资源布局是独立约定，未复制原版实现。

```csharp
scene.motion.enabled = true; // 显式选择，仍须满足运动模块的能力和网格条件
scene.screenShadow.enabled = true;
scene.screenShadow.gtao.enabled = true;
scene.screenShadow.gtao.temporal = new SceneGtaoTemporalSettings {
    enabled = true,
    historyWeight = .85f,
    maximumHistory = 16,
    maximumFrameGap = 1,
    depthTolerance = .02f, // 世界空间切平面分离距离
    normalThreshold = .9f,
    reactiveThreshold = .2f
};
// seek、暂停后的时间跳转或调用方已知的内容突变：
scene.ResetGtaoHistory();
```

仍须按 [场景后端](scene-deferred.md) 登记表面并排除宿主重复绘制。开启 temporal 而未开启有效 motion 会拒绝场景帧；不会偷偷启用运动模块或退化为纯相机运动。`Full` 和 `Half` 均支持，不自动改变旧应用的画质档、投影 jitter、角色 TAA 分类或相机调度。

## 每次成功渲染的数据流

1. 运动 GPU 快照／对应与当前几何预通道完成后，计算本次 GTAO。默认 `rotateSamples=true`：成功渲染的 phase 按 0–5 循环，切片额外偏移 `((phase + .5) / 6 - .5) × π / slices`。六次旋转覆盖更密的等角方向；不会额外抖动相机或查询位置。`Half` 的粗积分与无支持点回退使用同一 phase。
2. 将未和胶囊合并的当前 GTAO 写入全分辨率浮点 R。当前整数像素减 `motion.xy × size` 得到上次像素坐标，手动检查四个 bilinear tap，不依赖纹理硬件线性过滤跨边缘采样。
3. 拒绝画外、无对应／背景、不同表面身份、法线不相容或世界切平面分离超阈值的 tap。旧位置以 motion 的上次正视深度重建，旧实际历史以旧深度重建；这能处理斜面上的合理视深度变化。法线使用上次对应网格法线，不是当前法线、normal map 或贴花。
4. 有效支持归一化；没有支持则采用当前值。旧 AO 与当前值之差超过 `reactiveThreshold` 时也采用当前值，处理静止接收面上的移动遮挡物。这个标量判定不能识别所有遮挡变化，也可能把采样噪声当作变化。
5. 当前 3×3 同身份、法线／切平面相容的邻域决定 min/max 和均值／标准差。历史裁剪到两者交集附近，`clampPadding` 提供余量，同时保留中心值并限制距中心不超过 `maximumHistoryDeviation`。shader 使用中心化矩计算方差，避免白色平坦区域相减抵消。
6. 权重为 `min(historyWeight, age / (age + 1)) × saturate(有效 bilinear 支持)`；年龄更新为 `1 + min(age, maximumHistory - 1) × saturate(支持)`。最后才计算本次全分辨率主灯 R／胶囊并合并 G，送入场景光照。历史只包含 GTAO，不把胶囊、主灯或已着色 HDR 再累积一次。

首帧／拒绝历史时，覆盖像素年龄为 1、混合权重为 0。只在成功 `Camera.Render` 后交换历史；同一游戏帧可连续渲染，相机之间不共享。两次渲染间 `Time.frameCount` 差大于 `maximumFrameGap`、运动整体失效、reset、尺寸／资源丢失、GTAO／temporal 配置变化或 GPU 投影矩阵最大元素变化达到 `.1` 时，重新从 phase 0 开始。更小的投影变化使用完整前后矩阵重投影。暂停期间帧号未前进或 seek 未反映在几何数据中时，调用方必须显式 reset。

## 借用输出与成本

| `SceneDeferredCamera.Frame` 附件 | 格式与内容 |
| --- | --- |
| `gtaoCurrent` | RGBAFloat：R 为本次未合并 GTAO；GBA 为 0，背景 R=1 |
| `gtaoHistory` | RGBAFloat：R 为过滤后 GTAO，G 为当前正视深度，B 为浮点年龄，A 为实际混合权重；背景 `(1,0,0,0)` |
| `gtaoHistoryNormalIdentity` | RGBAFloat：RGB 为当前世界网格法线，A 为当前稳定表面身份；背景全零 |

所有附件为相机所有、Point／Clamp、无深度、全分辨率。Raw 一张、AO 两张、法线／身份两张，总计 **`80 × width × height` 字节**；1920×1080 约 **158.2 MiB**，尚未计入 motion、顶点快照、几何、Half 粗目标及 GBuffer。另增加两次 fullscreen draw。该实现优先提供可验证数据与明确所有权，不构成移动内存或加速方案。

`GtaoTemporalTargetCount` 统计五张；`GtaoTemporalRawDrawCalls`／`GtaoTemporalResolveDrawCalls` 分别为当前值／历史绘制数；`GtaoTemporalPhase` 为最近提交的 phase；`GtaoHistoryAvailable` 表示有成功生产的历史，不保证下次像素通过拒绝检查。Frame 仅借用，不得写入、Release 或跨渲染保留使用权。reset 立即使 Frame 失效；开关变化在下一次渲染处理。

关闭或有效零强度时释放全部五张 temporal 目标；motion 仍由调用方独立控制。默认未开启 temporal 时不分配、不增加 draw，沿用原 Full／Half／关闭 GTAO 变体。格式须确为 RGBA32 float；缺失 Render／Sample 能力、shader、有效 motion 或分配失败会拒绝帧并清理，不返回假历史。

参数必须有限：historyWeight `0..0.97`、maximumHistory `2..64`、maximumFrameGap `1..60`、depthTolerance `.000001..10000`、normalThreshold `0...9999`、varianceGamma `0..4`、clampPadding／maximumHistoryDeviation `0..1`、reactiveThreshold `.000001..1`。启用时先验证再裁掉零强度；关闭时忽略未使用 temporal 参数。

## 验收范围

桌面 D3D11 的逐像素 oracle、完整／半分辨率稳定采样、自制移动遮挡物／双骨 blendshape、切镜、反遮挡、alpha 变化、双相机、主灯／胶囊组合和资源生命周期见 [渲染记录](rendering.md)。画面质量对照采用独立相机当次计算的更密方向 GTAO，不用历史自身作为画质真值。

仍有屏幕空间方法的画外／背面信息缺失、薄面与接触质量边界。reactiveThreshold 越低越容易拒绝历史，同时减少平滑效果；当前采样本身错误时，限制历史偏差不能恢复真实遮挡。全角色／多服装接触、自动脚部胶囊、完整动态场景、时域 TAA／Motion Blur 消费以及移动后端／实机成本不由本阶段代验。
