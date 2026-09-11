# 冻结基线与整理范围

以下恢复记录是发布整理时的历史事实。后续获准进行的角色渲染开发由 `config/runtime-revisions.json` 记录：每个修改文件同时保存历史哈希和当前哈希，新增源文件单独列出；`config/source-baseline.json` 不覆盖。验证器检查历史基线加显式增量，未登记的 shader include 也会报错。当前版本因此不再宣称与整理前所有 Unity 文件完全相同。

目标为 2026-09-08 21:16（Asia/Shanghai），即第一次发布整理请求开始前。该时刻没有 Photo Studio Git commit；父工作区仅有更早且无关的历史。

依据为整理时生成的旧 Unity 源码归档，SHA-256：

`597c4a95781d4a2394a81be85826a2de2a58f547d9449e04c648426f061d5cfb`

归档内 177 项哈希已逐项验证。关联任务记录显示修改发生在新 Photo Studio 目录，旧运行时没有修改后再归档；旧文件时间早于目标时刻。这是整理前基线证据，不是虚构的 Git 提交。

## 恢复

恢复运行时 C#、shader、兼容类型、元数据、PhotoMode 场景、包定义和项目设置。仅保留构建所需的两个旧 Editor 文件，提取/采集探针及用户偏好留在私人归档。

移除周三新增的 PhotoStudioTool、AnimationSceneDocument、AssetCatalogRules、PhotoModeApp.ToolApi、LocalAssetPaths、ToolContractTests 及新场景，恢复曾被改写/删除的旧源码。回退前的源码、Git 索引和配置已备份在仓库外，旧 Player 也已单独存档。

## 首次发布整理边界（历史记录）

仅改文档、原创格式示例、发布工具、测试和允许列表。所有恢复的 Unity 源码保留原文，不改默认值、函数签名、执行顺序、物理、骨骼、资源选择或渲染。

发布准备新增根目录 `studio.py`，负责本地路径配置、输入文件检查以及调用已有构建/启动接口；它不编译进 Player，不变更冻结的 150 个 Unity 文件。

两处旧资产默认路径不含用户名、凭据或素材，保留以维持 C# 完全一致。审计只在这两个完整文件与冻结哈希一致时豁免 Windows 路径规则，不豁免秘密或新增路径。公开用户用已有环境变量覆盖。

config/source-baseline.json 固定公开 Unity 文件的内容哈希；verify_baseline.py 只容许 CRLF/LF 差异，并拒绝额外 C#/shader/asmdef。更新清单表示显式推进基线，不能掩盖重构。

源码一致与视觉一致分别验收。本记录不证明没有原有缺陷，也不证明完整来源及授权已经确认。

## 工具包 / 应用分离

本次重构以 Git `c2b3ba4` 为直接父版本，不重新回退已完成的渲染开发。核心移入 `packages/com.digital-kotone.toolkit`；摄影应用移入 `unity/Assets/Applications/PhotoStudio`。运行组件与 shader 的 GUID 保留，兼容程序集名称保留，PhotoModeApp 改为继承 CharacterSceneRuntime。

`runtime-revisions.json` 的 `relocations` 记录旧路径到新路径；内容修订仍校验原始父哈希，不改写 `source-baseline.json`。检查器同时扫描 Unity Assets 与核心包，拒绝未登记源码和碰撞/越界搬迁。公开允许列表仍默认拒绝未知文件与私人资产。

拆出的边界为窗口/画质策略、UI/快捷键、启动参数和诊断；核心新增显式初始化及选择接口，应用继续走原选择、播放和渲染流程。验证分别覆盖原/新 Player 的合成 GPU 测试、固定相机缓冲、摄影/剧情、暂停旋转/恢复，以及只有核心包的独立宿主工程。接入方式见 [工具库指南](toolkit.md)；不把构建成功替代为全功能视觉一致的保证。

独立 Development Player 检查暴露了旧 ActorRenderControls 在 MonoBehaviour 字段初始化期间创建 Unity 原生 MaterialPropertyBlock 的问题；其分配移到首次 OnEnable，仍复用同一个块，不更改输入、默认值或着色计算。原 Built-in/URP 构建包装抽成工具包的 Editor 帮助类，摄影构建器仍调用同一逻辑。

### 本轮验证范围

在 Tuanjie 2022.3.62t15、Windows / D3D11 上比较重构前保留的 Player 与重构后的实际 Editor 构建：

- `--self-test-actor-rendering`：前后均通过原有 1517 项渲染/动态/绑定检查，生成的 1267 张合成图逐张相同。
- 固定相机与固定几何输入：ActorData、当前 HDR、时间累积、模糊、Bloom 五个缓冲及最终 PNG 的前后比较相同。重构后另一重复运行的时间累积缓冲有 1 个半精度通道相差 0.001953125，其他缓冲与最终 PNG 相同；未放宽阈值或隐藏该差异。
- 摄影/剧情原命令正常完成；55 帧实景探针中的六组状态恢复均为零变化，暂停旋转的 11 个比较帧保持姿态，恢复后动画继续。自由运行的动作时钟与动态模拟并未锁帧，因此不宣称这些跨进程动态图逐像素相同。
- 独立宿主仅引用工具包并使用公开 Minimal Character Host 源码；Development Player 通过 23 项初始化、目录/选择、动作暂停/采样/恢复、剧情跳转、表情、渲染及关闭检查，生成 1280×720 非黑角色图，无剩余错误材质；未加载 `Gakumas.PhotoStudio`，窗口/帧率/画质策略保留宿主设置。
- `python -I -m unittest discover -s tests` 覆盖 103 项发布边界、源码锁、目录搬迁、文档/示例、启动器和工具包依赖契约；`tools/verify_baseline.py` 与 `tools/release.py audit` 检查通过。运行图与真实资产仍为私人验证材料，不发布。
