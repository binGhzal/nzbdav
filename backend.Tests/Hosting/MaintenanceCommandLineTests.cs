using System.Reflection;
using backend.Tests.Database;
using NzbWebDAV.Hosting;

namespace backend.Tests.Hosting;

public sealed class MaintenanceCommandLineTests
{
    public static TheoryData<string[], string, string?, bool, bool> ValidCommands => new()
    {
        { [], "None", null, false, false },
        { ["--db-migration"], "DatabaseMigration", null, false, false },
        { ["--db-migration", "20260711000000_Target"], "DatabaseMigration", "20260711000000_Target", false, false },
        { ["--db-export-json", "relative path/snapshot.json"], "ExportJson", "relative path/snapshot.json", false, false },
        { ["--db-import-json", "./snapshot.json"], "ImportJson", "./snapshot.json", false, false },
        { ["--db-import-json", "./snapshot.json", "--replace"], "ImportJson", "./snapshot.json", true, false },
        { ["--db-export-v3", "./snapshot directory"], "ExportV3Unavailable", "./snapshot directory", false, false },
        { ["--db-import-v3", "./snapshot directory"], "ImportV3Unavailable", "./snapshot directory", false, true },
    };

    public static TheoryData<string[]> InvalidCommands => new()
    {
        { [""] },
        { [" "] },
        { ["--"] },
        { ["--unknown"] },
        { ["--DB-MIGRATION"] },
        { ["--db-migrate"] },
        { ["prefix--db-migration"] },
        { ["--db-migration", ""] },
        { ["--db-migration", " \t"] },
        { ["--db-migration", "-target"] },
        { ["--db-migration", "--target"] },
        { ["--db-migration", "target", "extra"] },
        { ["--db-migration", "--db-export-json", "out"] },
        { ["--db-export-json"] },
        { ["--db-export-json", ""] },
        { ["--db-export-json", "-out"] },
        { ["--db-export-json", "out", "extra"] },
        { ["--db-export-json", "out", "--db-import-json", "in"] },
        { ["--db-import-json"] },
        { ["--db-import-json", ""] },
        { ["--db-import-json", "-in"] },
        { ["--db-import-json", "--replace"] },
        { ["--db-import-json", "in", "--replace", "--replace"] },
        { ["--db-import-json", "in", "extra", "--replace"] },
        { ["--db-import-json", "in", "--unknown"] },
        { ["--db-export-v3"] },
        { ["--db-export-v3", "-dir"] },
        { ["--db-export-v3", "dir", "extra"] },
        { ["--db-export-v3", "dir", "--replace"] },
        { ["--DB-EXPORT-V3", "dir"] },
        { ["--db-import-v3"] },
        { ["--db-import-v3", "-dir"] },
        { ["--db-import-v3", "dir", "extra"] },
        { ["--db-import-v3", "dir", "--replace"] },
        { ["--DB-IMPORT-V3", "dir"] },
        { ["--db-import-v3x", "dir"] },
    };

    [Theory]
    [MemberData(nameof(ValidCommands))]
    internal void ParseAcceptsOnlyFrozenFormsAndPreservesArgumentText(
        string[] arguments,
        string expectedKind,
        string? expectedArgument,
        bool expectedReplace,
        bool expectedImportAuthorization)
    {
        var parsed = MaintenanceCommandLine.Parse(arguments);

        Assert.Equal(expectedKind, parsed.Kind.ToString());
        Assert.Equal(expectedArgument, parsed.Argument);
        Assert.Equal(expectedReplace, parsed.Replace);
        Assert.Equal(expectedImportAuthorization, parsed.ImportAuthorization is not null);
    }

    [Theory]
    [MemberData(nameof(InvalidCommands))]
    internal void ParseRejectsEveryNoncanonicalFormWithoutMintingAuthority(string[] arguments)
    {
        var error = Assert.Throws<ArgumentException>(() => MaintenanceCommandLine.Parse(arguments));

        Assert.Equal(MaintenanceCommandLine.InvalidArgumentsMessage, error.Message);
    }

