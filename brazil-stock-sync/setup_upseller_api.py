from getpass import getpass
import json

from upseller_api_export import SECRET_PATH


cookie = getpass("粘贴 UpSeller Cookie 中 us_u 的值（输入不会回显）: ").strip()
if not cookie:
    raise SystemExit("未写入：us_u 不能为空")
SECRET_PATH.write_text(json.dumps({"us_u": cookie}, ensure_ascii=False), encoding="utf-8")
print("已保存到本机运行配置；不会写入日志。")
