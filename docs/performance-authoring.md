# 共享角色表演制作输入

`PerformanceClip` 把稀疏面部权重、贴花曲线、Prefab／材质区间和定位器定义放入同一份独立制作数据，对应 PPT 54、59–62 的协同制作与自定义属性传递。它不采用原版未公开的 Maya／FBX 数据格式，也不附带模型、贴图或特效资产。

## 接入已有宿主

从 [自制示范数据](../examples/authored-performance.json) 开始，将资源 ID 映射到自己提供的对象。ID 是区分大小写的字母、数字、下划线、点或连字符，最长 128 字符；不作为文件路径、层级路径或自动搜索名称。

```csharp
using GakumasPhotoMode;

PerformanceClip.TryParse(myJsonText, out var clip, out var reason);
var bindings = new PerformanceBindings();
bindings.morphs.Add("face.smile", new PerformanceBindings.Morph {
    faceDriver = myExplicitFaceDriver, index = mySmileIndex
});
// 普通 Unity blendshape 可改为 renderer = mySkin, index = myIndex。
// 输入是归一化权重，Unity 的百分比权重在绑定内部转换。
bindings.decals.Add("cheek", myProjector);
bindings.prefabs.Add("my.marker", myOwnPrefab);
bindings.attachments.Add("tear", myCurrentLocatorTransform);
bindings.textures.Add("my.atlas", myOwnAtlas);
bindings.materials.Add("face.surface", new PerformanceBindings.MaterialSlot {
    renderer = myRenderer, slot = myMaterialSlot
});
if (!PerformancePlayer.TryCreate(clip, bindings, out var player, out reason))
    throw new System.ArgumentException(reason);

// 身体动作先由宿主采样。两个阶段之间不要 Render、yield 或提交一帧。
if (player.TrySamplePose(mySeconds)) {
    myFace.ApplyCurrentWeights();
    myVertexSource.TryUpdate(myLocatorRig, myShaderVertexScale, out reason);
    player.TrySampleEffects();
}
// 此后才提交相机绘制。定位器失败会隐藏挂点，依附特效停止显示。

player.Stop(); // 恢复借用的权重／贴花值／材质，移除自己的特效实例。
myFace.ApplyCurrentWeights(); // 通知宿主把恢复后的权重应用到几何。
player.Dispose();
```

定位器由宿主用 `clip.locators` 创建 [VertexLocatorRig](vertex-locators.md)，根据实际当前几何更新，不自动猜顶点、骨骼名或 shader 缩放。贴花绘制仍由 [FaceDecalLayer](animated-face-decals.md) 或宿主 command buffer 接管；播放数据不改变渲染 pass。物理／世界轨迹粒子限制继承 [MotionEffect](motion-effects.md)。

绑定时复制所有制作数据及绑定字段，Unity 对象仍由宿主借出。修改原始 clip、字典或输入数组不会改变已有播放器，须重新创建。Morph／Decal 参数在绑定期间由该播放器独占；不要让 Animator／其他播放器同时写这些值。共享材质／纹理／Prefab 不修改，材质区间使用私有克隆；外部替换材质槽时失败并保留外部所有权。

同一目标的材质区间可重叠，最后序列化的有效项拥有该槽。材质需要 `_MainTex`／`_ShadeTex`／`_DefTex`／`_ActorTextureFrame` 契约。Color／Shade／Def 在此指三种纹理输入，未将 PPT 中的 Color 误当成额外的 tint 参数。贴花 tint 则是显式线性 float4。

## 时间与数据边界

- 关键帧时间是 float32 秒，严格递增。插值 0／1／2 分别为 Hold／Linear／Hermite，切线单位为每秒变化值。Hermite 过冲导致无效形变或贴花参数时整次采样失败，不钳制伪装成有效结果。
- 非负 loop 时钟按 duration 取模；不循环时 morph／decal 保持末端值，区间按 `[start,end)` 停止。负时间恢复绑定前状态。暂停由宿主停止或重复同一时间采样；没有全局时间写入。
- 成功的 TrySamplePose 后，先应用几何及当前定位器，再调用一次 TrySampleEffects。失败恢复借用值并清理实例；恢复面部权重后的实际 mesh 更新仍由宿主负责。
- 最多 256 morph、8 decal、32 prefab 区间、64 材质区间、256 定位器、总计 16384 keys，每条曲线最多 8192 keys。JSON 最多 1 MiB；版本和已知字段严格检查，JsonUtility 忽略未知字段，不能用未知字段保存必要行为。
- `duration`／curve 时间采用 float32；区间 start／duration 使用 double，可精确表示两个已量化端点之差。总时长不超过 3600 秒，单 Prefab 区间不超过 60 秒。资源与曲线有界不代表低移动开销。

## Unity 导入

将自制 JSON 保存为 `.performance` 文件放进 Assets，`PerformanceClipImporter` 转成可序列化 `PerformanceClipAsset`。示范文件保留 `.json` 方便阅读，使用时另存为 `.performance`。重新导入会更新数据，已运行的播放器继续使用自己的副本。无效输入生成带 importError、无 clip 的资产，不会继续使用上一份有效数据。

FBX 节点可带名为 `photoStudioPerformance` 的字符串自定义属性，内容为同一 JSON。仅这个精确名称触发转换，导入后的节点带被动 `ImportedPerformance` 组件，不自动播放，也不改变未标记 FBX 的模型设置。自定义属性回调见 [Unity API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/AssetPostprocessor.OnPostprocessGameObjectWithUserProperties.html)。

## DCC 制作辅助

[performance_authoring.py](../tools/dcc/performance_authoring.py) 不依赖 Maya／Blender。`bake(template, mappings, start_frame, end_frame, fps, read_attribute)` 消费指定属性，在包含首尾的整帧点采样。Morph／Decal 输出线性曲线；effect／material 启用值在左端点达到 0.5 时生成有效区间，每段重新开始特效局部时间。非整帧事件、weighted tangent 或复杂约束在采样点之间可能损失细节，不能宣称与任意 DCC 原曲线连续等价。

[maya_performance.py](../tools/dcc/maya_performance.py) 提供显式属性的共用数值／Key 面板和 export：在 Maya 中把 tools/dcc 加入 sys.path，`show_editor(mappings)` 操作已有属性；`export(controller, template, mappings, start_frame, end_frame, fps, destination)` 将采样数据写入该 controller 的字符串属性，并可输出 sidecar。fps 必须对应当前 Maya 时间单位。位置、朝向及长度由模板明确使用宿主坐标，不对任意 Maya 轴、旋转顺序或单位做隐式猜测。

Maya 客户端／面板／其 FBX 插件尚未实测，不能从 Python 数据测试或 Blender 导出推导 Maya 可用。当前实际 DCC 运输验收使用本机 Blender 创建原生自定义属性关键帧，再导出含字符串属性的 FBX，随后通过 Unity 的真实导入回调与 Player 消费。完整 Maya 制作体验、生产 rig 和其他 DCC 变体仍待验收。

桌面验收实际绘制了合成面部、贴花、材质图集和定位器上的 Prefab，并分别关闭各项确认其可见贡献。CPU／GPU、重复 seek、独立 FBX／sidecar owner 和等价类型化输入采用整幅浮点 RGBA 相等判据；另以独立数值检查曲线插值、归一化／百分比权重、区间边界与恢复。自制三角形探针证明数据链，不代表完整角色或制作素材的外观验收。

此模块不接管默认 Photo Studio 动作／故事时钟，不声称已覆盖所有角色、制作特效或移动性能。
