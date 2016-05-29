// Copyright 2016 Robin Sue
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace SerilogAnalyzer
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class SerilogAnalyzerAnalyzer : DiagnosticAnalyzer
    {
        public const string ExceptionDiagnosticId = "SerilogExceptionUsageAnalyzer";
        private static readonly LocalizableString ExceptionTitle = new LocalizableResourceString(nameof(Resources.ExceptionAnalyzerTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString ExceptionMessageFormat = new LocalizableResourceString(nameof(Resources.ExceptionAnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString ExceptionDescription = new LocalizableResourceString(nameof(Resources.ExceptionAnalyzerDescription), Resources.ResourceManager, typeof(Resources));
        private static DiagnosticDescriptor ExceptionRule = new DiagnosticDescriptor(ExceptionDiagnosticId, ExceptionTitle, ExceptionMessageFormat, "CodeQuality", DiagnosticSeverity.Warning, isEnabledByDefault: true, description: ExceptionDescription);

        public const string TemplateDiagnosticId = "SerilogMessageTemplateAnalyzer";
        private static readonly LocalizableString TemplateTitle = new LocalizableResourceString(nameof(Resources.TemplateAnalyzerTitle), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString TemplateMessageFormat = new LocalizableResourceString(nameof(Resources.TemplateAnalyzerMessageFormat), Resources.ResourceManager, typeof(Resources));
        private static readonly LocalizableString TemplateDescription = new LocalizableResourceString(nameof(Resources.TemplateAnalyzerDescription), Resources.ResourceManager, typeof(Resources));
        private static DiagnosticDescriptor TemplateRule = new DiagnosticDescriptor(TemplateDiagnosticId, TemplateTitle, TemplateMessageFormat, "CodeQuality", DiagnosticSeverity.Error, isEnabledByDefault: true, description: ExceptionDescription);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get { return ImmutableArray.Create(ExceptionRule, TemplateRule); } }

        public override void Initialize(AnalysisContext context)
        {
            context.RegisterSyntaxNodeAction(AnalyzeSymbol, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeSymbol(SyntaxNodeAnalysisContext context)
        {
            var invocation = context.Node as InvocationExpressionSyntax;
            var info = context.SemanticModel.GetSymbolInfo(invocation);
            var method = info.Symbol as IMethodSymbol;
            if (method == null)
            {
                return;
            }

            // is it a serilog logging method?
            var attributes = method.GetAttributes();
            var messageTemplateAttribute = context.SemanticModel.Compilation.GetTypeByMetadataName("Serilog.Core.MessageTemplateFormatMethodAttribute");
            var attributeData = attributes.FirstOrDefault(x => x.AttributeClass == messageTemplateAttribute);
            if (attributeData == null)
            {
                return;
            }

            string messageTemplateName = attributeData.ConstructorArguments.First().Value as string;

            // check for errors in the MessageTemplate
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                var paramter = DetermineParameter(argument, context.SemanticModel, true, context.CancellationToken);
                if (paramter.Name == messageTemplateName)
                {
                    var literal = argument.Expression as LiteralExpressionSyntax;
                    if (literal == null)
                    {
                        continue;
                    }

                    var literalSpan = literal.GetLocation().SourceSpan;

                    var messageTemplateDiagnostics = AnalyzingMessageTemplateParser.Analyze(literal.Token.ValueText);
                    foreach (var templateDiagnostic in messageTemplateDiagnostics)
                    {
                        var textSpan = new TextSpan(templateDiagnostic.StartIndex + literalSpan.Start + 1, templateDiagnostic.Length);
                        var sourceLocation = Location.Create(context.Node.SyntaxTree, textSpan);
                        context.ReportDiagnostic(Diagnostic.Create(TemplateRule, sourceLocation, templateDiagnostic.Diagnostic));
                    }
                    break;
                }
            }

            // is this an overload where the exception argument is used?
            var exception = context.SemanticModel.Compilation.GetTypeByMetadataName("System.Exception");
            if (method.Parameters.First().Type == exception)
            {
                return;
            }

            // is there an overload with the exception argument?
            if (!method.ContainingType.GetMembers().OfType<IMethodSymbol>().Any(x => x.Name == method.Name && x.Parameters.FirstOrDefault()?.Type == exception))
            {
                return;
            }

            // check wether any of the format arguments is an exception
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                var arginfo = context.SemanticModel.GetTypeInfo(argument.Expression);
                if (IsException(exception, arginfo.Type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(ExceptionRule, argument.GetLocation(), argument.Expression.ToFullString()));
                }
            }
        }

        private static bool IsException(ITypeSymbol exceptionSymbol, ITypeSymbol type)
        {
            for (ITypeSymbol symbol = type; symbol != null; symbol = symbol.BaseType)
            {
                if (symbol == exceptionSymbol)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Returns the parameter to which this argument is passed. If <paramref name="allowParams"/>
        /// is true, the last parameter will be returned if it is params parameter and the index of
        /// the specified argument is greater than the number of parameters.
        /// </summary>
        /// <remarks>Lifted from http://source.roslyn.io/#Microsoft.CodeAnalysis.CSharp.Workspaces/Extensions/ArgumentSyntaxExtensions.cs,af94352fb5da7056 </remarks>
        public static IParameterSymbol DetermineParameter(ArgumentSyntax argument, SemanticModel semanticModel, bool allowParams = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            var argumentList = argument.Parent as BaseArgumentListSyntax;
            if (argumentList == null)
            {
                return null;
            }

            var invocableExpression = argumentList.Parent as ExpressionSyntax;
            if (invocableExpression == null)
            {
                return null;
            }

            var symbol = semanticModel.GetSymbolInfo(invocableExpression, cancellationToken).Symbol;
            if (symbol == null)
            {
                return null;
            }

            var parameters = (symbol as IMethodSymbol)?.Parameters ?? (symbol as IPropertySymbol)?.Parameters ?? ImmutableArray.Create<IParameterSymbol>();

            // Handle named argument
            if (argument.NameColon != null && !argument.NameColon.IsMissing)
            {
                var name = argument.NameColon.Name.Identifier.ValueText;
                return parameters.FirstOrDefault(p => p.Name == name);
            }

            // Handle positional argument
            var index = argumentList.Arguments.IndexOf(argument);
            if (index < 0)
            {
                return null;
            }

            if (index < parameters.Length)
            {
                return parameters[index];
            }

            if (allowParams)
            {
                var lastParameter = parameters.LastOrDefault();
                if (lastParameter == null)
                {
                    return null;
                }

                if (lastParameter.IsParams)
                {
                    return lastParameter;
                }
            }

            return null;
        }
    }
}
