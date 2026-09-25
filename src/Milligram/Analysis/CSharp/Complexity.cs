using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Milligram.Analysis.CSharp;

/// <summary>Cyclomatic complexity: one plus every branch point, including those inside lambdas and local functions.</summary>
public static class Complexity
{
    public static int Of(SyntaxNode node) => 1 + node.DescendantNodes().Sum(Weight);

    public static int Of(IEnumerable<SyntaxNode> nodes) => 1 + nodes.SelectMany(n => n.DescendantNodesAndSelf()).Sum(Weight);

    private static int Weight(SyntaxNode node) => node switch
    {
        IfStatementSyntax or ConditionalExpressionSyntax or WhileStatementSyntax or DoStatementSyntax
            or ForStatementSyntax or CommonForEachStatementSyntax or CatchClauseSyntax
            or ConditionalAccessExpressionSyntax or WhenClauseSyntax
            or CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax or BinaryPatternSyntax => 1,
        SwitchExpressionArmSyntax arm => arm.Pattern is DiscardPatternSyntax ? 0 : 1,
        BinaryExpressionSyntax binary => binary.Kind() is SyntaxKind.LogicalAndExpression
            or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression ? 1 : 0,
        AssignmentExpressionSyntax assignment => assignment.IsKind(SyntaxKind.CoalesceAssignmentExpression) ? 1 : 0,
        _ => 0,
    };
}
