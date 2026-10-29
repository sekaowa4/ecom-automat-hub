# 项目索引 / Project Index

本页记录 **GitHub 中实际存在** 的自动化项目，方便在手机 ChatGPT、电脑 Codex 和 GitHub 之间定位。详情请以各项目最新 README、AGENTS.md 和源码为准。

## 已收录项目（4）

### 1. Brazil Stock Sync — 巴西库存同步

- **目录：** [`brazil-stock-sync/`](../brazil-stock-sync/)
- **业务场景：** UpSeller 数据导出、巴西 Stock_Order 库存更新、企业微信表格写回和校验。
- **主要入口：** `server.py`（本地工作台）、`daily_update.py`（库存更新相关逻辑）、`inventory_pipeline.py`（数据处理）、`wecom_sync.py`（企微表格同步）。
- **具体操作：** 使用六份 UpSeller 销售 / 库存源数据，先校验表头、SKU、店铺及在线目标位置，再写回并回读核验；只有确认成功后才发送群通知。
- **本机条件：** 需自行配置 UpSeller Cookie、企微授权、Webhook、最新 Stock_Order 模板以及依赖环境；云端副本不包含这些信息。
- **测试说明：** 仓库包含 `tests/`；README 记录两项已知历史预检失败，某些集成测试还依赖未上传的本机文件。**不可宣称完整测试通过**。
- **维护文档：** [README](../brazil-stock-sync/README.md) · [AGENTS](../brazil-stock-sync/AGENTS.md) · [GitHub 同步步骤](../brazil-stock-sync/docs/GITHUB_SYNC.md)
- **状态标注：** 已备份脱敏源码；云端未部署。实际业务运行状态须以本机环境核验。

### 2. Mercado Livre Auth Mailer — 美客多授权码自动发送

- **目录：** [`mercado-livre-auth-mailer/`](../mercado-livre-auth-mailer/)
- **业务场景：** 紫鸟企业账号及 Mercado Livre 子账号授权码采集。
- **主要入口：** `shop_auth_codes/`（主程序）、`run_daily.ps1`（日常启动脚本）、`collect_six_codes.ps1`（仅采集）、`config.example.toml`（配置示例）。
- **具体操作：** 通过已登录的紫鸟客户端采集六个店铺授权码；完整批次校验后，通过 SMTP 发信并使用 IMAP 核验状态。
- **本机条件：** Windows、已登录的紫鸟客户端、本机邮箱授权码和真实 `config.toml`。实际凭据、会话和运行数据不上传。
- **测试说明：** 仓库包含 `tests/`，按 [README](../mercado-livre-auth-mailer/README.md) 的步骤在合适的本机环境进行；本次文档整理未运行测试或业务发送。
- **维护文档：** [README](../mercado-livre-auth-mailer/README.md) · [AGENTS](../mercado-livre-auth-mailer/AGENTS.md)
- **状态标注：** 源码已入库；GitHub 并不会代替 Windows 本机运行或自动发信。

### 3. OMS Sea Workbench — 海运工作台

- **目录：** [`oms-sea-workbench/`](../oms-sea-workbench/)
- **业务场景：** UpSeller 与 OMS 海运入库、手工订单、税务信息、发票文件及沟通附件。
- **主要入口：** `启动海运工作台.cmd`，本机 http://127.0.0.1:8765；`web/` 为界面，`webapp/` 为服务与浏览器流程。
- **本机条件：** Windows、Python 3.12+、Node.js 22.12+、Chrome；填写私有 local-config.json，并选择已登录的专用浏览器标签页。真实清单、客户资料、发票、进度和历史单号不公开，换电脑需私下迁移。
- **安全边界：** 数量 K、单价 AA；OMS 只改 D:G；NCM 取商品导出 AG；提交、发货、开票及发送附件分别确认。保存结果不明先核对，避免重复建单。
- **测试方式：** `python -m unittest discover -s tests`、`npm.cmd run test:browser`、`npm.cmd run build`；可选 UI 验收需本机文件配置。真实页面需另外联调，不因代码上传而宣称生产流程通过。
- **维护文档：** [README](../oms-sea-workbench/README.md) · [AGENTS](../oms-sea-workbench/AGENTS.md)
- **状态标注：** 脱敏源码已入库，本机网页工具，未部署为云端业务服务。

### 4. Mercado Repricing — 美客多调价工作台

- **目录：** [`mercado-repricing/`](../mercado-repricing/)。当前源码版本 0.5.50。
- **业务场景：** 紫鸟中 Mercado Livre 巴西站日常调价、周末普通活动报名、商品链接逐项核验、标签裁切及手动刷新的 UpSeller 销售与 Full 订单看板（内置 SKU 批次映射）。
- **主要入口：** ASP.NET Core 后端 `server/`、React 前端 `web/`；构建后的本机地址为 http://127.0.0.1:5188。
- **本机条件：** Windows、.NET 10 SDK、Node/npm、Chrome、已登录紫鸟；完整构建、标签测试及打包还需本机授权标签引擎和自行构建的箱唛引擎。源码不含这两个 EXE、实际价格表、任务数据库、日志、浏览器会话或凭据。
- **执行边界：** 精确 SKU 多商品按 ID 分开执行与回读；周末只报名指定日期活动，不改原价或退出其他活动；保存不明先核验。未知身份、共享目标冲突和平台价限不能伪报成功。
- **测试说明：** 0.5.50 本机回归通过 387 项后端、18 项前端和 20 组浏览器场景；本次白名单快照排除运行数据库、日志、浏览器数据、凭据、本机工作簿和同事压缩包。自动化测试不替代生产页面核验；同事压缩包中的本地连接文件不进入 GitHub。
- **维护文档：** [README](../mercado-repricing/README.md) · [AGENTS](../mercado-repricing/AGENTS.md) · [GitHub 同步约定](../mercado-repricing/docs/GITHUB_SYNC.md)。
- **同步方式：** 后续已完成修改经相关验证和快照审查后同步本目录，保留仓库其他项目；不代表云端部署或自动监控电脑文件。

## 后续项目如何收录

新增项目时，先确认其实际源码已提交，再在本页新增一条记录；**不要把计划中的自动化当作已经上传或已上线**。

每个项目至少记录：业务场景、目录链接、主要入口、运行依赖、测试方式、同步边界和真实状态。继续使用现有目录名称，避免影响 Windows 定时任务和本地 Codex 的同步规则。

参见 [仓库维护指南](REPOSITORY_GUIDE.md)。
