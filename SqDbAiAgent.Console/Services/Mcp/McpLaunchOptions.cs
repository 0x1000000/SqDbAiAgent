namespace SqDbAiAgent.ConsoleApp.Services.Mcp;

public enum McpTransport
{
    Http,
    Stdio
}

public sealed record McpLaunchOptions(McpTransport? Transport, int DatabaseUserId)
{
    public static bool TryParse(string[] args, out McpLaunchOptions options, out string? error)
    {
        McpTransport? transport = null;
        var databaseUserId = 0;
        var userIdSpecified = false;

        for (var index = 0; index < args.Length; index++)
        {
            if (TryReadValue(args, ref index, "--transport", out var transportValue, out var matched, out error))
            {
                if (transport is not null)
                {
                    options = new(null, 0);
                    error = "--transport may be specified only once.";
                    return false;
                }

                if (!Enum.TryParse<McpTransport>(transportValue, true, out var parsedTransport))
                {
                    options = new(null, 0);
                    error = "--transport must be either 'http' or 'stdio'.";
                    return false;
                }

                transport = parsedTransport;
                continue;
            }

            if (matched)
            {
                options = new(null, 0);
                return false;
            }

            if (TryReadValue(args, ref index, "--database-user-id", out var userIdValue, out matched, out error))
            {
                if (userIdSpecified)
                {
                    options = new(null, 0);
                    error = "--database-user-id may be specified only once.";
                    return false;
                }

                if (!int.TryParse(userIdValue, out databaseUserId) || databaseUserId < 0)
                {
                    options = new(null, 0);
                    error = "--database-user-id must be a non-negative integer.";
                    return false;
                }

                userIdSpecified = true;
                continue;
            }

            if (matched)
            {
                options = new(null, 0);
                return false;
            }
        }

        if (userIdSpecified && transport != McpTransport.Stdio)
        {
            options = new(null, 0);
            error = "--database-user-id is supported only with --transport stdio.";
            return false;
        }

        options = new(transport, databaseUserId);
        error = null;
        return true;
    }

    private static bool TryReadValue(
        string[] args,
        ref int index,
        string name,
        out string value,
        out bool matched,
        out string? error)
    {
        var argument = args[index];
        matched = string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)
                  || argument.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)
                  || argument.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase);
        value = string.Empty;
        error = null;
        if (!matched)
        {
            return false;
        }

        if (argument.Length > name.Length)
        {
            value = argument[(name.Length + 1)..].Trim();
        }
        else if (++index < args.Length)
        {
            value = args[index];
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        error = $"{name} requires a value.";
        return false;
    }
}
