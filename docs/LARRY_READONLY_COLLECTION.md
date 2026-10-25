# 拉里只读采集

继承 `codex/visual-navigation-e2e` 的 `f63fec5`，不切换 main。沿用现有截图、持久视觉 worker、规划器、执行器和校验器；未修改模型或音频。

## 页面与数据流

`IScreenSource → CapturedScreen → PythonVisionPipeline → 原始YOLO/OCR → LarryPageClassifier → PageObservation → LarryRoutes → NavigationPlanner → NavigationExecutor → 新截图 → NavigationVerifier → LarryDetailReader → MarketDatabase.StoreLarry → Cancel → ContactShop`。

通用状态名保持兼容：`Home` 是大厅；拉里流程细分 `ContactList → ContactProfile → ContactShop → Exchange`，其中 `Exchange` 对应兑换详情。未知页、安全提示、意外确认框暂停。每次分类/商品/滚动/取消均为独立动态步骤，重新识别后验证结果。

联系人行按实际 OCR 名称的纵向位置定位，不依赖第二行。资料页右侧独立出现拉里姓名，才允许唯一的 `contact_shop_entry / 购买` 导航角色；只对 `larry:shop / ContactProfile → ContactShop` 开例外。其余购买、补齐、兑换、确认、出售、上架全部拒绝。

原始模型输出不可改写；同位置同文字的全图/ROI重复 OCR 在分类器副本中去重，原始数据通过 `*-raw.json` 保存。布局框始终注明 `LayoutRule ... NOT YOLO`。商品点击必须关联原有 YOLO product_card 和高置信 OCR，不接受单独文本中心。分类选中状态来自原图橙色像素测量，和新帧页面分类一起验证。

`UseCurrentFrameForInput` 是拉里逐步模式：旧计划只比较页面、窗口、布局和目标边界是否一致；随后从操作前的新截图重新定位并生成新的输入目标。真实 Windows 输入的5秒时效不变；旧目标不会换个 FrameId 后冒充新目标。默认稳定批次行为不变。

## 价格口径（用户最新确认）

- 合计价格是材料经济总成本，包含已拥有材料。
- 产品市场价是预计出售价格参考，不是实际出售成交价。
- 参考毛差价 = 产品市场价 − 合计价格。
- 补齐价格独立表示新增现金支出报价，尚未付款。
- 没有可靠成交卖价及实际费用时净利润为 `NotAvailable`，不套8%冒充已确认净利润。

默认按总价采集后取消返回，材料列表不完整不会阻塞主线。可选 `collectFullMaterials` 启用有界材料扫描；重复行按名称聚合，同时保留原始行及截图。重叠帧取同时可见行数的下界，不能确定完整时仍保持 Partial。价格变化或重复视口停止，不推算缺失单价。

七商品定义位于 `LarryTargets`。四种 Daily 商品的剩余次数必须读界面，不能凭本地日期计算；手雷为 RefreshWindow，配方无证据保持 RecipeUnknown，倒计时只取截图值。

数据保存在现有 MarketDatabase 同库的 ContactCollections 表，和采购/出售参考价及执行账本隔离。SessionId/ItemId/ContentHash/Replay 唯一，重复同图不增加记录；不同真实价格引起的截图变化可保存。不会写入真实支付、库存或交易资格。

## WPF 调试

运行现有 FieldTrader `--market-readonly`，打开第四页视觉导航调试，目标选 Larry，输入七种准确商品名之一。历史截图或游戏当前页仅识别、采集和生成计划，不发送输入。来源选 WPF 本地测试窗口时，选择包含 S01.png～S18.png 的截图目录；真实模式仍需显式勾选授权。

命令行：

```
MarketReplay --larry-replay <vision-config> <screens> <output>
MarketReplay --larry-check <replay.json> <output>
FieldTrader --larry-local-test <vision-config> <screens> <output>
FieldTrader --larry-local-simulated <vision-config> <screens> <output>
FieldTrader --larry-local-categories <vision-config> <screens> <output>
```

本地测试是交互截图窗口：真实原始 YOLO/OCR 读取新截图，页面切换和滚动视图由测试场景编排；滚动视图使用真实商品裁剪图。必须与真实游戏导航区分。原生输入仅可到本进程登记窗口，切走/移动/过期/暂停/急停会停止。游戏输入没有开放，交易开关没有修改。

已知缺口：MK II 在现有完整页被 OCR 读成 MKI，可靠名称不足时拒绝；两种手雷缺兑换详情，不能确认配方和产品参考价。TMK列表底部不完整，仍可按三种总金额完成主线。独立新游戏会话、不同缩放、真正游戏滚动和游戏输入均未验收。
