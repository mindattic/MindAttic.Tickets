using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

namespace MindAttic.Tickets.Data;

/// <summary>
/// Keeps the Microsoft Entra token of a service-principal connection (<c>Authentication=Active Directory Service
/// Principal;User Id=&lt;appId&gt;;Password=&lt;secret&gt;</c>) between processes (M.TIX-T342). SqlClient's own provider caches a
/// token in memory only, so the <c>tickets</c> CLI, a new process hundreds of times an hour, would ask Entra for one at
/// every start. This one asks through Azure.Identity with its persistent cache (a file under
/// <c>%LOCALAPPDATA%\.IdentityService</c>, protected for the signed-in user), so a start within the token's life (about an
/// hour) makes no network call for it. Nothing here runs for any other kind of connection: Windows authentication and
/// every other <c>Authentication=</c> mode are SqlClient's as they were.
/// </summary>
internal sealed class EntraTokenCache : SqlAuthenticationProvider
{
    /// <summary>Set to "off", the token is asked for at every start, as SqlClient does on its own.</summary>
    public const string EnvVar = "MINDATTIC_TICKETS_TOKEN_CACHE";

    /// <summary>The cache's name: its file is this process family's alone.</summary>
    private const string CacheName = "MindAttic.Tickets";

    private static int registered;

    /// <summary>Registers the provider, once a process, when <paramref name="connectionString"/> signs in as a service principal.</summary>
    public static void Use(string connectionString)
    {
        if (Volatile.Read(ref registered) != 0) return;
        // Asked of the text first: a localhost connection never loads the Entra libraries.
        if (connectionString.IndexOf("Authentication", StringComparison.OrdinalIgnoreCase) < 0) return;
        if (string.Equals(Environment.GetEnvironmentVariable(EnvVar), "off", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (new SqlConnectionStringBuilder(connectionString).Authentication != SqlAuthenticationMethod.ActiveDirectoryServicePrincipal) return;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException) { return; } // SqlClient says what's wrong with it at Open
        if (Interlocked.Exchange(ref registered, 1) != 0) return;
        SetProvider(SqlAuthenticationMethod.ActiveDirectoryServicePrincipal, new EntraTokenCache());
    }

    public override bool IsSupported(SqlAuthenticationMethod authenticationMethod) =>
        authenticationMethod == SqlAuthenticationMethod.ActiveDirectoryServicePrincipal;

    public override async Task<SqlAuthenticationToken> AcquireTokenAsync(SqlAuthenticationParameters parameters)
    {
        // As SqlClient's own provider reads them: the authority ends in the tenant, the scope is the resource's default.
        var slash = parameters.Authority.LastIndexOf('/');
        var tenant = parameters.Authority[(slash + 1)..];
        var host = new Uri(parameters.Authority[..(slash + 1)]);
        var scope = parameters.Resource.EndsWith("/.default", StringComparison.Ordinal) ? parameters.Resource : parameters.Resource + "/.default";
        using var cancel = new CancellationTokenSource();
        if (parameters.ConnectionTimeout > 0) cancel.CancelAfter(TimeSpan.FromSeconds(parameters.ConnectionTimeout));

        async Task<SqlAuthenticationToken> Ask(bool persistent)
        {
            var options = new ClientSecretCredentialOptions { AuthorityHost = host };
            if (persistent) options.TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName };
            var token = await new ClientSecretCredential(tenant, parameters.UserId, parameters.Password, options)
                .GetTokenAsync(new TokenRequestContext([scope]), cancel.Token).ConfigureAwait(false);
            return new SqlAuthenticationToken(token.Token, token.ExpiresOn);
        }

        try { return await Ask(persistent: true).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException and not AuthenticationFailedException)
        {
            // The cache file couldn't be read, written or protected: the token is still good from Entra itself.
            return await Ask(persistent: false).ConfigureAwait(false);
        }
    }
}
