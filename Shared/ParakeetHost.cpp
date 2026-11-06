#include "nemo_speech/asr.h"
#include "json.hpp"
#include <algorithm>
#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <cstdlib>
#endif

using nlohmann::json;

static std::vector<float> samples(const std::string & encoded) {
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
    nemo_speech_asr_recognizer * recognizer = nullptr;
    try {
        if (argc != 2 || std::string(argv[1]) != "--worker") return 2;
        std::string line;
        if (!std::getline(std::cin, line)) return 0;
        auto setup = json::parse(line);
        bool gpu = setup.at("gpu").get<bool>();
        auto path = setup.at("model").get<std::string>();
        nemo_speech_asr_backend_config backend{};
        backend.size = sizeof(backend); backend.gpu = gpu ? 0 : -1;
        nemo_speech_asr_model_config model{};
        model.size = sizeof(model); model.path = path.c_str();
        nemo_speech_asr_recognizer_config config{};
        config.size = sizeof(config); config.backend = &backend; config.model = &model;
        // NeMo rejects unavailable/failed GPU initialization; the app retries with CPU.
        if (nemo_speech_asr_create(&config, &recognizer) != NEMO_SPEECH_ASR_OK || !recognizer)
            throw std::runtime_error("Could not load speech model");
#ifdef __APPLE__
        const char * accelerator = "Metal";
#else
        const char * accelerator = "CUDA";
#endif
        std::cout << json({{"ready", true}, {"backend", gpu ? accelerator : "CPU"},
            {"device", gpu ? accelerator : "CPU"}}).dump() << std::endl;
        while (std::getline(std::cin, line)) {
            auto request = json::parse(line);
            auto pcm = samples(request.at("pcm").get<std::string>());
            auto options = nemo_speech_asr_recognition_options_default();
            options.enable_automatic_punctuation = true;
            auto language = request.value("language", "auto");
            options.language_code = language == "auto" ? nullptr : language.c_str();
            // Native TDT decoding includes punctuation. Do not apply a separate PnC model.
            nemo_speech_asr_result * result = nullptr;
            auto status = nemo_speech_asr_recognize_f32(recognizer, &options, pcm.data(), pcm.size(), 16000, &result);
            std::string text;
            if (result && nemo_speech_asr_result_alternative_count(result))
                text = nemo_speech_asr_result_transcript(result, 0);
            if (result) nemo_speech_asr_result_destroy(result);
            if (status != NEMO_SPEECH_ASR_OK) throw std::runtime_error("Transcription failed");
            std::cout << json({{"text", text}}).dump() << std::endl;
        }
        nemo_speech_asr_destroy(recognizer);
        return 0;
    } catch (const std::exception &) {
        std::cout << json({{"error", "The speech engine could not complete the request."}}).dump() << std::endl;
        if (recognizer) nemo_speech_asr_destroy(recognizer);
        return 1;
    }
}
