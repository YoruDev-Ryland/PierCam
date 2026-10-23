using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PierCam.Net;

/// <summary>
/// The API's own reference, served by the API: docs/api.md, built into the executable so the
/// page always describes the build that serves it. Rendered to HTML here with just enough
/// Markdown for that one document - headings, paragraphs, lists, tables, fenced code, bold,
/// inline code and links - rather than pulling in a Markdown library for it.
/// </summary>
internal static class ApiDocs
{
    private static readonly Lazy<string> Text = new(() =>
    {
        using var stream = typeof(ApiDocs).Assembly.GetManifestResourceStream("PierCam.Net.api.md");
        if (stream is null) return "# PierCam remote access API\n\nThe reference is missing from this build.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly Lazy<string> Html = new(() => Render(Text.Value));

    public static string Markdown => Text.Value;
    public static string Page => Html.Value;

    internal static string Render(string md)
    {
        var body = new StringBuilder();
        var lines = md.Replace("\r\n", "\n").Split('\n');
        var paragraph = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Length == 0) return;
            body.Append("<p>").Append(Inline(paragraph.ToString().Trim())).Append("</p>\n");
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new StringBuilder();
                for (i++; i < lines.Length && !lines[i].StartsWith("```", StringComparison.Ordinal); i++)
                    code.Append(lines[i]).Append('\n');
                body.Append("<pre><code>").Append(WebUtility.HtmlEncode(code.ToString().TrimEnd())).Append("</code></pre>\n");
                continue;
            }
            if (Regex.Match(line, @"^(#{1,4})\s+(.*)$") is { Success: true } h)
            {
                FlushParagraph();
                var level = h.Groups[1].Length;
                body.Append($"<h{level}>").Append(Inline(h.Groups[2].Value)).Append($"</h{level}>\n");
                continue;
            }
            if (line.StartsWith('|'))
            {
                FlushParagraph();
                body.Append("<table>\n");
                var header = true;
                for (; i < lines.Length && lines[i].StartsWith('|'); i++)
                {
                    var cells = lines[i].Trim().Trim('|').Split('|');
                    if (Regex.IsMatch(lines[i], @"^\|[\s:|-]+\|?\s*$")) { header = false; continue; }
                    var tag = header ? "th" : "td";
                    body.Append("<tr>");
                    foreach (var c in cells) body.Append($"<{tag}>").Append(Inline(c.Trim())).Append($"</{tag}>");
                    body.Append("</tr>\n");
                }
                i--;
                body.Append("</table>\n");
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                body.Append("<ul>\n");
                for (; i < lines.Length && (lines[i].StartsWith("- ", StringComparison.Ordinal) || lines[i].StartsWith("  ", StringComparison.Ordinal)); i++)
                {
                    if (lines[i].StartsWith("- ", StringComparison.Ordinal)) body.Append("<li>").Append(Inline(lines[i][2..].Trim())).Append("</li>\n");
                    else body.Insert(body.Length - "</li>\n".Length, " " + Inline(lines[i].Trim()));
                }
                i--;
                body.Append("</ul>\n");
                continue;
            }
            if (line.Trim().Length == 0) { FlushParagraph(); continue; }
            paragraph.Append(line).Append(' ');
        }
        FlushParagraph();

        return """
               <!doctype html>
               <html lang="en"><head><meta charset="utf-8">
               <meta name="viewport" content="width=device-width, initial-scale=1">
               <title>PierCam API</title>
               <style>
               :root { color-scheme: dark; --bg:#0d0f12; --panel:#15181d; --text:#d7dbe0; --dim:#8a929c; --accent:#ff7a3d; --line:#262b33; }
               body { margin:0; background:var(--bg); color:var(--text); font:15px/1.6 system-ui, sans-serif; }
               main { max-width:860px; margin:0 auto; padding:32px 16px 64px; }
               h1, h2, h3 { font-family: ui-monospace, Consolas, monospace; letter-spacing:.04em; }
               h1 { color:var(--accent); font-size:24px; } h2 { font-size:18px; margin-top:36px; border-top:1px solid var(--line); padding-top:20px; }
               code { font-family: ui-monospace, Consolas, monospace; font-size:13px; background:var(--panel); padding:1px 5px; border-radius:3px; }
               pre { background:var(--panel); border:1px solid var(--line); padding:14px; overflow-x:auto; border-radius:4px; }
               pre code { background:none; padding:0; }
               h2 code, h3 code { font-size:inherit; background:none; padding:0; color:var(--accent); }
               table { border-collapse:collapse; width:100%; margin:12px 0; } th, td { border:1px solid var(--line); padding:6px 10px; text-align:left; vertical-align:top; }
               th { background:var(--panel); } a { color:var(--accent); }
               </style></head><body><main>
               """ + body + "</main></body></html>";
    }

    private static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);
        s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");
        s = Regex.Replace(s, @"\*\*([^*]+)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", "<a href=\"$2\">$1</a>");
        return s;
    }
}
