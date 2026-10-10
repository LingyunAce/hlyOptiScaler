namespace Deployer.Core.GameDiscovery;

/// <summary>Small Steam KeyValues tokenizer/parser; nesting is preserved so keys cannot leak across blocks.</summary>
internal static class KeyValuesParser
{
    private enum TokenKind { Text, Open, Close }
    private readonly record struct Token(string Value, TokenKind Kind);

    public static Dictionary<string, object> Parse(string text)
    {
        var tokens = Tokenize(text).ToArray(); var i = 0;
        var result = ParseBlock(tokens, ref i, 0, false);
        if (i != tokens.Length) throw new InvalidDataException("KeyValues 文件存在多余的闭合括号。");
        return result;
    }

    private static Dictionary<string, object> ParseBlock(Token[] tokens, ref int i, int depth, bool nested)
    {
        if (depth > 128) throw new InvalidDataException("KeyValues 嵌套层级超过限制。");
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (i < tokens.Length)
        {
            if (tokens[i].Kind == TokenKind.Close)
            {
                if (!nested) throw new InvalidDataException("KeyValues 文件存在多余的闭合括号。");
                i++; return result;
            }
            if (tokens[i].Kind != TokenKind.Text) throw new InvalidDataException("KeyValues 出现了没有 key 的对象。");
            var key = tokens[i++].Value;
            if (i >= tokens.Length || tokens[i].Kind == TokenKind.Close) throw new InvalidDataException($"KeyValues key 缺少 value：{key}");
            object value = tokens[i].Kind == TokenKind.Open ? ReadNested(tokens, ref i, depth + 1) : tokens[i++].Value;
            result[key] = value;
        }
        if (nested) throw new InvalidDataException("KeyValues 对象缺少闭合括号。");
        return result;
    }

    private static Dictionary<string, object> ReadNested(Token[] tokens, ref int i, int depth)
    { i++; return ParseBlock(tokens, ref i, depth, true); }

    private static IEnumerable<Token> Tokenize(string input)
    {
        for (var i = 0; i < input.Length;)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }
            if (input[i] == '/' && i + 1 < input.Length && input[i + 1] == '/')
            { while (i < input.Length && input[i] != '\n') i++; continue; }
            if (input[i] == '{') { i++; yield return new("{", TokenKind.Open); continue; }
            if (input[i] == '}') { i++; yield return new("}", TokenKind.Close); continue; }
            if (input[i] == '"')
            {
                i++; var value = new System.Text.StringBuilder();
                var closed = false;
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length && input[i + 1] is '"' or '\\') i++;
                    value.Append(input[i++]);
                }
                if (i < input.Length) { i++; closed = true; }
                if (!closed) throw new InvalidDataException("KeyValues 字符串未闭合。");
                yield return new(value.ToString(), TokenKind.Text); continue;
            }
            var start = i; while (i < input.Length && !char.IsWhiteSpace(input[i]) && input[i] is not ('{' or '}')) i++;
            yield return new(input[start..i], TokenKind.Text);
        }
    }

    public static IEnumerable<(string Key, Dictionary<string, object> Value)> Children(Dictionary<string, object> node, string key)
    { if (node.TryGetValue(key, out var value) && value is Dictionary<string, object> child) foreach (var pair in child) if (pair.Value is Dictionary<string, object> block) yield return (pair.Key, block); }
    public static string? String(Dictionary<string, object> node, string key) => node.TryGetValue(key, out var value) && value is string text ? text : null;
}
