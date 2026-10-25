# GitHub 发布与同步

- 仓库：`https://github.com/sekaowa4/ecom-automat-hub`
- 子目录：`brazil-stock-sync`
- 公开范围：脱敏源码、测试、文档。用户已于2026-10-25批准。

## 每次同步

1. 在当前本地项目运行 `python tools/prepare_github_snapshot.py`。
2. 检查生成的本地 manifest 和脱敏报告。只发布 `runtime/github-public/brazil-stock-sync` 中列出的文件，不发布其他 runtime 文件。
3. 运行相关测试并记录失败项。不要为上传代码而执行库存写回或发送群通知。
4. 检查远端当前分支和项目目录，保留其他目录。通过已有 GitHub 连接提交更新后的项目树；如需删除文件，只删除上次 manifest 中属于本项目且本次确已移除的路径。
5. 回读远端提交和文件树，比较 blob hash，并将 commit SHA 记录在本机 `runtime/github_sync_state.json`。
6. 如失败，明确告知仅本地已更改，不声称同步成功。禁止强制推送覆盖远端工作。

不能直接将本地项目根目录推到仓库根目录；本项目属于一个子目录。
不要将 Cookie、Webhook、MCP密钥、私人文档链接、真实库存文件、模板、日志或截图加入 Git。
程序不会在后台自动同步未经检查的改动。“后续同步”指 Codex 完成经授权的项目修改后执行上述流程。
