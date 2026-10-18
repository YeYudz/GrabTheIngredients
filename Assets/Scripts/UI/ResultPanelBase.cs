using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using YokiFrame;

namespace GameUI
{
	/// <summary>
	/// 三个结算面板（阶段 / 通关 / 阵亡）的公共基类：节点解析、页签切换与列表条目构建。
	/// 子类只负责各自快照、三段内容填充与按钮语义，列表与页签机制在此复用。
	/// 节点结构由预制体约定，母版取自 Resources/Prefabs/UI；Inspector 绑定优先，槽位为空时按节点名查找兜底。
	/// </summary>
	public abstract class ResultPanelBase : UIPanel
	{
		[Header("标题 / 页签")]
		[SerializeField] protected Text mTitleText;
		[SerializeField] protected ToggleGroup mTabGroup;
		[SerializeField] protected GameObject[] mTabRoots;   // [0]战绩 [1]收获 [2]属性

		// ===== 列表容器 =====
		[Header("列表容器（拖 TabRoot0/1/2 下 ScrollView 的 Content）")]
		[Tooltip("战绩页 TabRoot0 的 ScrollView/Viewport/Content")]
		[SerializeField] protected RectTransform mStatsContent;
		[Tooltip("收获页 TabRoot1 的 ScrollView/Viewport/Content")]
		[SerializeField] protected RectTransform mRewardsContent;
		[Tooltip("属性页 TabRoot2 的 ScrollView/Viewport/Content")]
		[SerializeField] protected RectTransform mAttrsContent;

		// ===== 条目母版（仅作克隆源） =====
		[Header("列表母版（拖各 Content 下的模板节点）")]
		[Tooltip("战绩页母版：RecordGrid（BackGround | AttrName | AttrValue）")]
		[SerializeField] protected GameObject mStatsRowMaster;
		[Tooltip("属性页母版：RecordGrid（与战绩页同 prefab，但各自拖一份）")]
		[SerializeField] protected GameObject mAttrsRowMaster;
		[Tooltip("收获页母版：RecordBagItem（ItemIcon | ItemName | ItemCount）")]
		[SerializeField] protected GameObject mRewardsItemMaster;

		/// <summary>本次运行时创建的条目（面板关闭 / 重新刷新时统一销毁）</summary>
		private readonly List<GameObject> mDynamicItems = new();

		private const string kContentNode = "Content";
		private const string kRecordGridNode = "RecordGrid";        // 战绩页 / 属性页 名值行
		private const string kRecordBagItemNode = "RecordBagItem";  // 收获页 物品格
		private const string kAttrNameNode = "AttrName";
		private const string kAttrValueNode = "AttrValue";
		private const string kItemNameNode = "ItemName";
		private const string kItemCountNode = "ItemCount";
		private const string kItemIconNode = "ItemIcon";

		/// <summary>名值行宽（与 RecordGrid 母版 width 一致；仅用于运行时自建的文本标签）</summary>
		private const float kRowWidth = 900f;

		// ===== 引用解析 =====

