namespace TGK.Protocol;

/// <summary>Usernames: 3-32 characters from [a-zA-Z0-9._-], unique case-insensitively.</summary>
public static class UsernameRules
{
    public const int MinLength = 3;
    public const int MaxLength = 32;

    /// <summary>Returns null when <paramref name="username"/> is valid, otherwise a human-readable reason.</summary>
    public static string? Validate(string? username)
    {
        if (username is null || username.Length is < MinLength or > MaxLength)
            return $"Username must be {MinLength}-{MaxLength} characters long.";
        foreach (char c in username)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
                return "Username may only contain letters, digits, '.', '_' and '-'.";
        }
        return null;
    }
}
