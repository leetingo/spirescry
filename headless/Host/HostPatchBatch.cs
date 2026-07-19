using System.Reflection;

namespace Spirescry.Host;

internal enum HostPatchFailurePolicy
{
    Required,
    PresentationOnly,
}

internal sealed record HostPatchFailure(
    string MethodIdentity,
    Exception Cause)
{
    public static HostPatchFailure From(MethodInfo method, Exception cause) =>
        new(MethodIdentityOf(method), cause);

    private static string MethodIdentityOf(MethodInfo method)
    {
        var owner = method.DeclaringType?.FullName
            ?? method.DeclaringType?.Name
            ?? "<unknown-type>";
        var parameters = string.Join(",", method.GetParameters().Select(parameter =>
            parameter.ParameterType.FullName ?? parameter.ParameterType.Name));
        return $"{owner}.{method.Name}({parameters})";
    }
}

internal sealed record HostPatchBatchResult(
    int MatchedCount,
    int PatchedCount,
    IReadOnlyList<HostPatchFailure> Failures)
{
    public void Enforce(
        string patchSet,
        HostPatchFailurePolicy policy,
        Action<string, Exception> report)
    {
        var issues = new List<(string message, Exception cause)>();
        if (MatchedCount == 0)
        {
            var missing = new MissingMethodException(
                $"host patch set '{patchSet}' matched no methods");
            issues.Add((missing.Message, missing));
        }

        issues.AddRange(Failures.Select(failure =>
            ($"host patch set '{patchSet}' failed for {failure.MethodIdentity}",
                failure.Cause)));

        foreach (var (message, cause) in issues)
            report(message, cause);

        if (policy != HostPatchFailurePolicy.Required || issues.Count == 0)
            return;

        var details = string.Join("; ", issues.Select(issue =>
            $"{issue.message}: {issue.cause.GetType().Name}: {issue.cause.Message}"));
        var inner = issues.Count == 1
            ? issues[0].cause
            : new AggregateException(issues.Select(issue => issue.cause));
        throw new InvalidOperationException(
            $"required host patch set '{patchSet}' failed — {details}", inner);
    }
}
