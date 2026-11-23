using System;

namespace TCToolbox.Core;

/// <summary>開 InventoryTools 的「貨幣兌換價值排行」視窗（<c>/atcurrency</c>）。只在框架執行緒呼叫。</summary>
/// <remarks>
/// 那個指令不吃參數、每次呼叫都是開關切換，所以預選不了貨幣；視窗清單本身預設把最滿的貨幣排最前面。
/// 「能不能開」同時看插件已載入與指令已註冊：舊版 InventoryTools 沒有這個指令。
/// 刻意不走 <see cref="ChatSender"/>：指令沒註冊時它會把整行丟進遊戲聊天框，使用者會吃到「無此指令」。
/// </remarks>
internal static class CurrencyExchangeLink
{
    private const string PluginInternalName = "InventoryTools";

    public const string Command = "/atcurrency";

    public static bool IsAvailable()
    {
        try
        {
            return PluginPresence.IsLoaded(PluginInternalName) == true && Svc.Commands.Commands.ContainsKey(Command);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[CurrencyExchangeLink] 判斷 {Command} 能不能用時失敗：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }

    /// <returns>指令真的送出且有處理常式接下。</returns>
    public static bool TryOpen()
    {
        if (!IsAvailable()) return false;

        try
        {
            return Svc.Commands.ProcessCommand(Command);
        }
        catch (Exception ex)
        {
            Svc.Log.Information($"[CurrencyExchangeLink] 送出 {Command} 失敗：{ex.GetType().Name} {ex.Message}");
            return false;
        }
    }
}
