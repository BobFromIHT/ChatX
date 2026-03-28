using System;
using System.Globalization;
using System.Text;

namespace ChatX
{
    public partial class ChatX
    {
        private static string NormalizeChannelColorToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return string.Empty;
            token = token.Trim();
            if (token.StartsWith("#", StringComparison.Ordinal) && token.Length >= 7)
                token = token[..7];
            return token.ToUpperInvariant();
        }

        private static bool TryExtractChatChannel(string message, out ChatBehaviour.ChatChannel channel)
        {
            channel = default;
            if (string.IsNullOrEmpty(message)) return false;
            const string marker = "</color>: <color=";
            int markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return false;
            int colorStart = markerIndex + marker.Length;
            if (colorStart >= message.Length) return false;
            int colorEnd = message.IndexOf('>', colorStart);
            if (colorEnd < 0) return false;
            string colorToken = message[colorStart..colorEnd];
            string normalized = NormalizeChannelColorToken(colorToken);
            if (string.IsNullOrEmpty(normalized)) return false;
            switch (normalized)
            {
                case "YELLOW":
                case "#FFFF00":
                case "#FFD700":
                    channel = ChatBehaviour.ChatChannel.GLOBAL;
                    return true;
                case "#B2EC5D":
                    channel = ChatBehaviour.ChatChannel.PARTY;
                    return true;
                case "#FF8A90":
                    channel = ChatBehaviour.ChatChannel.ZONE;
                    return true;
                default:
                    return false;
            }
        }

        private static int SkipTimestampPrefix(string msg, int startIndex = 0)
        {
            if (string.IsNullOrEmpty(msg) || startIndex >= msg.Length) return startIndex;
            if (msg[startIndex] != '[') return startIndex;
            int i = startIndex + 1;
            if (i >= msg.Length || !char.IsDigit(msg[i])) return startIndex;
            i++;
            if (i < msg.Length && char.IsDigit(msg[i])) i++;
            if (i >= msg.Length || msg[i] != ':') return startIndex;
            i++;
            if (i + 1 >= msg.Length || !char.IsDigit(msg[i]) || !char.IsDigit(msg[i + 1])) return startIndex;
            i += 2;
            if (i < msg.Length && msg[i] == ' ')
            {
                if (i + 2 < msg.Length &&
                    (msg[i + 1] == 'A' || msg[i + 1] == 'a' || msg[i + 1] == 'P' || msg[i + 1] == 'p') &&
                    (msg[i + 2] == 'M' || msg[i + 2] == 'm'))
                {
                    i += 3;
                }
                else
                {
                    return startIndex;
                }
            }
            if (i >= msg.Length || msg[i] != ']') return startIndex;
            i++;
            if (i < msg.Length && msg[i] == ' ') i++;
            return i;
        }

        // Map channel enum -> colored (G)/(P)/(Z)
        public static string BuildColoredPrefixFromEnum(ChatBehaviour.ChatChannel ch) => ch switch
        {
            ChatBehaviour.ChatChannel.GLOBAL => "<color=yellow>(G)</color>",
            ChatBehaviour.ChatChannel.PARTY => "<color=#B2EC5D>(P)</color>",
            ChatBehaviour.ChatChannel.ZONE => "<color=#FF8A90>(Z)</color>",
            _ => string.Empty
        };

        public static string BuildMessagePrefix(ChatBehaviour.ChatChannel ch)
        {
            string timestamp = null;
            if (chatTimestamp?.Value == true)
            {
                string fmt = timestampFormat.Value ? "HH:mm" : "h:mm tt";
                timestamp = $"[{DateTime.Now.ToString(fmt, CultureInfo.InvariantCulture)}]";
            }
            var channelPrefix = BuildColoredPrefixFromEnum(ch);
            if (string.IsNullOrEmpty(channelPrefix))
                channelPrefix = null;

            if (timestamp == null && channelPrefix == null) return string.Empty;
            if (timestamp != null && channelPrefix != null) return $"{timestamp} {channelPrefix} ";
            return (timestamp ?? channelPrefix) + " ";
        }

        public static string BuildOocBadge()
        {
            return "<color=yellow>[</color><color=#A7FC00>OOC</color><color=yellow>]</color>";
        }

