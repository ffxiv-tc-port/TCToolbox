using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TCToolbox.Core;

namespace TCToolbox.Modules;

/// <summary>
/// 放大遊戲內建的中文輸入法候選字清單（也連帶放大同一個位置的文字補完／翻譯輔助選單）。
/// </summary>
/// <remarks>
/// <para>
/// 📌 <b>零 hook、零特徵碼、零遊戲函式呼叫。</b>整條路徑只有「讀欄位」與「寫兩個 float
/// ＋一個旗標」，所以任何一個假設不成立時的最差結果是<b>功能沒作用</b>，不是崩潰。
/// 參考 DailyRoutines <c>LargerIME</c> 的想法（縮放文字輸入元件的 4 號節點），
/// 但<b>刻意不沿用它的實作</b>——DR 是 hook <c>AtkComponentTextInput::ReceiveEvent</c>，
/// 那需要一條特徵碼、而且每次輸入事件都要進出 detour。
/// </para>
/// <para>
/// 🔑 <b>4 號節點是什麼，離線查證過（2026-09-03，<c>ui/uld/ChatLog.uld</c>）。</b>
/// ChatLog 的 <c>Component 1012 (TextInput)</c> 內部共 17 個節點：
/// <list type="bullet">
/// <item><c>Node 1</c>（Res 186x28）＝輸入框本體，<c>Node 16</c>＝輸入中的文字、<c>Node 17</c>＝底圖。</item>
/// <item><c>Node 4</c>（Res 186x221，位置 -1,16）＝<b>浮在輸入框下方的候選清單容器</b>；
/// 它底下掛著 <c>Node 5~13</c> 九個按鈕元件、<c>Node 14</c> 頁碼文字、<c>Node 15</c> 底圖。</item>
/// </list>
/// 這與 <c>FFXIVClientStructs</c> 的 <c>AtkComponentTextInput</c> 欄位一一對得起來：
/// <c>_autoTranslateMenuButtons</c> 正好是 <c>FixedSizeArray9</c>、另有
/// <c>AutoTranslateMenuPageInfoTextNode</c> 與 <c>AutoTranslateMenuBackground</c>。
/// ⚠️ 也就是說，這個容器在 CS 裡叫「翻譯輔助選單」——<b>遊戲把候選字清單與翻譯輔助選單
/// 畫在同一組節點上</b>（整個元件裡再也沒有第二組候選清單節點），所以放大它會兩者一起放大。
/// </para>
/// <para>
/// 🔴 <b>不用 <c>Type == 1000 + ComponentType</c> 判元件種類。</b>那條看似成立的規則是錯的：
/// 實機 <c>AtkResNode.Type</c> 對元件節點放的是<b>該 ULD 檔自己的元件編號</b>
/// （ChatLog 的文字輸入元件是 <c>1012</c>，不是 <c>1007</c>），Artisan 已在台服離線推翻過
/// （反例 <c>1028</c>／<c>1029</c> 超出 <c>ComponentType</c> 的上限 25）。
/// 這裡改讀元件自己的 <c>UldManager.Objects</c>（<c>BaseType == Component</c> 時就是
/// <c>AtkUldComponentInfo*</c>）裡的 <c>ComponentType</c> 欄位——那是遊戲自己記著的真值。
/// </para>
/// <para>
/// 🔴 <b>不跨幀保存任何原生指標。</b>每次輪詢當場從 <c>AllLoadedUnitsList</c> 走下來、
/// 當場用完丟掉；模組不持有任何 <c>AtkUnitBase*</c>／<c>AtkResNode*</c> 欄位。
/// </para>
/// <para>
/// 📌 <b>預設 1.0 ＝行為完全不變</b>，而且模組本身預設關閉。
/// </para>
/// </remarks>
public sealed unsafe class LargerIMECandidates : TcModule
{
    public override string InternalName => "LargerIMECandidates";
    public override string DisplayName => "放大中文輸入候選字";

    public override string Description =>
        "把遊戲內建輸入法的候選字清單整塊放大，打中文時不必瞇著眼睛選字。" +
        "候選清單與文字補完／翻譯輔助選單畫在同一組節點上，所以兩者會一起放大。" +
        "預設倍率 1.0（＝完全不改），要生效請把倍率調大。";

