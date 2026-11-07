#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <ole2.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <algorithm>
#include <cwctype>
#include <iostream>
#include <optional>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;
struct Text {
    BSTR value = nullptr;
    ~Text() { SysFreeString(value); }
    std::wstring string() const { return value ? std::wstring(value, SysStringLen(value)) : L""; }
};
struct Variant {
    VARIANT value{};
    ~Variant() { VariantClear(&value); }
};
struct Array {
    SAFEARRAY* value = nullptr;
    ~Array() { if (value) SafeArrayDestroy(value); }
};
struct Snapshot { HWND window, focus; std::vector<int> id; bool space = false; };

static bool terminal(std::wstring name) {
    std::transform(name.begin(), name.end(), name.begin(), towlower);
    for (auto word : {L"terminal", L"consolewindowclass", L"virtualconsole", L"cascadia_hosting", L"mintty", L"putty", L"conemu", L"xterm"})
        if (name.find(word) != std::wstring::npos) return true;
    return false;
}
static bool character(wchar_t ch, WORD flags) {
    WORD type{};
    return GetStringTypeW(CT_CTYPE1, &ch, 1, &type) && (type & flags) != 0;
}
static bool spaceAtCaret(IUIAutomationElement* element) {
    ComPtr<IUIAutomationTextPattern> pattern;
    if (FAILED(element->GetCurrentPatternAs(UIA_TextPatternId, IID_PPV_ARGS(pattern.GetAddressOf()))) || !pattern) return false;
    ComPtr<IUIAutomationTextRangeArray> selection;
    int length = 0;
    if (FAILED(pattern->GetSelection(&selection)) || !selection || FAILED(selection->get_Length(&length)) || length != 1) return false;
    ComPtr<IUIAutomationTextRange> range, right;
    int difference = 0, moved = 0;
    if (FAILED(selection->GetElement(0, &range)) || !range ||
        FAILED(range->CompareEndpoints(TextPatternRangeEndpoint_Start, range.Get(), TextPatternRangeEndpoint_End, &difference)) || difference != 0 ||
        FAILED(range->Clone(&right)) || !right) return false;
    if (SUCCEEDED(right->MoveEndpointByUnit(TextPatternRangeEndpoint_End, TextUnit_Character, 1, &moved)) && moved == 1) {
        Text text;
        if (FAILED(right->GetText(2, &text.value))) return false;
        for (wchar_t ch : text.string()) if (!character(ch, C1_SPACE)) return false;
    }
    if (FAILED(range->MoveEndpointByUnit(TextPatternRangeEndpoint_Start, TextUnit_Character, -1, &moved)) || moved != -1) return false;
    Text text;
    if (FAILED(range->GetText(2, &text.value))) return false;
    auto left = text.string();
    return !left.empty() && (character(left.back(), C1_ALPHA | C1_DIGIT) || std::wstring(L".!?").find(left.back()) != std::wstring::npos);
}

