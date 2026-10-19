using System;
using System.Collections.Generic;
using Dalamud.Plugin.Services;

namespace TCToolbox.Core;

/// <summary>
/// 請 TextAdvance 在本插件的批次期間整個讓開：把本插件的 InternalName 放進它的共享資料 <c>TextAdvance.StopRequests</c>。
/// </summary>
/// <remarks>
/// 契約：只在框架執行緒呼叫。持有者以名字記帳，最後一個 <see cref="Release"/> 時才拿掉；
/// 持有期間每幀補回（TextAdvance 重新載入會清空名單），補回那一幀當成剛加鎖。幀以 <c>IFramework.LastUpdateUTC</c> 區分。
/// 共享集合只取一次並快取：Dalamud 每次成功取用都多登記一個使用者、不去重。
/// </remarks>
internal static class TextAdvanceLock
{
    private const string StopRequestsName = "TextAdvance.StopRequests";

    private static readonly HashSet<string> holders = new(StringComparer.Ordinal);

    private static HashSet<string>? shared;
    private static bool subscribed;

    /// <summary>名字最近一次被（重新）放進去的那一幀（<c>IFramework.LastUpdateUTC</c>）。</summary>
    private static DateTime addedInFrame = DateTime.MaxValue;

    private static string Key => Svc.PluginInterface.InternalName;

    /// <summary>目前有沒有任何模組持有。</summary>
    public static bool IsHeld => holders.Count > 0;

    /// <summary>登記一個持有者並立刻加鎖。冪等。</summary>
    public static void Acquire(string owner)
    {
        if (holders.Add(owner) && holders.Count == 1)
            Svc.Log.Information($"[TextAdvanceLock] 已請 TextAdvance 讓開（{owner}）。");

        if (!subscribed)
        {
            Svc.Framework.Update += OnUpdate;
            subscribed = true;
        }

        Apply();
    }

    /// <summary>登出一個持有者；最後一個離開就解鎖。冪等。</summary>
    public static void Release(string owner)
    {
        if (!holders.Remove(owner) || holders.Count > 0) return;

        Unlock();
        Svc.Log.Information($"[TextAdvanceLock] 已放開 TextAdvance（{owner}）。");
    }

    /// <summary>
    /// 這一幀本插件可以點 Talk：TextAdvance 不在，或鎖已經掛滿一整幀。
    /// </summary>
    /// <remarks>
    /// 加鎖那一幀不點：TextAdvance 同一幀稍早可能已經看過名單（那時還沒有我們）並點了 Talk。
    /// 沒有任何持有者時一律回 <see langword="false"/>（呼叫端忘了 <see cref="Acquire"/> 就不點）。
    /// </remarks>
    public static bool MayClickTalk
    {
        get
        {
            if (holders.Count == 0) return false;
            if (!TryGetShared(out var set)) return true;

            try
            {
                return set.Contains(Key) && Svc.Framework.LastUpdateUTC > addedInFrame;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>外掛卸載時呼叫：拿掉名字、退訂。</summary>
    public static void ForceTeardown()
    {
        holders.Clear();
        Unlock();
    }

    private static void OnUpdate(IFramework _)
    {
        if (holders.Count == 0)
        {
            Unlock();
            return;
        }

        Apply();
    }

    private static void Apply()
    {
        if (!TryGetShared(out var set)) return;

        try
        {
            if (set.Add(Key))
                addedInFrame = Svc.Framework.LastUpdateUTC;
        }
        catch (InvalidOperationException)
        {
            addedInFrame = DateTime.MaxValue;
        }
    }

    private static void Unlock()
    {
        if (subscribed)
        {
            Svc.Framework.Update -= OnUpdate;
            subscribed = false;
        }

        addedInFrame = DateTime.MaxValue;
        if (shared == null) return;

        try
        {
            shared.Remove(Key);
        }
        catch (InvalidOperationException ex)
        {
            Svc.Log.Warning($"[TextAdvanceLock] 從 TextAdvance 名單拿掉本插件失敗（{ex.Message}），TextAdvance 會維持暫停到它重新載入。");
        }
    }

    /// <summary>取不到＝TextAdvance 這次啟動沒載入過（它載入時就建立這份共享資料）。</summary>
    private static bool TryGetShared(out HashSet<string> set)
    {
        if (shared == null)
        {
            try
            {
                if (Svc.PluginInterface.TryGetData<HashSet<string>>(StopRequestsName, out var data))
                    shared = data;
            }
            catch (Exception ex)
            {
                if (Throttle.Pass("TextAdvanceLock-Get", 60_000))
                    Svc.Log.Information($"[TextAdvanceLock] 取不到 TextAdvance 的共享名單：{ex.Message}");
            }
        }

        set = shared!;
        return shared != null;
    }
}
