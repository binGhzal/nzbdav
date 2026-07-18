using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Reflection.Emit;
using backend.Tests.Database;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Npgsql;

namespace backend.Tests.Database.Transfer.Phase4;

[Collection(nameof(SqliteMigrationContractEnvironmentCollection))]
public sealed class TransferV3PostgreSqlDiagnosticsTests
{
    private const string DescriptorSourcePath =
        "backend/Database/Transfer/Phase4/TransferV3PostgreSqlTargetDescriptor.cs";

    [Fact]
    public void CreatePathBuildsOnePrivateDiagnosticsDisabledDataSourceWithoutOpening()
    {
        var root = ParseDescriptorSource();
        var descriptor = Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText
                == "TransferV3PostgreSqlTargetDescriptor");
        var create = Assert.Single(
            descriptor.Members.OfType<MethodDeclarationSyntax>(),
            method => method.Identifier.ValueText == "Create"
                      && method.Modifiers.Any(SyntaxKind.StaticKeyword));
        var createNodes = create.DescendantNodes().ToArray();
        var reachableNodes = ReachableSameClassMethods(descriptor, create)
            .SelectMany(method => method.DescendantNodesAndSelf())
            .ToArray();

        var builderCreation = Assert.Single(
            reachableNodes.OfType<ObjectCreationExpressionSyntax>(),
            creation => IsType(creation.Type, "NpgsqlDataSourceBuilder"));
        Assert.Same(
            create,
            builderCreation.FirstAncestorOrSelf<MethodDeclarationSyntax>());
        var metadataGate = AssertSingleInvocation(
            createNodes,
            "ValidateRuntimeAssemblyIdentity");
        Assert.True(metadataGate.SpanStart < builderCreation.SpanStart);
        var builderVariable = Assert.IsType<VariableDeclaratorSyntax>(
            builderCreation.FirstAncestorOrSelf<VariableDeclaratorSyntax>());
        var builderName = builderVariable.Identifier.ValueText;

        var build = AssertSingleInvocation(reachableNodes, "Build");
        Assert.Equal(builderName, InvocationReceiver(build));
        var nameAssignment = Assert.Single(
            reachableNodes.OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left is MemberAccessExpressionSyntax
            {
                Expression: IdentifierNameSyntax receiver,
                Name.Identifier.ValueText: "Name"
            } && receiver.Identifier.ValueText == builderName);
        Assert.Equal("ApplicationName", Assert.IsType<IdentifierNameSyntax>(
            nameAssignment.Right).Identifier.ValueText);

        var logger = AssertSingleInvocation(reachableNodes, "UseLoggerFactory");
        Assert.Equal(builderName, InvocationReceiver(logger));
        Assert.Equal(
            "NullLoggerFactory.Instance",
            Assert.Single(logger.ArgumentList.Arguments).Expression.ToString());

        var parameterLogging = AssertSingleInvocation(
            reachableNodes,
            "EnableParameterLogging");
        Assert.Equal(builderName, InvocationReceiver(parameterLogging));
        AssertFalseLiteral(Assert.Single(parameterLogging.ArgumentList.Arguments).Expression);

        var configureTracing = AssertSingleInvocation(reachableNodes, "ConfigureTracing");
        Assert.Equal(builderName, InvocationReceiver(configureTracing));
        var tracingLambda = Assert.IsAssignableFrom<LambdaExpressionSyntax>(
            Assert.Single(configureTracing.ArgumentList.Arguments).Expression);
        var tracingNodes = tracingLambda.DescendantNodes().ToArray();

        AssertFalseFilter(reachableNodes, tracingNodes, "ConfigureCommandFilter");
        AssertFalseFilter(reachableNodes, tracingNodes, "ConfigureBatchFilter");
        AssertFalseFilter(reachableNodes, tracingNodes, "ConfigureCopyOperationFilter");
        var physicalOpen = AssertSingleInvocation(
            tracingNodes,
            "EnablePhysicalOpenTracing");
        Assert.Equal(
            physicalOpen.Span,
            AssertSingleInvocation(reachableNodes, "EnablePhysicalOpenTracing").Span);
        AssertFalseLiteral(Assert.Single(physicalOpen.ArgumentList.Arguments).Expression);

