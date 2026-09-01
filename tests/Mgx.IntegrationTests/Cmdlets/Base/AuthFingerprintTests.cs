using System.Management.Automation;
using Mgx.Cmdlets.Base;

namespace Mgx.IntegrationTests.Cmdlets;

/// <summary>
/// The cached Graph HttpClient is keyed on this fingerprint. Keying it on tenant id alone
/// meant that reconnecting the same tenant with a different app registration silently reused
/// the previous application's token, so every one of these cases is a stale-credential bug.
/// (Corpus: M365DSC-4426, stale credentials.)
/// </summary>
[Collection("Pipeline")]
public class AuthFingerprintTests
{
    [Fact]
    public void A_session_with_no_context_reads_as_disconnected_not_as_a_missing_module()
    {
        // Microsoft.Graph.Authentication is a soft dependency, so "absent" and "present but
        // disconnected" are different states needing different advice: naming Connect-MgGraph
        // to someone who does not have the module sends them to a cmdlet that does not exist.
        //
        // Only the disconnected half is reachable through the cmdlet in this process: the suite
        // declares a stand-in GraphSession for the resilience-injection scenarios and FindType
        // resolves a type by full name, so IsGraphAuthLoaded answers true from the type alone
        // whether or not a test has armed a session. The absent half is held by the test below,
        // which asks the choice itself rather than the state it is made from.
        using var scope = GraphSessionScope.Arm();

        using var ps = PowerShell.Create();
        ps.AddCommand("Import-Module")
          .AddParameter("Assembly", typeof(Mgx.Cmdlets.Cmdlets.InvokeMgxRequest).Assembly);
        ps.Invoke();
        ps.Commands.Clear();
        ps.AddCommand("Invoke-MgxRequest").AddParameter("Uri", "/users/u1");
        var thrown = Record.Exception(() => ps.Invoke());

        var ids = ps.Streams.Error.Select(e => e.FullyQualifiedErrorId).ToList();
        if (thrown is CmdletInvocationException invocation)
            ids.Add(invocation.ErrorRecord.FullyQualifiedErrorId);

        Assert.Contains(ids, id => id.StartsWith("NotConnected", StringComparison.Ordinal));
        Assert.DoesNotContain(ids, id => id.StartsWith("GraphAuthModuleNotLoaded", StringComparison.Ordinal));
    }

