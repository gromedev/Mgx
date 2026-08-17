using System.Management.Automation;
using System.Management.Automation.Language;

namespace Mgx.Cmdlets.Base;

internal sealed class ConsistencyLevelCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        if ("eventual".StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            yield return new CompletionResult("eventual", "eventual", CompletionResultType.ParameterValue, "eventual consistency");
    }
}

internal sealed class ThrottlePriorityCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        string[] priorities = ["Low", "Normal", "High"];
        string[] tooltips = [
            "Deprioritize under throttling pressure",
            "Default priority",
            "Prioritize under throttling pressure"
        ];
        return priorities
            .Select((p, i) => (p, i))
            .Where(x => x.p.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            .Select(x => new CompletionResult(x.p, x.p, CompletionResultType.ParameterValue,
                tooltips[x.i]));
    }
}

/// <summary>
/// Prefer-header tokens for drive delta queries. deltaExcludeParent is deliberately absent:
/// it is a standalone request header, not a Prefer token - pass it via
/// -Headers @{ deltaExcludeParent = "true" }.
/// </summary>
internal sealed class DeltaPreferCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        (string Value, string Tooltip)[] tokens =
        [
            ("deltashowremovedasdeleted", "Removed items carry the deleted facet"),
            ("deltatraversepermissiongaps", "Traverse permission gaps in the hierarchy"),
            ("deltashowsharingchanges", "Annotate permission-driven changes (requires the other two and Sites.FullControl.All)"),
            ("hierarchicalsharing", "Sharing information only for hierarchy roots and explicit changes"),
        ];
        return tokens
            .Where(t => t.Value.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            .Select(t => new CompletionResult(t.Value, t.Value, CompletionResultType.ParameterValue, t.Tooltip));
    }
}

internal sealed class ApiVersionCompleter : IArgumentCompleter
{
    public IEnumerable<CompletionResult> CompleteArgument(
        string commandName, string parameterName, string wordToComplete,
        CommandAst commandAst,
        System.Collections.IDictionary fakeBoundParameters)
    {
        string[] versions = ["v1.0", "beta"];
        return versions
            .Where(v => v.StartsWith(wordToComplete, StringComparison.OrdinalIgnoreCase))
            .Select(v => new CompletionResult(v, v, CompletionResultType.ParameterValue, $"Graph API {v}"));
    }
}
