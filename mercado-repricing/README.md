# 美客多调价工作台 / Mercado Repricing

当前源码版本：**0.5.38**。Windows 本机网页工具，使用 React、ASP.NET Core 和 SQLite，通过已登录的紫鸟客户端读取及操作 Mercado Livre 巴西站页面。

## 功能与运行边界

- 数据看板：UpSeller 所选店铺实时查询、昨日销售、本月 GMV/目标、七日折线、类目环图、预估毛利与 SKU 销售排行。使用本机每日推送数据源，经营数据与登录文件不随源码分发，见 [数据看板使用说明](docs/数据看板使用说明.md)。
- 日常调价：导入 XLSX / CSV，核对所选店铺，按授权执行活动降价、提价、收益比较及原价调整。表格目标价保持不变。
- 周末活动报名：自动匹配北京时间本周五至周日日期，展开对应 SKU 的更多活动，只报名指定周末普通活动；不修改原价或退出其他活动。
- 同一精确 SKU 对应多个商品 ID 时，拆为独立执行记录，分别保存并回读；搜索结果未完整返回、身份不明或共享价格目标冲突时保留待核对。不能仅凭一个 SKU 成功就推断所有链接都已成功。
- 页面连接故障与店铺验证分开处理：未确认验证页时按连接故障暂停，不弹要求店铺验证的窗口；继续前建立新的专用连接并先回读。
- 验证等待与提交检查点：验证中暂停业务操作，保存结果未知先回读。周末报名仅在两次独立读取确认尚未报名、状态不变后恢复同一计划。
- 任务记录、原表与完整日志保存在本机；支持执行明细搜索和筛选。
- 产品标签与箱唛裁切、预览。标签功能另需本机组件，见下方说明。

这是源码备份，**不包含云端部署或店铺登录凭据**。运行实际调价仍须核对紫鸟配置与实际卖家、表格目标及操作权限。不要同时手工或由两个任务操作同一店铺。报名成功可能是已预约，不等于当前成交价已切换。

## 开发环境与启动

需要 Windows、匹配 `global.json` 的 .NET 10 SDK、Node.js / npm、Chrome，以及业务操作所需的紫鸟客户端。首次克隆还需准备本机标签引擎：

1. 将已获授权的 `PDFLabelCropper.exe` 放入 `server/tools/labels/`。该用户提供的程序不随公开源码上传。
2. 按 `tools/carton/requirements-build.txt` 安装 Python 构建依赖，执行 `tools/carton/build.ps1` 生成 `CartonLabelCropper.exe`。
3. 完整构建和标签测试要求两个引擎均已准备好；只有源码快照不能直接生成完整便携包。

在本项目目录运行：

```powershell
npm.cmd --prefix web ci
npm.cmd --prefix web run build -- --configLoader runner
dotnet restore server/Repricing.Api.csproj
dotnet run --project server/Repricing.Api.csproj
```

本机地址为 `http://127.0.0.1:5188`。本机标签组件准备完毕、SDK 可用后，可运行 `scripts/check.ps1` 执行后端、前端及合成浏览器回归；`scripts/publish.ps1` 检查通过后生成 Windows 自包含包。发布脚本核对组件哈希，重建组件后须重新验收，不能绕过检查。

`启动调价程序.cmd` / `scripts/start.ps1` 用于运行已在 `dist/` 生成的便携程序；首次克隆不能直接使用这两个入口。构建辅助脚本会调整当前进程的开发缓存目录，生产便携程序应从独立的新进程启动。

## 文档

- [日常调价](docs/混合调价使用说明.md)
- [收益比较规则](docs/补贴活动收益优先.md)
- [周末报名与验证日志](docs/周末活动报名与验证日志.md)
- [标签剪裁](docs/标签剪裁使用说明.md) / [箱唛裁切与预览](docs/箱唛裁切与标签预览.md)
- [同事使用说明](docs/同事使用说明.txt)
- [GitHub 同步规则](docs/GITHUB_SYNC.md)

文档中的早期版本号为历史功能记录；当前源码版本以项目文件和本 README 为准。此前本机 0.5.25 发布检查通过 319 项后端、15 项前端及 19 组浏览器回归；这不表示另一台电脑或真实店铺无需重新验证。

## 源码同步

目标：`sekaowa4/ecom-automat-hub` 的 `main` 分支，项目目录 `mercado-repricing/`。后续已完成的修改经相关检查及公开范围审查后同步，保留仓库其他项目和他人的提交，不强制推送。

运行 `python scripts/prepare-github-snapshot.py` 生成白名单快照及 SHA256 清单，再审查并上传。快照工具如发现旧快照会停止，避免把旧文件混入新版本；重做快照前应检查并归档旧目录。详细步骤见 `docs/GITHUB_SYNC.md` 和 `AGENTS.md`。

不上传：实际运营表格、任务数据库、Cookie / Token、浏览器会话、凭据、日志、现场截图、实测报告、依赖环境、安装包及用户提供的 EXE。`tests/fixtures/labels/` 中 PDF 为合成测试样例，`samples/` 包含空白模板及 DEMO 示例。
