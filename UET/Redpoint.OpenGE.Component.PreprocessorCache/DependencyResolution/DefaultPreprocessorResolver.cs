﻿namespace Redpoint.OpenGE.Component.PreprocessorCache.DependencyResolution
{
    using Redpoint.Collections;
    using Redpoint.Concurrency;
    using Redpoint.OpenGE.Component.PreprocessorCache;
    using Redpoint.OpenGE.Component.PreprocessorCache.DirectiveScanner;
    using Redpoint.OpenGE.Component.PreprocessorCache.Filesystem;
    using Redpoint.OpenGE.Component.PreprocessorCache.LexerParser;
    using Redpoint.OpenGE.Protocol;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;

    internal class DefaultPreprocessorResolver : IPreprocessorResolver
    {
        private static readonly StringComparer _pathComparison = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        private readonly IFilesystemExistenceProvider _filesystemExistenceProvider;

        public DefaultPreprocessorResolver(
            IFilesystemExistenceProvider filesystemExistenceProvider)
        {
            _filesystemExistenceProvider = filesystemExistenceProvider;
        }

        private enum PreprocessorConditionState
        {
            Error,
            False,
            True,
        }

        private delegate bool PreprocessorStateHasInclude(string currentPath, string targetPath);

        private class ForwardIncludeScanRequest
        {
            public required PreprocessorState State;
            public required PreprocessorDirectiveInclude Directive;
            public required string Path;
            public required string[] IncludeParentPaths;
            public readonly Gate Completed = new Gate();
            public readonly Gate Started = new Gate();
            public bool Cancelled;
            public string? FoundPath;
            public string? SearchValue;
        }

        private static class ForwardIncludeScanner
        {
            private readonly static ConcurrentDictionary<long, ForwardIncludeScanRequest> _forwardIncludeScanRequestsLookup;
            private readonly static TerminableConcurrentQueue<ForwardIncludeScanRequest> _forwardIncludeScanRequests;
            private readonly static Thread[] _forwardIncludeScanThreads;
            private static long _nextId;

            [SuppressMessage("Performance", "CA1810:Initialize reference type static fields inline", Justification = "Initialisation order must be explicit as threads as started by this constructor.")]
            static ForwardIncludeScanner()
            {
                _forwardIncludeScanRequestsLookup = new ConcurrentDictionary<long, ForwardIncludeScanRequest>();
                _forwardIncludeScanRequests = new TerminableConcurrentQueue<ForwardIncludeScanRequest>();
                _nextId = 1000;
                _forwardIncludeScanThreads = Enumerable.Range(0, 8).Select(x =>
                {
                    var thread = new Thread(ForwardIncludeScanning);
                    thread.Start();
                    return thread;
                }).ToArray();
            }

            public static long EnqueueRequest(ForwardIncludeScanRequest request)
            {
                var allocatedId = Interlocked.Increment(ref _nextId);
                if (!_forwardIncludeScanRequestsLookup.TryAdd(allocatedId, request))
                {
                    throw new InvalidOperationException("Unable to add request to forward scanner");
                }
                _forwardIncludeScanRequests.Enqueue(request);
                return allocatedId;
            }

            public static bool TryPullRequest(long id, [NotNullWhen(true)] out ForwardIncludeScanRequest? request)
            {
                if (_forwardIncludeScanRequestsLookup.TryRemove(id, out var requestInternal))
                {
                    if (requestInternal.Cancelled)
                    {
                        request = null;
                        return false;
                    }

                    request = requestInternal;
                    return true;
                }

                request = null;
                return false;
            }

            private static void ForwardIncludeScanning()
            {
                foreach (var request in _forwardIncludeScanRequests)
                {
                    if (request.Cancelled)
                    {
                        request.Completed.Open();
                        continue;
                    }

                    try
                    {
                        request.Started.Open();
                        request.FoundPath = ResolveIncludeDirective(
                            request.State,
                            request.Directive,
                            request.Path,
                            request.IncludeParentPaths,
                            out request.SearchValue);
                    }
                    catch
                    {
                        request.FoundPath = null;
                    }
                    request.Completed.Open();
                }
            }
        }

        private class PreprocessorState
        {
            public required ICachingPreprocessorScanner Scanner;
            public required string[] IncludeDirectories;
            public readonly Dictionary<string, PreprocessorDirectiveDefine> CurrentDefinitions = new Dictionary<string, PreprocessorDirectiveDefine>();
            public readonly HashSet<string> SeenFiles = new HashSet<string>(_pathComparison);
            public readonly HashSet<string> ReferencedFilesInPch = new HashSet<string>(_pathComparison);
            public readonly HashSet<string> ReferencedFiles = new HashSet<string>(_pathComparison);
            public readonly Dictionary<long, PreprocessorConditionState> ConditionStates = new Dictionary<long, PreprocessorConditionState>();
            public readonly Dictionary<long, PreprocessorCondition> Conditions = new Dictionary<long, PreprocessorCondition>();
            public readonly Dictionary<string, HashSet<long>> ConditionDependentsOfIdentifiers = new Dictionary<string, HashSet<long>>();
            public readonly DependencyGraph<string> DefineGraph = new DependencyGraph<string>();
            public required PreprocessorStateHasInclude HasInclude;
            public required long BuildStartTicks;
            public required IFilesystemExistenceProvider FilesystemExistenceProvider;
        }

        public Task<PreprocessorResolutionResultWithTimingMetadata> ResolveAsync(
            ICachingPreprocessorScanner scanner,
            string path,
            string[] forceIncludes,
            string[] includeDirectories,
            Dictionary<string, string> globalDefinitions,
            long buildStartTicks,
            CompilerArchitype architype,
            CancellationToken cancellationToken)
        {
            if (!Path.IsPathRooted(path))
            {
                throw new ArgumentException($"Path '{path}' must be an absolute path", nameof(path));
            }
            if (forceIncludes.Any(x => !Path.IsPathRooted(x)))
            {
                throw new ArgumentException($"Paths must be absolute", nameof(forceIncludes));
            }
            if (includeDirectories.Any(x => !Path.IsPathRooted(x)))
            {
                throw new ArgumentException($"Paths must be absolute", nameof(includeDirectories));
            }

            var st = Stopwatch.StartNew();

            // Set up the preprocessor definition state.
            var state = new PreprocessorState
            {
                Scanner = scanner,
                IncludeDirectories = includeDirectories,
                HasInclude = (_, _) => false,
                BuildStartTicks = buildStartTicks,
                FilesystemExistenceProvider = _filesystemExistenceProvider,
            };

            // Generate standard defines based on the compiler architype.
            var standardDefines = new Dictionary<string, object>
            {
                { "__cplusplus", 1 },
                { "__FILE__", "__FILE__" },
                { "__LINE__", 0 },
                { "__STDC__", 0 },
            };
            switch (architype.CompilerCase)
            {
                case CompilerArchitype.CompilerOneofCase.Msvc:
                    standardDefines["_MSVC_LANG"] = architype.Msvc.CppLanguageVersion;
                    standardDefines["_MSC_VER"] = architype.Msvc.MsvcVersion;
                    standardDefines["_MSC_FULL_VER"] = architype.Msvc.MsvcFullVersion;
                    standardDefines["_MSVC_WARNING_LEVEL"] = 0;
                    standardDefines["__STDC_VERSION__"] = architype.Msvc.CLanguageVersion;
                    break;
                case CompilerArchitype.CompilerOneofCase.Clang:
                    standardDefines["__clang__"] = 1;
                    standardDefines["__clang_major__"] = architype.Clang.MajorVersion;
                    standardDefines["__clang_minor__"] = architype.Clang.MinorVersion;
                    standardDefines["__clang_patchlevel__"] = architype.Clang.PatchVersion;
                    standardDefines["_MSC_VER"] = architype.Clang.EmulatedMsvcVersion;
                    standardDefines["_MSC_FULL_VER"] = architype.Clang.EmulatedMsvcFullVersion;
                    standardDefines["_MSVC_LANG"] = architype.Clang.CppLanguageVersion;
                    standardDefines["__STDC_VERSION__"] = architype.Clang.CLanguageVersion;
                    break;
            }
            foreach (var strKv in architype.TargetPlatformStringDefines)
            {
                standardDefines[strKv.Key] = strKv.Value;
            }
            foreach (var numKv in architype.TargetPlatformNumericDefines)
            {
                standardDefines[numKv.Key] = numKv.Value;
            }
            foreach (var kv in standardDefines)
            {
                state.CurrentDefinitions[kv.Key] =
                    new PreprocessorDirectiveDefine
                    {
                        Identifier = kv.Key,
                        Expansion = new PreprocessorExpression
                        {
                            Token = kv.Value switch
                            {
                                string s => new PreprocessorExpressionToken { Text = s },
                                int i => new PreprocessorExpressionToken { Number = i },
                                long i => new PreprocessorExpressionToken { Number = i },
                                _ => throw new NotSupportedException(),
                            },
                        },
                        IsFunction = false,
                    };
            }

            // Add the custom global definitions.
            foreach (var globalDefinition in globalDefinitions)
            {
                state.CurrentDefinitions[globalDefinition.Key] =
                    new PreprocessorDirectiveDefine
                    {
                        Identifier = globalDefinition.Key,
                        IsFunction = false,
                        Expansion = PreprocessorExpressionParser.ParseExpansion(
                            PreprocessorExpressionLexer.Lex(globalDefinition.Value))
                    };
            }

            // Process all of the root files that are forced included
            // (but are not part of the PCH).
            foreach (var rootFile in forceIncludes)
            {
                try
                {
                    var stack = new Stack<string>();
                    stack.Push(path);
                    state.HasInclude = HasIncludeWithStack(state, stack);
                    ProcessFile(
                        state,
                        rootFile,
                        stack,
                        state.ReferencedFiles,
                        cancellationToken);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    throw new PreprocessorResolutionException(rootFile, ex);
                }
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Process the root file.
            try
            {
                var stack = new Stack<string>();
                state.HasInclude = HasIncludeWithStack(state, stack);
                ProcessFile(
                    state,
                    path,
                    stack,
                    state.ReferencedFiles,
                    cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                throw new PreprocessorResolutionException(path, ex);
            }
            cancellationToken.ThrowIfCancellationRequested();

            // Exclude files that were also referenced from the PCH.
            state.ReferencedFiles.ExceptWith(state.ReferencedFilesInPch);

            // We've finished processing.
            var result = new PreprocessorResolutionResultWithTimingMetadata
            {
                ResolutionTimeMs = st.ElapsedMilliseconds,
            };
            result.DependsOnPaths.AddRange(state.ReferencedFiles);
            return Task.FromResult(result);
        }

        private static PreprocessorStateHasInclude HasIncludeWithStack(
            PreprocessorState state,
            Stack<string> includeParentPaths)
        {
            return (currentPath, targetPath) =>
            {
                PreprocessorDirectiveInclude.IncludeOneofCase effectiveCase;
                string[] searchPaths;
                string searchValue;
                if (targetPath.StartsWith('"'))
                {
                    effectiveCase = PreprocessorDirectiveInclude.IncludeOneofCase.Normal;
                    searchPaths = state.IncludeDirectories;
                    searchValue = targetPath.Trim('"');
                }
                else if (targetPath.StartsWith('<'))
                {
                    effectiveCase = PreprocessorDirectiveInclude.IncludeOneofCase.System;
                    searchPaths = state.IncludeDirectories;
                    searchValue = targetPath.TrimStart('<').TrimEnd('>');
                }
                else
                {
                    // No idea what this include is.
                    return false;
                }
                string? foundPath = FindInclude(
                    state,
                    currentPath,
                    includeParentPaths,
                    effectiveCase,
                    searchPaths,
                    searchValue);
                return foundPath != null;
            };
        }

        private static PreprocessorConditionState EvaluateErrorableCondition(
            PreprocessorState state,
            string currentPath,
            PreprocessorExpression condition)
        {
            try
            {
                return EvaluateCondition(state, currentPath, condition) != 0
                    ? PreprocessorConditionState.True
                    : PreprocessorConditionState.False;
            }
            catch (PreprocessorIdentifierNotDefinedException)
            {
                return PreprocessorConditionState.Error;
            }
        }

        private static long EvaluateCondition(
            PreprocessorState state,
            string currentPath,
            PreprocessorExpression condition)
        {
            switch (condition.ExprCase)
            {
                case PreprocessorExpression.ExprOneofCase.Invoke:
                    if (condition.Invoke.Identifier == "__has_warning" ||
                        condition.Invoke.Identifier == "__has_feature")
                    {
                        // Clang special macro functions, we just always treat these as false.
                        return 0;
                    }
                    else if (condition.Invoke.Identifier == "defined" &&
                        condition.Invoke.Arguments.Count == 1 &&
                        condition.Invoke.Arguments[0].ExprCase == PreprocessorExpression.ExprOneofCase.Token &&
                        condition.Invoke.Arguments[0].Token.DataCase == PreprocessorExpressionToken.DataOneofCase.Identifier)
                    {
                        return state.CurrentDefinitions.ContainsKey(condition.Invoke.Arguments[0].Token.Identifier) ? 1 : 0;
                    }
                    else if (condition.Invoke.Identifier == "defined" &&
                        condition.Invoke.Arguments.Count == 1 &&
                        condition.Invoke.Arguments[0].ExprCase == PreprocessorExpression.ExprOneofCase.Chain)
                    {
                        var buffer = string.Empty;
                        var exprs = condition.Invoke.Arguments[0].Chain.Expressions;
                        int e = 0;
                        while (e < exprs.Count && exprs[e].ExprCase == PreprocessorExpression.ExprOneofCase.Whitespace)
                        {
                            // Skip leading whitespace.
                            e++;
                        }
                        while (e < exprs.Count && exprs[e].ExprCase == PreprocessorExpression.ExprOneofCase.Token &&
                            exprs[e].Token.DataCase == PreprocessorExpressionToken.DataOneofCase.Identifier)
                        {
                            buffer += exprs[e].Token.Identifier;
                            e++;
                        }
                        while (e < exprs.Count && exprs[e].ExprCase == PreprocessorExpression.ExprOneofCase.Whitespace)
                        {
                            // Skip trailing whitespace.
                            e++;
                        }
                        // If we haven't consumed everything now, then we've got multiple non-identifier tokens
                        // in the argument, potentially separated by whitespace, which isn't valid.
                        if (e != exprs.Count)
                        {
                            throw new PreprocessorIdentifierNotDefinedException($"Badly formed defined() expression uses chain '{condition.Invoke.Arguments[0].Chain}' as argument.");
                        }
                        return state.CurrentDefinitions.ContainsKey(buffer) ? 1 : 0;
                    }
                    else if (!state.CurrentDefinitions.TryGetValue(condition.Invoke.Identifier, out PreprocessorDirectiveDefine? define))
                    {
                        throw new PreprocessorIdentifierNotDefinedException($"Macro function identifier '{condition.Invoke.Identifier}' is not defined during condition.");
                    }
                    else
                    {
                        // Expand the function, then re-parse and evaluate
                        // it as an expression.
                        Dictionary<string, PreprocessorExpression> replacements = ComputeReplacements(
                            state,
                            condition.Invoke,
                            new Dictionary<string, PreprocessorExpression>());
                        var expanded = EvaluateExpansion(
                            state,
                            define.Expansion,
                            replacements);
                        return EvaluateCondition(
                            state,
                            currentPath,
                            PreprocessorExpressionParser.ParseCondition(
                                PreprocessorExpressionLexer.Lex(expanded)));
                    }
                case PreprocessorExpression.ExprOneofCase.Token:
                    switch (condition.Token.DataCase)
                    {
                        case PreprocessorExpressionToken.DataOneofCase.Identifier:
                            if (!state.CurrentDefinitions.TryGetValue(condition.Token.Identifier, out PreprocessorDirectiveDefine? define))
                            {
                                // The C/C++ standard says that undefined preprocessor identifiers
                                // evaluate to 0 by default.
                                return 0;
                            }

                            // Expand the identifier, then re-parse and evaluate
                            // it as an expression.
                            var expanded = EvaluateExpansion(
                                state,
                                define.Expansion,
                                new Dictionary<string, PreprocessorExpression>());
                            return EvaluateCondition(
                                state,
                                currentPath,
                                PreprocessorExpressionParser.ParseCondition(
                                    PreprocessorExpressionLexer.Lex(expanded)));
                        case PreprocessorExpressionToken.DataOneofCase.Text:
                            // @note: Any text value is truthy.
                            return 1;
                        case PreprocessorExpressionToken.DataOneofCase.Number:
                            return condition.Token.Number;
                        case PreprocessorExpressionToken.DataOneofCase.Whitespace:
                            // @note: Whitespace is not truthy.
                            return 0;
                        case PreprocessorExpressionToken.DataOneofCase.Type:
                            // @note: I guess this is truthy? What?
                            return 1;
                        default:
                            throw new InvalidOperationException($"Unsupported DataCase '{condition.Token.DataCase}'.");
                    }
                case PreprocessorExpression.ExprOneofCase.Chain:
                    if (condition.Chain.Expressions.Count == 1)
                    {
                        return EvaluateCondition(state, currentPath, condition.Chain.Expressions[0]);
                    }
                    else
                    {
                        throw new InvalidOperationException($"Chain in conditional expression contained more than one subexpression.");
                    }
                case PreprocessorExpression.ExprOneofCase.Unary:
                    switch (condition.Unary.Type)
                    {
                        case PreprocessorExpressionTokenType.BitwiseNot:
                            return ~EvaluateCondition(state, currentPath, condition.Unary.Expression);
                        case PreprocessorExpressionTokenType.LogicalNot:
                            return EvaluateCondition(state, currentPath, condition.Unary.Expression) == 0 ? 1 : 0;
                        case PreprocessorExpressionTokenType.Stringify:
                            // @note: This will turn 0 into "0", which is truthy.
                            return 1;
                        default:
                            throw new NotSupportedException($"Unsupported Unary Type '{condition.Unary.Type}'");
                    }
                case PreprocessorExpression.ExprOneofCase.Binary:
                    var lhs = EvaluateCondition(state, currentPath, condition.Binary.Left);
                    switch (condition.Binary.Type)
                    {
                        // Short circuit evaluation
                        case PreprocessorExpressionTokenType.LogicalAnd:
                            if (lhs == 0)
                            {
                                return 0;
                            }
                            break;
                        case PreprocessorExpressionTokenType.LogicalOr:
                            if (lhs != 0)
                            {
                                return 1;
                            }
                            break;
                    }
                    var rhs = EvaluateCondition(state, currentPath, condition.Binary.Right);
                    switch (condition.Binary.Type)
                    {
                        case PreprocessorExpressionTokenType.Multiply:
                            return lhs * rhs;
                        case PreprocessorExpressionTokenType.Divide:
                            return rhs == 0 ? 0 : (lhs / rhs);
                        case PreprocessorExpressionTokenType.Modulus:
                            return rhs == 0 ? 0 : (lhs % rhs);
                        case PreprocessorExpressionTokenType.Add:
                            return lhs + rhs;
                        case PreprocessorExpressionTokenType.Subtract:
                            return lhs - rhs;
                        case PreprocessorExpressionTokenType.LeftShift:
                            return lhs << (int)rhs;
                        case PreprocessorExpressionTokenType.RightShift:
                            return lhs >> (int)rhs;
                        case PreprocessorExpressionTokenType.LessThan:
                            return lhs < rhs ? 1 : 0;
                        case PreprocessorExpressionTokenType.GreaterThan:
                            return lhs > rhs ? 1 : 0;
                        case PreprocessorExpressionTokenType.LessEquals:
                            return lhs <= rhs ? 1 : 0;
                        case PreprocessorExpressionTokenType.GreaterEquals:
                            return lhs >= rhs ? 1 : 0;
                        case PreprocessorExpressionTokenType.Equals:
                            return lhs == rhs ? 1 : 0;
                        case PreprocessorExpressionTokenType.NotEquals:
                            return lhs != rhs ? 1 : 0;
                        case PreprocessorExpressionTokenType.BitwiseAnd:
                            return lhs & rhs;
                        case PreprocessorExpressionTokenType.BitwiseXor:
                            return lhs ^ rhs;
                        case PreprocessorExpressionTokenType.BitwiseOr:
                            return lhs | rhs;
                        case PreprocessorExpressionTokenType.LogicalAnd:
                            return lhs != 0 && rhs != 0 ? 1 : 0;
                        case PreprocessorExpressionTokenType.LogicalOr:
                            return lhs != 0 || rhs != 0 ? 1 : 0;
                        default:
                            throw new NotSupportedException($"Unsupported Binary Type '{condition.Binary.Type}'");
                    }
                case PreprocessorExpression.ExprOneofCase.Defined:
                    return state.CurrentDefinitions.ContainsKey(condition.Defined) ? 1 : 0;
                case PreprocessorExpression.ExprOneofCase.HasInclude:
                    return state.HasInclude(currentPath, condition.HasInclude) ? 1 : 0;
                default:
                    throw new NotSupportedException($"Unsupported Expr Type '{condition.ExprCase}'");
            }
        }

        private static PreprocessorExpression EvaluateNonrecursiveExpansion(
            PreprocessorState state,
            PreprocessorExpression original,
            Dictionary<string, PreprocessorExpression> replacements)
        {
            switch (original.ExprCase)
            {
                case PreprocessorExpression.ExprOneofCase.Unary:
                    return new PreprocessorExpression
                    {
                        Unary = new PreprocessorExpressionUnary
                        {
                            Type = original.Unary.Type,
                            Expression = EvaluateNonrecursiveExpansion(state, original.Unary.Expression, replacements)
                        }
                    };
                case PreprocessorExpression.ExprOneofCase.Whitespace:
                case PreprocessorExpression.ExprOneofCase.Defined:
                case PreprocessorExpression.ExprOneofCase.HasInclude:
                    return original;
                case PreprocessorExpression.ExprOneofCase.Invoke:
                    break;
                    // @todo: Finish this function. It needs to expand arguments without 
                    // resolving tokens.
            }

            throw new NotImplementedException();
        }

        private static Dictionary<string, PreprocessorExpression> ComputeReplacements(
            PreprocessorState state,
            PreprocessorExpressionInvoke invoke,
            Dictionary<string, PreprocessorExpression> upstreamReplacements)
        {
            var replacements = new Dictionary<string, PreprocessorExpression>();
            for (int i = 0; i < state.CurrentDefinitions[invoke.Identifier].Parameters.Count; i++)
            {
                var parameter = state.CurrentDefinitions[invoke.Identifier].Parameters[i];
                if (parameter == "...")
                {
                    // Replace as __VA_ARGS__
                    var chain = new PreprocessorExpressionChain();
                    for (int a = i; a < invoke.Arguments.Count; a++)
                    {
                        if (chain.Expressions.Count > 0)
                        {
                            chain.Expressions.Add(new PreprocessorExpression
                            {
                                Token = new PreprocessorExpressionToken
                                {
                                    Type = PreprocessorExpressionTokenType.Comma,
                                }
                            });
                        }
                        chain.Expressions.Add(
                            EvaluateNonrecursiveExpansion(
                                state,
                                invoke.Arguments[a],
                                upstreamReplacements));
                    }
                    replacements.Add("__VA_ARGS__", new PreprocessorExpression { Chain = chain });
                }
                else if (i < invoke.Arguments.Count)
                {
                    replacements.Add(
                        parameter,
                        EvaluateNonrecursiveExpansion(
                            state,
                            invoke.Arguments[i],
                            upstreamReplacements));
                }
                else
                {
                    replacements.Add(parameter, new PreprocessorExpression { Chain = new PreprocessorExpressionChain() });
                }
            }
            return replacements;
        }

        private static string RenderTokenType(PreprocessorExpressionTokenType tokenType)
        {
            return PreprocessorExpressionParser._literalMappings[tokenType];
        }

        private static string EvaluateExpansion(
            PreprocessorState state,
            PreprocessorExpression expression,
            Dictionary<string, PreprocessorExpression> localReplacements)
        {
            switch (expression.ExprCase)
            {
                case PreprocessorExpression.ExprOneofCase.Binary:
                    var lhs = EvaluateExpansion(state, expression.Binary.Left, localReplacements);
                    var rhs = EvaluateExpansion(state, expression.Binary.Right, localReplacements);
                    if (expression.Binary.Type == PreprocessorExpressionTokenType.Join)
                    {
                        return $"{lhs}{rhs}";
                    }
                    return $"({lhs}{RenderTokenType(expression.Binary.Type)}{rhs})";
                case PreprocessorExpression.ExprOneofCase.Unary:
                    var expr = EvaluateExpansion(state, expression.Unary.Expression, localReplacements);
                    if (expression.Unary.Type == PreprocessorExpressionTokenType.Stringify)
                    {
                        return $"\"{expr}\"";
                    }
                    return $"({RenderTokenType(expression.Unary.Type)}{expr})";
                case PreprocessorExpression.ExprOneofCase.Chain:
                    var buffer = string.Empty;
                    for (int e = 0; e < expression.Chain.Expressions.Count; e++)
                    {
                        var subexpr = expression.Chain.Expressions[e];
                        if (subexpr.ExprCase == PreprocessorExpression.ExprOneofCase.Token &&
                            subexpr.Token.Type == PreprocessorExpressionTokenType.Join)
                        {
                            buffer = buffer.TrimEnd();
                            e++;
                            if (e == expression.Chain.Expressions.Count) { break; }
                            var next = EvaluateExpansion(
                                state,
                                expression.Chain.Expressions[e],
                                localReplacements);
                            while (string.IsNullOrWhiteSpace(next))
                            {
                                e++;
                                if (e == expression.Chain.Expressions.Count) { break; }
                                next = EvaluateExpansion(
                                    state,
                                    expression.Chain.Expressions[e],
                                    localReplacements);
                            }
                            if (e == expression.Chain.Expressions.Count) { break; }
                            buffer += next.TrimStart();
                            e++;
                        }
                        else
                        {
                            buffer += EvaluateExpansion(
                                state,
                                expression.Chain.Expressions[e],
                                localReplacements);
                        }
                    }
                    return buffer;
                case PreprocessorExpression.ExprOneofCase.Whitespace:
                    return expression.Whitespace;
                case PreprocessorExpression.ExprOneofCase.Invoke:
                    if (state.CurrentDefinitions.TryGetValue(expression.Invoke.Identifier, out PreprocessorDirectiveDefine? define))
                    {
                        if (define.IsFunction)
                        {
                            Dictionary<string, PreprocessorExpression> replacements = ComputeReplacements(state, expression.Invoke, localReplacements);
                            return EvaluateExpansion(
                                state,
                                define.Expansion,
                                replacements);
                        }
                    }
                    return $"{expression.Invoke.Identifier}({string.Join(",", expression.Invoke.Arguments.Select(x => EvaluateExpansion(state, x, new Dictionary<string, PreprocessorExpression>())))})";
                case PreprocessorExpression.ExprOneofCase.Token:
                    switch (expression.Token.DataCase)
                    {
                        case PreprocessorExpressionToken.DataOneofCase.Type:
                            return RenderTokenType(expression.Token.Type);
                        case PreprocessorExpressionToken.DataOneofCase.Text:
                            return expression.Token.Text;
                        case PreprocessorExpressionToken.DataOneofCase.Identifier:
                            PreprocessorExpression expressionSource;
                            if (localReplacements.TryGetValue(expression.Token.Identifier, out PreprocessorExpression? localReplacement))
                            {
                                expressionSource = localReplacement;
                            }
                            else if (state.CurrentDefinitions.TryGetValue(expression.Token.Identifier, out PreprocessorDirectiveDefine? lookupDefine))
                            {
                                if (lookupDefine.IsFunction)
                                {
                                    // In this case, it's treated as-is.
                                    return expression.Token.Identifier;
                                }
                                else
                                {
                                    expressionSource = lookupDefine.Expansion;
                                }
                            }
                            else
                            {
                                return expression.Token.Identifier;
                            }
                            return EvaluateExpansion(
                                state,
                                expressionSource,
                                // @note: Local replacements don't propagate.
                                new Dictionary<string, PreprocessorExpression>());
                        case PreprocessorExpressionToken.DataOneofCase.Number:
                            return $"{expression.Token.Number}";
                        default:
                            throw new InvalidOperationException($"Unsupported DataCase '{expression.Token.DataCase}'.");
                    }
                default:
                    throw new InvalidOperationException($"Unsupported ExprCase '{expression.ExprCase}'.");
            }
        }

        private static void GatherDependentConditionsFromDefinitionChange(
            PreprocessorState state,
            string identifier,
            HashSet<long> dependentConditions)
        {
            // Add immediate condition dependents.
            if (state.ConditionDependentsOfIdentifiers.TryGetValue(identifier, out var value))
            {
                dependentConditions.UnionWith(value);
            }

            // If no definition depends on the value of this define, we only need
            // to worry about the immediate conditions.
            if (state.DefineGraph.WhatDependsOnTarget(identifier).Count == 0)
            {
                return;
            }

            // Recursively evaluate upstream defines in case they are dependents
            // of conditions.
            var upstreamDefines = new HashSet<string>();
            state.DefineGraph.WhatDependsOnTargetRecursive(identifier, upstreamDefines);
            foreach (var upstreamDefine in upstreamDefines)
            {
                // Add upstream condition dependents.
                if (state.ConditionDependentsOfIdentifiers.TryGetValue(upstreamDefine, out var upstreamValue))
                {
                    dependentConditions.UnionWith(upstreamValue);
                }
            }
        }

        private static void ProcessFile(
            PreprocessorState state,
            string path,
            Stack<string> includeParentPaths,
            HashSet<string> referencedFiles,
            CancellationToken cancellationToken)
        {
            // Normalize the path.
            path = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            // Add this file to the seen files list.
            state.SeenFiles.Add(path);

            // Add this file to the referenced files list.
            referencedFiles.Add(path);

            // Get the unresolved data from the cache.
            var unresolvedState = state.Scanner.ParseIncludes(path);
            cancellationToken.ThrowIfCancellationRequested();

            // For each condition this file depends on, add it to
            // the condition state.
            foreach (var condition in unresolvedState.Result.Conditions)
            {
                if (state.Conditions.TryAdd(condition.ConditionHash, condition))
                {
                    state.ConditionStates.Add(condition.ConditionHash, EvaluateErrorableCondition(state, path, condition.Condition));
                    foreach (var identifier in condition.DependentOnIdentifiers)
                    {
                        if (!state.ConditionDependentsOfIdentifiers.TryGetValue(identifier, out HashSet<long>? dependents))
                        {
                            dependents = new HashSet<long>();
                            state.ConditionDependentsOfIdentifiers.Add(identifier, dependents);
                        }

                        dependents.Add(condition.ConditionHash);
                    }
                }
            }

            // For all of the include directives, push them into the forward include scanner
            // so that we can start working on them while processing this file.
            string[]? includeParentPathsLocked = null;
            Dictionary<long, long>? forwardScannerLookup = null;
            void PreloadDirectiveRecursive(PreprocessorDirective directive)
            {
                switch (directive.DirectiveCase)
                {
                    case PreprocessorDirective.DirectiveOneofCase.Include:
                        if (directive.Include.IncludeCase != PreprocessorDirectiveInclude.IncludeOneofCase.Expansion)
                        {
                            if (includeParentPathsLocked == null)
                            {
                                includeParentPathsLocked = includeParentPaths.ToArray();
                            }
                            if (forwardScannerLookup == null)
                            {
                                forwardScannerLookup = new Dictionary<long, long>();
                            }
                            var request = new ForwardIncludeScanRequest
                            {
                                State = state,
                                Directive = directive.Include,
                                Path = path,
                                IncludeParentPaths = includeParentPathsLocked,
                            };
                            forwardScannerLookup[directive.Include.DirectiveId] = ForwardIncludeScanner.EnqueueRequest(request);
                        }
                        break;
                    case PreprocessorDirective.DirectiveOneofCase.If:
                        foreach (var subdir in directive.If.Subdirectives)
                        {
                            PreloadDirectiveRecursive(subdir);
                        }
                        if (directive.If.HasElseBranch)
                        {
                            PreloadDirectiveRecursive(directive.If.ElseBranch);
                        }
                        break;
                    case PreprocessorDirective.DirectiveOneofCase.Block:
                        foreach (var subdir in directive.Block.Subdirectives)
                        {
                            PreloadDirectiveRecursive(subdir);
                        }
                        break;
                }
            }

            // Process each directive in order.
            for (int directiveIndex = 0; directiveIndex < unresolvedState.Result.Directives.Count; directiveIndex++)
            {
                var directive = unresolvedState.Result.Directives[directiveIndex];
                ProcessDirective(
                    state,
                    path,
                    includeParentPaths,
                    referencedFiles,
                    directive,
                    forwardScannerLookup,
                    cancellationToken);
            }

            // Go and cancel any include requests that are still outstanding (since we 
            // didn't end up waiting on them).
            if (forwardScannerLookup != null)
            {
                foreach (var id in forwardScannerLookup.Values)
                {
                    if (ForwardIncludeScanner.TryPullRequest(id, out var request))
                    {
                        request.Cancelled = true;
                    }
                }
            }
        }

        private static void ProcessDirective(
            PreprocessorState state,
            string path,
            Stack<string> includeParentPaths,
            HashSet<string> referencedFiles,
            PreprocessorDirective directive,
            Dictionary<long, long>? forwardScannerLookup,
            CancellationToken cancellationToken)
        {
            switch (directive.DirectiveCase)
            {
                case PreprocessorDirective.DirectiveOneofCase.None:
                    throw new NotSupportedException("Unexpected 'None' directive in cache!");
                case PreprocessorDirective.DirectiveOneofCase.If:
                    {
                        var conditionHash = directive.If.ConditionHash;
                        var conditionState = state.ConditionStates[conditionHash];
                        if (conditionState == PreprocessorConditionState.Error)
                        {
                            throw new InvalidOperationException($"Preprocessor condition {conditionHash} depends on an undefined macro identifier for condition evaluation. The condition is: {state.Conditions[conditionHash].Condition}");
                        }
                        else if (conditionState == PreprocessorConditionState.True)
                        {
                            // Process the nested directives.
                            foreach (var subdirective in directive.If.Subdirectives)
                            {
                                ProcessDirective(
                                    state,
                                    path,
                                    includeParentPaths,
                                    referencedFiles,
                                    subdirective,
                                    forwardScannerLookup,
                                    cancellationToken);
                            }
                        }
                        else if (directive.If.HasElseBranch)
                        {
                            // Process the else branch if it exists.
                            ProcessDirective(
                                state,
                                path,
                                includeParentPaths,
                                referencedFiles,
                                directive.If.ElseBranch,
                                forwardScannerLookup,
                                cancellationToken);
                        }
                        break;
                    }
                case PreprocessorDirective.DirectiveOneofCase.Block:
                    {
                        // Process the nested directives. This directive is only
                        // used for unconditional else blocks.
                        foreach (var subdirective in directive.Block.Subdirectives)
                        {
                            ProcessDirective(
                                state,
                                path,
                                includeParentPaths,
                                referencedFiles,
                                subdirective,
                                forwardScannerLookup,
                                cancellationToken);
                        }
                        break;
                    }
                case PreprocessorDirective.DirectiveOneofCase.Define:
                    {
                        var dependentConditions = new HashSet<long>();
                        GatherDependentConditionsFromDefinitionChange(state, directive.Define.Identifier, dependentConditions);
                        state.CurrentDefinitions[directive.Define.Identifier] = directive.Define;
                        state.DefineGraph.SetDependsOn(directive.Define.Identifier, directive.ReferencedIdentifiers);
                        GatherDependentConditionsFromDefinitionChange(state, directive.Define.Identifier, dependentConditions);
                        foreach (var conditionHash in dependentConditions)
                        {
                            state.ConditionStates[conditionHash] = EvaluateErrorableCondition(state, path, state.Conditions[conditionHash].Condition);
                        }
                        break;
                    }
                case PreprocessorDirective.DirectiveOneofCase.Undefine:
                    {
                        var dependentConditions = new HashSet<long>();
                        GatherDependentConditionsFromDefinitionChange(state, directive.Undefine.Identifier, dependentConditions);
                        state.CurrentDefinitions.Remove(directive.Undefine.Identifier);
                        state.DefineGraph.SetDependsOn(directive.Undefine.Identifier, Array.Empty<string>());
                        foreach (var conditionHash in dependentConditions)
                        {
                            state.ConditionStates[conditionHash] = EvaluateErrorableCondition(state, path, state.Conditions[conditionHash].Condition);
                        }
                        break;
                    }
                case PreprocessorDirective.DirectiveOneofCase.Include:
                    string? foundPath;
                    string? searchValue;
                    if (forwardScannerLookup != null &&
                        forwardScannerLookup.TryGetValue(directive.Include.DirectiveId, out var foundScanResult) &&
                        ForwardIncludeScanner.TryPullRequest(foundScanResult, out var request))
                    {
                        if (request.Completed.TryWait(0, CancellationToken.None))
                        {
                            foundPath = request.FoundPath;
                            searchValue = request.SearchValue;
                        }
                        else if (request.Started.TryWait(0, CancellationToken.None))
                        {
                            // The forward scanner has started doing the work (i.e. it's not
                            // still in the request queue), so actually just wait for it to be
                            // done.
                            request.Completed.Wait(CancellationToken.None);
                            foundPath = request.FoundPath;
                            searchValue = request.SearchValue;
                        }
                        else
                        {
                            // Cancel the request.
                            request.Cancelled = true;

                            // Do the work ourselves.
                            foundPath = ResolveIncludeDirective(
                                state,
                                directive.Include,
                                path,
                                includeParentPaths,
                                out searchValue);
                        }
                    }
                    else
                    {
                        foundPath = ResolveIncludeDirective(
                            state,
                            directive.Include,
                            path,
                            includeParentPaths,
                            out searchValue);
                    }
                    if (foundPath == null)
                    {
                        // Unable to find this file.
                        throw new PreprocessorIncludeNotFoundException(searchValue!);
                    }
                    if (state.SeenFiles.Contains(foundPath))
                    {
                        // We don't need to reprocess this file again.
                        break;
                    }
                    // Process this include file.
                    includeParentPaths.Push(path);
                    try
                    {
                        ProcessFile(
                            state,
                            foundPath,
                            includeParentPaths,
                            referencedFiles,
                            cancellationToken);
                    }
                    catch (Exception ex) when (!(ex is OperationCanceledException))
                    {
                        throw new PreprocessorResolutionException(foundPath, ex);
                    }
                    finally
                    {
                        includeParentPaths.Pop();
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported DirectiveCase '{directive.DirectiveCase}' directive in cache!");
            }
        }

        private static string? ResolveIncludeDirective(
            PreprocessorState state,
            PreprocessorDirectiveInclude directive,
            string path,
            IEnumerable<string> includeParentPaths,
            out string searchValue)
        {
            PreprocessorDirectiveInclude.IncludeOneofCase effectiveCase;
            string effectiveValue;
            switch (directive.IncludeCase)
            {
                case PreprocessorDirectiveInclude.IncludeOneofCase.Normal:
                    effectiveCase = PreprocessorDirectiveInclude.IncludeOneofCase.Normal;
                    effectiveValue = directive.Normal;
                    break;
                case PreprocessorDirectiveInclude.IncludeOneofCase.System:
                    effectiveCase = PreprocessorDirectiveInclude.IncludeOneofCase.System;
                    effectiveValue = directive.System;
                    break;
                case PreprocessorDirectiveInclude.IncludeOneofCase.Expansion:
                    var expandedInclude = EvaluateExpansion(
                        state,
                        directive.Expansion,
                        new Dictionary<string, PreprocessorExpression>()).Trim();
                    if (expandedInclude.StartsWith('"'))
                    {
                        effectiveCase = PreprocessorDirectiveInclude.IncludeOneofCase.Normal;
                        effectiveValue = expandedInclude.Trim('"').Trim();
                    }
                    else if (expandedInclude.StartsWith('<'))
                    {
                        effectiveCase = PreprocessorDirectiveInclude.IncludeOneofCase.System;
                        effectiveValue = expandedInclude.TrimStart('<').TrimEnd('>').Trim();
                    }
                    else
                    {
                        throw new InvalidOperationException($"Include macro expanded into non-include value '{expandedInclude}' (it probably needs quotes)");
                    }
                    break;
                default:
                    throw new NotSupportedException($"Unsupported IncludeCase '{directive.IncludeCase}' in cache");
            }
            string[] searchPaths;
            switch (effectiveCase)
            {
                case PreprocessorDirectiveInclude.IncludeOneofCase.Normal:
                    searchPaths = state.IncludeDirectories;
                    searchValue = effectiveValue;
                    break;
                case PreprocessorDirectiveInclude.IncludeOneofCase.System:
                    searchPaths = state.IncludeDirectories;
                    searchValue = effectiveValue;
                    break;
                default:
                    throw new NotSupportedException($"Unsupported post-eval IncludeCase '{directive.IncludeCase}' in cache");
            }
            return FindInclude(state, path, includeParentPaths, effectiveCase, searchPaths, searchValue);
        }

        private static bool ConsiderPath(
            PreprocessorState state,
            string consideredPath,
            [NotNullWhen(true)] out string? foundPath)
        {
            consideredPath = consideredPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (state.FilesystemExistenceProvider.FileExists(consideredPath, state.BuildStartTicks))
            {
                foundPath = consideredPath;
                return true;
            }
            foundPath = null;
            return false;
        }

        private static string? FindInclude(
            PreprocessorState state,
            string path,
            IEnumerable<string> includeParentPaths,
            PreprocessorDirectiveInclude.IncludeOneofCase effectiveCase,
            string[] searchPaths,
            string searchValue)
        {
            string? foundPath = null;
            if (effectiveCase != PreprocessorDirectiveInclude.IncludeOneofCase.System)
            {
                // Relative to current file.
                {
                    ConsiderPath(
                        state,
                        Path.Combine(Path.GetDirectoryName(path)!, searchValue),
                        out foundPath);
                }

                // Relative to parent files.
                if (foundPath == null)
                {
                    foreach (var parentFile in includeParentPaths)
                    {
                        if (ConsiderPath(
                            state,
                            Path.Combine(Path.GetDirectoryName(parentFile)!, searchValue),
                            out foundPath))
                        {
                            break;
                        }
                    }
                }
            }
            if (foundPath == null)
            {
                foreach (var searchPath in searchPaths)
                {
                    if (ConsiderPath(
                        state,
                        Path.Combine(searchPath, searchValue),
                        out foundPath))
                    {
                        break;
                    }
                }
            }
            return foundPath;
        }
    }
}
