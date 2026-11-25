using VRPC.Install;

namespace VRPC.Tests.Installer;

// AU-07: a console that cannot be answered (redirected stdin in the updater
// handoff) must never turn EOF into consent.
public class ConsolePresenterTests
{
    private static readonly ConfirmPrompt Prompt = new()
    {
        Title = "Update VRPC",
        Text = "A prompt that needs a person.",
        PrimaryLabel = "Update",
        SecondaryLabel = "Uninstall",
        AffirmativeByDefault = true
    };

    [Fact]
    public async Task Non_interactive_consent_throws_instead_of_affirming()
    {
        ConsolePresenter presenter = new(quiet: false, yes: false, interactive: false);

        PresenterUnavailableException error = await Assert.ThrowsAsync<PresenterUnavailableException>(
            () => presenter.ConfirmAsync(Prompt));

        Assert.Contains("interactive console", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Non_interactive_force_close_throws_instead_of_affirming()
    {
        ConsolePresenter presenter = new(quiet: false, yes: false, interactive: false);

        await Assert.ThrowsAsync<PresenterUnavailableException>(
            () => presenter.ConfirmForceCloseAsync(Prompt));
    }

    [Fact]
    public async Task Explicit_unattended_consent_still_works_without_a_console()
    {
        ConsolePresenter yes = new(quiet: false, yes: true, interactive: false);
        Assert.Equal(InstallDecision.Proceed, await yes.ConfirmAsync(Prompt));
        Assert.True(await yes.ConfirmForceCloseAsync(Prompt));

        ConsolePresenter quiet = new(quiet: true, yes: false, interactive: false);
        Assert.Equal(InstallDecision.Proceed, await quiet.ConfirmAsync(Prompt));
    }
}
