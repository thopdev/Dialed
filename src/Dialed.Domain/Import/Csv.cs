using System.Text;

namespace Dialed.Domain.Import;

/// <summary>A small CSV reader (RFC 4180 quoting) that also copes with a BOM, semicolons (Excel in NL) and tabs.</summary>
public static class Csv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        text = text.TrimStart('﻿');
        var delimiter = DetectDelimiter(text);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString().Trim());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                EndRow(rows, row, field);
            }
            else
            {
                field.Append(c);
            }
        }

        EndRow(rows, row, field);
        return rows;
    }

    private static void EndRow(List<IReadOnlyList<string>> rows, List<string> row, StringBuilder field)
    {
        row.Add(field.ToString().Trim());
        field.Clear();
        if (row.Any(f => f.Length > 0))
        {
            rows.Add([.. row]);
        }

        row.Clear();
    }

    private static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        var best = ',';
        var bestCount = firstLine.Count(c => c == ',');
        foreach (var candidate in new[] { ';', '\t' })
        {
            var count = firstLine.Count(c => c == candidate);
            if (count > bestCount)
            {
                best = candidate;
                bestCount = count;
            }
        }

        return best;
    }
}