    public override ModuleCategory Category => ModuleCategory.Misc;

    /// <summary>開著就會每隔一段時間自己去改節點縮放，不是按鈕型。</summary>
    public override bool IsManualTrigger => false;

    public override bool HasConfigUI => true;

    /// <summary>候選清單容器在文字輸入元件裡的節點 ID（離線自 ChatLog.uld 解出）。</summary>
    private const uint CandidateContainerNodeId = 4;

    /// <summary>
    /// 元件節點的 <c>Type</c> 下界。
    /// </summary>
    /// <remarks>
    /// 🔴 這條只用來判「是不是元件家族」，<b>不拿來推元件種類</b>（見類別註解）。
    /// 必須先過這一關才准把 <c>AtkResNode*</c> 當成 <c>AtkComponentNode*</c>：
    /// <c>AtkResNode</c> 宣告 <c>Size = 0xB0</c>，而 <c>AtkComponentNode.Component</c>
    /// 就在 <c>FieldOffset(0xB0)</c> ⇒ 對非元件節點讀 <c>Component</c> 是讀出界。
    /// </remarks>
    private const int ComponentNodeTypeFloor = 1000;

    /// <summary>往元件內部遞迴的最大層數（0＝addon 的根 widget）。</summary>
    /// <remarks>
    /// 📌 ChatLog 的文字輸入元件就掛在根 widget 上（深度 0 就找得到）。
    /// 給到 2 是為了兜住「輸入框包在視窗元件裡」的 addon，同時讓成本有硬上界。
    /// </remarks>
    private const int MaxDepth = 2;

    /// <summary>單次掃描最多走訪幾個節點。純防呆：資料結構若被讀成垃圾，這裡先停下來。</summary>
    private const int NodeBudget = 20_000;

    /// <summary><c>AtkUnitList.Count</c> 的硬上界（<c>_entries</c> 是 <c>FixedSizeArray256</c>）。</summary>
    private const int MaxLoadedUnits = 256;

    /// <summary><c>AtkUldManager.NodeListCount</c> 的合理上界，超過就當資料不可信。</summary>
    private const int MaxNodeListCount = 1024;

    /// <summary>
    /// 節點的髒旗標：<c>0x1</c>＝有更新要畫、<c>0x4</c>＝需要重算變換矩陣。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意<b>不呼叫</b> <c>AtkResNode.SetScale</c>：那是 <c>[MemberFunction]</c>，
    /// 要靠特徵碼解位址，台服對不上時失敗發生在原生層。改成直接寫 <c>ScaleX</c>／<c>ScaleY</c>
    /// 再補上這兩個旗標（遊戲自己的 <c>SetScale</c> 做的也是同一件事）。
    /// 假設不成立時最差是「縮放要等別的事件把節點弄髒才生效」——良性失效。
    /// </remarks>
    private const uint DirtyDrawFlags = 0x1u | 0x4u;

    /// <summary>倍率下界。低於 1 只會讓候選字更難看清，沒有使用情境。</summary>
    private const float MinScale = 1.0f;

    /// <summary>倍率上界。再大就會整塊掉出畫面外。</summary>
    private const float MaxScale = 3.0f;

    /// <summary>兩次掃描之間隔多久（毫秒）。候選清單是打字時才出現，10Hz 綽綽有餘。</summary>
    private const int SweepIntervalMs = 100;

    private LargerIMECandidatesConfig Config => Plugin.Instance.Config.LargerIMECandidates;

    /// <summary>這一輪掃描已經走訪過的節點數（防呆預算）。</summary>
    private int visitedNodes;

    /// <summary>本次啟用期間有沒有真的改到過節點。只用來決定要不要寫那行診斷。</summary>
    private bool everApplied;

    protected override void OnEnable()
    {
        everApplied = false;

        // 🔑 「回 0」比「報錯」常見。把設定值寫進 Information 級記錄（使用者跑 LogLevel 2），
        //    讓「開了卻沒反應」時第一時間分得出來是「倍率還是 1.0」還是「找不到節點」。
        Svc.Log.Information(
            $"[{InternalName}] 模組啟用：候選字倍率 {Config.Scale:0.0}" +
            $"（1.0＝不放大）；掃描間隔 {SweepIntervalMs} 毫秒。");

        Svc.Framework.Update += OnUpdate;
    }

