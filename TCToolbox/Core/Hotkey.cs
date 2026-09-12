using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;

namespace TCToolbox.Core;

/// <summary>
/// 熱鍵：偵測「主鍵＋修飾鍵在這一幀剛被按下」，以及設定畫面上的挑鍵器。
/// </summary>
/// <remarks>
/// 🔴 <c>IKeyState</c> 的索引子對<b>無效的 vkCode 會擲 <c>ArgumentException</c></b>，
/// 而它跑在 <c>Framework.Update</c> 上——擲出去就是每幀一次例外。
/// 所有讀取一律先過 <see cref="IsUsable"/>（內含 <c>IsVirtualKeyValid</c>）。
/// </remarks>
public sealed class HotkeyWatcher
{
    /// <summary>上一次輪詢時「這組熱鍵是按下的」。邊緣偵測用。</summary>
    private bool wasDown;

    /// <summary>
    /// 輪詢一次；只有在「上一幀還沒按、這一幀按下去了」時回 <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 📌 未綁定（<see cref="HotkeyConfig.KeyCode"/> 為 0）或該鍵這台機器不認得時，
    /// 會順手把邊緣狀態歸零再回 false——否則使用者在「按著鍵的當下」改綁定，
    /// 放開時會被記成一次新的按下。
    /// </remarks>
    public bool Poll(HotkeyConfig config)
    {
        if (config.KeyCode == 0 || !IsUsable(config.KeyCode))
        {
            wasDown = false;
            return false;
        }

        var down = Svc.Keys[config.KeyCode] && ModifiersMatch(config);
        var fired = down && !wasDown;
        wasDown = down;
        return fired;
    }

    /// <summary>模組停用時清掉邊緣狀態，重新啟用不會補一次假的觸發。</summary>
    public void Reset() => wasDown = false;

    private static bool ModifiersMatch(HotkeyConfig config)
        => Held(VirtualKey.CONTROL) == config.Ctrl
           && Held(VirtualKey.SHIFT) == config.Shift
           && Held(VirtualKey.MENU) == config.Alt;

    private static bool Held(VirtualKey key) => IsUsable((int)key) && Svc.Keys[key];

    private static bool IsUsable(int code)
    {
        try
        {
            return Svc.Keys.IsVirtualKeyValid(code);
        }
        catch (Exception)
        {
            // IsVirtualKeyValid 本身不該擲例外，但它是我們唯一的守衛——它掛了就當成不可用，
            // 絕對不要讓例外往上跑到 Framework.Update。
            return false;
        }
    }
}

/// <summary>熱鍵的挑鍵 UI 與說明文字（設定畫面用）。</summary>
public static class HotkeyUi
{
    /// <summary>
    /// 不列進主鍵清單的鍵。
    /// </summary>
    /// <remarks>
    /// 純修飾鍵當主鍵沒有意義：勾了 CTRL 又把主鍵設成 CTRL 時，
    /// <see cref="HotkeyWatcher"/> 的修飾鍵精確比對永遠成立不了，
    /// 表現成「這顆熱鍵怎麼按都沒反應」而且完全沒有錯誤訊息。
    /// </remarks>
    private static readonly HashSet<int> ExcludedKeys =
    [
        (int)VirtualKey.SHIFT, (int)VirtualKey.CONTROL, (int)VirtualKey.MENU,
        (int)VirtualKey.LSHIFT, (int)VirtualKey.RSHIFT,
        (int)VirtualKey.LCONTROL, (int)VirtualKey.RCONTROL,
        (int)VirtualKey.LMENU, (int)VirtualKey.RMENU,
        (int)VirtualKey.LWIN, (int)VirtualKey.RWIN,
    ];

    /// <summary>可選的主鍵清單（第一筆固定是「未綁定」）。第一次用到時才建，之後共用。</summary>
    private static (int Code, string Label)[]? keyChoices;