		protected void ResolveRefs()
		{
			if (mTitleText == null) mTitleText = FindComponent<Text>("TitleText");
			if (mTabGroup == null) mTabGroup = FindComponent<ToggleGroup>("TabGroup");

			// 绑定值必须通过合法内容容器校验：误绑 Tab0~2 会在切页签时把页签自身一起隐藏
			mTabRoots = new[]
			{
				ResolveRoot(GetBoundRoot(0), "TabRoot0"),
				ResolveRoot(GetBoundRoot(1), "TabRoot1"),
				ResolveRoot(GetBoundRoot(2), "TabRoot2")
			};

		if (mStatsContent == null) mStatsContent = FindContent(mTabRoots[0]);
			if (mRewardsContent == null) mRewardsContent = FindContent(mTabRoots[1]);
			if (mAttrsContent == null) mAttrsContent = FindContent(mTabRoots[2]);

		if (mStatsRowMaster == null) mStatsRowMaster = FindMaster(mStatsContent, kRecordGridNode);
			if (mAttrsRowMaster == null) mAttrsRowMaster = FindMaster(mAttrsContent, kRecordGridNode);
			if (mRewardsItemMaster == null) mRewardsItemMaster = FindMaster(mRewardsContent, kRecordBagItemNode);

			if (mStatsContent == null || mRewardsContent == null || mAttrsContent == null)
				LogKit.Warning($"[{GetType().Name}] 缺少列表容器，请检查预制体 TabRoot0/1/2 下的 Viewport/Content（或手动拖入）");
			if (mStatsRowMaster == null) LogKit.Warning($"[{GetType().Name}] 未找到战绩页母版 {kRecordGridNode}（或未拖入）");
			if (mAttrsRowMaster == null) LogKit.Warning($"[{GetType().Name}] 未找到属性页母版 {kRecordGridNode}（或未拖入）");
			if (mRewardsItemMaster == null) LogKit.Warning($"[{GetType().Name}] 未找到收获页母版 {kRecordBagItemNode}（或未拖入）");
		}

		private GameObject GetBoundRoot(int index)
			=> (mTabRoots != null && index < mTabRoots.Length) ? mTabRoots[index] : null;

		/// <summary>解析内容根：合法的 Inspector 绑定 → 按节点名查找</summary>
		protected GameObject ResolveRoot(GameObject bound, string nodeName)
		{
			if (IsValidRoot(bound)) return bound;

			if (bound != null)
				LogKit.Warning($"[{GetType().Name}] mTabRoots 里的 {bound.name} 不是内容容器，已改用按名查找的 {nodeName}");

			var node = FindChild(transform, nodeName);
			return node != null ? node.gameObject : null;
		}

		/// <summary>
		/// 合法内容根：非空、不是面板根自身、不在页签组之下（否则会隐藏页签按钮）、
		/// 也不能把页签组包在里面（否则隐藏内容时会连页签一起隐藏）
		/// </summary>
		private bool IsValidRoot(GameObject go)
		{
			if (go == null) return false;
			if (go.transform == transform) return false;
			if (mTabGroup != null)
			{
				if (go.transform.IsChildOf(mTabGroup.transform)) return false;
				if (mTabGroup.transform.IsChildOf(go.transform)) return false;
			}
			return true;
		}

		protected T FindComponent<T>(string nodeName) where T : Component
		{
			var node = FindChild(transform, nodeName);
			return node != null ? node.GetComponent<T>() : null;
		}

		/// <summary>深度优先按名称查找子节点（不包含自身）</summary>
		protected static Transform FindChild(Transform parent, string nodeName)
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

		/// <summary>取内容根下的 ScrollView Content（列表挂载点）</summary>
		private static RectTransform FindContent(GameObject tabRoot)
		{
			if (tabRoot == null) return null;
			return FindChild(tabRoot.transform, kContentNode) as RectTransform;
		}

		/// <summary>取 Content 下的母版节点（按名精确匹配；母版名与模板预制体同名）</summary>
		private static GameObject FindMaster(RectTransform content, string masterName)
		{
			if (content == null) return null;
			var node = content.Find(masterName);
			return node != null ? node.gameObject : null;
		}

		// ===== 页签 =====

		protected void BindTabs()
		{
			if (mTabGroup == null) return;

			var toggles = mTabGroup.GetComponentsInChildren<Toggle>(true);
			int startIndex = 0;
			for (int i = 0; i < toggles.Length; i++)
			{
				int index = i;
				toggles[i].onValueChanged.RemoveAllListeners();
				toggles[i].onValueChanged.AddListener(isOn => { if (isOn) SwitchTo(index); });
				// 初始页签跟随预制体里已勾选的 Toggle（避免高亮页签与显示内容不一致）
				if (toggles[i].isOn) startIndex = i;
			}
			SwitchTo(startIndex);
		}

		protected void SwitchTo(int index)
		{
			if (mTabRoots == null) return;
			for (int i = 0; i < mTabRoots.Length; i++)
				if (mTabRoots[i] != null) mTabRoots[i].SetActive(i == index);
		}

