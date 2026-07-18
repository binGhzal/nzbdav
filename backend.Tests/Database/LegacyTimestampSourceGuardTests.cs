using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace backend.Tests.Database;

public sealed class LegacyTimestampSourceGuardTests
{
    [Fact]
    public void SyntheticLegacyAssignmentsDetectNowAndUtcNowButAllowDateTimeOffsetUtcNow()
    {
        const string source = """
            using System;

            sealed class QueueItem
            {
                public DateTime CreatedAt { get; set; }
                public DateTime? PauseUntil { get; set; }
            }

            sealed class ModernItem
            {
                public DateTimeOffset CreatedAt { get; set; }
                public DateTimeOffset UpdatedAt { get; set; }
            }

            static class Writes
            {
                static void Write(QueueItem queueItem)
                {
                    var queued = new QueueItem
                    {
                        CreatedAt = DateTime.Now,
                        PauseUntil = System.DateTime.UtcNow.AddSeconds(3)
                    };
                    queueItem.PauseUntil = DateTime.Now.AddMinutes(1);
                    var valid = new ModernItem
                    {
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                }
            }
            """;

        var violations = LegacyTimestampWriteGuard.FindViolations(source, "Synthetic.cs");

        Assert.Collection(
            violations.OrderBy(x => x.Line),
            x =>
            {
                Assert.Equal("CreatedAt", x.PropertyName);
                Assert.Equal("Now", x.ClockMember);
            },
            x =>
            {
                Assert.Equal("PauseUntil", x.PropertyName);
                Assert.Equal("UtcNow", x.ClockMember);
            },
            x =>
            {
                Assert.Equal("PauseUntil", x.PropertyName);
                Assert.Equal("Now", x.ClockMember);
            });
    }

    [Fact]
    public void SyntheticNonSimpleAssignmentsAndDeclarationInitializersAreDetected()
    {
        const string source = """
            using System;

            sealed class QueueItem
            {
                public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
                public DateTime? PauseUntil { get; set; } = System.DateTime.Now.AddMinutes(1);
            }

            sealed class ValidOffsets
            {
                public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
                public DateTimeOffset? PauseUntil { get; set; } = DateTimeOffset.UtcNow;
            }

            sealed class DfsDavNode
            {
                public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
            }

            sealed class HistoryItem
            {
                public DateTime CreatedAt = DateTime.Now;
            }

            sealed class DavItem
            {
                public DateTime CreatedAt = global::System.DateTime.UtcNow;
            }

            sealed class Writes
            {
                void Write(QueueItem queueItem)
                {
                    queueItem.PauseUntil ??= System.DateTime.UtcNow.AddMinutes(1);
                    queueItem.CreatedAt += DateTime.Now.TimeOfDay;
                }
            }
            """;

        var violations = LegacyTimestampWriteGuard.FindViolations(source, "SyntheticInitializers.cs")
            .OrderBy(x => x.Line)
            .ToArray();

        Assert.Equal(6, violations.Length);
        Assert.Equal(
            [
                "CreatedAt:UtcNow",
                "PauseUntil:Now",
                "CreatedAt:Now",
                "CreatedAt:UtcNow",
                "PauseUntil:UtcNow",
                "CreatedAt:Now"
            ],
            violations.Select(x => $"{x.PropertyName}:{x.ClockMember}"));
    }

    [Fact]
    public void SyntheticVarAndTargetTypedReceiversResolveExactModelType()
    {
        const string source = """
            using System;

            sealed class QueueItem
            {
                public DateTime? PauseUntil { get; set; }
            }

            static class Writes
            {
                static void Write()
                {
                    var queueItem = new QueueItem();
                    queueItem.PauseUntil = DateTime.UtcNow;
                    QueueItem targetTyped = new();
                    targetTyped.PauseUntil = DateTime.Now;
                }
            }
            """;

        Assert.Collection(
            LegacyTimestampWriteGuard.FindViolations(source, "SyntheticInferredReceivers.cs")
                .OrderBy(x => x.Line),
            x =>
            {
                Assert.Equal("PauseUntil", x.PropertyName);
                Assert.Equal("UtcNow", x.ClockMember);
            },
            x =>
            {
                Assert.Equal("PauseUntil", x.PropertyName);
                Assert.Equal("Now", x.ClockMember);
            });
    }

