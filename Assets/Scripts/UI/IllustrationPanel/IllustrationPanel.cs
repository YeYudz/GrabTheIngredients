using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	[System.Serializable]
	public sealed class IllustrationPanelData : IUIData
	{
	}

	/// <summary>
	/// 全物品图鉴面板：角色 / 武器 / 菜品 / 技能 / 敌人 / 增益 的分类浏览与详情。
	/// 六类数据源统一映射为 Entry 后渲染，解锁状态按各自口径取自 MetaManager 与 RecipeAtlasManager。
	/// 节点结构由预制体约定；母版缺失时退化为代码构建网格项。
	/// </summary>
	public partial class IllustrationPanel : UIPanel
	{
		private enum Category { All, Character, Weapon, Dish, Skill, Enemy, Buff }

		/// <summary>统一图鉴条目（跨 6 类数据源的展示镜像）</summary>
		private sealed class Entry
		{
			public string Id;
			public string Name;
			public string IconPath;
			public string Description;
			public bool Unlocked;
			public Category Category;
		}

		private readonly List<Entry> mAllEntries = new();   // 全量条目（渲染时按分类过滤）
		private readonly List<GameObject> mItems = new();   // 网格实例
		private Category mCurrent = Category.All;

		protected override void OnInit(IUIData data = null)
		{
			mData = data as IllustrationPanelData ?? new IllustrationPanelData();

			if (mCloseButton != null)
			{
				mCloseButton.onClick.RemoveAllListeners();
				mCloseButton.onClick.AddListener(CloseSelf);
			}
			// 未绑定时退化：取 Content 下首个子节点作为母版
			if (mItemTemplate == null && mGridContent != null && mGridContent.childCount > 0)
				mItemTemplate = mGridContent.GetChild(0).gameObject;
			if (mItemTemplate != null) mItemTemplate.SetActive(false);
			if (mGridContent == null) LogKit.Warning("[IllustrationPanel] 未绑定 GridContent，无法显示网格");

			BindToggles();
			EnsureDefaultToggle();
		}

		protected override void OnOpen(IUIData data = null)
		{
			mData = data as IllustrationPanelData ?? mData;
			Rebuild();
		}

		protected override void OnShow() => UiPanelTween.PlayIn(this);
		protected override void OnHide() { }

		protected override void OnClose()
		{
			ClearItems();
			mAllEntries.Clear();
		}

		// ===== 分类切换 =====

		private void BindToggles()
		{
			if (mToggleGroup == null) return;
			var toggles = mToggleGroup.GetComponentsInChildren<Toggle>(true);
			foreach (var t in toggles)
			{
				var cat = CategoryByName(t.name);
				if (cat == null) continue;
				var target = cat.Value;
				t.onValueChanged.RemoveAllListeners();
				t.onValueChanged.AddListener(on => { if (on) SetCategory(target); });
			}
		}

		private Category? CategoryByName(string toggleName)
		{
			switch (toggleName)
			{
				case "ToggleAll": return Category.All;
				case "ToggleCharacter": return Category.Character;
				case "ToggleWeapon": return Category.Weapon;
				case "ToggleDish": return Category.Dish;
				case "ToggleSkill": return Category.Skill;
				case "ToggleEnemy": return Category.Enemy;
				case "ToggleBuff": return Category.Buff;
				default: return null;
			}
		}

		/// <summary>
		/// 默认选中 ToggleAll（全部）：强制唯一选中，避免预制体初始 isOn 全 true 导致"点两次才切换"
		/// 用 SetIsOnWithoutNotify 不触发回调（OnOpen 时 Rebuild 会统一刷新）
		/// </summary>
		private void EnsureDefaultToggle()
		{
			if (mToggleGroup == null) return;
			foreach (var t in mToggleGroup.GetComponentsInChildren<Toggle>(true))
			{
				bool isAll = t.name == "ToggleAll";
				t.SetIsOnWithoutNotify(isAll);
			}
		}

		private void SetCategory(Category cat)
		{
			mCurrent = cat;
			Rebuild();
		}

		// ===== 数据装配 =====

		private void Rebuild()
		{
			ClearItems();
			mAllEntries.Clear();

			BuildCharacterEntries();
			BuildWeaponEntries();
			BuildDishEntries();
			BuildSkillEntries();
			BuildEnemyEntries();
			BuildBuffEntries();

			if (mGridContent == null) return;

			foreach (var e in mAllEntries)
			{
				if (mCurrent != Category.All && e.Category != mCurrent) continue;
				var go = CreateGridItem(e);
				if (go != null) mItems.Add(go);
			}

			// 默认显示第一条详情
			Entry first = null;
			foreach (var e in mAllEntries)
			{
				if (mCurrent != Category.All && e.Category != mCurrent) continue;
				first = e;
				break;
			}
			if (first != null) ShowDetail(first);
			else ClearDetail();

			// 切换分类会重建网格：补挂按钮悬停缩放
			UiHoverInstaller.Install(gameObject);
		}

		private void BuildCharacterEntries()
		{
			var wrapper = GameDataMgr.Instance?.Characters;
			if (wrapper == null || wrapper.Characters == null) return;
			foreach (var c in wrapper.Characters)
			{
				if (c == null || string.IsNullOrEmpty(c.id)) continue;
				mAllEntries.Add(new Entry
				{
					Id = c.id,
					Name = c.characterName,
					IconPath = c.iconPath,
					Description = c.description,
					Unlocked = MetaManager.HasInstance && MetaManager.Instance.HasCharacter(c.id),
					Category = Category.Character
				});
			}
		}

		private void BuildWeaponEntries()
		{
			var wrapper = GameDataMgr.Instance?.Weapons;
			if (wrapper == null || wrapper.Weapons == null) return;
			foreach (var w in wrapper.Weapons)
			{
				if (w == null || string.IsNullOrEmpty(w.id)) continue;
				mAllEntries.Add(new Entry
				{
					Id = w.id,
					Name = w.weaponName,
					IconPath = w.iconPath,
					Description = w.description,
					Unlocked = MetaManager.HasInstance && MetaManager.Instance.GetWeaponCount(w.id) > 0,
					Category = Category.Weapon
				});
			}
		}

		private void BuildDishEntries()
		{
			if (!RecipeAtlasManager.HasInstance) return;
			// 图鉴"菜品" = 成品菜（RecipeConfig，局外"饰品"）；画中画炒菜资源(CookingRecipeConfig)不计入
			foreach (var item in RecipeAtlasManager.Instance.Items)
			{
				if (item == null || string.IsNullOrEmpty(item.Id)) continue;
				mAllEntries.Add(new Entry
				{
					Id = item.Id,
					Name = item.Name,
					IconPath = item.IconPath,
					Description = item.Description,
					Unlocked = RecipeAtlasManager.Instance.IsUnlocked(item.Id),
					Category = Category.Dish
				});
			}
		}

		private void BuildSkillEntries()
		{
			var wrapper = GameDataMgr.Instance?.ActiveSkills;
			if (wrapper == null || wrapper.Skills == null) return;
			foreach (var s in wrapper.Skills)
			{
				if (s == null || string.IsNullOrEmpty(s.id)) continue;
				mAllEntries.Add(new Entry
				{
					Id = s.id,
					Name = s.skillName,
					IconPath = s.iconPath,
					Description = s.description,
					Unlocked = RosterManager.IsSkillAvailable(s.id),   // 统一解锁口径（见 RosterManager.IsSkillAvailable）
					Category = Category.Skill
				});
			}
		}

		private void BuildEnemyEntries()
		{
			var wrapper = GameDataMgr.Instance?.Enemies;
			if (wrapper == null || wrapper.Enemies == null) return;
			// 占位：缺少按敌人 ID 的击杀记录，暂以全局历史击杀数 > 0 判定解锁
			bool anyKill = MetaManager.HasInstance && MetaManager.Instance.Data.bestKills > 0;
			foreach (var e in wrapper.Enemies)
			{
				if (e == null || string.IsNullOrEmpty(e.id)) continue;
				mAllEntries.Add(new Entry
				{
					Id = e.id,
					Name = e.enemyName,
					IconPath = e.iconPath,
					Description = string.IsNullOrEmpty(e.description) ? "暂无描述" : e.description,
					Unlocked = anyKill,
					Category = Category.Enemy
				});
			}

			// BOSS 并入敌人页签（解锁口径与普通敌人一致）
			var bosses = GameDataMgr.Instance?.Bosses;
			if (bosses == null || bosses.Bosses == null) return;
			foreach (var b in bosses.Bosses)
			{
				if (b == null || string.IsNullOrEmpty(b.id)) continue;
				mAllEntries.Add(new Entry
				{
					Id = b.id,
					Name = b.bossName,
					IconPath = b.iconPath,
					Description = string.IsNullOrEmpty(b.description) ? "暂无描述" : b.description,
					Unlocked = anyKill,
					Category = Category.Enemy
				});
			}
		}

		private void BuildBuffEntries()
		{
			var wrapper = GameDataMgr.Instance?.Buffs;
			if (wrapper == null || wrapper.Buffs == null) return;
			foreach (var b in wrapper.Buffs)
			{
				if (b == null || string.IsNullOrEmpty(b.id)) continue;
				// 解锁 = 历史选择过该 Buff（出现但未选择的视为未解锁）
				bool unlocked = MetaManager.HasInstance && MetaManager.Instance.HasSelectedBuff(b.id);
				mAllEntries.Add(new Entry
				{
					Id = b.id,
					Name = b.buffName,
					IconPath = string.IsNullOrEmpty(b.iconPath) ? b.rankIconPath : b.iconPath,
					Description = b.description,
					Unlocked = unlocked,
					Category = Category.Buff
				});
			}
		}

		// ===== 网格构建 =====

		private GameObject CreateGridItem(Entry e)
		{
			var go = mItemTemplate != null ? Instantiate(mItemTemplate, mGridContent) : CreateDefaultGridItem();
			go.name = "IllustrationGrid_" + e.Category + "_" + e.Id;
			go.SetActive(true);

		var itemIcon = FindChild(go.transform, "ItemIcon")?.GetComponent<Image>();
			if (itemIcon != null)
			{
				var icon = e.Unlocked ? LoadIcon(e.IconPath) : null;
				itemIcon.gameObject.SetActive(icon != null);
				if (icon != null) itemIcon.sprite = icon;
			}

			// UnLockText 文案由预制体编辑，代码只管显隐
			var unlockText = FindChild(go.transform, "UnLockText");
			if (unlockText != null) unlockText.gameObject.SetActive(e.Unlocked);

		var lockPicture = FindChild(go.transform, "LockPicture");
			if (lockPicture != null) lockPicture.gameObject.SetActive(!e.Unlocked);

		var btn = go.GetComponent<Button>();
			if (btn == null) btn = go.AddComponent<Button>();
			btn.onClick.RemoveAllListeners();
			Entry entry = e;
			btn.onClick.AddListener(() => ShowDetail(entry));

			// 长按：已解锁条目打开物品详情弹窗（单击语义保持不变，两者不冲突；未解锁不开放）
			if (e.Unlocked)
			{
				var longPress = go.GetComponent<LongPressTrigger>();
				if (longPress == null) longPress = go.AddComponent<LongPressTrigger>();
				longPress.onLongPress = () => OpenEntryDetail(entry);
			}

			return go;
		}

		private GameObject CreateDefaultGridItem()
		{
			var go = new GameObject("IllustrationGrid", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
			var rt = (RectTransform)go.transform;
			rt.SetParent(mGridContent, false);
			rt.sizeDelta = new Vector2(160f, 160f);
			go.GetComponent<Image>().color = new Color(0.2f, 0.24f, 0.32f);

			CreateDefaultLock(go.transform);
			return go;
		}

		private static void CreateDefaultLock(Transform parent)
		{
			var go = new GameObject("LockPicture", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = (RectTransform)go.transform;
			rt.SetParent(parent, false);
			rt.anchorMin = Vector2.zero;
			rt.anchorMax = Vector2.one;
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
			var text = go.GetComponent<Text>();
			text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			text.fontSize = 48;
			text.alignment = TextAnchor.MiddleCenter;
			text.text = "?";
			text.color = Color.gray;
			text.raycastTarget = false;
		}

		// ===== 详情 =====

		private void ShowDetail(Entry e)
		{
			if (e == null) return;

			if (mDetailIcon != null)
			{
				var sprite = LoadIcon(e.IconPath);
				mDetailIcon.sprite = sprite;
				mDetailIcon.gameObject.SetActive(e.Unlocked && sprite != null);
			}
			if (mDetailNameText != null)
				mDetailNameText.text = e.Name;
			if (mDetailDescText != null)
				mDetailDescText.text = e.Unlocked ? e.Description : "??????";
			if (mDetailUnLockTipText != null)
			{
				mDetailUnLockTipText.text = e.Unlocked ? "已解锁" : "未解锁";
				mDetailUnLockTipText.color = e.Unlocked ? new Color(0.35f, 0.8f, 0.35f) : Color.gray;
			}
		}

		private void ClearDetail()
		{
			if (mDetailIcon != null) mDetailIcon.gameObject.SetActive(false);
			if (mDetailNameText != null) mDetailNameText.text = "";
			if (mDetailDescText != null) mDetailDescText.text = "";
			if (mDetailUnLockTipText != null) mDetailUnLockTipText.text = "";
		}

		/// <summary>长按打开物品详情弹窗（按图鉴分类经 ItemDetailFactory 读取配置填充）</summary>
		private void OpenEntryDetail(Entry e)
		{
			if (e == null || !e.Unlocked) return;

			ItemDetailPanelData data = null;
			switch (e.Category)
			{
				case Category.Character: data = ItemDetailFactory.ForCharacter(e.Id); break;
				case Category.Weapon: data = ItemDetailFactory.ForWeapon(e.Id); break;
				case Category.Dish: data = ItemDetailFactory.ForDish(e.Id); break;
				case Category.Skill: data = ItemDetailFactory.ForSkill(e.Id); break;
				case Category.Enemy: data = ItemDetailFactory.ForEnemy(e.Id) ?? ItemDetailFactory.ForBoss(e.Id); break;
				case Category.Buff: data = ItemDetailFactory.ForBuff(e.Id); break;
			}
			if (data == null) return;
			UIKit.OpenPanel<ItemDetailPanel>(UILevel.Pop, data);
		}

		// ===== 工具 =====

		private static Sprite LoadIcon(string path)
		{
			return string.IsNullOrEmpty(path) ? null : Resources.Load<Sprite>(path);
		}

		/// <summary>深度优先按名称查找子节点（不包含自身）；仅用于列表项内部</summary>
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

		private void ClearItems()
		{
			foreach (var go in mItems)
				if (go != null) Destroy(go);
			mItems.Clear();
		}

		private void CloseSelf()
		{
			if (AudioManager.HasInstance) AudioManager.Instance.PlayUiClick();
			UIKit.ClosePanel<IllustrationPanel>();
		}
	}
}