    protected override void OnDisable()
    {
        Svc.Framework.Update -= OnUpdate;

        // 🔴 停用要把縮放還原，否則節點會維持放大到 addon 下次重建為止
        //    （ChatLog 常駐不重建 ⇒ 等於「關不掉」）。
        //    這一趟刻意不看焦點閘門：使用者按下停用的當下，焦點通常已經不在輸入框上了。
        try
        {
            Sweep(1.0f);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[{InternalName}] 停用時還原候選字縮放失敗");
        }
    }

    private void OnUpdate(IFramework framework)
    {
        if (!Throttle.Pass("LargerIMECandidates-Sweep", SweepIntervalMs)) return;

        // 🔑 焦點閘門：沒有任何文字輸入框拿著焦點時，候選清單根本不會出現，整趟掃描可以省掉。
        //    TargetTextInputEventInterface 是純欄位（AtkModule.TextInput + 0x8），不需要特徵碼。
        //    ⚠️ 這個閘門只是省成本用的。萬一台服這個欄位的語意不同（例如永遠非 null），
        //       退化的結果是「每 100 毫秒白走一趟」，不會出錯。
        var atkModule = RaptureAtkModule.Instance();
        if (atkModule == null) return;
        if (atkModule->AtkModule.TextInput.TargetTextInputEventInterface == null) return;

        Sweep(Math.Clamp(Config.Scale, MinScale, MaxScale));
    }

    /// <summary>
    /// 走一趟目前載入的所有 addon，把找得到的文字輸入元件候選清單容器設成指定倍率。
    /// </summary>
    /// <remarks>
    /// 🔴 全程同步、當幀取得當幀用完，不把任何原生指標存進欄位。
    /// </remarks>
    private void Sweep(float scale)
    {
        visitedNodes = 0;

        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null) return;

        var units = &unitManager->AtkUnitManager.AllLoadedUnitsList;
        var count = units->Count;
        if (count > MaxLoadedUnits) count = MaxLoadedUnits;

