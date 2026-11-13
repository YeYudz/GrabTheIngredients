# GrabTheIngredients 性能优化记录（Phase 1 · 11.13）

## 优化前 vs 优化后 A/B 对比操作步骤

优化改动涉及 7 个文件，已另存备份补丁：`.workbuddy/perf_optimization_backup.patch`。

**「优化前」一轮（还原旧代码）：**

```bash
cd E:/Unity/Project/GrabTheIngredients
git stash push -m "perf-opt" -- Assets/Scripts/Combat/Health.cs Assets/Scripts/Combat/PlayerEffectController.cs Assets/Scripts/Core/AchievementManager.cs Assets/Scripts/Drop/DropItem.cs Assets/Scripts/Pool/PoolPrewarmer.cs Assets/Scripts/UI/HudPanel/HudPanel.cs
```

**「优化后」一轮（恢复新代码）：**

```bash
git stash pop
```

> 万一 stash pop 出冲突/丢失：`git apply .workbuddy/perf_optimization_backup.patch` 可重新打上全部优化。

**测试纪律（保证对比公平）：**
1. 两轮用**同一副本、同一波次、同一时长（60s）、同一分辨率**
2. 可用 GM 控制台（Development 构建下）直接跳到 wave 13，等刷怪稳定后再开始记录
3. 关闭 Unity 里其他占 CPU 的窗口；每轮开始前让 Profiler 深度分析跑一遍（Deep Profile 会失真，建议关掉用普通模式）
4. Editor 里测只是初稿数字，最终写进简历/笔记的数字建议用 **Development Build** 实测

---

> 用法：打开 Unity Profiler（Window → Analysis → Profiler），勾选 Development Build 或直接在 Editor 里跑。
> **用同一场景、同一波次、同一操作流程**各录 60 秒，把数字填进下表。
> ⚠️ Editor 里 EventKit/LogKit 的开销会虚高（Editor-only 分支），**最终结论建议用 Development Build 实测**。

## 场景：wave 8~13 高峰期（同屏 70 敌人 + 大量掉落物）

| 指标 | 优化前 | 优化后 | 备注 |
|---|---|---|---|
| 帧基线 / 尖峰 | **基线 16.7ms（vsync 60）+ 频繁多根尖峰**（击杀密集段连续出现） | **基线 ~5ms（211 FPS）+ 尖峰变稀变矮**（多数 ≤10ms，偶有更高） | 两轮 vsync 状态不同，基线 ms 不可直接比；尖峰频率/高度是有效对比 |
| GC Alloc / 帧 (B) | **Memory 模块周期性密集红色尖峰** | **稀疏少量尖峰**（密集→稀疏，改善明确） | 下轮可开 Show GC Alloc 采精确值 |
| 主线程最长帧尖峰 (ms) | 频繁多根（击杀密集段） | 明显减少 | |
| Canvas.BuildBatch 耗时 (ms) | 待补充证据帧 | 平时不再持续出现；尖峰帧 11.0ms | |
| Physics 2D Awake Bodies | 锯齿状 | 锯齿状（两轮相似） | **更正**：锯齿=怪物从池取出时物理体唤醒（刷怪本身），不是池扩容证据；池扩容要看 Instantiate 调用 |
| Batches / Tris | 211 / 18.0k | 207 / 17.7k | 场景内容接近，两轮基本一致 ✓ |
| **SetPass Calls** | 17（Tris 18.0k，敌种少） | **193（Tris 17.7k）→ 297（Tris 26.7k，混编后期波）** | 随敌人种类混编度暴涨，疑为无 Atlas 结构问题，见下方专项分析 |

## SetPass 17 → 193/297 异常分析

三个采样点：优化前 Tris 18.0k → SetPass **17**；优化后 Tris 17.7k → SetPass **193**；优化后 Tris 26.7k → SetPass **297**（Batches 329，几乎每批一次状态切换）。

**本轮优化没有改动任何渲染/材质代码**，且第二次复测仍高 → 排除「恰好截到闪白瞬间」的偶然解释。当前最大嫌疑是 **P1-16 无 SpriteAtlas 的结构性问题在后期波次的必然表现**：

- 敌人/精英/掉落物是 **5 套独立大图**（1792×1792~2387，无图集）
- 相机开启 CustomAxis Y 排序 → 不同贴图的怪物在渲染顺序上**交错**
- 后期波次同屏混编 5 种敌人 → 每换一次贴图就是一次 SetPass → 混编越多 SetPass 越高
- 优化前那张 17 SetPass 的截图，Tris 只有 18.0k——很可能是敌人种类少的早期时刻（如 wave 1 纯史莱姆，单一贴图 → SetPass 天然低）

