#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dbghelp.h>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <string>

// Capture second-chance failures outside the faulting process. No registry changes,
// administrator rights, upload, or symbols-server connection are needed.
static void dump(HANDLE process, DWORD pid, const DEBUG_EVENT &event, const std::filesystem::path &directory) {
    SYSTEMTIME now{}; GetSystemTime(&now);
    wchar_t name[128];
    swprintf_s(name, L"crash-%04u%02u%02u-%02u%02u%02u-%lu", now.wYear, now.wMonth, now.wDay,
               now.wHour, now.wMinute, now.wSecond, pid);
    auto base = directory / name;
    std::ofstream report(base.wstring() + L".txt");
    const auto &fault = event.u.Exception.ExceptionRecord;
    report << "TalkToMe native crash\nProcess: " << pid << "\nThread: " << event.dwThreadId
           << "\nException: 0x" << std::hex << fault.ExceptionCode
           << "\nAddress: 0x" << reinterpret_cast<uintptr_t>(fault.ExceptionAddress) << "\n";
    HANDLE thread = OpenThread(THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, event.dwThreadId);
    CONTEXT context{}; context.ContextFlags = CONTEXT_ALL;
    bool gotContext = thread && GetThreadContext(thread, &context);
    EXCEPTION_RECORD record = fault;
    EXCEPTION_POINTERS pointers{&record, &context};
    MINIDUMP_EXCEPTION_INFORMATION exception{event.dwThreadId, &pointers, FALSE};
    HANDLE file = CreateFileW((base.wstring() + L".dmp").c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file != INVALID_HANDLE_VALUE) {
        auto flags = static_cast<MINIDUMP_TYPE>(MiniDumpWithThreadInfo | MiniDumpWithUnloadedModules |
            MiniDumpWithIndirectlyReferencedMemory | MiniDumpWithProcessThreadData);
        if (!MiniDumpWriteDump(process, pid, file, flags, gotContext ? &exception : nullptr, nullptr, nullptr))
            report << "Dump failed: Win32 " << std::dec << GetLastError() << "\n";
        else report << "Memory dump saved alongside this report. It may contain private data.\n";
        CloseHandle(file);
    } else report << "Cannot create dump: Win32 " << std::dec << GetLastError() << "\n";
    SymSetOptions(SYMOPT_DEFERRED_LOADS | SYMOPT_UNDNAME | SYMOPT_FAIL_CRITICAL_ERRORS | SYMOPT_NO_PROMPTS);
    if (gotContext && SymInitialize(process, "", TRUE)) {
        STACKFRAME64 frame{};
        frame.AddrPC = {context.Rip, 0, AddrModeFlat};
        frame.AddrFrame = {context.Rbp, 0, AddrModeFlat};
        frame.AddrStack = {context.Rsp, 0, AddrModeFlat};
        report << "Native callstack (managed exception stacks are in events.log):\n";
        for (int i = 0; i < 64 && frame.AddrPC.Offset; ++i) {
            IMAGEHLP_MODULE64 module{}; module.SizeOfStruct = sizeof(module);
            report << "  0x" << std::hex << frame.AddrPC.Offset;
            if (SymGetModuleInfo64(process, frame.AddrPC.Offset, &module))
                report << " " << module.ModuleName << "+0x" << frame.AddrPC.Offset - module.BaseOfImage;
            report << "\n";
            if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, thread, &frame, &context, nullptr,
                            SymFunctionTableAccess64, SymGetModuleBase64, nullptr)) break;
        }
        SymCleanup(process);
    }
    if (thread) CloseHandle(thread);
}

int wmain(int argc, wchar_t **argv) {
    if (argc != 4) return 2;
    DWORD pid = wcstoul(argv[1], nullptr, 10);
    std::filesystem::path directory(argv[2]);
    // Keep each launch separate: reopening the app must not erase the failure.
    FILETIME stamp{}; GetSystemTimeAsFileTime(&stamp);
    auto session = directory / (L"monitor-" + std::to_wstring(pid) + L"-" +
        std::to_wstring((static_cast<unsigned long long>(stamp.dwHighDateTime) << 32) | stamp.dwLowDateTime) + L".txt");
    std::ofstream monitor(session);
    monitor << "TalkToMe crash monitor; process " << pid << "\n" << std::flush;
    HANDLE ready = OpenEventW(EVENT_MODIFY_STATE, FALSE, argv[3]);
    if (!pid || !ready) return 3;
    if (!DebugActiveProcess(pid)) {
        monitor << "Could not attach: Win32 " << GetLastError() << "\n";
        SetEvent(ready); CloseHandle(ready); return 4;
    }
    if (!DebugSetProcessKillOnExit(FALSE)) {
        monitor << "Could not disable kill-on-monitor-exit: Win32 " << GetLastError() << "\n";
        DebugActiveProcessStop(pid);
        SetEvent(ready); CloseHandle(ready); return 4;
    }
    monitor << "Attached. App will survive if this monitor exits.\n" << std::flush;
    HANDLE process = nullptr;
    bool initialBreakpoint = true;
    bool captured = false;
    for (;;) {
        DEBUG_EVENT event{};
        if (!WaitForDebugEventEx(&event, INFINITE)) {
            monitor << "WaitForDebugEvent failed: Win32 " << GetLastError() << "\n";
            break;
        }
        DWORD status = DBG_CONTINUE;
        switch (event.dwDebugEventCode) {
        case CREATE_PROCESS_DEBUG_EVENT:
            process = event.u.CreateProcessInfo.hProcess;
            // Debug-event process/thread handles are closed by Windows when the
            // corresponding exit event is continued. Do not close them twice.
            if (event.u.CreateProcessInfo.hFile) CloseHandle(event.u.CreateProcessInfo.hFile);
            SetEvent(ready); CloseHandle(ready); ready = nullptr;
            break;
        case CREATE_THREAD_DEBUG_EVENT: break;
        case LOAD_DLL_DEBUG_EVENT:
            if (event.u.LoadDll.hFile) CloseHandle(event.u.LoadDll.hFile);
            break;
        case EXCEPTION_DEBUG_EVENT:
            if (initialBreakpoint && event.u.Exception.ExceptionRecord.ExceptionCode == EXCEPTION_BREAKPOINT) {
                initialBreakpoint = false;
            } else {
                status = DBG_EXCEPTION_NOT_HANDLED;
                if (!event.u.Exception.dwFirstChance && process && !captured) {
                    captured = true;
                    monitor << "Second-chance exception: 0x" << std::hex
                            << event.u.Exception.ExceptionRecord.ExceptionCode << "\n" << std::flush;
                    try { dump(process, pid, event, directory); } catch (...) { }
                }
            }
            break;
        case EXIT_PROCESS_DEBUG_EVENT:
            monitor << "Process exited: 0x" << std::hex << event.u.ExitProcess.dwExitCode
                    << "; crash captured: " << (captured ? "yes" : "no") << "\n" << std::flush;
            ContinueDebugEvent(event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
            if (ready) CloseHandle(ready);
            return 0;
        }
        if (!ContinueDebugEvent(event.dwProcessId, event.dwThreadId, status)) {
            monitor << "ContinueDebugEvent failed: Win32 " << std::dec << GetLastError() << "\n";
            break;
        }
    }
    DebugActiveProcessStop(pid);
    if (ready) { SetEvent(ready); CloseHandle(ready); }
    return 5;
}