    [Fact]
    public void ProductionLegacyAssignmentsDoNotUseSystemDateTimeNowOrUtcNow()
    {
        var backendDirectory = Path.Combine(FindRepositoryRoot(), "backend");
        var violations = Directory
            .EnumerateFiles(backendDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path, backendDirectory))
            .SelectMany(path => LegacyTimestampWriteGuard.FindViolations(
                File.ReadAllText(path),
                Path.GetRelativePath(backendDirectory, path)))
            .OrderBy(x => x.FilePath, StringComparer.Ordinal)
            .ThenBy(x => x.Line)
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "Legacy local-wall writes must use TimeProvider.GetLocalNow().DateTime, not System.DateTime clocks:\n"
            + string.Join('\n', violations.Select(x =>
                $"{x.FilePath}:{x.Line} {x.PropertyName} <- DateTime.{x.ClockMember}")));
    }

    private static bool IsBuildOutput(string path, string backendDirectory)
    {
        var relativeParts = Path.GetRelativePath(backendDirectory, path)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relativeParts.Contains("bin", StringComparer.Ordinal)
               || relativeParts.Contains("obj", StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "backend", "NzbWebDAV.csproj")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException(
            $"Could not find the NZBDav repository root above '{AppContext.BaseDirectory}'.");
    }
}

internal static class LegacyTimestampWriteGuard
{
    private static readonly HashSet<string> LegacyPropertyNames =
        ["CreatedAt", "PauseUntil"];

    internal static IReadOnlyList<LegacyTimestampClockViolation> FindViolations(
        string source,
        string filePath)
    {
        var root = CSharpSyntaxTree.ParseText(source, path: filePath).GetRoot();
        var violations = new List<LegacyTimestampClockViolation>();

        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var propertyName = GetAssignedPropertyName(assignment.Left);
            if (propertyName is null || !LegacyPropertyNames.Contains(propertyName)) continue;
            var ownerType = GetAssignmentOwnerType(root, assignment);
            if (!IsLegacyProperty(ownerType, propertyName)) continue;
            AddClockViolations(
                violations,
                filePath,
                propertyName,
                assignment.Right,
                assignment);
        }

        foreach (var initializer in root.DescendantNodes().OfType<EqualsValueClauseSyntax>())
        {
            var target = GetInitializedTarget(initializer);
            if (target is null || !IsLegacyProperty(target.Value.OwnerType, target.Value.PropertyName)) continue;
            AddClockViolations(
                violations,
                filePath,
                target.Value.PropertyName,
                initializer.Value,
                initializer.Parent ?? initializer);
        }