    private static (int Code, string Label)[] KeyChoices
    {
        get
        {
            if (keyChoices != null) return keyChoices;

            var list = new List<(int Code, string Label)> { (0, "未綁定") };
            try
            {
                // 🔑 清單來源是遊戲自己認可的按鍵（IKeyState.GetValidVirtualKeys），不是我們寫死的一張表：
                //    寫死的表遲早會列出遊戲根本不會回報狀態的鍵，那種綁定按下去毫無反應且無訊息。
                foreach (var key in Svc.Keys.GetValidVirtualKeys())
                {
                    var code = (int)key;
                    if (code == 0 || ExcludedKeys.Contains(code)) continue;
                    list.Add((code, Describe(key)));
                }
            }
            catch (Exception ex)
            {
                // 清單建不起來時只剩「未綁定」一個選項——使用者看得出來有問題，
                // 而已經綁好的熱鍵仍然可以用（Poll 不依賴這張表）。
                Svc.Log.Information($"[Hotkey] 取得可用按鍵清單失敗，挑鍵器本次只會顯示「未綁定」：{ex.Message}");
            }

            keyChoices = [.. list];
            return keyChoices;
        }
    }

    private static string Describe(VirtualKey key)
    {
        try
        {
            // GetFancyName 走的是列舉成員上的 [VirtualKey(...)] 屬性；沒有屬性的成員會 NRE。
            var name = key.GetFancyName();
            return string.IsNullOrEmpty(name) ? key.ToString() : name;
        }
        catch (Exception)
        {
            return key.ToString();
        }
    }

    /// <summary>把一組熱鍵設定畫成人看得懂的字串（未綁定時回「未綁定」）。</summary>
    public static string Describe(HotkeyConfig config)
    {
        if (config.KeyCode == 0) return "未綁定";

        var name = Describe((VirtualKey)config.KeyCode);
        var prefix = string.Empty;
        if (config.Ctrl) prefix += "CTRL＋";
        if (config.Shift) prefix += "SHIFT＋";
        if (config.Alt) prefix += "ALT＋";
        return prefix + name;
    }

    /// <summary>
    /// 畫一組熱鍵設定（修飾鍵勾選框＋主鍵下拉）。有變更時回 <see langword="true"/>，
    /// 由呼叫端負責存檔。
    /// </summary>
    /// <param name="id">同一扇視窗裡的唯一 id 片段（不會顯示給使用者）。</param>
    /// <param name="config">要編輯的設定物件。</param>
    public static bool Draw(string id, HotkeyConfig config)
    {
        var changed = false;

        var ctrl = config.Ctrl;
        if (ImGui.Checkbox($"CTRL##{id}-ctrl", ref ctrl))
        {
            config.Ctrl = ctrl;
            changed = true;
        }

        ImGui.SameLine();
        var shift = config.Shift;
        if (ImGui.Checkbox($"SHIFT##{id}-shift", ref shift))
        {
            config.Shift = shift;
            changed = true;
        }

        ImGui.SameLine();
        var alt = config.Alt;
        if (ImGui.Checkbox($"ALT##{id}-alt", ref alt))
        {
            config.Alt = alt;
            changed = true;
        }

        ImGui.SameLine();

        var choices = KeyChoices;
        var currentIndex = 0;
        for (var i = 0; i < choices.Length; i++)
        {
            if (choices[i].Code == config.KeyCode) currentIndex = i;
        }

        ImGui.SetNextItemWidth(200f);
        if (ImGui.BeginCombo($"熱鍵##{id}-key", choices[currentIndex].Label))
        {
            for (var i = 0; i < choices.Length; i++)
            {
                if (!ImGui.Selectable(choices[i].Label, i == currentIndex)) continue;
                config.KeyCode = choices[i].Code;
                changed = true;
            }

            ImGui.EndCombo();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "熱鍵預設未綁定。\n" +
                "沒有勾的修飾鍵必須是放開的：綁「X」時按 CTRL＋X 不會觸發，這是刻意的。\n" +
                "聊天框有輸入焦點時不會觸發（與遊戲自己的快捷鍵一致）。");
        }

        return changed;
    }
}
