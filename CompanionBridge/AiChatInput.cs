using System;

namespace CompanionBridge;

internal static class AiChatInput
{
    internal static bool TryParse(string text, out string task)
    {
        task=null;
        if(text==null||text.Length>2004||!text.StartsWith("!ai",StringComparison.Ordinal)
            ||text.Length<5||!char.IsWhiteSpace(text[3])||text.IndexOfAny(new[]{'\r','\n','\0'})>=0)return false;
        task=text[4..].Trim();
        return task.Length>0&&task.Length<=2000;
    }
}
