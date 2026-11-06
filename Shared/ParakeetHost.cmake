# Included by NeMo's top-level project(), keeping its source-relative build intact.
add_executable(TalkToMeParakeetHost "${CMAKE_CURRENT_LIST_DIR}/ParakeetHost.cpp")
target_compile_features(TalkToMeParakeetHost PRIVATE cxx_std_17)
target_include_directories(TalkToMeParakeetHost PRIVATE
    "${CMAKE_SOURCE_DIR}/include" "${CMAKE_SOURCE_DIR}/llama.cpp/vendor/nlohmann")
target_link_libraries(TalkToMeParakeetHost PRIVATE nemo_speech_asr_c)
set_target_properties(TalkToMeParakeetHost PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    BUILD_WITH_INSTALL_RPATH ON INSTALL_RPATH "@loader_path")
