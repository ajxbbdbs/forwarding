// WorldfallHotbar —— 为 Worldfall 增加 Minecraft 风格快捷栏 / 背包。
// 由 NeoModLoader 与 WorldfallMod.cs 一起在现场编译（引用 Assemblies/FirstPerson.dll 与游戏程序集）。
//
// 功能：
//   B        打开/关闭 背包面板（MC 灰色等距网格风格：第一行武器，其后为背包资源）
//   1-9      选择快捷栏槽位（选到武器会自动装备）
//   鼠标滚轮 切换选中槽位（会抵消 Worldfall 自带的 FOV 缩放）
//   左键点击 槽位同数字键；面板中点击武器格 = 装备
//   背包上限 原 24 个资源 → 27 格 × 每格 64 = 1728（MC 式按格堆叠，transpiler 改写 IL 常量）
// 布局：快捷栏贴在血条（vitals）正上方，绝不遮挡血条；仅第一人称激活时显示。
// FirstPerson 的内部状态一律走反射，任何失败都静默降级，绝不抛异常打断游戏。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using FirstPerson; // public WorldBoxMod 入口类型
using HarmonyLib;  // NML 自带（StreamingAssets/mods/NML/Assemblies/0Harmony.dll）
using UnityEngine;
using Object = UnityEngine.Object;

namespace WorldfallNml
{
    [DefaultExecutionOrder(200)] // 晚于 FirstPerson.WorldBoxMod.Update，便于覆盖滚轮/光标
    public class WorldfallHotbar : MonoBehaviour
    {
        private const int BaseSlots = 9;  // 基础 9 格（数字键 1-9 对应）
        private const int MaxSlots = 15;  // 仓库武器多时快捷栏最多扩到 15 格
        private int SlotCount => Mathf.Max(BaseSlots, Mathf.Min(MaxSlots, _slots.Count));
        private const float RebuildInterval = 0.5f;

        // ---------- 反射缓存（FirstPerson 内部成员） ----------
        private static bool _reflReady;
        private static FieldInfo _fiHost;        // WorldBoxMod._host
        private static FieldInfo _fiActive;      // WorldBoxMod._active
        private static FieldInfo _fiSettings;    // WorldBoxMod.Settings
        private static FieldInfo _fiHud;         // WorldBoxMod._hud
        private static FieldInfo _fiFov;         // Settings.FieldOfView
        private static FieldInfo _fiVitals;      // Hud._vitalsRect
        private static FieldInfo _fiBlockAttack; // PossessionHooks.BlockAttack (static)
        private static FieldInfo _fiGameArtP;    // GameArt.P (static float)
        private static MethodInfo _miIconNamed;  // GameArt.IconNamed(string) -> Sprite
        private static MethodInfo _miGetString;  // LocalizedTextManager.getText(string,...) -> string（按游戏语言返回）
        private static int _miGetStringArgs;     // getText 参数个数（兼容不同重载）
        private static MethodInfo _miStringExists; // LocalizedTextManager.stringExists(string) -> bool（缺失 key 不查，避免游戏报错）
        private static FieldInfo _fiCurLang;     // LocalizedTextManager.current_language（当前语言资产）
        private static FieldInfo _fiIsHanzi;     // GameLanguageAsset.is_hanzi（中文类语言标记）
        private static PropertyInfo _piAnyMenu;  // WorldBoxMod.AnyMenu
        private static FieldInfo _fiHolstered;   // WorldBoxMod.Holstered（收起武器状态）
        private static FieldInfo _fiUnholsterFrame; // WorldBoxMod._unholsterFrame（左键掏武器的帧标记，仅该方法会写）

        // 底部按钮排"帮助按钮"（挂在 Hud.DrawMenuButtons 的 postfix 上）
        private static FieldInfo _fiMenuLeft;    // Hud._menuLeft（本排最左元素左缘）
        private static FieldInfo _fiBagTop;      // Hud._bagTop（本排按钮 y）
        private static MethodInfo _miStoneButton; // GameArt.StoneButton(Rect, Sprite, float, bool, Color?)
        private static MethodInfo _miLabel;       // GameArt.Label(Rect, string, float, TextAnchor, Color, bool)
        private static MethodInfo _miSliced;      // GameArt.Sliced(Rect, Sprite, Color, float, float, float, float, float)
        private static FieldInfo _fiKeyOrange;    // GameArt.KeyOrange
        private static FieldInfo _fiTooltipSprite; // GameArt.Tooltip
        private static Rect _helpRect;            // 帮助按钮屏幕矩形（GUI 坐标，供悬停/点击判定）
        private static float _helpAlpha = 1f;     // HUD 当帧透明度（帮助按钮入场动画同步用）
        private static float _helpShowUntil;      // 按 ? 键强制显示键位说明的截止时间（unscaled）
        private static FieldInfo _fiAudio;       // WorldBoxMod._audio (FirstPersonAudio 实例)
        private static FieldInfo _fiMusicVol;    // FirstPersonAudio.MusicVolume
        private static FieldInfo _fiGameWant;    // FirstPersonAudio._gameMusicWant
        private static FieldInfo _fiGameWait;    // FirstPersonAudio._gameMusicWait
        private static FieldInfo _fiSettingsMenu;      // WorldBoxMod._settingsMenu
        private static FieldInfo _fiSettingsMenuRows;  // SettingsMenu._rows (List<Row>[])
        private static FieldInfo _fiRowName;           // Row.Name
        private static FieldInfo _fiRowNote;           // Row.Note
        private static FieldInfo _fiRowMin;            // Row.Min
        private static FieldInfo _fiRowMax;            // Row.Max
        private static FieldInfo _fiRowStep;           // Row.Step
        private static FieldInfo _fiRowRatio;          // Row.Ratio
        private static FieldInfo _fiRowValue;          // Row.Value (Func<Settings,float>)
        private static FieldInfo _fiRowSetValue;       // Row.SetValue (Action<Settings,float>)
        private static FieldInfo _fiRowShow;           // Row.Show (Func<float,string>)

        // ---------- 运行状态 ----------
        private FirstPerson.WorldBoxMod _mod;
        private Actor _host;
        private bool _active;
        private float _fovKeep = -1f;
        private float _rebuildAt;
        private int _sel;
        private bool _lastHolstered; // 上一帧的收起状态（检测 H 键变化，同步快捷栏选中）
        private bool _panel;
        private string _toast;
        private float _toastUntil;
        private Texture2D _white;

        private sealed class HotSlot
        {
            public bool IsWeapon;
            public bool IsCurrent;
            public bool IsTool;   // 常驻采集工具（斧头/铁锤），绿色标记
            public string Id;
            public string Name;
            public int Count;
            public int Nutrition;
            public Sprite Icon;
            public Item RefItem;  // 仓库武器槽：指向被换下的原物品（装回时保留耐久/品质）
        }

        private readonly List<HotSlot> _slots = new List<HotSlot>();
        private Dictionary<string, EquipmentAsset> _itemMap; // 全部可装备武器 id→资产（查表用）

        // ---- 武器仓库：切换武器时被顶下的原物品按生物暂存，避免被打掉的武器丢到地上 ----
        private static readonly Dictionary<Actor, List<Item>> _weaponStash = new Dictionary<Actor, List<Item>>();
        private static MethodInfo _miSlotSetItem; // ActorEquipmentSlot.setItem(Item, Actor)（internal，需反射）

        private static bool _menuInjected;   // 已成功注入（或确认结构不兼容永久放弃）

        // ================= 生命周期 =================

        private void OnEnable()
        {
            EnsureReflection();
            if (_white == null)
            {
                _white = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                        _white.SetPixel(x, y, Color.white);
                _white.Apply(false);
                _white.hideFlags = HideFlags.HideAndDontSave;
            }
            _itemMap = null; // 延迟构建（等 AssetManager 就绪）
            _actorWeapons = null;
        }

