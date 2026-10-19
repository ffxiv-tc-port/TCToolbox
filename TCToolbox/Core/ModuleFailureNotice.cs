using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace TCToolbox.Core;

/// <summary>
/// 模組（或其中一項功能）因遊戲版本對不上而停用時，在聊天視窗補一則說明：登入後才印，每次載入至多一次。
/// </summary>
/// <remarks>
/// 只寫 log 使用者可能沒注意到，所以另外要有聊天提示。Error log 由呼叫端自己寫。
/// 任何執行緒都可以呼叫 <see cref="Queue"/>；印出一律在框架執行緒。
/// </remarks>
internal static class ModuleFailureNotice
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Announced = [];
    private static readonly List<string> Pending = [];
    private static bool subscribed;

    /// <param name="safety">停用後保證不做的事（括號裡那句）。</param>
    public static void Queue(string internalName, string displayName, string reason, string safety = "不會寫入遊戲記憶體")
        => Add(internalName,
            $"[TC Toolbox] 「{displayName}」在這個遊戲版本無法使用，已自動停用（{safety}）。" +
            $"原因：{reason}。等插件更新後再到 TC Toolbox 設定裡開啟即可。");

    /// <summary>模組照常運作、只停用其中一項功能時用；每個模組的每項功能至多一次。</summary>
    public static void QueueFeature(string internalName, string displayName, string feature, string reason, string effect)
        => Add($"{internalName}/{feature}",
            $"[TC Toolbox] 「{displayName}」的「{feature}」在這個遊戲版本無法使用，已先停用（{effect}）。" +
            $"原因：{reason}。等插件更新後會自動恢復。");

    private static void Add(string key, string message)
    {
        lock (Gate)
        {
            if (!Announced.Add(key)) return;

            Pending.Add(message);
            if (subscribed) return;

            Svc.Framework.Update += OnUpdate;
            subscribed = true;
        }
    }

    private static void OnUpdate(IFramework framework)
    {
        if (!Svc.ClientState.IsLoggedIn) return;

        string[] messages;
        lock (Gate)
        {
            messages = Pending.ToArray();
            Pending.Clear();
            Svc.Framework.Update -= OnUpdate;
            subscribed = false;
        }

        foreach (var message in messages)
            Svc.Chat.PrintError(message);
    }

    /// <summary>插件卸載時拆掉框架訂閱（模組停用路徑不一定會走到這裡）。</summary>
    public static void ForceTeardown()
    {
        lock (Gate)
        {
            if (subscribed) Svc.Framework.Update -= OnUpdate;
            subscribed = false;
            Pending.Clear();
        }
    }
}
