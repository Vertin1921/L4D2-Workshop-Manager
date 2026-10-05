using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace L4D2ModManager.Core.Services;

/// <summary>JSON 读写工具：中文不转义、原子写入、容忍注释与尾随逗号。</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions() => new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            Log.Error($"读取 JSON 失败: {path}", ex);
            return null;
        }
    }

    public static bool Save<T>(string path, T value)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(value, Options);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(path))
                File.Replace(temp, path, null, ignoreMetadataErrors: true);
            else
                File.Move(temp, path);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"写入 JSON 失败: {path}", ex);
            return false;
        }
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex)
        {
            Log.Error("反序列化失败", ex);
            return null;
        }
    }
}