        public static string ExtractRenderedOocBadge(ref string message)
        {
            if (!IsOocFeatureEnabled() || string.IsNullOrEmpty(message)) return string.Empty;
            const string bodyMarker = "</color>: <color=";
            int markerIndex = message.IndexOf(bodyMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return string.Empty;
            int bodyColorStart = markerIndex + bodyMarker.Length;
            int bodyOpenTagEnd = message.IndexOf('>', bodyColorStart);
            if (bodyOpenTagEnd < 0) return string.Empty;
            int bodyStart = bodyOpenTagEnd + 1;
            if (!TryFindLeadingOocToken(message, bodyStart, out int tokenStart, out int tokenLength, out int trailingSpaceStart, out int trailingSpaceLength))
                return string.Empty;
            if (trailingSpaceLength > 0)
                message = message.Remove(trailingSpaceStart, trailingSpaceLength);
            message = message.Remove(tokenStart, tokenLength);
            return BuildOocBadge();
        }

        public static string CombinePrefixSegments(string prefix, string badge)
        {
            bool hasPrefix = !string.IsNullOrWhiteSpace(prefix);
            bool hasBadge = !string.IsNullOrWhiteSpace(badge);
            if (!hasPrefix && !hasBadge) return string.Empty;
            prefix = hasPrefix ? prefix.TrimEnd() : string.Empty;
            badge = hasBadge ? badge.Trim() : string.Empty;
            if (!hasPrefix) return badge + " ";
            if (!hasBadge) return prefix.EndsWith(" ", StringComparison.Ordinal) ? prefix : prefix + " ";
            return prefix + " " + badge + " ";
        }

        // Detect either plain "(G) " / "(P) " / "(Z) " or colored "<color=...>(G|P|Z)</color> "
        public static bool LooksPrefixed(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return false;
            int i = SkipTimestampPrefix(msg);
            if (msg.Length - i >= 4
                && msg[i] == '('
                && (msg[i + 1] == 'G' || msg[i + 1] == 'P' || msg[i + 1] == 'Z')
                && msg[i + 2] == ')'
                && msg[i + 3] == ' ')
                return true;
            if (i < msg.Length && msg.IndexOf("<color", i, StringComparison.OrdinalIgnoreCase) == i)
            {
                int gt = msg.IndexOf('>', i);
                if (gt >= 0)
                {
                    int endColor = msg.IndexOf("</color>", gt + 1, StringComparison.OrdinalIgnoreCase);
                    if (endColor > gt + 1)
                    {
                        string inner = msg[(gt + 1)..endColor];
                        if (inner.StartsWith("(G)") || inner.StartsWith("(P)") || inner.StartsWith("(Z)"))
                            return true;
                    }
                }
            }
            return false;
        }

        public static string InsertPrefix(string line, string prefix)
        {
            if (string.IsNullOrEmpty(prefix) || string.IsNullOrEmpty(line)) return line;
            return prefix + line;
        }

        public static string ApplyAsteriskFormatting(string s)
        {
            if (asteriskItalic?.Value != true || string.IsNullOrEmpty(s) || s.IndexOf('*') < 0)
                return s;
            var sb = new StringBuilder(s.Length + 16);
            var span = s.AsSpan();
            bool ital = false;
            bool bold = false;
            bool inTag = false;
            int italOpenAt = -1;
            int boldOpenAt = -1;
            for (int i = 0; i < span.Length; i++)
            {
                char c = span[i];
                if (c == '<') { inTag = true; sb.Append(c); continue; }
                if (c == '>' && inTag) { inTag = false; sb.Append(c); continue; }
                if (c == '\\' && i + 1 < span.Length && span[i + 1] == '*') { sb.Append('*'); i++; continue; }
                if (c == '*' && !inTag)
                {
                    int j = i + 1;
                    while (j < span.Length && span[j] == '*') j++;
                    int run = j - i;
                    if (run >= 2)
                    {
                        int remaining = run;
                        while (remaining >= 2)
                        {
                            if (bold) { sb.Append("</b>"); bold = false; boldOpenAt = -1; }
                            else { boldOpenAt = sb.Length; sb.Append("<b>"); bold = true; }
                            remaining -= 2;
                        }
                        if (remaining == 1)
                        {
                            if (ital) { sb.Append("</i>"); ital = false; italOpenAt = -1; }
                            else { italOpenAt = sb.Length; sb.Append("<i>"); ital = true; }
                        }
                    }
                    else
                    {
                        if (ital) { sb.Append("</i>"); ital = false; italOpenAt = -1; }
                        else { italOpenAt = sb.Length; sb.Append("<i>"); ital = true; }
                    }
                    i = j - 1;
                    continue;
                }
                sb.Append(c);
            }
            if (bold && boldOpenAt >= 0)
            {
                sb.Remove(boldOpenAt, 3).Insert(boldOpenAt, "**");
                if (italOpenAt > boldOpenAt)
                    italOpenAt += 2 - 3;
            }
            if (ital && italOpenAt >= 0)
                sb.Remove(italOpenAt, 3).Insert(italOpenAt, "*");
            return sb.ToString();
        }
    }
}
