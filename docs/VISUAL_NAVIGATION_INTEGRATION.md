# 视觉导航集成

本轮沿用 be03c67 导航核心及市场费用/OCR未提交成果，工作分支 codex/visual-navigation-e2e。未接入游戏输入和真实交易。

## 六个环节

IScreenSource → CapturedScreen（FrameId/时间/HWND/客户区物理坐标/尺寸/hash）→ IVisionPipeline → VisionResult（原始YOLO/OCR、识别器来源、分段性能及区域内容指纹）→ IPageClassifier → PageObservation（页型、规则置信指标、错误、布局派生区域及来源）→ VisualTargetResolver.ResolveDetailed → NavigationPlanner/BatchActionPlanner → NavigationExecutor → IInputController → NavigationVerifier.Before/After。

同一目标的YOLO、OCR和FrameId必须一致。VisualTarget点击坐标是视觉区域中心经客户区原点转换后的物理桌面坐标；布局适配区域明确标LayoutRule，不称为新YOLO类别。UNKNOWN/AMBIGUOUS均不发出输入。

## WPF入口

现有市场巡查窗口第四个Tab“视觉导航调试”。默认只读；历史/游戏来源只允许只读。模拟和真实输入仅能选择本地测试窗口；真实模式另需勾选授权。

本地窗口登记在LocalNavigationWindows，原生输入每次检查登记、进程、前台、客户区、帧时效及互斥所有权。测试中失焦后不会自动夺回焦点；BringLocalForeground只在新测试准备时激活自建窗口。

暂停、取消、急停在操作边界生效；急停锁存后须明确新建会话。恢复采用新窗口/新截图重跑，不复用旧坐标。稳定批次每次动作仍独立检查边界；动态布局后重新定位。滚动只接受目标视口内容变化或新结果锚点，其他像素变化不算成功。

## 命令行验收

- FieldTrader --visual-navigation-replay <vision-config> <images> <output>：真实模型、文件回放、零输入。
- FieldTrader --visual-navigation-integration-test <output>：本地识别/模拟/真实三模式，明确使用截图像素夹具识别器，不是YOLO/OCR。
- FieldTrader --visual-navigation-local-test <output>：24个既有/扩展本地场景，每场景3次，真实SendInput。
- FieldTrader --visual-navigation-cancel-test <output>：模拟模式验证UI同源CancellationToken绑定，零原生输入。
- FieldTrader --visual-navigation-overlays <replay-output> <output>：生成只读定位可视化。

原tools/NavigationLab入口仍保留；实现移动到主工程Windows/NavigationLab供WPF复用，避免两套状态机。

## 范围

大厅底栏、联系人行与市场左栏采用可追溯的截图布局适配；原始YOLO/OCR不被改写。仓库逐物品框没有现成14类检测器支持，名称只能保留候选，不能据此点击。联系人列表到已选联系人可规划，但“购买”商店入口与真正购买提交尚未拆成独立安全角色；当前保守禁用，不声称完整联系人→兑换路线验收成功。

会话内worker只启动一次；全屏OCR一次，通知/输入/材料等局部OCR另计。已计入此前漏算的ROI耗时，只跳过全文已唯一且高置信读出的冗余卡片复读，不改置信门槛、权重或依赖。
