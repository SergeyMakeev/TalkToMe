#include "WhisperBridge.h"
#include "whisper.h"
#include "ggml-backend.h"
#include "json.hpp"
#include <atomic>
#include <cstdio>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#ifdef _WIN32
#define NOMINMAX
#include <windows.h>
#include <cstdlib>
#endif

using nlohmann::json;
static std::atomic_bool cuda_used(false), cuda_failed(false);

static void log_message(enum ggml_log_level, const char * text, void *) {
    // Pinned whisper.cpp logs this immediately before initializing its chosen backend.
    std::string message(text);
    if (message.find("whisper_backend_init_gpu: using CUDA") != std::string::npos) cuda_used = true;
    if (message.find("whisper_backend_init_gpu: failed to initialize") != std::string::npos) cuda_failed = true;
    std::fputs(text, stderr);
}

static json devices() {
    ggml_backend_load_all();
    auto found = json::array();
    for (size_t i = 0; i < ggml_backend_dev_count(); ++i) {
        auto dev = ggml_backend_dev_get(i);
        std::string id(ggml_backend_dev_name(dev));
        if (ggml_backend_dev_type(dev) != GGML_BACKEND_DEVICE_TYPE_GPU || id.rfind("CUDA", 0) != 0) continue;
        size_t free = 0, total = 0;
        ggml_backend_dev_memory(dev, &free, &total);
        found.push_back({{"id", id}, {"name", ggml_backend_dev_description(dev)}, {"memory", total}});
    }
    return found;
}

static std::vector<float> samples(const std::string & encoded) {
    // JSON byte[] is base64 PCM16, mono, 16 kHz. Bound memory to the app's 60-minute recording limit.
    if (encoded.size() % 4 || encoded.size() > 153600000) throw std::runtime_error("Invalid audio length");
    const std::string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::vector<unsigned char> bytes;
    bytes.reserve(encoded.size() / 4 * 3);
    unsigned int bits = 0;
    int count = 0;
    for (char c : encoded) {
        if (c == '=') break;
        auto digit = alphabet.find(c);
        if (digit == std::string::npos) throw std::runtime_error("Invalid audio encoding");
        bits = (bits << 6) | static_cast<unsigned int>(digit);
        count += 6;
        if (count >= 8) { count -= 8; bytes.push_back(static_cast<unsigned char>((bits >> count) & 255)); }
    }
    if (bytes.size() % 2) throw std::runtime_error("Invalid PCM16 audio");
    std::vector<float> pcm(bytes.size() / 2);
    for (size_t i = 0; i < pcm.size(); ++i)
        pcm[i] = static_cast<int16_t>(bytes[2*i] | (static_cast<unsigned int>(bytes[2*i+1]) << 8)) / 32768.0f;
    return pcm;
}

int main(int argc, char ** argv) {
#ifdef _WIN32
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    _set_abort_behavior(0, _WRITE_ABORT_MSG | _CALL_REPORTFAULT);
#endif
    whisper_log_set(log_message, nullptr);
    void * model = nullptr;
    try {
        if (argc == 2 && std::string(argv[1]) == "--probe") {
            std::cout << json({{"devices", devices()}}).dump() << std::endl;
            return 0;
        }
        // Paths travel over UTF-8 JSON stdin, avoiding Windows ANSI argv path conversion.
        if (argc != 2 || std::string(argv[1]) != "--worker") return 2;
        std::string line;
        if (!std::getline(std::cin, line)) return 0;
        auto setup = json::parse(line);
        bool gpu = setup.at("gpu").get<bool>();
        auto available = gpu ? devices() : json::array();
        if (gpu && available.empty()) throw std::runtime_error("No CUDA GPU available");
        auto path = setup.at("model").get<std::string>();
        model = ttm_open(path.c_str(), gpu);
        if (!model || (gpu && (!cuda_used || cuda_failed))) throw std::runtime_error("Speech backend initialization failed");
        std::cout << json({{"ready", true}, {"backend", gpu ? "CUDA" : "CPU"},
            {"device", gpu ? available[0]["name"].get<std::string>() : "CPU"}}).dump() << std::endl;
        while (std::getline(std::cin, line)) {
            auto request = json::parse(line);
            auto pcm = samples(request.at("pcm").get<std::string>());
            auto language = request.at("language").get<std::string>();
            auto glossary = request.at("glossary").get<std::string>();
            auto vad = request.at("vad").get<std::string>();
            int threads = request.at("threads").get<int>();
            if (threads < 1 || threads > 64) throw std::runtime_error("Invalid thread count");
            auto job = ttm_new_job();
            char * text = ttm_transcribe(model, job, pcm.data(), static_cast<int>(pcm.size()),
                language.c_str(), glossary.c_str(), vad.c_str(), threads);
            ttm_free_job(job);
            if (!text) throw std::runtime_error("Transcription failed");
            auto reply = json({{"text", text}}).dump();
            ttm_free_text(text);
            std::cout << reply << std::endl;
        }
        ttm_close(model);
        return 0;
    } catch (const std::exception &) {
        // Native error logs remain on stderr. Do not put user-provided paths or text into diagnostics.
        std::cout << json({{"error", "The speech engine could not complete the request."}}).dump() << std::endl;
        if (model) ttm_close(model);
        return 1;
    }
}
