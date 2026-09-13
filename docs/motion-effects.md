# 显式 MotionEffect 预制体

`MotionEffectSequence` 是不依赖资源目录、剧情格式或摄影 UI 的独立预制体播放器，对应 PPT 59–60 的实例、父挂点、种子、位置及旋转输入。已有 `ActorMaterialEffectRuntime` 继续负责原来的材质替换和图集动画，本模块不替换它。宿主可把同一动作时间分别传给两个模块。

```csharp
using GakumasPhotoMode;
using UnityEngine;

var effects = new MotionEffectSequence();
bool bound = effects.TryBind(new[] {
    new MotionEffectSequence.Entry {
        id = "left-tear",
        prefab = myOwnParticlePrefab,
        attachment = leftEyeLocator,
        startSeconds = 0.25,
        durationSeconds = 2,
        seed = 713,
        positionOffset = new Vector3(0, -0.01f, 0),
        rotationDegrees = Vector3.zero,
        scale = Vector3.one
    }
}, durationSeconds: 3, repeat: true);
if (!bound) Debug.LogError(effects.LastError);

// 先更新角色姿态／挂点，再显式采样。相同时间保留同一结果，不读 Unity 时间。
if (!effects.TrySample(0.75)) Debug.LogError(effects.LastError);
effects.Stop();    // 立即隐藏并销毁实例，保留绑定，可重新 seek。
effects.Clear();   // 同时解除绑定。
effects.Dispose();// 永久结束这个 owner，不能重新绑定。
```

模板和挂点由宿主借出，entries 的数值在 Bind 时复制。宿主可独立更新挂点的 Transform；不会自动搜索骨骼名、创建顶点定位器或猜测左右眼。实例根变换为 `attachment.localToWorldMatrix * TRS(positionOffset, Euler(rotationDegrees), scale)`，模板根的原始 TRS 不参与，内部子节点 TRS 保留。挂点不能位于模板内部。

绑定期间保持模板、借出的 Mesh 和 Material 不变；编辑模板后重新 TryBind，已有克隆不会自动同步合法参数修改。当前资源检查能拦截模板销毁或切换到不支持的模拟方式，但不充当编辑器实时同步器。依赖外部时间／全局参数的自定义 shader 仍由宿主负责，粒子时钟不会接管 shader 的时间。

`GetInstance(id)` 返回只读借用的实例，便于宿主登记渲染对象。不要更改其组件／粒子参数或把外来物体放入它的层级。实例外部销毁后，下次有效采样会重新创建。没有在 Photo Studio 的默认初始化路径创建本模块，也没有隐式 Update、默认特效或共享材质写入。

## 时间与粒子契约

效果区间为 `[startSeconds, startSeconds + durationSeconds)`，结束边界立即移除。负时间不播放；repeat 只对非负时间按整个序列长度取模。重叠 entry 各自拥有实例，无权重混合。暂停采样会保持最后姿态，不继续推进粒子，Stop 才会移除。

