<div align="center">
  <h1>📦 Brazil Stock Sync</h1>
  <p><strong>巴西库存更新 · UpSeller → Stock_Order → 企业微信</strong></p>
  <p>将六份销售/库存数据转换为可核对的库存更新结果，并在校验后写回在线表格。</p>
  <p>
    <img alt="Python 3.x" src="https://img.shields.io/badge/Python-3.x-3776AB?style=flat-square&amp;logo=python&amp;logoColor=white">
    <img alt="UpSeller and WeCom" src="https://img.shields.io/badge/Integration-UpSeller%20%2B%20WeCom-285943?style=flat-square">
    <img alt="Windows Local" src="https://img.shields.io/badge/Runtime-Windows%20Local-64748B?style=flat-square">
    <img alt="Sanitized Source" src="https://img.shields.io/badge/Release-Sanitized%20Source-64748B?style=flat-square">
  </p>
  <p><a href="../README.md">返回项目总览</a> · <a href="docs/GITHUB_SYNC.md">同步规范</a> · <a href="AGENTS.md">开发约定</a></p>
</div>

> [!IMPORTANT]
> **这是接口版的脱敏源码备份，不是线上服务。** 本机原项目为“副本2”；仓库不包含授权、真实 Stock_Order 模板或实际库存数据，也不代表已部署至 Codex Cloud。请勿直接用于生产写回。

## ✨ 项目概览

| 维度 | 说明 |
| :-- | :-- |
| **业务目标** | 减少巴西店铺库存更新时的重复导出、人工整理与在线表格写入工作 |
| **数据源** | UpSeller 接口：近 **30 / 7 / 15 / 3 天销量** + **FULL 库存** + **三方仓库存**，共六份数据 |
| **处理方式** | 检查表头与字段、按 SKU / 店铺汇总、结合本机最新 Stock_Order 模板生成更新结果 |
| **写入目标** | 授权的企业微信在线表格：更新指定数据列与 A1 日期，保留公式及其他工作表 |
| **完成条件** | 写后回读并核对，通过才允许发送成功群通知；失败不发送成功消息 |

## 🔄 工作流程

~~~mermaid
flowchart TD
    A["UpSeller API<br/>4 份销量 + FULL 库存 + 三方仓库存"] --> B["六份数据表头与字段校验"]
    B --> C["SKU / 店铺汇总与模板映射"]
    C --> D["生成 Stock_Order 更新结果"]
    D --> E["核对在线列含义、SKU 行位置及库存来源"]
    E --> F["企业微信在线表格写回<br/>保留业务公式与其他工作表"]
    F --> G{"回读核对通过？"}
    G -- 是 --> H["成功群通知"]
    G -- 否 --> I["停止并报告问题<br/>不发送成功通知"]
~~~

> **流程图是业务设计示意，不是一次真实运行的执行记录。** 在线列含义、库存来源独立核验、SKU 行位置和写后回读需要由实际执行流程落实；不能仅凭本地表与在线表数值一致认定源数据正确。

## 🧩 核心模块

| 文件 / 目录 | 职责 |
| :-- | :-- |
| [`server.py`](server.py) + [`web/`](web/) | 本地库存工作台及 Web 页面 |
| [`upseller_api_export.py`](upseller_api_export.py) | UpSeller API 数据导出 |
| [`inventory_pipeline.py`](inventory_pipeline.py) | 库存数据清洗、校验及汇总逻辑 |
| [`daily_update.py`](daily_update.py) | 每日库存更新相关逻辑 |
| [`wecom_sync.py`](wecom_sync.py) | 企业微信在线表格同步 |
| [`preflight_check.py`](preflight_check.py) | 运行前检查 |
| [`tests/`](tests/) | 单元及历史集成测试 |
| [`automation/`](automation/) | 浏览器与 OMS 历史 / 备用实现；**不是**接口主流程必需步骤 |

## 🚀 在本机启动

**先决条件：** Python 运行环境、必要依赖、有效的本机 UpSeller 授权、最新 Stock_Order 模板与企业微信授权。

在项目目录下，按原有流程运行：

~~~powershell
python portable_setup.py
python setup_upseller_api.py
python server.py
~~~

本地工作台地址：**`http://127.0.0.1:8765/`**

- 以上命令用于本机配置和启动；是否能够执行完整业务流程取决于本地模板、Cookie、企微授权及运行环境。
- 备用的浏览器自动化还需 Node.js、Playwright、JSZip；一些历史 CMD 路径与机器环境绑定，迁移电脑后需要适配。
- 请先阅读 [新电脑使用说明](新电脑使用说明.md) 和 [自动化连接器说明](自动化连接器说明.md)，不要直接对正式表做试运行。

## 🔐 本地配置与安全边界

以下文件或信息**只保留本机，不上传 GitHub**：

| 本机路径 / 项 | 用途 |
| :-- | :-- |
| `runtime/upseller_api.local.json` 或环境变量 `UPSELLER_US_U` | UpSeller Cookie |
| `runtime/wecom_config.json` | 在线表链接；MCP 授权由本机企微 CLI 管理 |
| `runtime/automation_config.json` | 企业微信群 Webhook |
| `runtime/stock_order_template_fixed_layout.xlsx` | 当前真实业务模板 |
| 导出数据、运行预览、日志、浏览器会话 | 本地运行数据与状态 |

公开副本中私人文档标识、授权参数和本机用户名已替换为示例值。**本机加密凭据不能简单复制到云端或新电脑。** 任何后续提交均需检查文件内容及 Git 历史，不可只依赖 `.gitignore`。

## 🛡️ 质量与校验原则

- 2026-10 已针对“三方仓接口实际返回 22 列、旧请求只提供 14 列表头”的错配问题做过修复。
- 六份数据若存在有效数据列缺失表头或重复表头，必须停止生成和写回。
- **FULL 未匹配到 SKU / 店铺时留空，而不是写 0**；库存属于导出时刻的快照，不能当成实时库存相等。
- 写回时保留已有公式、格式及其他工作表，更新 A1 日期的要求为北京时间 `最近更新时间：M.D`、18 磅标准红色。
- 写后回读只是必要检查之一；必须另外核验来源、字段、行位置，成功后才通知。

### 测试

~~~powershell
python -m unittest discover -s tests
~~~

> [!WARNING]
> **当前不能宣称“全部测试通过”。** 公开副本保留两项已知历史预检报错：`test_marks_the_configured_test_table_as_valid`、`test_reports_a_stale_running_job`。部分历史集成测试依赖未上传的 `runtime/` 模板、原始文件及授权，干净环境中可能还有额外的缺失配置错误。本次 README 排版更新没有执行测试。

## 🔁 GitHub 同步与继续开发

库存项目有**独立的脱敏快照流程**，不要将本机工作目录直接推送到仓库：

1. 在已授权的本机项目中运行 `python tools/prepare_github_snapshot.py`。
2. 审核本地 manifest 与脱敏结果；只发布 `runtime/github-public/brazil-stock-sync` 中允许的文件。
3. 运行与改动相关的安全检查，核对远端差异，保留其他项目文件。
4. 提交后回读远端版本；本机同步状态只有核验成功才能更新。

详细规则请查看 [GitHub 发布与同步](docs/GITHUB_SYNC.md) 和 [AGENTS.md](AGENTS.md)。**GitHub 同步不等于自动部署、自动运行库存任务或后台持续监控。**

---

<sub>Part of <a href="../README.md">E-commerce Automation Hub</a> · Source archive · Local execution only</sub>
