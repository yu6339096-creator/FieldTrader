# FieldTrader 0.8.16

安装包：`FieldTrader-0.8.16-SingleTrade-Setup.exe`（Git LFS大文件）。下载LFS文件内容后运行安装，启动器默认请求Windows管理员权限。

本版包含OCR 75%门槛、YOLO辅助页面识别、真实Windows SendInput鼠标输入、自动处理目标匹配的兑换确认框、进度显示、预算核验和每轮运行状态重置。旧未完成记录不再阻止下一次主动开始，原始结果保留，不冒充已完成交易。

当前真实入口从已打开的详情页开始，每轮一笔：拉里100D战地医疗包×1，或市场购买1—5件。迪克、兰德尔、材料与钥匙路线及统一利润引擎的代码一并归档；完整巡回和出售不属于当前单笔入口。

`verification.json`保留35项交易流程检查、界面检查、本地窗口真实鼠标检查，以及用户反馈本版本真实执行成功的记录。用户反馈与自动验证证据分别标记。

`package-manifest.json`提供安装包及程序的哈希；模型文件见`models/manifest.json`。打包工具见`tools/Packaging/package_release.py`；`tools/Packaging/archive/`保留交付当时的本地脚本，原始目录假设仅作为历史记录。
