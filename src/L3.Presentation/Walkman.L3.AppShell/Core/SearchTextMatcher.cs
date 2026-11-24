using System.Text;

namespace Walkman.L3.AppShell.Core;

/// <summary>
/// Pre-normalized search query to avoid per-item normalization and params-array allocations.
/// </summary>
public readonly record struct PreparedSearchQuery(string Raw, string Norm, string Compact, string[] Tokens)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);
}

/// <summary>
/// Punctuation-insensitive search matcher supporting optional syntax/symbols and multi-token queries.
/// </summary>
public static class SearchTextMatcher
{
    public static PreparedSearchQuery Prepare(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new PreparedSearchQuery(string.Empty, string.Empty, string.Empty, Array.Empty<string>());
        }

        string trimmed = query.Trim();
        string norm = NormalizeWithSpaces(trimmed);
        string compact = NormalizeCompact(trimmed);
        string[] tokens = string.IsNullOrEmpty(norm)
            ? Array.Empty<string>()
            : norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return new PreparedSearchQuery(trimmed, norm, compact, tokens);
    }

    public static bool Matches(in PreparedSearchQuery q, string? f1, string? f2 = null, string? f3 = null, string? f4 = null)
    {
        if (q.IsEmpty)
        {
            return true;
        }

        if ((!string.IsNullOrEmpty(f1) && f1.Contains(q.Raw, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(f2) && f2.Contains(q.Raw, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(f3) && f3.Contains(q.Raw, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(f4) && f4.Contains(q.Raw, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrEmpty(q.Norm))
        {
            return false;
        }

        var normBuilder = new StringBuilder();
        var compactBuilder = new StringBuilder();
        AppendField(f1, normBuilder, compactBuilder);
        AppendField(f2, normBuilder, compactBuilder);
        AppendField(f3, normBuilder, compactBuilder);
        AppendField(f4, normBuilder, compactBuilder);

        string combinedNorm = normBuilder.ToString();
        string combinedCompact = compactBuilder.ToString();

        if (combinedNorm.Contains(q.Norm, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(q.Compact) && combinedCompact.Contains(q.Compact, StringComparison.Ordinal))
        {
            return true;
        }

        if (q.Tokens.Length > 1)
        {
            for (int i = 0; i < q.Tokens.Length; i++)
            {
                string tok = q.Tokens[i];
                if (!combinedNorm.Contains(tok, StringComparison.Ordinal) &&
                    !combinedCompact.Contains(tok, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        return false;
    }

    private static void AppendField(string? field, StringBuilder normBuilder, StringBuilder compactBuilder)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            return;
        }

        string nf = NormalizeWithSpaces(field);
        if (!string.IsNullOrEmpty(nf))
        {
            if (normBuilder.Length > 0) normBuilder.Append(' ');
            normBuilder.Append(nf);
        }

        string cf = NormalizeCompact(field);
        if (!string.IsNullOrEmpty(cf))
        {
            compactBuilder.Append(cf);
        }
    }
    public static string NormalizeWithSpaces(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool prevSpace = true;

        foreach (char raw in text)
        {
            if (raw is '\'' or '’' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(raw))
            {
                sb.Append(char.ToLowerInvariant(raw));
                prevSpace = false;
            }
            else if (!prevSpace)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    public static string NormalizeCompact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char raw in text)
        {
            if (char.IsLetterOrDigit(raw))
            {
                sb.Append(char.ToLowerInvariant(raw));
            }
        }

        return sb.ToString();
    }

    public static bool Matches(string? query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        string trimmed = query.Trim();
        for (int i = 0; i < fields.Length; i++)
        {
            if (!string.IsNullOrEmpty(fields[i]) &&
                fields[i]!.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string normQuery = NormalizeWithSpaces(trimmed);
        if (string.IsNullOrEmpty(normQuery))
        {
            return false;
        }

        string compactQuery = NormalizeCompact(trimmed);
        var normBuilder = new StringBuilder();
        var compactBuilder = new StringBuilder();

        for (int i = 0; i < fields.Length; i++)
        {
            string? field = fields[i];
            if (string.IsNullOrWhiteSpace(field))
            {
                continue;
            }

            string nf = NormalizeWithSpaces(field);
            if (!string.IsNullOrEmpty(nf))
            {
                if (normBuilder.Length > 0)
                {
                    normBuilder.Append(' ');
                }
                normBuilder.Append(nf);
            }

            string cf = NormalizeCompact(field);
            if (!string.IsNullOrEmpty(cf))
            {
                compactBuilder.Append(cf);
            }
        }

        string combinedNorm = normBuilder.ToString();
        string combinedCompact = compactBuilder.ToString();

        if (combinedNorm.Contains(normQuery, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(compactQuery) &&
            combinedCompact.Contains(compactQuery, StringComparison.Ordinal))
        {
            return true;
        }

        string[] tokens = normQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 1)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                string tok = tokens[i];
                if (!combinedNorm.Contains(tok, StringComparison.Ordinal) &&
                    !combinedCompact.Contains(tok, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }
}
