using Dialed.Client.Data;

namespace Dialed.Client.Account;

/// <summary>The signed-in person and their household; before sign-in everything belongs to the local stand-in.</summary>
public sealed class SessionContext(AuthSession session) : ICurrentContext
{
    private readonly LocalContext local = new();

    public string UserId => session.Tokens?.UserId ?? local.UserId;

    public string HouseholdId => session.Tokens?.HouseholdId ?? local.HouseholdId;
}