    [Fact]
    internal void OnlyExactFutureImportV3MintsOpaqueImportAuthority()
    {
        Environment.SetEnvironmentVariable("NZBDAV_TRANSFER_V3_IMPORT_AUTHORIZATION", "true");
        try
        {
            Assert.Null(MaintenanceCommandLine.Parse([]).ImportAuthorization);
            Assert.Null(MaintenanceCommandLine.Parse(["--db-export-v3", "snapshot"]).ImportAuthorization);
            Assert.NotNull(MaintenanceCommandLine.Parse(["--db-import-v3", "snapshot"]).ImportAuthorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NZBDAV_TRANSFER_V3_IMPORT_AUTHORIZATION", null);
        }

        var constructors = typeof(TransferV3ImportAuthorization).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Single(constructors);
        Assert.True(constructors[0].IsPrivate);
        Assert.Empty(typeof(TransferV3ImportAuthorization).GetProperties(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    internal void NoProductionIoApiConsumesImportAuthority()
    {
        var authorizationType = typeof(TransferV3ImportAuthorization);
        var commandTypeName = typeof(MaintenanceCommand).FullName;
        var expectedConsumers = new[]
        {
            $"constructor:{commandTypeName}",
            $"field:{commandTypeName}.<ImportAuthorization>k__BackingField",
            $"method:{commandTypeName}.Deconstruct",
            $"method:{commandTypeName}.get_ImportAuthorization",
            $"method:{commandTypeName}.set_ImportAuthorization",
            $"property:{commandTypeName}.ImportAuthorization",
        };
        var consumers = authorizationType.Assembly.GetTypes()
            .SelectMany(type => GetAuthorizationConsumers(type, authorizationType))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedConsumers.Order(StringComparer.Ordinal), consumers);

        var repositoryRoot = SqliteContractTestSupport.RepositoryRoot;
        var parserPath = Path.Combine(repositoryRoot, "backend", "Hosting", "MaintenanceCommandLine.cs");
        var productionSources = Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, "backend"),
                "*.cs",
                SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, parserPath, StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal)
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                               StringComparison.Ordinal));
        foreach (var path in productionSources)
        {
            Assert.DoesNotContain(
                nameof(TransferV3ImportAuthorization),
                File.ReadAllText(path),
                StringComparison.Ordinal);
        }

        var parserSource = File.ReadAllText(parserPath);
        Assert.Equal(1, parserSource.Split("new TransferV3ImportAuthorization()", StringSplitOptions.None).Length - 1);
        Assert.Contains("private TransferV3ImportAuthorization()", parserSource, StringComparison.Ordinal);
        Assert.Contains("ParseExactImportV3", parserSource, StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetAuthorizationConsumers(Type type, Type authorizationType)
    {
        const BindingFlags flags = BindingFlags.Instance
                                   | BindingFlags.Static
                                   | BindingFlags.Public
                                   | BindingFlags.NonPublic;

        foreach (var method in type.GetMethods(flags))
        {
            if (UsesAuthorization(method.ReturnType, authorizationType)
                || method.GetParameters().Any(parameter =>
                    UsesAuthorization(parameter.ParameterType, authorizationType)))
                yield return $"method:{type.FullName}.{method.Name}";
        }

        foreach (var constructor in type.GetConstructors(flags))
        {
            if (constructor.GetParameters().Any(parameter =>
                    UsesAuthorization(parameter.ParameterType, authorizationType)))
                yield return $"constructor:{type.FullName}";
        }

        foreach (var field in type.GetFields(flags))
        {
            if (UsesAuthorization(field.FieldType, authorizationType))
                yield return $"field:{type.FullName}.{field.Name}";
        }

        foreach (var property in type.GetProperties(flags))
        {
            if (UsesAuthorization(property.PropertyType, authorizationType))
                yield return $"property:{type.FullName}.{property.Name}";
        }
    }

    private static bool UsesAuthorization(Type type, Type authorizationType) =>
        (type.IsByRef ? type.GetElementType() : type) == authorizationType;
}
