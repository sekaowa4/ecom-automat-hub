<div align="center">
  <h1>✉️ Mercado Livre Auth Mailer</h1>
  <p><strong>美客多店铺授权码自动采集与发送</strong></p>
  <p>紫鸟启动六个子账号店铺 → 授权码批次校验 → 邮件发送与状态核验。</p>
  <p>
    <img alt="Python 3.x" src="https://img.shields.io/badge/Python-3.x-3776AB?style=flat-square&amp;logo=python&amp;logoColor=white">
    <img alt="Mercado Livre" src="https://img.shields.io/badge/Platform-Mercado%20Livre-FFE600?style=flat-square">
    <img alt="Windows Local" src="https://img.shields.io/badge/Runtime-Windows%20Local-64748B?style=flat-square">
    <img alt="Source and Sample Config" src="https://img.shields.io/badge/Release-Source%20%2B%20Sample%20Config-64748B?style=flat-square">
  </p>
  <p><a href="../README.md">返回项目总览</a> · <a href="config.example.toml">配置示例</a> · <a href="AGENTS.md">开发约定</a></p>
</div>

> [!IMPORTANT]
> **这是 Windows 本地项目的源码和示例配置，不是在线授权码服务。** GitHub 中没有紫鸟登录会话、邮箱客户端授权码、真实 `config.toml` 或发送记录。运行前必须在自己的本机环境重新完成授权。

## ✨ 项目概览

| 维度 | 说明 |
| :-- | :-- |
| **业务目标** | 批量采集 Mercado Livre 六个子账号店铺授权码，减少人工逐店操作 |
| **采集方式** | 使用已登录的紫鸟企业客户端，通过官方 agent 通道启动店铺并读取授权码 |
| **发送渠道** | 通过 163 邮箱 SMTP 发送，并使用 IMAP 核验发送状态 |
| **结果通知** | 在邮件成功发送并核验后，按既定流程发送企业微信通知 |
| **失败策略** | 任一店铺缺失、重复、格式错误或会话异常，整批停止，不发送部分邮件 |
| **运行环境** | Windows 本机 + 紫鸟客户端 + Python + 本地邮箱授权配置 |

## 🔄 工作流程

~~~mermaid
flowchart TD
    A["本机紫鸟客户端<br/>已登录企业账号"] --> B["官方 Agent 启动六个子账号店铺"]
    B --> C["读取授权码"]
    C --> D{"六店齐全且格式、重复校验通过？"}
    D -- 否 --> X["停止整批任务<br/>不发送部分邮件"]
    D -- 是 --> E["163 SMTP 发信"]
    E --> F["IMAP 核验邮件发送状态"]
    F --> G{"发送及核验成功？"}
    G -- 是 --> H["企业微信成功通知"]
    G -- 否 --> I["标记失败 / 待处理<br/>不宣称发送成功"]
~~~

> 流程图用于展示预期逻辑，不表示本次实际执行了采集、邮件发送或通知。遇到登录验证码、二次验证或风控拦截时，应人工处理，工具不自动绕过。

## 🧩 核心模块

| 文件 / 目录 | 职责 |
| :-- | :-- |
| [`shop_auth_codes/`](shop_auth_codes/) | 主程序：采集、校验、凭据、发送、核验与通知 |
| [`shop_auth_codes/cli.py`](shop_auth_codes/cli.py) | 命令行入口 |
| [`run_daily.ps1`](run_daily.ps1) | 日常启动脚本 |
| [`collect_six_codes.ps1`](collect_six_codes.ps1) | 手动只采集六个授权码 |
| [`config.example.toml`](config.example.toml) | 可复制的配置示例（不含真实凭据） |
| [`tests/`](tests/) | 不需要执行真实发送的测试 |
| [`tools/`](tools/) | 辅助采集与调试脚本 |

## 🚀 快速开始（Windows）

### 1. 安装项目依赖

在项目目录下打开 PowerShell：

~~~powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
.venv\Scripts\playwright.exe install chromium
Copy-Item config.example.toml config.toml
~~~

