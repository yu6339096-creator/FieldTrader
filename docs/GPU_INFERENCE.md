# Windows GPU 优先与 CPU 回退

默认配置 `InferenceMode: "GpuPreferred"`、`GpuDeviceId: 0`。
AMD Radeon 与 NVIDIA GeForce 都使用 ONNX Runtime DirectML，不要求 CUDA。
设备必须支持 DirectX 12；不能据品牌名称保证所有旧显卡都支持。
OCR 的检测、方向和识别模型，以及 UI/物品两套 YOLO 都尝试使用 GPU。
DirectML 不支持的个别算子仍可能由 ONNX Runtime 的 CPU provider 执行。

GPU 初始化或单次推理抛出异常时，对应模型立即切换 CPU，并重试这次识别。
此后同一个 worker 会话保持 CPU，不反复加载故障 GPU。不同模型独立回退。
CPU 本身再失败会正常报错，不返回伪造的空结果。进程卡死/驱动崩溃不属于
可捕获的推理异常：宿主仍会超时终止并暂停，避免继续使用过时截图。
初始化允许 300 秒，普通识别允许 60 秒；用户取消不触发重试。

## 安装包已接入内置依赖

`tools/Packaging/package_release.py` 会在构建机下载并内置：

- onnxruntime-directml 1.24.4（含 DirectML.dll 与 CPU provider）
- onnx 1.17.0（供本地 YOLO 导出使用）

安装后的用户无需 pip、CUDA 或单独下载 DirectML。仍需正常的 Windows
DirectX 12 显卡驱动。GPU 不可用时保留 CPU 回退。

依赖位于 `VisionRuntime/gpu-packages`，在配置和嵌入式 Python 的 `_pth`
中优先于原有 site-packages。这样不会修改构建所用原始 runtime，也不会让旧的
CPU 版 onnxruntime 抢先加载。升级安装会清理这个专用目录，避免旧 DLL 残留。
依赖 wheel 的许可证和第三方声明随目录保留，并生成文件 SHA256 清单。

构建机需要有 pip 的 Python；软件内置的 Python 不需要 pip。完整 runtime
仍需提供已有的 NumPy、protobuf、torch、OpenCV、RapidOCR 等依赖。
打包脚本会在复制好的 Windows Python 中导入这些模块，检查 DirectML provider、
实际导入路径，并运行一个 CPU ONNX 模型；任一检查失败就阻止生成安装包。
检查报告位于 `VisionRuntime/gpu-validation.json`。此检查不代表 GPU 真机测试。

使用原来的打包命令即可；联网下载只发生在构建阶段。离线构建可先准备 wheel：

```powershell
python -m pip download --only-binary=:all: --no-deps --platform win_amd64 --python-version 3.12 --implementation cp --abi cp312 --dest wheels onnxruntime-directml==1.24.4 onnx==1.17.0
python tools/Packaging/package_release.py --payload-dir C:\FieldTraderRuntime --inno-compiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' --wheelhouse wheels
```

当前目标固定为现有包使用的 Windows x64 / CPython 3.12。如升级 Python，必须
同时更新 wheel 目标与 `_pth` 配置。旧版已发布安装包不会自动获得这些依赖，
需重新构建并安装新包。源码单独运行也需使用含 DirectML 的视觉 Python 环境。

YOLO 首次加载会把已校验的本地 `.pt` 导出为静态 FP32、640 输入、opset17 ONNX，
缓存在 `CacheDirectory`，按源权重 SHA256 区分，并验证缓存 ONNX 哈希。
首次启动较慢；后续复用缓存。原始权重不被修改。发布前可用完整模型预热缓存。

多显卡电脑可修改 `GpuDeviceId` 为对应 DirectML 适配器索引（从 0 开始）。
索引 0 不保证是独立显卡；9700X 的集显也可能被枚举。应在 Windows 任务管理器
检查实际 GPU 使用率并选择 7800 XT 或 NVIDIA 独显的索引。
强制 CPU 可设置 `InferenceMode: "CpuOnly"`，或环境变量 `FIELDTRADER_FORCE_CPU=1`。

识别结果的 `InferenceDevices` 分别记录 OCR、YOLO、ItemYOLO 的实际 provider、
设备索引或回退原因；`Recognizer` 同时附带这些信息，宿主诊断记录设备变化。
选择 DirectML 不代表已经测得加速效果，需测量完整截图识别耗时。

## 验证范围

Mac 上通过模拟 GPU 成功、初始化失败、运行失败、会话内不重复尝试、CPU 异常传播、
OCR 局部识别回退、DirectML 会话选项、YOLO 类别 NMS 与坐标还原测试。
Windows AMD/NVIDIA 真机加载、两套完整模型的 CPU/GPU 检测一致性、DPI/截图导航、
性能与显存占用均待验证；真实操作仍只限获准的本地测试界面。

运行测试：`python -m unittest discover -s tests -p test_inference_backend.py -v`。
