using EvalRunner;

namespace EvalFramework.Tests;

/// <summary>
/// Command line parsing, and in particular the rejection of options a command does not know.
/// </summary>
/// <remarks>
/// Incident replay moved out of Tier 3 into its own command, and CI kept calling
/// <c>tier3 --incident PATH</c>. The unknown option was dropped without a word, so instead of
/// replaying a saved trace the job started a full Tier 3 run against the live model. A flag that
/// disappears quietly is worse than one that breaks loudly.
/// </remarks>
public sealed class CommandLineTests
{
    [Fact]
    public void AKnownOptionIsAccepted()
    {
        CommandLine cli = new(["tier3", "--run", "artifact.json"]);

        cli.Validate();

        Assert.Equal("tier3", cli.Command);
        Assert.Equal("artifact.json", cli.Option("--run"));
    }

    [Fact]
    public void TheRemovedIncidentFlagIsRejectedOnTier3()
    {
        CommandLine cli = new(["tier3", "--incident", "trace.json"]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(cli.Validate);

        Assert.Contains("--incident", error.Message, StringComparison.Ordinal);
        Assert.Contains("--run", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIncidentCommandAcceptsTheFlagThatMovedToIt()
    {
        CommandLine cli = new(["incident", "--trace", "trace.json", "--judge"]);

        cli.Validate();

        Assert.Equal("trace.json", cli.Option("--trace"));
        Assert.True(cli.HasFlag("--judge"));
    }

    [Fact]
    public void ACommandWithNoOptionsSaysSo()
    {
        CommandLine cli = new(["rules", "--verbose"]);

        Assert.Contains(
            "takes no options",
            Assert.Throws<InvalidOperationException>(cli.Validate).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryUnknownOptionIsListed()
    {
        CommandLine cli = new(["tier2", "--nope", "--also-nope"]);

        string message = Assert.Throws<InvalidOperationException>(cli.Validate).Message;

        Assert.Contains("--nope", message, StringComparison.Ordinal);
        Assert.Contains("--also-nope", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesThatLookLikeOptionsAreNotMistakenForThem()
    {
        // A query is free text and may begin with anything.
        CommandLine cli = new(["retrieve", "--query", "refund policy", "--top", "3"]);

        cli.Validate();

        Assert.Equal("refund policy", cli.Option("--query"));
        Assert.Equal(3, cli.IntOption("--top"));
    }

    [Fact]
    public void NoArgumentsShowsHelp()
    {
        CommandLine cli = new([]);

        cli.Validate();

        Assert.Equal("help", cli.Command);
    }

    [Fact]
    public void AnUnknownCommandIsLeftToTheDispatcher()
    {
        // The switch falls through to help, so parsing does not need an opinion here.
        CommandLine cli = new(["nonsense", "--whatever"]);

        cli.Validate();

        Assert.Equal("nonsense", cli.Command);
    }

    [Fact]
    public void AMissingOptionReturnsNullRatherThanThrowing()
    {
        CommandLine cli = new(["tier3"]);

        Assert.Null(cli.Option("--run"));
        Assert.False(cli.HasFlag("--judge"));
    }
}
