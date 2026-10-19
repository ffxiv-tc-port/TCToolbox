using System;

namespace TCToolbox.Core;

/// <summary>「某個外掛現在有沒有載入」，給「會不會有別人同時按同一扇窗」這類安全判斷用。</summary>
internal static class PluginPresence
{
    /// <returns><see langword="null"/>＝列舉失敗、問不到；安全判斷要把它當成「可能已載入」。</returns>
    public static bool? IsLoaded(string internalName)
    {
        try
        {
            foreach (var plugin in Svc.PluginInterface.InstalledPlugins)
            {
                if (plugin.IsLoaded && string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            if (Throttle.Pass($"PluginPresence-{internalName}", 60_000))
                Svc.Log.Information($"[PluginPresence] 列舉已安裝插件失敗（{internalName}）：{ex.Message}");

            return null;
        }
    }
}