		// ===== 列表构建 =====

		/// <summary>清空三个页签的列表（销毁本次运行创建的条目；母版只隐藏不销毁）</summary>
		protected void ClearAllLists()
		{
			ClearList(mStatsContent);
			ClearList(mRewardsContent);
			ClearList(mAttrsContent);
		}

		/// <summary>
		/// 只销毁登记过的动态条目；母版等预置子节点只隐藏——销毁母版会让后续克隆全部失败（列表全空）。
		/// </summary>
		private void ClearList(RectTransform container)
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

		/// <summary>新增一条「名称 | 数值」行（母版 RecordGrid）</summary>
		protected void AddRow(RectTransform content, GameObject master, string name, string value)
		{
			var go = InstantiateEntry(master, content);
			if (go == null) return;

			SetChildText(go, kAttrNameNode, name);
			SetChildText(go, kAttrValueNode, value);
		}

		/// <summary>新增加一行「属性变化量」（值为 0 的行自动跳过）</summary>
		protected bool AddDeltaRow(RectTransform content, GameObject master,
			string name, float delta, string suffix = "")
		{
			if (Mathf.Abs(delta) < 0.01f) return false;
			string sign = delta > 0f ? "+" : "";
			AddRow(content, master, name, $"{sign}{delta:F0}{suffix}");
			return true;
		}

		/// <summary>新增一个物品格（母版 RecordBagItem：ItemIcon | ItemName | ×ItemCount）</summary>
		protected void AddItemCell(RectTransform content, string iconPath, string itemName, int count)
		{
			var go = InstantiateEntry(mRewardsItemMaster, content);
			if (go == null) return;

			SetChildText(go, kItemNameNode, itemName);
			SetChildText(go, kItemCountNode, $"×{count}");
			SetChildIcon(go, kItemIconNode, iconPath);
		}

		/// <summary>
		/// 新增一个「空态」物品格：复用 RecordBagItem 母版，只写名称、隐藏图标与数量。
		/// 用于收获页（网格布局，塞不进普通文本节点）的「（无）」提示。
		/// </summary>
		protected void AddEmptyItemCell(RectTransform content, string text)
		{
			var go = InstantiateEntry(mRewardsItemMaster, content);
			if (go == null) return;

			SetChildText(go, kItemNameNode, text);
			SetChildActive(go, kItemCountNode, false);
			SetChildIcon(go, kItemIconNode, null);
		}

