using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Core;

/// <summary>Installs only the local MCP connection and scoped permission Jarvis needs in agy's user config.</summary>
internal static class AntigravityCliSetup
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string EnsureJarvisToolsAvailable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new ModelException("Could not locate the Windows user profile for Antigravity CLI configuration.");

        var serverName = EnsureMcpServer(Path.Combine(home, ".gemini", "config", "mcp_config.json"));
        EnsurePermission(Path.Combine(home, ".gemini", "antigravity-cli", "settings.json"), serverName);
        return serverName;
    }

    private static string EnsureMcpServer(string path)
    {
        var root = ReadObject(path, "Antigravity MCP configuration");
        if (root["mcpServers"] is not JsonObject servers)
        {
            if (root["mcpServers"] is not null)
                throw new ModelException("Antigravity's mcpServers configuration is not an object. Fix its mcp_config.json before using Jarvis tools.");
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        foreach (var entry in servers)
        {
            if (entry.Value is not JsonObject server || !SameEndpoint(server["serverUrl"]?.GetValue<string>(), JarvisMcpServer.ServerUrl))
                continue;
            if (server["disabled"] is JsonValue disabled && disabled.TryGetValue<bool>(out var isDisabled) && isDisabled)
                throw new ModelException($"The Antigravity MCP server '{entry.Key}' for Jarvis is disabled in mcp_config.json.");
            if (SetSessionAuthorization(server)) WriteObject(path, root);
            return entry.Key;
        }

        var name = ChooseServerName(servers);
        var serverConfig = new JsonObject { ["serverUrl"] = JarvisMcpServer.ServerUrl };
        SetSessionAuthorization(serverConfig);
        servers[name] = serverConfig;
        WriteObject(path, root);
        Log.Info($"Registered Jarvis's local Antigravity MCP server as '{name}'.");
        return name;
    }

    private static bool SetSessionAuthorization(JsonObject server)
    {
        if (server["headers"] is not JsonObject headers)
        {
            if (server["headers"] is not null)
                throw new ModelException("The Jarvis MCP server headers setting must be an object in mcp_config.json.");
            headers = new JsonObject();
            server["headers"] = headers;
        }

        var key = headers.Select(pair => pair.Key)
            .FirstOrDefault(name => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) ?? "Authorization";
        var value = "Bearer " + JarvisMcpServer.SessionToken;
        if (headers[key]?.GetValue<string>() == value) return false;
        headers[key] = value;
        return true;
    }

    private static string ChooseServerName(JsonObject servers)
    {
        foreach (var candidate in new[] { "jarvis", "jarvis-app", "jarvis-local", "jarvis-runtime" })
            if (!servers.ContainsKey(candidate)) return candidate;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"jarvis-app-{suffix}";
            if (!servers.ContainsKey(candidate)) return candidate;
        }
    }

    private static void EnsurePermission(string path, string serverName)
    {
        var root = ReadObject(path, "Antigravity CLI settings");
        if (root["permissions"] is not JsonObject permissions)
        {
            if (root["permissions"] is not null)
                throw new ModelException("Antigravity CLI permissions are not an object. Jarvis cannot safely configure its tool access.");
            permissions = new JsonObject();
            root["permissions"] = permissions;
        }

        var allow = GetOrCreateStringArray(permissions, "allow");
        var ask = GetStrings(permissions["ask"]);
        var deny = GetStrings(permissions["deny"]);
        var rule = $"mcp({serverName}/*)";

        if (deny.Any(value => AppliesToServer(value, serverName)) || ask.Any(value => AppliesToServer(value, serverName)))
            throw new ModelException($"Antigravity CLI has an Ask or Deny permission rule for '{serverName}' MCP tools. Headless Jarvis cannot approve those prompts; adjust that rule to let Jarvis's own safety gate handle approvals.");

        if (GetStrings(allow).Any(value => AppliesToServer(value, serverName))) return;
        allow.Add(rule);
        WriteObject(path, root);
        Log.Info($"Added the scoped Antigravity permission '{rule}'; Jarvis still gates each action through PolicyEngine.");
    }

    private static bool AppliesToServer(string rule, string serverName)
    {
        var value = rule.Trim();
        if (!value.StartsWith("mcp(", StringComparison.OrdinalIgnoreCase) || !value.EndsWith(')')) return false;
        var target = value[4..^1].Trim();
        if (target == "*") return true;
        return target.Equals(serverName, StringComparison.OrdinalIgnoreCase) ||
               target.StartsWith(serverName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameEndpoint(string? configured, string required)
    {
        if (string.IsNullOrWhiteSpace(configured) || !Uri.TryCreate(configured, UriKind.Absolute, out var a) ||
            !Uri.TryCreate(required, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port &&
               string.Equals(a.AbsolutePath.TrimEnd('/'), b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static JsonObject ReadObject(string path, string description)
    {
        if (!File.Exists(path)) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node as JsonObject ?? throw new ModelException($"{description} must contain a JSON object.");
        }
        catch (ModelException) { throw; }
        catch (Exception ex)
        {
            throw new ModelException($"Could not read {description} at {path}: {ex.Message}", ex);
        }
    }

    private static JsonArray GetOrCreateStringArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray array) return array;
        if (parent[key] is not null)
            throw new ModelException($"Antigravity CLI permissions.{key} must be a JSON array.");
        array = new JsonArray();
        parent[key] = array;
        return array;
    }

    private static IEnumerable<string> GetStrings(JsonNode? node)
    {
        if (node is not JsonArray array) yield break;
        foreach (var item in array)
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
                yield return text;
    }

    private static void WriteObject(string path, JsonObject root)
    {
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporary = path + ".jarvis-tmp";
            File.WriteAllText(temporary, root.ToJsonString(JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new ModelException($"Could not save Antigravity's Jarvis MCP setup at {path}: {ex.Message}", ex);
        }
    }
}
