using System;

namespace CompanionBridge;

internal static class AiChatInput
{
    internal static bool TryParse(string text, out string task)
    {
        task=null;
        if(text==null||text.Length>2004||text.IndexOfAny(new[]{'\r','\n','\0'})>=0)return false;
        string input=text.Trim();
        if(input.Length==0||input.StartsWith("/",StringComparison.Ordinal))return false;
        // Ordinary local speech is the default; retain the old exact prefix as an alias.
        if(input.StartsWith("!ai",StringComparison.Ordinal)
            &&(input.Length==3||char.IsWhiteSpace(input[3])))input=input[3..].Trim();
        if(input.Length==0||input.Length>2000)return false;
        task=input;
        return true;
    }
}
