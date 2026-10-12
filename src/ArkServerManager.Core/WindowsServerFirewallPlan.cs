using System.Globalization;

namespace ArkServerManager.Core;
public enum ServerNetworkScope { LocalNetwork, Internet }
public sealed record ServerFirewallRule(string Name, string Program, string Protocol, IReadOnlyList<int> Ports, string RemoteAddress);
public static class WindowsServerFirewallPlan
{
    public static ServerFirewallRule Build(ServerDefinition server, ServerNetworkScope scope)
    {
        if (server.Id == Guid.Empty || !Enum.IsDefined(server.Edition) || !Enum.IsDefined(scope))
            throw new ArgumentException("Choose a server and network scope.");
        server.Settings.Validate();
        if (!Path.IsPathFullyQualified(server.Directory)) throw new ArgumentException("Choose an absolute server folder.");
        var executable = Path.Combine(Path.GetFullPath(server.Directory), "ShooterGame", "Binaries", "Win64",
            server.Edition == ArkEdition.ASA ? "ArkAscendedServer.exe" : "ShooterGameServer.exe");
        // Local management uses loopback RCON; do not expose its administrator port.
        return new("ARKServerManager-" + server.Id.ToString("N") + "-Players", executable, "UDP",
            [server.Settings.GamePort, server.Settings.GamePort + 1, server.Settings.QueryPort],
            scope == ServerNetworkScope.LocalNetwork ? "LocalSubnet" : "Any");
    }

    public static string CreateScript(ServerDefinition server, ServerNetworkScope scope)
    {
        var rule = Build(server, scope);
        static string Literal(string value)
        {
            if (value.Any(char.IsControl)) throw new ArgumentException("Firewall values cannot contain control characters.");
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
        var ports = string.Join(",", rule.Ports.Select(port => port.ToString(CultureInfo.InvariantCulture)));
        return "$ErrorActionPreference = 'Stop'\n"
            + "$existing = @(Get-NetFirewallRule -Name " + Literal(rule.Name) + " -ErrorAction SilentlyContinue)\n"
            + "if ($existing.Count -gt 0) {\n"
            + "  if ($existing.Count -ne 1 -or $existing[0].Group -ne 'ARK Server Manager') { throw 'Rule ownership is uncertain. Review it before replacing it.' }\n"
            + "  $application = @($existing[0] | Get-NetFirewallApplicationFilter)\n"
            + "  if ($application.Count -ne 1 -or $application[0].Program -ne " + Literal(rule.Program) + ") { throw 'The rule belongs to a different executable.' }\n"
            + "  $existing[0] | Set-NetFirewallRule -Direction Inbound -Action Allow -Enabled True -Profile Any -Protocol UDP -LocalPort " + ports
            + " -RemoteAddress " + Literal(rule.RemoteAddress) + " | Out-Null\n"
            + "} else {\n"
            + "New-NetFirewallRule -Name " + Literal(rule.Name) + " -DisplayName " + Literal(rule.Name)
            + " -Group 'ARK Server Manager' -Direction Inbound -Action Allow -Enabled True -Profile Any"
            + " -Program " + Literal(rule.Program) + " -Protocol UDP -LocalPort " + ports
            + " -RemoteAddress " + Literal(rule.RemoteAddress) + " -PolicyStore PersistentStore | Out-Null\n}\n"
            + "$verified = @(Get-NetFirewallRule -Name " + Literal(rule.Name) + " -PolicyStore PersistentStore)\n"
            + "if ($verified.Count -ne 1 -or $verified[0].Group -ne 'ARK Server Manager' -or $verified[0].Enabled -ne 'True' -or $verified[0].Direction -ne 'Inbound' -or $verified[0].Action -ne 'Allow' -or $verified[0].Profile -ne 'Any') { throw 'Firewall rule verification failed.' }\n"
            + "$verifiedApp = @($verified[0] | Get-NetFirewallApplicationFilter)\n"
            + "if ($verifiedApp.Count -ne 1 -or $verifiedApp[0].Program -ne " + Literal(rule.Program) + ") { throw 'Firewall executable verification failed.' }\n"
            + "$verifiedPort = @($verified[0] | Get-NetFirewallPortFilter)\n"
            + "$expectedPorts = @(" + ports + ") | ForEach-Object { [string]$_ } | Sort-Object\n"
            + "if ($verifiedPort.Count -ne 1 -or $verifiedPort[0].Protocol -notin @('UDP', '17')) { throw 'Firewall protocol verification failed.' }\n"
            + "$actualPorts = @($verifiedPort[0].LocalPort) | ForEach-Object { [string]$_ } | Sort-Object\n"
            + "if (@(Compare-Object $expectedPorts $actualPorts).Count -ne 0) { throw 'Firewall player ports verification failed.' }\n"
            + "$verifiedAddress = @($verified[0] | Get-NetFirewallAddressFilter)\n"
            + "if ($verifiedAddress.Count -ne 1 -or @($verifiedAddress[0].RemoteAddress).Count -ne 1 -or $verifiedAddress[0].RemoteAddress[0] -ne " + Literal(rule.RemoteAddress) + ") { throw 'Firewall remote scope verification failed.' }\n";
    }

    public static string RemovalScript(ServerDefinition server)
    {
        var rule = Build(server, ServerNetworkScope.LocalNetwork);
        static string Quote(string value)
        {
            if (value.Any(char.IsControl)) throw new ArgumentException("Invalid firewall path.");
            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }
        return "$ErrorActionPreference = 'Stop'\n$existing = @(Get-NetFirewallRule -Name " + Quote(rule.Name) + " -ErrorAction SilentlyContinue)\n"
            + "if ($existing.Count -gt 0) {\n"
            + " if ($existing.Count -ne 1 -or $existing[0].Group -ne 'ARK Server Manager') { throw 'Rule ownership changed.' }\n"
            + " $application = @($existing[0] | Get-NetFirewallApplicationFilter)\n"
            + " if ($application.Count -ne 1 -or $application[0].Program -ne " + Quote(rule.Program) + ") { throw 'The rule belongs to another executable.' }\n"
            + " $existing[0] | Remove-NetFirewallRule\n}\n"
            + "if (@(Get-NetFirewallRule -Name " + Quote(rule.Name) + " -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Firewall removal verification failed.' }\n";
    }
}
