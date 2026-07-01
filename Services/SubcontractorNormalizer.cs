using System.Globalization;
using System.Text.RegularExpressions;

namespace PADS.MoneyFlow.Api.Services;

// Pure name-cleaning and identity logic for subcontractor and client (užsakovas)
// names. Extracted from MonthlyFlowStore so the matching rules live in one place,
// independent of persistence/query concerns. Every method here is a deterministic
// pure function: the same raw name always yields the same key/display form, and
// no substring, fuzzy, or similarity matching is used.
//
// The two tiny text helpers (CollapseWhitespace / NormalizeKeyPart) are duplicated
// from MonthlyFlowStore on purpose: they are one-liners used pervasively on both
// sides, and copying them keeps this class self-contained without churning dozens
// of unrelated call sites in the store.
public static class SubcontractorNormalizer
{
    public static string NormalizeClientName(string? value)
    {
        var text = CleanCustomerDisplayName(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        foreach (var quote in SubcontractorQuoteChars)
        {
            text = text.Replace(quote.ToString(), " ");
        }

        text = Regex.Replace(text, @"[()[\]{}]", " ");
        text = Regex.Replace(text, @"\s*[,.]\s*", " ");
        text = CollapseWhitespace(text).ToUpperInvariant();
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return string.Join(' ', tokens);
    }

    public static string CanonicalClientDisplayName(IReadOnlyCollection<string> rawNames)
    {
        var candidates = rawNames
            .Select(CleanClientDisplayName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(ClientDisplayScore)
            .ThenBy(name => name.Length)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidates.Count == 0 ? "(Be užsakovo)" : candidates[0];
    }

    private static string? CleanClientDisplayName(string? value)
    {
        var text = CleanCustomerDisplayName(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (var quote in SubcontractorQuoteChars)
        {
            text = text.Replace(quote.ToString(), " ");
        }

        text = Regex.Replace(text, @"\s*[,.]\s*", " ");
        text = CollapseWhitespace(text);
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[0]))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && ClientLegalFormTokens.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return ToClientDisplayCase(tokens.Count == 0 ? text : string.Join(' ', tokens));
    }

    private static string ToClientDisplayCase(string value)
    {
        if (!value.Any(char.IsLetter) || value.Any(char.IsLower))
        {
            return value;
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture));
    }

    private static int ClientDisplayScore(string name)
    {
        var score = 0;
        if (name.Contains('"') || name.Contains('„') || name.Contains('“') || name.Contains('”'))
        {
            score += 10;
        }

        var firstToken = NormalizeKeyPart(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstToken) && ClientLegalFormTokens.Contains(firstToken))
        {
            score += 2;
        }