    [Fact]
    public void An_absent_module_and_a_disconnected_session_give_different_advice()
    {
        // Naming Connect-MgGraph to someone without the module is the failure this splits, so
        // the two messages have to differ in the instruction, not only in the error id.
        var disconnected = MgxCmdletBase.DescribeMissingConnection(graphAuthLoaded: true);
        var absent = MgxCmdletBase.DescribeMissingConnection(graphAuthLoaded: false);

        Assert.Equal("NotConnected", disconnected.ErrorId);
        Assert.Contains("Connect-MgGraph", disconnected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Install-PSResource", disconnected.Message, StringComparison.Ordinal);

        Assert.Equal("GraphAuthModuleNotLoaded", absent.ErrorId);
        Assert.Contains("Install-PSResource -Name Microsoft.Graph.Authentication",
            absent.Message, StringComparison.Ordinal);
    }


    /// <summary>
    /// Stands in for the SDK's AuthContext. BuildAuthFingerprint is duck-typed, so matching
    /// the member names is enough - no Graph assemblies, no live tenant.
    /// </summary>
    private sealed class FakeAuthContext
    {
        public string? TenantId { get; init; } = "11111111-1111-1111-1111-111111111111";
        public string? ClientId { get; init; } = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        public string[]? Scopes { get; init; }
        public string? AuthType { get; init; } = "AppOnly";
        public string? TokenCredentialType { get; init; } = "ClientCertificate";
        public string? Environment { get; init; } = "Global";
        public string? Account { get; init; }
        public string? AppName { get; init; } = "Contoso Export";
        public string? ManagedIdentityId { get; init; }
        public string? CertificateThumbprint { get; init; } = "0123456789ABCDEF0123456789ABCDEF01234567";
        public string? CertificateSubjectName { get; init; }
        public bool SendCertificateChain { get; init; }
        public bool WamEnabled { get; init; }
        public object? Certificate { get; init; }
    }

    private sealed class FakeCertificate
    {
        public string? Thumbprint { get; init; }
    }

    private static string Fingerprint(FakeAuthContext context, string? graphEndpoint = null) =>
        MgxCmdletBase.BuildAuthFingerprint(context, graphEndpoint);

    #region No usable context

    [Fact]
    public void Returns_empty_for_a_null_context()
    {
        Assert.Equal(string.Empty, MgxCmdletBase.BuildAuthFingerprint(null, null));
    }

    [Fact]
    public void Returns_empty_when_the_object_has_no_matching_members()
    {
        // Graceful degradation: an unrecognized object reads as "not connected"
        // rather than throwing out of the cmdlet's first call.
        Assert.Equal(string.Empty, MgxCmdletBase.BuildAuthFingerprint(new object(), null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_empty_without_a_tenant_id(string? tenantId)
    {
        // Drives the NotConnected terminating error in GetClient().
        Assert.Equal(string.Empty, Fingerprint(new FakeAuthContext { TenantId = tenantId }));
    }

    #endregion

    #region Identity changes that must invalidate the cached client

    [Fact]
    public void Differs_when_the_client_id_changes_on_the_same_tenant()
    {
        // The reported bug: same tenant, second Connect-MgGraph with an app that holds the
        // required permissions, requests kept failing with 403 from the first app's token.
        var appA = new FakeAuthContext { ClientId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" };
        var appB = new FakeAuthContext { ClientId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" };

        Assert.NotEqual(Fingerprint(appA), Fingerprint(appB));
    }

    [Fact]
    public void Differs_when_the_certificate_thumbprint_changes()
    {
        var first = new FakeAuthContext { CertificateThumbprint = "AAAA" };
        var second = new FakeAuthContext { CertificateThumbprint = "BBBB" };

        Assert.NotEqual(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void Differs_when_only_the_certificate_object_thumbprint_differs()
    {
        // Connect-MgGraph -Certificate passes the certificate itself, leaving
        // CertificateThumbprint unset.
        var first = new FakeAuthContext
        {
            CertificateThumbprint = null,
            Certificate = new FakeCertificate { Thumbprint = "AAAA" }
        };
        var second = new FakeAuthContext
        {
            CertificateThumbprint = null,
            Certificate = new FakeCertificate { Thumbprint = "BBBB" }
        };

        Assert.NotEqual(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void Differs_when_switching_between_delegated_and_app_only()
    {
        var delegated = new FakeAuthContext { AuthType = "Delegated", Account = "admin@contoso.com" };
        var appOnly = new FakeAuthContext { AuthType = "AppOnly", Account = null };

        Assert.NotEqual(Fingerprint(delegated), Fingerprint(appOnly));
    }

    [Fact]
    public void Differs_when_the_signed_in_account_changes()
    {
        var first = new FakeAuthContext { AuthType = "Delegated", Account = "ann@contoso.com" };
        var second = new FakeAuthContext { AuthType = "Delegated", Account = "bob@contoso.com" };

        Assert.NotEqual(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void Differs_when_the_credential_type_or_managed_identity_changes()
    {
        var certificate = new FakeAuthContext { TokenCredentialType = "ClientCertificate" };
        var managedIdentity = new FakeAuthContext
        {
            TokenCredentialType = "ManagedIdentity",
            ManagedIdentityId = "system"
        };

        Assert.NotEqual(Fingerprint(certificate), Fingerprint(managedIdentity));
    }

    [Fact]
    public void Differs_when_the_graph_endpoint_changes()
    {
        // Sovereign clouds must never share a cached client with the commercial cloud.
        var context = new FakeAuthContext();

        Assert.NotEqual(
            Fingerprint(context, "https://graph.microsoft.com"),
            Fingerprint(context, "https://graph.microsoft.us"));
    }

    [Fact]
    public void Differs_when_a_scope_is_added()
    {
        var narrow = new FakeAuthContext { Scopes = ["User.Read.All"] };
        var wide = new FakeAuthContext { Scopes = ["User.Read.All", "Group.Read.All"] };

        Assert.NotEqual(Fingerprint(narrow), Fingerprint(wide));
    }

    #endregion

    #region Stability - changes that must NOT rebuild the client

    [Fact]
    public void Is_stable_for_two_equal_contexts()
    {
        Assert.Equal(Fingerprint(new FakeAuthContext()), Fingerprint(new FakeAuthContext()));
    }

    [Fact]
    public void Is_stable_across_scope_reordering()
    {
        // Graph does not care about scope order, so neither should the cache key.
        var first = new FakeAuthContext { Scopes = ["User.Read.All", "Group.Read.All"] };
        var second = new FakeAuthContext { Scopes = ["Group.Read.All", "User.Read.All"] };

        Assert.Equal(Fingerprint(first), Fingerprint(second));
    }

    #endregion

    #region Encoding

    [Fact]
    public void Cannot_be_forged_by_shifting_a_value_across_the_field_boundary()
    {
        // Length-prefixed fields: without them these two would concatenate identically.
        var first = new FakeAuthContext { TenantId = "ab", ClientId = "c" };
        var second = new FakeAuthContext { TenantId = "a", ClientId = "bc" };

        Assert.NotEqual(Fingerprint(first), Fingerprint(second));
    }

    [Fact]
    public void Does_not_leak_the_account_or_thumbprint_verbatim()
    {
        // A prefix of the fingerprint is written to the verbose stream.
        var context = new FakeAuthContext
        {
            Account = "admin@contoso.com",
            CertificateThumbprint = "0123456789ABCDEF0123456789ABCDEF01234567"
        };

        var fingerprint = Fingerprint(context);

        Assert.DoesNotContain("admin@contoso.com", fingerprint, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0123456789ABCDEF", fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Source equivalence

    [Fact]
    public void A_PSObject_source_produces_the_same_fingerprint_as_the_underlying_object()
    {
        // GetCurrentAuthIdentity reads GraphSession directly, and falls back to Get-MgContext
        // when that reflection breaks. The two paths must agree, or the fallback would look
        // like an identity change and rebuild the client on every request.
        var context = new FakeAuthContext { Scopes = ["User.Read.All"] };

        Assert.Equal(
            MgxCmdletBase.BuildAuthFingerprint(context, null),
            MgxCmdletBase.BuildAuthFingerprint(new PSObject(context), null));
    }

    [Fact]
    public void Reads_members_off_a_PSObject_and_a_plain_object_alike()
    {
        var context = new FakeAuthContext { ClientId = "cafe" };

        Assert.Equal("cafe", MgxCmdletBase.ReadAuthMember(context, "ClientId"));
        Assert.Equal("cafe", MgxCmdletBase.ReadAuthMember(new PSObject(context), "ClientId"));
        Assert.Null(MgxCmdletBase.ReadAuthMember(context, "NoSuchMember"));
        Assert.Null(MgxCmdletBase.ReadAuthMember(null, "ClientId"));
    }

    #endregion
}