        Assert.DoesNotContain(
            tracingNodes.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationReceiver(invocation) == builderName);
        Assert.DoesNotContain(
            reachableNodes.OfType<ObjectCreationExpressionSyntax>(),
            creation => IsType(creation.Type, "NpgsqlConnection"));
        Assert.DoesNotContain(
            reachableNodes.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) is
                "CreateConnection" or "Open" or "OpenAsync");
        Assert.DoesNotContain(
            reachableNodes.OfType<InvocationExpressionSyntax>(),
            invocation => InvocationName(invocation) == "Create"
                          && InvocationReceiver(invocation) == "NpgsqlDataSource");

        var normalizedCreation = Assert.Single(
            createNodes.OfType<ObjectCreationExpressionSyntax>(),
            creation => IsType(creation.Type, "NpgsqlConnectionStringBuilder")
                        && creation.Initializer?.Expressions
                            .OfType<AssignmentExpressionSyntax>()
                            .Any(assignment => assignment.Left is IdentifierNameSyntax
                            {
                                Identifier.ValueText: "Options"
                            }) == true);
        var normalizedVariable = Assert.IsType<VariableDeclaratorSyntax>(
            normalizedCreation.FirstAncestorOrSelf<VariableDeclaratorSyntax>());
        Assert.Equal(
            normalizedVariable.Identifier.ValueText + ".ConnectionString",
            Assert.Single(Assert.IsAssignableFrom<ArgumentListSyntax>(
                    builderCreation.ArgumentList).Arguments)
                .Expression.ToString());
        var emptyOptionsAssignment = Assert.Single(
            Assert.IsAssignableFrom<InitializerExpressionSyntax>(
                    normalizedCreation.Initializer)
                .Expressions
                .OfType<AssignmentExpressionSyntax>(),
            assignment => assignment.Left is IdentifierNameSyntax
            {
                Identifier.ValueText: "Options"
            });
        Assert.Equal("string.Empty", emptyOptionsAssignment.Right.ToString());
    }

    [Fact]
    public void DescriptorRetainsOnlyPrivateDataSourceAndSafeFacts()
    {
        var root = ParseDescriptorSource();
        var descriptor = Assert.Single(
            root.DescendantNodes().OfType<ClassDeclarationSyntax>(),
            declaration => declaration.Identifier.ValueText
                == "TransferV3PostgreSqlTargetDescriptor");
        var instanceFields = descriptor.Members
            .OfType<FieldDeclarationSyntax>()
            .Where(field => !field.Modifiers.Any(SyntaxKind.StaticKeyword)
                            && !field.Modifiers.Any(SyntaxKind.ConstKeyword))
            .ToArray();

        var dataSourceField = Assert.Single(
            instanceFields,
            field => IsType(field.Declaration.Type, "NpgsqlDataSource"));
        Assert.True(dataSourceField.Modifiers.Any(SyntaxKind.PrivateKeyword));
        Assert.DoesNotContain(
            instanceFields,
            field => IsType(field.Declaration.Type, "string")
                     || IsType(field.Declaration.Type, "NpgsqlConnectionStringBuilder")
                     || IsType(field.Declaration.Type, "NpgsqlConnection"));
        Assert.DoesNotContain(
            descriptor.Members.OfType<FieldDeclarationSyntax>(),
            field => field.Modifiers.Any(SyntaxKind.StaticKeyword)
                     && IsType(field.Declaration.Type, "NpgsqlDataSource"));

        var safeStringProperties = descriptor.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(property => IsType(property.Type, "string"))
            .ToArray();
        Assert.Equal(
            ["TargetSchema", "TimeZoneId"],
            safeStringProperties.Select(property => property.Identifier.ValueText)
                .Order(StringComparer.Ordinal));
        Assert.All(
            safeStringProperties,
            property => Assert.False(property.Modifiers.Any(SyntaxKind.PublicKeyword)));
        Assert.DoesNotContain(
            descriptor.Members.OfType<PropertyDeclarationSyntax>(),
            property => property.Identifier.ValueText.Contains(
                "ConnectionString",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReviewedProviderSourceExposesIndependentSqlEventSourceAndNoMetricsDisableSwitch()
    {
        var assembly = typeof(NpgsqlDataSourceBuilder).Assembly;
        Assert.Equal(
            "10.0.3+d3768398c17877b3a916c3c4d87e8e11698991fc",
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion);

        var sqlEventSource = Assert.IsAssignableFrom<Type>(
            assembly.GetType("Npgsql.NpgsqlSqlEventSource", throwOnError: true));
        Assert.True(typeof(EventSource).IsAssignableFrom(sqlEventSource));
        var eventSourceName = Assert.IsAssignableFrom<FieldInfo>(
            sqlEventSource.GetField(
                "EventSourceName",
                BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Equal("Npgsql.Sql", eventSourceName.GetRawConstantValue());
        var sqlCommandStart = Assert.IsAssignableFrom<MethodInfo>(
            sqlEventSource.GetMethod(
                "CommandStart",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [typeof(string)],
                modifiers: null));
        Assert.Equal("sql", Assert.Single(sqlCommandStart.GetParameters()).Name);
        var eventAttribute = Assert.Single(
            sqlCommandStart.GetCustomAttributes<EventAttribute>());
        Assert.Equal(3, eventAttribute.EventId);

        var primaryEventSource = Assert.IsAssignableFrom<Type>(
            assembly.GetType("Npgsql.NpgsqlEventSource", throwOnError: true));
        var providerCommandStart = Assert.IsAssignableFrom<MethodInfo>(
            primaryEventSource.GetMethod(
                "CommandStart",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string)],
                modifiers: null));
        Assert.Contains(
            CalledMethods(providerCommandStart),
            called => called.DeclaringType == sqlEventSource
                      && called.Name == "CommandStart");

        Assert.DoesNotContain(
            typeof(NpgsqlDataSourceBuilder).GetMembers(
                BindingFlags.Instance | BindingFlags.Public),
            member => member.Name.Contains("EventSource", StringComparison.Ordinal)
                      || member.Name.Contains("EventListener", StringComparison.Ordinal)
                      || member.Name.Contains("Metric", StringComparison.Ordinal));
        Assert.DoesNotContain(
            typeof(NpgsqlMetricsOptions).GetMembers(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            member => member.MemberType is not MemberTypes.Constructor);

        var metricsReporter = Assert.IsAssignableFrom<Type>(
            assembly.GetType("Npgsql.MetricsReporter", throwOnError: true));
        var providerMeter = Assert.IsAssignableFrom<FieldInfo>(
            metricsReporter.GetField(
                "Meter",
                BindingFlags.Static | BindingFlags.NonPublic));
        Assert.Equal(typeof(Meter), providerMeter.FieldType);
    }

    private static CompilationUnitSyntax ParseDescriptorSource()
    {
        var source = File.ReadAllText(RepositoryPath(DescriptorSourcePath));
        var tree = CSharpSyntaxTree.ParseText(source, path: DescriptorSourcePath);
        var errors = tree.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();
        Assert.True(
            errors.Length == 0,
            string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        return Assert.IsType<CompilationUnitSyntax>(tree.GetRoot());
    }

    private static IReadOnlyList<MethodDeclarationSyntax> ReachableSameClassMethods(
        ClassDeclarationSyntax descriptor,
        MethodDeclarationSyntax entryPoint)
    {
        var methods = descriptor.Members
            .OfType<MethodDeclarationSyntax>()
            .ToArray();
        var methodsByName = methods
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var reachable = new HashSet<MethodDeclarationSyntax>();
        var pending = new Queue<MethodDeclarationSyntax>();
        pending.Enqueue(entryPoint);

        while (pending.TryDequeue(out var method))
        {
            if (!reachable.Add(method))
                continue;

            foreach (var invocation in method.DescendantNodes()
                         .OfType<InvocationExpressionSyntax>())
            {
                var sameClassName = invocation.Expression switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    MemberAccessExpressionSyntax
                    {
                        Expression: IdentifierNameSyntax receiver
                    } member when receiver.Identifier.ValueText is
                        "this" or "TransferV3PostgreSqlTargetDescriptor" =>
                        member.Name.Identifier.ValueText,
                    _ => null,
                };
                if (sameClassName is null
                    || !methodsByName.TryGetValue(sameClassName, out var callees))
                {
                    continue;
                }

                foreach (var callee in callees)
                    pending.Enqueue(callee);
            }
        }

        return reachable.ToArray();
    }

    private static InvocationExpressionSyntax AssertSingleInvocation(
        IEnumerable<SyntaxNode> nodes,
        string name) => Assert.Single(
        nodes.OfType<InvocationExpressionSyntax>(),
        invocation => InvocationName(invocation) == name);

    private static void AssertFalseFilter(
        IEnumerable<SyntaxNode> createNodes,
        IEnumerable<SyntaxNode> tracingNodes,
        string name)
    {
        var invocation = AssertSingleInvocation(tracingNodes, name);
        Assert.Equal(invocation.Span, AssertSingleInvocation(createNodes, name).Span);
        var filter = Assert.IsAssignableFrom<LambdaExpressionSyntax>(
            Assert.Single(invocation.ArgumentList.Arguments).Expression);
        switch (filter.Body)
        {
            case ExpressionSyntax expression:
                AssertFalseLiteral(expression);
                break;
            case BlockSyntax block:
                var returnStatement = Assert.Single(
                    block.Statements.OfType<ReturnStatementSyntax>());
                AssertFalseLiteral(Assert.IsAssignableFrom<ExpressionSyntax>(
                    returnStatement.Expression));
                break;
            default:
                Assert.Fail($"{name} must use a filter that returns the false literal.");
                break;
        }
    }

    private static void AssertFalseLiteral(ExpressionSyntax expression) =>
        Assert.True(
            expression.IsKind(SyntaxKind.FalseLiteralExpression),
            $"Expected the false literal, found '{expression.Kind()}'.");

    private static string? InvocationName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
            _ => null
        };

    private static string? InvocationReceiver(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax member
            ? member.Expression.ToString()
            : null;

    private static bool IsType(TypeSyntax syntax, string expected) =>
        syntax switch
        {
            NullableTypeSyntax nullable => IsType(nullable.ElementType, expected),
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText == expected,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText == expected,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText == expected,
            _ => syntax.ToString().TrimEnd('?').EndsWith(
                expected,
                StringComparison.Ordinal)
        };

    private static IReadOnlyList<MethodBase> CalledMethods(MethodInfo method)
    {
        var body = Assert.IsAssignableFrom<MethodBody>(method.GetMethodBody());
        var il = Assert.IsAssignableFrom<byte[]>(body.GetILAsByteArray());
        var called = new List<MethodBase>();
        var index = 0;

        while (index < il.Length)
        {
            var first = il[index++];
            var code = first == 0xfe
                ? MultiByteOpCodes[il[index++]]
                : SingleByteOpCodes[first];
            if (code.OperandType == OperandType.InlineMethod)
            {
                var token = BitConverter.ToInt32(il, index);
                called.Add(method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method.GetGenericArguments())!);
            }

            index += OperandSize(code.OperandType, il, index);
        }

        return called;
    }

    private static int OperandSize(OperandType operandType, byte[] il, int index) =>
        operandType switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI or OperandType.InlineBrTarget
                or OperandType.InlineField or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString
                or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, index),
            _ => throw new InvalidOperationException(
                $"Unreviewed IL operand type '{operandType}'.")
        };

    private static readonly OpCode[] SingleByteOpCodes = BuildSingleByteOpCodes();
    private static readonly OpCode[] MultiByteOpCodes = BuildMultiByteOpCodes();

    private static OpCode[] BuildSingleByteOpCodes() => BuildOpCodes(multiByte: false);

    private static OpCode[] BuildMultiByteOpCodes() => BuildOpCodes(multiByte: true);

    private static OpCode[] BuildOpCodes(bool multiByte)
    {
        var result = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(
                     BindingFlags.Static | BindingFlags.Public))
        {
            if (field.GetValue(null) is not OpCode code) continue;
            var value = unchecked((ushort)code.Value);
            if (multiByte != ((value & 0xff00) == 0xfe00)) continue;
            result[value & 0xff] = code;
        }

        return result;
    }

    private static string RepositoryPath(string relativePath)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(relativePath);
    }
}
