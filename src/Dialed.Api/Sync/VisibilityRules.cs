using System.Text.Json.Nodes;

namespace Dialed.Api.Sync;

/// <summary>How a reader stands to the author of an item.</summary>
public enum Relationship
{
    /// <summary>The reader made the item.</summary>
    Author,

    /// <summary>Another member of the author's household.</summary>
    HouseholdMember,

    /// <summary>Someone from outside the household whose follow request the author accepted.</summary>
    AcceptedFollower,

    /// <summary>Everyone else.</summary>
    Stranger,

    /// <summary>Someone the author blocked: sees nothing, whatever else is true.</summary>
    Blocked,
}

/// <summary>Who may read an item of a given visibility (#75): Private = only the author, Household = the household, Friends = the household and accepted followers.</summary>
public static class VisibilityRules
{
    public const int Private = 0;
    public const int Household = 1;
    public const int Friends = 2;

    public static bool CanRead(int visibility, Relationship relationship) => relationship switch
    {
        Relationship.Blocked => false,
        Relationship.Author => true,
        Relationship.HouseholdMember => visibility is Household or Friends,
        Relationship.AcceptedFollower => visibility is Friends,
        _ => false,
    };

    /// <summary>The item's visibility from its JSON (a number or a name); items without one are visible to the household.</summary>
    public static int Of(JsonObject entity)
    {
        if (entity["visibility"] is not JsonValue value)
        {
            return Household;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number is >= Private and <= Friends ? number : Private; // an unknown value is treated as the safest one
        }

        return value.TryGetValue<string>(out var name)
            ? name.ToUpperInvariant() switch
            {
                "PRIVATE" => Private,
                "HOUSEHOLD" => Household,
                "FRIENDS" => Friends,
                _ => Private,
            }
            : Household;
    }

    /// <summary>The reader's relationship to the author for items read from their own household.</summary>
    public static Relationship InHousehold(JsonObject entity, string viewerId) =>
        entity["createdBy"]?.GetValue<string>() == viewerId ? Relationship.Author : Relationship.HouseholdMember;
}
