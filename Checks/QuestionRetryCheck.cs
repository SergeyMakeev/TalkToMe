using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using TalkToMe;

internal static class QuestionRetryCheck
{
    private sealed class Replies(bool repair) : HttpMessageHandler
    {
        internal int Count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Count++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            string input = json.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString()!;
            if ((Count == 2) != input.StartsWith("Preserve the speaker's questions"))
                throw new Exception("Question retry reminder missing or sent on the first request.");
            string text = repair && Count == 2 ? "Why did it fail?" : "It failed.";
            var result = new { choices = new[] { new { finish_reason = "stop", message = new { content = text } } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
    internal static async Task Run()
    {
        var settings = Settings.Defaults(Environment.CurrentDirectory);
        settings.BundledSpeech = false; settings.Translation = "server"; settings.UseEnvironmentKey = false;
        foreach (bool repair in new[] { true, false })
        {
            var handler = new Replies(repair);
            using var editor = new EnglishEditor(settings, handler);
            try {
                string result = await editor.EditAsync("Why did it fail?", default);
                if (!repair || result != "Why did it fail?") throw new Exception("Lost question accepted.");
            } catch (InvalidDataException e) when (!repair && e.Message.Contains("removed a question")) { }
            if (handler.Count != 2) throw new Exception("Question retry was not bounded to two requests.");
        }
        settings.Prompt = "Custom instructions.";
        var custom = new Replies(false);
        using (var editor = new EnglishEditor(settings, custom))
            if (await editor.EditAsync("Why did it fail?", default) != "It failed." || custom.Count != 1)
                throw new Exception("Custom instructions were overridden by the question guard.");
    }
}