**定论方法（二选一，1 分钟）：**
1. 用优化后版本打一局 **wave 1**（纯史莱姆），截 Stats——如果 SetPass 回落到两位数，即证明 SetPass 随「敌人种类混编度」增长、与优化代码无关
2. Frame Debugger（Window → Analysis → Frame Debugger）在高 SetPass 帧看：是不是史莱姆/哥布林/蘑菇/精英的贴图在反复切换

**顺带的结论**：这正好是「做 SpriteAtlas」收益的直接证据——合并贴图后 SetPass 预期能从 ~300 降到几十，这是下一个优化项的最大单点收益。

## 测试方法备注（下次修正）

- 两轮的 **VSync 状态不一致**（一轮锁 60FPS、一轮 200FPS）：请固定 Project Settings → Quality → VSync Count，否则基线帧耗时没有可比性
- Physics2D「Awake Bodies」锯齿是刷怪活动的正常表现，不代表性能问题

## 本轮已做的代码优化（2026-11-13）

| # | 问题 | 修改位置 | 预期收益 |
|---|---|---|---|
| 1 | 每次击杀触发成就进度 → **同步落盘**（备份拷贝+校验码+全量JSON+IO），割草期周期性卡顿元凶 | `Core/AchievementManager.cs`：进度变化改为脏标记，最长 10s 合批落盘；解锁/暂停/退出立即落盘 | 消除每杀一次磁盘写+序列化尖峰 |
| 2 | 每次扣血/死亡广播都 `GetComponent<PlayerController/EnemySimple>`（近战每帧数十目标） | `Combat/Health.cs`：Awake 缓存身份引用 | 命中热路径零 GetComponent |
| 3 | 每次命中对每个条件效果 `enum.ToString()` + 字符串比较 + 高频日志 | `Combat/PlayerEffectController.cs`：TriggerEvent 改枚举比较，删除每次触发日志 | 消除每次命中字符串分配 |
| 4 | 每次爆炸/找最近敌人 `new List<EnemySimple>()`；效果弹每次 `GetComponent<AutoAttack>` | 同上：AcquireQueryBuffer 复用缓冲（带重入保护）+ 缓存 AutoAttack | AOE/飞弹路径零分配 |
| 5 | 每个掉落物生成都 `Resources.Load` + 路径拼接（一局 5000+ 次）；拾取时 GetComponent | `Drop/DropItem.cs`：静态 Sprite 缓存 + Poolable 缓存 | 掉落高频路径零 IO 查询 |
| 6 | 对象池只按第一波规模预热（50 怪/约 33 掉落），后期波次刷怪高峰仍要 Instantiate | `Pool/PoolPrewarmer.cs`：敌人按全局同屏上限（maxAlive=70）等比放大，掉落物下限 70 个 | 中后期波次开局无池扩容抖动 |
| 7 | HUD 每帧重建 Canvas：指数 lerp 永不到达 → fillAmount 每帧微变 SetVerticesDirty；计时器/冷却数字每帧写新字符串；每帧 GetComponent | `UI/HudPanel/HudPanel.cs`：收敛吸附 + 秒数变化才写 text + 缓存 DodgeSkill/PlayerEffectController | 静止时 HUD 零重建 |

## 本轮已定位、留待后续（按优先级）

1. **合批结构性问题**：全工程无 SpriteAtlas（233 张独立贴图）+ 相机 CustomAxis Y 排序 → DrawCall 随敌人/掉落数线性上升。改法：给敌人/掉落/食材建 SpriteAtlas V2（需要 Unity 编辑器操作，代码改不了）。
2. **70 个动态刚体互相碰撞**：物理层 `OnCollisionStay2D` 与逻辑层 SpatialGrid 分离重复做工。可试：敌人改 Kinematic 或敌人间不互碰，Profiler 验证后决定。
3. **音游面板**：每个音符 new GameObject + 每帧 GetComponent<TrapezoidImage> + SetVerticesDirty（`UI/CookingPanel/CookingPanel.cs:746,767`）。
4. **每次 WaveStarted 全量断点落盘**（`SaveSystem/RunSaveManager.cs:49`）：一局 14~28 次，每次 1~5ms GC+IO 尖峰，可移到波次准备期分帧序列化。
5. **掉落物无生命周期上限**：每杀 2~3 掉落只有 1.2m 内才回收，后期数百个滞留。改自动合并/超时自动回收（涉及玩法取舍，需你拍板）。

## Profiler 验证顺序建议

1. GC Alloc 峰值帧：`AchievementManager.UpdateCondition` / `PlayerEffectController.TriggerEvent`
2. `Canvas.BuildBatch` 占比：验证 HUD 优化（找一段无战斗静止画面，优化前应持续有重建）
3. `Physics2D.Simulate`：改 Kinematic 做 A/B
4. `Batches` 曲线：wave 8~13 vs 满屏掉落对比
5. Timeline 上 `File.Write` / `Resources.Load` 尖刺是否消失