        return violations;
    }

    private static void AddClockViolations(
        ICollection<LegacyTimestampClockViolation> violations,
        string filePath,
        string propertyName,
        ExpressionSyntax value,
        SyntaxNode locationNode)
    {
        foreach (var clockAccess in value
                     .DescendantNodesAndSelf()
                     .OfType<MemberAccessExpressionSyntax>()
                     .Where(IsForbiddenSystemDateTimeClock))
        {
            var line = locationNode.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            violations.Add(new LegacyTimestampClockViolation(
                filePath,
                line,
                propertyName,
                clockAccess.Name.Identifier.ValueText));
        }
    }

    private static string? GetAssignedPropertyName(ExpressionSyntax left)
    {
        return left switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            _ => null
        };
    }

    private static (string OwnerType, string PropertyName)? GetInitializedTarget(
        EqualsValueClauseSyntax initializer)
    {
        return initializer.Parent switch
        {
            PropertyDeclarationSyntax property => CreateTarget(
                GetContainingTypeName(property),
                property.Identifier.ValueText),
            VariableDeclaratorSyntax variable
                when variable.Parent?.Parent is FieldDeclarationSyntax => CreateTarget(
                    GetContainingTypeName(variable),
                    variable.Identifier.ValueText),
            _ => null
        };
    }

    private static (string OwnerType, string PropertyName)? CreateTarget(
        string? ownerType,
        string propertyName) =>
        ownerType is null ? null : (ownerType, propertyName);

    private static string? GetAssignmentOwnerType(
        SyntaxNode root,
        AssignmentExpressionSyntax assignment)
    {
        if (assignment.Left is MemberAccessExpressionSyntax memberAccess)
            return ResolveReceiverType(root, memberAccess.Expression, assignment);

        if (assignment.Left is not IdentifierNameSyntax) return null;
        if (assignment.Parent is InitializerExpressionSyntax initializer
            && initializer.IsKind(SyntaxKind.ObjectInitializerExpression))
        {
            return GetObjectInitializerType(root, initializer, assignment);
        }

        return GetContainingTypeName(assignment);
    }

    private static string? GetObjectInitializerType(
        SyntaxNode root,
        InitializerExpressionSyntax initializer,
        SyntaxNode useSite)
    {
        return initializer.Parent switch
        {
            ObjectCreationExpressionSyntax creation => GetSimpleTypeName(creation.Type),
            ImplicitObjectCreationExpressionSyntax implicitCreation =>
                ResolveImplicitObjectCreationType(root, implicitCreation, useSite),
            _ => null
        };
    }

    private static string? ResolveImplicitObjectCreationType(
        SyntaxNode root,
        ImplicitObjectCreationExpressionSyntax creation,
        SyntaxNode useSite)
    {
        if (creation.Parent is EqualsValueClauseSyntax equalsValue)
        {
            return equalsValue.Parent switch
            {
                VariableDeclaratorSyntax variable when variable.Parent is VariableDeclarationSyntax declaration =>
                    GetSimpleTypeName(declaration.Type),
                PropertyDeclarationSyntax property => GetSimpleTypeName(property.Type),
                _ => null
            };
        }

        if (creation.Parent is AssignmentExpressionSyntax assignment
            && assignment.Left is IdentifierNameSyntax identifier)
        {
            return ResolveDeclaredIdentifierType(root, identifier.Identifier.ValueText, useSite);
        }

        return null;
    }

    private static string? ResolveReceiverType(
        SyntaxNode root,
        ExpressionSyntax receiver,
        SyntaxNode useSite)
    {
        return receiver switch
        {
            IdentifierNameSyntax identifier => ResolveDeclaredIdentifierType(
                root,
                identifier.Identifier.ValueText,
                useSite),
            ThisExpressionSyntax or BaseExpressionSyntax => GetContainingTypeName(useSite),
            ParenthesizedExpressionSyntax parenthesized => ResolveReceiverType(
                root,
                parenthesized.Expression,
                useSite),
            PostfixUnaryExpressionSyntax suppressed
                when suppressed.IsKind(SyntaxKind.SuppressNullableWarningExpression) => ResolveReceiverType(
                    root,
                    suppressed.Operand,
                    useSite),
            _ => null
        };
    }

    private static string? ResolveDeclaredIdentifierType(
        SyntaxNode root,
        string identifier,
        SyntaxNode useSite)
    {
        var candidates = new List<(int ScopeLength, int Position, string TypeName)>();

        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>()
                     .Where(x => x.Identifier.ValueText == identifier && x.Type is not null))
        {
            var scope = parameter.Ancestors().FirstOrDefault(IsDeclarationScope);
            AddCandidate(candidates, scope, parameter.SpanStart, parameter.Type, useSite);
        }

        foreach (var variable in root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                     .Where(x => x.Identifier.ValueText == identifier))
        {
            if (variable.Parent is not VariableDeclarationSyntax declaration) continue;
            var scope = variable.Ancestors().FirstOrDefault(x =>
                x is BlockSyntax or TypeDeclarationSyntax);
            if (scope is BlockSyntax && variable.SpanStart > useSite.SpanStart) continue;
            AddCandidate(
                candidates,
                scope,
                variable.SpanStart,
                ResolveVariableTypeName(declaration, variable),
                useSite);
        }

        foreach (var property in root.DescendantNodes().OfType<PropertyDeclarationSyntax>()
                     .Where(x => x.Identifier.ValueText == identifier))
        {
            var scope = property.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            AddCandidate(candidates, scope, property.SpanStart, property.Type, useSite);
        }

        return candidates
            .OrderBy(x => x.ScopeLength)
            .ThenByDescending(x => x.Position)
            .Select(x => x.TypeName)
            .FirstOrDefault();
    }

    private static void AddCandidate(
        ICollection<(int ScopeLength, int Position, string TypeName)> candidates,
        SyntaxNode? scope,
        int position,
        TypeSyntax? type,
        SyntaxNode useSite) =>
        AddCandidate(
            candidates,
            scope,
            position,
            type is null ? null : GetSimpleTypeName(type),
            useSite);

    private static void AddCandidate(
        ICollection<(int ScopeLength, int Position, string TypeName)> candidates,
        SyntaxNode? scope,
        int position,
        string? typeName,
        SyntaxNode useSite)
    {
        if (scope is null || typeName is null || !scope.Span.Contains(useSite.SpanStart)) return;
        candidates.Add((scope.Span.Length, position, typeName));
    }

    private static string? ResolveVariableTypeName(
        VariableDeclarationSyntax declaration,
        VariableDeclaratorSyntax variable)
    {
        var declaredType = GetSimpleTypeName(declaration.Type);
        if (declaredType != "var") return declaredType;

        return variable.Initializer?.Value switch
        {
            ObjectCreationExpressionSyntax creation => GetSimpleTypeName(creation.Type),
            CastExpressionSyntax cast => GetSimpleTypeName(cast.Type),
            _ => null
        };
    }

    private static bool IsDeclarationScope(SyntaxNode node) =>
        node is BaseMethodDeclarationSyntax
            or LocalFunctionStatementSyntax
            or AnonymousFunctionExpressionSyntax
            or TypeDeclarationSyntax;

    private static string? GetContainingTypeName(SyntaxNode node) =>
        node.AncestorsAndSelf()
            .OfType<TypeDeclarationSyntax>()
            .FirstOrDefault()
            ?.Identifier.ValueText;

    private static string? GetSimpleTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
            NullableTypeSyntax nullable => GetSimpleTypeName(nullable.ElementType),
            _ => null
        };
    }

    private static bool IsLegacyProperty(string? ownerType, string propertyName)
    {
        return ownerType switch
        {
            "DavItem" => propertyName == "CreatedAt",
            "HistoryItem" => propertyName == "CreatedAt",
            "QueueItem" => propertyName is "CreatedAt" or "PauseUntil",
            _ => false
        };
    }

    private static bool IsForbiddenSystemDateTimeClock(MemberAccessExpressionSyntax memberAccess)
    {
        var memberName = memberAccess.Name.Identifier.ValueText;
        if (memberName is not ("Now" or "UtcNow")) return false;

        // The syntax-node target check deliberately excludes DateTimeOffset.UtcNow.
        // Fully qualified System.DateTime forms are supported without scanning raw text.
        var target = memberAccess.Expression.WithoutTrivia().ToString();
        return target is "DateTime" or "System.DateTime" or "global::System.DateTime";
    }
}

internal sealed record LegacyTimestampClockViolation(
    string FilePath,
    int Line,
    string PropertyName,
    string ClockMember);