        for (var i = 0; i < count; i++)
        {
            var addon = units->Entries[i].Value;
            if (addon == null) continue;

            // 沒載完的 addon 節點清單還在變動，這一輪跳過即可（下一輪還會再來）。
            if (addon->UldManager.LoadedState != AtkLoadState.Loaded) continue;

            ApplyToUld(&addon->UldManager, scale, 0);
            if (visitedNodes >= NodeBudget) return;
        }
    }

    /// <summary>
    /// 在一個 <c>AtkUldManager</c> 的節點清單裡找文字輸入元件，找到就處理、沒找到就往元件內部遞迴。
    /// </summary>
    private void ApplyToUld(AtkUldManager* uld, float scale, int depth)
    {
        if (uld == null || depth > MaxDepth) return;

        var nodes = uld->NodeList;
        if (nodes == null) return;

        int count = uld->NodeListCount;
        if (count <= 0 || count > MaxNodeListCount) return;

        for (var i = 0; i < count; i++)
        {
            if (++visitedNodes >= NodeBudget) return;

            var node = nodes[i];
            if (node == null) continue;

            // 🔴 先過這一關才准當成 AtkComponentNode（見 ComponentNodeTypeFloor 的註解）。
            if ((int)node->Type < ComponentNodeTypeFloor) continue;

            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) continue;

            switch (GetComponentType(component))
            {
                case ComponentType.TextInput:
                    ApplyToTextInput(component, scale);
                    break;

                default:
                    // 文字輸入元件內部不會再包一個文字輸入元件，所以只有其他種類才需要往下鑽。
                    ApplyToUld(&component->UldManager, scale, depth + 1);
                    break;
            }
        }
    }

    /// <summary>
    /// 讀出一個元件的種類。
    /// </summary>
    /// <remarks>
    /// 🔴 <b>刻意不呼叫 <c>AtkComponentBase.GetComponentType()</c></b>（那是特徵碼型的
    /// <c>[MemberFunction]</c>），也<b>刻意不從節點的 <c>Type</c> 推</b>（那是 ULD 檔的元件編號，
    /// 不是 <c>ComponentType</c>）。這裡走 <c>UldManager.Objects</c>：
    /// <c>BaseType == Component</c> 時它就是 <c>AtkUldComponentInfo*</c>，
    /// 種類寫在 <c>+0x10</c>。三個欄位讀取，判不出來就回 <see cref="ComponentType.Base"/>
    /// （＝當成一般元件往下鑽），不會誤判成文字輸入。
    /// </remarks>
    private static ComponentType GetComponentType(AtkComponentBase* component)
    {
        if (component->UldManager.BaseType != AtkUldManagerBaseType.Component) return ComponentType.Base;

        var info = (AtkUldComponentInfo*)component->UldManager.Objects;
        return info == null ? ComponentType.Base : info->ComponentType;
    }

    /// <summary>
    /// 把一個文字輸入元件的候選清單容器設成指定倍率。
    /// </summary>
    /// <remarks>
    /// 🔑 <b>不讀 <c>AtkComponentTextInput.AutoTranslateMenuNode</c>（+0x3D0）。</b>
    /// 那個欄位確實就是這個節點，但只要種類判斷有一絲差錯，讀 <c>+0x3D0</c> 就是讀出界
    /// ——而一般元件的配置只有 <c>0xC0</c>。改成在元件自己的節點清單裡找 ID 4：
    /// <c>UldManager</c> 在 <c>+0x08</c>，對<b>任何</b>元件都是有效欄位，
    /// 所以就算種類判錯，最壞情況也只是「找不到符合條件的節點」。
    /// </remarks>
    private void ApplyToTextInput(AtkComponentBase* component, float scale)
    {
        var uld = &component->UldManager;
        if (uld->LoadedState != AtkLoadState.Loaded) return;

        var nodes = uld->NodeList;
        if (nodes == null) return;

        int count = uld->NodeListCount;
        if (count <= 0 || count > MaxNodeListCount) return;

        for (var i = 0; i < count; i++)
        {
            if (++visitedNodes >= NodeBudget) return;

            var node = nodes[i];
            if (node == null || node->NodeId != CandidateContainerNodeId) continue;

            // 🔴 形狀校驗：候選清單容器是一個 Res 節點，底下掛著九個候選按鈕＋頁碼＋底圖。
            //    別的元件也有 4 號節點（例如按鈕元件的碰撞節點），這兩個條件把它們排除掉。
            //    校驗沒過就什麼都不做——寧可功能失效，也不要縮放到別的東西。
            if (node->Type != NodeType.Res || node->ChildCount < 4) return;

            // 只在真的不一樣時才寫，免得每輪都把節點標成髒的。
            if (Math.Abs(node->ScaleX - scale) < 0.001f && Math.Abs(node->ScaleY - scale) < 0.001f) return;

            node->ScaleX = scale;
            node->ScaleY = scale;
            node->DrawFlags |= DirtyDrawFlags;

            if (!everApplied && scale > 1.0f)
            {
                everApplied = true;
                Svc.Log.Information(
                    $"[{InternalName}] 已套用候選字縮放 {scale:0.0}（節點 {CandidateContainerNodeId}、" +
                    $"子節點 {node->ChildCount} 個）。");
            }

            return;
        }
    }

    public override void DrawConfig()
    {
        ImGui.SetNextItemWidth(200f);
        var scale = Config.Scale;
        if (ImGui.SliderFloat("候選字放大倍率", ref scale, MinScale, MaxScale, "%.1f 倍"))
        {
            // 寫回前夾擠：slider 可以 Ctrl+點擊鍵入範圍外的值，設定檔手改也會持久生效。
            // ⚙ 這只是第二道，真正把關的是 OnUpdate 裡的 Math.Clamp。
            Config.Scale = Math.Clamp(scale, MinScale, MaxScale);
            Plugin.Instance.Config.Save();
        }

        ImGui.Spacing();
        ImGui.TextDisabled("1.0 倍＝完全不改，等於這個模組沒開。");
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "放大的是輸入框下方那塊候選清單容器（連同九個候選字、頁碼與底圖一起）。\n" +
                "遊戲把「輸入法候選字」與「文字補完／翻譯輔助選單」畫在同一組節點上，所以兩者會一起變大。\n" +
                "倍率調太大時清單可能超出畫面，往下調即可。\n" +
                "關閉模組會立刻還原成 1.0 倍。");
        }
    }
}
