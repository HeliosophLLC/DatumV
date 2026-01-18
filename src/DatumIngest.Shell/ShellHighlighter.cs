using DatumIngest.Parsing.Tokens;
using RadLine;
using Spectre.Console;
using Spectre.Console.Rendering;
using Superpower.Model;

namespace DatumIngest.Shell;

/// <summary>
/// Applies syntax highlighting to SQL input using the DatumIngest SQL tokenizer.
/// Keywords, strings, numbers, and operators are colored distinctly.
/// </summary>
internal sealed class ShellHighlighter : IHighlighter
{
    /// <inheritdoc />
    public IRenderable BuildHighlightedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Markup(Markup.Escape(text));
        }

        // Dot-commands are highlighted as a single unit.
        if (text.TrimStart().StartsWith('.'))
        {
            return new Markup($"[cyan]{Markup.Escape(text)}[/]");
        }

        try
        {
            TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(text);
            List<HighlightedSpan> spans = new();
            int lastEnd = 0;

            foreach (Token<SqlToken> token in tokens)
            {
                int start = token.Position.Absolute;
                int length = token.Span.Length;

                // Add any unhighlighted gap (whitespace between tokens).
                if (start > lastEnd)
                {
                    spans.Add(new HighlightedSpan(text[lastEnd..start], null));
                }

                string color = GetTokenColor(token.Kind);
                spans.Add(new HighlightedSpan(token.ToStringValue(), color));

                lastEnd = start + length;
            }

            // Trailing text after last token.
            if (lastEnd < text.Length)
            {
                spans.Add(new HighlightedSpan(text[lastEnd..], null));
            }

            return BuildMarkup(spans);
        }
        catch
        {
            // If tokenization fails (incomplete SQL), render without highlighting.
            return new Markup(Markup.Escape(text));
        }
    }

    private static string GetTokenColor(SqlToken kind)
    {
        return kind switch
        {
            SqlToken.Select or SqlToken.From or SqlToken.Where or
            SqlToken.Join or SqlToken.Left or SqlToken.Right or
            SqlToken.Full or SqlToken.Outer or SqlToken.Cross or
            SqlToken.Inner or SqlToken.On or SqlToken.And or
            SqlToken.Or or SqlToken.Not or SqlToken.In or
            SqlToken.Between or SqlToken.Like or SqlToken.Is or
            SqlToken.As or SqlToken.Into or SqlToken.Shard or
            SqlToken.Order or SqlToken.By or SqlToken.Asc or
            SqlToken.Desc or SqlToken.Limit or SqlToken.Offset or
            SqlToken.Cast => "blue",

            SqlToken.True or SqlToken.False or SqlToken.Null => "magenta",

            SqlToken.StringLiteral => "green",

            SqlToken.NumberLiteral => "cyan",

            SqlToken.Identifier => "white",

            SqlToken.Star or SqlToken.Equals or SqlToken.NotEquals or
            SqlToken.LessThan or SqlToken.GreaterThan or
            SqlToken.LessOrEqual or SqlToken.GreaterOrEqual or
            SqlToken.Plus or SqlToken.Minus or SqlToken.Slash or
            SqlToken.Percent or SqlToken.Caret or SqlToken.Pipe or
            SqlToken.DoublePipe => "yellow",

            SqlToken.Comma or SqlToken.Dot or
            SqlToken.LeftParen or SqlToken.RightParen => "grey",

            _ => "white"
        };
    }

    private static IRenderable BuildMarkup(List<HighlightedSpan> spans)
    {
        System.Text.StringBuilder builder = new();

        foreach (HighlightedSpan span in spans)
        {
            string escaped = Markup.Escape(span.Text);

            if (span.Color is null)
            {
                builder.Append(escaped);
            }
            else
            {
                builder.Append($"[{span.Color}]{escaped}[/]");
            }
        }

        return new Markup(builder.ToString());
    }

    private readonly record struct HighlightedSpan(string Text, string? Color);
}
