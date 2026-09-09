# 角色渲染与验证

普通摄影模式使用 Built-in 管线中的复现 shader，不要求运行原版 shader。模型、贴图和数据由使用者自行准备；本仓库不提供它们。

## 渲染控制

启动摄影模式后按 **F8** 打开角色渲染面板。它独立于动作、面部和物理调度。

- 描边：使用模型切线中的平滑法线及打包顶点色中的宽度、颜色、深度偏移；保留材质禁用描边的标记。
- 眼部前发覆盖：只在眼白/眉眼的 stencil 区域补画头发。Base 的 A 是视角淡出遮罩，配合材质 `_FadeParam` 与动画头部方向控制透明度，并非直接当作 opacity。其他区域保留不透明头发的深度与混合规则。
- Override story lighting：临时覆盖光照；关闭后恢复剧情/摄影参数。可调相机相对或世界空间主光、明暗边界、阴影颜色混合强度、光滑度、环境光和附加光强度。
- Toggle two test lights：添加/移除两盏演示点光源。Unity 场景中的点光和聚光会被角色采样，最多取距离角色最近的 8 盏；不包含灯光 cookie 或附加灯阴影。

Inspector 中 `ActorRenderControls` 还提供有色主光、阴影乘色/加色、有色边缘光、附加镜面光强度和描边宽度。描边默认宽度是摄影棚设置，不宣称适用于原游戏所有镜头。

材质的 Layer 使用第二套 UV：贴图左半部控制基础色，右半部控制 Definition；同一个权重同时作用于两部分。Definition 的 G 为光滑度，B 为金属度（面部用作三角区遮罩），A 为高光强度。禁用 Definition 贴图时使用材质常量。

剧情背景的 `actorProfile` 中的 `matCapOffset`、`matCapSmoothScale`、`shadeApplyRatio`、`giScale`、`additiveLightScale`、`additiveLightSpecularScale`、`eyeHighlightColor` 已连接至复现 shader；阴影加色和边缘光 RGB 不再被丢弃。

## 可重复检查

先完成常规配置和构建。在已设置 `GAKUMAS_PHOTO_STAGING` 的终端运行：

```powershell
& .\unity\output\KotonePhotoStudio.exe --photo-mode --hide-ui --validate-actor-rendering .\LocalAssets\render-check
```

探针保存近景正面/侧面、描边与前发覆盖开关、明暗边界、世界空间光照、点光/聚光、Layer、环境光、有色边缘光以及恢复动画后的序列帧，并输出 `rendering-probes.json`。需要自己的兼容数据；不是无资产测试，也不是逐骨骼关键帧编辑器或正式导出器。

检查实际图像和 pass 数，不能仅以进程退出码或“画面不黑”验收。眼部遮挡还应选一个睁眼表情，检查侧视时眼睛、高光与前发的关系。不同机器上的动画时刻、动态解算和时域累积不保证逐像素确定性。

新增 pass 使用独立的 `ActorSupplemental.shader`，由相机在透明物体之前按“描边 → 眼部前发覆盖”执行。不对每个身体/眼睛材质重复执行头发 pass。这个拆分也避免已安装 URP 的构建裁剪器删除 Built-in shader 中的自定义 LightMode。

## 原版 shader 研究入口的限制

`--original-shader --original-urp` 仅用于本地诊断，不是面向用户的替代渲染器。专用构建入口为 `GakumasPhotoMode.Editor.PhotoStudioBuilder.BuildOriginalShaderReferencePlayer`，输出到 `unity/output/OriginalShaderReference/`，不覆盖普通 Player。

研究构建会启用 URP 以保留其 shader，调度自定义 `VLActor` pass，并跳过 Built-in 的 `OnRenderImage` 超采样显示链。仅把 URP 配置放在 Resources 中、然后在 Built-in 构建运行时切换管线，并不足以得到有效对照。

当前本地 Windows bundle 的 Actor pass 仍出现错误 shader；RenderDoc 已确认，不能把这个入口描述为成功的原版画面重放。需要进一步解决 bundle/引擎/变体的兼容性。普通复现 shader 不依赖该入口。原版截帧、字节码及研究产物不得加入公开仓库。

## 参考与范围

- [截帧分析](https://zhuanlan.zhihu.com/p/1898275837159126787)：管线拆解方法。
- [Shader 逆向](https://zhuanlan.zhihu.com/p/1901300706872394096)：变量、变体和 GPU 调试方法。
- [角色渲染](https://zhuanlan.zhihu.com/p/1908718263602489063)：角色贴图、光照、前发覆盖与描边。
- [作者公开仓库](https://github.com/Yu-ki016/Yu-ki016-Articles)：参考工程与文章；未复制其示例工程、源码或资产。

本实现继续使用既有角色阴影与后处理，并保留此前验证的材质差异。新增功能不等于完整 Live 管线、原游戏逐像素一致、全角色兼容或完整产品验收。
