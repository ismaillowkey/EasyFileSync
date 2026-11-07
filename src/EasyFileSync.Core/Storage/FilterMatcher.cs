using EasyFileSync.Core.Models;

namespace EasyFileSync.Core.Storage;

public sealed class StorageException : Exception
{
    public StorageException(string message) : base(message) { }
    public StorageException(string message, Exception inner) : base(message, inner) { }
}

public sealed class FilterMatcher
{
    private readonly FilterOptions _options;
    private readonly List<System.Text.RegularExpressions.Regex> _include = new();
    private readonly List<System.Text.RegularExpressions.Regex> _exclude = new();

    public static readonly string[] DefaultExcludes =
    {
        "_EasyFileSync_/", "_EasyFileSync_", "_gsdata_/", "_gsdata_",
        "*.tmp", "*.temp", "*.part", "*.partial", "~$*", "*.EasyFileSync-part",
        "Thumbs.db", "desktop.ini", ".DS_Store", "*.crdownload", "*.!ut",
    };

    public FilterMatcher(FilterOptions options)
    {
        _options = options;
        foreach (var p in DefaultExcludes) AddPattern(_exclude, p, isExclude: true);
        foreach (var p in options.Exclude) AddPattern(_exclude, p, isExclude: true);
        foreach (var p in options.Include) AddPattern(_include, p, isExclude: false);
    }

    private static void AddPattern(List<System.Text.RegularExpressions.Regex> list, string pattern, bool isExclude)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        string p = pattern.Trim();
        if (p.Length == 0)
        {
            return;
        }

        // 1. Cek awalan prefix "name " atau "path " (GoodSync filter syntax)
        bool isNameRule = false;

        if (p.StartsWith("name ", StringComparison.OrdinalIgnoreCase))
        {
            isNameRule = true;
            p = p.Substring(5).Trim();
        }
        else if (p.StartsWith("path ", StringComparison.OrdinalIgnoreCase))
        {
            p = p.Substring(5).Trim();
        }
        else
        {
            // Jika tanpa prefix: jika tidak mengandung slash maka dianggap name
            if (!p.Contains('/') && !p.Contains('\\'))
            {
                isNameRule = true;
            }
        }

        if (p.Length == 0)
        {
            return;
        }

        // Cek apakah diakhiri slash (menandakan spesifik direktori)
        bool isDirExplicit = false;
        if (p.EndsWith('/') || p.EndsWith('\\'))
        {
            isDirExplicit = true;
            p = p.TrimEnd('/', '\\');
        }

        if (p.Length == 0)
        {
            return;
        }

        p = p.Replace('\\', '/');

        var sb = new System.Text.StringBuilder();

        if (isNameRule)
        {
            // Aturan Nama: Mencocokkan nama file atau folder di tingkat manapun pada pohon direktori
            sb.Append("^(?:.*/)?");
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*')
                {
                    if (i + 1 < p.Length && p[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i = i + 1;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                }
            }

            // Jika aturan exclude dan pola tidak mengandung titik ekstensi (seperti node_modules, temp, build, dll)
            // atau eksplisit direktori, maka otomatis mengecualikan seluruh isi subfolder dan filenya
            if (isExclude)
            {
                if (!p.Contains('.') || isDirExplicit)
                {
                    sb.Append("(?:/.*)?$");
                }
                else
                {
                    sb.Append("$");
                }
            }
            else
            {
                if (isDirExplicit)
                {
                    sb.Append("(?:/.*)?$");
                }
                else
                {
                    sb.Append("$");
                }
            }
        }
        else
        {
            // Aturan Path: Relatif dari direktori root
            if (p.StartsWith("/"))
            {
                sb.Append("^");
                p = p.Substring(1);
            }
            else if (p.StartsWith("*/"))
            {
                sb.Append("^(?:.*/)?");
                p = p.Substring(2);
            }
            else
            {
                sb.Append("^(?:.*/)?");
            }

            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*')
                {
                    if (i + 1 < p.Length && p[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i = i + 1;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
                }
            }

            if (isExclude)
            {
                if (isDirExplicit || !p.Contains('.'))
                {
                    sb.Append("(?:/.*)?$");
                }
                else
                {
                    sb.Append("$");
                }
            }
            else
            {
                if (isDirExplicit)
                {
                    sb.Append("(?:/.*)?$");
                }
                else
                {
                    sb.Append("$");
                }
            }
        }

        try
        {
            var rx = new System.Text.RegularExpressions.Regex(
                sb.ToString(),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            list.Add(rx);
        }
        catch
        {
            // Abaikan jika format regex tidak dapat dikompilasi
        }
    }

    public bool IsExcluded(string relativePath, FileEntry entry)
    {
        string path = relativePath.Replace('\\', '/').TrimStart('/');
        if (_options.ExcludeHidden && entry.IsHidden)
        {
            return true;
        }
        if (_options.ExcludeSystem && entry.IsSystem)
        {
            return true;
        }

        foreach (var rx in _exclude)
        {
            if (rx.IsMatch(path))
            {
                return true;
            }
        }

        if (_include.Count > 0)
        {
            foreach (var rx in _include)
            {
                if (rx.IsMatch(path))
                {
                    return false;
                }
            }

            // Folder tetap diizinkan agar file di dalamnya yang cocok dengan include dapat dianalisa
            if (!entry.IsDirectory)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsExcludedDirectory(string relativePath)
    {
        string path = relativePath.Replace('\\', '/').TrimStart('/');
        foreach (var rx in _exclude)
        {
            if (rx.IsMatch(path))
            {
                return true;
            }
        }
        return false;
    }

    public bool AllowsDirectory(string relativePath)
    {
        bool excluded = IsExcludedDirectory(relativePath);
        if (excluded)
        {
            return false;
        }
        else
        {
            return true;
        }
    }
}
