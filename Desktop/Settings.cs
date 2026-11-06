using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace TalkToMe;

internal sealed class Settings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080/v1";
    public string Model { get; set; } = "dictation";
    public LocalServerSettings? LocalServer { get; set; }
    // Missing keys in existing preferences select the original user-configured engines.
    public bool BundledSpeech { get; set; }
    public bool UseGpu { get; set; } = true;
    public string SpeechModel { get; set; } = "base";
    public string Translation { get; set; } = "server";
    [JsonIgnore] public bool LocalTranslation => Translation is "qwen08" or "qwen2" or "qwen4";
    [JsonIgnore] public string ModelDirectory { get; set; } = Path.Combine(UserDirectory, "Models");
    public IEnumerable<string> RequiredModels()
    {
        if (BundledSpeech) { yield return SpeechModel; yield return "activity"; }
        if (LocalTranslation) yield return Translation;
    }
    public double Temperature { get; set; } = 0.1;
    public int TimeoutSeconds { get; set; } = 60;
    public string WhisperDirectory { get; set; } = "models/whisper-large-v3-turbo";
    public string Python { get; set; } = ".venv/Scripts/python.exe";
    public string Device { get; set; } = "cuda";
    public string ComputeType { get; set; } = "float16";
    public string CudaLibraryDirectory { get; set; } = "";
    public string Language { get; set; } = "auto";
    public const string DefaultRecordShortcut = "Ctrl + Numpad 0";
    public string Shortcut { get; set; } = DefaultRecordShortcut;
    public string CancelShortcut { get; set; } = "Ctrl+Alt+F10";
    public bool Sounds { get; set; } = true;
    public int RecordingMinutes { get; set; } = 5;
    public string Prompt { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    public bool UseEnvironmentKey { get; set; } = true;
    public bool Toggle { get; set; }
    public bool CopyOnly { get; set; }
    public int Microphone { get; set; } = -1;
    public string[] Glossary { get; set; } = [];

    [JsonIgnore] public string Root { get; private set; } = "";
    public static string UserDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TalkToMe");
    [JsonIgnore] private string SettingsFile { get; set; } = "";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static string PreferenceFile(string root, bool useUserDirectory)
    {
        var file = Path.Combine(Path.GetFullPath(root), "settings.json");
        return useUserDirectory && !File.Exists(file) ? Path.Combine(UserDirectory, "settings.json") : file;
    }
    public static Settings Load(string root, bool useUserDirectory = false)
    {
        var file = PreferenceFile(root, useUserDirectory);
        string source = File.Exists(file) ? file : Path.Combine(root, "settings.example.json");
        var config = File.Exists(source) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(source), Json)
            ?? throw new InvalidDataException("Settings must be a JSON object.") : Defaults(root);
        config.SettingsFile = file;
        config.Root = Path.GetFullPath(root);
        config.Validate();
        return config;
    }

    public static Settings Defaults(string root, bool useUserDirectory = false) => new()
    {
        Root = Path.GetFullPath(root), SettingsFile = PreferenceFile(root, useUserDirectory), BundledSpeech = true, Translation = "qwen2"
    };
    public Settings Draft() => (Settings)MemberwiseClone();
    public void Validate()
    {
        if (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 ||
            uri.UserInfo.Length + uri.Query.Length + uri.Fragment.Length != 0)
            throw new InvalidDataException("serverUrl must be an HTTP(S) base URL, for example http://127.0.0.1:8080/v1.");
        if (SpeechModel is not ("base" or "speech") || Translation is not ("server" or "transcript" or "qwen08" or "qwen2" or "qwen4") ||
            Language is not ("auto" or "en" or "ru") || TimeoutSeconds is < 1 or > 600 ||
            !double.IsFinite(Temperature) || Temperature is < 0 or > 2 || string.IsNullOrWhiteSpace(Model) || Glossary is null ||
            RecordingMinutes is < 1 or > 60 || Microphone < -1 || string.IsNullOrWhiteSpace(Python) ||
            string.IsNullOrWhiteSpace(WhisperDirectory) || string.IsNullOrWhiteSpace(Device) || string.IsNullOrWhiteSpace(ComputeType))
            throw new InvalidDataException("Check the speech, server and recording settings.");
        if (string.IsNullOrWhiteSpace(Shortcut) || string.IsNullOrWhiteSpace(CancelShortcut))
            throw new InvalidDataException("Enter a recording and a cancel shortcut.");
        if (TalkToMe.Shortcut.Parse(Shortcut) == TalkToMe.Shortcut.Parse(CancelShortcut))
            throw new InvalidDataException("Choose different record and cancel shortcuts.");
        if (LocalServer is {} local && (string.IsNullOrWhiteSpace(local.ModelFile) ||
            local.ContextSize < 512 || (local.GpuLayers != "all" && (!int.TryParse(local.GpuLayers, out int n) || n < 0))))
            throw new InvalidDataException("Choose a GGUF, context size and GPU layer count.");
    }

    private byte[] KeyScope() => Encoding.UTF8.GetBytes(new Uri(ServerUrl).GetLeftPart(UriPartial.Authority));
    public string? ReadApiKey() => string.IsNullOrEmpty(ProtectedApiKey)
        ? (UseEnvironmentKey ? Environment.GetEnvironmentVariable("TALKTOME_API_KEY") : null)
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedApiKey), KeyScope(), DataProtectionScope.CurrentUser));
    public void SetApiKey(string key)
    {
        ProtectedApiKey = key.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), KeyScope(), DataProtectionScope.CurrentUser));
        UseEnvironmentKey = false;
    }

    public string Resolve(string path) => Path.GetFullPath(path, Root);

    public void Save()
    {
        var file = SettingsFile.Length > 0 ? SettingsFile : Resolve("settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(file + ".tmp", file, true);
    }
}

internal sealed record LocalServerSettings(string Executable, string ModelFile, int ContextSize = 8192, string GpuLayers = "all");