		/// <summary>新增一段说明 / 小标题文本（随列表滚动，参与 VerticalLayoutGroup 排版）</summary>
		protected void AddListLabel(RectTransform content, string text,
			int fontSize = 24, TextAnchor anchor = TextAnchor.MiddleLeft)
		{
			if (content == null) return;

			var go = new GameObject("ListLabel", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
			var rt = (RectTransform)go.transform;
			rt.SetParent(content, false);
			rt.anchorMin = new Vector2(0.5f, 1f);
			rt.anchorMax = new Vector2(0.5f, 1f);
			rt.pivot = new Vector2(0.5f, 1f);
			rt.sizeDelta = new Vector2(kRowWidth, 40f);

			var label = go.GetComponent<Text>();
			label.font = ResolveFont();
			label.fontSize = fontSize;
			label.alignment = anchor;
			label.color = new Color(0.78f, 0.78f, 0.84f);
			label.horizontalOverflow = HorizontalWrapMode.Wrap;
			label.text = text;

			mDynamicItems.Add(go);
		}

		// ===== 通用工具 =====

		private GameObject InstantiateEntry(GameObject master, RectTransform parent)
		{
			if (master == null || parent == null) return null;

			var go = Instantiate(master, parent, false);
			go.SetActive(true);
			mDynamicItems.Add(go);
			return go;
		}

		/// <summary>设置子节点文本（找不到节点则忽略）</summary>
		private static void SetChildText(GameObject root, string childName, string value)
		{
			var node = FindChild(root.transform, childName);
			if (node == null) return;

			var text = node.GetComponent<Text>();
			if (text != null) text.text = value;
		}

		/// <summary>设置子节点显隐（找不到节点则忽略）</summary>
		private static void SetChildActive(GameObject root, string childName, bool active)
		{
			var node = FindChild(root.transform, childName);
			if (node != null) node.gameObject.SetActive(active);
		}

		/// <summary>设置子节点图标；路径为空 / 加载失败时隐藏该节点</summary>
		private static void SetChildIcon(GameObject root, string childName, string iconPath)
		{
			var node = FindChild(root.transform, childName);
			if (node == null) return;

			var image = node.GetComponent<Image>();
			if (image == null) return;

			var sprite = string.IsNullOrEmpty(iconPath) ? null : Resources.Load<Sprite>(iconPath);
			if (sprite != null)
			{
				image.sprite = sprite;
				image.gameObject.SetActive(true);
			}
			else
			{
				image.gameObject.SetActive(false);
			}
		}

		/// <summary>字体：优先沿用面板自带文本的字体（继承其中文回退配置），否则用内置字体</summary>
		private Font ResolveFont()
		{
			if (mTitleText != null && mTitleText.font != null) return mTitleText.font;
			return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
		}

		protected static string IngredientName(string id)
		{
			if (string.IsNullOrEmpty(id)) return id;
			var cfg = GameDataMgr.Instance.GetIngredient(id);
			return cfg != null && !string.IsNullOrEmpty(cfg.ingredientName) ? cfg.ingredientName : id;
		}

		protected static string IngredientIconPath(string id)
		{
			if (string.IsNullOrEmpty(id)) return null;
			var cfg = GameDataMgr.Instance.GetIngredient(id);
			return cfg != null ? cfg.iconPath : null;
		}

		/// <summary>成品菜条目名：菜谱名 + ★星级（查不到配置时回退菜谱ID）</summary>
		protected static string DishDisplayName(string recipeId, int star)
		{
			var name = recipeId;
			if (!string.IsNullOrEmpty(recipeId))
			{
				var cfg = GameDataMgr.Instance.GetRecipe(recipeId);
				if (cfg != null && !string.IsNullOrEmpty(cfg.recipeName)) name = cfg.recipeName;
			}
			return $"{name} ★{star}";
		}

		/// <summary>成品菜图标路径（查不到配置返回 null，图标节点自动隐藏）</summary>
		protected static string DishIconPath(string recipeId)
		{
			if (string.IsNullOrEmpty(recipeId)) return null;
			var cfg = GameDataMgr.Instance.GetRecipe(recipeId);
			return cfg != null ? cfg.iconPath : null;
		}

		protected static string FormatTime(float seconds)
		{
			int t = Mathf.Max(0, Mathf.FloorToInt(seconds));
			return $"{t / 60:00}:{t % 60:00}";
		}

		/// <summary>
		/// 把「解锁菜谱：宫保鸡丁」这类收获文案拆成 名称 / 内容 两段。
		/// 没有分隔符的整句（如「解锁局外永久Buff（传说奖励）」）整段落在名称列，内容列留空。
		/// </summary>
		protected static void SplitRewardLine(string raw, out string name, out string value)
		{
			name = raw ?? "";
			value = "";
			if (string.IsNullOrEmpty(raw)) return;

			int i = raw.IndexOf('：');
			if (i < 0) i = raw.IndexOf(':');
			if (i <= 0 || i >= raw.Length - 1) return;

			name = raw.Substring(0, i);
			value = raw.Substring(i + 1);
		}

		/// <summary>结算面板打开：根节点淡入 + XxxWindow 缩放弹入（公共 UiPanelTween）</summary>
		protected override void OnShow()
		{
			UiPanelTween.PlayIn(this, 0.24f);
		}

		protected override void OnClose()
		{
			foreach (var go in mDynamicItems)
				if (go != null) Destroy(go);
			mDynamicItems.Clear();
		}
	}
}