static std::optional<Snapshot> capture(IUIAutomation* automation, DWORD owner) {
    HWND window = GetForegroundWindow();
    DWORD pid = 0, thread = GetWindowThreadProcessId(window, &pid);
    GUITHREADINFO gui{sizeof(gui)};
    if (!window || !thread || pid == owner || !GetGUIThreadInfo(thread, &gui) || !gui.hwndFocus) return {};
    wchar_t className[256]{}; GetClassNameW(window, className, 256);
    if (terminal(className)) return {};
    ComPtr<IUIAutomationElement> element;
    if (FAILED(automation->GetFocusedElement(&element)) || !element) return {};
    BOOL password = TRUE, enabled = FALSE;
    CONTROLTYPEID type = 0;
    if (FAILED(element->get_CurrentIsPassword(&password)) || password ||
        FAILED(element->get_CurrentIsEnabled(&enabled)) || !enabled ||
        FAILED(element->get_CurrentControlType(&type))) return {};
    ComPtr<IUIAutomationValuePattern> value;
    ComPtr<IUIAutomationTextPattern> pattern;
    if (SUCCEEDED(element->GetCurrentPatternAs(UIA_ValuePatternId, IID_PPV_ARGS(value.GetAddressOf()))) && value) {
        BOOL readOnly = TRUE;
        if (FAILED(value->get_CurrentIsReadOnly(&readOnly)) || readOnly) return {};
    } else if (SUCCEEDED(element->GetCurrentPatternAs(UIA_TextPatternId, IID_PPV_ARGS(pattern.GetAddressOf()))) && pattern) {
        ComPtr<IUIAutomationTextRange> document;
        Variant readOnly;
        if (FAILED(pattern->get_DocumentRange(&document)) || !document ||
            FAILED(document->GetAttributeValue(UIA_IsReadOnlyAttributeId, &readOnly.value))) return {};
        if ((readOnly.value.vt == VT_BOOL && readOnly.value.boolVal != VARIANT_FALSE) ||
            (readOnly.value.vt != VT_BOOL && type != UIA_EditControlTypeId)) return {};
    } else if (type != UIA_EditControlTypeId) return {};

    ComPtr<IUIAutomationTreeWalker> walker;
    if (FAILED(automation->get_RawViewWalker(&walker)) || !walker) return {};
    ComPtr<IUIAutomationElement> ancestor = element;
    for (int depth = 0; ancestor; ++depth) {
        if (depth >= 64) return {}; // A broken provider must not loop indefinitely.
        Text name, identifier, title;
        UIA_HWND handle{};
        if (FAILED(ancestor->get_CurrentClassName(&name.value)) || FAILED(ancestor->get_CurrentAutomationId(&identifier.value)) ||
            FAILED(ancestor->get_CurrentNativeWindowHandle(&handle))) return {};
        if (terminal(name.string()) || terminal(identifier.string())) return {};
        if (depth == 0 && (FAILED(ancestor->get_CurrentName(&title.value)) || terminal(title.string()))) return {};
        if (reinterpret_cast<HWND>(handle) == window) break;
        ComPtr<IUIAutomationElement> parent;
        if (FAILED(walker->GetParentElement(ancestor.Get(), &parent))) return {};
        ancestor = parent;
    }
    Array runtime;
    LONG lower = 0, upper = -1;
    if (FAILED(element->GetRuntimeId(&runtime.value)) || !runtime.value || SafeArrayGetDim(runtime.value) != 1 ||
        FAILED(SafeArrayGetLBound(runtime.value, 1, &lower)) || FAILED(SafeArrayGetUBound(runtime.value, 1, &upper)) ||
        upper < lower || upper - lower >= 128) return {};
    Snapshot result{window, gui.hwndFocus};
    for (LONG index = lower; index <= upper; ++index) {
        int part = 0;
        if (FAILED(SafeArrayGetElement(runtime.value, &index, &part))) return {};
        result.id.push_back(part);
    }
    result.space = spaceAtCaret(element.Get());
    GUITHREADINFO current{sizeof(current)};
    if (GetForegroundWindow() != window || !GetGUIThreadInfo(thread, &current) || current.hwndFocus != gui.hwndFocus) return {};
    return result;
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3 || std::wstring(argv[1]) != L"--owner") return 2;
    DWORD owner = wcstoul(argv[2], nullptr, 10);
    if (!owner) return 2;
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) return 3;
    int result = 0;
    {
        ComPtr<IUIAutomation> automation;
        if (FAILED(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(automation.GetAddressOf())))) return 4;
        std::cout << "ready\n" << std::flush;
        std::string command;
        while (std::getline(std::cin, command)) {
            if (command != "capture") { result = 2; break; }
            // All native objects are released on this MTA before the response is
            // written. No UIAutomation SafeHandles/finalizers live in the UI app.
            auto snapshot = capture(automation.Get(), owner);
            if (!snapshot) std::cout << "none";
            else {
                std::cout << reinterpret_cast<uintptr_t>(snapshot->window) << ' ' << reinterpret_cast<uintptr_t>(snapshot->focus)
                          << ' ' << snapshot->space << ' ' << snapshot->id.size();
                for (int part : snapshot->id) std::cout << ' ' << part;
            }
            std::cout << '\n' << std::flush;
        }
    }
    CoUninitialize();
    return result;
}