        return score;
    }

    private static readonly IReadOnlyCollection<LegalFormPattern> SubcontractorLegalForms =
    [
        new("SP Z O O", ["SP", "Z", "O", "O"]),
        new("VŠĮ", ["VŠĮ"]),
        new("VŠĮ", ["VSI"]),
        new("UAB", ["UAB"]),
        new("MB", ["MB"]),
        new("AB", ["AB"]),
        new("IĮ", ["IĮ"]),
        new("II", ["II"]),
        new("VĮ", ["VĮ"]),
        new("SIA", ["SIA"]),
        new("AS", ["AS"]),
        new("GMBH", ["GMBH"]),
        new("OU", ["OU"]),
        new("OÜ", ["OÜ"]),
    ];

    private static readonly HashSet<string> ClientLegalFormTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "AB", "UAB", "MB", "VŠĮ", "VšĮ", "IĮ", "VĮ"
    };

    private static readonly char[] SubcontractorQuoteChars =
    {
        '"', '\'', '„', '“', '”', '«', '»', // " ' „ “ ” « »
    };

    public static SubcontractorNameIdentity NormalizeSubcontractorIdentity(string? value)
    {
        var rawName = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return new SubcontractorNameIdentity("", "(Be subrangovo)", "", "", "");
        }

        var text = rawName;
        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0)
        {
            text = pipeIndex == 0 ? text[(pipeIndex + 1)..] : text[..pipeIndex];
        }

        foreach (var quote in SubcontractorQuoteChars)
        {
            text = text.Replace(quote.ToString(), " ");
        }

        text = Regex.Replace(text, @"\bsp\.?\s*z\.?\s*o\.?\s*o\.?\b", " SP Z O O ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"[;,()[\]{}]", " ");
        text = Regex.Replace(text, @"\s*\.\s*", ".");
        text = CollapseWhitespace(text);
        if (text.Length == 0)
        {
            return new SubcontractorNameIdentity(rawName, "(Be subrangovo)", "", "", "");
        }

        var tokens = text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => NormalizeCompanyToken(token).ToUpperInvariant())
            .ToList();

        var legalForm = "";
        if (TryRemoveLegalForm(tokens, fromStart: true, out var startLegalForm))
        {
            legalForm = startLegalForm;
        }
        else if (TryRemoveLegalForm(tokens, fromStart: false, out var endLegalForm))
        {
            legalForm = endLegalForm;
        }

        var baseName = CollapseWhitespace(string.Join(' ', tokens));
        var normalizedKey = string.IsNullOrWhiteSpace(baseName)
            ? legalForm
            : $"{legalForm}|{baseName}";
        var canonicalName = CanonicalSubcontractorDisplayName(legalForm, baseName, rawName);

        return new SubcontractorNameIdentity(
            rawName,
            canonicalName,
            legalForm,
            baseName,
            normalizedKey);
    }

    /// <summary>
    /// Builds a deterministic matching key for a subcontractor company name.
    /// The key is LEGALFORM|BASENAME; no substring, fuzzy, or similarity matching
    /// is used.
    /// </summary>
    public static string NormalizeSubcontractorName(string? value)
    {
        return NormalizeSubcontractorIdentity(value).NormalizedKey;
    }

    public static string CanonicalSubcontractorName(string? value)
    {
        return NormalizeSubcontractorIdentity(value).CanonicalDisplayName;
    }

    private static bool TryRemoveLegalForm(List<string> tokens, bool fromStart, out string legalForm)
    {
        foreach (var candidate in SubcontractorLegalForms.OrderByDescending(form => form.Tokens.Length))
        {
            if (fromStart)
            {
                if (tokens.Count < candidate.Tokens.Length
                    || !candidate.Tokens.SequenceEqual(tokens.Take(candidate.Tokens.Length), StringComparer.Ordinal))
                {
                    continue;
                }

                tokens.RemoveRange(0, candidate.Tokens.Length);
                legalForm = candidate.Key;
                return true;
            }

            if (tokens.Count < candidate.Tokens.Length
                || !candidate.Tokens.SequenceEqual(tokens.Skip(tokens.Count - candidate.Tokens.Length), StringComparer.Ordinal))
            {
                continue;
            }

            tokens.RemoveRange(tokens.Count - candidate.Tokens.Length, candidate.Tokens.Length);
            legalForm = candidate.Key;
            return true;
        }

        legalForm = "";
        return false;
    }

    private static string CanonicalSubcontractorDisplayName(string legalForm, string baseName, string rawName)
    {
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return CleanSubcontractorDisplayName(rawName) ?? "(Be subrangovo)";
        }

        var displayBase = string.Join(' ', baseName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(DisplayCompanyToken));

        return string.IsNullOrWhiteSpace(legalForm)
            ? displayBase
            : $"{legalForm} {displayBase}";
    }

    private static string DisplayCompanyToken(string token)
    {
        if (Regex.IsMatch(token, @"^\d+[A-Z0-9]*\.LT$", RegexOptions.IgnoreCase))
        {
            var prefix = token[..^3].ToLowerInvariant();
            return $"{prefix}.LT";
        }

        if (token.Length <= 3 && token.All(char.IsLetter))
        {
            return token;
        }

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(token.ToLower(CultureInfo.CurrentCulture));
    }

    private static string NormalizeCompanyToken(string token)
    {
        if (!token.Contains('.')
            && Regex.IsMatch(token, @"^(?=.*\d)[a-z0-9]+lt$", RegexOptions.IgnoreCase))
        {
            return $"{token[..^2]}.lt";
        }

        return token;
    }

    public static string? CleanSubcontractorDisplayName(string? value)
    {
        var text = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0)
        {
            text = pipeIndex == 0 ? text[(pipeIndex + 1)..] : text[..pipeIndex];
        }

        text = CollapseWhitespace(text);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return text.Trim();
    }

    public static string? CleanCustomerDisplayName(string? value)
    {
        var text = CollapseWhitespace(value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var pipeIndex = text.IndexOf('|');
        if (pipeIndex >= 0)
        {
            text = pipeIndex == 0 ? "" : text[..pipeIndex];
        }

        text = CollapseWhitespace(text);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string NormalizeKeyPart(string? value)
    {
        return CollapseWhitespace(value).ToUpperInvariant();
    }

    private static string CollapseWhitespace(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : Regex.Replace(value.Trim(), @"\s+", " ");
    }
}

public sealed record SubcontractorNameIdentity(
    string RawName,
    string CanonicalDisplayName,
    string LegalForm,
    string BaseName,
    string NormalizedKey);

public sealed record LegalFormPattern(string Key, string[] Tokens);
