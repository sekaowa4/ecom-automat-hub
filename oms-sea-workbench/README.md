# OMS Sea Workbench · 海运工作台

本机运行的海运录入工作台：上传装箱清单，核对商品与数量，再分步骤处理 UpSeller 与 OMS。亮橙白色界面，以批次任务和操作确认为主。

## 启动

需要 Windows、Python 3.12+、Node.js 22.12+ 和 Chrome。

1. 将 `local-config.example.json` 复制为 `local-config.json`，填写自己的 OMS 网站地址、开票店铺及收件资料。真实配置只留本机。
2. 双击 `启动海运工作台.cmd`，首次自动安装依赖并构建页面。入口 http://127.0.0.1:8765 。
3. 在连接设置中复用已登录的专用 Chrome 调试连接，选择常用 UpSeller、开票专用 UpSeller 和 OMS 标签页，不复制 Cookie。
4. 上传清单，核对结果，点击开始。提交入库单、发货、开票、发送附件分别需要网页当次确认。

这是本机网页工具，不是公开在线 SaaS。不要将本地服务直接暴露到公网。

## 固定流程

- XLSX 数量 K 列按 SKU 汇总；单价 AA 列；单套尺寸 W:Y 为 cm；Z 列 kg 转 g。CSV 使用明确表头。支持合并单元格和 WPS 图片；特殊共享图片映射放在本机 image_aliases。
- 从常用 UpSeller 导出已有商品，排除补货 SKU，只新建缺失商品，每个商品保存后再继续。
- OMS 更新只修改导出表 D:G 的长宽高和重量，保留葡语名称 B 列及其他字段，导入后再次核验。
- 入库单包含整批新品和老品。默认 BomJesus、箱、海运、按箱预报、SKU 维度；页面总数量填件数而非箱数。平台信息变化时应核验页面后再执行。
- 专用账号手工订单使用“添加商品”，不使用“导入SKU”。收件资料来自本机配置，等待 CNPJ 自动带出其余地址再核对。金额用逗号小数键盘输入，保存前核对每项与总额。
- NCM 来自常用 UpSeller 商品列表导出文件 AG 列，不从装箱单读取。逐 SKU 填写 NCM、原产地代码 2，只改两项，保存后复开核验，全部通过后才开票。
- PDF：待发货中只选目标订单 → 更多操作 → 打印发票 → DANFE；必要时从 PDF 预览下载。XML：订单 → 巴西 NF-e → 近期发票 → 对应已开票记录 → 更多 → 导出XML。
- 按目标订单、发票号、44 位访问密钥、总额和收件 CNPJ 核对两份文件，命名为“第{批次}批入库发票.pdf/.xml”，不覆盖同名文件。
- OMS 按入库单号打开沟通记录，发送 PDF、清单和原样 XML 的 ZIP。清单超过 10 MB 时仅压缩副本图片，保留表格与原文件名，不加“压缩上传”后缀。发送后检查记录，状态不明先查询，避免重复发送。

## 本机资料与历史保护

`.workbench/` 保存批次进度、源清单、导出文件与单号；`webapp/completed_batches.json` 保存本机已完成批次保护记录。均不进入公开仓库。现有机器的历史记录必须保留；换电脑需私下迁移，拉取源码不会同步历史业务状态。

`README.local.md`、`AGENTS.local.md` 是原有业务说明的本机副本，不公开。不要把其中的真实客户资料重新写回源码。

## 开发验证

```powershell
npm.cmd install
python -m pip install -r requirements.txt
npm.cmd run build
python -m unittest discover -s tests
npm.cmd run test:browser
```

可选 `python tests/run_web_ui_check.py` 需要私有配置中的 acceptance_files 与 acceptance_checks，使用临时服务，不执行真实平台操作。默认单元测试不依赖真实业务文件。

当前自动化最多处理一次完整显示的 100 个 SKU。页面变更、登录失效或保存结果不明时停止。真实 UpSeller/OMS 各步骤仍需登录后逐步联调；本地测试与构建通过不代表真实全流程已验收。

## GitHub 同步

项目名：`oms-sea-workbench`。源码：[ecom-automat-hub / oms-sea-workbench](https://github.com/sekaowa4/ecom-automat-hub/tree/main/oms-sea-workbench)。

后续由助手修改时，先验证、审查暂存文件是否含业务数据，再提交并推送。同步失败必须报告，不强推覆盖远端。不上传清单、发票、客户资料、会话、密钥、进度和日志。手工修改不会在后台自动上传。
