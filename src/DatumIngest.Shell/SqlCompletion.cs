using DatumIngest.LanguageServer;
using RadLine;

namespace DatumIngest.Shell;

/// <summary>
/// Bridges RadLine's <see cref="ITextCompletion"/> contract to
/// <see cref="LanguageService.GetCompletions(string, int)"/>. RadLine splits
/// the input into <c>prefix + word + suffix</c> at the cursor; the language
/// service expects the full text plus a cursor offset, so this adapter
/// reconstructs both views.
/// </summary>
internal sealed class SqlCompletion : ITextCompletion
{
    private readonly LanguageService _service;

    public SqlCompletion(LanguageService service)
    {
        _service = service;
    }

    public IEnumerable<string> GetCompletions(string prefix, string word, string suffix)
    {
        if (!_service.IsInitialized)
        {
            yield break;
        }

        string fullText = prefix + word + suffix;
        int cursorOffset = prefix.Length + word.Length;

        CompletionItem[] items;
        try
        {
            items = _service.GetCompletions(fullText, cursorOffset);
        }
        catch
        {
            // The language service surfaces parser errors as exceptions on
            // partial input; swallow them so typing never throws.
            yield break;
        }

        foreach (CompletionItem item in items)
        {
            string text = !string.IsNullOrEmpty(item.InsertText) ? item.InsertText : item.Label;
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            // RadLine's convention is that returned strings replace the current
            // word; only emit candidates that actually extend what's typed so
            // we don't list every dialect keyword on every keystroke.
            if (word.Length == 0 || text.StartsWith(word, StringComparison.OrdinalIgnoreCase))
            {
                yield return text;
            }
        }
    }
}
