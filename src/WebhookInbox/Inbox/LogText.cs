using System.Globalization;

namespace WebhookInbox;

// Ids, event types and keys come from the request body, and the endpoint stores whatever it is sent. Logged as they
// are, a new line or a terminal escape in one of them would forge a line of the log, so what is logged is the text
// with those characters replaced and cut to a length a person can read.
internal static class LogText
{
    public const int MaxLength = 200;

    public static string Safe(string? value)
    {
        var text = Cut(value, MaxLength);
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = IsUnsafe(source[i]) ? '?' : source[i];
            }
        });
    }

    // The text cut to maxLength characters, with "..." when something was cut. The cut never leaves half of a
    // surrogate pair behind.
    public static string Cut(string? value, int maxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);
        if (value is null || value.Length <= maxLength)
        {
            return value ?? "";
        }

        var length = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..length] + "...";
    }

    // Control characters; format characters, among them the right-to-left override that reorders what follows when a
    // line is displayed and the zero-width ones that hide that two ids differ; the line and paragraph separators; and
    // code points with no assignment. Letters, digits, punctuation, spaces and surrogate pairs (emoji) are kept.
    private static bool IsUnsafe(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned;
}
