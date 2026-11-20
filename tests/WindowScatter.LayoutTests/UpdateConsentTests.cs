using WindowScatter;
using Xunit;

public sealed class UpdateConsentTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InstallationRequiresExplicitConsentAfterResolvingUnsavedPreferences(bool accepted)
    {
        var calls = new List<string>();
        bool result = UpdateConsent.Confirm("4.2.19", CancellationToken.None,
            () => { calls.Add("preferences"); return true; },
            message =>
            {
                calls.Add("consent");
                Assert.Contains("4.2.19", message);
                return accepted;
            });
        Assert.Equal(accepted, result);
        Assert.Equal(new[] { "preferences", "consent" }, calls);
    }

    [Fact]
    public void UnresolvedPreferencesPreventInstallationConsent()
    {
        Assert.False(UpdateConsent.Confirm("4.2.19", CancellationToken.None, () => false,
            _ => throw new Exception("Consent must not be requested after cancelling preferences.")));
    }

    [Fact]
    public void CancellationBeforeOrDuringConsentPreventsInstallation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(UpdateConsent.Confirm("4.2.19", cancelled.Token,
            () => throw new Exception("Preferences must not be requested for a cancelled operation."),
            _ => throw new Exception("Consent must not be requested for a cancelled operation.")));
        using var during = new CancellationTokenSource();
        Assert.False(UpdateConsent.Confirm("4.2.19", during.Token, () => true,
            _ => { during.Cancel(); return true; }));
    }

    [Fact]
    public void CancellationWhileResolvingPreferencesDoesNotPromptForConsent()
    {
        using var cancellation = new CancellationTokenSource();
        Assert.False(UpdateConsent.Confirm("4.2.19", cancellation.Token,
            () => { cancellation.Cancel(); return true; },
            _ => throw new Exception("Consent must not be requested after cancellation.")));
    }
}