### 2. 配置本机邮箱与店铺规则

仅在**本地**修改 `config.toml`：

- 填写 163 发件箱、正式收件人和主题。
- 核对 `imap_folder` 与邮箱“已发送”文件夹名称。
- 如果页面出现多个符合格式的字符串，为相应店铺设置 `code_selector`。
- 如果实际授权码格式不是 8 位大写字母数字，按真实规则调整 `rules.code_regex`。

### 3. 初始化本机凭据与店铺

~~~powershell
.venv\Scripts\python.exe -m shop_auth_codes.cli --config config.toml setup
~~~

首次初始化会提示输入 **163 邮箱客户端授权码（不是网页登录密码）**，将其保存到 Windows 凭据管理器，并验证紫鸟官方店铺列表。运行前应保持紫鸟客户端已登录相应企业账号，六个带“子账号”标识的店铺可见。

### 4. 日常执行

按项目原有安排，**13:00 之后**在本机运行：

~~~powershell
# 预演：只采集并显示待发送内容，不真正发邮件
.venv\Scripts\python.exe -m shop_auth_codes.cli --config config.toml dry-run

# 正式运行：完整批次校验通过后才执行邮件发送
.venv\Scripts\python.exe -m shop_auth_codes.cli --config config.toml run
~~~

如果只需要采集、不发送邮件：

~~~powershell
.\collect_six_codes.ps1
~~~

> **注意：** `run` 是真实业务操作，可能发送邮件和企业微信通知；请仅在被授权执行正式业务任务时使用。阅读本 README、代码同步或运行测试均不构成发信授权。

## 🔐 发送保护与数据安全

| 保护点 | 规则 |
| :-- | :-- |
| 完整性检查 | 必须集齐六个店铺的有效授权码，否则整个批次停止 |
| 重复发送保护 | 已发送的相同批次默认不可再次发送 |
| 凭据 | 163 客户端授权码存于本机 Windows 凭据管理器，不写进源码 |
| 日志 | `data/runs.json`、`data/run.log` 记录批次指纹、状态和错误，不记录完整授权码 |
| 终端输出 | 日常终端、运行报告和日志只展示掩码或批次指纹 |
| 完整授权码 | 仅在本机结果窗口和成功发送后核验的企业微信通知中显示 |

**不得提交 GitHub：** `config.toml`、`profiles/`、`runtime/`、`data/`、`.venv/`、Cookie、邮箱密码、SMTP 授权码、Webhook 密钥、真实授权码、浏览器会话或原始 Word 流程文档。文件排除规则见 [`.gitignore`](.gitignore)，但仍需人工审核代码和提交差异。

## 🧪 测试与已知限制

测试命令（不执行正式邮件发送或企微通知）：

~~~powershell
.venv\Scripts\python.exe -m unittest discover -s tests -v
~~~

- 紫鸟未登录或六个子账号不可见时，流程会失败并停止。
- 页面文案或结构变化可能导致采集器找不到目标内容，需要维护选择器。
- 不自动处理验证码、二次验证或风控拦截。
- **本次仅修改 README 展示文档，没有运行本机测试或真实业务发送，因此不对当前通过率作出保证。**

## 🔁 GitHub 同步与本机协作

- 此目录为 [`ecom-automat-hub`](../README.md) 的一个独立子项目。
- 本机正式运行目录继续保留原位置，**不要因为整理 GitHub 而改变 Windows 定时任务路径**。
- Codex 完成用户要求的代码修改后，先执行相应检查、脱敏，再同步本项目目录；禁止覆盖仓库里的其他自动化项目。
- GitHub 同步仅备份源码，不表示 Windows 本机文件会实时同步。手工修改本地文件不会自动上传；换电脑还需重新配置紫鸟、邮箱和定时任务。

具体维护范围及排除规则请查看 [AGENTS.md](AGENTS.md)。

---

<sub>Part of <a href="../README.md">E-commerce Automation Hub</a> · Local Windows automation · No live credentials in repository</sub>
