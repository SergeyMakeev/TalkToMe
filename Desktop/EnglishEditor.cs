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
    private static readonly JsonSerializerOptions TranscriptJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public Task PrepareAsync(CancellationToken token) => server.PrepareAsync(token);
    private HttpClient http = CreateClient(config);
    private static HttpClient CreateClient(Settings s) => new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !s.LocalTranslation })
        { Timeout = TimeSpan.FromSeconds(s.LocalTranslation ? Math.Max(300, s.TimeoutSeconds) : s.TimeoutSeconds) };

    public void Reconfigure(Settings next)
    {
        if (next.Translation != config.Translation || next.LocalServer != config.LocalServer || next.ServerUrl != config.ServerUrl || next.Model != config.Model ||
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

    private static string ReadInstruction()
    {
        using var stream = typeof(EnglishEditor).Assembly.GetManifestResourceStream("TalkToMe.EnglishPrompt.txt")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    public async Task<string> EditAsync(string transcript, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return "";
        if (config.Translation == "transcript") return transcript;
        await PrepareAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Post, server.Endpoint.AbsoluteUri.TrimEnd('/') + "/chat/completions");
        var key = server.Key;
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new
        {
            model = server.Model,
            messages = new[] {
                new { role = "system", content = string.IsNullOrWhiteSpace(config.Prompt) ? Instruction : config.Prompt },
                new { role = "user", content = JsonSerializer.Serialize(new { transcript, glossary = config.Glossary }, TranscriptJson) }
            },
            chat_template_kwargs = new { enable_thinking = false },
            temperature = config.Temperature,
            max_tokens = Math.Clamp(transcript.Length + 64, 128, 4096),
            stream = false
        });
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"llama-server returned HTTP {(int)response.StatusCode}. Check serverUrl, model, API key and context size.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return CompleteText(json.RootElement);
    }

    internal static string CompleteText(JsonElement response)
    {
        if (!response.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() != 1 || !choices[0].TryGetProperty("finish_reason", out var reason) ||
            reason.GetString() != "stop")
            throw new InvalidDataException("llama-server returned an incomplete generation. Retry or copy the transcript.");
        var message = choices[0].GetProperty("message");
        var text = message.GetProperty("content").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')) ||
            Regex.IsMatch(text, @"</?(think|thinking|reasoning)(?:\s[^>]*)?>|<\|(?:im_|channel|message|endof)", RegexOptions.IgnoreCase) ||
            new[] { "reasoning", "reasoning_content" }.Any(k =>
                message.TryGetProperty(k, out var value) && !string.IsNullOrWhiteSpace(value.ToString())))
            throw new InvalidDataException("llama-server returned empty output or reasoning. Check --reasoning off and the Qwen template.");
        return text;
    }

    public void Dispose() { server.Dispose(); http.Dispose(); }
}
