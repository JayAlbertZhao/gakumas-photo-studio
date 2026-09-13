# 可动画角色贴花

`FaceDecalRenderer` 提供独立、显式接收面的投影贴花；`FaceDecalLayer` 是可选的 Built-in 相机适配器。用途包括自制腮红、脸色变化、图案及其他角色表面装饰。没有组件或没有启用 `decalsEnabled` 时，不分配贴花资源、不提交绘制。既有 `FaceDecalRuntime`、默认角色 shader 和 CPU／GPU 面部形变路径保持原样。

设计对应 PPT 58、62 的参数动画及 PDF 29 的全尺寸角色贴花阶段。实现使用接收面重画，不依赖 URP Projector、不修改原版 shader，也不声称掌握讲演未公开的混合公式。图集、模型、动画由使用者提供并确认其授权。

## 相机接入

```csharp
using GakumasPhotoMode;
using UnityEngine;
using UnityEngine.Rendering;

// projectorObject 可挂在角色骨骼／自制定位器下。
var projector = projectorObject.AddComponent<FaceDecalProjector>();
projector.size = new Vector3(0.2f, 0.12f, 0.04f);
projector.edgeFeather = 0.1f;
projector.opacity = 0.6f;

var layer = camera.gameObject.AddComponent<FaceDecalLayer>();
layer.atlas = yourAtlas;
layer.projectors = new[] { projector }; // 数组顺序也是混合顺序
layer.receivers = new[] {
    new FaceDecalRenderer.Receiver {
        surface = new SceneDepthData.Surface {
            renderer = faceRenderer,
            materialIndex = 0,
            cull = CullMode.Back,
            vertexScale = Vector3.one,
            alphaMask = yourCutoutMask,
            alphaMaskST = new Vector4(1, 1, 0, 0),
            alphaCutoff = 0.5f
        }
    }
};
layer.decalsEnabled = true;
```

输入只接受显式的 MeshRenderer／SkinnedMeshRenderer 子网格。`vertexScale`、cutout UV／纹理／阈值及 cull 必须与原接收面的几何和裁剪方式一致；不猜测第三方材质的属性命名。不写深度的透明表面、任意 shader 顶点位移、不同投影矩阵或深度偏移不满足该契约。原接收面先绘制，贴花以 `ZTest Equal` 重画当前几何，保留目标 alpha、depth 和 stencil。可选 stencil 比较由宿主显式配置，贴花始终不写 stencil。

适配器在 `OnPreCull` 获取当前姿态并在 `BeforeForwardAlpha` 绘制，使用全尺寸相机附件，先于普通透明几何和后处理。适用于 Built-in、完整视口、非 XR／非 MSAA、有深度的固定尺寸目标。相机 cullingMask 过滤显式接收面；两个相机要各自配置。SRP 宿主可使用底层记录接口自行安排相同几何／深度／投影和绘制顺序，当前适配器不会自动接管 URP／HDRP。

蒙皮接收面使用引擎实际提交的顶点缓冲。宿主应在引擎蒙皮更新前完成骨动画，再提交匹配时刻的投影姿态；在同一帧多次手动改骨并调用 Camera.Render，不保证 Unity 重新生成蒙皮缓冲。本模块不强制 BakeMesh、不读回旧 CPU 顶点来代替实际几何。自检会先跨过引擎蒙皮更新边界，再对同一姿态的 CPU 参考与实际绘制做整图比较。

## 坐标与混合

投影体为 `[-0.5, 0.5]³`，XY 是图集画布，+Z 是投射方向。局部到世界矩阵依次为：父级矩阵、`TRS(positionOffset, Euler(rotationDegrees), scale)`、`Translate(pivot)`、`Scale(size)`。`pivot` 在尺寸缩放之前的局部坐标中；size 必须正，scale 可负但不能为零。父级可旋转、非均匀缩放或剪切，必须是可逆仿射矩阵。

`uvRotationDegrees` 围绕画布中心旋转，然后加 0.5，再应用 `uvScale`、`uvBias`。不创建隐式图集或改写纹理采样设置；wrap／filter／mip 由调用者纹理决定。`tint` 为显式线性 `Vector4`，不隐含 Inspector Color 的 sRGB 转换。

立方体外 coverage=0；内侧到最近边界的距离除以 edgeFeather 并钳位，feather=0 为硬边。几何法线与反投射方向的夹角在 `angleFadeStart` 与 `angleFadeEnd` 的余弦域线性衰减；两者相等为硬阈值，start=180 关闭角度限制。角度限制需要网格 NORMAL；启用 cutout 需要 UV0。双面绘制时按实际面朝向翻转法线。

每层 alpha=`saturate(texel.a) * tint.a * opacity * coverage`。`StraightAlpha` 的颜色为 `texel.rgb * tint.rgb`；独立 `AlphaModulated` 模式的颜色为 `lerp(1, texel.rgb * tint.rgb, saturate(texel.a))`。两种模式都按数组顺序进行预乘 over 合成，并保留目标 alpha；与旧全局贴花的最大值混合不互相替代。

## 动画与任意时间采样

`FaceDecalProjector` 的 opacity、UV、位置／旋转／尺寸／pivot、tint 与角度等为组件上的直接公开字段，可用 AnimationClip／Animator 动画，绑定名例如 `opacity`、`uvRotationDegrees`、`positionOffset.x`。宿主负责播放与采样时间；相机组件不推进动画时钟。

