# 源码发布检查单

公开根必须是独立 photo-studio 仓库，不发布父工作区 Git 历史、私人归档或本地 Player。

1. 审阅 public-files.json 每个路径及内容。未知文件默认不进入 Git 或 ZIP。
2. 运行 `python -I tools/release.py sync-ignore`。
3. 运行 `python -I -m unittest discover -s tests -v` 和 `python -I tools/verify_baseline.py`。
4. 运行 `python -I tools/release.py audit`，检查工作树。
5. 仅暂存审阅的文件和删除，再运行 `python -I tools/release.py audit --tracked`。索引多余/缺少文件、秘密及工作树差异均会阻止通过。
6. 设置 `git config core.hooksPath .githooks`。CI 重跑基线和边界审计，不仅依赖本地 hook。
7. 运行 `python -I tools/release.py pack --tracked --output ../photo-studio-source.zip`，拒绝覆盖已有输出。
8. 干净解包，核对哈希、构建和所宣称的功能；维护者再决定 commit、tag 与推送。

## 素材和来源

不提交 bundle、模型、贴图、声音、原始动画、官方台本、游戏 DLL、原始 shader 字节码、反编译文本、截帧、录像、提取器、针对原游戏进程的探针、密钥和个人配置。只运行本项目的渲染检查代码可经逐文件审阅进入允许列表；其输出始终排除。

PhotoMode 场景及两个 URP 资产是本项目自建的挂载/渲染配置，按精确路径及内容哈希允许；不允许任意 .asset 或 .unity 混入。

兼容命名、观察数值和重建算法仍需来源审阅。完整逐文件授权结论和许可证选择未完成；确认贡献归属、派生边界和适用许可后再做公开授权声明，扫描无法代替这些事项。

## 人工验收关卡

- 同资产、同动作、同相机、同帧时序检查头颈/蒙皮、毛发和动作过渡。
- 不带私人数据的干净克隆构建，不得有隐藏编译依赖。
- README 不把未来手 K、原始脚本导入和通用动画导出写成已完成功能。
