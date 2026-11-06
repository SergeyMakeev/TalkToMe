using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace TalkToMe;

internal sealed class EnglishEditor(Settings config) : IDisposable
{
    private LocalServer server = new(config);
    internal int? ProcessId => server.ProcessId;
    private static readonly JsonSerializerOptions TranscriptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public Task PrepareAsync(CancellationToken token) => server.PrepareAsync(token);
    private HttpClient http = CreateClient(config);
    private static HttpClient CreateClient(Settings s) => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !s.LocalTranslation })
        { Timeout = TimeSpan.FromSeconds(s.LocalTranslation ? Math.Max(300, s.TimeoutSeconds) : s.TimeoutSeconds) };

    public void Reconfigure(Settings next)
    {
        if (next.Translation != config.Translation || next.UseGpu != config.UseGpu || next.LocalServer != config.LocalServer || next.ServerUrl != config.ServerUrl || next.Model != config.Model ||
            next.ProtectedApiKey != config.ProtectedApiKey || next.UseEnvironmentKey != config.UseEnvironmentKey)
        {
            server.Dispose();
            server = new(next);
        }
        if (next.TimeoutSeconds != config.TimeoutSeconds || next.LocalTranslation != config.LocalTranslation)
        {
            http.Dispose();
            http = CreateClient(next);
        }
        config = next;
    }

    internal static readonly string Instruction = ReadInstruction();
    private static readonly JsonElement LocalEditing = ReadLocalEditing();

    private static JsonElement ReadLocalEditing()
    {
        using var stream = typeof(EnglishEditor).Assembly.GetManifestResourceStream("TalkToMe.LocalEditing.json")!;
        using var json = JsonDocument.Parse(stream);
        return json.RootElement.Clone();
    }

    internal static Dictionary<string, object> RequestBody(string transcript, Settings settings, string model)
    {
        bool defaults = string.IsNullOrWhiteSpace(settings.Prompt);
        string instruction = defaults ? Instruction : settings.Prompt;
        if (settings.LocalTranslation) instruction += "\n" + LocalEditing.GetProperty("instruction").GetString();
        var messages = new List<object> { new { role = "system", content = instruction } };
        // Tiny 0.8B tends to copy examples verbatim. Larger models benefit from them.
        if (settings.LocalTranslation && settings.Translation != "qwen08" && defaults)
            messages.AddRange(LocalEditing.GetProperty("examples").EnumerateArray().Select(x => (object)x));
        messages.Add(new { role = "user", content = JsonSerializer.Serialize(new { transcript, glossary = settings.Glossary }, TranscriptJson) });
        var body = new Dictionary<string, object> {
            ["model"] = model, ["messages"] = messages,
            ["chat_template_kwargs"] = new { enable_thinking = false },
            ["temperature"] = settings.Temperature,
            ["max_tokens"] = Math.Clamp(transcript.Length + 96, 160, 4096), ["stream"] = false
        };
        if (settings.LocalTranslation) body["response_format"] = LocalEditing.GetProperty("response_format");
        return body;
    }

    private static string ReadInstruction()
    {
        using var stream = typeof(EnglishEditor).Assembly.GetManifestResourceStream("TalkToMe.EnglishPrompt.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    public async Task<string> EditAsync(string transcript, CancellationToken token, ProcessingMetrics? metrics = null)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "";
        if (config.Translation == "transcript") return transcript;
        try
        {
            try { return await RequestAsync(transcript, token, metrics); }
            catch (Exception ex) when (!token.IsCancellationRequested && server.RunningGpu &&
                (ex is IOException or TaskCanceledException || ex is HttpRequestException httpError &&
                (httpError.StatusCode == null || (int)httpError.StatusCode >= 500)))
            {
                if (!await server.FallBackAsync(token)) throw;
                return await RequestAsync(transcript, token, metrics);
            }
        }
        finally
        {
            if (metrics != null) { metrics.TranslationBackend = server.Backend; metrics.TranslationFallbackReason = server.FallbackReason; }
        }
    }

    private async Task<string> RequestAsync(string transcript, CancellationToken token, ProcessingMetrics? metrics)
    {
        using (metrics?.Measure("translation_ready")) await PrepareAsync(token);
        using var inference = metrics?.Measure("translation_request");
        using var gpuDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (server.RunningGpu) gpuDeadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        token = gpuDeadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        var key = server.Key;
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(RequestBody(transcript, config, server.Model));
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"llama-server returned HTTP {(int)response.StatusCode}. Check serverUrl, model, API key and context size.", null, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        metrics?.ReadTranslationDetails(json.RootElement);
        return CompleteText(json.RootElement, config.LocalTranslation, transcript);
    }

    internal static string CompleteText(JsonElement response, bool structured = false, string? transcript = null)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() != 1 || choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("finish_reason", out var reason) ||
            reason.ValueKind != JsonValueKind.String || reason.GetString() != "stop")
            throw new InvalidDataException("llama-server returned an incomplete generation. Retry or copy the transcript.");
        if (!choices[0].TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
            new[] { "reasoning", "reasoning_content", "tool_calls", "refusal" }.Any(k =>
                message.TryGetProperty(k, out var value) && !string.IsNullOrWhiteSpace(value.ToString())))
            throw new InvalidDataException("The translator returned reasoning or an invalid result. Retry or copy the transcript.");
        string text = content.GetString()!.Trim();
        if (structured)
        {
            try
            {
                using var result = JsonDocument.Parse(text);
                var root = result.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 ||
                    !root.TryGetProperty("text", out var final) || final.ValueKind != JsonValueKind.String)
                    throw new JsonException();
                text = final.GetString()!.Trim();
            }
            catch (JsonException)
            { throw new InvalidDataException("The translator returned commentary or an invalid result. Try Qwen 2B or 4B, or copy the transcript."); }
            if (transcript != null && text.Length > Math.Max(256L, transcript.Length * 4L))
                throw new InvalidDataException("The translator expanded the dictation unexpectedly. Try a larger model or copy the transcript.");
        }
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')) ||
            Regex.IsMatch(text, @"</?(think|thinking|reasoning)(?:\s[^>]*)?>|<\|(?:im_|channel|message|endof)", RegexOptions.IgnoreCase))
            throw new InvalidDataException("llama-server returned empty output or reasoning. Check --reasoning off and the Qwen template.");
        return text;
    }

    public void Dispose() { server.Dispose(); http.Dispose(); }
}