不使用 Unity 动画组件的宿主可保存 `FaceDecalAnimation` 的 JSON：

```csharp
var baseline = projector.Snapshot().pose;
var animation = new FaceDecalAnimation {
    duration = 2,
    wrap = FaceDecalWrap.PingPong,
    tracks = new[] {
        new FaceDecalAnimation.Track {
            channel = FaceDecalChannel.Opacity,
            keys = new[] {
                new FaceDecalAnimation.Key(0, 0),
                new FaceDecalAnimation.Key(1, 0.7f),
                new FaceDecalAnimation.Key(2, 0)
            }
        }
    }
};
if (animation.TrySample(baseline, timelineSeconds, out var pose, out var error))
    projector.TryApplyPose(pose, out error);
```

最多 28 条不重复的通道、总计 8192 个 key；key 时间必须严格递增且位于 `[0, duration]`。支持 Hold／Linear／Hermite（左右切线单位为数值／秒）；左 key 决定所在区间的插值。首 key 之前和末 key 之后保持端值。Clamp 保留终点；Loop 在 duration 回到起点，负时间正向取模；PingPong 在 duration 保留终点、2×duration 回到起点。无内部随机或累积状态，同一输入／时间重复采样得到相同结果。

所有通道采样后一次性校验完整结果。非法尺寸、非有限数或越界曲线过冲会返回 false／null，而非截断或部分修改 baseline。`TryApplyPose` 同样先校验再原子赋值。JSON 为本项目独立字段格式，不是原版 Maya 导出格式；没有附带 Maya 插件、Timeline 轨或第三方资产转换器。

## 底层与 Planar

无组件的宿主可创建 `FaceDecalRenderer`，每帧在动画之后调用 `TryPrepare(atlas, FaceDecalProjector.Data[], Receiver[])`，再调用 `Record(commandBuffer)`。每组最多 8 个共享图集的投影和 128 个接收子网格；每个当前接收面拥有一个材质、一次绘制，fragment 最多计算 8 层。资源／提交次数不等于 GPU 帧时测量。

宿主必须在重新 Prepare／Clear／Dispose 前清空并停止使用旧 command buffer。`Draws` 和 `PlanarDraws()` 中的材质、接收快照是借用对象，只活到下一次 Prepare／Clear／Dispose；不要缓存到下一帧，也不要当成自己的材质销毁。传入姿态会被打包为自有材质常量，接收描述会拷贝，不修改源材质、源 property block 或全局 shader 参数。

`PlanarDraws()` 返回颜色 pass 和不写覆盖率的 pass；每帧在 `ActorPlanarCaptureSet.Draws` **之后**追加，再让 Planar 绘制。这保持原角色 alpha／独立覆盖率，不把贴花体积误当作额外轮廓。仍需显式匹配 Planar 原接收面的顶点缩放、cutout 和 stencil。

禁用、空输入或全零 opacity／tint alpha 不保留材质与绘制；无效输入／失效纹理会清除本帧输出并给出 `UnavailableReason`，不能沿用上一帧贴花。纹理需为当前可采样非 MSAA 的 2D 颜色纹理，单边不超过 16384；不会擅自修改或销毁调用者图集。`Dispose` 可重复调用，之后 Prepare 被拒绝。

DrawRenderer 会继承接收面的 property block，因此 `_AuthoredDecal*` 是保留属性；有效 block 中如有同名覆盖，Prepare／Record 会明确拒绝，不覆盖用户数据。材质槽 block 优先于 renderer block，其他属性保持原样。网格顶点／法线本身的有限性及正确几何约定由调用者保证；运行时不会读回 GPU 顶点来检查它们。

## 验收边界

桌面 D3D11 Player 的完整角色／场景合成自检包含 175 项新增贴花控制：八组完整 113×79 RGBA 图像的投影与混合、实际 AnimationClip、28 条通道、生命周期、裁剪／遮挡／stencil、移动蒙皮及 GPU-only 几何。原生捕获独立重建全部 71,416 个像素，最大误差约 0.000105，alpha 和原生 depth／stencil 字节不变；使用预先固定的最大 0.0005、平均 0.00002 门槛。

本地一个 5,206 顶点／108 形状角色的所有单独形状、五个代表视角及 Planar 共执行 1,129 项控制，验证七个显式写深度子网格、贴花实际出现、CPU／GPU 完整图像和关闭后恢复。Planar 贴花改变颜色而不增加覆盖率。此结果未公开本地资产或其画面。

使用资产无关的 `--self-test-actor-rendering` 可复跑完整相机、动画、生命周期和变形接收面检查。持有本地角色资产时，`--photo-mode --validate-face-decal-character <output-directory>` 显式执行所有单独形状、CPU／GPU、代表视角与 Planar 对照，输出日志、JSON 和预览；正常启动不进入该路径。

预览 PNG 只用于查看，数值比较使用完整线性 HDR RGBA，没有遮罩、离群像素剔除或容差随结果调整。一个本地角色的全形状不代表所有角色、服装和制作动作已经一致。Maya 导入、MotionEffect Prefab／粒子种子／逐顶点定位器属于后续独立条目；移动端驱动、GPU 帧时及全舞台覆盖也仍需实测。