        private void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception)
            {
                // 静默：任何问题都不允许打断游戏
            }
        }

        private void Tick()
        {
            _mod = FindFpMod();
            if (_mod == null) return;

            // ---- Worldfall 背景音乐独立控制：不依赖附身状态，只要 mod 存在就生效 ----
            // （每帧覆盖：在他们 Settings 同步之后，本组件执行顺序更晚）
            ApplyMusicVolume(_mod);

            // ---- Ctrl+M：快速静音/恢复 ----
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.M))
            {
                if (WorldfallMusic.Volume > 0.01f) { WorldfallMusic.Volume = 0f; ShowToast(T("Worldfall 背景音乐：关（Ctrl+M 恢复）", "Worldfall music: OFF (Ctrl+M to restore)")); }
                else { WorldfallMusic.Volume = WorldfallMusic.Last > 0.01f ? WorldfallMusic.Last : 1f; ShowToast(T("Worldfall 背景音乐：开 ", "Worldfall music: ON ") + Mathf.RoundToInt(WorldfallMusic.Volume * 100f) + "%"); }
                ApplyMusicVolume(_mod);
            }

            // ---- F2 设置页注入“Worldfall 音乐”滑条（同样不依赖附身） ----
            TryInjectMenuRow();

            // ---- 血条上移补丁（一次性）+ 每帧上移量 ----
            TryPatchVitals();
            VitalsLift = 0f;
            if (_fiActive != null)
            {
                object v = _fiActive.GetValue(_mod);
                if (v is bool && (bool)v && World.world != null)
                {
                    // 快捷栏总高 36p（30p 格 + 2*3p 边距）+ 6p 间隙：把血条整体抬到快捷栏之上
                    VitalsLift = 42f * ArtP();
                }
            }

            _active = false;
            _host = null;
            if (_fiActive != null)
            {
                object v = _fiActive.GetValue(_mod);
                if (v is bool) _active = (bool)v;
            }
            if (!_active) return;
            if (World.world == null) return;
            if (_fiHost != null) _host = _fiHost.GetValue(_mod) as Actor;
            _hostStatic = _host; // 静态镜像（补丁方法用）
            if (!ReferenceEquals(_host, _lastHost))
            {
                List<Item> oldStash = StashOf(_lastHost);
                int oldId;
                string oldTag = (_lastHost == null || !_hostIds.TryGetValue(_lastHost, out oldId)) ? "无" : oldId.ToString();
                WLog("宿主变更: #" + oldTag + " → #" + HostTag()
                    + " → #" + HostTag()
                    + "，旧宿主仓内 " + (oldStash == null ? 0 : oldStash.Count) + " 件");
                _lastHost = _host;
            }

            // ---- H 键收起/拿起状态同步到快捷栏选中位置 ----
            bool holstered = GetHolstered();
            if (holstered != _lastHolstered)
            {
                _lastHolstered = holstered;
                if (holstered)
                {
                    // 收起：选中跳到第一个空槽（按 Id 判空，填充格不是 null）
                    for (int i = 0; i < SlotCount; i++)
                    {
                        if (i >= _slots.Count || _slots[i] == null || string.IsNullOrEmpty(_slots[i].Id)) { _sel = i; break; }
                    }
                }
                else if (_sel >= _slots.Count || _sel >= SlotCount || _slots[_sel] == null || string.IsNullOrEmpty(_slots[_sel].Id))
                {
                    // 拿起：若当前停在空槽，跳回当前武器所在槽（或第一把武器）
                    string wid = CurrentWeaponId(_host);
                    int idx = wid != null ? _slots.FindIndex(x => x != null && x.IsWeapon && x.Id == wid) : -1;
                    if (idx < 0) idx = _slots.FindIndex(x => x != null && x.IsWeapon);
                    if (idx >= 0) _sel = idx;
                }
            }

            bool anyMenu = AnyMenuOpen();

            // 武器显隐完全由快捷栏选择驱动：
            //   滚轮/数字键选中武器槽 → SelectSlot 拿出武器；选中空槽 → SelectSlot 收起（等同按 H）
            //   左键自动掏武器由 Harmony 补丁在 Update 末尾取消（见 UpdateCancelDrawPostfix）

            // ---- 按键 ----
            if (!anyMenu && Input.GetKeyDown(KeyCode.B))
            {
                _panel = !_panel;
                ShowToast(_panel ? T("背包（B 或 Esc 关闭）", "Bag open (B or Esc to close)") : T("背包已关闭", "Bag closed"));
            }
            if (_panel && Input.GetKeyDown(KeyCode.Escape))
            {
                _panel = false;
            }
            if (!anyMenu)
            {
                for (int i = 0; i < BaseSlots; i++) // 数字键只绑基础 9 格
                {
                    if (Input.GetKeyDown((KeyCode)((int)KeyCode.Alpha1 + i)))
                    {
                        SelectSlot(i, true);
                        break;
                    }
                }
                if (Input.GetKeyDown(KeyCode.Delete))
                {
                    DiscardSelected();
                }
                // "?" 键（US 布局即 Shift+/，Slash 与 Question 都监听）：切换键位说明显示
                if (Input.GetKeyDown(KeyCode.Slash) || Input.GetKeyDown(KeyCode.Question))
                {
                    bool show = _helpShowUntil < Time.unscaledTime;
                    _helpShowUntil = show ? Time.unscaledTime + 6f : 0f;
                    ShowToast(show ? T("键位说明已显示（再按 ? 关闭）", "Controls shown (press ? again to hide)") : T("键位说明已关闭", "Controls hidden"));
                }
            }

            // ---- 滚轮：切换槽位，并抵消 FOV 缩放 ----
            float wheel = Input.mouseScrollDelta.y;
            if (wheel != 0f)
            {
                if (!anyMenu)
                {
                    int dir = wheel > 0f ? -1 : 1;
                    SelectSlot((_sel + dir + SlotCount) % SlotCount, true);
                }
                RestoreFov();
            }
            else
            {
                CacheFov();
            }

            // ---- 面板打开 / 悬停快捷栏 / 悬停帮助按钮时，屏蔽攻击并显示光标 ----
            bool helpHover = HelpHover(_helpRect);
            bool hover = HoverHotbar() || (_panel && HoverPanel()) || helpHover;
            if (_fiBlockAttack != null && (_panel || hover))
            {
                try { _fiBlockAttack.SetValue(null, true); } catch { }
            }
            if (_panel || hover)
            {
                try
                {
                    if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                catch { }
            }
            if (helpHover && Input.GetMouseButtonDown(0))
            {
                // 点 ? 按钮 = 显示/关闭键位说明（开背包只有 B 键）
                bool show = _helpShowUntil < Time.unscaledTime;
                _helpShowUntil = show ? Time.unscaledTime + 6f : 0f;
            }

            // ---- 定期重建槽位内容 ----
            if (Time.unscaledTime >= _rebuildAt)
            {
                _rebuildAt = Time.unscaledTime + RebuildInterval;
                RebuildSlots();
            }
        }

        // ---- G 键：丢弃当前选中槽位的物品（类似回收） ----
        // 武器槽（含工具槽）：销毁手上/仓库里对应的武器
        // 仓库槽：销毁存进去的原物品
        // 资源/食物槽：整组丢弃（写回内部字典并移除，模仿游戏自身的 spend 模式）
        private void DiscardSelected()
        {
            if (_host == null || !IsAlive(_host)) return;
            if (_sel >= _slots.Count) return;
            HotSlot s = _slots[_sel];
            if (s == null || string.IsNullOrEmpty(s.Id))
            {
                ShowToast(T("空格子没有东西可丢", "Empty slot - nothing to drop"));
                return;
            }
            if (s.IsWeapon)
            {
                // 优先看手上拿的是不是这把武器
                Item cur = null;
                try { cur = _host.equipment.weapon.getItem(); } catch { }
                if (cur != null && cur.asset != null && cur.asset.id == s.Id)
                {
                    try
                    {
                        if (cur.isCursed()) { ShowToast(T("诅咒武器丢不掉", "Cursed weapons cannot be dropped")); return; }
                        _suppressCapture = true; // 主动丢弃，不让拦截补丁把武器收进仓库
                        try
                        {
                            _host.equipment.weapon.takeAwayItem(); // 卸下（clearUnit，变成无主）
                        }
                        finally
                        {
                            _suppressCapture = false;
                        }
                        cur.setFavorite(false);
                        cur.setShouldBeRemoved();              // 标记回收，游戏统一清理
                        SetHolstered(true);                    // 手空了，保持空手状态
                        WLog("[宿主#" + HostTag() + "] 丢弃当前武器 " + ItemDesc(cur));
                        ShowToast(T("已丢弃 ", "Dropped ") + s.Name);
                    }
                    catch { ShowToast(T("无法丢弃 ", "Could not drop ") + s.Name); }
                }
                else if (s.RefItem != null && StashItemValid(_host, s.RefItem))
                {
                    // 仓库槽：直接销毁存进去的原物品
                    List<Item> l = StashOf(_host);
                    if (l != null) l.Remove(s.RefItem);
                    s.RefItem.setFavorite(false);
                    try { s.RefItem.clearUnit(); } catch { } // 解除宿主认领，否则回收判定不生效
                    s.RefItem.setShouldBeRemoved();
                    WLog("[宿主#" + HostTag() + "] 丢弃仓库武器 " + s.Id);
                    ShowToast(T("已丢弃 ", "Dropped ") + s.Name);
                }
                else
                {
                    ShowToast(T("该槽位没有武器可丢弃", "No weapon in this slot to drop"));
                    return;
                }
            }
            else
            {
                // 资源/食物：MC 式只丢当前选中格的堆叠数（一格最多 64），不是整组
                Dictionary<string, ResourceContainer> bag = GetBag();
                if (bag == null || !bag.ContainsKey(s.Id))
                {
                    ShowToast(T("背包里没有 ", "No ") + s.Name + T("", " in bag"));
                    return;
                }
                ResourceContainer c = bag[s.Id];
                int n = Mathf.Min(s.Count > 0 ? s.Count : c.amount, c.amount); // 本格堆叠数，防御性夹到实际持有量内
                if (n <= 0) return;
                c.amount -= n;
                if (c.amount <= 0) bag.Remove(s.Id);       // 数量归零即移除（与游戏 spendResource 相同模式）
                else bag[s.Id] = c;                        // ResourceContainer 是 struct，剩余部分必须索引器写回
                WLog("[宿主#" + HostTag() + "] 丢弃资源 " + s.Id + " ×" + n + "（剩余 " + Mathf.Max(0, c.amount) + "）");
                ShowToast(T("已丢弃 ", "Dropped ") + s.Name + " x" + n);
            }
            _rebuildAt = 0f; // 立刻刷新快捷栏
        }

        private static void ApplyMusicVolume(FirstPerson.WorldBoxMod mod)
        {
            try
            {
                if (_fiAudio == null || mod == null) return;
                object audio = _fiAudio.GetValue(mod);
                if (audio == null) return;
                if (_fiMusicVol != null) _fiMusicVol.SetValue(audio, WorldfallMusic.Volume);
                // 音量归零时解除对游戏本体音乐的压制（duck），让游戏音乐正常播放
                if (WorldfallMusic.Volume <= 0.001f)
                {
                    if (_fiGameWant != null) _fiGameWant.SetValue(audio, 1f);
                    if (_fiGameWait != null) _fiGameWait.SetValue(audio, 0f);
                }
            }
            catch { }
        }

        // ---- 往 F2 设置菜单“声音”页原生注入“Worldfall 音乐”滑条 ----
        private static int _injectFails;     // 异常重试计数（最多 5 次，间隔 3 秒）
        private static float _nextInjectTry; // 下次重试时间

        private static void TryInjectMenuRow()
        {
            if (_menuInjected || _fiSettingsMenu == null || _fiSettingsMenuRows == null) return;
            // Row 字段桥任何一个没解析到 → 结构不兼容，直接放弃
            if (_fiRowName == null || _fiRowNote == null || _fiRowMin == null || _fiRowMax == null ||
                _fiRowStep == null || _fiRowRatio == null || _fiRowValue == null || _fiRowSetValue == null ||
                _fiRowShow == null)
            {
                _menuInjected = true;
                return;
            }
            if (Time.unscaledTime < _nextInjectTry) return;
            try
            {
                object menu = _fiSettingsMenu.GetValue(FindFpMod());
                if (menu == null) { _nextInjectTry = Time.unscaledTime + 3f; return; }
                Array tabs = _fiSettingsMenuRows.GetValue(menu) as Array;
                if (tabs == null) { _nextInjectTry = Time.unscaledTime + 3f; return; } // Build() 尚未执行（首次打开 F2 后才有）
                int audioTab = -1, musicIdx = -1;
                System.Collections.IList rows = null;
                for (int t = 0; t < tabs.Length; t++)
                {
                    System.Collections.IList list = tabs.GetValue(t) as System.Collections.IList;
                    if (list == null) continue;
                    for (int i = 0; i < list.Count; i++)
                    {
                        object existing = list[i];
                        if (existing == null) continue;
                        if (Convert.ToString(_fiRowName.GetValue(existing)) == "音乐")
                        {
                            audioTab = t; musicIdx = i; rows = list; break;
                        }
                    }
                    if (rows != null) break;
                }
                if (rows == null) { _menuInjected = true; return; } // 没找到“音乐”行：结构变了，放弃
                // 组装 Row：类型是 SettingsMenu+Row（internal 嵌套类），委托用 DynamicMethod 生成
                Assembly fp = typeof(FirstPerson.WorldBoxMod).Assembly;
                Type settingsType = fp.GetType("FirstPerson.Settings");
                Type rowType = fp.GetType("FirstPerson.SettingsMenu+Row");
                if (settingsType == null || rowType == null) { _menuInjected = true; return; }
                object row = Activator.CreateInstance(rowType, true);
                _fiRowName.SetValue(row, LangChinese ? "Worldfall 音乐" : "Worldfall Music");
                _fiRowNote.SetValue(row, LangChinese ? "仅模组自己的背景音乐，不影响游戏音乐与音效" : "Only the mod's own background music; game music and SFX are unaffected");
                _fiRowMin.SetValue(row, 0f);
                _fiRowMax.SetValue(row, 1f);
                _fiRowStep.SetValue(row, 0.05f);
                _fiRowRatio.SetValue(row, false);
                Module module = typeof(WorldfallHotbar).Module;
                // Func<Settings,float> Value -> WorldfallMusic.get_Volume()
                DynamicMethod dv = new DynamicMethod("wfGetMusicVol", typeof(float), new Type[] { settingsType }, module, true);
                ILGenerator il = dv.GetILGenerator();
                il.Emit(OpCodes.Call, typeof(WorldfallMusic).GetMethod("get_Volume"));
                il.Emit(OpCodes.Ret);
                _fiRowValue.SetValue(row, dv.CreateDelegate(typeof(Func<,>).MakeGenericType(settingsType, typeof(float))));
                // Action<Settings,float> SetValue -> WorldfallMusic.set_Volume(float)
                DynamicMethod ds = new DynamicMethod("wfSetMusicVol", null, new Type[] { settingsType, typeof(float) }, module, true);
                ILGenerator il2 = ds.GetILGenerator();
                il2.Emit(OpCodes.Ldarg_1);
                il2.Emit(OpCodes.Call, typeof(WorldfallMusic).GetMethod("set_Volume"));
                il2.Emit(OpCodes.Ret);
                _fiRowSetValue.SetValue(row, ds.CreateDelegate(typeof(Action<,>).MakeGenericType(settingsType, typeof(float))));
                // Func<float,string> Show -> 直接绑定静态方法
                _fiRowShow.SetValue(row, Delegate.CreateDelegate(
                    typeof(Func<,>).MakeGenericType(typeof(float), typeof(string)),
                    typeof(WorldfallMusic).GetMethod("Show", new Type[] { typeof(float) })));
                rows.Insert(musicIdx + 1, row);
                _menuInjected = true;
                Debug.Log("[WorldfallHotbar] 已注入“Worldfall 音乐”滑条到 F2 设置（声音页，位置 " + audioTab + ":" + (musicIdx + 1) + "）");
            }
            catch (Exception ex)
            {
                // 失败不永久放弃：限流重试最多 5 次，仍失败则放弃（不影响游戏）
                _injectFails++;
                if (_injectFails >= 5)
                {
                    _menuInjected = true;
                    Debug.LogWarning("[WorldfallHotbar] 音乐滑条注入失败，已放弃：" + ex.Message);
                }
                else
                {
                    _nextInjectTry = Time.unscaledTime + 3f;
                }
            }
        }

        // ---- Harmony 补丁：把 Hud.DrawVitals 整体上移（纯反射加载 0Harmony，无编译期依赖） ----
        internal static float VitalsLift;   // 本帧血条上移像素（Tick 里设置）
        private static bool _vitalsPatched;

        private static void TryPatchVitals()
        {
            if (_vitalsPatched) return;
            _vitalsPatched = true;
            try
            {
                Assembly fp = typeof(FirstPerson.WorldBoxMod).Assembly;
                Type modType = typeof(FirstPerson.WorldBoxMod);
                Type hudType = fp.GetType("FirstPerson.Hud");
                MethodInfo mv = hudType != null ? hudType.GetMethod("DrawVitals", BindingFlags.NonPublic | BindingFlags.Instance) : null;
                if (mv == null) { Debug.LogWarning("[WorldfallHotbar] 找不到 Hud.DrawVitals，血条上移不可用"); }

                Type artType = fp.GetType("FirstPerson.GameArt");
                MethodInfo ms = artType != null ? artType.GetMethod("BarSlab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static) : null;
                if (ms == null) { Debug.LogWarning("[WorldfallHotbar] 找不到 GameArt.BarSlab，血条底板屏蔽不可用"); }

                // 在已加载程序集里找 0Harmony（BepInEx 自带）
                Assembly har = null;
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == "0Harmony") { har = a; break; }
                }
                if (har == null) { Debug.LogWarning("[WorldfallHotbar] 找不到 0Harmony，血条补丁不可用"); return; }

                Type hType = har.GetType("HarmonyLib.Harmony");
                Type hmType = har.GetType("HarmonyLib.HarmonyMethod");
                if (hType == null || hmType == null) return;

                PropertyInfo preProp = hmType.GetProperty("Prefix");

                // 通用的补丁挂载（兼容 Harmony 1.x/2.x，postfix 可空）
                bool TryPatch(MethodInfo target, MethodInfo prefix, MethodInfo postfix, string label)
                {
                    if (target == null || (prefix == null && postfix == null)) return false;
                    try
                    {
                        object inst = Activator.CreateInstance(hType, "worldfall.hotbar." + label);
                        object pre = prefix != null ? Activator.CreateInstance(hmType, prefix) : null;
                        object post = postfix != null ? Activator.CreateInstance(hmType, postfix) : null;
                        if (preProp != null && preProp.CanWrite && prefix != null) preProp.SetValue(pre, prefix, null);
                        PropertyInfo postProp = hmType.GetProperty("Postfix");
                        if (postProp != null && postProp.CanWrite && postfix != null) postProp.SetValue(post, postfix, null);

                        foreach (MethodInfo m in hType.GetMethods())
                        {
                            if (m.Name != "Patch" || m.IsStatic) continue;
                            ParameterInfo[] ps = m.GetParameters();
                            if (ps.Length < 2) continue;
                            Type p0 = ps[0].ParameterType;
                            // 注意：Harmony 2.x 的 Patch 第一个参数是 MethodBase（不是 MethodInfo）
                            if (p0 != typeof(MethodBase) && p0 != typeof(MethodInfo)) continue;

                            if (ps.Length == 2 && ps[1].ParameterType == hmType)
                            {
                                m.Invoke(inst, new object[] { target, pre ?? post });
                                Debug.Log("[WorldfallHotbar] " + label + " 补丁已挂载（Harmony 1.x）");
                                return true;
                            }

                            object[] args = new object[ps.Length];
                            args[0] = target;
                            bool hasNeeded = false;
                            for (int i = 1; i < ps.Length; i++)
                            {
                                string n = ps[i].Name;
                                if (n == "prefix" && pre != null) { args[i] = pre; hasNeeded = true; }
                                else if (n == "postfix" && post != null) { args[i] = post; hasNeeded = true; }
                            }
                            if (hasNeeded)
                            {
                                m.Invoke(inst, args);
                                Debug.Log("[WorldfallHotbar] " + label + " 补丁已挂载（Harmony 2.x）");
                                return true;
                            }
                        }
                        Debug.LogWarning("[WorldfallHotbar] " + label + "：Harmony.Patch 签名不匹配");
                        return false;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[WorldfallHotbar] " + label + " 补丁失败：" + ex.Message);
                        return false;
                    }
                }

                MethodInfo miPrefix = typeof(WorldfallHotbar).GetMethod("VitalsPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miSlab = typeof(WorldfallHotbar).GetMethod("VitalsSlabPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miVitalsPost = typeof(WorldfallHotbar).GetMethod("VitalsPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miSync = typeof(WorldfallHotbar).GetMethod("MusicSyncPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo targetSync = modType.GetMethod("UpdateMusicEar", BindingFlags.NonPublic | BindingFlags.Instance);
                if (targetSync == null) Debug.LogWarning("[WorldfallHotbar] 找不到 UpdateMusicEar，音乐音量控制不可用");

                MethodInfo miCancelDraw = typeof(WorldfallHotbar).GetMethod("UpdateCancelDrawPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo targetUpdate = modType.GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
                if (targetUpdate == null) Debug.LogWarning("[WorldfallHotbar] 找不到 WorldBoxMod.Update，左键掏武器取消不可用");

                MethodInfo miMenuDiag = typeof(WorldfallHotbar).GetMethod("MenuRowPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo targetMenu = hudType != null ? hudType.GetMethod("DrawMenuButtons", BindingFlags.NonPublic | BindingFlags.Instance) : null;
                if (targetMenu == null) Debug.LogWarning("[WorldfallHotbar] 找不到 DrawMenuButtons，菜单栏诊断不可用");

                MethodInfo miAbilityRow = typeof(WorldfallHotbar).GetMethod("AbilityRowPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo targetAbility = hudType != null ? hudType.GetMethod("DrawAbility", BindingFlags.NonPublic | BindingFlags.Instance) : null;

                _fiVitalsS = hudType != null ? hudType.GetField("_vitalsRect", BindingFlags.NonPublic | BindingFlags.Instance) : null;

                TryPatch(mv, miPrefix, miVitalsPost, "血条上移");
                TryPatch(ms, miSlab, null, "血条底板屏蔽");
                TryPatch(targetSync, null, miSync, "音乐音量");
                TryPatch(targetUpdate, null, miCancelDraw, "左键掏武器取消");
                MethodInfo miHelpBtn = typeof(WorldfallHotbar).GetMethod("MenuHelpPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                TryPatch(targetMenu, miMenuDiag, null, "菜单栏修复");
                TryPatch(targetMenu, null, miHelpBtn, "帮助按钮");
                TryPatch(targetAbility, miAbilityRow, null, "能力栏修复");

                MethodInfo miSlotPre = typeof(WorldfallHotbar).GetMethod("SetItemPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miSlotPost = typeof(WorldfallHotbar).GetMethod("SetItemPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo targetSlotSet = typeof(ActorEquipmentSlot).GetMethod("setItem", BindingFlags.NonPublic | BindingFlags.Instance);
                if (targetSlotSet == null) Debug.LogWarning("[WorldfallHotbar] 找不到 ActorEquipmentSlot.setItem，武器入仓拦截不可用");
                TryPatch(targetSlotSet, miSlotPre, miSlotPost, "武器入仓拦截");

                MethodInfo miTakePre = typeof(WorldfallHotbar).GetMethod("TakeAwayPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miTakePost = typeof(WorldfallHotbar).GetMethod("TakeAwayPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo targetTakeAway = typeof(ActorEquipmentSlot).GetMethod("takeAwayItem", BindingFlags.Public | BindingFlags.Instance);
                if (targetTakeAway == null) Debug.LogWarning("[WorldfallHotbar] 找不到 takeAwayItem，锻造换装拦截不可用");
                TryPatch(targetTakeAway, miTakePre, miTakePost, "锻造换装拦截");

                // 木棍火把：LookOf 打色标 / Item 换模型 / BuildHands 加光源
                MethodInfo miTorchLookPost = typeof(WorldfallHotbar).GetMethod("TorchLookPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miTorchItemPost = typeof(WorldfallHotbar).GetMethod("TorchItemPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miTorchLightPost = typeof(WorldfallHotbar).GetMethod("TorchLightPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo tTorchLook = typeof(FirstPerson.Core.ViewMeshes).GetMethod("LookOf", BindingFlags.Public | BindingFlags.Static);
                MethodInfo tTorchItem = typeof(FirstPerson.Core.ViewMeshes).GetMethod("Item", BindingFlags.Public | BindingFlags.Static, null, CallingConventions.Any, new[] { typeof(FirstPerson.Core.WeaponLook) }, null);
                MethodInfo tTorchHands = typeof(FirstPerson.WorldBoxMod).GetMethod("BuildHands", BindingFlags.NonPublic | BindingFlags.Instance);
                _fiWbRenderer = typeof(FirstPerson.WorldBoxMod).GetField("Renderer", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                _fiWbHolstered = typeof(FirstPerson.WorldBoxMod).GetField("Holstered", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (tTorchLook == null || tTorchItem == null || tTorchHands == null)
                    Debug.LogWarning("[WorldfallHotbar] 找不到火把目标方法（LookOf/Item/BuildHands），火把功能不可用");
                if (_fiWbRenderer == null || _fiWbHolstered == null)
                    Debug.LogWarning("[WorldfallHotbar] 找不到 Renderer/Holstered 字段，火把光源不可用");
                TryPatch(tTorchLook, null, miTorchLookPost, "火把·色标");
                TryPatch(tTorchItem, null, miTorchItemPost, "火把·模型");
                TryPatch(tTorchHands, null, miTorchLightPost, "火把·光源");

                // Worldfall 本体写死中文 → 非中文语言环境下运行时英译（Toast + Label 两个咽喉点）
                MethodInfo tToast = typeof(FirstPerson.WorldBoxMod).GetMethod("Toast", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Type tGameArt = typeof(FirstPerson.WorldBoxMod).Assembly.GetType("FirstPerson.GameArt"); // internal，只能反射
                MethodInfo tLabel = tGameArt != null ? tGameArt.GetMethod("Label", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null;
                MethodInfo miToastEn = typeof(WorldfallHotbar).GetMethod("ToastEnPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo miLabelEn = typeof(WorldfallHotbar).GetMethod("LabelEnPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                if (tToast == null || tLabel == null)
                    Debug.LogWarning("[WorldfallHotbar] 找不到 Toast/Label，本体英译层不可用");
                TryPatch(tToast, miToastEn, null, "本体英译·Toast");
                TryPatch(tLabel, miLabelEn, null, "本体英译·Label");

                // GUI.Label 全局兜底：Hud.Text（血条 Pill/暂停条）、PopText（+5 木材 飘字）、
                // 本模组自己的 GUI.Label 面板文字都直接走 UnityEngine.GUI.Label，绕过了上面的 GameArt.Label。
                // GameArt.Label 内部最终也调 GUI.Label，这里再挂一层保证全覆盖（英文结果无 CJK，二次进 FpEn 直通）。
                try
                {
                    MethodInfo miGuiEn = typeof(WorldfallHotbar).GetMethod("GuiLabelEnPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo miGuiContentEn = typeof(WorldfallHotbar).GetMethod("GuiLabelContentEnPrefix", BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo[] guiTargets = new[]
                    {
                        typeof(UnityEngine.GUI).GetMethod("Label", new[] { typeof(UnityEngine.Rect), typeof(string) }),
                        typeof(UnityEngine.GUI).GetMethod("Label", new[] { typeof(UnityEngine.Rect), typeof(string), typeof(UnityEngine.GUIStyle) }),
                        typeof(UnityEngine.GUI).GetMethod("Label", new[] { typeof(UnityEngine.Rect), typeof(UnityEngine.GUIContent) }),
                        typeof(UnityEngine.GUI).GetMethod("Label", new[] { typeof(UnityEngine.Rect), typeof(UnityEngine.GUIContent), typeof(UnityEngine.GUIStyle) }),
                    };
                    int guiOk = 0;
                    foreach (MethodInfo gt in guiTargets)
                    {
                        if (gt == null) continue;
                        bool isContent = gt.GetParameters().Any(pp => pp.ParameterType == typeof(UnityEngine.GUIContent));
                        TryPatch(gt, isContent ? miGuiContentEn : miGuiEn, null, "本体英译·GUI.Label");
                        guiOk++;
                    }
                    if (guiOk == 0)
                        Debug.LogWarning("[WorldfallHotbar] 找不到 UnityEngine.GUI.Label，全局英译兜底不可用");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[WorldfallHotbar] GUI.Label 英译兜底挂载失败：" + ex);
                }

                // 锤子挖矿：游戏只有 hammer 没有 pickaxe，把矿物（Building_Mineral）的
                // 工具判定/效率加成/提示文案全部改成锤子
                Type tWork = typeof(FirstPerson.WorldBoxMod).Assembly.GetType("FirstPerson.Work"); // internal 类，走反射
                if (tWork != null)
                {
                    MethodInfo miHammerRight = typeof(WorldfallHotbar).GetMethod("HammerRightToolPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo miHammerBonus = typeof(WorldfallHotbar).GetMethod("HammerBonusPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo miHammerNeeds = typeof(WorldfallHotbar).GetMethod("HammerNeedsPostfix", BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo tRightTool = tWork.GetMethod("RightTool", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static); // RightTool 是 public，其余 private
                    MethodInfo tToolBonus = tWork.GetMethod("ToolBonus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    MethodInfo tNeedsFor = tWork.GetMethod("NeedsFor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (tRightTool == null || tToolBonus == null || tNeedsFor == null)
                        Debug.LogWarning("[WorldfallHotbar] 找不到 Work.RightTool/ToolBonus/NeedsFor（" + (tRightTool == null ? "RightTool " : "") + (tToolBonus == null ? "ToolBonus " : "") + (tNeedsFor == null ? "NeedsFor" : "") + "），锤子挖矿不可用");
                    TryPatch(tRightTool, null, miHammerRight, "锤挖矿·判定");
                    TryPatch(tToolBonus, null, miHammerBonus, "锤挖矿·效率");
                    TryPatch(tNeedsFor, null, miHammerNeeds, "锤挖矿·提示");
                }
                else Debug.LogWarning("[WorldfallHotbar] 找不到 FirstPerson.Work 类型，锤子挖矿不可用");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[WorldfallHotbar] 血条补丁失败：" + ex.Message);
            }

            TryPatchBagCap();
        }

        // ==================== 木棍火把：原版木棍顶部加火焰，手持时照亮周围 ====================
        // 零引擎数据修改，三个 postfix 组合：
        //   1) TorchLookPostfix  → ViewMeshes.LookOf：木棍 Accent 设为火橙标记色
        //      （ViewMeshes.Items 缓存 key 含 Accent，原版棍保持默认金色，互不污染）
        //   2) TorchItemPostfix  → ViewMeshes.Item（Hands.Build 每帧调用）：按动画帧
        //      换模型。模型来自用户 Blender 工程（火焰blend.blend）的逐帧烘焙数据：
        //      12 帧循环，含空物体驱动的置换修改器形变（真·火焰抖动）。
        //      材质映射：浅木=柄色 / 深木 / 余烬条与火焰球=kind 11 全亮发光 / 炭环
        //   3) TorchLightPostfix → WorldBoxMod.BuildHands：跟随视点前方的暖色闪烁光源
        //      （Radius 11.5，炉火同款闪烁公式 0.86+0.14·sin9t·sin(5.7t+1)，Glow 0.15；
        //      写法参照游戏自带 AddLantern 的灯）
        // 收起武器（Holstered）时不发光；ViewMeshes.Item 只在手持时被调用，模型同理不换。
        internal static readonly uint TorchAccent = FirstPerson.Core.TerrainCache.Rgb(255, 140, 30); // 火橙标记色
        private const int TorchFrames = 12;    // 火焰动画帧数（预烘焙，零每帧分配）
        private const bool TorchBakeMirror = false; // 烘焙模型若在游戏内呈镜像，改为 true
        private static readonly FirstPerson.Core.Vec3[] Vec3Tmp = new FirstPerson.Core.Vec3[8]; // Face 复用缓冲
        private static readonly Dictionary<uint, FirstPerson.Core.MeshModel[]> TorchVariants = new Dictionary<uint, FirstPerson.Core.MeshModel[]>(); // 按柄色缓存 12 帧
        private static FieldInfo _fiWbRenderer;  // WorldBoxMod.Renderer（internal）
        private static FieldInfo _fiWbHolstered; // WorldBoxMod.Holstered（internal）
        private static float _torchWind;         // 风相位：随宿主移动加速滚动
        private static bool _torchHasPos;
        private static float _torchLastX, _torchLastY;
        private static bool _torchLightLogged;

        private static void TorchLookPostfix(string id, ref FirstPerson.Core.WeaponLook __result)
        {
            if (id == null || __result.Kind != FirstPerson.Core.WeaponKind.Stick) return;
            if (!id.Contains("stick_wood")) return;   // 只改原版木棍
            __result.Accent = TorchAccent;            // 打上火橙标记，Item 阶段识别
        }

        private static int TorchFrame()
        {
            int f = (int)(Time.unscaledTime * 13f + _torchWind) % TorchFrames; // 13 帧/秒的基础燃动
            if (f < 0) f += TorchFrames;
            return f;
        }

        // 预烘焙火把帧（按柄色缓存）：模型来自用户 Blender 工程的逐帧烘焙数据
        // （WorldfallTorchBake.Data，blend_export.py 生成；含置换修改器形变动画）。
        // 数据格式：
        //   short nFrames; byte nMeshes;
        //   每 mesh { short nVerts; int nPolys; nVerts*3*short 基准坐标(×1000, 游戏单位);
        //             nPolys*(byte 角点数; 角点数*short 顶点号; byte 组) }
        //   随后 (nFrames-1) × 每 mesh nVerts*3*short 形变增量(×1000)
        // 组 → 材质槽：0=浅木(柄色) 1=深木 2=余烬(发光) 3=火焰(发光) 4=炭环
        private static FirstPerson.Core.MeshModel[] TorchVariantsFor(uint handle)
        {
            FirstPerson.Core.MeshModel[] arr;
            if (TorchVariants.TryGetValue(handle, out arr) && arr != null && arr.Length == TorchFrames && arr[0] != null) return arr;

            byte[] raw = Convert.FromBase64String(WorldfallHotbarParts.TorchBake.Data);
            int off = 0;
            int frames = BitConverter.ToInt16(raw, off); off += 2;
            int meshCount = raw[off++];
            int[][] baseVerts = new int[meshCount][];
            List<int[]>[] polyCorners = new List<int[]>[meshCount];
            List<byte>[] polyGroup = new List<byte>[meshCount];
            int[] vertCounts = new int[meshCount];
            for (int m = 0; m < meshCount; m++)
            {
                int nv = BitConverter.ToInt16(raw, off); off += 2;
                int np = BitConverter.ToInt32(raw, off); off += 4;
                vertCounts[m] = nv;
                baseVerts[m] = new int[nv * 3];
                for (int k = 0; k < nv * 3; k++) { baseVerts[m][k] = BitConverter.ToInt16(raw, off); off += 2; }
                polyCorners[m] = new List<int[]>(np);
                polyGroup[m] = new List<byte>(np);
                for (int p = 0; p < np; p++)
                {
                    int cn = raw[off++];
                    int[] cs = new int[cn];
                    for (int c = 0; c < cn; c++) { cs[c] = BitConverter.ToInt16(raw, off); off += 2; }
                    polyCorners[m].Add(cs);
                    polyGroup[m].Add(raw[off++]);
                }
            }
            int[][][] deltas = new int[frames - 1][][];
            for (int f = 1; f < frames; f++)
            {
                deltas[f - 1] = new int[meshCount][];
                for (int m = 0; m < meshCount; m++)
                {
                    int[] d = new int[vertCounts[m] * 3];
                    for (int k = 0; k < d.Length; k++) { d[k] = BitConverter.ToInt16(raw, off); off += 2; }
                    deltas[f - 1][m] = d;
                }
            }

            arr = new FirstPerson.Core.MeshModel[frames];
            for (int f = 0; f < frames; f++)
            {
                FirstPerson.Core.MeshBuilder b = new FirstPerson.Core.MeshBuilder("torch_baked");
                b.Mirror = TorchBakeMirror; // 游戏内若整体镜像则改此常量
                int slotLightWood = b.Slot("wood_light", 3, handle, 0);                                      // 浅木 → 游戏柄色
                int slotDarkWood = b.Slot("wood_dark", 3, FirstPerson.Core.TerrainCache.Rgb(70, 45, 28), 0); // 深木
                int slotEmber = b.Slot("ember", 11, FirstPerson.Core.TerrainCache.Rgb(255, 110, 30), 1);     // 余烬条（发光）
                int slotFlame = b.Slot("flame", 11, FirstPerson.Core.TerrainCache.Rgb(255, 175, 60), 1);     // 火焰球（发光）
                int slotRing = b.Slot("ring", 7, FirstPerson.Core.TerrainCache.Scale(handle, 0.55f), 0);     // 炭环
                b.Part("item", -1, new FirstPerson.Core.Vec3(0f, 0f, 0f));
                for (int m = 0; m < meshCount; m++)
                {
                    int[] bv = baseVerts[m];
                    int[] dl = f == 0 ? null : deltas[f - 1][m];
                    int[] cornerPos = new int[bv.Length];
                    for (int k = 0; k < bv.Length; k++) cornerPos[k] = bv[k] + (dl != null ? dl[k] : 0);
                    List<int[]> pcs = polyCorners[m];
                    List<byte> pgs = polyGroup[m];
                    for (int p = 0; p < pcs.Count; p++)
                    {
                        int[] cs = pcs[p];
                        Vec3Tmp[0] = new FirstPerson.Core.Vec3(cornerPos[cs[0] * 3] / 1000f, cornerPos[cs[0] * 3 + 1] / 1000f, cornerPos[cs[0] * 3 + 2] / 1000f);
                        Vec3Tmp[1] = new FirstPerson.Core.Vec3(cornerPos[cs[1] * 3] / 1000f, cornerPos[cs[1] * 3 + 1] / 1000f, cornerPos[cs[1] * 3 + 2] / 1000f);
                        switch (cs.Length)
                        {
                            case 3:
                                Vec3Tmp[2] = new FirstPerson.Core.Vec3(cornerPos[cs[2] * 3] / 1000f, cornerPos[cs[2] * 3 + 1] / 1000f, cornerPos[cs[2] * 3 + 2] / 1000f);
                                b.Face(SlotOfGroup(pgs[p], slotLightWood, slotDarkWood, slotEmber, slotFlame, slotRing), Vec3Tmp[0], Vec3Tmp[1], Vec3Tmp[2]);
                                break;
                            case 4:
                                Vec3Tmp[2] = new FirstPerson.Core.Vec3(cornerPos[cs[2] * 3] / 1000f, cornerPos[cs[2] * 3 + 1] / 1000f, cornerPos[cs[2] * 3 + 2] / 1000f);
                                Vec3Tmp[3] = new FirstPerson.Core.Vec3(cornerPos[cs[3] * 3] / 1000f, cornerPos[cs[3] * 3 + 1] / 1000f, cornerPos[cs[3] * 3 + 2] / 1000f);
                                b.Face(SlotOfGroup(pgs[p], slotLightWood, slotDarkWood, slotEmber, slotFlame, slotRing), Vec3Tmp[0], Vec3Tmp[1], Vec3Tmp[2], Vec3Tmp[3]);
                                break;
                            default:
                                FirstPerson.Core.Vec3[] tmp = new FirstPerson.Core.Vec3[cs.Length];
                                for (int c = 0; c < cs.Length; c++)
                                    tmp[c] = new FirstPerson.Core.Vec3(cornerPos[cs[c] * 3] / 1000f, cornerPos[cs[c] * 3 + 1] / 1000f, cornerPos[cs[c] * 3 + 2] / 1000f);
                                b.Face(SlotOfGroup(pgs[p], slotLightWood, slotDarkWood, slotEmber, slotFlame, slotRing), tmp);
                                break;
                        }
                    }
                }
                arr[f] = b.Build();
            }
            if (TorchVariants.Count > 4) TorchVariants.Clear();
            TorchVariants[handle] = arr;
            return arr;
        }

        private static int SlotOfGroup(byte g, int lightWood, int darkWood, int ember, int flame, int ring)
        {
            switch (g)
            {
                case 0: return lightWood;
                case 1: return darkWood;
                case 2: return ember;
                case 3: return flame;
                default: return ring;
            }
        }

        private static void TorchItemPostfix(FirstPerson.Core.WeaponLook look, ref FirstPerson.Core.MeshModel __result)
        {
            try
            {
                if (look.Kind != FirstPerson.Core.WeaponKind.Stick || look.Accent != TorchAccent) return;
                __result = TorchVariantsFor(look.Handle)[TorchFrame()];
            }
            catch (Exception ex)
            {
                if (!_torchErrLogged) // 只报一次，避免刷屏
                {
                    _torchErrLogged = true;
                    WLog("[火把] 模型构建失败，退回原版棍：" + ex.GetType().Name + " " + ex.Message);
                    Debug.LogWarning("[WorldfallHotbar] [火把] 模型构建失败：" + ex);
                }
            }
        }

        private static bool _torchErrLogged;

        private static void TorchLightPostfix(FirstPerson.WorldBoxMod __instance, Actor host)
        {
            try
            {
                if (_fiWbRenderer == null || _fiWbHolstered == null) return;
                if ((bool)_fiWbHolstered.GetValue(__instance)) return; // 收起武器不发光
                if (host == null) return;
                string wid = CurrentWeaponId(host);
                if (wid == null || !wid.Contains("stick_wood")) return;
                // 风吹效果：宿主移动越快，火焰动画滚动越快（Item 每帧取帧时体现）
                try
                {
                    Vector2 pos = host.current_position;
                    if (_torchHasPos)
                    {
                        float dx = pos.x - _torchLastX, dy = pos.y - _torchLastY;
                        _torchWind += Mathf.Sqrt(dx * dx + dy * dy) * 2.5f;
                    }
                    _torchLastX = pos.x; _torchLastY = pos.y; _torchHasPos = true;
                }
                catch { }
                FirstPerson.Core.FrameRenderer r = _fiWbRenderer.GetValue(__instance) as FirstPerson.Core.FrameRenderer;
                if (r == null) return;
                if (r.Lights.Count >= 64) return; // 引擎点光源上限
                FirstPerson.Core.ViewState v = __instance.View;
                float yaw = __instance.ViewYaw;
                float t = Time.unscaledTime;
                float fl = 0.86f + 0.14f * Mathf.Sin(t * 9f) * Mathf.Sin(t * 5.7f + 1f); // 炉火同款闪烁
                r.Lights.Add(new FirstPerson.Core.PointLight
                {
                    X = v.X + Mathf.Cos(yaw) * 0.35f, // 视点前方一点，正是手里火把的位置
                    Y = v.Y + Mathf.Sin(yaw) * 0.35f,
                    Z = v.Z,
                    Radius = 11.5f,
                    R = 1.00f * fl,
                    G = 0.62f * fl,
                    B = 0.28f * fl,
                    Glow = 0.15f
                });
                if (!_torchLightLogged)
                {
                    _torchLightLogged = true;
                    WLog("火把光源已点亮（Radius 11.5，暖橙闪烁；白天环境亮时主要在夜间/暗处可见）");
                }
            }
            catch { }
        }

        // ==================== 锤子挖矿：游戏没有镐，锤子 = 唯一采矿工具 ====================
        // 三个 postfix 打在 FirstPerson.Work 的私有静态方法上（internal 类，注册时走反射）：
        //   1) HammerRightToolPostfix → RightTool：矿物（Building_Mineral）允许锤子、禁止斧头
        //   2) HammerBonusPostfix     → ToolBonus：锤子挖矿享受与镐同款 2 倍效率
        //   3) HammerNeedsPostfix     → NeedsFor：提示文案「用镐或斧头采矿」→「用锤子采矿」
        // 砍树（Building_Tree）逻辑不动，依然斧子/徒手。
        private static FieldInfo _fiBuildingAsset; // Building.asset（internal 字段，反射缓存）

        // 目标是矿物建筑（Building_Mineral = 7）
        private static bool IsMineralBuilding(Building b)
        {
            try
            {
                if (_fiBuildingAsset == null)
                    _fiBuildingAsset = typeof(Building).GetField("asset", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                if (_fiBuildingAsset == null) return false;
                BuildingAsset ba = _fiBuildingAsset.GetValue(b) as BuildingAsset;
                return ba != null && (int)ba.building_type == 7;
            }
            catch { return false; }
        }

        // 持锤且目标是矿物
        private static bool HammerOnMineral(Building b, string tool)
        {
            return tool != null && tool.Contains("hammer") && IsMineralBuilding(b);
        }

        private static void HammerRightToolPostfix(Building b, string tool, ref bool __result)
        {
            if (b == null) return;
            // 锤子 → 允许挖矿
            if (!__result && HammerOnMineral(b, tool))
            {
                __result = true;
                return;
            }
            // 斧头 → 禁止挖矿（砍树不受影响，只拦矿物）
            if (__result && tool != null && tool.Contains("axe") && IsMineralBuilding(b))
                __result = false;
        }

        private static void HammerBonusPostfix(Building b, string tool, ref float __result)
        {
            if (HammerOnMineral(b, tool)) __result = 2f; // 与镐同款 2 倍效率
        }

        private static void HammerNeedsPostfix(ref string __result)
        {
            if (__result == null) return;
            // Worldfall 本体的 NeedsFor 是写死中文的，这里顺便按游戏语言把砍树/挖矿提示都翻对
            if (!LangChinese)
            {
                if (__result.Contains("镐")) // 挖矿提示：按模组规则改为锤子
                    __result = __result.Contains("|H")
                        ? "Mine with a hammer|H:Take it out"
                        : "Mine with a hammer";
                else if (__result.Contains("斧子")) // 砍树提示
                    __result = __result.Contains("|H")
                        ? "Chop with an axe or bare hands|H:Holster it"
                        : "Chop with an axe or bare hands";
                return;
            }
            if (!__result.Contains("镐")) return;
            __result = __result.Replace("用镐或斧头来采矿", "用锤子来采矿"); // 兼容带「来」的变体
            __result = __result.Replace("用镐或斧头采矿", "用锤子采矿");
        }

        // ==================== 背包容量改造：24 个 → MC 式「格子数 × 每格堆叠 64」 ====================
        // 原版第一人称采集把背包上限写死为「总共 24 个资源」（FirstPerson.Work.Capacity = 24 的
        // const，编译期已内联进 IL，无法反射修改）。改成 MC 式：按背包格子数封顶，每格堆 64。
        // 涉及 24 的位置（全部用 transpiler 把 ldc.i4.s 24 换成实时容量 BagCap()）：
        //   - Work.Update          BagCount >= 24 拦截采集 + 满包 toast 文本
        //   - Work.Finish          剩余容量 24 - BagCount，限制本次采集产出
        //   - Dialogue.Buy         24 - BagTotal < 5 判定商店购买「包满了」
        //   - Hud.DrawCraftPanel   「袋子 X/24」标题显示
        //   - Hud.DrawMenuButtons  I 键按钮「X/24」显示
        //   - WorldBoxMod.RenderFrame 提示词「（袋子已满）」
        internal const int BagSlots = 27;  // 背包格子数：9 列 × 3 行（与 B 键背包面板、MC 背包 27 格一致）
        internal const int BagStack = 64;  // 每格堆叠数：与快捷栏 MC 式堆叠一致

        public static int BagCap()
        {
            return BagSlots * BagStack; // 27 × 64 = 1728
        }

        private static void TryPatchBagCap()
        {
            try
            {
                Assembly fp = typeof(FirstPerson.WorldBoxMod).Assembly;
                Type workT = fp.GetType("FirstPerson.Work");
                Type hudT = fp.GetType("FirstPerson.Hud");
                Type dlgT = fp.GetType("FirstPerson.Dialogue");
                MethodInfo tp = typeof(WorldfallHotbar).GetMethod("BagCapTranspiler", BindingFlags.NonPublic | BindingFlags.Static);
                if (tp == null) { Debug.LogWarning("[WorldfallHotbar] 找不到 BagCapTranspiler，背包容量改造不可用"); return; }

                Harmony h = new Harmony("worldfall.hotbar.bagcap");
                int ok = 0, miss = 0;
                ok += CapPatch(h, workT, "Update", tp, ref miss);
                ok += CapPatch(h, workT, "Finish", tp, ref miss);
                ok += CapPatch(h, dlgT, "Buy", tp, ref miss);
                ok += CapPatch(h, hudT, "DrawCraftPanel", tp, ref miss);
                ok += CapPatch(h, hudT, "DrawMenuButtons", tp, ref miss);
                ok += CapPatch(h, fp.GetType("FirstPerson.WorldBoxMod"), "RenderFrame", tp, ref miss);
                Debug.Log("[WorldfallHotbar] 背包容量补丁：容量=" + BagCap() + "（" + BagSlots + " 格 × " + BagStack + "），成功 " + ok + " 处，未找到 " + miss + " 处");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[WorldfallHotbar] 背包容量补丁失败：" + ex.Message);
            }
        }

        private static int CapPatch(Harmony h, Type t, string name, MethodInfo transpiler, ref int miss)
        {
            if (t == null) { miss++; Debug.LogWarning("[WorldfallHotbar] 背包容量补丁：找不到类型（" + name + "）"); return 0; }
            MethodInfo mi = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (mi == null) { miss++; Debug.LogWarning("[WorldfallHotbar] 背包容量补丁：找不到 " + t.Name + "." + name); return 0; }
            h.Patch(mi, transpiler: new HarmonyMethod(transpiler));
            Debug.Log("[WorldfallHotbar] 背包容量补丁已挂载：" + t.Name + "." + name);
            return 1;
        }

        private static IEnumerable<CodeInstruction> BagCapTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int replaced = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (ci.opcode != OpCodes.Ldc_I4_S) continue;
                int v = ci.operand is sbyte ? (sbyte)ci.operand : (int)ci.operand;
                if (v != 24 || !LooksLikeBagCap24(list, i)) continue;
                ci.opcode = OpCodes.Call;   // 原地改，保留 labels/branches
                ci.operand = typeof(WorldfallHotbar).GetMethod("BagCap", BindingFlags.Public | BindingFlags.Static);
                replaced++;
            }
            if (replaced > 0) Debug.Log("[WorldfallHotbar] 背包容量 transpiler：本方法替换 " + replaced + " 处 24 → " + BagCap());
            return list;
        }

        // 判定一处 ldc.i4.s 24 是否属于背包容量语义（满包判定 / 剩余容量 / 显示 24），
        // 避免误伤颜色字节、文本长度、位移等无关的 24。
        // 已对照 FirstPerson.dll 实际 IL 核验全部 7 处目标：
        //   Work.Update:   [ldfld BagCount, 24, blt.s] + [24, stloc, ldloca, call ToString]（toast 文本）
        //   Work.Finish:   [24, ldarg.0, ldfld BagCount, sub]
        //   Dialogue.Buy:  [24, ldloc, call BagTotal, sub, ldc.i4.5, bge.s]
        //   Hud.DrawCraftPanel / DrawMenuButtons: [24, stloc, ldloca, call ToString]（「/24」显示）
        //   WorldBoxMod.RenderFrame: [ldfld BagCount, 24, bge.s]
        private static bool LooksLikeBagCap24(List<CodeInstruction> list, int i)
        {
            int n = list.Count;
            if (i + 1 >= n) return false;
            OpCode op = list[i + 1].opcode;
            // 1) 紧跟 box：字符串拼接装箱（满包 toast 等文本）
            if (op == OpCodes.Box) return true;
            // 2) 紧跟比较跳转：BagCount >= 24（满包拦截、「袋子已满」提示）
            if (op == OpCodes.Blt_S || op == OpCodes.Bge_S || op == OpCodes.Ble_S || op == OpCodes.Bgt_S
                || op == OpCodes.Blt || op == OpCodes.Bge || op == OpCodes.Ble || op == OpCodes.Bgt) return true;
            // 3) ToString 临时变量：[24, stloc V, ldloca V, call/callvirt ToString]（24.ToString() 的编译结果）
            if (i + 3 < n
                && (op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1
                    || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3))
            {
                OpCode lo = list[i + 2].opcode;
                OpCode to = list[i + 3].opcode;
                if ((lo == OpCodes.Ldloca || lo == OpCodes.Ldloca_S) && (to == OpCodes.Call || to == OpCodes.Callvirt))
                {
                    object va = list[i + 1].operand, vb = list[i + 2].operand;
                    bool sameLocal = ReferenceEquals(va, vb)
                        || (va is LocalBuilder la && vb is LocalBuilder lb && la.LocalIndex == lb.LocalIndex);
                    MethodInfo mi = list[i + 3].operand as MethodInfo;
                    if (sameLocal && mi != null && mi.Name == "ToString") return true;
                }
            }
            // 4) 减法「24 - X」（X 为 BagCount 字段或 BagTotal 调用）：24 后跟取数指令、以 sub 收尾
            if (op == OpCodes.Sub || op == OpCodes.Sub_Ovf) return false; // 「X - 24」形态，不碰
            for (int j = i + 1; j < n && j <= i + 4; j++)
            {
                OpCode o = list[j].opcode;
                if (j > i + 1 && (o == OpCodes.Sub || o == OpCodes.Sub_Ovf)) return true;
                if (o != OpCodes.Ldarg_0 && o != OpCodes.Ldarg_1 && o != OpCodes.Ldarg_2 && o != OpCodes.Ldarg_3
                    && o != OpCodes.Ldarg && o != OpCodes.Ldarg_S
                    && o != OpCodes.Ldloc_0 && o != OpCodes.Ldloc_1 && o != OpCodes.Ldloc_2 && o != OpCodes.Ldloc_3
                    && o != OpCodes.Ldloc && o != OpCodes.Ldloc_S
                    && o != OpCodes.Ldfld && o != OpCodes.Call && o != OpCodes.Callvirt) return false;
            }
            return false;
        }

        private static Rect _vitalsBefore; // DrawVitals 调用前的 _vitalsRect（用于精确还原）

        private static void VitalsPrefix(object __instance, ref float sh)
        {
            if (VitalsLift <= 0.5f) return;
            // 记录调用前的 _vitalsRect（上一帧还原后的正确值），画完后原样放回。
            // 不用"y += 抬升量"的相对还原：DrawVitals 内部有动画插值，写入值和 sh 不成固定关系
            try
            {
                if (_fiVitalsS != null)
                {
                    object v = _fiVitalsS.GetValue(__instance);
                    if (v is Rect) _vitalsBefore = (Rect)v;
                }
            }
            catch { }
            sh -= VitalsLift;
        }

        private static FieldInfo _fiVitalsS; // Hud._vitalsRect（静态桥，供补丁用）

        private static void VitalsPostfix(object __instance)
        {
            // 血条画完后把 _vitalsRect 精确还原为调用前的值：能力按钮（集结）、金币/建造栏等
            // 都锚定在 _vitalsRect 上，不还原的话会跟着血条一起抬高
            if (VitalsLift <= 0.5f || _fiVitalsS == null) return;
            try
            {
                if (_vitalsBefore.width > 1f || _vitalsBefore.height > 1f)
                {
                    _fiVitalsS.SetValue(__instance, _vitalsBefore); // 精确还原
                }
                else
                {
                    object v = _fiVitalsS.GetValue(__instance); // 首帧兜底：相对还原
                    if (v is Rect)
                    {
                        Rect r = (Rect)v;
                        r.y += VitalsLift;
                        _fiVitalsS.SetValue(__instance, r);
                    }
                }
            }
            catch { }
        }

        private static int _menuDiagLogged;

        // ---- 菜单栏/能力栏基线修复 ----
        // 实测（v3.16 探针）：DrawMenuButtons 收到的 sh = Screen.height - VitalsLift（被血条抬升连带改写），
        // 导致金币/M/K/I 整排按钮浮在半空。这里只在"sh 恰好被扣掉抬升量"这一异常出现时复原为屏幕高，
        // 其他情况一律不动（避免误伤别的模组）。
        private static void FixLiftedSh(ref float sh)
        {
            if (VitalsLift > 0.5f && Mathf.Abs(sh - (Screen.height - VitalsLift)) < 1f)
            {
                sh = Screen.height;
            }
        }

        private static void MenuRowPrefix(ref float sh)
        {
            bool fixedIt = false;
            if (VitalsLift > 0.5f && Mathf.Abs(sh - (Screen.height - VitalsLift)) < 1f)
            {
                sh = Screen.height;
                fixedIt = true;
            }
            if (_menuDiagLogged == 0)
            {
                _menuDiagLogged++;
                Debug.Log("[WorldfallHotbar][诊断] MenuButtons sh入=" + sh.ToString("F0") + " 屏高=" + Screen.height
                    + " 抬升=" + VitalsLift.ToString("F0") + " 已修正=" + fixedIt + "\n" + Environment.StackTrace);
            }
        }

        private static void AbilityRowPrefix(ref float sh)
        {
            FixLiftedSh(ref sh);
        }

        private static bool VitalsSlabPrefix()
        {
            // 返回 false 跳过原方法：附身状态下不画血条背后那块灰褐色底板
            return VitalsLift <= 0.5f;
        }

        // ---- 取消"左键自动掏武器"（Harmony postfix 挂在 WorldBoxMod.Update 末尾） ----
        // 原版逻辑：Holstered 时按左键 → Holstered = false 并记录 _unholsterFrame = Time.frameCount。
        // _unholsterFrame 只有这一处会写（H 键/附身/重生都不写），所以用它精准识别"这帧刚发生左键掏武器"，
        // 识别到就把 Holstered 翻回去 —— 彻底废除左键掏武器，掏/收武器只由快捷栏滚轮选择与 H 键驱动。
        // 开销：仅在已掏出武器（Holstered == false）时才读一次帧标记，平时一帧只多一次布尔读，可忽略。
        private static void UpdateCancelDrawPostfix(object __instance)
        {
            try
            {
                if (_fiHolstered == null || _fiUnholsterFrame == null) return;
                if ((bool)_fiHolstered.GetValue(__instance)) return; // 本来就是收起状态，无事发生
                int f = (int)_fiUnholsterFrame.GetValue(__instance);
                if (f == Time.frameCount)
                {
                    _fiHolstered.SetValue(__instance, true); // 撤销本次左键掏武器
                }
            }
            catch { }
        }

        // ---- 底部按钮排"帮助按钮"：与 M/K/F 同排同风格，悬停显示快捷键说明，点击开关背包 ----
        private static string[] HelpTextLines()
        {
            return new[]
            {
                T("B 打开/关闭 背包",        "B - Toggle bag"),
                T("滚轮 / 1-9 切换选中",     "Wheel / 1-9 - Select slot"),
                T("Delete 丢弃选中物品",     "Delete - Drop selected"),
                T("H 收起/拿起武器",         "H - Holster / draw weapon"),
                T("J 吃一口食物",            "J - Eat one bite"),
                T("点击格子 选择/装备",      "Click a slot - select / equip")
            };
        }

        private static void MenuHelpPostfix(object __instance, float alpha)
        {
            try
            {
                if (alpha <= 0.01f || _miStoneButton == null || _miLabel == null)
                {
                    _helpRect = default(Rect); // HUD 隐藏时清空判定区，防残留误触
                    return;
                }
                if (_fiMenuLeft == null || _fiBagTop == null) return;
                float p = ArtP();
                float size = Mathf.Round(22f * p);
                float gap = Mathf.Round(3f * p);
                float y = (float)_fiBagTop.GetValue(__instance);
                float menuLeft = (float)_fiMenuLeft.GetValue(__instance);
                float right = menuLeft - gap; // 紧贴本排最左元素的左侧
                Rect r = new Rect(right - size, y, size, size);
                _helpRect = r;
                _helpAlpha = alpha;
                // 与 KeyButton 相同画法：石质按钮 + 左上角橙色键名 + 居中大问号
                _miStoneButton.Invoke(null, new object[] { r, null, alpha, true, null });
                Color keyCol = _fiKeyOrange != null ? (Color)_fiKeyOrange.GetValue(null) : new Color(0.95f, 0.59f, 0.12f);
                _miLabel.Invoke(null, new object[]
                {
                    new Rect(r.x + 3f * p, r.y + 2f * p, size, 7f * p), "?", 5f, (TextAnchor)0,
                    new Color(keyCol.r, keyCol.g, keyCol.b, alpha), false
                });
                _miLabel.Invoke(null, new object[]
                {
                    new Rect(r.x, r.y + size * 0.22f, size, size * 0.62f), "?", 8f, (TextAnchor)4,
                    new Color(1f, 1f, 1f, alpha), false
                });

                // 键位说明面板不在 postfix 里画：此刻能力键/快捷栏尚未绘制，会盖住面板。
                // 只在这里记录状态，由我们自己 OnGUI 的最后一步统一绘制（保证在最顶层）。
            }
            catch { }
        }

        // 帮助按钮悬停判定：OS 光标可用时用光标位置；十字准星锁定时准星恒在屏幕中心，用中心点判定
        private static bool HelpHover(Rect r)
        {
            if (r.width <= 1f) return false;
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            if (r.Contains(m)) return true;
            if (Cursor.lockState != CursorLockMode.None)
            {
                return r.Contains(new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
            }
            return false;
        }

        private static void DrawHelpTooltip(Rect btn, float p, float alpha)
        {
            try
            {
                if (_miSliced == null || _fiTooltipSprite == null) return;
                Sprite tip = _fiTooltipSprite.GetValue(null) as Sprite;
                if (tip == null) return;
                float lh = Mathf.Round(10f * p);
                float w = Mathf.Round(96f * p);
                string[] lines = HelpTextLines();
                float h = lines.Length * lh + 6f * p;
                Rect t = new Rect(btn.xMax - w, btn.yMin - h - 4f * p, w, h);
                Color frame = new Color(1f, 0.82f, 0.3f, alpha); // 与能力键高亮同款金框
                _miSliced.Invoke(null, new object[] { t, tip, frame, 7f, 7f, 7f, 7f, 0f });
                for (int i = 0; i < lines.Length; i++)
                {
                    _miLabel.Invoke(null, new object[]
                    {
                        new Rect(t.x + 4f * p, t.y + 3f * p + i * lh, w - 8f * p, lh), lines[i],
                        5.5f, (TextAnchor)3, new Color(1f, 1f, 1f, alpha), false
                    });
                }
            }
            catch { }
        }

        // ---- setItem / takeAwayItem 拦截：覆盖所有换装路径 ----
        // setItem：正常换武器（createNewWeapon、我们的装回）
        // takeAwayItem：锻造（FirstPerson.Crafting）会先 takeAwayItem 清槽再 setItem 装新，
        //   旧武器直接被扔地上（没城市时）——必须在这里截获，setItem 只能看到空槽。
        private static Item _displacedWeapon; // 即将被顶下的武器（prefix 记录，postfix 消费）
        private static Actor _displacedOwner;
        private static Actor _hostStatic;     // 宿主的静态镜像（补丁方法必须是静态的）
        private static bool _suppressCapture; // 我们自己主动卸下（丢弃）时不截获

        private static void CaptureDisplaced(ActorEquipmentSlot slot)
        {
            _displacedWeapon = null;
            _displacedOwner = null;
            try
            {
                if (slot.type != EquipmentType.Weapon) return; // 只关心武器槽
                Item old = slot.getItem();
                if (old == null || old.shouldbe_removed) return; // 空槽/已被游戏标记回收的不管
                if (old.isCursed()) return;                      // 诅咒武器原版规则也不允许换下
                if (_hostStatic == null || !IsAlive(_hostStatic)) return;
                if (slot != _hostStatic.equipment.weapon) return; // 只管宿主的武器槽
                _displacedWeapon = old;
                _displacedOwner = _hostStatic;
            }
            catch { }
        }

        private static void ConsumeDisplaced()
        {
            try
            {
                if (_displacedWeapon == null) return;
                if (_displacedOwner != null && _displacedOwner.city != null)
                {
                    // 有城市：游戏本体自己会把旧武器放进城市仓库（tryToPutItem），不抢
                    _displacedWeapon = null;
                    _displacedOwner = null;
                    return;
                }
                AddToStash(_displacedOwner, _displacedWeapon);
                _displacedWeapon = null;
                _displacedOwner = null;
            }
            catch { }
        }

        private static void SetItemPrefix(ActorEquipmentSlot __instance, Item pItem, Actor pActor)
        {
            CaptureDisplaced(__instance);
        }

        private static void SetItemPostfix()
        {
            ConsumeDisplaced();
        }

        private static void TakeAwayPrefix(ActorEquipmentSlot __instance)
        {
            if (_suppressCapture) return;
            CaptureDisplaced(__instance);
        }

        private static void TakeAwayPostfix()
        {
            if (_suppressCapture) return;
            ConsumeDisplaced();
        }

        private static void MusicSyncPostfix(object __instance)
        {
            // WorldBoxMod.UpdateMusicEar 每帧执行 _audio.MusicVolume = Settings.MusicVolume，
            // 会把我们在 Tick 里写的音量冲掉。挂 postfix 在同步之后立刻覆盖，保证顺序正确。
            try
            {
                if (_fiAudio == null || _fiMusicVol == null) return;
                object audio = _fiAudio.GetValue(__instance);
                if (audio == null) return;
                _fiMusicVol.SetValue(audio, WorldfallMusic.Volume);
                if (WorldfallMusic.Volume <= 0.001f)
                {
                    if (_fiGameWant != null) _fiGameWant.SetValue(audio, 1f);
                    if (_fiGameWait != null) _fiGameWait.SetValue(audio, 0f);
                }
            }
            catch { }
        }

        private void SelectSlot(int index, bool allowEquip)
        {
            if (index < 0) return;
            _sel = index;
            if (index >= _slots.Count)
            {
                // 空格子（快捷栏未填充的占位格）= 空手：收起武器（等同按 H），左键不会攻击
                if (SetHolstered(true)) ShowToast(T("空手（选择武器槽或按 H 拿起）", "Empty-handed (select a weapon slot or press H)"));
                return;
            }
            HotSlot s = index < _slots.Count ? _slots[index] : null;
            // 填充空格（Id 为空，含空手的"手持格"）= 空手：收起武器（等同按 H），左键不会攻击。
            // 注意：RebuildSlots 会把槽位补齐到 9 格，填充格是 HotSlot 对象而非 null，
            // 所以必须按 Id 判空，不能按对象判空。
            if (s == null || string.IsNullOrEmpty(s.Id))
            {
                if (SetHolstered(true)) ShowToast(T("空手（选择武器槽或按 H 拿起）", "Empty-handed (select a weapon slot or press H)"));
                return;
            }
            if (s.IsWeapon)
            {
                SetHolstered(false); // 拿出武器
                if (allowEquip && !s.IsCurrent)
                {
                    if (s.RefItem != null && StashItemValid(_host, s.RefItem))
                    {
                        // 仓库武器槽：原物装回（保留耐久/品质），当前手持武器换下入仓
                        Item cur = _host != null ? _host.equipment.weapon.getItem() : null;
                        EquipItem(s.RefItem);
                        List<Item> l = StashOf(_host);
                        if (l != null)
                        {
                            l.Remove(s.RefItem);
                            s.RefItem.setFavorite(false); // 装回后解除星标
                            WLog("[宿主#" + HostTag() + "] 仓库槽点选：原物装回 " + s.Id + "，被顶下 " + ItemDesc(cur));
                        }
                        AddToStash(_host, cur);
                        ShowToast(T("已装备 ", "Equipped ") + s.Name);
                    }
                    else
                    {
                        if (s.RefItem != null) WLog("[宿主#" + HostTag() + "] 仓库槽失效，退回重新生成: " + ItemDesc(s.RefItem));
                        EquipWeapon(s.Id); // 内部自带"已装备 / 不会用"提示
                    }
                }
                else if (s.IsCurrent)
                {
                    ShowToast(s.Name + T("（手持中）", " (in hand)"));
                }
                else
                {
                    ShowToast(s.Name);
                }
            }
            else
            {
                // 非武器格（资源/食物）一律收起武器，与空格效果一致
                SetHolstered(true);
                // 只显示物品信息，不自动进食；食物提示按 J，玩家自己决定吃不吃
                ShowToast(s.Name + " x" + s.Count + (s.Nutrition > 0 ? T("（按 J 进食）", " (press J to eat)") : ""));
            }
        }

        // ---- FirstPerson.Eating.Eat(Actor, Action<string>) 反射桥 ----
        private static MethodInfo _miEat;

        private bool TryEat(Actor host)
        {
            try
            {
                if (_miEat == null)
                {
                    _miEat = typeof(FirstPerson.WorldBoxMod).Assembly.GetType("FirstPerson.Eating")
                        .GetMethod("Eat", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (_miEat == null) return false;
                _miEat.Invoke(null, new object[] { host, new Action<string>(ShowToast) });
                _rebuildAt = 0f; // 数量可能变了，刷新
                return true;
            }
            catch { return false; }
        }

        // ---- Holstered 读写桥 ----
        private bool GetHolstered()
        {
            try
            {
                if (_fiHolstered != null && _mod != null)
                {
                    object v = _fiHolstered.GetValue(_mod);
                    if (v is bool) return (bool)v;
                }
            }
            catch { }
            return false;
        }

        private bool SetHolstered(bool value)
        {
            try
            {
                if (_fiHolstered == null || _mod == null) return false;
                bool cur = GetHolstered();
                if (cur != value) _fiHolstered.SetValue(_mod, value);
                _lastHolstered = value; // 自己引起的变更不触发 H 同步逻辑
                return true;
            }
            catch { return false; }
        }

        // ---- 武器仓库辅助 ----
        // 原版 createNewWeapon 会把武器槽里旧物品 takeAwayItem（clearUnit 脱离生物但不销毁），
        // 旧武器就此掉到地上"消失"。这里把被顶下的物品按生物暂存，快捷栏多一格仓库槽，
        // 点选时用 ActorEquipmentSlot.setItem 原物装回（保留耐久/品质），不重复生成新物品。
        private static void WLog(string msg)
        {
            Debug.Log("[WorldfallHotbar][武器] " + msg);
        }

        private Actor _lastHost; // 宿主变更追踪（仓库按生物分仓，换宿主时仓库不同）
        private readonly Dictionary<Actor, int> _hostIds = new Dictionary<Actor, int>(); // 自分配宿主编号（NML 编译环境下 Actor 取不到 GetInstanceID）
        private int _hostIdSeq;

        private string HostTag()
        {
            if (_host == null) return "无";
            int id;
            if (!_hostIds.TryGetValue(_host, out id))
            {
                id = ++_hostIdSeq;
                _hostIds[_host] = id;
            }
            return id.ToString();
        }

        private static string ItemDesc(Item it)
        {
            if (it == null) return "null";
            string id = it.asset != null ? it.asset.id : "?";
            return id + "(shouldRemove=" + it.shouldbe_removed + " 持有者=" + (it.getActor() != null ? "有" : "无") + ")";
        }

        private static bool ItemUsable(Item it)
        {
            return it != null && !it.shouldbe_removed && it.getActor() == null;
        }

        // 仓库物品有效性：无主，或已被宿主认领（setUnitHasIt），都算有效
        private static bool StashItemValid(Actor a, Item it)
        {
            if (it == null || it.shouldbe_removed) return false;
            Actor owner = it.getActor();
            return owner == null || owner == a;
        }

        private static List<Item> StashOf(Actor a, bool create = false)
        {
            if (a == null) return null;
            List<Item> l;
            if (!_weaponStash.TryGetValue(a, out l) || l == null)
            {
                if (!create) return null;
                l = new List<Item>();
                _weaponStash[a] = l;
            }
            return l;
        }

        private static void AddToStash(Actor a, Item it)
        {
            if (it == null) return;
            if (!StashItemValid(a, it))
            {
                WLog("入仓拒绝: " + ItemDesc(it));
                return;
            }
            if (it.asset == null) { WLog("入仓拒绝: 无资产"); return; }
            List<Item> l = StashOf(a, true);
            foreach (Item x in l)
            {
                if (x == it) return;                                     // 已在仓库
                if (x.asset != null && x.asset.id == it.asset.id)
                {
                    WLog("入仓拒绝: 同名已有 " + it.asset.id + "，旧物留地上");
                    return;
                }
            }
            l.Add(it);
            if (l.Count > 10)
            {
                Item drop = l[0];
                l.RemoveAt(0); // 上限保护（最多 10 件，足够容纳连续锻造的武器）
                if (drop != null)
                {
                    // 弃置：解除星标并释放认领，交回游戏正常回收
                    drop.setFavorite(false);
                    try { drop.clearUnit(); } catch { }
                }
                WLog("仓库已满，弃置 " + (drop != null && drop.asset != null ? drop.asset.id : "?"));
            }
            // 双重保护：
            // 1) 星标：无主物品会被 ItemManager.checkDeadObjects 回收，收藏状态豁免
            // 2) 宿主认领（setUnitHasIt）：新生物生成时 generateDefaultSpawnWeapons 会扫描
            //    "无主+不可销毁"的物品直接当初始武器抢走——星标恰好是头号目标！
            //    认领后 hasActor=true，扫描和回收都跳过它，AI 也捡不走
            it.setFavorite(true);
            try { it.setUnitHasIt(a); } catch { }
            WLog("已入仓 " + it.asset.id + "（仓内 " + l.Count + " 件，星标+宿主认领双保护）");
        }

        private static Item TakeFromStash(Actor a, string id)
        {
            List<Item> l = StashOf(a);
            if (l == null) return null;
            for (int i = l.Count - 1; i >= 0; i--)
            {
                Item x = l[i];
                if (!StashItemValid(a, x) || x.asset == null)
                {
                    WLog("仓库清理失效物品: " + ItemDesc(x));
                    l.RemoveAt(i);
                    continue;
                }
                if (x.asset.id == id)
                {
                    l.RemoveAt(i);
                    x.setFavorite(false); // 装回后解除星标
                    WLog("从仓库取出 " + id);
                    return x;
                }
            }
            return null;
        }

        private void EquipItem(Item it)
        {
            if (_host == null || it == null) return;
            try
            {
                if (_miSlotSetItem == null)
                {
                    _miSlotSetItem = typeof(ActorEquipmentSlot).GetMethod("setItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                if (_miSlotSetItem != null) _miSlotSetItem.Invoke(_host.equipment.weapon, new object[] { it, _host });
            }
            catch { }
        }

        private static void PruneStash()
        {
            // 清理死亡生物与失效物品，防止字典无限增长
            List<Actor> dead = null;
            foreach (KeyValuePair<Actor, List<Item>> kv in _weaponStash)
            {
                Actor a = kv.Key;
                List<Item> l = kv.Value;
                for (int i = l.Count - 1; i >= 0; i--)
                {
                    Item x = l[i];
                    if (x == null || x.asset == null) { l.RemoveAt(i); continue; }
                    if (!StashItemValid(a, x))
                    {
                        Actor thief = x.getActor();
                        WLog("仓库物品失效被清除: " + ItemDesc(x)
                            + (x.shouldbe_removed ? "（游戏已回收该物品）"
                            : thief != null ? "（被其他单位装备：" + (thief.asset != null ? thief.asset.id : "?") + "）"
                            : "（进了城市仓库）"));
                        l.RemoveAt(i);
                    }
                }
                if (a == null || !IsAlive(a) || l.Count == 0)
                {
                    if (dead == null) dead = new List<Actor>();
                    dead.Add(a);
                }
            }
            if (dead != null)
            {
                foreach (Actor a in dead)
                {
                    // 弃置仓库：解除星标，让游戏按正常规则回收这些无主物品
                    List<Item> l;
                    if (a != null && _weaponStash.TryGetValue(a, out l) && l != null)
                    {
                        foreach (Item x in l)
                        {
                            if (x == null) continue;
                            // 释放：解除星标+解除宿主认领，物品变回无主普通物品，游戏正常回收
                            x.setFavorite(false);
                            try { x.clearUnit(); } catch { }
                        }
                    }
                    _weaponStash.Remove(a);
                }
            }
        }

        private void EquipWeapon(string id)
        {
            if (_host == null || id == null) return;
            try
            {
                if (!IsAlive(_host)) return;
                if (CurrentWeaponId(_host) == id)
                {
                    ShowToast(T("已在此武器上", "Already holding this weapon"));
                    return;
                }
                // 1) 仓库里有同 id 的武器 → 原物装回，不重复生成
                Item stashed = TakeFromStash(_host, id);
                if (stashed != null)
                {
                    Item cur = _host.equipment.weapon.getItem();
                    EquipItem(stashed);
                    AddToStash(_host, cur); // 被顶下的当前武器收进仓库
                    HotSlot s0 = FindSlot(id);
                    ShowToast(T("已装备 ", "Equipped ") + (s0 != null && s0.Name != null ? s0.Name : id));
                    WLog("[宿主#" + HostTag() + "] 切到 " + id + "：仓库原物装回，被顶下的 " + ItemDesc(cur) + " 再入仓");
                    _rebuildAt = 0f;
                    return;
                }
                // 2) 原版生成新武器，被顶下的旧武器收进仓库而不是扔到地上
                Item old = _host.equipment.weapon.getItem();
                WLog("[宿主#" + HostTag() + "] 切到 " + id + "：仓库存货=" + (stashed != null) + "，当前武器=" + ItemDesc(old));
                if (_host.createNewWeapon(id))
                {
                    AddToStash(_host, old);
                    HotSlot s = FindSlot(id);
                    ShowToast(T("已装备 ", "Equipped ") + (s != null && s.Name != null ? s.Name : id));
                    _rebuildAt = 0f; // 立刻刷新
                }
                else
                {
                    ShowToast(T("这个生物不会用 ", "This creature cannot use ") + id);
                }
            }
            catch (Exception)
            {
                ShowToast(T("无法装备 ", "Cannot equip ") + id);
            }
        }

        // ================= 数据构建 =================

        private void EnsureWeapons()
        {
            if (_itemMap != null) return;
            _itemMap = new Dictionary<string, EquipmentAsset>();
            try
            {
                List<EquipmentAsset> all = AssetManager.items.list; // AssetLibrary<T>.list 公开字段
                if (all != null)
                {
                    foreach (EquipmentAsset a in all)
                    {
                        if (a == null || a.id == null) continue;
                        if (a.id.StartsWith("$") || a.id.StartsWith("boat_")) continue; // 模板/船用炮弹
                        if (a.equipment_type != EquipmentType.Weapon) continue;
                        // 天生攻击与投掷物不是可装备武器
                        switch (a.id)
                        {
                            case "hands":
                            case "jaws":
                            case "claws":
                            case "bite":
                            case "base_attack":
                            case "rocks":
                            case "snowball":
                                continue;
                        }
                        _itemMap[a.id] = a;
                    }
                }
            }
            catch
            {
                _itemMap = new Dictionary<string, EquipmentAsset>();
            }
        }

        // ---- 该生物自己的武器表：来自其种族配置 default_weapons（+当前手持） ----
        private List<EquipmentAsset> _actorWeapons;

        private void BuildActorWeapons()
        {
            EnsureWeapons();
            _actorWeapons = new List<EquipmentAsset>();
            try
            {
                if (_host != null && IsAlive(_host) && _host.asset != null)
                {
                    string[] dw = _host.asset.default_weapons;
                    if (dw != null)
                    {
                        foreach (string id in dw)
                        {
                            if (string.IsNullOrEmpty(id)) continue;
                            EquipmentAsset a;
                            if (_itemMap.TryGetValue(id, out a) && !_actorWeapons.Contains(a)) _actorWeapons.Add(a);
                        }
                    }
                }
            }
            catch { }
            // 当前手持的武器也要能看见/切回
            try
            {
                string cur = CurrentWeaponId(_host);
                if (!string.IsNullOrEmpty(cur))
                {
                    EquipmentAsset a;
                    if (_itemMap.TryGetValue(cur, out a) && !_actorWeapons.Contains(a)) _actorWeapons.Insert(0, a);
                }
            }
            catch { }
        }

        private void RebuildSlots()
        {
            _slots.Clear();
            PruneStash();
            BuildActorWeapons();
            string currentId = null;
            if (_host != null && IsAlive(_host)) currentId = CurrentWeaponId(_host);

            // 槽 0：当前手持
            _slots.Add(new HotSlot
            {
                IsWeapon = true,
                IsCurrent = true,
                Id = currentId,
                Name = currentId != null ? PrettyName(currentId) : "空手",
                Icon = currentId != null ? WeaponIcon(currentId) : null
            });

            // 槽 1-2：常驻采集工具（斧头 + 铁锤），切换武器不影响这两个槽位
            AddToolSlot("axe_iron", "axe_", "斧头", currentId);
            AddToolSlot("hammer_iron", "hammer_", "铁锤", currentId);

            // 槽 3..：该生物自己的武器配置（default_weapons）+ 当前手持
            foreach (EquipmentAsset a in _actorWeapons)
            {
                if (_slots.Count >= BaseSlots - 2) break; // 至少留 2 格给资源
                if (a.id == currentId) continue;
                bool isTool = false;
                foreach (HotSlot t in _slots) if (t.IsTool && t.Id == a.id) { isTool = true; break; }
                if (isTool) continue;
                _slots.Add(new HotSlot
                {
                    IsWeapon = true,
                    Id = a.id,
                    Name = LocalName(a.translation_key, a.id),
                    Icon = FirstSprite(a.gameplay_sprites)
                });
            }

            // 槽 ..：武器仓库——被换下的原物品（打造的/生成过的），点选原物装回
            List<Item> stash = StashOf(_host);
            if (stash != null)
            {
                foreach (Item it in stash)
                {
                    if (_slots.Count >= MaxSlots - 2)
                    {
                        WLog("仓库槽放不下（格子已满）: " + ItemDesc(it));
                        break;
                    }
                    if (it == null || it.asset == null) continue;
                    if (it.asset.id == currentId) continue;   // 已在手持格
                    bool dup = false;
                    foreach (HotSlot t in _slots)
                    {
                        if (t.Id == it.asset.id) { dup = true; break; } // 与默认武器槽同名，不重复占格
                    }
                    if (dup)
                    {
                        WLog("仓库槽跳过（已有同名槽位）: " + it.asset.id);
                        continue;
                    }
                    _slots.Add(new HotSlot
                    {
                        IsWeapon = true,
                        Id = it.asset.id,
                        Name = PrettyName(it.asset.id),
                        Icon = FirstSprite(it.asset.gameplay_sprites),
                        RefItem = it
                    });
                    WLog("仓库槽已显示: " + it.asset.id);
                }
            }

            // 剩余槽位：背包采集资源，MC 式按 64/格拆分（76 个 = 1 格 64 + 1 格 12）
            // 允许占用动态扩容区（最多到 MaxSlots），排序按总量降序，同种物品各格角标显示各自堆叠数
            Dictionary<string, ResourceContainer> bag = GetBag();
            if (bag != null && _slots.Count < MaxSlots)
            {
                List<ResourceContainer> items = new List<ResourceContainer>(bag.Values);
                items.Sort(delegate (ResourceContainer a, ResourceContainer b) { return b.amount.CompareTo(a.amount); });
                foreach (ResourceContainer c in items)
                {
                    if (_slots.Count >= MaxSlots) break;
                    if (c.amount <= 0) continue;
                    ResourceAsset ra = SafeResource(c.id);
                    string resName = ra != null ? LocalName(ResourceLocaleId(ra), c.id) : PrettyName(c.id);
                    int nutrition = ra != null ? ra.restore_nutrition : 0;
                    Sprite icon = ResourceIcon(ra);
                    for (int left = c.amount; left > 0 && _slots.Count < MaxSlots;)
                    {
                        int take = Mathf.Min(BagStack, left); // 每格最多 64
                        left -= take;
                        _slots.Add(new HotSlot
                        {
                            IsWeapon = false,
                            Id = c.id,
                            Name = resName,
                            Count = take,
                            Nutrition = nutrition,
                            Icon = icon
                        });
                    }
                }
            }

            // 补齐到 9 格（空格保持 MC 观感）
            while (_slots.Count < BaseSlots)
            {
                _slots.Add(new HotSlot { IsWeapon = false, Id = null, Name = "" });
            }
            if (_sel >= SlotCount) _sel = 0;
        }

        // ---- 常驻采集工具槽：优先指定 id，没有则退化为同类前缀（兼容 mod 装备库） ----
        private void AddToolSlot(string preferredId, string prefix, string label, string currentId)
        {
            if (_slots.Count >= BaseSlots - 2) return; // 至少留 2 格给资源
            if (_itemMap.ContainsKey(preferredId)) { AddToolSlotById(preferredId, label, currentId); return; }
            foreach (KeyValuePair<string, EquipmentAsset> kv in _itemMap)
            {
                if (kv.Key.StartsWith(prefix)) { AddToolSlotById(kv.Key, label, currentId); return; }
            }
        }

        private void AddToolSlotById(string id, string label, string currentId)
        {
            if (id == currentId) return; // 手上已是该工具，不再重复占格
            foreach (HotSlot s in _slots) if (s.Id == id) return; // 已在格上
            EquipmentAsset a = _itemMap[id];
            _slots.Add(new HotSlot
            {
                IsWeapon = true,
                IsTool = true,
                Id = id,
                Name = label + "（采集工具）",
                Icon = FirstSprite(a.gameplay_sprites)
            });
        }

        // ---- 常驻采集工具的两个固定 id（面板用） ----
        private List<EquipmentAsset> ToolAssets()
        {
            List<EquipmentAsset> list = new List<EquipmentAsset>();
            EnsureWeapons();
            foreach (string id in new string[] { "axe_iron", "hammer_iron" })
            {
                EquipmentAsset a;
                if (_itemMap.TryGetValue(id, out a)) list.Add(a);
                else
                {
                    string prefix = id.StartsWith("axe") ? "axe_" : "hammer_";
                    foreach (KeyValuePair<string, EquipmentAsset> kv in _itemMap)
                    {
                        if (kv.Key.StartsWith(prefix)) { list.Add(kv.Value); break; }
                    }
                }
            }
            return list;
        }

        // ================= 绘制 =================

        private void OnGUI()
        {
            try
            {
                Draw();
            }
            catch
            {
            }
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white, ScaleMode.StretchToFill);
            GUI.color = old;
        }

        private void Draw()
        {
            if (!_active || _mod == null || World.world == null || _slots.Count == 0) return;
            if (_white == null) return;
            GUI.depth = -1001;

            float p = ArtP();
            float sh = Screen.height;
            float sw = Screen.width;
            float slot = Mathf.Round(30f * p);
            float pad = Mathf.Round(3f * p);
            float gap = Mathf.Round(2f * p);

            Rect vitals = VitalsRect(new Rect(sw * 0.5f - 135f * p, sh - 37f * p, 270f * p, 37f * p));
            float w = SlotCount * slot + (SlotCount - 1) * gap + pad * 2f;
            // 关键：放在血条【下方】（血条 yMax 之下），贴屏幕底边，绝不遮挡血条
            float slabH = slot + pad * 2f;
            float y = vitals.yMax + Mathf.Round(4f * p);
            if (y + slabH > sh - 2f) y = sh - slabH - 2f; // 底部空间不足时贴屏幕底
            Rect slab = new Rect(Mathf.Round(vitals.center.x - w * 0.5f), Mathf.Round(y), Mathf.Round(w), Mathf.Round(slabH));

            DrawSlab(slab, p);

            for (int i = 0; i < SlotCount; i++)
            {
                float sx = slab.x + pad + i * (slot + gap);
                Rect cell = new Rect(sx, slab.y + pad, slot, slot);
                DrawCell(cell, i < _slots.Count ? _slots[i] : null, i == _sel, p);
            }

            // 面板
            if (_panel) DrawPanel(slab, p, sw);

            // 提示文字
            if (!string.IsNullOrEmpty(_toast) && Time.unscaledTime < _toastUntil)
            {
                float a = Mathf.Clamp01((_toastUntil - Time.unscaledTime) / 0.4f);
                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.alignment = TextAnchor.MiddleCenter;
                st.fontSize = Mathf.Max(10, Mathf.RoundToInt(8 * p));
                st.fontStyle = FontStyle.Bold;
                Color oc = st.normal.textColor;
                st.normal.textColor = new Color(0f, 0f, 0f, a);
                // 提示文字优先画在快捷栏下方（不往血条上盖），底部没空间再放上方
                bool below = slab.yMax + 24f * p < sh - 2f;
                Rect shadow = new Rect(slab.center.x - 200f, below ? slab.yMax + 2f : slab.y - 22f * p + 1f, 400f, 20f);
                GUI.Label(shadow, _toast, st);
                st.normal.textColor = new Color(1f, 1f, 1f, a);
                shadow.y -= 1f;
                GUI.Label(shadow, _toast, st);
            }

            // 键位说明面板：放在我们绘制流程的最后一步（本组件 depth=-1001 已在最上层），
            // 保证面板盖住血条、能力键、快捷栏等一切 UI，不会被任何元素挡住
            if (_helpRect.width > 1f && (HelpHover(_helpRect) || Time.unscaledTime < _helpShowUntil))
            {
                DrawHelpTooltip(_helpRect, p, _helpAlpha);
            }
        }

        private void DrawSlab(Rect r, float p)
        {
            // 暗橄榄色底板（贴近血条风格）
            Fill(r, new Color(0.11f, 0.12f, 0.09f, 0.94f));          // 外框
            Fill(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), new Color(0.22f, 0.24f, 0.17f, 0.96f));
            Fill(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, Mathf.Round(1f * p)), new Color(0.40f, 0.43f, 0.31f, 0.9f));
            Fill(new Rect(r.x + 1f, r.y + 1f, Mathf.Round(1f * p), r.height - 2f), new Color(0.40f, 0.43f, 0.31f, 0.9f));
            Fill(new Rect(r.x + 1f, r.yMax - 1f - Mathf.Round(1f * p), r.width - 2f, Mathf.Round(1f * p)), new Color(0.13f, 0.14f, 0.10f, 0.9f));
            Fill(new Rect(r.xMax - 1f - Mathf.Round(1f * p), r.y + 1f, Mathf.Round(1f * p), r.height - 2f), new Color(0.13f, 0.14f, 0.10f, 0.9f));
        }

        private void DrawCell(Rect cell, HotSlot s, bool selected, float p)
        {
            // 槽内凹底
            Fill(cell, new Color(0.15f, 0.16f, 0.12f, 0.95f));
            Fill(new Rect(cell.x, cell.y, cell.width, Mathf.Round(1f * p)), new Color(0.09f, 0.10f, 0.07f, 0.9f));
            Fill(new Rect(cell.x, cell.y, Mathf.Round(1f * p), cell.height), new Color(0.09f, 0.10f, 0.07f, 0.9f));
            Fill(new Rect(cell.x, cell.yMax - Mathf.Round(1f * p), cell.width, Mathf.Round(1f * p)), new Color(0.34f, 0.36f, 0.27f, 0.8f));
            Fill(new Rect(cell.xMax - Mathf.Round(1f * p), cell.y, Mathf.Round(1f * p), cell.height), new Color(0.34f, 0.36f, 0.27f, 0.8f));

            if (selected)
            {
                Rect f = new Rect(cell.x - Mathf.Round(2f * p), cell.y - Mathf.Round(2f * p), cell.width + Mathf.Round(4f * p), cell.height + Mathf.Round(4f * p));
                Fill(f, new Color(0.95f, 0.95f, 0.88f, 0.95f));
                Fill(new Rect(f.x + Mathf.Round(1.5f * p), f.y + Mathf.Round(1.5f * p), f.width - 3f * p, f.height - 3f * p), new Color(0.15f, 0.16f, 0.12f, 0.98f));
            }

            if (s == null || s.Id == null) return;

            // 图标
            if (s.Icon != null && s.Icon.texture != null)
            {
                Rect tr = s.Icon.textureRect;
                if (tr.width > 1f && tr.height > 1f && s.Icon.texture.width > 0 && s.Icon.texture.height > 0)
                {
                    // 等比缩放、居中，四周留 2p 呼吸边
                    float k = Mathf.Min((cell.width - 4f * p) / tr.width, (cell.height - 4f * p) / tr.height);
                    float iw = tr.width * k, ih = tr.height * k;
                    Rect icon = new Rect(cell.center.x - iw * 0.5f, cell.center.y - ih * 0.5f, iw, ih);
                    GUI.DrawTextureWithTexCoords(icon, s.Icon.texture,
                        new Rect(tr.x / s.Icon.texture.width, tr.y / s.Icon.texture.height,
                                 tr.width / s.Icon.texture.width, tr.height / s.Icon.texture.height), true);
                }
            }

            // 数量（资源）
            if (!s.IsWeapon && s.Count > 0)
            {
                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.fontSize = Mathf.Max(9, Mathf.RoundToInt(7 * p));
                st.fontStyle = FontStyle.Bold;
                st.alignment = TextAnchor.LowerRight;
                st.normal.textColor = Color.white;
                Rect tr2 = new Rect(cell.x, cell.yMax - 14f * p, cell.width - 2f * p, 13f * p);
                GUI.color = new Color(0, 0, 0, 0.9f);
                GUI.Label(new Rect(tr2.x + 1, tr2.y + 1, tr2.width, tr2.height), s.Count.ToString(), st);
                GUI.color = Color.white;
                GUI.Label(tr2, s.Count.ToString(), st);
            }

            // 当前手持标记（金色下划线）
            if (s.IsCurrent && s.Id != null)
            {
                Fill(new Rect(cell.x + 2f * p, cell.yMax - 3f * p, cell.width - 4f * p, Mathf.Round(2f * p)), new Color(0.95f, 0.78f, 0.25f, 0.95f));
            }
            // 常驻采集工具标记（绿色下划线，左上角小绿点）
            else if (s.IsTool && s.Id != null)
            {
                Fill(new Rect(cell.x + 2f * p, cell.yMax - 3f * p, cell.width - 4f * p, Mathf.Round(2f * p)), new Color(0.35f, 0.85f, 0.35f, 0.9f));
                Fill(new Rect(cell.x + 2f * p, cell.y + 2f * p, Mathf.Round(4f * p), Mathf.Round(4f * p)), new Color(0.35f, 0.85f, 0.35f, 0.95f));
            }

            // 点击选择
            Event e = Event.current;
            if (e != null && e.type == EventType.MouseDown && cell.Contains(e.mousePosition))
            {
                SelectSlot(_slots.IndexOf(s), true);
                e.Use();
            }
            // 悬停提示
            else if (e != null && e.type == EventType.Repaint && cell.Contains(e.mousePosition))
            {
                string tip = s.Name;
                if (!s.IsWeapon && s.Count > 0) tip += " ×" + s.Count;
                if (s.Nutrition > 0) tip += T("（恢复 ", " (restores ") + s.Nutrition + T(" 饱食）", " nutrition)");
                if (s.IsWeapon && !s.IsCurrent) tip += T("（点击/选中装备）", " (click/select to equip)");
                if (!string.IsNullOrEmpty(tip)) DrawTooltip(cell, tip, p);
            }
        }

        private void DrawTooltip(Rect near, string text, float p)
        {
            if (string.IsNullOrEmpty(text)) return;
            GUIStyle st = new GUIStyle(GUI.skin.label);
            st.fontSize = Mathf.Max(10, Mathf.RoundToInt(8 * p));
            st.alignment = TextAnchor.MiddleLeft;
            Vector2 size = st.CalcSize(new GUIContent(text));
            float tw = size.x + 10f * p;
            float th = size.y + 2f * p;
            float tx = Mathf.Clamp(near.center.x - tw * 0.5f, 4f, Screen.width - tw - 4f);
            float ty = near.y - th - 4f * p;
            if (ty < 2f) ty = near.yMax + 4f * p;
            Rect box = new Rect(tx, ty, tw, th);
            // MC 风格提示框：近黑底 + 紫边
            Fill(new Rect(box.x - 1f, box.y - 1f, box.width + 2f, box.height + 2f), new Color(0.16f, 0.04f, 0.35f, 0.95f));
            Fill(box, new Color(0.06f, 0.0f, 0.06f, 0.94f));
            st.normal.textColor = new Color(0.95f, 0.95f, 0.9f);
            GUI.Label(new Rect(box.x + 5f * p, box.y, box.width - 10f * p, box.height), text, st);
        }

        // ---------- B 背包面板（与快捷栏一致的暗橄榄风格） ----------

        private Color McPanel = new Color(0.22f, 0.24f, 0.17f, 0.97f);  // 底板：快捷栏同款暗橄榄
        private Color McSlot = new Color(0.15f, 0.16f, 0.12f, 0.95f);   // 槽底：快捷栏凹槽同色
        private Color McDark = new Color(0.09f, 0.10f, 0.07f, 0.9f);    // 凹槽上/左暗边
        private Color McLight = new Color(0.34f, 0.36f, 0.27f, 0.85f);  // 凹槽下/右亮边
        private Color McTitle = new Color(0.90f, 0.92f, 0.78f);         // 标题：浅米色（深底可读）

        private void DrawMcSlotCell(Rect rc)
        {
            Fill(rc, McDark);                                                     // 上/左 暗边（凹槽）
            Fill(new Rect(rc.x, rc.y, rc.width, Mathf.Max(1f, Mathf.Round(1f * 1f))), McDark);
            Fill(new Rect(rc.xMax - 1f, rc.y, 1f, rc.height), McLight);           // 下/右 亮边
            Fill(new Rect(rc.x, rc.yMax - 1f, rc.width, 1f), McLight);
            Fill(new Rect(rc.x + 1f, rc.y + 1f, rc.width - 2f, rc.height - 2f), McSlot);
        }

        private void DrawPanel(Rect hotbar, float p, float sw)
        {
            int cols = 9;
            float cell = Mathf.Round(34f * p);
            float gap = Mathf.Round(3f * p);
            float pad = Mathf.Round(8f * p);
            EnsureWeapons();
            Dictionary<string, ResourceContainer> bag = GetBag();

            List<ResourceContainer> items = new List<ResourceContainer>(); // 已按 64 拆分的"格"
            Dictionary<string, int> totals = new Dictionary<string, int>(); // 每种资源总量（tooltip 用）
            if (bag != null)
            {
                List<ResourceContainer> src = new List<ResourceContainer>();
                foreach (KeyValuePair<string, ResourceContainer> kv in bag)
                    if (kv.Value.amount > 0) src.Add(kv.Value);
                src.Sort(delegate (ResourceContainer a, ResourceContainer b) { return b.amount.CompareTo(a.amount); });
                foreach (ResourceContainer c in src)
                {
                    totals[c.id] = c.amount;
                    for (int left = c.amount; left > 0;)
                    {
                        int take = Mathf.Min(64, left); // 每格最多 64，溢出到下一格
                        left -= take;
                        ResourceContainer piece = c; // struct 拷贝
                        piece.amount = take;
                        items.Add(piece);
                    }
                }
            }
            int resCount = items.Count;
            // 资源区至少 2 整行空格（MC 观感：没东西也画满网格），不足整行时补空格
            int resRows = Mathf.Max(2, Mathf.CeilToInt(resCount / (float)cols));
            int resCells = resRows * cols;
            int weaponCells = _actorWeapons != null ? _actorWeapons.Count : 0;
            int weaponRows = Mathf.CeilToInt(Mathf.Max(weaponCells, 1) / (float)cols);
            List<EquipmentAsset> tools = ToolAssets();

            float gridW = cols * cell + (cols - 1) * gap;
            float titleH = Mathf.Round(18f * p);
            float sectionH = Mathf.Round(14f * p);
            float rowH = cell + gap;
            float w = gridW + pad * 2f;
            float h = pad + titleH + sectionH + rowH /*工具行*/ + sectionH + weaponRows * (cell + gap) + sectionH + resRows * (cell + gap) + pad;

            Rect panel = new Rect(Mathf.Round(sw * 0.5f - w * 0.5f), hotbar.y - h - Mathf.Round(10f * p), w, h);
            if (panel.y < 4f) panel.y = 4f;

            // 暗橄榄面板：深外框 + 上左高光 + 下右阴影（与快捷栏底板同款）
            Fill(new Rect(panel.x - 2f, panel.y - 2f, panel.width + 4f, panel.height + 4f), new Color(0.11f, 0.12f, 0.09f, 0.97f));
            Fill(panel, McPanel);
            Fill(new Rect(panel.x, panel.y, panel.width, Mathf.Round(2f * p)), new Color(0.40f, 0.43f, 0.31f, 0.9f));
            Fill(new Rect(panel.x, panel.y, Mathf.Round(2f * p), panel.height), new Color(0.40f, 0.43f, 0.31f, 0.9f));
            Fill(new Rect(panel.x, panel.yMax - Mathf.Round(2f * p), panel.width, Mathf.Round(2f * p)), new Color(0.13f, 0.14f, 0.10f, 0.9f));
            Fill(new Rect(panel.xMax - Mathf.Round(2f * p), panel.y, Mathf.Round(2f * p), panel.height), new Color(0.13f, 0.14f, 0.10f, 0.9f));

            GUIStyle title = new GUIStyle(GUI.skin.label);
            title.fontSize = Mathf.Max(11, Mathf.RoundToInt(9 * p));
            title.fontStyle = FontStyle.Bold;
            title.alignment = TextAnchor.MiddleLeft;
            title.normal.textColor = McTitle;
            float cy = panel.y + pad;
            GUI.Label(new Rect(panel.x + pad, cy, w - pad * 2f, titleH), "背 包", title);
            cy += titleH;

            // ---- 工具区（常驻采集工具：斧头/铁锤，切换武器不影响） ----
            {
                GUIStyle toolSection = new GUIStyle(GUI.skin.label);
                toolSection.fontSize = Mathf.Max(10, Mathf.RoundToInt(7 * p));
                toolSection.alignment = TextAnchor.MiddleLeft;
                toolSection.normal.textColor = new Color(0.50f, 0.90f, 0.50f);
                GUI.Label(new Rect(panel.x + pad, cy, w - pad * 2f, sectionH), "工 具（常驻 · 采集用，绿色标记）", toolSection);
                cy += sectionH;

                string curId2 = _host != null ? CurrentWeaponId(_host) : null;
                for (int col = 0; col < cols; col++)
                {
                    Rect rc = new Rect(panel.x + pad + col * (cell + gap), cy, cell, cell);
                    DrawMcSlotCell(rc);
                    if (col < tools.Count)
                    {
                        EquipmentAsset a = tools[col];
                        DrawPanelItem(rc, FirstSprite(a.gameplay_sprites), null,
                            LocalName(a.translation_key, a.id) + T("（采集工具·点击装备）", " (gathering tool - click to equip)"), p);
                        Fill(new Rect(rc.x + 2f * p, rc.yMax - 3f * p, rc.width - 4f * p, Mathf.Round(2f * p)), new Color(0.35f, 0.85f, 0.35f, 0.9f));
                        ClickEquip(rc, a.id);
                    }
                }
                cy += rowH;
            }

            // ---- 武器区（第一行） ----
            GUIStyle section = new GUIStyle(GUI.skin.label);
            section.fontSize = Mathf.Max(10, Mathf.RoundToInt(7 * p));
            section.alignment = TextAnchor.MiddleLeft;
            section.normal.textColor = new Color(0.85f, 0.87f, 0.75f);
            GUI.Label(new Rect(panel.x + pad, cy, w - pad * 2f, sectionH), "武 器（点击装备）", section);
            cy += sectionH;

            string curId = _host != null ? CurrentWeaponId(_host) : null;
            if (weaponCells == 0)
            {
                GUIStyle empty = new GUIStyle(GUI.skin.label);
                empty.fontSize = Mathf.Max(10, Mathf.RoundToInt(7 * p));
                empty.alignment = TextAnchor.MiddleLeft;
                empty.normal.textColor = new Color(0.62f, 0.64f, 0.55f);
                GUI.Label(new Rect(panel.x + pad, cy, w - pad * 2f, cell), "（该生物没有自己的武器配置，用双手/当前武器）", empty);
            }
            for (int i = 0; i < weaponRows * cols; i++)
            {
                int col = i % cols;
                int row = i / cols;
                Rect rc = new Rect(panel.x + pad + col * (cell + gap), cy + row * (cell + gap), cell, cell);
                DrawMcSlotCell(rc);
                if (i < weaponCells)
                {
                    EquipmentAsset a = _actorWeapons[i];
                    bool cur = a.id == curId;
                    DrawPanelItem(rc, FirstSprite(a.gameplay_sprites), null,
                        LocalName(a.translation_key, a.id) + (cur ? T("（当前手持）", " (currently held)") : T("（点击装备）", " (click to equip)")), p);
                    if (cur)
                        Fill(new Rect(rc.x + 2f * p, rc.yMax - 3f * p, rc.width - 4f * p, Mathf.Round(2f * p)), new Color(0.95f, 0.78f, 0.25f, 0.95f));
                    ClickEquip(rc, a.id);
                }
            }
            cy += weaponRows * (cell + gap);

            // ---- 资源区 ----
            GUI.Label(new Rect(panel.x + pad, cy, w - pad * 2f, sectionH), "采 集 / 资 源", section);
            cy += sectionH;

            if (resCount == 0)
            {
                GUIStyle empty = new GUIStyle(GUI.skin.label);
                empty.fontSize = Mathf.Max(10, Mathf.RoundToInt(7 * p));
                empty.alignment = TextAnchor.MiddleLeft;
                empty.normal.textColor = new Color(0.62f, 0.64f, 0.55f);
                GUI.Label(new Rect(panel.x + pad, cy, w - pad * 2f, cell), "（空）—— 去砍树、采集、购买后这里会显示物品", empty);
            }
            // 画满 resRows 行 × 9 列的等距网格：有物的格子放物品，其余保持 MC 空槽
            for (int i = 0; i < resCells; i++)
            {
                int col = i % cols;
                int row = i / cols;
                Rect rc = new Rect(panel.x + pad + col * (cell + gap), cy + row * (cell + gap), cell, cell);
                DrawMcSlotCell(rc);
                if (i >= resCount) continue;
                ResourceContainer c = items[i];
                ResourceAsset ra = SafeResource(c.id);
                int total = totals.TryGetValue(c.id, out int t) ? t : c.amount;
                string tip = (ra != null ? LocalName(ResourceLocaleId(ra), c.id) : PrettyName(c.id)) + " ×" + c.amount +
                             (total > c.amount ? "（共 " + total + "）" : "") +
                             (ra != null && ra.restore_nutrition > 0 ? T("（J 键进食）", " (J to eat)") : "");
                DrawPanelItem(rc, ResourceIcon(ra), c.amount.ToString(), tip, p);
            }
        }

        private void DrawPanelItem(Rect rc, Sprite icon, string count, string tooltip, float p)
        {
            if (icon != null && icon.texture != null)
            {
                Rect tr = icon.textureRect;
                if (tr.width > 1f && tr.height > 1f && icon.texture.width > 0 && icon.texture.height > 0)
                {
                    // 等比缩放、居中，四周留 3p 呼吸边，保证任何贴图都完整落在格内
                    float k = Mathf.Min((rc.width - 6f * p) / tr.width, (rc.height - 6f * p) / tr.height);
                    k = Mathf.Min(k, Mathf.Max(rc.width, rc.height)); // 防御：k 不会离谱
                    GUI.DrawTextureWithTexCoords(
                        new Rect(rc.center.x - tr.width * k * 0.5f, rc.center.y - tr.height * k * 0.5f, tr.width * k, tr.height * k),
                        icon.texture,
                        new Rect(tr.x / icon.texture.width, tr.y / icon.texture.height, tr.width / icon.texture.width, tr.height / icon.texture.height),
                        true);
                }
            }
            if (!string.IsNullOrEmpty(count))
            {
                GUIStyle st = new GUIStyle(GUI.skin.label);
                st.fontSize = Mathf.Max(9, Mathf.RoundToInt(7 * p));
                st.fontStyle = FontStyle.Bold;
                st.alignment = TextAnchor.LowerRight;
                st.normal.textColor = Color.white;
                GUI.color = new Color(0, 0, 0, 0.85f);
                GUI.Label(new Rect(rc.x + 1, rc.y + 1, rc.width - 3f * p, rc.height - 2f * p), count, st);
                GUI.color = Color.white;
                GUI.Label(new Rect(rc.x, rc.y, rc.width - 3f * p, rc.height - 2f * p), count, st);
            }
            Event e = Event.current;
            if (e != null && e.type == EventType.Repaint && rc.Contains(e.mousePosition) && !string.IsNullOrEmpty(tooltip))
            {
                DrawTooltip(rc, tooltip, p);
            }
        }

        private void ClickEquip(Rect rc, string weaponId)
        {
            Event e = Event.current;
            if (e != null && e.type == EventType.MouseDown && rc.Contains(e.mousePosition))
            {
                EquipWeapon(weaponId);
                e.Use();
            }
        }

        // ================= 数据访问 =================

        private static FirstPerson.WorldBoxMod _cachedMod;
        private static float _nextModProbe;

        private static FirstPerson.WorldBoxMod FindFpMod()
        {
            if (_cachedMod != null) return _cachedMod;
            if (Time.unscaledTime < _nextModProbe) return null;
            _nextModProbe = Time.unscaledTime + 2f;
            _cachedMod = Object.FindObjectOfType<FirstPerson.WorldBoxMod>();
            return _cachedMod;
        }

        private static bool IsAlive(Actor a)
        {
            try { return a.isAlive(); }
            catch { return true; }
        }

        private static string CurrentWeaponId(Actor a)
        {
            try { return FirstPersonNml.WeaponIdOf(a); }
            catch { return null; }
        }

        private static ResourceAsset SafeResource(string id)
        {
            try { return AssetManager.resources.get(id); }
            catch { return null; }
        }

        private static string ResourceLocaleId(ResourceAsset ra)
        {
            try { return ra != null ? ra.getLocaleID() : null; }
            catch { return null; }
        }

        private Dictionary<string, ResourceContainer> GetBag()
        {
            try
            {
                if (_host == null || _host.inventory == null) return null;
                return _host.inventory.getResources();
            }
            catch { return null; }
        }

        private static Sprite FirstSprite(Sprite[] list)
        {
            if (list == null) return null;
            foreach (Sprite s in list) if (s != null) return s;
            return null;
        }

        private static Sprite WeaponIcon(string id)
        {
            try
            {
                foreach (EquipmentAsset a in AssetManager.items.list)
                {
                    if (a != null && a.id == id) return FirstSprite(a.gameplay_sprites);
                }
            }
            catch { }
            return null;
        }

        private static Sprite ResourceIcon(ResourceAsset ra)
        {
            if (ra == null) return null;
            try
            {
                if (_miIconNamed != null && !string.IsNullOrEmpty(ra.path_icon))
                {
                    object r = _miIconNamed.Invoke(null, new object[] { ra.path_icon });
                    if (r is Sprite) return (Sprite)r;
                }
            }
            catch { }
            return FirstSprite(ra.gameplay_sprites);
        }

        private static string LocalName(string key, string fallback)
        {
            string s = LocGetString(key);
            if (!string.IsNullOrEmpty(s)) return s;
            return PrettyName(fallback);
        }

        // 按游戏当前语言取翻译（LocalizedTextManager.getText 自动跟随语言设置）
        private static string LocGetString(string key)
        {
            if (string.IsNullOrEmpty(key) || _miGetString == null) return null;
            try
            {
                // 缺失的 key 不查：getText 会对缺失 key 报错并写 missing 文件
                if (_miStringExists != null && !(bool)_miStringExists.Invoke(null, new object[] { key })) return null;
                object r = _miGetStringArgs >= 3
                    ? _miGetString.Invoke(null, new object[] { key, null, false })
                    : _miGetString.Invoke(null, new object[] { key });
                string s = r as string;
                return s == key ? null : s; // 万一仍返回 key 原文，按缺失处理
            }
            catch { return null; }
        }

        // ---- 模组自身文案的双语支持：按游戏语言（is_hanzi）实时判定 ----
        private static float _langCheckAt = -999f;
        private static bool _langChinese = true; // 检测失败时默认中文（本模组主要受众）

        private static bool LangChinese
        {
            get
            {
                float t = Time.unscaledTime;
                if (t >= _langCheckAt) // 每秒刷新一次，游戏内切语言也能跟上
                {
                    _langCheckAt = t + 1f;
                    try
                    {
                        object lang = _fiCurLang != null ? _fiCurLang.GetValue(null) : null;
                        if (lang != null && _fiIsHanzi != null)
                            _langChinese = (bool)_fiIsHanzi.GetValue(lang);
                    }
                    catch { }
                }
                return _langChinese;
            }
        }

        // T(中文, English)：按游戏语言选择文案
        private static string T(string zh, string en)
        {
            return LangChinese ? zh : en;
        }

        // ---- Worldfall 本体写死中文的运行时英译层 ----
        // 本体（FirstPerson.dll）有 ~2700 条硬编码中文（对话/提示/交互键）。全量离线翻译不现实，
        // 这里在两个文本咽喉点（WorldBoxMod.Toast / GameArt.Label）挂 prefix：
        // 非中文语言环境下按 WorldfallEnDict 词典做分段替换（长键优先），结果缓存，中文环境零开销直通。
        private static readonly Dictionary<string, string> _enCache = new Dictionary<string, string>();

        private static bool HasCJK(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] >= 0x4e00 && s[i] <= 0x9fff) return true;
            }
            return false;
        }

        private static string FpEn(string s)
        {
            if (s == null || s.Length == 0 || LangChinese || !HasCJK(s)) return s;
            string r;
            if (_enCache.TryGetValue(s, out r)) return r;
            r = s;
            foreach (KeyValuePair<string, string> kv in WorldfallEnDict.Pairs)
                if (r.Contains(kv.Key)) r = r.Replace(kv.Key, kv.Value);
            if (_enCache.Count > 4096) _enCache.Clear();
            _enCache[s] = r;
            return r;
        }

        private static void ToastEnPrefix(ref string message) { message = FpEn(message); }

        private static void LabelEnPrefix(ref string text) { text = FpEn(text); }

        private static void GuiLabelEnPrefix(ref string text) { text = FpEn(text); }

        private static void GuiLabelContentEnPrefix(UnityEngine.GUIContent content)
        {
            if (content != null) content.text = FpEn(content.text);
        }

        private static string PrettyName(string id)
        {
            if (string.IsNullOrEmpty(id)) return "";
            return id.Replace('_', ' ');
        }

        private HotSlot FindSlot(string id)
        {
            foreach (HotSlot s in _slots) if (s != null && s.Id == id) return s;
            return null;
        }

        private void ShowToast(string text)
        {
            _toast = text;
            _toastUntil = Time.unscaledTime + 2.2f;
        }

        // ================= 反射桥 =================

        private bool AnyMenuOpen()
        {
            try
            {
                if (_piAnyMenu != null)
                {
                    object v = _piAnyMenu.GetValue(_mod);
                    if (v is bool) return (bool)v;
                }
            }
            catch { }
            return false;
        }

        private void CacheFov()
        {
            try
            {
                if (_fiSettings == null || _mod == null) return;
                object st = _fiSettings.GetValue(_mod);
                if (st == null || _fiFov == null) return;
                _fovKeep = (float)_fiFov.GetValue(st);
            }
            catch { }
        }

        private void RestoreFov()
        {
            try
            {
                if (_fiSettings == null || _mod == null || _fovKeep < 0f) return;
                object st = _fiSettings.GetValue(_mod);
                if (st == null || _fiFov == null) return;
                _fiFov.SetValue(st, _fovKeep);
            }
            catch { }
        }

        private Rect VitalsRect(Rect fallback)
        {
            try
            {
                if (_fiHud != null && _fiVitals != null)
                {
                    object hud = _fiHud.GetValue(_mod);
                    if (hud != null)
                    {
                        object v = _fiVitals.GetValue(hud);
                        if (v is Rect) return (Rect)v;
                    }
                }
            }
            catch { }
            return fallback;
        }

        private static float ArtP()
        {
            try
            {
                if (_fiGameArtP != null)
                {
                    object v = _fiGameArtP.GetValue(null);
                    if (v is float) return (float)v;
                }
            }
            catch { }
            return 2f;
        }

        private bool HoverHotbar()
        {
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            float p = ArtP();
            float slot = Mathf.Round(30f * p);
            float pad = Mathf.Round(3f * p);
            float w = SlotCount * slot + (SlotCount - 1) * 2f * p + pad * 2f;
            Rect vitals = VitalsRect(new Rect(Screen.width * 0.5f - 135f * p, Screen.height - 37f * p, 270f * p, 37f * p));
            float y = vitals.yMin - slot - pad * 2f - 4f * p;
            Rect slab = new Rect(vitals.center.x - w * 0.5f, y - 2f * p, w, slot + pad * 2f + 4f * p);
            return slab.Contains(m);
        }

        private bool HoverPanel()
        {
            // 面板打开时，屏幕中上部区域视为悬停（避免点击穿透到攻击）
            Vector2 m = Input.mousePosition;
            m.y = Screen.height - m.y;
            return m.y < Screen.height * 0.75f && m.x > Screen.width * 0.15f && m.x < Screen.width * 0.85f;
        }

        private static void EnsureReflection()
        {
            if (_reflReady) return;
            _reflReady = true;
            try
            {
                Type modType = typeof(FirstPerson.WorldBoxMod);
                Assembly fp = modType.Assembly;
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                _fiHost = modType.GetField("_host", All);
                _fiActive = modType.GetField("_active", All);
                _fiSettings = modType.GetField("Settings", All);
                _fiHud = modType.GetField("_hud", All);
                _piAnyMenu = modType.GetProperty("AnyMenu", All);
                _fiHolstered = modType.GetField("Holstered", All);
                _fiUnholsterFrame = modType.GetField("_unholsterFrame", All);

                Type settingsType = fp.GetType("FirstPerson.Settings");
                if (settingsType != null) _fiFov = settingsType.GetField("FieldOfView", All);

                Type hudType = fp.GetType("FirstPerson.Hud");
                if (hudType != null) _fiVitals = hudType.GetField("_vitalsRect", All);
                _fiMenuLeft = hudType != null ? hudType.GetField("_menuLeft", All) : null;
                _fiBagTop = hudType != null ? hudType.GetField("_bagTop", All) : null;

                // GameArt（internal static）：底部按钮排帮助按钮的绘制桥
                Type ga = fp.GetType("FirstPerson.GameArt");
                if (ga != null)
                {
                    _miStoneButton = ga.GetMethod("StoneButton", All);
                    _miLabel = ga.GetMethod("Label", All);
                    _miSliced = ga.GetMethod("Sliced", All);
                    _fiKeyOrange = ga.GetField("KeyOrange", All);
                    _fiTooltipSprite = ga.GetField("Tooltip", All);
                }

                Type hooksType = fp.GetType("FirstPerson.PossessionHooks");
                if (hooksType != null) _fiBlockAttack = hooksType.GetField("BlockAttack", All);

                // 音频与设置菜单
                _fiAudio = modType.GetField("_audio", All);
                Type audioType = fp.GetType("FirstPerson.FirstPersonAudio");
                if (audioType != null)
                {
                    _fiMusicVol = audioType.GetField("MusicVolume", All);
                    _fiGameWant = audioType.GetField("_gameMusicWant", All);
                    _fiGameWait = audioType.GetField("_gameMusicWait", All);
                }
                _fiSettingsMenu = modType.GetField("_settingsMenu", All);
                Type menuType = fp.GetType("FirstPerson.SettingsMenu");
                if (menuType != null)
                {
                    _fiSettingsMenuRows = menuType.GetField("_rows", All);
                    Type rowType = menuType.GetNestedType("Row", All);
                    if (rowType != null)
                    {
                        _fiRowName = rowType.GetField("Name", All);
                        _fiRowNote = rowType.GetField("Note", All);
                        _fiRowMin = rowType.GetField("Min", All);
                        _fiRowMax = rowType.GetField("Max", All);
                        _fiRowStep = rowType.GetField("Step", All);
                        _fiRowRatio = rowType.GetField("Ratio", All);
                        _fiRowValue = rowType.GetField("Value", All);
                        _fiRowSetValue = rowType.GetField("SetValue", All);
                        _fiRowShow = rowType.GetField("Show", All);
                    }
                }

                Type artType = fp.GetType("FirstPerson.GameArt");
                if (artType != null)
                {
                    _fiGameArtP = artType.GetField("P", All);
                    _miIconNamed = artType.GetMethod("IconNamed", All, null, new Type[] { typeof(string) }, null);
                }

                // 游戏本地化：WorldBox 用 LocalizedTextManager.getText(key, text, forceEnglish)
                //（静态，自动按当前游戏语言返回；CLocalization/Localization 均不存在）
                Type loc = null;
                try
                {
                    foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (a.GetName().Name == "Assembly-CSharp")
                        {
                            loc = a.GetType("LocalizedTextManager");
                            break;
                        }
                    }
                }
                catch { }
                if (loc == null) loc = Type.GetType("LocalizedTextManager, Assembly-CSharp");
                if (loc != null)
                {
                    foreach (MethodInfo mi in loc.GetMethods(All))
                    {
                        if (!mi.IsStatic || mi.Name != "getText") continue;
                        ParameterInfo[] ps = mi.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType == typeof(string))
                        {
                            _miGetString = mi;
                            _miGetStringArgs = ps.Length;
                            break;
                        }
                    }
                    _fiCurLang = loc.GetField("current_language", All);
                    if (_fiCurLang != null && _fiCurLang.FieldType != null)
                        _fiIsHanzi = _fiCurLang.FieldType.GetField("is_hanzi", All);
                    foreach (MethodInfo mi in loc.GetMethods(All))
                    {
                        if (mi.IsStatic && mi.Name == "stringExists")
                        {
                            ParameterInfo[] ps = mi.GetParameters();
                            if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) { _miStringExists = mi; break; }
                        }
                    }
                }
            }
            catch
            {
                // 反射失败也不影响游戏本体；相关功能自动降级
            }
        }
    }

    // Worldfall 背景音乐独立音量（静态，供 F2 设置行委托与快捷键共用）
    internal static class WorldfallMusic
    {
        private const string Key = "WorldfallHotbar_MusicVolume";
        private static float _vol = -1f;
        private static float _last = 1f;

        public static float Last { get { return _last; } }

        public static float Volume
        {
            get
            {
                if (_vol < 0f)
                {
                    _vol = Mathf.Clamp01(PlayerPrefs.GetFloat(Key, 1f));
                    if (_vol > 0.01f) _last = _vol;
                }
                return _vol;
            }
            set
            {
                _vol = Mathf.Clamp01(value);
                if (_vol > 0.01f) _last = _vol;
                PlayerPrefs.SetFloat(Key, _vol);
            }
        }

        // F2 设置行百分比显示（0 时显示“关闭”，与原版行一致）
        public static string Show(float v)
        {
            return v <= 0f ? "关闭" : Mathf.RoundToInt(v * 100f) + "%";
        }
    }

    // WeaponOf 是 WorldBoxMod 的私有静态方法，放个桥接类集中处理
    internal static class FirstPersonNml
    {
        private static MethodInfo _mi;
        private static bool _tried;

        internal static string WeaponIdOf(Actor a)
        {
            if (!_tried)
            {
                _tried = true;
                try
                {
                    _mi = typeof(FirstPerson.WorldBoxMod).GetMethod("WeaponOf",
                        BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(Actor) }, null);
                }
                catch { }
            }
            if (_mi != null)
            {
                try
                {
                    object r = _mi.Invoke(null, new object[] { a });
                    return r as string;
                }
                catch { }
            }
            // 兜底：直接读 Actor 内部方法
            try
            {
                MethodInfo m = typeof(Actor).GetMethod("getWeaponAsset",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m != null)
                {
                    EquipmentAsset ea = m.Invoke(a, null) as EquipmentAsset;
                    return ea != null ? ea.id : null;
                }
            }
            catch { }
            return null;
        }
    }
}
