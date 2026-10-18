using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class ShopPanelData : IUIData
	{
	}

	/// <summary>
	/// 商店面板：局外资产的出售与购买。
	/// 纯显示层——读写全部委托 MetaManager，面板只做展示与操作上报，使存档与 UI 共享同一数据源。
	/// 出售与购买复用同一网格，靠 TradeMode 切换筛选条件与按钮语义，避免维护两套近乎相同的列表逻辑。
	/// </summary>
	public partial class ShopPanel : UIPanel
	{
		/// <summary>交易类型</summary>
		private enum TradeMode { Sell, Buy }

		/// <summary>商品类型</summary>
		private enum GoodType { Ingredient, Dish, Weapon, Character, Recipe, Skill }

		/// <summary>商店上架配置的类型键（MetaShopConfig.type）</summary>
		private const string k_shopTypeCharacter = "Character";
		private const string k_shopTypeWeapon = "Weapon";
		private const string k_shopTypeRecipe = "Recipe";
		private const string k_shopTypeSkill = "Skill";

		/// <summary>商品（选中项快照：静态信息，数量实时从 MetaManager 读取）</summary>
		private struct Good
		{
			public GoodType type;    // 商品类型
			public string id;        // 配置ID
			public int star;         // 菜品星级（非菜品 = 0）
			public string name;      // 显示名
			public string desc;      // 描述
			public string iconPath;  // 图标 Resources 路径（菜品暂无图标 = 空）
			public int unitPrice;    // 出售单价 / 购买价
			public bool owned;       // 购买页：是否已拥有/已解锁（唯一物品已拥有不显示；菜品可重复购买）
		}

		/// <summary>购买筛选：由 MetaShopConfig 接管时按上架白名单 + 价格覆盖；未接管则放行全部</summary>
		private struct BuySelection
		{
			private bool mActive;
			private Dictionary<string, MetaShopEntry> mEntries;

			/// <summary>是否由上架表接管（接管后未上架的商品一律不显示）</summary>
			public bool Active => mActive;

			public bool Allows(string id)
			{
				return !mActive || (mEntries != null && mEntries.ContainsKey(id));
			}

			public int PriceOf(string id, int fallbackPrice)
			{
				if (mActive && mEntries != null
					&& mEntries.TryGetValue(id, out var e) && e.price > 0)
				{
					return e.price;
				}
				return fallbackPrice;
			}

			/// <summary>
			/// 构建某类型的购买筛选：
			/// - 上架表为空 → 不接管（回退旧的"全量扫描 buyPrice"逻辑，保持兼容）
			/// - 上架表非空 → 全局接管；本类型无上架项（或全部下架）时白名单为空，该类型不再显示任何商品
			/// </summary>
			public static BuySelection From(MetaShopConfigWrapper shop, string type)
			{
				var sel = new BuySelection();
				if (shop == null || shop.Count == 0) return sel;   // 上架表为空 → 不接管

				sel.mActive = true;
				sel.mEntries = new Dictionary<string, MetaShopEntry>();

				var entries = shop.GetByType(type);
				if (entries == null) return sel;                   // 本类型无上架项 → 空白名单（不上架）

				foreach (var e in entries)
				{
					if (e == null || string.IsNullOrEmpty(e.id) || !e.onSale) continue;
					sel.mEntries[e.id] = e;
				}
				return sel;
			}
		}

		private TradeMode mMode = TradeMode.Sell;
		private GoodType? mFilter;                          // 商品分类过滤（null=全部）
		private Good? mSelected;                       // 当前选中商品（null = 未选中）
		private int mCurrentCount = 1;                 // 当前可交易数量（滑块驱动，保留计算值）

		private readonly List<GameObject> mDynamicItems = new();

		// ===== 交互动画（UiTween，见 UI/Common/UiTween.cs） =====

		/// <summary>详情区展开时的横向位移（像素），收起时反向弹出</summary>
		private const float k_detailSlide = 26f;

		private const float k_detailInDuration = 0.18f;
		private const float k_detailOutDuration = 0.12f;

		private Coroutine mDetailTween;                // 详情区展开 / 收起
		private Coroutine mGridTween;                  // 商品网格错峰弹入
		private Coroutine mCoinTween;                  // 金币数值脉冲

		private CanvasGroup mDetailGroupAlpha;
		private CanvasGroup mGridGroup;
		private RectTransform mDetailRect;
		private RectTransform mCoinRect;
		private Vector2 mDetailHome;                   // 详情区原始 anchoredPosition（展开/收起都以它为基准）
		private bool mDetailHomeValid;
		private bool mAnimateGridNextRefresh;          // 下次重建网格时播放弹入（仅模式/分类切换，选中重绘不播）

		protected override void OnInit(IUIData data = null)
		{
			mData = data as ShopPanelData ?? new ShopPanelData();
			ResolveRefs();
			ResolveTweenRefs();
			BindEvents();
			// 初始化直接隐藏详情区（面板还没露脸，不需要播收起动画）
			if (mDetailGroup != null) mDetailGroup.SetActive(false);
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as ShopPanelData ?? mData;
			ResetTradeState();
			mAnimateGridNextRefresh = true;   // 打开时商品列表整体弹入一次
			RefreshGrid();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);

		protected override void OnHide() { }

		protected override void OnClose()
		{
			// 先停动画再销毁格子：动画协程里持有格子引用（已做 null 判断，这里少跑一帧更干净）
			UiTween.Stop(this, ref mDetailTween);
			UiTween.Stop(this, ref mDetailTween);
			UiTween.Stop(this, ref mGridTween);
			UiTween.Stop(this, ref mCoinTween);

			ClearDynamic();
			mSelected = null;
		}

		// ===== 引用解析（Inspector 已绑定的优先，缺失则按节点名查找） =====

		private void ResolveRefs()
		{
			if (mShopMode == null) mShopMode = FindComponent<ToggleGroup>("ShopMode");
			if (mCategoryGroup == null) mCategoryGroup = FindComponent<ToggleGroup>("ToggleGroup");

			if (mGridContent == null)
			{
				var grid = FindChild(transform, "GridGroup");
				if (grid != null)
				{
					var viewport = FindChild(grid, "Viewport");
					var content = viewport != null ? FindChild(viewport, "Content") : null;
					mGridContent = content ?? grid;
				}
			}
			if (mGridTemplate == null && mGridContent != null)
			{
				var tpl = FindChild(mGridContent, "ShopGrid");
				// 名称未约定时退化为首个子节点：兼容嵌套在 Content 下的模板
				if (tpl == null && mGridContent.childCount > 0)
					tpl = mGridContent.GetChild(0);
				if (tpl != null) mGridTemplate = tpl.gameObject;
			}
			// 母版仅作克隆源：运行时隐藏，不参与网格显示（实例克隆后单独激活）
			if (mGridTemplate != null) mGridTemplate.SetActive(false);

			if (mDetailGroup == null)
			{
				var detail = FindChild(transform, "DetailGroup");
				if (detail != null) mDetailGroup = detail.gameObject;
			}
			if (mDetailIcon == null) mDetailIcon = FindComponent<Image>("DetailIcon");
			if (mDetailNameText == null) mDetailNameText = FindComponent<Text>("NameText");
			if (mDetailDescText == null) mDetailDescText = FindComponent<Text>("DescText");
			if (mCountBack == null) mCountBack = FindComponent<Scrollbar>("CountBack");
			if (mCountText == null && mCountBack != null)
			{
				// CountText 是 CountBack 滑块的子物体，按名查找
				var ct = FindChild(mCountBack.transform, "CountText");
				if (ct != null) mCountText = ct.GetComponent<Text>();
			}
			if (mActionButton == null) mActionButton = FindComponent<Button>("ActionButton");

			if (mCoinText == null)
			{
				var coinBack = FindChild(transform, "CoinBack");
				if (coinBack != null)
					mCoinText = coinBack.GetComponentInChildren<Text>();
			}
			if (mBackButton == null) mBackButton = FindComponent<Button>("BackButton");

			if (mGridContent == null) LogKit.Warning("[ShopPanel] 未找到 GridGroup/Content，无法显示商品网格");
		}

		/// <summary>动画所需的附加引用：CanvasGroup / 原始坐标。预制体不用改，缺什么运行时补什么。</summary>
		private void ResolveTweenRefs()
		{
			if (mDetailGroup != null)
			{
				mDetailRect = mDetailGroup.transform as RectTransform;
				mDetailGroupAlpha = UiTween.EnsureCanvasGroup(mDetailGroup);
				if (mDetailRect != null)
				{
					mDetailHome = mDetailRect.anchoredPosition;
					mDetailHomeValid = true;
				}
			}

			var coinBack = FindChild(transform, "CoinBack");
			if (coinBack != null) mCoinRect = coinBack as RectTransform;
		}

		// ===== 交互动画 =====
		// 面板整体打开动画走公共的 UiPanelTween.PlayIn（根节点淡入 + ShopWindow 缩放弹入），这里只管面板内元素。

		/// <summary>商品网格重建后错峰弹入（只在切换模式 / 分类 / 打开面板时播放）</summary>
		private void PlayGridIn()
		{
			if (mGridContent == null) return;
			if (mGridGroup == null) mGridGroup = UiTween.EnsureCanvasGroup(mGridContent.gameObject);
			UiTween.Play(this, ref mGridTween, UiTween.StaggerPop(mDynamicItems, mGridGroup));
		}

		/// <summary>金币变化时轻微放大回弹，给出"钱到账 / 扣钱"的视觉反馈</summary>
		private void PlayCoinPulse()
		{
			if (mCoinRect == null) return;
			UiTween.Play(this, ref mCoinTween, UiTween.Pulse(mCoinRect, 1.12f, 0.24f));
		}

		// ===== 事件绑定 =====

		private void BindEvents()
		{
			// 模式切换：ToggleSell / ToggleBuy
			var sellTg = FindToggle(mShopMode, "ToggleSell");
			if (sellTg != null)
			{
				sellTg.onValueChanged.RemoveAllListeners();
				sellTg.onValueChanged.AddListener(on => { if (on) SetMode(TradeMode.Sell); });
			}
			var buyTg = FindToggle(mShopMode, "ToggleBuy");
			if (buyTg != null)
			{
				buyTg.onValueChanged.RemoveAllListeners();
				buyTg.onValueChanged.AddListener(on => { if (on) SetMode(TradeMode.Buy); });
			}

			// 分类切换：ToggleAll / ToggleCharacter / ToggleWeapon / ToggleDish(菜品·菜谱) / ToggleSkill / ToggleIngredient(食材)
			BindCategoryToggle("ToggleAll", null);
			BindCategoryToggle("ToggleCharacter", GoodType.Character);
			BindCategoryToggle("ToggleWeapon", GoodType.Weapon);
			BindCategoryToggle("ToggleDish", GoodType.Dish);
			BindCategoryToggle("ToggleSkill", GoodType.Skill);
			BindCategoryToggle("ToggleIngredient", GoodType.Ingredient);

			// 数量滑块
			if (mCountBack != null)
			{
				mCountBack.onValueChanged.RemoveAllListeners();
				mCountBack.onValueChanged.AddListener(_ => RefreshCountText());
			}

			// 操作按钮
			if (mActionButton != null)
			{
				mActionButton.onClick.RemoveAllListeners();
				mActionButton.onClick.AddListener(OnAction);
				mActionButton.interactable = false;
			}

			// 返回
			if (mBackButton != null)
			{
				mBackButton.onClick.RemoveAllListeners();
				mBackButton.onClick.AddListener(OnBack);
			}
		}

		/// <summary>绑定单个分类 Toggle（goodType 为 null 表示"全部/菜品菜谱"复合分类）</summary>
		private void BindCategoryToggle(string toggleName, GoodType? goodType)
		{
			var tg = FindToggle(mCategoryGroup, toggleName);
			if (tg == null) return;
			tg.onValueChanged.RemoveAllListeners();
			tg.onValueChanged.AddListener(on =>
			{
				if (on)
				{
					mFilter = goodType;
					ClearSelection();
					mAnimateGridNextRefresh = true;
					RefreshGrid();
				}
			});
		}

		private static Toggle FindToggle(ToggleGroup group, string name)
		{
			if (group == null) return null;
			foreach (var t in group.GetComponentsInChildren<Toggle>(true))
				if (t.name == name) return t;
			return null;
		}

		// ===== 打开默认 =====

		private void ResetTradeState()
		{
			mMode = TradeMode.Sell;
			mFilter = null;
			ClearSelection();

			var sellTg = FindToggle(mShopMode, "ToggleSell");
			if (sellTg != null) sellTg.isOn = true;
			var allTg = FindToggle(mCategoryGroup, "ToggleAll");
			if (allTg != null) allTg.isOn = true;

			SetActionLabel();
		}

		// ===== 模式 / 过滤切换 =====

		private void SetMode(TradeMode mode)
		{
			if (mMode == mode) return;
			mMode = mode;
			SetActionLabel();
			ClearSelection();
			mAnimateGridNextRefresh = true;
			RefreshGrid();
		}

		private void SetActionLabel()
		{
			if (mActionButton == null) return;
			var txt = mActionButton.GetComponentInChildren<Text>();
			if (txt == null) return;
			if (mMode == TradeMode.Sell)
				txt.text = "出售";
			else
			{
				// 唯一物品已拥有 → "已拥有"；菜品已解锁仍可重复购买 → "购买"
				bool ownedBlocked = mSelected != null && mSelected.Value.owned
					&& !IsRepeatablePurchase(mSelected.Value.type);
				txt.text = ownedBlocked ? "已拥有" : "购买";
			}
		}

		// ===== 商品列表构建 =====

		private void RefreshGrid()
		{
			// 不需要播弹入时先把网格透明度复位：上一段动画若被中途打断，alpha 会停在中间值
			if (!mAnimateGridNextRefresh && mGridGroup != null)
			{
				UiTween.Stop(this, ref mGridTween);
				mGridGroup.alpha = 1f;
			}

			ClearListChildren();

			if (mCoinText != null && MetaManager.HasInstance)
				mCoinText.text = $"钱币: {MetaManager.Instance.Coins}";

			if (!MetaManager.HasInstance) { ClearSelection(); return; }

			var goods = BuildGoodsList();

			// 重新校验选中项是否仍存在（出售后归零 / 购买后已拥有 → 清空选中）
			if (mSelected != null && !goods.Exists(g => SameGood(g, mSelected.Value)))
			{
				ClearSelection();
			}

			foreach (var good in goods)
			{
				bool selected = mSelected != null && SameGood(good, mSelected.Value);
				CreateCell(good, selected);
			}

			if (goods.Count == 0)
				CreateEmptyHint("  该分类暂无物品");

			if (mSelected != null)
			{
				UpdateInfo();
				// 出售后最大可操作数量可能变化，同步滑块档位
				ApplySliderSteps(GetMaxCount(mSelected.Value));
				RefreshCountText();
			}

			// 格子是运行时 new 出来的 Button，重建后补挂悬停缩放（此刻 scale 仍是 1，基准记录准确）
			UiHoverInstaller.Install(mGridContent != null ? mGridContent.gameObject : gameObject);

			// 只在"换了一批商品"时播弹入；选中高亮重绘（RefreshGrid 的另一种调用来源）不播，否则每次点格子都闪
			if (mAnimateGridNextRefresh)
			{
				mAnimateGridNextRefresh = false;
				PlayGridIn();
			}
		}

		private List<Good> BuildGoodsList()
		{
			var list = new List<Good>();
			if (mMode == TradeMode.Sell) BuildSellGoods(list);
			else BuildBuyGoods(list);
			return list;
		}

		/// <summary>出售列表：局外资产（食材/成品菜/武器；角色/技能不可出售）</summary>
		private void BuildSellGoods(List<Good> list)
		{
			var meta = MetaManager.Instance;

			// 食材
			if (mFilter == null || mFilter == GoodType.Ingredient)
			{
				var backpack = meta.Data.ingredientBackpack;
				if (backpack != null)
				{
					foreach (var s in backpack)
					{
						if (s == null || s.count <= 0) continue;
						var cfg = GameDataMgr.Instance.GetIngredient(s.id);
						if (cfg == null) continue;
						list.Add(new Good
						{
							type = GoodType.Ingredient, id = s.id, star = 0,
							name = cfg.ingredientName, desc = cfg.ingredientName,
							iconPath = cfg.iconPath, unitPrice = cfg.sellPrice
						});
					}
				}
			}

			// 成品菜
			if (mFilter == null || mFilter == GoodType.Dish)
			{
				var dishes = meta.GetDishInventory();
				var seen = new HashSet<string>();
				foreach (var d in dishes)
				{
					if (d == null) continue;
					string key = d.recipeId + "_" + d.star;
					if (!seen.Add(key)) continue;
					if (meta.GetDishCount(d.recipeId, d.star) <= 0) continue;
					var recipe = GameDataMgr.Instance.GetRecipe(d.recipeId);
					if (recipe == null) continue;
					// 星级不再拼进菜名：星数单列在 Good.star，由格子 StarText 显示
					list.Add(new Good
					{
						type = GoodType.Dish, id = d.recipeId, star = d.star,
						name = recipe.recipeName, desc = recipe.description,
						iconPath = recipe.iconPath, unitPrice = recipe.sellPrice
					});
				}
			}

			// 武器
			if (mFilter == null || mFilter == GoodType.Weapon)
			{
				var weapons = GameDataMgr.Instance.Weapons?.Weapons;
				if (weapons != null)
				{
					foreach (var w in weapons)
					{
						if (w == null || meta.GetWeaponCount(w.id) <= 0) continue;
						list.Add(new Good
						{
							type = GoodType.Weapon, id = w.id, star = 0,
							name = w.weaponName, desc = w.description,
							iconPath = w.iconPath, unitPrice = w.sellPrice
						});
					}
				}
			}
		}

		/// <summary>购买列表：唯一物品（角色/武器/技能）只列未拥有（已拥有不显示）；
		/// 菜品（菜谱）同类可合成，已解锁仍可重复购买。
		/// 某类型若在 MetaShopConfig 有上架项 → 由该表接管（白名单 + 价格覆盖）；否则回退全量扫描</summary>
		private void BuildBuyGoods(List<Good> list)
		{
			var meta = MetaManager.Instance;
			var shop = GameDataMgr.Instance.GetMetaShop();

			if (mFilter == null || mFilter == GoodType.Character)
			{
				var sel = BuySelection.From(shop, k_shopTypeCharacter);
				var chars = GameDataMgr.Instance.Characters?.Characters;
				if (chars != null)
				{
					foreach (var c in chars)
					{
						if (c == null || !sel.Allows(c.id)) continue;
						bool owned = meta.HasCharacter(c.id);
						if (owned) continue;   // 已拥有 = 已解锁，购买页只卖未解锁的（不再"列出但不可购买"）
						int price = sel.PriceOf(c.id, c.buyPrice);
						if (price <= 0) continue;
						list.Add(new Good
						{
							type = GoodType.Character, id = c.id, star = 0,
							name = c.characterName, desc = c.description,
							iconPath = c.iconPath, unitPrice = price, owned = owned
						});
					}
				}
			}

			if (mFilter == null || mFilter == GoodType.Weapon)
			{
				var sel = BuySelection.From(shop, k_shopTypeWeapon);
				var weapons = GameDataMgr.Instance.Weapons?.Weapons;
				if (weapons != null)
				{
					foreach (var w in weapons)
					{
						if (w == null || !sel.Allows(w.id)) continue;
						// 武器唯一：已拥有 → 商品栏不显示、无法购买
						if (meta.GetWeaponCount(w.id) > 0) continue;
						int price = sel.PriceOf(w.id, w.buyPrice);
						if (price <= 0) continue;
						list.Add(new Good
						{
							type = GoodType.Weapon, id = w.id, star = 0,
							name = w.weaponName, desc = w.description,
							iconPath = w.iconPath, unitPrice = price, owned = false
						});
					}
				}
			}

			// 菜谱（购买）
			if (mFilter == null || mFilter == GoodType.Dish)
			{
				var sel = BuySelection.From(shop, k_shopTypeRecipe);
				var recipes = GameDataMgr.Instance.Recipes?.Recipes;
				if (recipes != null)
				{
					foreach (var r in recipes)
					{
						if (r == null || !sel.Allows(r.id)) continue;
						// 菜品同类可合成 → 已解锁也照常上架（owned 仅用于详情文案，不拦截购买）
						bool owned = meta.IsRecipeUnlocked(r.id);
						int price = sel.PriceOf(r.id, r.buyPrice);
						if (price <= 0) continue;
						list.Add(new Good
						{
							type = GoodType.Recipe, id = r.id, star = 0,
							name = r.recipeName, desc = r.description,
							iconPath = r.iconPath, unitPrice = price, owned = owned
						});
					}
				}
			}

			if (mFilter == null || mFilter == GoodType.Skill)
			{
				var sel = BuySelection.From(shop, k_shopTypeSkill);
				var skills = GameDataMgr.Instance.ActiveSkills?.Skills;
				if (skills != null)
				{
					foreach (var s in skills)
					{
						if (s == null || !sel.Allows(s.id)) continue;
						// 统一解锁口径（RosterManager.IsSkillAvailable）：已解锁（买过 / 共有池 / 角色附赠 / 已拥有武器主动）
						// 不再列出。角色专属技能不因"拥有角色"而解锁，所以它们会正常留在货架上等购买。
						bool owned = RosterManager.IsSkillAvailable(s.id);
						if (owned) continue;
						int price = sel.PriceOf(s.id, s.buyPrice);
						if (price <= 0) continue;
						list.Add(new Good
						{
							type = GoodType.Skill, id = s.id, star = 0,
							name = s.skillName, desc = BuildSkillDesc(s),
							iconPath = s.iconPath, unitPrice = price, owned = owned
						});
					}
				}
			}
		}

		/// <summary>
		/// 技能商品描述：角色专属技能在描述后补一句归属角色，避免玩家买到"当前角色装不上"的技能后困惑。
		/// 非角色专属（共有池 / 武器主动）不加后缀。
		/// </summary>
		private static string BuildSkillDesc(ActiveSkillConfig skill)
		{
			if (skill == null) return string.Empty;

			var owner = RosterManager.GetExclusiveOwner(skill.id);
			if (string.IsNullOrEmpty(owner)) return skill.description;

			var ownerName = GameDataMgr.Instance.GetCharacter(owner)?.characterName;
			return $"{skill.description}（{(string.IsNullOrEmpty(ownerName) ? owner : ownerName)}专属，仅该角色可装配）";
		}

		private static bool SameGood(Good a, Good b)
		{
			return a.type == b.type && a.id == b.id && a.star == b.star;
		}

		/// <summary>该类型商品是否可重复购买：菜品同类可合成（可堆叠耗材），已拥有仍可再买；
		/// 武器/角色/技能为唯一物品，已拥有即不可购买</summary>
		private static bool IsRepeatablePurchase(GoodType type)
		{
			return type == GoodType.Recipe;
		}

		// ===== 格子 =====

		private void CreateCell(Good good, bool selected)
		{
			GameObject go;
			if (mGridTemplate != null && mGridContent != null)
			{
				go = Instantiate(mGridTemplate, mGridContent, false);
				go.SetActive(true);
			}
			else
			{
				go = new GameObject("ShopCell", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
				var img = go.GetComponent<Image>();
				img.color = new Color(0.22f, 0.24f, 0.3f, 0.9f);
				go.GetComponent<Button>().targetGraphic = img;
			}

			// NameText：只写物品名 —— 购买页不再把价格拼进名称（价格改由 DetailGroup 的 DescText 展示）
			// 颜色：成品菜按稀有度着色，其余（含购买页全部商品）统一纯白（预制体默认是黑色，深色格子上看不清）
			var nameText = go.transform.Find("NameText")?.GetComponent<Text>();
			if (nameText != null)
			{
				nameText.text = good.name;
				nameText.color = good.type == GoodType.Dish
					? DishRarity.GetColor(good.star)
					: Color.white;
			}

			// StarText：菜品星级专用文本，格式 "★1" / "★2" / "★n"（与菜名分离显示）
			// 非菜品（食材/武器/角色/菜谱/技能）无星级 → 整节点隐藏，避免留下空文本
			var starTextT = go.transform.Find("StarText");
			if (starTextT != null)
			{
				bool showStar = good.type == GoodType.Dish && good.star > 0;
				starTextT.gameObject.SetActive(showStar);
				if (showStar)
				{
					var starText = starTextT.GetComponent<Text>();
					if (starText != null)
					{
						starText.text = $"★{good.star}";
						starText.color = DishRarity.GetColor(good.star);
					}
				}
				// 子节点不接收点击，避免遮挡/拦截格子的选中与长按
				foreach (var g in starTextT.GetComponentsInChildren<Graphic>(true))
					g.raycastTarget = false;
			}

			// CountText：可出售/购买的最大数量
			var countText = go.transform.Find("CountText")?.GetComponent<Text>();
			if (countText != null)
			{
				countText.text = mMode == TradeMode.Sell
					? $"可售 {GetOwnedCount(good)}"
					: "可购 1";
			}

			// Icon：物品图标（模板 Icon 子节点；路径为空/加载失败时隐藏）
			var icon = go.transform.Find("Icon")?.GetComponent<Image>();
			if (icon != null)
			{
				var sprite = string.IsNullOrEmpty(good.iconPath) ? null : Resources.Load<Sprite>(good.iconPath);
				icon.gameObject.SetActive(sprite != null);
				if (sprite != null)
				{
					icon.sprite = sprite;
					icon.color = Color.white;
				}
				icon.raycastTarget = false;
			}

			// Selected：选中高亮覆盖层（预制体默认隐藏，选中时显示）；子节点不接收点击
			var selectedMark = go.transform.Find("Selected");
			if (selectedMark != null)
			{
				selectedMark.gameObject.SetActive(selected);
				foreach (var g in selectedMark.GetComponentsInChildren<Graphic>(true))
					g.raycastTarget = false;
			}

			var bg = go.GetComponent<Image>();
			if (bg != null)
			{
				bg.color = selected
					? new Color(0.3f, 0.55f, 0.35f, 0.9f)
					: new Color(0.22f, 0.24f, 0.3f, 0.9f);
			}

			var btn = go.GetComponent<Button>();
			if (btn == null) btn = go.AddComponent<Button>();
			btn.onClick.RemoveAllListeners();
			btn.onClick.AddListener(() => SelectGood(good));

			// 长按：打开物品详情弹窗（单击语义保持不变，两者不冲突）
			var longPress = go.GetComponent<LongPressTrigger>();
			if (longPress == null) longPress = go.AddComponent<LongPressTrigger>();
			longPress.onLongPress = () => OpenGoodDetail(good);

			mDynamicItems.Add(go);
		}

		// ===== 选中 / 信息 / 数量 =====

		/// <summary>长按打开物品详情弹窗（按商品类型经 ItemDetailFactory 读取配置填充）</summary>
		private void OpenGoodDetail(Good good)
		{
			ItemDetailPanelData data = null;
			switch (good.type)
			{
				case GoodType.Ingredient: data = ItemDetailFactory.ForIngredient(good.id); break;
				case GoodType.Dish: data = ItemDetailFactory.ForDish(good.id, good.star); break;
				case GoodType.Weapon: data = ItemDetailFactory.ForWeapon(good.id); break;
				case GoodType.Character: data = ItemDetailFactory.ForCharacter(good.id); break;
				case GoodType.Recipe: data = ItemDetailFactory.ForDish(good.id); break;
				case GoodType.Skill: data = ItemDetailFactory.ForSkill(good.id); break;
			}
			if (data == null) return;
			UIKit.OpenPanel<ItemDetailPanel>(UILevel.Pop, data);
		}

		private void SelectGood(Good good)
		{
			mSelected = good;
			mCurrentCount = 1;
			// 先按最大可操作数量设置滑块离散档位（numberOfSteps = max-1），再归零
			ApplySliderSteps(GetMaxCount(good));
			if (mCountBack != null) mCountBack.value = 0f;
			SetDetailVisible(true);
			UpdateInfo();
			RefreshCountText();
			SetActionLabel();
			// 购买页：唯一物品已拥有/已解锁不可购买；菜品可重复购买
			if (mActionButton != null)
				mActionButton.interactable = mMode == TradeMode.Sell
					|| !good.owned || IsRepeatablePurchase(good.type);
			RefreshGrid(); // 重绘高亮
			PlayClick();
		}

		private void ClearSelection()
		{
			mSelected = null;
			mCurrentCount = 1;
			SetDetailVisible(false);
			if (mActionButton != null) mActionButton.interactable = false;
			if (mCountText != null) mCountText.text = "0";
			if (mCountBack != null)
			{
				mCountBack.numberOfSteps = 0;
				mCountBack.value = 0f;
			}
		}

		/// <summary>
		/// DetailGroup 显隐（未选中商品前隐藏）。
		/// 显示 = 从右侧 26px 处滑入 + 淡入；隐藏 = 反向滑出 + 淡出后 SetActive(false)。
		/// 已展开时再次调用只复位到终态，避免每次切换商品都重播一遍入场动画。
		/// </summary>
		private void SetDetailVisible(bool visible)
		{
			if (mDetailGroup == null) return;

			if (visible)
			{
				// 已展开：只对齐终态（同时停掉可能残留的收起动画）
				if (mDetailGroup.activeSelf)
				{
					UiTween.Stop(this, ref mDetailTween);
					ResetDetailFinalState();
					return;
				}

				UiTween.Stop(this, ref mDetailTween);   // 终止可能仍在跑的收起动画，否则它稍后会把刚展开的详情再关掉
				mDetailGroup.SetActive(true);

				// 宿主未激活时起不了协程：直接落到终态，避免详情区停在 alpha=0 的隐形状态
				if (!isActiveAndEnabled)
				{
					ResetDetailFinalState();
					return;
				}

				if (mDetailHomeValid && mDetailRect != null)
				{
					var from = mDetailHome + new Vector2(k_detailSlide, 0f);
					UiTween.Play(this, ref mDetailTween, UiTween.SlideFade(
						mDetailRect, mDetailGroupAlpha, from, mDetailHome,
						0f, 1f, k_detailInDuration, UiEase.OutCubic));
				}
				else
				{
					ResetDetailFinalState();
				}
				return;
			}

			if (!mDetailGroup.activeSelf) return;

			// 宿主未激活（初始化阶段就要求隐藏）：直接关节点，不留残影
			if (!isActiveAndEnabled || !mDetailHomeValid || mDetailRect == null)
			{
				mDetailGroup.SetActive(false);
				return;
			}

			UiTween.Play(this, ref mDetailTween, HideDetailRoutine(mDetailHome + new Vector2(k_detailSlide, 0f)));
		}

		/// <summary>收起动画：滑出 + 淡出，结束后再隐藏节点并复位，下次展开从干净状态开始</summary>
		private IEnumerator HideDetailRoutine(Vector2 to)
		{
			if (mDetailGroupAlpha != null) mDetailGroupAlpha.blocksRaycasts = false;

			yield return UiTween.SlideFade(
				mDetailRect, mDetailGroupAlpha, mDetailHome, to,
				1f, 0f, k_detailOutDuration, UiEase.InCubic);

			if (mDetailGroup != null) mDetailGroup.SetActive(false);
			mDetailTween = null;
			if (mDetailRect != null) mDetailRect.anchoredPosition = mDetailHome;
			if (mDetailGroupAlpha != null) mDetailGroupAlpha.alpha = 1f;   // 复位：节点已隐藏，透明度留给下次展开
			if (mDetailGroupAlpha != null) mDetailGroupAlpha.blocksRaycasts = true;
		}

		/// <summary>把详情区对齐到"完全展开"的静态状态（无动画路径的兜底）</summary>
		private void ResetDetailFinalState()
		{
			if (mDetailRect != null && mDetailHomeValid) mDetailRect.anchoredPosition = mDetailHome;
			if (mDetailGroupAlpha != null)
			{
				mDetailGroupAlpha.alpha = 1f;
				mDetailGroupAlpha.blocksRaycasts = true;
			}
		}

		private void UpdateInfo()
		{
			if (mSelected == null) return;
			var good = mSelected.Value;

			if (mDetailIcon != null)
			{
				var spr = !string.IsNullOrEmpty(good.iconPath) ? Resources.Load<Sprite>(good.iconPath) : null;
				mDetailIcon.sprite = spr;
				mDetailIcon.color = spr != null ? Color.white : new Color(1f, 1f, 1f, 0.15f);
			}

			// 详情区名称：与格子同一套规则 —— 星级由 StarText 承担，NameText 只显示菜名
			if (mDetailNameText != null) mDetailNameText.text = good.name;

			// DetailGroup 若挂了 StarText（节点名约定同格子）则在这里写星级；
			// 尚未添加该节点时回退为名称尾部追加 " ★n"，保证旧版式不丢星级信息
			var detailStarT = mDetailGroup != null ? FindChild(mDetailGroup.transform, "StarText") : null;
			if (detailStarT != null)
			{
				bool showStar = good.type == GoodType.Dish && good.star > 0;
				detailStarT.gameObject.SetActive(showStar);
				if (showStar)
				{
					var starText = detailStarT.GetComponent<Text>();
					if (starText != null)
					{
						starText.text = $"★{good.star}";
						starText.color = DishRarity.GetColor(good.star);
					}
				}
			}
			else if (mDetailNameText != null && good.type == GoodType.Dish && good.star > 0)
			{
				mDetailNameText.text = $"{good.name} ★{good.star}";
			}

			if (mDetailDescText != null)
			{
				if (mMode == TradeMode.Sell)
					mDetailDescText.text = $"{good.desc}\n可出售 {GetOwnedCount(good)} 个，单价 {good.unitPrice} 币";
				else if (good.owned && !IsRepeatablePurchase(good.type))
					mDetailDescText.text = $"{good.desc}\n已拥有（不可重复购买）";
				else if (good.owned)
					mDetailDescText.text = $"{good.desc}\n已解锁 · 可重复购买（每次获得 ★1 成品菜），价格 {good.unitPrice} 币";
				else
					mDetailDescText.text = $"{good.desc}\n可购买 1 个，价格 {good.unitPrice} 币";
			}
		}

		/// <summary>
		/// 按最大可操作数量设置滑块离散档位：
		/// numberOfSteps = max → Unity 将 value 量化到 Round(value×(max-1))/(max-1)，
		/// 即只落在 0, 1/(max-1), ..., 1（共 max 个档位），对应数量 1..max
		/// max=1 时无可操作区间：禁用滑块并强制 value=0
		/// </summary>
		private void ApplySliderSteps(int max)
		{
			if (mCountBack == null) return;
			if (max <= 1)
			{
				mCountBack.numberOfSteps = 0;
				mCountBack.interactable = false;
				mCountBack.value = 0f;
			}
			else
			{
				mCountBack.numberOfSteps = max;
				mCountBack.interactable = true;
			}
		}

		/// <summary>当前滑块数量：Value 0-1 → 1..上限（出售=拥有数，购买=1）</summary>
		private void RefreshCountText()
		{
			if (mSelected == null) return;
			mCurrentCount = CurrentCount();
			if (mCountText != null) mCountText.text = mCurrentCount.ToString();
		}

		private int CurrentCount()
		{
			if (mSelected == null || mCountBack == null) return 1;
			int max = GetMaxCount(mSelected.Value);
			return 1 + Mathf.RoundToInt(mCountBack.value * Mathf.Max(0, max - 1));
		}

		private int GetMaxCount(Good good)
		{
			return mMode == TradeMode.Buy ? 1 : Mathf.Max(1, GetOwnedCount(good));
		}

		private int GetOwnedCount(Good good)
		{
			var meta = MetaManager.Instance;
			switch (good.type)
			{
				case GoodType.Ingredient: return meta.GetIngredientCount(good.id);
				case GoodType.Dish: return meta.GetDishCount(good.id, good.star);
				case GoodType.Weapon: return meta.GetWeaponCount(good.id);
				default: return 0;
			}
		}

		// ===== 操作 =====

		private void OnAction()
		{
			if (mSelected == null || !MetaManager.HasInstance) return;
			var good = mSelected.Value;
			// 唯一物品（武器/角色/技能）已拥有/已解锁不可购买；菜品可重复购买
			if (mMode == TradeMode.Buy && good.owned && !IsRepeatablePurchase(good.type)) return;
			bool ok = false;

			if (mMode == TradeMode.Sell)
			{
				int count = CurrentCount();
				int gain = 0;
				switch (good.type)
				{
					case GoodType.Ingredient: gain = MetaManager.Instance.SellIngredient(good.id, count); break;
					case GoodType.Dish: gain = MetaManager.Instance.SellDish(good.id, good.star, count); break;
					case GoodType.Weapon: gain = MetaManager.Instance.SellWeapon(good.id, count); break;
				}
				ok = gain > 0;
			}
			else
			{
				switch (good.type)
				{
					case GoodType.Character: ok = MetaManager.Instance.BuyCharacter(good.id); break;
					case GoodType.Weapon: ok = MetaManager.Instance.BuyWeapon(good.id); break;
					case GoodType.Recipe: ok = MetaManager.Instance.BuyRecipe(good.id); break;
					case GoodType.Skill: ok = MetaManager.Instance.BuySkill(good.id); break;
				}
			}

			if (ok)
			{
				PlayClick();
				PlayCoinPulse();   // 钱币变化反馈
				RefreshGrid(); // 钱币/数量/选中项实时刷新
			}
		}

		private void OnBack()
		{
			PlayClick();
			CloseSelf();
		}

		private void PlayClick()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
		}

		// ===== UI 工具 =====

		private void CreateEmptyHint(string msg)
		{
			if (mGridContent == null) return;
			var textGo = new GameObject("EmptyHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = textGo.GetComponent<RectTransform>();
			rt.SetParent(mGridContent, false);
			rt.anchorMin = new Vector2(0f, 1f);
			rt.anchorMax = new Vector2(1f, 1f);
			rt.pivot = new Vector2(0.5f, 1f);
			rt.sizeDelta = new Vector2(0f, 60f);
			var text = textGo.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = 22;
			text.alignment = TextAnchor.MiddleLeft;
			text.color = new Color(0.75f, 0.75f, 0.8f);
			text.text = msg;
			mDynamicItems.Add(textGo);
		}

		private void ClearListChildren()
		{
			if (mGridContent == null) return;
			for (int i = mGridContent.childCount - 1; i >= 0; i--)
			{
				var child = mGridContent.GetChild(i).gameObject;
				// 只销毁运行时登记的动态格子；预制体内预置的 ShopGrid 母版只隐藏不销毁，
				// 否则母版一旦被销毁，后续克隆全部失败
				if (mDynamicItems.Contains(child))
				{
					mDynamicItems.Remove(child);
					Destroy(child);
				}
				else
				{
					child.SetActive(false);
				}
			}
		}

		private void ClearDynamic()
		{
			foreach (var go in mDynamicItems)
				if (go != null) Destroy(go);
			mDynamicItems.Clear();
		}

		private T FindComponent<T>(string nodeName) where T : Component
		{
			var node = FindChild(transform, nodeName);
			return node != null ? node.GetComponent<T>() : null;
		}

		/// <summary>深度优先按名称查找子节点（不包含自身）</summary>
		private static Transform FindChild(Transform parent, string nodeName)
		{
			for (int i = 0; i < parent.childCount; i++)
			{
				var child = parent.GetChild(i);
				if (child.name == nodeName) return child;
				var found = FindChild(child, nodeName);
				if (found != null) return found;
			}
			return null;
		}
	}
}
