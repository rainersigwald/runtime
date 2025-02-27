// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XUnitWrapperGenerator;

[Generator]
public sealed class XUnitWrapperGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var methodsInSource = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, ct) =>
                    node.IsKind(SyntaxKind.MethodDeclaration)
                        && node is MethodDeclarationSyntax method
                        && method.AttributeLists.Count > 0,
                static (context, ct) => (IMethodSymbol)context.SemanticModel.GetDeclaredSymbol(context.Node, ct)!);

        var outOfProcessTests = context.AdditionalTextsProvider.Combine(context.AnalyzerConfigOptionsProvider).SelectMany((data, ct) =>
        {
            var (file, options) = data;

            AnalyzerConfigOptions fileOptions = options.GetOptions(file);

            if (fileOptions.IsOutOfProcessTestAssembly())
            {
                string? assemblyPath = fileOptions.TestAssemblyRelativePath();
                string? testDisplayName = fileOptions.TestDisplayName();
                if (assemblyPath is not null && testDisplayName is not null)
                {
                    return ImmutableArray.Create<ITestInfo>(new OutOfProcessTest(testDisplayName, assemblyPath));
                }
            }

            return ImmutableArray<ITestInfo>.Empty;
        });

        var aliasMap = context.CompilationProvider.Select((comp, ct) =>
        {
            var aliasMap = ImmutableDictionary.CreateBuilder<string, string>();
            aliasMap.Add(comp.Assembly.MetadataName, "global");
            foreach (var reference in comp.References)
            {
                aliasMap.Add(comp.GetAssemblyOrModuleSymbol(reference)!.MetadataName, reference.Properties.Aliases.FirstOrDefault() ?? "global");
            }

            return aliasMap.ToImmutable();
        }).WithComparer(new ImmutableDictionaryValueComparer<string, string>(EqualityComparer<string>.Default));

        var assemblyName = context.CompilationProvider.Select((comp, ct) => comp.Assembly.MetadataName);

        var alwaysWriteEntryPoint = context.CompilationProvider.Select((comp, ct) => comp.Options.OutputKind == OutputKind.ConsoleApplication && comp.GetEntryPoint(ct) is null);

        var testsInSource =
            methodsInSource
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Combine(aliasMap)
            .SelectMany((data, ct) => ImmutableArray.CreateRange(GetTestMethodInfosForMethod(data.Left.Left, data.Left.Right, data.Right)));

        var pathsForReferences = context
            .AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select((data, ct) => new KeyValuePair<string, string?>(data.Left.Path, data.Right.GetOptions(data.Left).SingleTestDisplayName()))
            .Where(data => data.Value is not null)
            .Collect()
            .Select((paths, ct) => ImmutableDictionary.CreateRange(paths))
            .WithComparer(new ImmutableDictionaryValueComparer<string, string?>(EqualityComparer<string?>.Default));

        var testsInReferencedAssemblies = context
            .MetadataReferencesProvider
            .Combine(context.CompilationProvider)
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Combine(pathsForReferences)
            .Combine(aliasMap)
            .SelectMany((data, ct) =>
            {
                var ((((reference, compilation), configOptions), paths), aliasMap) = data;
                ExternallyReferencedTestMethodsVisitor visitor = new();
                IEnumerable<IMethodSymbol> testMethods = visitor.Visit(compilation.GetAssemblyOrModuleSymbol(reference))!;
                ImmutableArray<ITestInfo> tests = ImmutableArray.CreateRange(testMethods.SelectMany(method => GetTestMethodInfosForMethod(method, configOptions, aliasMap)));
                if (tests.Length == 1 && reference is PortableExecutableReference { FilePath: string pathOnDisk } && paths.TryGetValue(pathOnDisk, out string? referencePath))
                {
                    // If we only have one test in the module and we have a display name for the module the test comes from, then rename it to the module name to make on disk discovery easier.
                    return ImmutableArray.Create((ITestInfo)new TestWithCustomDisplayName(tests[0], referencePath!));
                }
                return tests;
            });

        var allTests = testsInSource.Collect().Combine(testsInReferencedAssemblies.Collect()).Combine(outOfProcessTests.Collect()).SelectMany((tests, ct) => tests.Left.Left.AddRange(tests.Left.Right).AddRange(tests.Right));

        context.RegisterImplementationSourceOutput(
            allTests
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Where(data =>
            {
                var (test, options) = data;
                var filter = new XUnitWrapperLibrary.TestFilter(options.GlobalOptions.TestFilter(), null);
                return filter.ShouldRunTest($"{test.ContainingType}.{test.Method}", test.DisplayNameForFiltering, Array.Empty<string>());
            })
            .Select((data, ct) => data.Left)
            .Collect()
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Combine(aliasMap)
            .Combine(assemblyName)
            .Combine(alwaysWriteEntryPoint),
            static (context, data) =>
            {
                var ((((methods, configOptions), aliasMap), assemblyName), alwaysWriteEntryPoint) = data;

                if (methods.Length == 0 && !alwaysWriteEntryPoint)
                {
                    // If we have no test methods, assume that this project is not migrated to the new system yet
                    // and that we shouldn't generate a no-op Main method.
                    return;
                }

                bool isMergedTestRunnerAssembly = configOptions.GlobalOptions.IsMergedTestRunnerAssembly();
                configOptions.GlobalOptions.TryGetValue("build_property.TargetOS", out string? targetOS);

                if (isMergedTestRunnerAssembly)
                {
                    if (targetOS?.ToLowerInvariant() is "ios" or "iossimulator" or "tvos" or "tvossimulator" or "maccatalyst" or "android" or "browser")
                    {
                        context.AddSource("XHarnessRunner.g.cs", GenerateXHarnessTestRunner(methods, aliasMap, assemblyName));
                    }
                    else
                    {
                        context.AddSource("FullRunner.g.cs", GenerateFullTestRunner(methods, aliasMap, assemblyName));
                    }
                }
                else
                {
                    context.AddSource("SimpleRunner.g.cs", GenerateStandaloneSimpleTestRunner(methods, aliasMap));
                }
            });
    }

    private static void AppendAliasMap(CodeBuilder builder, ImmutableDictionary<string, string> aliasMap)
    {
        bool didOutput = false;
        foreach (string alias in aliasMap.Values.Where(alias => alias != "global").OrderBy(a => a))
        {
            builder.AppendLine($"extern alias {alias};");
            didOutput = true;
        }
        if (didOutput)
        {
            builder.AppendLine();
        }
    }

    private static string GenerateFullTestRunner(ImmutableArray<ITestInfo> testInfos, ImmutableDictionary<string, string> aliasMap, string assemblyName)
    {
        // For simplicity, we'll use top-level statements for the generated Main method.
        CodeBuilder builder = new();
        AppendAliasMap(builder, aliasMap);

        builder.AppendLine("System.Collections.Generic.HashSet<string> testExclusionList = XUnitWrapperLibrary.TestFilter.LoadTestExclusionList();");
        builder.AppendLine();

        builder.AppendLine($@"if (System.IO.File.Exists(""{assemblyName}.tempLog.xml""))");
        using (builder.NewBracesScope())
        {
            builder.AppendLine($@"System.IO.File.Delete(""{assemblyName}.tempLog.xml"");");
        }
        builder.AppendLine($@"if (System.IO.File.Exists(""{assemblyName}.testStats.csv""))");
        using (builder.NewBracesScope())
        {
            builder.AppendLine($@"System.IO.File.Delete(""{assemblyName}.testStats.csv"");");
        }
        builder.AppendLine();

        builder.AppendLine("XUnitWrapperLibrary.TestFilter filter = new (args, testExclusionList);");
        builder.AppendLine("XUnitWrapperLibrary.TestSummary summary = new();");
        builder.AppendLine("System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();");
        builder.AppendLine("XUnitWrapperLibrary.TestOutputRecorder outputRecorder = new(System.Console.Out);");
        builder.AppendLine("System.Console.SetOut(outputRecorder);");
        builder.AppendLine();

        builder.AppendLine($@"using (System.IO.StreamWriter tempLogSw = System.IO.File.AppendText(""{assemblyName}.tempLog.xml""))");
        builder.AppendLine($@"using (System.IO.StreamWriter statsCsvSw = System.IO.File.AppendText(""{assemblyName}.testStats.csv"")){{");
        CodeBuilder testExecutorBuilder = new();
        int totalTestsEmitted = 0;

        using (builder.NewBracesScope())
        {
            builder.AppendLine("statsCsvSw.WriteLine($\"{TestCount.Count},0,0,0\");");
            // CAUTION NOTE: If this ever changes and the 'assembly' tag is no longer
            // the topmost one in the temp log, XUnitLogChecker must be updated accordingly.
            // Otherwise, it's going to fail when attempting to find dumps.
            builder.AppendLine($@"summary.WriteHeaderToTempLog(""{assemblyName}"", tempLogSw);");

            ITestReporterWrapper reporter =
                new WrapperLibraryTestSummaryReporting("summary", "filter", "outputRecorder");

            int testsLeftInCurrentTestExecutor = 0;
            int currentTestExecutor = 0;

            if (testInfos.Length > 0)
            {
                // Break tests into groups of 50 so that we don't create an unreasonably large main method
                // Excessively large methods are known to take a long time to compile, and use excessive stack
                // leading to test failures.
                foreach (ITestInfo test in testInfos)
                {
                    if (testsLeftInCurrentTestExecutor == 0)
                    {
                        if (currentTestExecutor != 0)
                        {
                            testExecutorBuilder.PopIndent();
                            testExecutorBuilder.AppendLine("}");
                            testExecutorBuilder.AppendLine();
                        }

                        currentTestExecutor++;
                        testExecutorBuilder.AppendLine($"void TestExecutor{currentTestExecutor}(System.IO.StreamWriter tempLogSw, System.IO.StreamWriter statsCsvSw)");
                        testExecutorBuilder.AppendLine("{");
                        testExecutorBuilder.PushIndent();

                        builder.AppendLine($"TestExecutor{currentTestExecutor}(tempLogSw, statsCsvSw);");
                        testsLeftInCurrentTestExecutor = 50; // Break test executors into groups of 50, which empirically seems to work well
                    }
                    else
                    {
                        testExecutorBuilder.AppendLine();
                    }

                    testExecutorBuilder.Append(test.GenerateTestExecution(reporter));
                    totalTestsEmitted++;
                    testsLeftInCurrentTestExecutor--;
                }

                testExecutorBuilder.PopIndent();
                testExecutorBuilder.AppendLine("}");
                testExecutorBuilder.AppendLine();
            }

            testExecutorBuilder.AppendLine("}");
            builder.AppendLine("tempLogSw.WriteLine(\"</assembly>\");");
        }
        builder.AppendLine();

        builder.AppendLine($@"string testResults = summary.GetTestResultOutput(""{assemblyName}"");");
        builder.AppendLine($@"string workitemUploadRoot = System.Environment.GetEnvironmentVariable(""HELIX_WORKITEM_UPLOAD_ROOT"");");
        builder.AppendLine($@"if (workitemUploadRoot != null)");
        using (builder.NewBracesScope())
        {
            builder.AppendLine($@"System.IO.File.WriteAllText(System.IO.Path.Combine(workitemUploadRoot, ""{assemblyName}.testResults.xml.txt""), testResults);");
        }
        builder.AppendLine();

        builder.AppendLine($@"System.IO.File.WriteAllText(""{assemblyName}.testResults.xml"", testResults);");
        builder.AppendLine("return 100;");
        builder.AppendLine();

        builder.Append(testExecutorBuilder);
        builder.AppendLine("public static class TestCount { public const int Count = " + totalTestsEmitted.ToString() + "; }");
        return builder.GetCode();
    }

    private static string GenerateXHarnessTestRunner(ImmutableArray<ITestInfo> testInfos, ImmutableDictionary<string, string> aliasMap, string assemblyName)
    {
        // For simplicity, we'll use top-level statements for the generated Main method.
        CodeBuilder builder = new();
        AppendAliasMap(builder, aliasMap);

        builder.AppendLine("System.Collections.Generic.HashSet<string> testExclusionList = XUnitWrapperLibrary.TestFilter.LoadTestExclusionList();");
        builder.AppendLine();

        builder.AppendLine("try");
        using (builder.NewBracesScope())
        {
            builder.AppendLine($@"return await XHarnessRunnerLibrary.RunnerEntryPoint.RunTests(RunTests, ""{assemblyName}"", args.Length != 0 ? args[0] : null, testExclusionList);");
        }
        builder.AppendLine("catch(System.Exception ex)");
        using (builder.NewBracesScope())
        {
            builder.AppendLine("System.Console.WriteLine(ex.ToString());");
            builder.AppendLine("return 101;");
        }
        builder.AppendLine();

        builder.AppendLine("static XUnitWrapperLibrary.TestSummary RunTests(XUnitWrapperLibrary.TestFilter filter)");
        using (builder.NewBracesScope())
        {
            builder.AppendLine("XUnitWrapperLibrary.TestSummary summary = new();");
            builder.AppendLine("System.Diagnostics.Stopwatch stopwatch = new();");
            builder.AppendLine("XUnitWrapperLibrary.TestOutputRecorder outputRecorder = new(System.Console.Out);");
            builder.AppendLine("System.Console.SetOut(outputRecorder);");
            builder.AppendLine();

            builder.AppendLine($@"if (System.IO.File.Exists(""{assemblyName}.tempLog.xml""))");
            using (builder.NewBracesScope())
            {
                builder.AppendLine($@"System.IO.File.Delete(""{assemblyName}.tempLog.xml"");");
            }
            builder.AppendLine($@"if (System.IO.File.Exists(""{assemblyName}.testStats.csv""))");
            using (builder.NewBracesScope())
            {
                builder.AppendLine($@"System.IO.File.Delete(""{assemblyName}.testStats.csv"");");
            }
            builder.AppendLine();

            ITestReporterWrapper reporter = new WrapperLibraryTestSummaryReporting("summary", "filter", "outputRecorder");

            CodeBuilder testExecutorBuilder = new();
            int testsLeftInCurrentTestExecutor = 0;
            int currentTestExecutor = 0;

            // Open the stream writer for the temp log.
            builder.AppendLine($@"using (System.IO.StreamWriter tempLogSw = System.IO.File.AppendText(""{assemblyName}.templog.xml""))");
            builder.AppendLine($@"using (System.IO.StreamWriter statsCsvSw = System.IO.File.AppendText(""{assemblyName}.testStats.csv""))");
            using (builder.NewBracesScope())
            {
                builder.AppendLine($"statsCsvSw.WriteLine(\"{testInfos.Length},0,0,0\");");

                if (testInfos.Length > 0)
                {
                    // Break tests into groups of 50 so that we don't create an unreasonably large main method
                    // Excessively large methods are known to take a long time to compile, and use excessive stack
                    // leading to test failures.
                    foreach (ITestInfo test in testInfos)
                    {
                        if (testsLeftInCurrentTestExecutor == 0)
                        {
                            if (currentTestExecutor != 0)
                            {
                                testExecutorBuilder.PopIndent();
                                testExecutorBuilder.AppendLine("}");
                                testExecutorBuilder.AppendLine();
                            }

                            currentTestExecutor++;
                            testExecutorBuilder.AppendLine($"static void TestExecutor{currentTestExecutor}("
                                                           + "XUnitWrapperLibrary.TestSummary summary, "
                                                           + "XUnitWrapperLibrary.TestFilter filter, "
                                                           + "XUnitWrapperLibrary.TestOutputRecorder outputRecorder, "
                                                           + "System.Diagnostics.Stopwatch stopwatch, "
                                                           + "System.IO.StreamWriter tempLogSw, "
                                                           + "System.IO.StreamWriter statsCsvSw)");
                            testExecutorBuilder.AppendLine("{");
                            testExecutorBuilder.PushIndent();

                            builder.AppendLine($"TestExecutor{currentTestExecutor}(summary, filter, outputRecorder, stopwatch, tempLogSw, statsCsvSw);");
                            testsLeftInCurrentTestExecutor = 50; // Break test executors into groups of 50, which empirically seems to work well
                        }
                        else
                        {
                            testExecutorBuilder.AppendLine();
                        }

                        testExecutorBuilder.Append(test.GenerateTestExecution(reporter));
                        testsLeftInCurrentTestExecutor--;
                    }

                    testExecutorBuilder.PopIndent();
                    testExecutorBuilder.AppendLine("}");
                    testExecutorBuilder.AppendLine();
                }
            }
            builder.AppendLine("return summary;");

            builder.Append(testExecutorBuilder);
        }

        return builder.GetCode();
    }

    private static string GenerateStandaloneSimpleTestRunner(ImmutableArray<ITestInfo> testInfos, ImmutableDictionary<string, string> aliasMap)
    {
        ITestReporterWrapper reporter = new NoTestReporting();
        CodeBuilder builder = new();
        AppendAliasMap(builder, aliasMap);
        builder.AppendLine("class __GeneratedMainWrapper");
        using (builder.NewBracesScope())
        {
            builder.AppendLine("public static int Main()");
            using (builder.NewBracesScope())
            {
                builder.AppendLine("try");
                using (builder.NewBracesScope())
                {
                    foreach (ITestInfo testInfo in testInfos)
                    {
                        builder.Append(testInfo.GenerateTestExecution(reporter));
                    }
                }
                builder.AppendLine("catch(System.Exception ex)");
                using (builder.NewBracesScope())
                {
                    builder.AppendLine("System.Console.WriteLine(ex.ToString());");
                    builder.AppendLine("return 101;");
                }
                builder.AppendLine("return 100;");
            }
        }
        return builder.GetCode();
    }

    private sealed class ExternallyReferencedTestMethodsVisitor : SymbolVisitor<IEnumerable<IMethodSymbol>>
    {
        public override IEnumerable<IMethodSymbol>? VisitAssembly(IAssemblySymbol symbol)
        {
            return Visit(symbol.GlobalNamespace);
        }

        public override IEnumerable<IMethodSymbol>? VisitModule(IModuleSymbol symbol)
        {
            return Visit(symbol.GlobalNamespace);
        }

        public override IEnumerable<IMethodSymbol>? VisitNamespace(INamespaceSymbol symbol)
        {
            foreach (var type in symbol.GetMembers())
            {
                foreach (var result in Visit(type) ?? Array.Empty<IMethodSymbol>())
                {
                    yield return result;
                }
            }
        }

        public override IEnumerable<IMethodSymbol>? VisitNamedType(INamedTypeSymbol symbol)
        {
            if (symbol.DeclaredAccessibility == Accessibility.Public)
            {
                foreach (var type in symbol.GetMembers())
                {
                    foreach (var result in Visit(type) ?? Array.Empty<IMethodSymbol>())
                    {
                        yield return result;
                    }
                }
            }
        }

        public override IEnumerable<IMethodSymbol>? VisitMethod(IMethodSymbol symbol)
        {
            if (symbol.DeclaredAccessibility == Accessibility.Public
                && symbol.GetAttributes().Any(attr => attr.AttributeClass?.ContainingNamespace.Name == "Xunit"))
            {
                return new[] { symbol };
            }
            return Array.Empty<IMethodSymbol>();
        }
    }

    private static IEnumerable<ITestInfo> GetTestMethodInfosForMethod(IMethodSymbol method, AnalyzerConfigOptionsProvider options, ImmutableDictionary<string, string> aliasMap)
    {
        bool factAttribute = false;
        bool theoryAttribute = false;
        List<AttributeData> theoryDataAttributes = new();
        List<AttributeData> filterAttributes = new();
        foreach (var attr in method.GetAttributesOnSelfAndContainingSymbols())
        {
            var hasSkip = attr.NamedArguments.Any(x => x.Key == "Skip");

            switch (attr.AttributeClass?.ToDisplayString())
            {
                case "Xunit.ConditionalFactAttribute":
                    if (!hasSkip)
                    {
                        filterAttributes.Add(attr);
                        factAttribute = true;
                    }
                    break;
                case "Xunit.FactAttribute":
                    if (!hasSkip)
                    {
                        factAttribute = true;
                    }
                    break;
                case "Xunit.ConditionalTheoryAttribute":
                    if (!hasSkip)
                    {
                        filterAttributes.Add(attr);
                        theoryAttribute = true;
                    }
                    break;
                case "Xunit.TheoryAttribute":
                    if (!hasSkip)
                    {
                        theoryAttribute = true;
                    }
                    break;
                case "Xunit.ConditionalClassAttribute":
                case "Xunit.SkipOnPlatformAttribute":
                case "Xunit.ActiveIssueAttribute":
                case "Xunit.OuterloopAttribute":
                case "Xunit.PlatformSpecificAttribute":
                case "Xunit.SkipOnMonoAttribute":
                case "Xunit.SkipOnTargetFrameworkAttribute":
                case "Xunit.SkipOnCoreClrAttribute":
                    filterAttributes.Add(attr);
                    break;
                case "Xunit.InlineDataAttribute":
                case "Xunit.MemberDataAttribute":
                    theoryDataAttributes.Add(attr);
                    break;
            }
        }

        ImmutableArray<ITestInfo> testInfos = ImmutableArray<ITestInfo>.Empty;

        if (factAttribute)
        {
            if (!method.Parameters.IsEmpty)
            {
                // todo: emit diagnostic
            }
            else if (method.IsStatic && method.ReturnType.SpecialType == SpecialType.System_Int32)
            {
                // Support the old executable-based test design where an int return of 100 is success.
                testInfos = ImmutableArray.Create((ITestInfo)new LegacyStandaloneEntryPointTestMethod(method, aliasMap[method.ContainingAssembly.MetadataName]));
            }
            else
            {
                testInfos = ImmutableArray.Create((ITestInfo)new BasicTestMethod(method, aliasMap[method.ContainingAssembly.MetadataName]));
            }
        }
        else if (theoryAttribute)
        {
            testInfos = CreateTestCases(method, theoryDataAttributes, aliasMap[method.ContainingAssembly.MetadataName]);
        }

        foreach (var filterAttribute in filterAttributes)
        {
            switch (filterAttribute.AttributeClass!.ToDisplayString())
            {
                case "Xunit.ConditionalFactAttribute":
                case "Xunit.ConditionalTheoryAttribute":
                case "Xunit.ConditionalClassAttribute":
                    {
                        ITypeSymbol conditionType;
                        ImmutableArray<TypedConstant> conditionMembers;
                        if (filterAttribute.AttributeConstructor!.Parameters.Length == 1)
                        {
                            conditionType = method.ContainingType;
                            conditionMembers = filterAttribute.ConstructorArguments[0].Values;
                        }
                        else
                        {
                            Debug.Assert(filterAttribute.AttributeConstructor!.Parameters.Length == 2);
                            conditionType = (ITypeSymbol)filterAttribute.ConstructorArguments[0].Value!;
                            conditionMembers = filterAttribute.ConstructorArguments[1].Values;
                        }
                        testInfos = DecorateWithUserDefinedCondition(
                            testInfos,
                            conditionType,
                            conditionMembers,
                            aliasMap[conditionType.ContainingAssembly.MetadataName],
                            false /* do not negate the condition, as this attribute indicates that a test will be run */);
                        break;
                    }
                case "Xunit.OuterloopAttribute":
                    if (options.GlobalOptions.Priority() == 0)
                    {
                        // If we aren't building the outerloop/Pri 1 test suite, then this attribute acts like an
                        // [ActiveIssue] attribute (it has the same shape).
                        goto case "Xunit.ActiveIssueAttribute";
                    }
                    break;
                case "Xunit.ActiveIssueAttribute":
                    if (filterAttribute.AttributeConstructor!.Parameters.Length == 3)
                    {
                        ITypeSymbol conditionType = (ITypeSymbol)filterAttribute.ConstructorArguments[1].Value!;
                        testInfos = DecorateWithUserDefinedCondition(
                            testInfos,
                            conditionType,
                            filterAttribute.ConstructorArguments[2].Values,
                            aliasMap[conditionType.ContainingAssembly.MetadataName],
                            true /* negate the condition, as this attribute indicates that a test will NOT be run */);
                        break;
                    }
                    else if (filterAttribute.AttributeConstructor.Parameters.Length == 4)
                    {
                        testInfos = FilterForSkippedRuntime(
                            FilterForSkippedTargetFrameworkMonikers(
                                DecorateWithSkipOnPlatform(testInfos, (int)filterAttribute.ConstructorArguments[1].Value!, options),
                                (int)filterAttribute.ConstructorArguments[2].Value!),
                            (int)filterAttribute.ConstructorArguments[3].Value!, options);
                    }
                    else
                    {
                        switch (filterAttribute.AttributeConstructor.Parameters[1].Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                        {
                            case "global::Xunit.TestPlatforms":
                                testInfos = DecorateWithSkipOnPlatform(testInfos, (int)filterAttribute.ConstructorArguments[1].Value!, options);
                                break;
                            case "global::Xunit.TestRuntimes":
                                testInfos = FilterForSkippedRuntime(testInfos, (int)filterAttribute.ConstructorArguments[1].Value!, options);
                                break;
                            case "global::Xunit.TargetFrameworkMonikers":
                                testInfos = FilterForSkippedTargetFrameworkMonikers(testInfos, (int)filterAttribute.ConstructorArguments[1].Value!);
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case "Xunit.SkipOnMonoAttribute":
                    if (options.GlobalOptions.RuntimeFlavor().ToLowerInvariant() != "mono")
                    {
                        // If we're building tests not for Mono, we can skip handling the specifics of the SkipOnMonoAttribute.
                        continue;
                    }
                    testInfos = DecorateWithSkipOnPlatform(testInfos, (int)filterAttribute.ConstructorArguments[1].Value!, options);
                    break;
                case "Xunit.SkipOnPlatformAttribute":
                    testInfos = DecorateWithSkipOnPlatform(testInfos, (int)filterAttribute.ConstructorArguments[0].Value!, options);
                    break;
                case "Xunit.PlatformSpecificAttribute":
                    testInfos = DecorateWithSkipOnPlatform(testInfos, ~(int)filterAttribute.ConstructorArguments[0].Value!, options);
                    break;
                case "Xunit.SkipOnTargetFrameworkAttribute":
                    testInfos = FilterForSkippedTargetFrameworkMonikers(testInfos, (int)filterAttribute.ConstructorArguments[0].Value!);
                    break;
                case "Xunit.SkipOnCoreClrAttribute":
                    if (options.GlobalOptions.RuntimeFlavor().ToLowerInvariant() != "coreclr")
                    {
                        // If we're building tests not for CoreCLR, we can skip handling the specifics of the SkipOnCoreClrAttribute.
                        continue;
                    }

                    Xunit.TestPlatforms skippedTestPlatforms = 0;
                    Xunit.RuntimeConfiguration skippedConfigurations = 0;
                    Xunit.RuntimeTestModes skippedTestModes = 0;

                    for (int i = 1; i < filterAttribute.AttributeConstructor!.Parameters.Length; i++)
                    {
                        ReadSkippedInformationFromSkipOnCoreClrAttributeArgument(filterAttribute, i);
                    }

                    void ReadSkippedInformationFromSkipOnCoreClrAttributeArgument(AttributeData filterAttribute, int argumentIndex)
                    {
                        int argumentValue = (int)filterAttribute.ConstructorArguments[argumentIndex].Value!;
                        switch (filterAttribute.AttributeConstructor!.Parameters[argumentIndex].Type.ToDisplayString())
                        {
                            case "Xunit.TestPlatforms":
                                skippedTestPlatforms = (Xunit.TestPlatforms)argumentValue;
                                break;
                            case "Xunit.RuntimeTestModes":
                                skippedTestModes = (Xunit.RuntimeTestModes)argumentValue;
                                break;
                            case "Xunit.RuntimeConfiguration":
                                skippedConfigurations = (Xunit.RuntimeConfiguration)argumentValue;
                                break;
                            default:
                                break;
                        }
                    }

                    if (skippedTestModes == Xunit.RuntimeTestModes.Any)
                    {
                        testInfos = FilterForSkippedRuntime(testInfos, (int)Xunit.TestRuntimes.CoreCLR, options);
                    }
                    testInfos = DecorateWithSkipOnPlatform(testInfos, (int)skippedTestPlatforms, options);
                    testInfos = DecorateWithSkipOnCoreClrConfiguration(testInfos, skippedTestModes, skippedConfigurations);

                    break;
            }
        }

        return testInfos;
    }

    private static ImmutableArray<ITestInfo> DecorateWithSkipOnCoreClrConfiguration(ImmutableArray<ITestInfo> testInfos, Xunit.RuntimeTestModes skippedTestModes, Xunit.RuntimeConfiguration skippedConfigurations)
    {
        const string ConditionClass = "TestLibrary.CoreClrConfigurationDetection";
        List<string> conditions = new();
        if (skippedConfigurations.HasFlag(Xunit.RuntimeConfiguration.Debug | Xunit.RuntimeConfiguration.Checked | Xunit.RuntimeConfiguration.Release))
        {
            // If all configurations are skipped, just skip the test as a whole
            return ImmutableArray<ITestInfo>.Empty;
        }

        if (skippedConfigurations.HasFlag(Xunit.RuntimeConfiguration.Debug))
        {
            conditions.Add($"!{ConditionClass}.IsDebugRuntime");
        }
        if (skippedConfigurations.HasFlag(Xunit.RuntimeConfiguration.Checked))
        {
            conditions.Add($"!{ConditionClass}.IsCheckedRuntime");
        }
        if (skippedConfigurations.HasFlag(Xunit.RuntimeConfiguration.Release))
        {
            conditions.Add($"!{ConditionClass}.IsReleaseRuntime");
        }
        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.RegularRun))
        {
            conditions.Add($"{ConditionClass}.IsStressTest");
        }
        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.JitStress))
        {
            conditions.Add($"!{ConditionClass}.IsJitStress");
        }
        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.JitStressRegs))
        {
            conditions.Add($"!{ConditionClass}.IsJitStressRegs");
        }
        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.JitMinOpts))
        {
            conditions.Add($"!{ConditionClass}.IsJitMinOpts");
        }
        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.TailcallStress))
        {
            conditions.Add($"!{ConditionClass}.IsTailcallStress");
        }
        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.ZapDisable))
        {
            conditions.Add($"!{ConditionClass}.IsZapDisable");
        }

        if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.AnyGCStress))
        {
            conditions.Add($"!{ConditionClass}.IsGCStress");
        }
        else if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.GCStress3))
        {
            conditions.Add($"!{ConditionClass}.IsGCStress3");
        }
        else if (skippedTestModes.HasFlag(Xunit.RuntimeTestModes.GCStressC))
        {
            conditions.Add($"!{ConditionClass}.IsGCStressC");
        }

        return ImmutableArray.CreateRange<ITestInfo>(testInfos.Select(t => new ConditionalTest(t, string.Join(" && ", conditions))));
    }

    private static ImmutableArray<ITestInfo> FilterForSkippedTargetFrameworkMonikers(ImmutableArray<ITestInfo> testInfos, int v)
    {
        var tfm = (Xunit.TargetFrameworkMonikers)v;

        if (tfm.HasFlag(Xunit.TargetFrameworkMonikers.Netcoreapp))
        {
            return ImmutableArray<ITestInfo>.Empty;
        }
        else
        {
            return testInfos;
        }
    }

    private static ImmutableArray<ITestInfo> CreateTestCases(IMethodSymbol method, List<AttributeData> theoryDataAttributes, string alias)
    {
        var testCasesBuilder = ImmutableArray.CreateBuilder<ITestInfo>();
        foreach (var attr in theoryDataAttributes)
        {
            switch (attr.AttributeClass!.ToDisplayString())
            {
                case "Xunit.InlineDataAttribute":
                    {
                        var args = attr.ConstructorArguments[0].Values;
                        if (method.Parameters.Length != args.Length)
                        {
                            // Emit diagnostic
                            continue;
                        }
                        var argsAsCode = ImmutableArray.CreateRange(args.Select(a => a.ToCSharpString()));
                        testCasesBuilder.Add(new BasicTestMethod(method, alias, arguments: argsAsCode));
                        break;
                    }
                case "Xunit.MemberDataAttribute":
                    {
                        string? memberName = (string?)attr.ConstructorArguments[0].Value;
                        if (string.IsNullOrEmpty(memberName))
                        {
                            // Emit diagnostic
                            continue;
                        }
                        INamedTypeSymbol memberType = method.ContainingType;
                        if (attr.NamedArguments.FirstOrDefault(p => p.Key == "MemberType").Value.Value is INamedTypeSymbol memberTypeOverride)
                        {
                            memberType = memberTypeOverride;
                        }
                        var membersByName = memberType.GetMembers(memberName!);
                        if (membersByName.Length != 1)
                        {
                            // Emit diagnostic
                            continue;
                        }
                        const string argumentVariableIdentifier = "testArguments";
                        // The display name for the test is an interpolated string that includes the arguments.
                        string displayNameOverride = $@"$""{alias}::{method.ContainingType.ToDisplayString(FullyQualifiedWithoutGlobalNamespace)}.{method.Name}({{string.Join("","", {argumentVariableIdentifier})}})""";
                        var argsAsCode = method.Parameters.Select((p, i) => $"({p.Type.ToDisplayString()}){argumentVariableIdentifier}[{i}]").ToImmutableArray();
                        testCasesBuilder.Add(new MemberDataTest(membersByName[0], new BasicTestMethod(method, alias, argsAsCode, displayNameOverride), alias, argumentVariableIdentifier));
                        break;
                    }
                default:
                    break;
            }
        }
        return testCasesBuilder.ToImmutable();
    }

    private static ImmutableArray<ITestInfo> FilterForSkippedRuntime(ImmutableArray<ITestInfo> testInfos, int skippedRuntimeValue, AnalyzerConfigOptionsProvider options)
    {
        Xunit.TestRuntimes skippedRuntimes = (Xunit.TestRuntimes)skippedRuntimeValue;
        string runtimeFlavor = options.GlobalOptions.RuntimeFlavor().ToLowerInvariant();
        if (runtimeFlavor == "mono" && skippedRuntimes.HasFlag(Xunit.TestRuntimes.Mono))
        {
            return ImmutableArray<ITestInfo>.Empty;
        }
        else if (runtimeFlavor == "coreclr" && skippedRuntimes.HasFlag(Xunit.TestRuntimes.CoreCLR))
        {
            return ImmutableArray<ITestInfo>.Empty;
        }
        return testInfos;
    }

    private static ImmutableArray<ITestInfo> DecorateWithSkipOnPlatform(ImmutableArray<ITestInfo> testInfos, int v, AnalyzerConfigOptionsProvider options)
    {
        Xunit.TestPlatforms platformsToSkip = (Xunit.TestPlatforms)v;
        options.GlobalOptions.TryGetValue("build_property.TargetOS", out string? targetOS);
        Xunit.TestPlatforms targetPlatform = GetPlatformForTargetOS(targetOS);

        if (platformsToSkip == 0)
        {
            // In this case, we don't need to skip any platforms
            return testInfos;
        }
        else if (platformsToSkip.HasFlag(targetPlatform))
        {
            // If the target platform is skipped, then we don't have any tests to emit.
            return ImmutableArray<ITestInfo>.Empty;
        }
        else if (targetPlatform.HasFlag(platformsToSkip))
        {
            // If our target platform encompases one or more of the skipped platforms,
            // emit a runtime platform check here.
            Xunit.TestPlatforms platformsToEnableTest = targetPlatform & ~platformsToSkip;
            return ImmutableArray.CreateRange(testInfos.Select(t => (ITestInfo)new ConditionalTest(t, platformsToEnableTest)));
        }
        else
        {
            // The target platform is not mentioned in the attribute, just run it as-is.
            return testInfos;
        }

        static Xunit.TestPlatforms GetPlatformForTargetOS(string? targetOS)
        {
            return targetOS?.ToLowerInvariant() switch
            {
                "windows" => Xunit.TestPlatforms.Windows,
                "linux" => Xunit.TestPlatforms.Linux,
                "osx" => Xunit.TestPlatforms.OSX,
                "illumos" => Xunit.TestPlatforms.illumos,
                "solaris" => Xunit.TestPlatforms.Solaris,
                "android" => Xunit.TestPlatforms.Android,
                "ios" => Xunit.TestPlatforms.iOS,
                "tvos" => Xunit.TestPlatforms.tvOS,
                "maccatalyst" => Xunit.TestPlatforms.MacCatalyst,
                "browser" => Xunit.TestPlatforms.Browser,
                "wasi" => Xunit.TestPlatforms.Wasi,
                "freebsd" => Xunit.TestPlatforms.FreeBSD,
                "netbsd" => Xunit.TestPlatforms.NetBSD,
                null or "" or "anyos" => Xunit.TestPlatforms.Any,
                _ => 0
            };
        }
    }

    private static ImmutableArray<ITestInfo> DecorateWithUserDefinedCondition(
        ImmutableArray<ITestInfo> testInfos,
        ITypeSymbol conditionType,
        ImmutableArray<TypedConstant> values,
        string externAlias,
        bool negate)
    {
        string condition = string.Join("&&", values.Select(v => $"{externAlias}::{conditionType.ToDisplayString(FullyQualifiedWithoutGlobalNamespace)}.{v.Value}"));
        if (negate)
            condition = $"!({condition})";
        return ImmutableArray.CreateRange<ITestInfo>(testInfos.Select(m => new ConditionalTest(m, condition)));
    }

    public static readonly SymbolDisplayFormat FullyQualifiedWithoutGlobalNamespace = SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);
}
