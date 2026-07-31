using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace TCToolbox.Core;

/// <summary>
/// 斜線指令執行器。Dalamud 註冊的外掛指令（/snd 之類）直接分派，
/// 其餘送進遊戲聊天框處理原生指令。只接受斜線開頭，不代發一般聊天文字。
/// 只在主執行緒使用。
/// </summary>
public static unsafe class ChatSender
{
    /// <summary>執行單行斜線指令；回傳 false 表示被拒絕或失敗。</summary>
    public static bool ExecuteCommand(string command)
    {
        command = command.Trim();
        if (!command.StartsWith('/'))
        {
            Svc.Log.Warning($"[ChatSender] 拒絕非斜線開頭的指令：{command}");
            return false;
        }

        if (command.Contains('\n') || command.Contains('\r'))
        {
            Svc.Log.Warning("[ChatSender] 拒絕含換行字元的指令");
            return false;
        }

        if (Encoding.UTF8.GetByteCount(command) > 500)
        {
            Svc.Log.Warning($"[ChatSender] 拒絕超過 500 位元組的指令：{command}");
            return false;
        }

        try
        {
            if (Svc.Commands.ProcessCommand(command))
                return true;

            var uiModule = UIModule.Instance();
            if (uiModule == null)
            {
                Svc.Log.Warning("[ChatSender] UIModule 尚未就緒，指令未送出");
                return false;
            }

            var utf8 = Utf8String.FromString(command);
            try
            {
                uiModule->ProcessChatBoxEntry(utf8);
            }
            finally
            {
                utf8->Dtor();
                IMemorySpace.Free(utf8);
            }

            return true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"[ChatSender] 指令執行失敗：{command}");
            return false;
        }
    }
}