每次采样从零重建粒子，使用显式 1/60 秒步长和最后的余数，逐系统调用 `Simulate(..., withChildren:false, fixedTimeStep:false)`，避免子节点重复模拟和项目 `Time.fixedDeltaTime` 改变步长。Unity 的 Simulate 会在推进后暂停系统，见 [Unity Simulate API](https://docs.unity3d.com/2022.3/Documentation/ScriptReference/ParticleSystem.Simulate.html)。本模块只修改克隆体的 playOnAwake、stopAction、cullingMode、autoRandomSeed 和 seed，不改模板或全局随机状态。

`SystemSeed(seed, hierarchyOrdinal)` 用确定的 32 位整数散列分配每个系统的非零种子。序号是模板深度优先层级中的 ParticleSystem 顺序，不依赖运行时 instance ID 或 entry 排序。同模板、种子、时间及挂点姿态，在已验证的同一桌面引擎环境内可重放；这不承诺 Unity 各版本或各平台的粒子算法逐位一致。

当前接受只有 Transform、MeshFilter、MeshRenderer、ParticleSystemRenderer 和 ParticleSystem 的被动模板，根可以是不激活的场景对象或 prefab 资产。子节点激活状态保留。粒子必须用 Local simulation space，不使用 prewarm 或重力。涉及历史／外部世界的 world-space 粒子、距离发射、world-space trails、碰撞、trigger、外部力、继承速度、subemitters、世界速度／力和动态网格发射会拒绝。自定义脚本和 Animator 也不会在模板实例化时偷偷执行。对这些功能，需要先接入完整历史或显式采样适配器，不能用当前挂点静态重放冒充历史运动。

资源边界：最多 32 entries，每个模板 16 个粒子系统、65536 的 maxParticles 总和；单效果时长 `(0,60]` 秒，序列 `(0,3600]` 秒。每次采样最多 60000 次系统步进，超预算会在实例化前拒绝，不截断时间。外部时间必须有限且绝对值不超过 `1e12`。长时间、多系统实时播放的成本仍需优化，此接口优先保证可复现 seek，不宣称更快。

错误输入或当前模板／挂点失效时，Try 方法返回 false、设置 LastError 并清除所有现存实例，不留下上一帧特效。无效 Bind 还解除旧绑定。挂点 inactive 时该效果不活动，重新 active 可再次采样。Destroy 在 Player 结束当前帧时回收，但对象在调用时已 SetActive(false)，不会多画一帧。模板和共享材质始终归宿主所有。

## Playable 接入

最小宿主可用 `MotionEffectGraph`，它创建独立的手动 graph，内部使用同一个 PlayableBehaviour：

```csharp
using (var player = new MotionEffectGraph()) {
    // player.Sequence.TryBind(entries, durationSeconds, repeat);
    player.TrySample(0.75);
    player.Stop(); // 立即隐藏，绑定保留。
} // Dispose 先立即隐藏并释放 owner，再安排销毁 graph。
```

已有自己的 graph 时，可以直接接入行为：

```csharp
using UnityEngine.Playables;

var graph = PlayableGraph.Create("authored effects");
graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
var playable = ScriptPlayable<MotionEffectPlayable>.Create(graph);
var output = ScriptPlayableOutput.Create(graph, "effects");
output.SetSourcePlayable(playable);
// 在 GetBehaviour() 的独立 Sequence 上 TryBind 自己的 entries。
var owner = playable.GetBehaviour().Sequence;
playable.SetTime(0.75);
graph.Evaluate(0); // PrepareFrame 从 playable.GetTime() 采样。
owner.Dispose();  // 同帧停止显示，不释放模板／挂点。
graph.Destroy();
```

`LastSampleAccepted` 和 `Sequence.LastError` 用于观察实际采样。克隆 PlayableBehaviour 不共享 owner；clone 需要单独绑定。OnGraphStop 会 Stop，OnPlayableDestroy 会 Dispose。[Unity Graph.Destroy](https://docs.unity3d.com/cn/6000.0/ScriptReference/Playables.PlayableGraph.Destroy.html) 的实际销毁在本帧稍后发生，直接接入时若需调用处立即移除，先显式 Dispose 这个 Sequence；`MotionEffectGraph` 已封装此顺序。手动 Evaluate 可用于暂停后的拖动；真正停止后若要恢复，需再次评估时间。宿主负责把角色动画／定位器更新排在效果采样之前。本模块不注册 Timeline 自定义轨道、不修改 Animator graph，也没有 Maya 导入器。

## 验收边界

Player 自检使用自制粒子、完整浮点 Camera.Render，检查真实粒子年龄／位置、seek 和独立 owner 的整图结果、正向可见覆盖、TRS、图生命周期、无效输入和模板不变性。本阶段在团结 2022.3.62t15／D3D11 执行 98 项新增检查，包含完整 113×79 RGBA 的精确重放与全部子系统粒子状态对照。完整自检中的 7088 项旧记录和 1412 张旧 PNG 逐字节不变，普通角色摄影与原有 Planar 检查保留。数值位置／TRS 门限为 0.00002，未使用遮罩或百分位排除失败像素。

当前 CPU／GPU 面部几何可通过 [VertexLocatorRig](vertex-locators.md) 提供逐顶点定位器／三角形挂点，宿主须在效果采样之前显式更新。普通 SkinnedMeshRenderer 等其他当前几何 provider、Maya 自定义属性导入、完整制作动作和真实汗／泪资产对照仍待后续阶段。PDF 29 的全分辨率透明与 PDF 31 的低分辨率透明由渲染宿主决定；此生命周期模块保留 prefab 原来的 layer／材质，不把所有粒子强行放入同一个 pass。不附带原版 prefab、粒子资产或源码。移动端帧时与全部 A/E/P/O/C 技术清单尚未完成。
