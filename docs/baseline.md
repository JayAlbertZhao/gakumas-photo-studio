# 冻结基线与整理范围

以下恢复记录是发布整理时的历史事实。后续获准进行的角色渲染开发由 `config/runtime-revisions.json` 记录：每个修改文件同时保存历史哈希和当前哈希，新增源文件单独列出；`config/source-baseline.json` 不覆盖。验证器检查历史基线加显式增量，未登记的 shader include 也会报错。当前版本因此不再宣称与整理前所有 Unity 文件完全相同。

目标为 2026-09-08 21:16（Asia/Shanghai），即第一次发布整理请求开始前。该时刻没有 Photo Studio Git commit；父工作区仅有更早且无关的历史。

依据为整理时生成的旧 Unity 源码归档，SHA-256：

`597c4a95781d4a2394a81be85826a2de2a58f547d9449e04c648426f061d5cfb`

归档内 177 项哈希已逐项验证。关联任务记录显示修改发生在新 Photo Studio 目录，旧运行时没有修改后再归档；旧文件时间早于目标时刻。这是整理前基线证据，不是虚构的 Git 提交。

## 恢复

恢复运行时 C#、shader、兼容类型、元数据、PhotoMode 场景、包定义和项目设置。仅保留构建所需的两个旧 Editor 文件，提取/采集探针及用户偏好留在私人归档。

移除周三新增的 PhotoStudioTool、AnimationSceneDocument、AssetCatalogRules、PhotoModeApp.ToolApi、LocalAssetPaths、ToolContractTests 及新场景，恢复曾被改写/删除的旧源码。回退前的源码、Git 索引和配置已备份在仓库外，旧 Player 也已单独存档。

## 整理边界

仅改文档、原创格式示例、发布工具、测试和允许列表。所有恢复的 Unity 源码保留原文，不改默认值、函数签名、执行顺序、物理、骨骼、资源选择或渲染。

发布准备新增根目录 `studio.py`，负责本地路径配置、输入文件检查以及调用已有构建/启动接口；它不编译进 Player，不变更冻结的 150 个 Unity 文件。

两处旧资产默认路径不含用户名、凭据或素材，保留以维持 C# 完全一致。审计只在这两个完整文件与冻结哈希一致时豁免 Windows 路径规则，不豁免秘密或新增路径。公开用户用已有环境变量覆盖。

config/source-baseline.json 固定公开 Unity 文件的内容哈希；verify_baseline.py 只容许 CRLF/LF 差异，并拒绝额外 C#/shader/asmdef。更新清单表示显式推进基线，不能掩盖重构。

源码一致与视觉一致分别验收。本记录不证明没有原有缺陷，也不证明完整来源及授权已经确认。
