#include "WhisperBridge.h"
#include "whisper.h"
#include <atomic>
#include <cstring>
#include <string>

void *ttm_open(const char *path, bool metal) {
    auto params = whisper_context_default_params();
    params.use_gpu = metal;
    return whisper_init_from_file_with_params(path, params);
}
void ttm_close(void *model) { whisper_free(static_cast<whisper_context *>(model)); }
void *ttm_new_job() { return new std::atomic_bool(false); }
void ttm_cancel(void *job) { static_cast<std::atomic_bool *>(job)->store(true); }
void ttm_free_job(void *job) { delete static_cast<std::atomic_bool *>(job); }

char *ttm_transcribe(void *model, void *job, const float *pcm, int count, const char *language,
                     const char *glossary, const char *vad_path, int threads) {
    auto *ctx = static_cast<whisper_context *>(model);
    auto p = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    p.n_threads = threads;
    p.language = language;
    p.translate = false;
    p.no_context = true;
    p.no_timestamps = true;
    p.print_realtime = p.print_progress = p.print_timestamps = false;
    p.suppress_nst = true;
    p.initial_prompt = glossary[0] ? glossary : nullptr;
    p.vad = vad_path[0] != '\0';
    p.vad_model_path = vad_path;
    p.abort_callback = [](void *token) { return static_cast<std::atomic_bool *>(token)->load(); };
    p.abort_callback_user_data = job;
    if (whisper_full(ctx, p, pcm, count) != 0) return nullptr;
    std::string text;
    for (int i = 0; i < whisper_full_n_segments(ctx); ++i) text += whisper_full_get_segment_text(ctx, i);
    return strdup(text.c_str());
}
