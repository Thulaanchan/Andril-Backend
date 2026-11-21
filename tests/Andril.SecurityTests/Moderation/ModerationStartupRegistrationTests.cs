using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Andril.SecurityTests.Moderation;

public sealed class ModerationStartupRegistrationTests
{
    [Fact]
    public void Program_RegistersModerationAuthorizationBeforeBuild()
    {
        var program = ReadProgramSource();

        const string registration =
            "builder.Services.AddModerationReportAuthorization();";

        var matches = Regex.Matches(
            program,
            @"(?m)^[ \t]*builder\.Services\.AddModerationReportAuthorization\(\);[ \t]*\r?$");

        Assert.Single(matches);

        var registrationIndex = program.IndexOf(
            registration,
            StringComparison.Ordinal);

        var buildIndex = program.IndexOf(
            "builder.Build()",
            StringComparison.Ordinal);

        Assert.True(registrationIndex >= 0);
        Assert.True(buildIndex > registrationIndex);
    }

    private static string ReadProgramSource()
    {
        DirectoryInfo? directory =
            new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "Andril.API",
                "Program.cs");

            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "ANDRIL repository source root was not found.");
    }
}
