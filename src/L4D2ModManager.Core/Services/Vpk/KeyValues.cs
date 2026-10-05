namespace L4D2ModManager.Core.Services.Vpk;

/// <summary>Valve KeyValues 节点（用于解析 addoninfo.txt）。</summary>
public sealed class KeyValuesNode
{
    public string Name { get; set; } = string.Empty;

    /// <summary>键值对的值；块节点为 null。</summary>
    public string? Value { get; set; }

    public List<KeyValuesNode> Children { get; set; } = new();

    public bool IsBlock => Children.Count > 0;

    /// <summary>递归查找同名的所有节点（不区分大小写）。</summary>
    public IEnumerable<KeyValuesNode> FindAll(string key)
    {
        foreach (var child in Children)
        {
            if (string.Equals(child.Name, key, StringComparison.OrdinalIgnoreCase))
                yield return child;

            foreach (var nested in child.FindAll(key))
                yield return nested;
        }
    }

    public KeyValuesNode? Find(string key) => FindAll(key).FirstOrDefault();

    /// <summary>按顺序尝试多个键名，返回第一个非空值。</summary>
    public string? GetString(params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = FindAll(key)
                .Select(n => n.Value)
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
        }
        return null;
    }

    /// <summary>收集某个键的全部取值（例如多个 addonTag）。</summary>
    public List<string> GetStrings(params string[] keys)
    {
        var result = new List<string>();
        foreach (var key in keys)
        {
            foreach (var node in FindAll(key))
            {
                if (string.IsNullOrWhiteSpace(node.Value)) continue;
                var value = node.Value!.Trim();
                if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
                    result.Add(value);
            }
        }
        return result;
    }
}

/// <summary>KeyValues 文本解析器（支持注释、引号、嵌套块、重复键）。</summary>
public static class KeyValuesParser
{
    public static KeyValuesNode? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var tokenizer = new Tokenizer(text);
        var root = new KeyValuesNode { Name = "root" };
        ParseInto(root, tokenizer);
        return root;
    }

    public static KeyValuesNode? ParseFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ParseInto(KeyValuesNode parent, Tokenizer tokenizer)
    {
        while (true)
        {
            var token = tokenizer.Next();
            if (token == null) return;
            if (token == "}") return;
            if (token == "{") continue;

            var name = token;
            var next = tokenizer.Peek();

            if (next == "{")
            {
                tokenizer.Next();
                var node = new KeyValuesNode { Name = name };
                ParseInto(node, tokenizer);
                parent.Children.Add(node);
            }
            else if (next == null || next == "}")
            {
                parent.Children.Add(new KeyValuesNode { Name = name, Value = null });
            }
            else
            {
                var value = tokenizer.Next();
                parent.Children.Add(new KeyValuesNode { Name = name, Value = value });
            }
        }
    }

    private sealed class Tokenizer
    {
        private readonly string _text;
        private int _index;
        private string? _peeked;
        private bool _hasPeeked;

        public Tokenizer(string text) => _text = text;

        public string? Peek()
        {
            if (!_hasPeeked)
            {
                _peeked = ReadNext();
                _hasPeeked = true;
            }
            return _peeked;
        }

        public string? Next()
        {
            if (_hasPeeked)
            {
                _hasPeeked = false;
                var value = _peeked;
                _peeked = null;
                return value;
            }
            return ReadNext();
        }

        private string? ReadNext()
        {
            SkipTrivia();
            if (_index >= _text.Length) return null;

            char c = _text[_index];
            if (c == '{' || c == '}')
            {
                _index++;
                return c.ToString();
            }

            if (c == '"')
            {
                _index++;
                var buffer = new System.Text.StringBuilder();
                while (_index < _text.Length)
                {
                    char ch = _text[_index];
                    if (ch == '\\' && _index + 1 < _text.Length)
                    {
                        char escaped = _text[_index + 1];
                        buffer.Append(escaped switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            _ => escaped,
                        });
                        _index += 2;
                        continue;
                    }
                    if (ch == '"')
                    {
                        _index++;
                        break;
                    }
                    buffer.Append(ch);
                    _index++;
                }
                return buffer.ToString();
            }

            var word = new System.Text.StringBuilder();
            while (_index < _text.Length)
            {
                char ch = _text[_index];
                if (char.IsWhiteSpace(ch) || ch == '{' || ch == '}' || ch == '"') break;
                word.Append(ch);
                _index++;
            }
            return word.Length == 0 ? null : word.ToString();
        }

        private void SkipTrivia()
        {
            while (_index < _text.Length)
            {
                char c = _text[_index];
                if (char.IsWhiteSpace(c) || c == '\uFEFF')
                {
                    _index++;
                    continue;
                }
                if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                {
                    while (_index < _text.Length && _text[_index] != '\n') _index++;
                    continue;
                }
                if (c == '/' && _index + 1 < _text.Length && _text[_index + 1] == '*')
                {
                    _index += 2;
                    while (_index + 1 < _text.Length && !(_text[_index] == '*' && _text[_index + 1] == '/')) _index++;
                    _index = Math.Min(_index + 2, _text.Length);
                    continue;
                }
                break;
            }
        }
    }
}
