using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ttasks.ChatApp.Services;

internal static partial class OutputReferenceResolver
{
    [GeneratedRegex(@"\$\{\{\s*(?<expr>[^}]+?)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex ReferencePattern();

    public static IReadOnlyList<string> ResolveAll(IReadOnlyList<string> args, IReadOnlyDictionary<string, string> upstreamOutputs) =>
        args.Select(arg => Resolve(arg, upstreamOutputs)).ToList();

    public static string Resolve(string arg, IReadOnlyDictionary<string, string> upstreamOutputs) =>
        ReferencePattern().Replace(arg, match => ResolveExpression(match.Groups["expr"].Value.Trim(), upstreamOutputs));

    public static IReadOnlyList<string> ReferencedTaskIds(string arg)
    {
        var ids = new List<string>();
        foreach (Match m in ReferencePattern().Matches(arg))
        {
            var id = ExtractTaskId(m.Groups["expr"].Value.Trim());
            if (id is not null)
                ids.Add(id);
        }
        return ids;
    }

    public static bool ContainsReference(string arg) => ReferencePattern().IsMatch(arg);

    private static string? ExtractTaskId(string expr)
    {
        const string prefix = "tasks.";
        if (!expr.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = expr[prefix.Length..];
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest[..dot];
    }

    private static string ResolveExpression(string expr, IReadOnlyDictionary<string, string> upstreamOutputs)
    {
        const string prefix = "tasks.";
        if (!expr.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException($"Unsupported reference '${{{{ {expr} }}}}'; expected 'tasks.<id>.output[.path]'.");

        var rest = expr[prefix.Length..];
        var firstDot = rest.IndexOf('.');
        if (firstDot < 0)
            throw new InvalidOperationException($"Reference '${{{{ {expr} }}}}' must include .output.");
        var taskId = rest[..firstDot];
        var afterId = rest[(firstDot + 1)..];
        if (!afterId.StartsWith("output", StringComparison.Ordinal))
            throw new InvalidOperationException($"Reference '${{{{ {expr} }}}}' must use .output (got '{afterId}').");

        if (!upstreamOutputs.TryGetValue(taskId, out var raw))
            throw new InvalidOperationException($"Reference '${{{{ {expr} }}}}' targets unknown or non-dependency upstream task '{taskId}'.");

        var remaining = afterId.Length == "output".Length ? string.Empty : afterId["output".Length..];
        return EvaluatePath(raw, remaining);
    }

    private static string EvaluatePath(string raw, string remaining)
    {
        if (string.IsNullOrEmpty(remaining))
            return raw;

        JsonNode? current;
        try
        {
            current = JsonNode.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Upstream output is not valid JSON; cannot navigate path '{remaining}'. {ex.Message}");
        }
        if (current is null)
            throw new InvalidOperationException("Upstream output parsed as null.");

        var segments = remaining.Split("|fromjson|", StringSplitOptions.None);
        for (var i = 0; i < segments.Length; i++)
        {
            var path = segments[i].TrimStart('.');
            current = NavigatePath(current, path);
            if (i < segments.Length - 1)
            {
                var asString = current is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var str)
                    ? str
                    : throw new InvalidOperationException("|fromjson| requires the prior value to be a string.");
                current = JsonNode.Parse(asString) ?? throw new InvalidOperationException("|fromjson| target parsed as null.");
            }
        }

        return Stringify(current);
    }

    private static JsonNode? NavigatePath(JsonNode? node, string path)
    {
        if (string.IsNullOrEmpty(path))
            return node;
        foreach (var token in ParsePath(path))
        {
            if (node is null)
                return null;
            if (token.IsIndex)
            {
                if (node is not JsonArray array)
                    throw new InvalidOperationException($"Cannot index [{token.Index}] into non-array value.");
                if (token.Index < 0 || token.Index >= array.Count)
                    throw new InvalidOperationException($"Index [{token.Index}] is out of range for array of length {array.Count}.");
                node = array[token.Index];
            }
            else
            {
                if (node is not JsonObject obj)
                    throw new InvalidOperationException($"Cannot read field '{token.Field}' from non-object value.");
                node = obj[token.Field];
            }
        }
        return node;
    }

    private static IEnumerable<PathToken> ParsePath(string path)
    {
        var i = 0;
        while (i < path.Length)
        {
            if (path[i] == '[')
            {
                var close = path.IndexOf(']', i);
                if (close < 0)
                    throw new InvalidOperationException($"Malformed index in path '{path}'.");
                if (!int.TryParse(path[(i + 1)..close], out var num))
                    throw new InvalidOperationException($"Non-numeric index '{path[(i + 1)..close]}' in path '{path}'.");
                yield return PathToken.FromIndex(num);
                i = close + 1;
                if (i < path.Length && path[i] == '.')
                    i++;
            }
            else
            {
                var start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[')
                    i++;
                yield return PathToken.FromField(path[start..i]);
                if (i < path.Length && path[i] == '.')
                    i++;
            }
        }
    }

    private static string Stringify(JsonNode? node)
    {
        if (node is null)
            return string.Empty;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s))
                return s;
            return node.ToJsonString();
        }
        return node.ToJsonString();
    }

    private readonly record struct PathToken(string Field, int Index, bool IsIndex)
    {
        public static PathToken FromField(string field) => new(field, 0, false);
        public static PathToken FromIndex(int index) => new(string.Empty, index, true);
    }
}
