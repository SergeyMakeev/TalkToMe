using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkToMe;

internal sealed class Settings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080/v1";
    public string Model { get; set; } = "dictation";
    public LocalServerSettings? LocalServer { get; set; }
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 60;
    public string WhisperDirectory { get; set; } = "models/whisper-large-v3-turbo";
    public string Python { get; set; } = ".venv/Scripts/python.exe";
    public string Device { get; set; } = "cuda";
    public string ComputeType { get; set; } = "float16";
    public string CudaLibraryDirectory { get; set; } = "";
    public string Language { get; set; } = "auto";
    public string Shortcut { get; set; } = "Ctrl+Alt+F9";
    public bool Toggle { get; set; }
    public bool CopyOnly { get; set; }
    public int Microphone { get; set; } = -1;
    public string[] Glossary { get; set; } = [];

    [JsonIgnore] public string Root { get; private set; } = "";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Settings Load(string root)
    {
        var file = Path.Combine(root, "settings.json");
        if (!File.Exists(file)) file = Path.Combine(root, "settings.example.json");
        var config = JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json)
            ?? throw new InvalidDataException("Settings must be a JSON object.");
        config.Root = Path.GetFullPath(root);
        if (!Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 ||
            uri.UserInfo.Length + uri.Query.Length + uri.Fragment.Length != 0)
            throw new InvalidDataException("serverUrl must be an HTTP(S) base URL, for example http://127.0.0.1:8080/v1.");
        if (config.Language is not ("auto" or "en" or "ru") || config.TimeoutSeconds is < 1 or > 600 ||
            config.Temperature is < 0 or > 2 || string.IsNullOrWhiteSpace(config.Model) || config.Glossary is null)
            throw new InvalidDataException("Check language, timeoutSeconds, temperature, model and glossary in settings.json.");
        return config;
    }

    public string Resolve(string path) => Path.GetFullPath(path, Root);

    public void Save()
    {
        var file = Resolve("settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(file + ".tmp", file, true);
    }
}

internal sealed record LocalServerSettings(string Executable, string ModelFile);
