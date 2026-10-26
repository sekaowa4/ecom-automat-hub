# GitHub 同步约定

仓库：https://github.com/sekaowa4/ecom-automat-hub ，分支 `main`，项目目录 `mercado-repricing/`。

用户已授权本项目源码首次上传，以及后续已完成、已验证修改的同步。每次修改结束后检查远端最新提交，生成并审查源码快照，保留其他项目及他人的提交，再上传并核对远端提交。同步失败需明确报告；不强制推送。

运行 `python scripts/prepare-github-snapshot.py`，默认生成本机 `tmp/github-source-snapshot/mercado-repricing/`。脚本只复制白名单源码、合成测试样例、空白模板和通用说明；实际任务、日志、订单及价格表、截图、凭据、数据库、依赖环境和安装包不在快照中。快照清单记录文件大小及 SHA256。

`PDFLabelCropper.exe` 是用户在本机提供的标签引擎，`CartonLabelCropper.exe` 是本机构建产物，均不公开上传。克隆后按 README 准备这两个本机组件再构建、执行完整标签测试或打包。

源码上传不等于部署，也不会监控电脑上任意文件变化。同步发生在本项目改动完成并验证之后；业务任务仍需本机紫鸟、登录状态和用户授权。
