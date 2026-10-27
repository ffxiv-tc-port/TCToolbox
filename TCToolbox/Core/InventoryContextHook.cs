using System;
using System.Threading;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace TCToolbox.Core;

/// <summary>
/// 背包右鍵選單函式（<c>AgentInventoryContext::OpenForItemSlot</c>）的共用 hook：
/// 全外掛只掛這一次，原函式先跑，再依序通知所有訂閱者。
/// </summary>
/// <remarks>
/// 契約：訂閱者在原函式回來之後才被呼叫，執行緒＝呼叫端（遊戲主執行緒）；
/// 單一訂閱者擲例外只記 log，不影響其他訂閱者與原生呼叫端。
/// 訂閱／退訂只在框架執行緒上呼叫；最後一位退訂時 hook 一併卸載。
/// </remarks>
public static unsafe class InventoryContextHook
{
    public delegate void OpenedHandler(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

    private delegate void OpenForItemSlotDelegate(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId);

    private static readonly object Gate = new();
    private static Hook<OpenForItemSlotDelegate>? hook;

    // 寫入時整份換新，detour 讀快照，所以呼叫途中退訂不會動到正在走的陣列。
    private static OpenedHandler[] handlers = [];

    /// <summary>訂閱；第一位訂閱者會把 hook 掛上。位址解不出來時擲例外（呼叫端的模組啟用即失敗、不掛）。</summary>
    public static void Subscribe(OpenedHandler handler)
    {
        lock (Gate)
        {
            if (Array.IndexOf(handlers, handler) >= 0) return;

            if (hook == null)
            {
                var address = AgentInventoryContext.Addresses.OpenForItemSlot.Value;
                if (address == 0)
                    throw new InvalidOperationException("找不到背包右鍵選單函式的位址，無法掛接。");

                var created = Svc.Hooks.HookFromAddress<OpenForItemSlotDelegate>(address, Detour);
                created.Enable();
                hook = created;
            }

            var next = new OpenedHandler[handlers.Length + 1];
            handlers.CopyTo(next, 0);
            next[^1] = handler;
            Volatile.Write(ref handlers, next);
        }
    }

    public static void Unsubscribe(OpenedHandler handler)
    {
        lock (Gate)
        {
            var index = Array.IndexOf(handlers, handler);
            if (index < 0) return;

            var next = new OpenedHandler[handlers.Length - 1];
            for (int from = 0, to = 0; from < handlers.Length; from++)
            {
                if (from != index) next[to++] = handlers[from];
            }

            Volatile.Write(ref handlers, next);

            if (next.Length == 0)
            {
                hook?.Dispose();
                hook = null;
            }
        }
    }

    private static void Detour(
        AgentInventoryContext* agent, InventoryType inventoryType, int slot, int a4, uint addonId)
    {
        // hook 欄位會在最後一位退訂時設回 null，而 detour 可能正在執行；只用快照。
        var current = hook;
        if (current == null)
        {
            Svc.Log.Information("[InventoryContextHook] hook 已在呼叫途中被卸載，略過本次原始呼叫。");
            return;
        }

        current.OriginalDisposeSafe(agent, inventoryType, slot, a4, addonId);

        foreach (var handler in Volatile.Read(ref handlers))
        {
            try
            {
                handler(agent, inventoryType, slot, a4, addonId);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[InventoryContextHook] 訂閱者處理右鍵選單時發生例外");
            }
        }
    }
}
