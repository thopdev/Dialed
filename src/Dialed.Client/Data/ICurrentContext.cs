namespace Dialed.Client.Data;

/// <summary>Who is using the app and for which household; stamped onto every record the user creates or changes.</summary>
public interface ICurrentContext
{
    string UserId { get; }

    string HouseholdId { get; }
}

/// <summary>Stand-in until sign-in (#32) and households (#70) exist: one local user and household.</summary>
public sealed class LocalContext : ICurrentContext
{
    public string UserId => "local-user";

    public string HouseholdId => "local-household";
}
