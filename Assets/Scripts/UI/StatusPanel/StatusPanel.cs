using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class StatusPanelData : IUIData
	{
	}

	/// <summary>
	/// 局内状态面板分页（互斥）
	/// </summary>
	public enum StatusTab
	{
		Attr,        // 属性：最终战斗属性
		Preparation, // 战备：本局携带的武器与技能
		BuffCard,    // 增益卡：本局已获得的升级卡
		Bag          // 背包：本局食材背包
	}

	/// <summary>
	/// 局内状态面板：属性 / 战备 / 增益卡 / 背包四分页的只读展示。
	/// 纯显示层——数值全部向各业务管理器实时查询，面板不持有也不修改状态。
	/// 打开时以 timeScale=0 暂停，换取“打开即快照、无需增量刷新”的实现复杂度。
	/// 未绑定的节点与模板按层级名 / Resources 路径回退查找，使面板不依赖手工接线。
	/// </summary>
	public partial class StatusPanel : UIPanel
	{
		// 分页节点名表：按固定顺序映射，与层级先后解耦
		private static readonly string[] kTabToggleNames = { "TabAttr", "TabState", "TabBuffCard", "TabBag" };

		// 内容区根节点名，与 kTabToggleNames 一一对应
		private static readonly string[] kGroupNames = { "AttrGroup", "StateGroup", "BuffCardGroup", "BagGroup" };

		// 条目模板 Resources 路径
		private const string kAttrTemplatePath = "Prefabs/UI/StatusAttrGrid";
		private const string kPreparationTemplatePath = "Prefabs/UI/PreparationGrid";
		private const string kBuffCardTemplatePath = "Prefabs/UI/StatusBuffCardItem";
		private const string kBagTemplatePath = "Prefabs/UI/StatusBagItem";

		private readonly List<GameObject> mDynamicItems = new();
		private float mPrevTimeScale = 1f;
		private bool mBound;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as StatusPanelData ?? new StatusPanelData();
			BindUI();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as StatusPanelData ?? mData;
			BindUI();

			// 暂停：恢复由 OnClose 负责
			mPrevTimeScale = Time.timeScale;
			Time.timeScale = 0f;

			// 只切页不取数：数据在 OnShow 激活后刷新，保证首帧即有内容
			SetActiveTab((int)StatusTab.Attr);
		}

		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this);
			// 取数放在激活后：确保首帧渲染即有内容
			RefreshAll();
		}

		protected override void OnHide()
		{
		}

		protected override void OnClose()
		{
			Time.timeScale = mPrevTimeScale; // 恢复打开前的流速
			ClearDynamicItems();
		}

		private void Update()
		{
			if (Input.GetKeyDown(KeyCode.C) || Input.GetKeyDown(KeyCode.Escape))
			{
				OnClosePressed();
			}
		}

		/// <summary>解析节点并绑定事件；由 mBound 保证只执行一次</summary>
		private void BindUI()
		{
			if (mBound) return;

			ResolveNodes();
			BindEvents();
			mBound = true;
		}

		/// <summary>补全未在 Inspector 绑定的节点（按层级名查找）</summary>
		private void ResolveNodes()
		{
			if (mTitleText == null) mTitleText = FindDeep<Text>("TitleText");
			if (mCloseButton == null) mCloseButton = FindDeep<Button>("CloseButton");

			if (mTabToggles == null || mTabToggles.Length < kTabToggleNames.Length)
			{
				mTabToggles = new Toggle[kTabToggleNames.Length];
				for (int i = 0; i < kTabToggleNames.Length; i++)
					mTabToggles[i] = FindDeep<Toggle>(kTabToggleNames[i]);
			}

			if (mContentGroups == null || mContentGroups.Length < kGroupNames.Length)
			{
				mContentGroups = new GameObject[kGroupNames.Length];
				for (int i = 0; i < kGroupNames.Length; i++)
				{
					var node = FindDeep(kGroupNames[i]);
					mContentGroups[i] = node != null ? node.gameObject : null;
				}
			}

			// 同屏存在多个 Content，须先定位 DetailGroup 再按父级区分
			if (mAttrContent == null) mAttrContent = FindContentUnder("AttrDetailGroup");
			if (mWeaponContent == null) mWeaponContent = FindContentUnder("WeaponGroup");
			if (mSkillContent == null) mSkillContent = FindContentUnder("SkillGroup");
			if (mRecipeContent == null) mRecipeContent = FindContentUnder("RecipeGroup");
			if (mBuffCardContent == null) mBuffCardContent = FindContentUnder("BuffCardDetailGroup");
			if (mBagContent == null) mBagContent = FindContentUnder("BagDetailGroup");

			// 模板未绑定时退化为各 Content 的首个子节点：兼容预制体预置的母版
			mAttrGridPrefab = CaptureMaster(mAttrGridPrefab, mAttrContent);
			mBagItemPrefab = CaptureMaster(mBagItemPrefab, mBagContent);
			mBuffCardItemPrefab = CaptureMaster(mBuffCardItemPrefab, mBuffCardContent);
			mPreparationGridPrefab = CaptureMaster(mPreparationGridPrefab, mWeaponContent);
			mPreparationGridPrefab = CaptureMaster(mPreparationGridPrefab, mSkillContent);
			mPreparationGridPrefab = CaptureMaster(mPreparationGridPrefab, mRecipeContent);

			// 角色信息：限定 RoleGroup 子树，避免同名节点误命中
			var roleGroup = FindDeep("RoleGroup");
			if (roleGroup != null)
			{
				if (mRoleIcon == null) mRoleIcon = FindDeep<Image>(roleGroup, "RoleIcon");
				if (mRoleName == null) mRoleName = FindDeep<Text>(roleGroup, "RoleName");
				if (mGameTimeText == null) mGameTimeText = FindDeep<Text>(roleGroup, "GameTimeText");
				if (mKillCountText == null) mKillCountText = FindDeep<Text>(roleGroup, "KillCountText");
			}
		}

		private void BindEvents()
		{
			if (mCloseButton != null)
			{
				mCloseButton.onClick.RemoveAllListeners();
				mCloseButton.onClick.AddListener(OnClosePressed);
			}

			BindRoleLongPress();

			if (mTabToggles == null) return;

			for (int i = 0; i < mTabToggles.Length; i++)
			{
				int index = i; // 闭包捕获循环变量
				if (mTabToggles[i] == null) continue;
				mTabToggles[i].onValueChanged.RemoveAllListeners();
				mTabToggles[i].onValueChanged.AddListener(on => { if (on) SetActiveTab(index); });
			}
		}

		/// <summary>角色头像长按：查看当前角色的详细属性（RoleIcon 上已挂 Button，长按由触发器吞掉单击）</summary>
		private void BindRoleLongPress()
		{
			if (mRoleIcon == null) return;

			AttachLongPress(mRoleIcon.gameObject, () =>
			{
				string id = RosterManager.HasInstance
					? RosterManager.Instance.GetCurrentCharacterId() : null;
				return string.IsNullOrEmpty(id) ? null : ItemDetailFactory.ForCharacter(id);
			});
		}

		/// <summary>切换分页：Toggle 勾选态与内容区显隐由同一 index 驱动</summary>
		private void SetActiveTab(int index)
		{
			if (mTabToggles != null)
			{
				for (int i = 0; i < mTabToggles.Length; i++)
				{
					if (mTabToggles[i] != null && mTabToggles[i].isOn != (i == index))
						mTabToggles[i].isOn = i == index;
				}
			}

			if (mContentGroups != null)
			{
				for (int i = 0; i < mContentGroups.Length; i++)
				{
					if (mContentGroups[i] != null)
						mContentGroups[i].SetActive(i == index);
				}
			}
		}

		// 整体刷新一次即可：打开期间 timeScale=0，数据不再变化

		private void RefreshAll()
		{
			RefreshAttrs();
			RefreshPreparation();
			RefreshBuffCards();
			RefreshBag();
			RefreshRole();
		}

		private void RefreshAttrs()
		{
			ClearDynamicChildren(mAttrContent);
			if (mAttrContent == null) return;

			var player = GameManager.HasInstance ? GameManager.Instance.Player : null;
			if (player == null)
			{
				CreateEmptyHint(mAttrContent, "当前不在战斗");
				return;
			}

			// 技能 / 闪避冷却缩减是效果引擎里两条独立通道，故分两行展示；取实时值
			// （层数型 buff101 随击杀叠层、受击掉层），与 ActiveSkillSystem / DodgeSkill 消费口径一致
			var effectCtrl = player.GetComponent<PlayerEffectController>();
			float skillCdr = effectCtrl != null ? effectCtrl.CooldownReduction : 0f;
			float dodgeCdr = effectCtrl != null ? effectCtrl.DodgeCooldownReduction : 0f;

			// 法力回复 = 角色基础 + 武器槽提供（与 PlayerController.InitMana 口径一致）
			float manaRegen = player.BaseAttr.ManaRegen;
			if (RosterManager.HasInstance)
			{
				foreach (var weaponId in RosterManager.Instance.GetCurrentWeaponIds())
				{
					var weapon = GameDataMgr.Instance.GetWeapon(weaponId);
					if (weapon != null) manaRegen += weapon.manaRegen;
				}
			}

			// 当前值取 Health 实时值，非配置上限
			float currentHp = player.Health != null ? player.Health.CurrentHp : player.MaxHp;
			AddAttrRow("生命值", $"{currentHp:F0}/{player.MaxHp:F0}");
			AddAttrRow("攻击", player.Attack.ToString("F0"));
			AddAttrRow("物理防御", player.PhysDef.ToString("F0"));
			AddAttrRow("元素防御", player.ElemDef.ToString("F0"));
			AddAttrRow("移动速度", player.MoveSpeed.ToString("F1"));
			// 攻速走最终值口径：局内增益只作用于 AttackSpeedBonus 倍率，
			// 直接读 player.AttackSpeed 会永远停在角色基础值
			AddAttrRow("攻击速度", AttributeText.AttackSpeed(player));
			AddAttrRow("闪避", (player.DodgeChance * 100f).ToString("F1") + "%");
			AddAttrRow("闪避冷却缩减", (dodgeCdr * 100f).ToString("F1") + "%");
			AddAttrRow("暴击率", (player.CritChance * 100f).ToString("F1") + "%");
			AddAttrRow("暴击伤害", (player.CritDamage * 100f).ToString("F0") + "%");
			AddAttrRow("物理伤害加成", (player.PhysDamageBonus * 100f).ToString("F1") + "%");
			AddAttrRow("元素伤害加成", (player.ElemDamageBonus * 100f).ToString("F1") + "%");
			AddAttrRow("技能冷却缩减", (skillCdr * 100f).ToString("F1") + "%");
			AddAttrRow("最大法力", player.MaxMana.ToString("F0"));
			AddAttrRow("法力回复", manaRegen.ToString("F1"));
		}

		/// <summary>新增一条属性行（模板 StatusAttrGrid）</summary>
		private void AddAttrRow(string name, string value)
		{
			var go = InstantiateEntry(GetAttrTemplate(), mAttrContent);
			if (go == null) return;

			SetChildText(go, "AttrName", name);
			SetChildText(go, "AttrValue", value);
			// 属性图标暂无配置来源 → 隐藏图标节点，保留背景/名字/数值布局
			SetChildIcon(go, "AttrIcon", null);
		}

		// 模板解析：优先 Inspector 绑定，未绑定则按 Resources 路径加载
		private GameObject GetAttrTemplate()
			=> mAttrGridPrefab != null ? mAttrGridPrefab : Resources.Load<GameObject>(kAttrTemplatePath);

		/// <summary>
		/// 当局战备：左列本局携带的武器、中列本局装配的可选技能、右列本局装配的菜品。
		/// 数据取 RosterManager 的“当前生效配置”（与开局 ApplyPreparation 同源），配置缺失的 id 直接跳过。
		/// </summary>
		private void RefreshPreparation()
		{
			ClearDynamicChildren(mWeaponContent);
			ClearDynamicChildren(mSkillContent);
			ClearDynamicChildren(mRecipeContent);

			if (mWeaponContent != null)
			{
				int count = 0;
				var weaponIds = RosterManager.HasInstance
					? RosterManager.Instance.GetCurrentWeaponIds() : null;
				if (weaponIds != null)
				{
					foreach (var id in weaponIds)
					{
						if (string.IsNullOrEmpty(id)) continue;
						var weapon = GameDataMgr.Instance.GetWeapon(id);
						if (weapon == null) continue;

						string detailId = weapon.id; // 闭包捕获
						AddPreparationRow(mWeaponContent, weapon.weaponName, weapon.iconPath,
							() => ItemDetailFactory.ForWeapon(detailId));
						count++;
					}
				}
				if (count == 0) CreateEmptyHint(mWeaponContent, "未携带武器");
			}

			if (mSkillContent != null)
			{
				int count = 0;
				var skillIds = RosterManager.HasInstance
					? RosterManager.Instance.GetCurrentSkillIds() : null;
				if (skillIds != null)
				{
					foreach (var id in skillIds)
					{
						if (string.IsNullOrEmpty(id)) continue;
						var skill = GameDataMgr.Instance.GetActiveSkill(id);
						if (skill == null) continue;

						string detailId = skill.id; // 闭包捕获
						AddPreparationRow(mSkillContent, skill.skillName, skill.iconPath,
							() => ItemDetailFactory.ForSkill(detailId));
						count++;
					}
				}
				if (count == 0) CreateEmptyHint(mSkillContent, "未装配技能");
			}

			// 菜品：唯一真相是战前准备页写入的装配槽（RosterManager.Preparation.equippedDishes）；
			// 空槽（RecipeId 为空）与配置缺失的菜直接跳过，与准备页显示口径一致
			if (mRecipeContent != null)
			{
				int count = 0;
				var dishes = RosterManager.HasInstance
					? RosterManager.Instance.GetEquippedDishes() : null;
				if (dishes != null)
				{
					foreach (var dish in dishes)
					{
						if (string.IsNullOrEmpty(dish.RecipeId)) continue;
						var recipe = GameDataMgr.Instance.GetRecipe(dish.RecipeId);
						if (recipe == null) continue;

						string detailId = dish.RecipeId; // 闭包捕获（dish 是结构体，不能直接闭包）
						int star = dish.Star;
						AddPreparationRow(mRecipeContent, recipe.recipeName, recipe.iconPath,
							() => ItemDetailFactory.ForDish(detailId, star), star);
						count++;
					}
				}
				if (count == 0) CreateEmptyHint(mRecipeContent, "未携带菜品");
			}
		}

		/// <summary>
		/// 新增一条战备行（模板 PreparationGrid：根(Image+Button) → Icon / NameText / StarText / Selected）。
		/// 武器与技能都无星级、也无选中态，故隐藏后两者，只留图标 + 名称；菜品额外显示 StarText（"★n" + 稀有度色）。
		/// 长按查看详细属性。
		/// </summary>
		private void AddPreparationRow(RectTransform parent, string name, string iconPath,
			System.Func<ItemDetailPanelData> detailBuilder, int star = 0)
		{
			var go = InstantiateEntry(GetPreparationTemplate(), parent);
			if (go == null) return;

			SetChildText(go, "NameText", name);
			SetChildIcon(go, "Icon", iconPath);
			SetPreparationStar(go, star);
			SetChildActive(go, "Selected", false);

			AttachLongPress(go, detailBuilder);
		}

		/// <summary>星级文本：star&gt;0 显示 "★n" 并按稀有度着色，=0（武器/技能/空态）整节点隐藏</summary>
		private static void SetPreparationStar(GameObject root, int star)
		{
			var node = FindDeep(root.transform, "StarText");
			if (node == null) return;

			bool hasStar = star > 0;
			node.gameObject.SetActive(hasStar);
			if (!hasStar) return;

			var text = node.GetComponent<Text>();
			if (text == null) return;
			text.text = $"★{star}";
			text.color = DishRarity.GetColor(star);
		}

		private GameObject GetPreparationTemplate()
			=> mPreparationGridPrefab != null
				? mPreparationGridPrefab
				: Resources.Load<GameObject>(kPreparationTemplatePath);

		private void RefreshBuffCards()
		{
			ClearDynamicChildren(mBuffCardContent);
			if (mBuffCardContent == null) return;

			var ids = UpgradeBuffSystem.HasInstance
				? UpgradeBuffSystem.Instance.GetSelectedBuffIds() : null;
			if (ids == null || ids.Count == 0)
			{
				CreateEmptyHint(mBuffCardContent, "尚未获得增益卡");
				return;
			}

			foreach (var id in ids)
			{
				var config = GameDataMgr.Instance.GetBuffUpgrade(id);
				if (config == null) continue;
				AddBuffCardRow(config);
			}
		}

		/// <summary>新增一张增益卡（模板 StatusBuffCardItem）</summary>
		private void AddBuffCardRow(BuffUpgradeConfig config)
		{
			var go = InstantiateEntry(GetBuffCardTemplate(), mBuffCardContent);
			if (go == null) return;

			SetChildText(go, "NameText", config.buffName);
			SetChildText(go, "DescText", config.description);
			SetChildIcon(go, "SkillIcon", config.iconPath);

			AttachLongPress(go, () => ItemDetailFactory.ForBuff(config.id));
		}

		private GameObject GetBuffCardTemplate()
			=> mBuffCardItemPrefab != null ? mBuffCardItemPrefab : Resources.Load<GameObject>(kBuffCardTemplatePath);

		private void RefreshBag()
		{
			ClearDynamicChildren(mBagContent);
			if (mBagContent == null) return;

			// 局内食材：临时数据，仅本局累计
			bool hasIngredients = false;
			if (DropManager.HasInstance)
			{
				var inventory = DropManager.Instance.IngredientInventory;
				if (inventory != null && inventory.Count > 0)
				{
					hasIngredients = true;

					var keys = new List<string>(inventory.Keys);
					keys.Sort();
					foreach (var id in keys)
					{
						var config = GameDataMgr.Instance.GetIngredient(id);
						AddBagRow(
							config != null ? config.ingredientName : id,
							config != null ? config.iconPath : null,
							inventory[id],
							detailBuilder: () => ItemDetailFactory.ForIngredient(id));
					}
				}
			}

			// 成品菜：本局炒出的菜（画中画炒菜与 BOSS 音游结算入账 DropManager.RunDishGains）
			bool hasDishes = RefreshBagDishes();

			if (!hasIngredients && !hasDishes)
			{
				CreateEmptyHint(mBagContent, "背包是空的");
			}
		}

		/// <summary>本局炒出的成品菜（DropManager 局内记账，非局外背包），按 菜谱+星级 追加行；返回是否存在成品菜</summary>
		private bool RefreshBagDishes()
		{
			if (!DropManager.HasInstance) return false;
			var gains = DropManager.Instance.RunDishGains;
			if (gains == null || gains.Count == 0) return false;

			// 键为 "recipeId|star"（AddRunDish 写入时已聚合），这里拆回结构化行
			var rows = new List<(string recipeId, int star, int count)>();
			foreach (var kv in gains)
			{
				var sep = kv.Key.LastIndexOf('|');
				if (sep <= 0) continue;
				if (!int.TryParse(kv.Key.Substring(sep + 1), out int star)) continue;
				rows.Add((kv.Key.Substring(0, sep), star, kv.Value));
			}
			if (rows.Count == 0) return false;

			rows.Sort((a, b) =>
			{
				var ra = GameDataMgr.Instance.GetRecipe(a.recipeId);
				var rb = GameDataMgr.Instance.GetRecipe(b.recipeId);
				return string.CompareOrdinal(ra?.recipeName ?? a.recipeId, rb?.recipeName ?? b.recipeId);
			});

			foreach (var row in rows)
			{
				var recipe = GameDataMgr.Instance.GetRecipe(row.recipeId);
				AddBagRow(
					$"{(recipe != null ? recipe.recipeName : row.recipeId)} ★{row.star}",
					recipe?.iconPath,
					row.count,
					row.star,
					() => ItemDetailFactory.ForDish(row.recipeId, row.star));
			}
			return true;
		}

		/// <summary>新增一条背包行（模板 StatusBagItem；star&gt;0 时成品菜名按稀有度着色）</summary>
		private void AddBagRow(string name, string iconPath, int count, int star = 0,
			System.Func<ItemDetailPanelData> detailBuilder = null)
		{
			var go = InstantiateEntry(GetBagTemplate(), mBagContent);
			if (go == null) return;

			SetChildText(go, "ItemName", name);
			if (star > 0)
			{
				var nameText = go.transform.Find("ItemName")?.GetComponent<Text>();
				if (nameText != null) nameText.color = DishRarity.GetColor(star);
			}
			SetChildText(go, "ItemCount", $"×{count}");
			SetChildIcon(go, "ItemIcon", iconPath);

			AttachLongPress(go, detailBuilder);
		}

		private GameObject GetBagTemplate()
			=> mBagItemPrefab != null ? mBagItemPrefab : Resources.Load<GameObject>(kBagTemplatePath);

		private void RefreshRole()
		{
			var character = RosterManager.HasInstance
				? RosterManager.Instance.GetCurrentCharacter() : null;

			if (mRoleName != null)
				mRoleName.text = character != null ? character.characterName : "未知角色";

			if (mRoleIcon != null)
			{
				var sprite = character != null && !string.IsNullOrEmpty(character.iconPath)
					? Resources.Load<Sprite>(character.iconPath) : null;
				mRoleIcon.gameObject.SetActive(sprite != null);
				if (sprite != null) mRoleIcon.sprite = sprite;
			}

			if (mGameTimeText != null)
			{
				float elapsed = GameManager.HasInstance
					? Mathf.Max(0f, Time.time - GameManager.Instance.RunStartTime) : 0f;
				mGameTimeText.text = "本局时间  " + FormatGameTime(elapsed);
			}

			if (mKillCountText != null)
			{
				int killCount = DropManager.HasInstance ? DropManager.Instance.KillCount : 0;
				mKillCountText.text = "击杀数  " + killCount;
			}
		}

		/// <summary>当局时长文案：不足 1 小时显示 分:秒，超过显示 时:分:秒</summary>
		private static string FormatGameTime(float seconds)
		{
			int total = Mathf.FloorToInt(seconds);
			int hours = total / 3600;
			int minutes = total % 3600 / 60;
			int secs = total % 60;

			return hours > 0
				? $"{hours}:{minutes:D2}:{secs:D2}"
				: $"{minutes:D2}:{secs:D2}";
		}

		/// <summary>实例化条目并登记，生命周期由面板关闭时统一销毁</summary>
		private GameObject InstantiateEntry(GameObject template, RectTransform parent)
		{
			if (template == null || parent == null) return null;

			var go = Instantiate(template, parent, false);
			go.SetActive(true);
			mDynamicItems.Add(go);
			return go;
		}

		/// <summary>设置子节点文本（找不到节点则忽略）</summary>
		private static void SetChildText(GameObject root, string childName, string value)
		{
			var node = FindDeep(root.transform, childName);
			if (node == null) return;

			var text = node.GetComponent<Text>();
			if (text != null) text.text = value;
		}

		/// <summary>设置子节点图标；路径为空/加载失败时隐藏该节点</summary>
		private static void SetChildIcon(GameObject root, string childName, string iconPath)
		{
			var node = FindDeep(root.transform, childName);
			if (node == null) return;

			var image = node.GetComponent<Image>();
			if (image == null) return;

			var sprite = string.IsNullOrEmpty(iconPath) ? null : Resources.Load<Sprite>(iconPath);
			if (sprite != null)
			{
				image.sprite = sprite;
				node.gameObject.SetActive(true);
			}
			else
			{
				node.gameObject.SetActive(false);
			}
		}

		/// <summary>设置子节点显隐（找不到节点则忽略）</summary>
		private static void SetChildActive(GameObject root, string childName, bool active)
		{
			var node = FindDeep(root.transform, childName);
			if (node != null) node.gameObject.SetActive(active);
		}

		/// <summary>挂长按触发器打开详情弹窗；载荷为空（配置缺失）时不打开，条目保留原有点击语义</summary>
		private static void AttachLongPress(GameObject go, System.Func<ItemDetailPanelData> detailBuilder)
		{
			if (go == null || detailBuilder == null) return;

			var longPress = go.GetComponent<LongPressTrigger>();
			if (longPress == null) longPress = go.AddComponent<LongPressTrigger>();
			longPress.onLongPress = () =>
			{
				var data = detailBuilder();
				if (data == null) return;
				UIKit.OpenPanel<ItemDetailPanel>(UILevel.Pop, data);
			};
		}

		/// <summary>销毁本次运行时创建的条目。非动态子节点即母版，只停用不销毁——
		/// 误销毁会让后续克隆全部失败（表现为列表全空）</summary>
		private void ClearDynamicChildren(RectTransform container)
		{
			if (container == null) return;

			for (int i = container.childCount - 1; i >= 0; i--)
			{
				var child = container.GetChild(i).gameObject;
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

		/// <summary>模板未绑定时从 Content 现有子节点捕获母版；已绑定或无子节点时原样返回</summary>
		private static GameObject CaptureMaster(GameObject current, RectTransform content)
		{
			if (current != null || content == null || content.childCount == 0) return current;
			return content.GetChild(0).gameObject;
		}

		/// <summary>创建空列表提示文本（居中，随面板关闭销毁）</summary>
		private void CreateEmptyHint(RectTransform parent, string message)
		{
			var go = new GameObject("EmptyHint",
				typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = (RectTransform)go.transform;
			rt.SetParent(parent, false);
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;

			var text = go.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = 20;
			text.alignment = TextAnchor.MiddleCenter;
			text.color = new Color(0.75f, 0.75f, 0.8f);
			text.text = message;

			mDynamicItems.Add(go);
		}

		/// <summary>销毁全部动态条目</summary>
		private void ClearDynamicItems()
		{
			foreach (var go in mDynamicItems)
			{
				if (go != null) Destroy(go);
			}
			mDynamicItems.Clear();
		}

		private T FindDeep<T>(string name) where T : Component
		{
			var node = FindDeep(transform, name);
			return node != null ? node.GetComponent<T>() : null;
		}

		private static T FindDeep<T>(Transform root, string name) where T : Component
		{
			var node = FindDeep(root, name);
			return node != null ? node.GetComponent<T>() : null;
		}

		private Transform FindDeep(string name) => FindDeep(transform, name);

		/// <summary>按名称深度查找子节点（广度优先；面板内同名节点唯一）</summary>
		private static Transform FindDeep(Transform root, string name)
		{
			for (int i = 0; i < root.childCount; i++)
			{
				if (root.GetChild(i).name == name) return root.GetChild(i);
			}
			for (int i = 0; i < root.childCount; i++)
			{
				var found = FindDeep(root.GetChild(i), name);
				if (found != null) return found;
			}
			return null;
		}

		/// <summary>在指定 DetailGroup 下查找其 Content（同名 Content 按父级区分）</summary>
		private RectTransform FindContentUnder(string detailGroupName)
		{
			var detail = FindDeep(detailGroupName);
			if (detail == null) return null;
			return FindDeep(detail, "Content") as RectTransform;
		}

		private void OnClosePressed()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			UIKit.ClosePanel<StatusPanel>();
		}
	}
}
