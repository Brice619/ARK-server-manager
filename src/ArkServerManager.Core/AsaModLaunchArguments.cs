namespace ArkServerManager.Core;

/// <summary>Public ASA project IDs only. Pass returned tokens through ProcessStartInfo.ArgumentList.</summary>
public static class AsaModLaunchArguments
{
    public static IReadOnlyList<string> Build(IReadOnlyList<string> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var ids = orderedIds.ToArray();
        if (ids.Any(id => string.IsNullOrEmpty(id) || id.Any(c => c is < '0' or > '9')
            || !ulong.TryParse(id, out var value) || value == 0)
            || ids.Select(id => ulong.Parse(id)).Distinct().Count() != ids.Length)
            throw new ArgumentException("Choose distinct numeric ASA project IDs.");
        return ids.Length == 0 ? [] : ["-mods=" + string.Join(",", ids)];
    }
}
