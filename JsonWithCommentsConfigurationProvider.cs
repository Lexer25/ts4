using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TS4;

sealed class JsonWithCommentsConfigurationSource : IConfigurationSource
{
    public string Path { get; set; } = "";
    public string BasePath { get; set; } = "";
    public bool Optional { get; set; }
    public bool ReloadOnChange { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        var fullPath = System.IO.Path.Combine(BasePath, Path);
        var provider = new JsonWithCommentsConfigurationProvider(fullPath, Optional, ReloadOnChange);
        if (ReloadOnChange)
        {
            var dir = System.IO.Path.GetDirectoryName(fullPath) ?? "";
            var fileName = System.IO.Path.GetFileName(fullPath);
            var watcher = new PhysicalFileProvider(dir).Watch(fileName);
            watcher.RegisterChangeCallback(_ =>
            {
                System.Threading.Thread.Sleep(50); // задержка для завершения записи файла
                provider.ReloadFromFile();
            }, null);
        }
        return provider;
    }
}

/// <summary>Провайдер конфигурации из JSON с поддержкой комментариев (// и /* */).</summary>
sealed class JsonWithCommentsConfigurationProvider : ConfigurationProvider
{
    readonly string _fullPath;
    readonly bool _optional;
    readonly bool _reloadOnChange;

    public JsonWithCommentsConfigurationProvider(string fullPath, bool optional, bool reloadOnChange)
    {
        _fullPath = fullPath;
        _optional = optional;
        _reloadOnChange = reloadOnChange;
    }

    public override void Load()
    {
        if (!File.Exists(_fullPath))
        {
            if (_optional)
            {
                Data = new Dictionary<string, string?>();
                return;
            }
            throw new FileNotFoundException($"Файл конфигурации не найден: {_fullPath}");
        }

        using var stream = File.OpenRead(_fullPath);
        using var reader = new StreamReader(stream);
        using var jsonReader = new JsonTextReader(reader);
        var obj = JObject.Load(jsonReader, new JsonLoadSettings { CommentHandling = CommentHandling.Ignore });
        Data = Flatten(obj, null);
    }

    public void ReloadFromFile()
    {
        Load();
        OnReload();
    }

    static Dictionary<string, string?> Flatten(JToken token, string? prefix)
    {
        var result = new Dictionary<string, string?>();
        if (token is JObject obj)
        {
            foreach (var prop in obj.Properties())
            {
                var key = string.IsNullOrEmpty(prefix) ? prop.Name : $"{prefix}:{prop.Name}";
                if (prop.Value != null && (prop.Value is JObject || prop.Value is JArray))
                    foreach (var kv in Flatten(prop.Value, key))
                        result[kv.Key] = kv.Value;
                else
                    result[key] = prop.Value?.ToString();
            }
        }
        else if (token is JArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                var key = $"{prefix}:{i}";
                if (arr[i] is JObject || arr[i] is JArray)
                    foreach (var kv in Flatten(arr[i]!, key))
                        result[kv.Key] = kv.Value;
                else
                    result[key] = arr[i]?.ToString();
            }
        }
        return result;
    }
}
