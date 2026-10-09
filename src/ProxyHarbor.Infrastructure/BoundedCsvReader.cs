using System.Text;

namespace ProxyHarbor.Infrastructure;

/// <summary>Bounded CSV records with quoted fields and embedded newlines.</summary>
internal static class BoundedCsvReader
{
    internal static IEnumerable<string[]> ReadRows(string content, int maxColumns)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var index = 0; index < content.Length; index++)
        {
            var c = content[index];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (index + 1 < content.Length && content[index + 1] == '"') { field.Append('"'); index++; }
                else { quoted = false; closed = true; }
            }
            else if (c is ',' or '\r' or '\n')
            {
                fields.Add(field.ToString());
                if (fields.Count > maxColumns) throw InvalidFormat();
                field.Clear();
                closed = false;
                if (c == ',') continue;
                if (c == '\r' && index + 1 < content.Length && content[index + 1] == '\n') index++;
                yield return fields.ToArray();
                fields.Clear();
            }
            else if (closed) throw InvalidFormat();
            else if (c == '"')
            {
                if (field.Length != 0) throw InvalidFormat();
                quoted = true;
            }
            else field.Append(c);
            if (field.Length > 16_384) throw InvalidFormat();
        }
        if (quoted) throw InvalidFormat();
        if (fields.Count != 0 || field.Length != 0 || closed)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    private static InvalidDataException InvalidFormat() => new("Malformed CSV record or field limit exceeded.");
}
