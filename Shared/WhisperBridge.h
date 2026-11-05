#pragma once
#include <stdint.h>
#include <stdbool.h>
#ifdef __cplusplus
extern "C" {
#endif
void *ttm_open(const char *path, bool metal);
void ttm_close(void *model);
void ttm_free_text(char *text);
void *ttm_new_job(void);
void ttm_cancel(void *job);
void ttm_free_job(void *job);
// Returns a malloc-owned UTF-8 transcript. A null result means failure or cancellation.
char *ttm_transcribe(void *model, void *job, const float *pcm, int count, const char *language,
                     const char *glossary, const char *vad_path, int threads);
#ifdef __cplusplus
}
#endif
