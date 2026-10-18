using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	/// <summary>
	/// 通用物品详情页中的单条属性（名称 + 数值）。
	/// </summary>
	[System.Serializable]
	public sealed class ItemDetailAttr
	{
		public string name;
		public string value;

		public ItemDetailAttr() { }

		public ItemDetailAttr(string name, string value)
		{
			this.name = name;
			this.value = value;
		}
	}

	/// <summary>
	/// 通用物品详情页数据载荷：由调用方填充后经 UIKit.OpenPanel 传入。
	/// </summary>
	[System.Serializable]
	public sealed class ItemDetailPanelData : IUIData
	{
		public string title = "";        // 顶部标题（可空，空则隐藏标题节点）
		public string iconPath = "";     // 图标 Resources 路径（空则隐藏图标）
		public string itemName = "";     // 物品名称
		public string description = "";  // 物品描述
		public List<ItemDetailAttr> attributes = new(); // 详细属性行

		/// <summary>链式追加一条属性行。</summary>
		public ItemDetailPanelData Add(string name, string value)
		{
			attributes.Add(new ItemDetailAttr(name, value));
			return this;
		}
	}

	/// <summary>
	/// 通用物品详情弹窗：按 ItemDetailPanelData 渲染图标 / 名称 / 描述 / 属性行。
	/// 纯显示层——面板不感知物品类型，展示内容完全由调用方构造的载荷决定。
	/// 不改动 timeScale，以弹窗覆盖在来源面板之上；属性行由模板实例化，模板未绑定时回退取 Content 首个子节点。
	/// </summary>
	public partial class ItemDetailPanel : UIPanel
	{
		private readonly List<GameObject> mDynamicItems = new();
		private bool mBound;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as ItemDetailPanelData ?? new ItemDetailPanelData();
			BindUI();
		}

		protected override void OnOpen(IUIData data = null)
		{
			if (data is ItemDetailPanelData typed) mData = typed;
			BindUI();
			RefreshView();
		}

		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this, 0.18f);   // 弹窗节奏略快：0.18s
			// 激活后构建：保证首帧即可见
			RefreshView();
		}

		protected override void OnClose()
		{
			ClearDynamicItems();
		}

		private void BindUI()
		{
			if (mBound) return;
			ResolveNodes();
			BindEvents();
			mBound = true;
		}

		/// <summary>补全未在 Inspector 绑定的节点（按层级名自动查找）</summary>
		private void ResolveNodes()
		{
			if (mTitleText == null) mTitleText = FindDeep<Text>("TitleText");
			if (mCloseButton == null) mCloseButton = FindDeep<Button>("CloseButton");
			if (mItemIcon == null) mItemIcon = FindDeep<Image>("ItemIcon");
			if (mItemNameText == null) mItemNameText = FindDeep<Text>("NameText");
			if (mDescText == null) mDescText = FindDeep<Text>("DescText");
			if (mAttrContent == null) mAttrContent = FindDeep("Content") as RectTransform;
			if (mMaskButton == null) mMaskButton = FindDeep<Button>("Mask");
			if (mAttrTemplate == null) mAttrTemplate = CaptureMaster(mAttrTemplate, mAttrContent);
		}

		private void BindEvents()
		{
			if (mCloseButton != null)
			{
				mCloseButton.onClick.RemoveAllListeners();
				mCloseButton.onClick.AddListener(OnClosePressed);
			}

			if (mMaskButton != null)
			{
				mMaskButton.onClick.RemoveAllListeners();
				mMaskButton.onClick.AddListener(OnClosePressed);
			}
		}

		private void RefreshView()
		{
			var data = mData ?? new ItemDetailPanelData();

			if (mTitleText != null)
			{
				mTitleText.text = data.title;
				mTitleText.gameObject.SetActive(!string.IsNullOrEmpty(data.title));
			}

			if (mItemNameText != null) mItemNameText.text = data.itemName;
			if (mDescText != null) mDescText.text = data.description;

			RefreshIcon(data.iconPath);
			RefreshAttributes(data);
		}

		/// <summary>设置图标；路径为空或加载失败时隐藏图标节点</summary>
		private void RefreshIcon(string iconPath)
		{
			if (mItemIcon == null) return;

			var sprite = string.IsNullOrEmpty(iconPath) ? null : Resources.Load<Sprite>(iconPath);
			mItemIcon.gameObject.SetActive(sprite != null);
			if (sprite != null) mItemIcon.sprite = sprite;
		}

		/// <summary>按数据重建属性行（模板 AttrRow；无数据时显示空提示）</summary>
		private void RefreshAttributes(ItemDetailPanelData data)
		{
			ClearDynamicItems();
			if (mAttrContent == null) return;

			if (data.attributes == null || data.attributes.Count == 0)
			{
				CreateEmptyHint(mAttrContent, "暂无属性数据");
				return;
			}

			if (mAttrTemplate == null)
			{
				CreateEmptyHint(mAttrContent, "缺少属性行模板");
				return;
			}

			foreach (var attr in data.attributes)
			{
				var go = Instantiate(mAttrTemplate, mAttrContent, false);
				go.SetActive(true);
				mDynamicItems.Add(go);

				SetChildText(go, "AttrName", attr.name);
				SetChildText(go, "AttrValue", attr.value);
			}
		}

		/// <summary>设置子节点文本（找不到节点则忽略）</summary>
		private static void SetChildText(GameObject root, string childName, string value)
		{
			var node = FindDeep(root.transform, childName);
			if (node == null) return;

			var text = node.GetComponent<Text>();
			if (text != null) text.text = value;
		}

		/// <summary>销毁本次运行时创建的条目；预置母版只隐藏不销毁</summary>
		private void ClearDynamicItems()
		{
			for (int i = mDynamicItems.Count - 1; i >= 0; i--)
			{
				var go = mDynamicItems[i];
				if (go != null) Destroy(go);
			}
			mDynamicItems.Clear();

			if (mAttrContent == null) return;
			for (int i = mAttrContent.childCount - 1; i >= 0; i--)
			{
				var child = mAttrContent.GetChild(i).gameObject;
				if (!mDynamicItems.Contains(child)) child.SetActive(false);
			}
		}

		/// <summary>模板未绑定时，从 Content 现有子节点捕获母版（取首个）</summary>
		private static GameObject CaptureMaster(GameObject current, RectTransform content)
		{
			if (current != null || content == null || content.childCount == 0) return current;
			return content.GetChild(0).gameObject;
		}

		/// <summary>创建空列表提示文本（随面板关闭销毁）</summary>
		private void CreateEmptyHint(RectTransform parent, string message)
		{
			var go = new GameObject("EmptyHint", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
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

		private T FindDeep<T>(string name) where T : Component
		{
			var node = FindDeep(transform, name);
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

		private void OnClosePressed()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			UIKit.ClosePanel<ItemDetailPanel>();
		}
	}

	/// <summary>
	/// 从各物品配置构造详情载荷的无状态工厂：使各面板长按物品时复用同一个详情弹窗。
	/// 只读配置、不持有状态；配置缺失返回 null，由调用方据此跳过。
	/// </summary>
	public static class ItemDetailFactory
	{
		/// <summary>角色详情（13 项战斗属性 + 固有被动技能）</summary>
		public static ItemDetailPanelData ForCharacter(string id)
		{
			var c = GameDataMgr.Instance.GetCharacter(id);
			if (c == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "角色详情",
				iconPath = c.iconPath,
				itemName = c.characterName,
				description = c.description,
			};
			data.Add("生命值", c.maxHp.ToString("F0"));
			data.Add("攻击力", c.attack.ToString("F0"));
			data.Add("物理防御", c.physDef.ToString("F0"));
			data.Add("元素防御", c.elemDef.ToString("F0"));
			data.Add("移动速度", c.moveSpeed.ToString("F1"));
			data.Add("攻击速度", c.attackSpeed.ToString("F2"));
			data.Add("闪避几率", (c.dodgeChance * 100f).ToString("F1") + "%");
			data.Add("暴击率", (c.critChance * 100f).ToString("F1") + "%");
			data.Add("暴击伤害", (c.critDamage * 100f).ToString("F0") + "%");
			data.Add("物理增伤", (c.physDamageBonus * 100f).ToString("F1") + "%");
			data.Add("元素增伤", (c.elemDamageBonus * 100f).ToString("F1") + "%");
			data.Add("法力值", c.maxMana.ToString("F0"));
			data.Add("法力回复", c.manaRegen.ToString("F1"));
			AppendPassiveEffect(data, c.passiveEffectId);
			return data;
		}

		/// <summary>追加角色固有被动（passiveEffectId → GameEffectConfig，常驻免佩戴）。
		/// 属性行只放技能名：AttrValue 列固定 160px 且垂直截断，长描述改挂描述区（300×284 多行，可整段显示）</summary>
		private static void AppendPassiveEffect(ItemDetailPanelData data, string effectId)
		{
			if (string.IsNullOrEmpty(effectId))
			{
				data.Add("被动技能", "无");
				return;
			}

			var effect = GameDataMgr.Instance.GetEffect(effectId);
			if (effect == null)
			{
				// 配置缺失时暴露 id，便于定位数据问题
				data.Add("被动技能", effectId);
				return;
			}

			data.Add("被动技能", effect.effectName);
			AppendSkillDescription(data, "被动技能", effect.effectName, effect.description);
		}

		/// <summary>把技能效果说明并入详情描述区（与物品自身描述之间空一行分隔）</summary>
		private static void AppendSkillDescription(ItemDetailPanelData data, string label, string name, string description)
		{
			if (string.IsNullOrEmpty(description)) return;

			string block = $"【{label}】{name}\n{description}";
			data.description = string.IsNullOrEmpty(data.description) ? block : data.description + "\n\n" + block;
		}

		/// <summary>
		/// 武器详情：基础属性（baseAttrs 非 0 项）+ 共有攻击参数 + 按攻击方式二选一的专属参数 + 主动技能。
		/// 近战不显示索敌 / 子弹、远程不显示刀刃，避免互不相关的字段挤在同一张表。
		/// 法力两类由武器独立字段提供，不在此表，以免与 baseAttrs 混淆。
		/// </summary>
		public static ItemDetailPanelData ForWeapon(string id)
		{
			var w = GameDataMgr.Instance.GetWeapon(id);
			if (w == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "武器详情",
				iconPath = w.iconPath,
				itemName = w.weaponName,
				description = w.description,
			};

			AppendBaseAttrs(data, w.baseAttrs);

			data.Add("攻击方式", w.attackMode == WeaponAttackMode.Melee ? "近战" : "远程");
			data.Add("伤害类型", w.damageType == DamageType.Physical ? "物理" : "元素");
			data.Add("武器对应弱点", WeaponWeaknessText(w.attackType));
			data.Add("攻击倍率", w.damageMultiplier.ToString("F2"));
			data.Add("攻速倍率", w.attackSpeedScale.ToString("F2") + "×");

			if (w.attackMode == WeaponAttackMode.Ranged)
			{
				data.Add("索敌距离", w.attackRange.ToString("F1"));
				data.Add("子弹速度", w.projectileSpeed.ToString("F1"));
				data.Add("子弹数量", w.projectileCount.ToString());
			}
			else
			{
				data.Add("刀刃长度", w.meleeBladeLength.ToString("F1"));
				// 0 = 运行时从 sprite 网格顶点自动测量刃宽
				data.Add("刀刃宽度", w.meleeBladeWidth > 0f ? w.meleeBladeWidth.ToString("F2") : "自动");
			}

			AppendWeaponActiveSkill(data, w.activeSkillId);
			return data;
		}

		/// <summary>
		/// 追加武器主动技能（WeaponConfig.activeSkillId）。
		/// 技能实体取自 ActiveSkillConfig（与自由装配的技能池同表），冷却 / 蓝耗口径与 ForSkill 一致；
		/// 若指向 GameEffectConfig 则走兜底分支（该表无冷却 / 蓝耗，只出名字与说明）。
		/// </summary>
		private static void AppendWeaponActiveSkill(ItemDetailPanelData data, string skillId)
		{
			if (string.IsNullOrEmpty(skillId))
			{
				data.Add("主动技能", "无");
				return;
			}

			var skill = GameDataMgr.Instance.GetActiveSkill(skillId);
			if (skill != null)
			{
				data.Add("主动技能", skill.skillName);
				data.Add("主动技能冷却", skill.cooldown.ToString("F1") + "秒");
				data.Add("主动技能蓝耗", skill.manaCost < 0 ? "无" : skill.manaCost.ToString());
				AppendSkillDescription(data, "主动技能", skill.skillName, skill.description);
				return;
			}

			var effect = GameDataMgr.Instance.GetEffect(skillId);
			if (effect == null)
			{
				data.Add("主动技能", skillId);
				return;
			}

			data.Add("主动技能", effect.effectName);
			AppendSkillDescription(data, "主动技能", effect.effectName, effect.description);
		}

		/// <summary>
		/// 菜品 / 菜谱详情：只展示该菜品给予的属性数值加成（按星级解析 starEffects 效果链并聚合）。
		/// star &lt;= 0（图鉴、商店菜谱无星级概念）按 ★1 处理——否则一条加成都不显示。
		/// </summary>
		public static ItemDetailPanelData ForDish(string recipeId, int star = 0)
		{
			var r = GameDataMgr.Instance.GetRecipe(recipeId);
			if (r == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "菜品详情",
				iconPath = r.iconPath,
				itemName = star > 0 ? $"{r.recipeName} ★{star}" : r.recipeName,
				description = r.description,
			};
			AppendDishBonuses(data, r, Mathf.Max(1, star));
			return data;
		}

		/// <summary>
		/// 追加菜品给予的属性数值加成。
		/// 效果链与装配侧同源（RosterManager.ResolveStarEffects）：高星级条目是独立配置而非覆盖，
		/// 故同属性多档必须求和（生命 +12 与 生命 +18 → 生命值 +30）。
		/// 只收属性类行为；飞弹 / 燃烧 / 减速等战斗行为不属于属性数值加成，不在本表展示。
		/// </summary>
		private static void AppendDishBonuses(ItemDetailPanelData data, RecipeConfig recipe, int star)
		{
			var ids = RosterManager.ResolveStarEffects(recipe, star);
			if (ids == null || ids.Count == 0) return;

			var order = new List<string>();
			var sums = new Dictionary<string, float>();
			var kinds = new Dictionary<string, DishBonusKind>();

			foreach (var id in ids)
			{
				var effect = GameDataMgr.Instance.GetEffect(id);
				if (effect?.actions == null) continue;

				foreach (var action in effect.actions)
				{
					if (action == null) continue;
					if (!TryResolveDishBonus(action, out string name, out float value, out var kind)) continue;

					if (!sums.ContainsKey(name))
					{
						order.Add(name);
						sums[name] = 0f;
						kinds[name] = kind;
					}
					sums[name] += value;
				}
			}

			foreach (var name in order)
				data.Add(name, FormatDishBonus(sums[name], kinds[name]));
		}

		/// <summary>属性加成的展示口径</summary>
		private enum DishBonusKind
		{
			Flat,   // 直接加到属性上的数值（攻击 / 生命值 / 移动速度）
			Ratio,  // 按比例加成，显示为百分比（攻速 / 暴击率 / 暴击伤害 / 物元增伤）
			Shield  // 护盾，值为最大生命比例
		}

		/// <summary>
		/// 单个效果行为 → (属性名, 数值, 口径)；非属性类行为返回 false，由调用方跳过。
		/// ⚠️ 口径必须与 PlayerEffectController.ApplyActionPassiveSoft 一致，否则面板给出与实机不符的数字：
		/// MoveSpeedBonus 是平坦加值（attr.MoveSpeed = value），要表达「+X% 移速」必须写 percentOfMoveSpeed，
		/// 两者分别按 Flat / Ratio 呈现（eff_cucumber_fresh/_2 已统一为 percentOfMoveSpeed 0.3/0.45）；
		/// MaxHpBonus 走 mHealth.AddMaxHp（平坦生命），percentOfMaxHp 才是按最大生命比例。
		/// ⚠️ 残留口径差：★2 依次应用两条移速效果形成复利（5 → 6.5 → 9.43，约 +88.6%），
		/// 本表按同属性求和显示 +75%；消除需让 percentOfMoveSpeed 按基础移速换算（与 OneStackDelta.stackToMoveSpeed 同口径）。
		/// </summary>
		private static bool TryResolveDishBonus(EffectActionData a, out string name, out float value, out DishBonusKind kind)
		{
			name = null;
			value = 0f;
			kind = DishBonusKind.Flat;

			switch ((EffectActionType)a.action)
			{
				case EffectActionType.DamageBonus:
					name = "攻击";
					if (a.baseValue != 0f) { kind = DishBonusKind.Flat; value = a.baseValue; }
					else if (a.percentOfAttack != 0f) { kind = DishBonusKind.Ratio; value = a.percentOfAttack; }
					break;

				case EffectActionType.MaxHpBonus:
					name = "生命值";
					if (a.baseValue != 0f) { kind = DishBonusKind.Flat; value = a.baseValue; }
					else if (a.percentOfMaxHp != 0f) { kind = DishBonusKind.Ratio; value = a.percentOfMaxHp; }
					break;

				case EffectActionType.MoveSpeedBonus:
					name = "移动速度";
					if (a.baseValue != 0f) { kind = DishBonusKind.Flat; value = a.baseValue; }
					else if (a.percentOfMoveSpeed != 0f) { kind = DishBonusKind.Ratio; value = a.percentOfMoveSpeed; }
					break;

				case EffectActionType.AttackSpeedBonus: name = "攻击速度"; kind = DishBonusKind.Ratio; value = a.baseValue; break;
				case EffectActionType.CritChanceBonus: name = "暴击率"; kind = DishBonusKind.Ratio; value = a.baseValue; break;
				case EffectActionType.CritDamageBonus: name = "暴击伤害"; kind = DishBonusKind.Ratio; value = a.baseValue; break;
				case EffectActionType.PhysDamageBonus: name = "物理增伤"; kind = DishBonusKind.Ratio; value = a.baseValue; break;
				case EffectActionType.ElemDamageBonus: name = "元素增伤"; kind = DishBonusKind.Ratio; value = a.baseValue; break;

				case EffectActionType.Shield: name = "护盾"; kind = DishBonusKind.Shield; value = a.percentOfMaxHp; break;

				default: return false;
			}

			return name != null;
		}

		/// <summary>格式化一条菜品加成（正值带 +，比例转百分比）</summary>
		private static string FormatDishBonus(float value, DishBonusKind kind)
		{
			switch (kind)
			{
				case DishBonusKind.Ratio:
					return (value >= 0f ? "+" : "") + (value * 100f).ToString("F1") + "%";
				case DishBonusKind.Shield:
					return "最大生命×" + (value * 100f).ToString("F0") + "%";
				default:
					return (value >= 0f ? "+" : "") + value.ToString("0.#");
			}
		}

		/// <summary>主动技能详情：只展示冷却时间与蓝量消耗（其余效果参数由 description 承载）</summary>
		public static ItemDetailPanelData ForSkill(string id)
		{
			var s = GameDataMgr.Instance.GetActiveSkill(id);
			if (s == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "技能详情",
				iconPath = s.iconPath,
				itemName = s.skillName,
				description = s.description,
			};
			data.Add("冷却时间", s.cooldown.ToString("F1") + "秒");
			data.Add("蓝量消耗", s.manaCost < 0 ? "无" : s.manaCost.ToString());
			return data;
		}

		/// <summary>食材详情</summary>
		public static ItemDetailPanelData ForIngredient(string id)
		{
			var i = GameDataMgr.Instance.GetIngredient(id);
			if (i == null) return null;

			string type = IngredientTypeText(i.ingredientType);
			var data = new ItemDetailPanelData
			{
				title = "食材详情",
				iconPath = i.iconPath,
				itemName = i.ingredientName,
				description = $"{type}类食材",
			};
			data.Add("分类", type);
			data.Add("出售单价", i.sellPrice.ToString());
			return data;
		}

		/// <summary>敌人详情</summary>
		public static ItemDetailPanelData ForEnemy(string id)
		{
			var e = GameDataMgr.Instance.GetEnemy(id);
			if (e == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "敌人详情",
				iconPath = e.iconPath,
				itemName = e.enemyName,
				description = string.IsNullOrEmpty(e.description) ? "暂无描述" : e.description,
			};
			data.Add("生命值", e.maxHp.ToString("F0"));
			data.Add("移动速度", e.speed.ToString("F1"));
			data.Add("攻击伤害", e.contactDamage.ToString("F0"));
			data.Add("行为类型", EnemyBehaviorText(e.behavior));

			var drop = string.IsNullOrEmpty(e.dropIngredientId) ? null : GameDataMgr.Instance.GetIngredient(e.dropIngredientId);
			data.Add("掉落食材", drop != null ? drop.ingredientName
				: (string.IsNullOrEmpty(e.dropIngredientId) ? "无" : e.dropIngredientId));
			return data;
		}

		/// <summary>BOSS 详情（图鉴敌人页签长按；Boss id 不在 Enemies 中，由调用方兜底调用）</summary>
		public static ItemDetailPanelData ForBoss(string id)
		{
			var b = GameDataMgr.Instance.GetBoss(id);
			if (b == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "BOSS 详情",
				iconPath = b.iconPath,
				itemName = b.bossName,
				description = string.IsNullOrEmpty(b.description) ? "暂无描述" : b.description,
			};
			data.Add("基准生命值", b.maxHp.ToString("F0"));
			data.Add("攻击伤害", b.attack.ToString("F0"));
			data.Add("伤害类型", b.damageType == DamageType.Element ? "元素" : "物理");
			data.Add("移动速度", b.moveSpeed.ToString("F1"));

			// 弱点随血量阶段切换，越低越难命中
			if (b.phases != null && b.phases.Count > 0)
			{
				var parts = new List<string>(b.phases.Count);
				foreach (var p in b.phases)
				{
					if (p == null) continue;
					parts.Add($"{p.hpThreshold:P0}以下→{AttackTypeText(p.weaknessType)}");
				}
				data.Add("弱点阶段", string.Join("，", parts));
				data.Add("破韧奖励", $"虚弱增伤×{b.weaknessMultiplier:F1}（{b.weaknessDuration:F0}秒）");
			}

			var rare = string.IsNullOrEmpty(b.dropRareIngredientId) ? null : GameDataMgr.Instance.GetIngredient(b.dropRareIngredientId);
			data.Add("稀有掉落", rare != null ? rare.ingredientName
				: (string.IsNullOrEmpty(b.dropRareIngredientId) ? "无" : b.dropRareIngredientId));
			return data;
		}

		/// <summary>局内升级增益详情（图鉴 Buff 分类 / 状态页增益卡）</summary>
		public static ItemDetailPanelData ForBuff(string id)
		{
			var b = GameDataMgr.Instance.GetBuffUpgrade(id);
			if (b == null) return null;

			var data = new ItemDetailPanelData
			{
				title = "增益详情",
				iconPath = string.IsNullOrEmpty(b.iconPath) ? b.rankIconPath : b.iconPath,
				itemName = b.buffName,
				description = b.description,
			};
			data.Add("阶级", b.tier.ToString());
			data.Add("权重", b.weight.ToString("F2"));
			data.Add("类型", BuffTypeText(b.buffType));
			return data;
		}

		/// <summary>追加武器基础属性：只显示非 0 项；法力两类由武器独立字段提供，此处不含</summary>
		private static void AppendBaseAttrs(ItemDetailPanelData data, PlayerAttribute a)
		{
			if (a.MaxHp != 0f) data.Add("基础生命值", a.MaxHp.ToString("F0"));
			if (a.Attack != 0f) data.Add("基础攻击", a.Attack.ToString("F0"));
			if (a.PhysDef != 0f) data.Add("基础物理防御", a.PhysDef.ToString("F0"));
			if (a.ElemDef != 0f) data.Add("基础元素防御", a.ElemDef.ToString("F0"));
			if (a.MoveSpeed != 0f) data.Add("基础移动速度", a.MoveSpeed.ToString("F1"));
			if (a.AttackSpeed != 0f) data.Add("基础攻击速度", a.AttackSpeed.ToString("F2"));
			if (a.DodgeChance != 0f) data.Add("基础闪避几率", (a.DodgeChance * 100f).ToString("F1") + "%");
			if (a.CritChance != 0f) data.Add("基础暴击率", (a.CritChance * 100f).ToString("F1") + "%");
			if (a.CritDamage != 0f) data.Add("基础暴击伤害", (a.CritDamage * 100f).ToString("F0") + "%");
			if (a.PhysDamageBonus != 0f) data.Add("基础物理增伤", (a.PhysDamageBonus * 100f).ToString("F1") + "%");
			if (a.ElemDamageBonus != 0f) data.Add("基础元素增伤", (a.ElemDamageBonus * 100f).ToString("F1") + "%");
		}

		private static string IngredientTypeText(IngredientType type)
		{
			switch (type)
			{
				case IngredientType.Vegetable: return "蔬菜";
				case IngredientType.Meat: return "肉类";
				case IngredientType.Spice: return "香料";
				default: return "通用";
			}
		}

		private static string EnemyBehaviorText(EnemyBehavior behavior)
		{
			switch (behavior)
			{
				case EnemyBehavior.SlowChase: return "慢速追击";
				case EnemyBehavior.FastCharge: return "快速冲锋";
				case EnemyBehavior.Stationary: return "静止/自爆";
				case EnemyBehavior.Boss: return "BOSS";
				default: return behavior.ToString();
			}
		}

		/// <summary>BOSS 弱点属性文案（枚举 → 中文；与食材分类同一套叫法）</summary>
		private static string AttackTypeText(AttackType type)
		{
			switch (type)
			{
				case AttackType.Vegetable: return "蔬菜";
				case AttackType.Meat: return "肉类";
				case AttackType.Spice: return "香料";
				case AttackType.Universal: return "通用";
				default: return type.ToString();
			}
		}

		/// <summary>
		/// 武器对应弱点（WeaponConfig.attackType → BOSS 弱点匹配）。
		/// BOSS 弱点判定是严格相等（BossHealth.TakeDamage：attackType == phase.weaknessType），
		/// 而各阶段 weaknessType 只配 蔬菜/肉类/香料 → Universal 永不匹配，故通用武器显示「无」：
		/// 显示枚举名「通用」会被读成克制所有弱点，与实机完全相反。
		/// ⚠️ AttrValue 列宽固定 160px（font 25）且 VerticalOverflow=Truncate，文案必须短，不加解释性后缀。
		/// </summary>
		private static string WeaponWeaknessText(AttackType type)
		{
			return type == AttackType.Universal ? "无" : AttackTypeText(type);
		}

		/// <summary>升级增益类型文案（枚举 → 中文；文案与角色属性行保持同一套叫法）</summary>
		private static string BuffTypeText(UpgradeBuffType type)
		{
			switch (type)
			{
				case UpgradeBuffType.Attack: return "攻击";
				case UpgradeBuffType.MaxHp: return "生命值";
				case UpgradeBuffType.Heal: return "回血";
				case UpgradeBuffType.CritChance: return "暴击率";
				case UpgradeBuffType.CritDamage: return "暴击伤害";
				case UpgradeBuffType.MoveSpeed: return "移动速度";
				case UpgradeBuffType.PhysDamage: return "物理增伤";
				case UpgradeBuffType.ElemDamage: return "元素增伤";
				case UpgradeBuffType.DropMultiplier: return "掉落倍率";
				case UpgradeBuffType.Dodge: return "闪避几率";
				case UpgradeBuffType.Effect: return "特殊效果";
				default: return "未知";
			}
		}
	}
}
