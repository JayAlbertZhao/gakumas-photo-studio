# 场景 GTAO

`SceneDeferredCamera.screenShadow.gtao` 使用已登记场景的实际网格法线／视深度预通道计算环境可见性，写入现有 RG8 的 G 通道，默认关闭。PDF13／19 与 PPT126 提及 Amplify Occlusion，本包不附带该商业插件。此处按 [Jimenez 等人的 GTAO 技术报告 §4](https://www.iryoku.com/downloads/Practical-Realtime-Strategies-for-Accurate-Indirect-Occlusion.pdf) 的视轴 horizon 积分独立实现，不导入原版 shader 或游戏材质。

```csharp
scene.screenShadow.enabled = true;
scene.screenShadow.gtao = new SceneGtaoSettings {
    enabled = true,
    radius = .7f,          // 世界单位
    slices = 4,
    stepsPerSide = 8,
    maxRadiusPixels = 64,
    strength = 1,
    normalBias = .002f,
    falloffStart = .8f,
    thicknessBlend = 0,
    combineWithCapsules = SceneAmbientCombination.Multiply
};
```

调用方仍须登记 `scene.surfaces` 并按 [场景后端](scene-deferred.md) 的层约定排除宿主重复绘制。GTAO 不读取 ActorData、normal map、材质贴花法线或上一帧深度。不新增相机、历史纹理或全局 shader 状态，也不自动为现有 Photo Studio 开启效果。

## 当前积分与采样约定

以实际投影的观察射线为球坐标轴，在其垂直平面等角选择切片。每个切片沿两个屏幕方向查询场景深度，形成一对最大遮挡 horizon，再对投影网格法线的余弦权重做封闭形式的角积分。实现积分被遮蔽的角区，最后从已知无遮挡值 1 扣除，避免有限切片对孤立斜面的积分偏差。它计算深度高度场的可见区域，不把局部深度差或曲率直接当作 AO。

纹素位置取最近像素中心。屏幕方向由实际视图／投影矩阵的局部导数确定，支持透视、正交和偏移投影。每侧距离从至少一个像素到投影半径按二次分布采样，并受 `maxRadiusPixels` 限制；重建后的实际世界距离还必须小于 `radius`。纹素中心偏离切片平面时，先确认采样点位于真实网格法线的正半球，再投影回切片求角度，避免共面的斜面因纹素取整而错误自遮蔽。超出屏幕、背景、中心自身和无效距离不产生遮挡，不将越界查询 clamp 回边缘。

`normalBias` 沿网格法线移动接收点。半径外贡献为零；`falloffStart` 指定开始将候选 horizon 向无阻挡边界衰减的半径比例，1 表示硬半径截断。`thicknessBlend` 默认为 0，保留最大 horizon；大于 0 时，较远采样发现较低 horizon 会使当前值朝它衰减。这是薄物体启发式，不是实际厚度测量，也不能恢复深度图不可见的背面。零法线、背向或近切线朝向观察射线的法线输出完全可见。

当前每像素使用固定切片，无空间随机旋转、双边去噪、历史重投影或抖动。可重复的采样仍可能在运动、薄几何、半径硬切换和屏幕边界出现走样。低采样档、半径像素上限与画外几何缺失影响画质，不能用 GTAO 名称宣称完整场景 ground-truth 一致。

## Capsule AO 与光照

GTAO 与胶囊分别先应用自己的强度，再合并进 G。`Multiply` 相乘，隐含二者遮挡近似独立；同一物体同时参与深度与胶囊可能被重复计算。`Minimum` 只保留两者中较暗者，避免简单相乘，但也可能低估来自不同方向的独立遮挡。两种标量组合都不能精确计算角度可见性的并集。

最终光照仍只让基础间接漫反射乘 G。主灯可见性 R、主灯／附加灯直接光、直接光 GI 乘色与 emission 不随 GTAO 统一压暗。当前没有多次反弹补光、GTSO 镜面遮蔽或 bent normal。旧胶囊／主灯模式在 GTAO 关闭时保留原 shader 变体，不新增运行时绘制。

## 资源与输入边界

沿用 [ScreenShadow](scene-screen-shadow.md) 的全分辨率 RGBAFloat 几何与 RG8 输出两张私有目标，不为 GTAO 增加附件。`Frame.IsCurrent`、两相机隔离、目标丢失、resize 和禁用语义相同。仅 GTAO 有贡献时也生产预通道与 RG8；GTAO、主灯与胶囊全无贡献时不分配。

允许 1–16 个切片、每侧 2–32 个深度采样、1–256 像素半径上限；这约束输入，不承诺最大配置适合实时或移动设备。半径须有限且在 `.001..10000`，strength／falloffStart／thicknessBlend 在 `0..1`，bias 在 `0..radius`，组合枚举必须合法。启用时先验证参数，再裁掉零强度；关闭或 null 时忽略未使用参数。

P05 的完整范围仍包括低分辨率／时域稳定、全角色接触与移动成本。当前 Built-in 桌面路径没有与 SSR 合并为移动 RenderPass，也未实现 Memoryless/subpass。各阶段的实际执行证据见 [渲染记录](rendering.md)，本接口不构成这些平台和画质验收的替代。
