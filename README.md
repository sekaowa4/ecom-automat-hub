# E-commerce Automation Hub

跨境电商运营自动化项目集合，集中管理 **Mercado Livre（美客多）、Shopee（虾皮）、UpSeller、OMS 与企业微信** 等业务场景的工具源码与文档。

> **仓库定位：** 自动化项目的源码备份、版本管理和协作入口；**不是**云端部署平台，也不会自动同步公司电脑上的文件。当前仓库为 **Public（公开）**，请仅提交经确认可公开的脱敏内容。

## 📦 已收录项目

| 项目 | 业务场景 | 核心功能 | 当前仓库内容 |
| --- | --- | --- | --- |
| [Brazil Stock Sync（巴西库存同步）](brazil-stock-sync/README.md) | UpSeller / 企业微信 | API 导出六份销售及库存数据、校验、生成 Stock_Order 更新结果、在线写回与回读核对 | 脱敏源码、测试、启动脚本、技术文档 |
| [Mercado Livre Auth Mailer（美客多授权码自动发送）](mercado-livre-auth-mailer/README.md) | 紫鸟 / 美客多 / 邮箱 | 采集六个子账号店铺授权码、批次校验、邮件发送与状态核验 | 源码、测试、PowerShell 脚本、示例配置 |
| [OMS Sea Workbench（海运工作台）](oms-sea-workbench/README.md) | UpSeller / OMS 海运入库与开票 | 上传清单、批次校验、商品与单据分步处理、操作确认及附件回传 | 脱敏源码、网页界面、测试、启动脚本和示例配置 |
| [Mercado Repricing（美客多调价工作台）](mercado-repricing/README.md) | 紫鸟 / Mercado Livre 巴西站 | 日常调价、周末活动报名、多商品链接逐项核验、标签裁切、UpSeller 销售与 Full 订单看板、内置 SKU 批次映射 | 0.5.48 脱敏源码与回归测试；本机运营数据、工作簿及凭据不公开 |

**项目说明和运行要求以各自 README 为准。** 已收录表示代码位于 GitHub，不等于已在云端部署或通过所有测试。

想快速查看各项目的入口、依赖与注意事项？参见 **[项目索引](docs/PROJECTS.md)**。

## 🗂️ 仓库结构

```text
ecom-automat-hub/
├── README.md                      # 仓库首页
├── docs/
│   ├── PROJECTS.md                # 项目索引、入口和运行边界
│   └── REPOSITORY_GUIDE.md        # 新项目接入与 GitHub 同步约定
├── brazil-stock-sync/             # 巴西库存更新自动化
│   ├── README.md
│   ├── AGENTS.md
│   ├── docs/
│   ├── tests/
│   └── ...                        # 源码、脚本及前端
├── mercado-repricing/             # 美客多调价、周末活动报名及标签工作台
├── oms-sea-workbench/             # 海运录入工作台，独立本机配置
│   ├── README.md
│   ├── web/                       # 网页界面
│   ├── webapp/                    # 本机服务与浏览器流程
│   └── ...
└── mercado-livre-auth-mailer/     # 美客多授权码自动发送
    ├── README.md
    ├── AGENTS.md
    ├── shop_auth_codes/
    ├── tests/
    └── ...                        # 源码及 PowerShell 脚本
```

> 结构图仅展示主要目录；省略号不代表需要新建文件。**不调整现有项目目录名称**，以免影响本地脚本、自动化任务及后续同步。

## 🚀 如何使用

1. **查看项目：** 先阅读上方项目列表，进入对应子目录的 `README.md`。
2. **本机运行：** 按该项目的说明安装依赖，并在**本机**配置凭据、模板及授权；勿认为克隆仓库即可执行生产任务。
3. **协同开发：** 每次以最新 GitHub 提交为基础修改；验证、脱敏后再提交。不同项目之间互不覆盖。
4. **新增工具：** 每个自动化项目使用单独的英文小写连字符目录，并更新首页和项目索引。具体规范见 [仓库维护指南](docs/REPOSITORY_GUIDE.md)。

## 🔐 安全与数据边界

- **只上传：** 审查后的源码、测试、脚本、必要的说明和**示例**配置。
- **禁止上传：** Cookie、Token、邮箱密码或授权码、企微 Webhook 密钥、真实库存表和销售数据、客户信息、日志、浏览器会话、实际运行配置、虚拟环境。
- 子项目已有独立 `.gitignore` 与同步说明。**仅有 .gitignore 不保证文件不会泄露**，提交前仍须检查差异及 Git 历史。
- 若凭据曾经提交，即使删除文件也应立即撤销或轮换凭据；必要时检查历史提交。

## ⚠️ 运行状态说明

- **Brazil Stock Sync：** 需要本机 UpSeller 授权、Stock_Order 模板、企微授权和运行环境；部分历史测试有已知失败，详见 [项目说明](brazil-stock-sync/README.md)。
- **Mercado Livre Auth Mailer：** 需要已登录的紫鸟客户端、独立邮箱配置与 Windows 本机环境；采集和发信不是仓库维护操作的一部分。
- **OMS Sea Workbench：** 需要 Windows 本机、已登录专用 Chrome 和私有配置。清单、发票及历史单号不公开；本地测试通过不代表真实 UpSeller/OMS 流程已联调验收。

仓库维护与源码同步不调用正式业务接口、修改运营数据、开票、发送附件、邮件或通知。
