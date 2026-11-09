using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dialed.Api.Sync;

/// <summary>Shape checks on a pushed record before it is stored: sane id, bounded text, depth and list sizes (#92).</summary>
public static partial class SyncPayloadValidator
{
    public const int MaxTextLength = 4000;
    public const int MaxListLength = 500;
    public const int MaxDepth = 8;

    /// <summary>Errors by field (empty when the record is acceptable).</summary>
    public static Dictionary<string, string[]> Validate(string routeId, JsonObject entity)
    {
        Dictionary<string, string[]> errors = [];
        if (!IdPattern().IsMatch(routeId))
        {
            errors["id"] = ["The id must be 1-64 letters, digits or dashes."];
        }
        else if (entity["id"]?.GetValue<string>() is { } bodyId && bodyId != routeId)
        {
            errors["id"] = ["The id in the body must match the id in the address."];
        }

        Check(entity, "", 0, errors);
        return errors;
    }

    private static void Check(JsonNode? node, string path, int depth, Dictionary<string, string[]> errors)
    {
        if (errors.Count >= 5)
        {
            return;
        }

        if (depth > MaxDepth)
        {
            errors[path] = ["Nested too deeply."];
            return;
        }

        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj)
                {
                    Check(value, path.Length == 0 ? name : $"{path}.{name}", depth + 1, errors);
                }

                break;
            case JsonArray array:
                if (array.Count > MaxListLength)
                {
                    errors[path] = [$"At most {MaxListLength} items."];
                    return;
                }

                for (var i = 0; i < array.Count; i++)
                {
                    Check(array[i], $"{path}[{i}]", depth + 1, errors);
                }

                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.Length > MaxTextLength:
                errors[path] = [$"At most {MaxTextLength} characters."];
                break;
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex IdPattern();
}
