# FieldTrader 模型归档

全部 `.pt`、`.onnx` 权重及版本安装包使用 Git LFS。克隆后执行 `git lfs install`、`git lfs pull`，再运行 `python tools/ItemVision/verify_models.py` 校验原始文件大小和 SHA-256。

当前程序使用：

- `ui14/best.pt`：14类界面模型，保留原始类别顺序。
- `items22/model/items22-selected.pt`：22类物品模型，本版本选择的权重。
- `ocr-v4/`：PP-OCRv4检测、识别及方向分类运行模型。

`items19/`和`items22/`保留各轮训练产生的模型、best/last及定期checkpoint；内容相同的文件由LFS按哈希复用存储。`manifest.json`列出每个文件的大小、哈希和当前选型，权重字节未被改写。

`reports/`保留训练进度、评估、类别与标注元数据；机器本地路径替换为占位符。训练用的原始游戏截图、个人仓库和交易运行日志没有加入此归档。训练脚本位于`tools/ItemVision/`，历史训练脚本内的工作目录需要按实际数据位置调整。

19类和22类模型的训练集复测、开发验证、无独立验证样本的类别分别记载在评估报告中。实验物品识别不代表所有类别和兑换路线均已完成真实游戏验收。

模型及其上游依赖的授权条件不因本归档改变；OCR运行模型并非本项目重新训练的权重。
