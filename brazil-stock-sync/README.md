# Brazil Stock Sync / 巴西库存更新

`ecom-automat-hub/brazil-stock-sync` 保存当前接口版（本地“副本2”）的脱敏源码。
这是源码备份，不代表已部署到 Codex Cloud，也不包含账号授权和业务数据。

## 当前流程

1. 使用本机配置的 UpSeller Cookie，通过接口导出近30、7、15、3天销量、FULL库存、三方仓库存，共六份文件。
2. 校验源文件表头，按 SKU/店铺汇总，在本机最新 Stock_Order 模板基础上生成更新结果。
3. 通过授权的企微文档 MCP 更新正式表的指定数据列和 A1 日期，保留业务公式及其他工作表。
4. 回读核对通过后才发送成功群通知；失败不发群通知。

接口为默认路径。`automation/` 中浏览器和 OMS 代码为历史或备用实现，不是当前六文件流程的必经步骤。
历史设计文档中的上传文件、OMS、列位置等旧说明不代表当前规则。

## 本机配置，不上传 GitHub

- `runtime/upseller_api.local.json`：Cookie，或使用环境变量 `UPSELLER_US_U`。
- `runtime/wecom_config.json`：在线表链接；MCP凭据由本机企微 CLI 授权保存。
- `runtime/automation_config.json`：群 Webhook。
- `runtime/stock_order_template_fixed_layout.xlsx`：最新业务模板，需要自行提供。
- 导出文件、预览、日志、浏览器状态、机器专用依赖全部排除。

公开副本中的私人文档标识、授权参数和本机用户名已替换为示例值，不能直接用于生产。
新电脑必须重新配置凭据和模板；Windows本机加密凭据不可直接搬到云端。

## 本地启动

在已安装项目 Python 依赖的电脑上执行：

```powershell
python portable_setup.py
python setup_upseller_api.py
python server.py
```

工作台：`http://127.0.0.1:8765/`。完整运行还需要本机模板、有效Cookie和企微授权。
浏览器备用流程另需 Node.js、Playwright、JSZip；CMD及历史代码中的机器运行时路径需要适配目标电脑。

## 安全与测试

- 2026-10修复三方仓接口返回22列、旧请求仅提供14列表头导致的错配。
- 六份源文件出现无表头的有效数据列或重复表头时，停止生成和写回。
- 本地表与在线表一致，不等于源数据独立校验通过。
- Codex日常编排另执行在线列含义、库存列表独立核验、SKU行位置与写后回读；不能仅凭调用某个历史脚本就假定这些检查均已执行。
- 库存以导出时点为快照，实时占用变化单独记录；FULL未匹配留空，不填0。

```powershell
python -m unittest discover -s tests
```

本次发布保留2项已知历史预检测试报错，未改动业务实现来掩盖它们：
`test_marks_the_configured_test_table_as_valid`、`test_reports_a_stale_running_job`。
不能将当前测试套件描述为全部通过。
另外，部分历史集成测试直接依赖本机 `runtime` 中的模板、原始文件和授权；脱敏副本未携带这些内容，在干净环境中会产生额外缺失配置错误，不能视为开箱即用的完整离线测试套件。

## GitHub 同步

后续用户请求的源码变更，在验证并脱敏后同步本仓库，不上传每天产生的数据，也不设置后台监控。
步骤见 `docs/GITHUB_SYNC.md`，规则见 `AGENTS.md`。
