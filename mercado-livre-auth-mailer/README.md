# Mercado Livre Auth Mailer · 店铺授权码发送自动化

这是一个 Windows 本地工具：紫鸟企业账号登录一次后，程序通过紫鸟官方 agent 通道启动六个子账号店铺，读取授权码，校验完整批次，再通过 163 的 SMTP 发信并用 IMAP 核验发送状态。

## GitHub 与后续同步

项目保存在 [sekaowa4/ecom-automat-hub 的 mercado-livre-auth-mailer 目录](https://github.com/sekaowa4/ecom-automat-hub/tree/main/mercado-livre-auth-mailer)。本机定时任务继续在原来的项目文件夹运行。

后续通过助手修改项目时，完成相应检查后同步到该目录，保留仓库中的其他项目。同步步骤和排除范围见 `AGENTS.md`。这是一项开发工作约定，不是后台文件监控；手工修改文件不会自行上传。

云端只保存源码、测试、脚本、模板和示例配置。`config.toml`、Windows 凭据、`profiles/`、`runtime/`、`data/`、虚拟环境和原始 Word 流程文档只留在本机。换电脑时需要重新配置邮箱凭据、紫鸟环境和定时任务。

测试命令（不会发送邮件或企微通知）：

```powershell
.venv\Scripts\python.exe -m unittest discover -s tests -v
```

## 首次安装

在本目录打开 PowerShell：

```powershell
python -m venv .venv
.venv\Scripts\python.exe -m pip install -r requirements.txt
.venv\Scripts\playwright.exe install chromium
Copy-Item config.example.toml config.toml
```

编辑 `config.toml`：

- 填写 163 发件箱、正式收件人和主题。
- 确认 `imap_folder` 与 163 中的“已发送”文件夹名称一致。
- 如果页面正文出现多个符合格式的字符串，为对应店铺填写 `code_selector`。
- 如果实际授权码不是 8 位大写字母数字，调整 `rules.code_regex`。

## 初始化

```powershell
.venv\Scripts\python.exe -m shop_auth_codes.cli --config config.toml setup
```

程序会提示输入 163 客户端授权码，并将其保存到 Windows 凭据管理器；随后验证紫鸟官方店铺列表。紫鸟企业账号和 Mercado Livre 子账号登录状态由紫鸟客户端维护，程序不会要求或保存紫鸟密码。

163 的客户端授权码不是邮箱网页登录密码，需要在 163 邮箱设置中开启 IMAP/SMTP 后生成。

## 日常运行

13:00 之后执行：

```powershell
.venv\Scripts\python.exe -m shop_auth_codes.cli --config config.toml dry-run
.venv\Scripts\python.exe -m shop_auth_codes.cli --config config.toml run
```

`dry-run` 只采集并显示待发送正文；`run` 在所有店铺采集和校验通过后发送邮件。任一店铺失败、账号会话失效、代码缺失/重复/格式错误时，整批停止，不发送部分邮件。

采集器兼容旧版“获取代码”弹窗及新版“用于授权货件及关联账户的代码”页面，读取新版“今日发货代码”字段。原取码入口返回 HTTP 403 时，先在当前店铺同一账号的“售后”页面读取“今日退货的授权码”；正常取码的店铺保持原流程。页面出现新手引导时先关闭引导；遇到登录验证或售后页面仍返回 HTTP 403 时，尝试同店铺另一个账号。若两个账号都要求验证，程序会在企微群 @`paprika` 一次，并每30秒重查；完成验证后会自动继续采集。浏览器临时断连、导航中断和紫鸟启动未完成时持续重试，不设最终等待上限。

企微官方接口使用直接 HTTPS 连接，避免本机海外代理导致通知 TLS 握手失败；不修改系统代理设置。

手动只采集六个授权码、不发邮件时，可执行：

```powershell
.\collect_six_codes.ps1
```

完整授权码只在本机结果窗口和“邮件已发送并核验成功”的企微群通知中显示；终端、运行报告和日志只显示掩码或批次指纹。执行前请保持紫鸟客户端已登录 Kevin，并确保六个带“子账号”的店铺可见。

运行记录保存在 `data/runs.json`，审计日志保存在 `data/run.log`；两者只保存批次指纹、状态和错误，不保存完整授权码。已发送的同一批次默认不能重复发送。

## 注意事项

- 不要把 `config.toml`、`profiles/` 或 `data/` 提交到版本库。
- 不要把邮箱密码或客户端授权码写入代码、配置或日志。
- 紫鸟客户端未登录或六个子账号不可见时程序会失败即停。
- 页面文字变化时程序会失败即停，需要更新采集器中的页面选择器。
- 首版不自动处理验证码、二次验证或风控拦截；遇到这些情况请在浏览器中人工完成登录。
