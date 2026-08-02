#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include "include/cef_browser.h"
#include "include/cef_registration.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <climits>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>

namespace {

constexpr wchar_t kExpectedProcessName[] = L"cloudmusic.exe";
constexpr wchar_t kPlayerWindowClass[] = L"OrpheusBrowserHost";
constexpr wchar_t kCefWindowClass[] = L"CefBrowserWindow";
constexpr int kExpectedCefVersion[] = {
    91, 2, 2, 2376, 91, 0, 4472, 169
};
constexpr char kExpectedUniversalApiHash[] =
    "37d5f9f068cf9b5ecfb6d039fc3c5c56be3864ba";
constexpr char kExpectedPlatformApiHash[] =
    "306fdfb40c5dbdc34992b9a5669c199a64749d5c";

// CEF 91 branch 4472, validated by the API hash above:
// CefBrowserPlatformDelegate stores browser_ immediately after its vptr and
// web_contents_ pointer. The CefBrowserHost vtable declares
// SendDevToolsMessage as method 21.
constexpr size_t kPlatformDelegateBrowserOffset = 0x10;
constexpr size_t kSendDevToolsMessageSlot = 21;
constexpr size_t kAddDevToolsMessageObserverSlot = 23;
constexpr size_t kLastValidatedHostSlot = 59;
constexpr char kTrackEventBinding[] = "__awooNcmNativeEvent";

struct CefCBaseRefCounted {
  size_t size;
  void(__stdcall* add_ref)(CefCBaseRefCounted* self);
  int(__stdcall* release)(CefCBaseRefCounted* self);
  int(__stdcall* has_one_ref)(CefCBaseRefCounted* self);
  int(__stdcall* has_at_least_one_ref)(CefCBaseRefCounted* self);
};

struct CefCTask {
  CefCBaseRefCounted base;
  void(__stdcall* execute)(CefCTask* self);
};

using CefPostTask = int(__cdecl*)(int thread_id, CefCTask* task);
using CefVersionInfo = int(__cdecl*)(int entry);
using CefApiHash = const char* (__cdecl*)(int entry);
using SendDevToolsMessage = bool(__fastcall*)(
    void* self,
    const void* message,
    size_t message_size);
// libcef is built with Chromium's clang::trivial_abi scoped_refptr. On the
// Win64 ABI the return value still uses hidden storage, while the observer
// argument is passed as its contained raw pointer. The public standalone CEF
// headers intentionally provide a portable scoped_refptr replacement that
// MSVC passes by address, so invoking this virtual directly with CefRefPtr
// shifts the observer by one pointer level and faults inside libcef.
using AddDevToolsMessageObserverAbi = void(__fastcall*)(
    void* self,
    CefRefPtr<CefRegistration>* result,
    CefDevToolsMessageObserver* observer);

HMODULE g_libcef = nullptr;
uintptr_t g_libcef_begin = 0;
uintptr_t g_libcef_end = 0;
CefPostTask g_cef_post_task = nullptr;
std::atomic<long> g_bridge_state{0};
std::atomic<int> g_message_id{1};
std::atomic<long> g_event_watcher_state{0};
std::atomic<unsigned long long> g_devtools_message_count{0};
std::atomic<unsigned long> g_last_devtools_exception_code{0};
std::atomic<unsigned long long> g_last_devtools_exception_address{0};
wchar_t g_status[256] = L"initializing";
std::mutex g_track_event_mutex;
std::condition_variable g_track_event_condition;
unsigned long long g_track_event_sequence = 0;
unsigned long long g_track_event_tick = 0;
std::string g_track_event_payload;
std::mutex g_devtools_diagnostics_mutex;
std::string g_last_devtools_message;
std::string g_watcher_install_status = "not-started";
CefRefPtr<CefDevToolsMessageObserver> g_devtools_observer;
CefRefPtr<CefRegistration> g_devtools_registration;
void* g_observed_host = nullptr;
void* g_observer_abi_vtable[5]{};
std::atomic<int> g_cef_validation_mode{0};

bool IsReadableProtection(DWORD protect) {
  if ((protect & PAGE_GUARD) != 0 || (protect & PAGE_NOACCESS) != 0) {
    return false;
  }
  const DWORD base = protect & 0xFF;
  return base == PAGE_READONLY
      || base == PAGE_READWRITE
      || base == PAGE_WRITECOPY
      || base == PAGE_EXECUTE_READ
      || base == PAGE_EXECUTE_READWRITE
      || base == PAGE_EXECUTE_WRITECOPY;
}

bool IsReadableRange(const void* pointer, size_t size) {
  if (pointer == nullptr || size == 0) {
    return false;
  }
  MEMORY_BASIC_INFORMATION memory{};
  if (VirtualQuery(pointer, &memory, sizeof(memory)) != sizeof(memory)
      || memory.State != MEM_COMMIT
      || !IsReadableProtection(memory.Protect)) {
    return false;
  }
  const auto begin = reinterpret_cast<uintptr_t>(pointer);
  const auto end = begin + size;
  const auto region_end =
      reinterpret_cast<uintptr_t>(memory.BaseAddress) + memory.RegionSize;
  return end >= begin && end <= region_end;
}

bool IsLibCefExecutablePointer(const void* pointer) {
  if (pointer == nullptr) {
    return false;
  }
  const auto address = reinterpret_cast<uintptr_t>(pointer);
  if (address < g_libcef_begin || address >= g_libcef_end) {
    return false;
  }
  MEMORY_BASIC_INFORMATION memory{};
  if (VirtualQuery(pointer, &memory, sizeof(memory)) != sizeof(memory)
      || memory.State != MEM_COMMIT
      || (memory.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
    return false;
  }
  const DWORD base = memory.Protect & 0xFF;
  return base == PAGE_EXECUTE
      || base == PAGE_EXECUTE_READ
      || base == PAGE_EXECUTE_READWRITE
      || base == PAGE_EXECUTE_WRITECOPY;
}

void SetStatus(const wchar_t* value) {
  if (value == nullptr) {
    value = L"unknown";
  }
  wcsncpy_s(g_status, value, _TRUNCATE);
}

std::wstring CurrentProcessName() {
  wchar_t path[MAX_PATH]{};
  if (GetModuleFileNameW(nullptr, path, MAX_PATH) == 0) {
    return {};
  }
  const wchar_t* leaf = wcsrchr(path, L'\\');
  return leaf == nullptr ? path : leaf + 1;
}

bool ReadModuleRange(HMODULE module) {
  if (module == nullptr) {
    return false;
  }
  const auto begin = reinterpret_cast<uintptr_t>(module);
  if (!IsReadableRange(
          reinterpret_cast<const void*>(begin),
          sizeof(IMAGE_DOS_HEADER))) {
    return false;
  }
  const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(begin);
  if (dos->e_magic != IMAGE_DOS_SIGNATURE) {
    return false;
  }
  const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS64*>(
      begin + static_cast<uintptr_t>(dos->e_lfanew));
  if (!IsReadableRange(nt, sizeof(*nt))
      || nt->Signature != IMAGE_NT_SIGNATURE
      || nt->OptionalHeader.SizeOfImage == 0) {
    return false;
  }
  g_libcef_begin = begin;
  g_libcef_end = begin + nt->OptionalHeader.SizeOfImage;
  return g_libcef_end > g_libcef_begin;
}

bool ValidateCefVersion() {
  if (_wcsicmp(CurrentProcessName().c_str(), kExpectedProcessName) != 0) {
    SetStatus(L"refused: target is not cloudmusic.exe");
    return false;
  }

  for (int attempt = 0; attempt < 100; ++attempt) {
    g_libcef = GetModuleHandleW(L"libcef.dll");
    if (g_libcef != nullptr) {
      break;
    }
    Sleep(100);
  }
  if (g_libcef == nullptr || !ReadModuleRange(g_libcef)) {
    SetStatus(L"refused: libcef.dll is not loaded");
    return false;
  }

  const auto version_info = reinterpret_cast<CefVersionInfo>(
      GetProcAddress(g_libcef, "cef_version_info"));
  const auto api_hash = reinterpret_cast<CefApiHash>(
      GetProcAddress(g_libcef, "cef_api_hash"));
  g_cef_post_task = reinterpret_cast<CefPostTask>(
      GetProcAddress(g_libcef, "cef_post_task"));
  if (version_info == nullptr
      || api_hash == nullptr
      || g_cef_post_task == nullptr) {
    SetStatus(L"refused: required CEF exports are missing");
    return false;
  }

  int actual_version[8]{};
  bool exact_version = true;
  for (int index = 0; index < 8; ++index) {
    actual_version[index] = version_info(index);
    exact_version = exact_version
        && actual_version[index] == kExpectedCefVersion[index];
  }

  // CEF_API_HASH_UNIVERSAL is entry 0 and CEF_API_HASH_PLATFORM is entry 1.
  // These were historically read in reverse order, which made validation
  // fail and was then hidden by HELLO changing the bridge state back to ready.
  const char* universal = api_hash(0);
  const char* platform = api_hash(1);
  if (universal == nullptr
      || platform == nullptr
      || std::strcmp(universal, kExpectedUniversalApiHash) != 0
      || std::strcmp(platform, kExpectedPlatformApiHash) != 0) {
    wchar_t details[256]{};
    swprintf_s(
        details,
        L"refused: CEF API hash mismatch platform=%hs universal=%hs",
        platform == nullptr ? "null" : platform,
        universal == nullptr ? "null" : universal);
    SetStatus(details);
    return false;
  }

  // The CEF API hashes describe the public binary ABI and are a stronger
  // compatibility boundary than the descriptive patch/build numbers. Permit
  // another patch build only when both hashes and the CEF/Chromium major
  // versions still match. ResolveLiveHostObject and the first DevTools watcher
  // installation then act as non-persistent structural/runtime probes. A
  // changed ABI hash is never tried.
  if (!exact_version) {
    if (actual_version[0] != kExpectedCefVersion[0]
        || actual_version[4] != kExpectedCefVersion[4]) {
      SetStatus(L"refused: unsupported CEF major version");
      return false;
    }
    g_cef_validation_mode.store(2, std::memory_order_release);
    SetStatus(L"probing: compatible CEF API hash on an unknown patch build");
    return true;
  }

  g_cef_validation_mode.store(1, std::memory_order_release);
  return true;
}

std::wstring ReadWindowClass(HWND window) {
  wchar_t value[256]{};
  return GetClassNameW(window, value, _countof(value)) > 0
      ? value
      : L"";
}

struct WindowSearch {
  HWND player = nullptr;
  HWND cef = nullptr;
};

BOOL CALLBACK FindPlayerWindow(HWND window, LPARAM parameter) {
  auto* search = reinterpret_cast<WindowSearch*>(parameter);
  DWORD process_id = 0;
  GetWindowThreadProcessId(window, &process_id);
  if (process_id == GetCurrentProcessId()
      && ReadWindowClass(window) == kPlayerWindowClass) {
    search->player = window;
    return FALSE;
  }
  return TRUE;
}

BOOL CALLBACK FindCefWindow(HWND window, LPARAM parameter) {
  auto* search = reinterpret_cast<WindowSearch*>(parameter);
  if (ReadWindowClass(window) == kCefWindowClass) {
    search->cef = window;
    return FALSE;
  }
  return TRUE;
}

bool LooksLikeHostObject(void* host) {
  if (!IsReadableRange(host, sizeof(void*))) {
    return false;
  }
  __try {
    auto** vtable = *reinterpret_cast<void***>(host);
    if (!IsReadableRange(
            vtable,
            (kLastValidatedHostSlot + 1) * sizeof(void*))) {
      return false;
    }
    constexpr size_t slots[] = {0, 4, 7, 21, 22, 23, 59};
    for (const size_t slot : slots) {
      if (!IsLibCefExecutablePointer(vtable[slot])) {
        return false;
      }
    }
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}

void* ResolveLiveHostObject() {
  WindowSearch search{};
  EnumWindows(
      FindPlayerWindow,
      reinterpret_cast<LPARAM>(&search));
  if (search.player == nullptr) {
    SetStatus(L"waiting: player window is not available");
    return nullptr;
  }

  EnumChildWindows(
      search.player,
      FindCefWindow,
      reinterpret_cast<LPARAM>(&search));
  if (search.cef == nullptr) {
    SetStatus(L"waiting: CefBrowserWindow is not available");
    return nullptr;
  }

  auto* platform_delegate = reinterpret_cast<std::uint8_t*>(
      GetWindowLongPtrW(search.cef, GWLP_USERDATA));
  if (!IsReadableRange(
          platform_delegate,
          kPlatformDelegateBrowserOffset + sizeof(void*))) {
    SetStatus(L"waiting: CEF platform delegate is not available");
    return nullptr;
  }

  void* host = nullptr;
  __try {
    host = *reinterpret_cast<void**>(
        platform_delegate + kPlatformDelegateBrowserOffset);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    host = nullptr;
  }
  if (!LooksLikeHostObject(host)) {
    SetStatus(L"refused: live CEF host validation failed");
    return nullptr;
  }
  return host;
}

std::string StatusAsAscii() {
  std::string status;
  for (const wchar_t* cursor = g_status;
       *cursor != L'\0';
       ++cursor) {
    status.push_back(
        *cursor >= 0 && *cursor <= 0x7F
            ? static_cast<char>(*cursor)
            : '?');
  }
  return status;
}

std::string WideToUtf8(const std::wstring& value) {
  if (value.empty()) {
    return {};
  }
  const int size = WideCharToMultiByte(
      CP_UTF8,
      0,
      value.data(),
      static_cast<int>(value.size()),
      nullptr,
      0,
      nullptr,
      nullptr);
  if (size <= 0) {
    return {};
  }
  std::string result(static_cast<size_t>(size), '\0');
  WideCharToMultiByte(
      CP_UTF8,
      0,
      value.data(),
      static_cast<int>(value.size()),
      result.data(),
      size,
      nullptr,
      nullptr);
  return result;
}

std::string EscapeJsonString(const std::string& value) {
  std::string result;
  result.reserve(value.size() + 32);
  for (const unsigned char character : value) {
    switch (character) {
      case '\\':
        result += "\\\\";
        break;
      case '"':
        result += "\\\"";
        break;
      case '\b':
        result += "\\b";
        break;
      case '\f':
        result += "\\f";
        break;
      case '\n':
        result += "\\n";
        break;
      case '\r':
        result += "\\r";
        break;
      case '\t':
        result += "\\t";
        break;
      default:
        if (character < 0x20) {
          char escaped[7]{};
          sprintf_s(escaped, "\\u%04X", character);
          result += escaped;
        } else {
          result.push_back(static_cast<char>(character));
        }
        break;
    }
  }
  return result;
}

bool TryReadJsonStringField(
    const std::string& json,
    const std::string& field,
    std::string* value) {
  const std::string marker = "\"" + field + "\":";
  size_t cursor = json.find(marker);
  if (cursor == std::string::npos) {
    return false;
  }
  cursor += marker.size();
  while (cursor < json.size()
         && std::isspace(
             static_cast<unsigned char>(json[cursor])) != 0) {
    ++cursor;
  }
  if (cursor >= json.size() || json[cursor] != '"') {
    return false;
  }
  ++cursor;

  std::string result;
  result.reserve(512);
  bool escaped = false;
  for (; cursor < json.size(); ++cursor) {
    const char character = json[cursor];
    if (escaped) {
      switch (character) {
        case '"':
        case '\\':
        case '/':
          result.push_back(character);
          break;
        case 'b':
          result.push_back('\b');
          break;
        case 'f':
          result.push_back('\f');
          break;
        case 'n':
          result.push_back('\n');
          break;
        case 'r':
          result.push_back('\r');
          break;
        case 't':
          result.push_back('\t');
          break;
        default:
          return false;
      }
      escaped = false;
      continue;
    }
    if (character == '\\') {
      escaped = true;
      continue;
    }
    if (character == '"') {
      *value = std::move(result);
      return true;
    }
    result.push_back(character);
  }
  return false;
}

void PublishTrackEvent(std::string payload) {
  if (payload.empty() || payload.size() > 64 * 1024) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_track_event_mutex);
    g_track_event_payload = std::move(payload);
    g_track_event_tick = GetTickCount64();
    ++g_track_event_sequence;
  }
  g_track_event_condition.notify_all();
}

bool ProcessDevToolsMessage(
    const void* message,
    size_t message_size) {
    if (message == nullptr || message_size == 0) {
      return true;
    }
    const std::string json(
        static_cast<const char*>(message),
        message_size);
    g_devtools_message_count.fetch_add(1, std::memory_order_relaxed);
    {
      std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
      g_last_devtools_message = json.substr(0, 4096);
    }
    if (json.find("\"method\":\"Runtime.bindingCalled\"")
            == std::string::npos) {
      return true;
    }

    std::string name;
    std::string payload;
    if (TryReadJsonStringField(json, "name", &name)
        && name == kTrackEventBinding
        && TryReadJsonStringField(json, "payload", &payload)) {
      PublishTrackEvent(std::move(payload));
      g_event_watcher_state.store(1, std::memory_order_release);
    }
    // We parse the complete protocol message above. Returning true prevents
    // CEF from dispatching the same message through the ABI-sensitive typed
    // OnDevToolsMethodResult/OnDevToolsEvent callbacks.
    return true;
}

bool __fastcall ObserverMessageAbiThunk(
    void*,
    CefBrowser*,
    const void* message,
    size_t message_size) {
  return ProcessDevToolsMessage(message, message_size);
}

void __fastcall ObserverMethodResultAbiThunk(
    void*, CefBrowser*, int, bool, const void*, size_t) {}

void __fastcall ObserverEventAbiThunk(
    void*, CefBrowser*, const CefString&, const void*, size_t) {}

void __fastcall ObserverAgentAbiThunk(void*, CefBrowser*) {}

class BridgeDevToolsObserver final
    : public CefDevToolsMessageObserver {
 public:
  bool OnDevToolsMessage(
      CefRefPtr<CefBrowser>,
      const void* message,
      size_t message_size) override {
    return ProcessDevToolsMessage(message, message_size);
  }

  void AddRef() const override { references_.AddRef(); }

  bool Release() const override {
    if (!references_.Release()) {
      return false;
    }
    delete this;
    return true;
  }

  bool HasOneRef() const override {
    return references_.HasOneRef();
  }

  bool HasAtLeastOneRef() const override {
    return references_.HasAtLeastOneRef();
  }

 private:
  ~BridgeDevToolsObserver() override = default;
  CefRefCount references_;
};

void InstallObserverAbiVtable(BridgeDevToolsObserver* observer) {
  auto*** object_vtable = reinterpret_cast<void***>(observer);
  void** original = *object_vtable;
  for (size_t index = 0; index < 5; ++index) {
    g_observer_abi_vtable[index] = original[index];
  }
  g_observer_abi_vtable[0] =
      reinterpret_cast<void*>(&ObserverMessageAbiThunk);
  g_observer_abi_vtable[1] =
      reinterpret_cast<void*>(&ObserverMethodResultAbiThunk);
  g_observer_abi_vtable[2] =
      reinterpret_cast<void*>(&ObserverEventAbiThunk);
  g_observer_abi_vtable[3] =
      reinterpret_cast<void*>(&ObserverAgentAbiThunk);
  g_observer_abi_vtable[4] =
      reinterpret_cast<void*>(&ObserverAgentAbiThunk);
  *object_vtable = g_observer_abi_vtable;
}

int CaptureDevToolsException(EXCEPTION_POINTERS* exception) {
  if (exception != nullptr && exception->ExceptionRecord != nullptr) {
    g_last_devtools_exception_code.store(
        exception->ExceptionRecord->ExceptionCode,
        std::memory_order_release);
    g_last_devtools_exception_address.store(
        reinterpret_cast<unsigned long long>(
            exception->ExceptionRecord->ExceptionAddress),
        std::memory_order_release);
  }
  return EXCEPTION_EXECUTE_HANDLER;
}

bool EnsureDevToolsObserverOnUiThread(void* host) {
  if (host == nullptr) {
    return false;
  }
  if (g_devtools_registration.get() != nullptr
      && g_devtools_observer.get() != nullptr
      && g_observed_host == host) {
    return true;
  }

  CefRefPtr<BridgeDevToolsObserver> concrete(
      new BridgeDevToolsObserver());
  InstallObserverAbiVtable(concrete.get());
  CefRefPtr<CefDevToolsMessageObserver> observer(concrete);
  CefRefPtr<CefRegistration> registration;
  auto** vtable = *reinterpret_cast<void***>(host);
  const auto add_observer =
      reinterpret_cast<AddDevToolsMessageObserverAbi>(
          vtable[kAddDevToolsMessageObserverSlot]);
  add_observer(host, &registration, observer.get());
  if (registration.get() == nullptr) {
    return false;
  }
  g_devtools_registration = registration;
  g_devtools_observer = observer;
  g_observed_host = host;
  return true;
}

std::string BuildRuntimeEvaluateMessage(
    const std::wstring& source) {
  int message_id = g_message_id.fetch_add(1);
  if (message_id <= 0) {
    g_message_id.store(2);
    message_id = 1;
  }
  return
      "{\"id\":" + std::to_string(message_id)
      + ",\"method\":\"Runtime.evaluate\",\"params\":{"
        "\"expression\":\""
      + EscapeJsonString(WideToUtf8(source))
      + "\",\"silent\":true,\"returnByValue\":true,"
        "\"userGesture\":false}}";
}

std::string BuildRuntimeAddBindingMessage() {
  int message_id = g_message_id.fetch_add(1);
  if (message_id <= 0) {
    g_message_id.store(2);
    message_id = 1;
  }
  return
      "{\"id\":" + std::to_string(message_id)
      + ",\"method\":\"Runtime.addBinding\",\"params\":{"
        "\"name\":\""
      + kTrackEventBinding
      + "\"}}";
}

struct DevToolsTask {
  CefCTask task;
  std::atomic<long> references{1};
  std::atomic<int> result{-1};
  HANDLE completed = nullptr;
  std::string message;

  ~DevToolsTask() {
    if (completed != nullptr) {
      CloseHandle(completed);
    }
  }
};

DevToolsTask* ToDevToolsTask(CefCBaseRefCounted* base) {
  return reinterpret_cast<DevToolsTask*>(base);
}

DevToolsTask* ToDevToolsTask(CefCTask* task) {
  return reinterpret_cast<DevToolsTask*>(task);
}

void __stdcall TaskAddRef(CefCBaseRefCounted* base) {
  ToDevToolsTask(base)->references.fetch_add(
      1,
      std::memory_order_relaxed);
}

int __stdcall TaskRelease(CefCBaseRefCounted* base) {
  DevToolsTask* task = ToDevToolsTask(base);
  if (task->references.fetch_sub(
          1,
          std::memory_order_acq_rel) != 1) {
    return 0;
  }
  delete task;
  return 1;
}

int __stdcall TaskHasOneRef(CefCBaseRefCounted* base) {
  return ToDevToolsTask(base)->references.load(
      std::memory_order_acquire) == 1;
}

int __stdcall TaskHasAtLeastOneRef(CefCBaseRefCounted* base) {
  return ToDevToolsTask(base)->references.load(
      std::memory_order_acquire) >= 1;
}

void __stdcall TaskExecute(CefCTask* raw_task) {
  DevToolsTask* task = ToDevToolsTask(raw_task);
  int result = -2;
  void* host = ResolveLiveHostObject();
  if (host != nullptr) {
    __try {
      if (!EnsureDevToolsObserverOnUiThread(host)) {
        result = -4;
      } else {
        auto** vtable = *reinterpret_cast<void***>(host);
        const auto send = reinterpret_cast<SendDevToolsMessage>(
            vtable[kSendDevToolsMessageSlot]);
        result = send(
            host,
            task->message.data(),
            task->message.size())
            ? 1
            : 0;
      }
    } __except (CaptureDevToolsException(GetExceptionInformation())) {
      result = -3;
    }
  }
  task->result.store(result, std::memory_order_release);
  SetEvent(task->completed);
}

bool PostRawDevToolsMessage(
    std::string message,
    std::string* response) {
  if (g_cef_post_task == nullptr
      || ResolveLiveHostObject() == nullptr) {
    *response = "ERR bridge-not-ready";
    return false;
  }

  auto* task = new DevToolsTask();
  task->completed = CreateEventW(
      nullptr,
      TRUE,
      FALSE,
      nullptr);
  if (task->completed == nullptr) {
    delete task;
    *response = "ERR task-event-failed";
    return false;
  }
  task->task.base.size = sizeof(CefCTask);
  task->task.base.add_ref = TaskAddRef;
  task->task.base.release = TaskRelease;
  task->task.base.has_one_ref = TaskHasOneRef;
  task->task.base.has_at_least_one_ref = TaskHasAtLeastOneRef;
  task->task.execute = TaskExecute;
  task->message = std::move(message);

  const int posted = g_cef_post_task(0, &task->task);
  if (posted == 0) {
    task->task.base.release(&task->task.base);
    *response = "ERR cef-post-task-failed";
    return false;
  }

  const DWORD wait = WaitForSingleObject(
      task->completed,
      1500);
  const int result = task->result.load(std::memory_order_acquire);
  task->task.base.release(&task->task.base);
  if (wait != WAIT_OBJECT_0) {
    *response = "ERR devtools-task-timeout";
    return false;
  }
  if (result == 1) {
    *response = "OK POSTED route=internal-devtools";
    return true;
  }
  if (result == 0) {
    *response = "ERR devtools-message-rejected";
  } else if (result == -2) {
    *response = "ERR live-host-lost";
  } else if (result == -4) {
    *response = "ERR devtools-observer-failed";
  } else {
    *response = "ERR devtools-call-fault";
  }
  return false;
}

bool PostDevToolsMessage(
    const std::wstring& source,
    std::string* response) {
  return PostRawDevToolsMessage(
      BuildRuntimeEvaluateMessage(source),
      response);
}

std::wstring BuildChannelDispatchScript(
    const std::wstring& payload) {
  return
      L"(()=>{try{let r;const a=globalThis.webpackJsonp;"
      L"if(!a||typeof a.push!=='function')return;"
      // CloudMusic 3.1.x still uses Webpack 4. Its JSONP runtime expects
      // [chunkIds, modules, entryModules]; the Webpack 5 runtime-callback
      // trick never executes here and used to make every command a silent
      // no-op. Register a one-shot synthetic module to capture __webpack_require__.
      // Use a non-index property. A large numeric module id would expand the
      // Webpack 4 module array's length even after the property is deleted.
      L"const k='__awoo_capture_'+Date.now(),m={};"
      L"m[k]=(x,y,q)=>{r=q};a.push([[k],m,[[k]]]);"
      L"if(!r||!r.c)return;"
      L"try{delete r.c[k];delete r.m[k]}catch(_){}"
      L"let s;for(const v of Object.values(r.c)){"
      L"const e=v&&v.exports;"
      L"for(const c of [e,e&&e.default]){"
      L"const q=c&&c.channelManage&&c.channelManage.orpheus"
      L"&&c.channelManage.orpheus.dataSrc$;"
      L"if(q&&typeof q.next==='function'){s=q;break;}}"
      L"if(s)break;}globalThis.__AWOO_NCM_BRIDGE_LAST__="
      L"{found:!!s,at:Date.now()};if(s)s.next("
      + payload
      + L");}catch(_){}})();";
}

std::wstring BuildTrackWatcherScript() {
  return LR"AWOO((()=>{try{
const VERSION=2,BINDING='__awooNcmNativeEvent';
const previous=globalThis.__AWOO_NCM_TRACK_WATCHER__;
const encode=value=>btoa(unescape(encodeURIComponent(JSON.stringify(value))));
const send=(type,extra={})=>{try{
  const binding=globalThis[BINDING];
  if(typeof binding!=='function')return false;
  binding(encode({version:VERSION,type,at:Date.now(),title:String(document.title||''),...extra}));
  return true;
}catch(_){return false}};
if(previous&&previous.version===VERSION&&typeof previous.refresh==='function'){
  previous.refresh();return;
}
if(previous&&typeof previous.dispose==='function'){try{previous.dispose()}catch(_){}}
const cleanups=[];
let retryTimer=0,unsubscribeStore=null,lastFingerprint='',lastTrackId='';
const normalizeId=value=>value===undefined||value===null?'':String(value);
const findStore=()=>{try{
  const existing=globalThis.__AWOO_NCM_REDUX_STORE__;
  if(existing&&typeof existing.getState==='function'&&typeof existing.subscribe==='function')return existing;
  const rootElement=document.querySelector('#root');
  const roots=[];
  if(globalThis._fiberRoot)roots.push(globalThis._fiberRoot.current||globalThis._fiberRoot);
  const legacy=rootElement&&rootElement._reactRootContainer&&rootElement._reactRootContainer._internalRoot;
  if(legacy)roots.push(legacy.current||legacy);
  if(rootElement){
    for(const key of Object.getOwnPropertyNames(rootElement)){
      if(key.startsWith('__reactContainer$')||key.startsWith('__reactFiber$')){
        const candidate=rootElement[key];if(candidate)roots.push(candidate.current||candidate);
      }
    }
  }
  const queue=[...roots],seen=new Set();let visited=0;
  while(queue.length&&visited<30000){
    const node=queue.shift();
    if(!node||seen.has(node))continue;seen.add(node);visited++;
    const candidates=[node.memoizedProps&&node.memoizedProps.store,node.stateNode&&node.stateNode.store];
    for(const store of candidates){
      if(store&&typeof store.getState==='function'&&typeof store.subscribe==='function'){
        globalThis.__AWOO_NCM_REDUX_STORE__=store;return store;
      }
    }
    if(node.child)queue.push(node.child);
    if(node.sibling)queue.push(node.sibling);
  }
}catch(_){}return null};
const songFrom=(id,list)=>{try{
  const wanted=normalizeId(id);if(!wanted)return null;
  const item=(Array.isArray(list)?list:[]).find(value=>normalizeId(value&&(
    value.id??(value.track&&value.track.id)))===wanted);
  const track=item&&(item.track||item);if(!track)return {id:wanted,name:'',artist:'',album:'',coverUrl:''};
  const artists=track.artists||track.ar||[];
  const album=track.album||track.al||{};
  return {id:wanted,name:String(track.name||''),artist:artists.map(value=>value&&value.name).filter(Boolean).join('/'),
    album:String(album.name||album.albumName||''),
    coverUrl:String(album.picUrl||album.coverUrl||album.cover||track.coverUrl||item.coverUrl||'')};
}catch(_){return null}};
const readState=store=>{try{
  const state=store.getState()||{};
  const id=normalizeId(state.playing&&(state.playing.resourceTrackId||state.playing.onlineResourceId));
  const list=state.playingList&&state.playingList.curPlayingList||[];
  const current=songFrom(id,list)||{id,name:'',artist:'',album:'',coverUrl:''};
  const index=Array.isArray(list)?list.findIndex(value=>normalizeId(value&&(value.id??(value.track&&value.track.id)))===id):-1;
  const next=index>=0&&index+1<list.length?songFrom(list[index+1].id??(list[index+1].track&&list[index+1].track.id),list):null;
  return {current,next};
}catch(_){return {current:null,next:null}}};
const emitRedux=(type,force=false)=>{const store=findStore();if(!store)return false;
  const snapshot=readState(store),current=snapshot.current||{},next=snapshot.next||{};
  const fingerprint=normalizeId(current.id)+'|'+normalizeId(next.id);
  if(!force&&fingerprint===lastFingerprint)return true;
  const actualType=type==='redux:state'&&normalizeId(current.id)!==lastTrackId?'redux:track-changed':type;
  lastFingerprint=fingerprint;lastTrackId=normalizeId(current.id);
  send(actualType,{trackId:normalizeId(current.id),name:String(current.name||''),artist:String(current.artist||''),
    album:String(current.album||''),coverUrl:String(current.coverUrl||''),nextTrackId:normalizeId(next.id),
    nextName:String(next.name||''),nextArtist:String(next.artist||''),nextAlbum:String(next.album||''),
    nextCoverUrl:String(next.coverUrl||'')});return true};
const attachStore=()=>{try{
  const store=findStore();
  if(!store){send('redux:waiting');retryTimer=setTimeout(attachStore,750);return;}
  emitRedux('redux:ready',true);
  unsubscribeStore=store.subscribe(()=>{try{emitRedux('redux:state')}catch(_){}});
}catch(_){retryTimer=setTimeout(attachStore,1000)}};
const describeMedia=media=>({
  paused:!!media.paused,
  ended:!!media.ended,
  currentTime:Number.isFinite(media.currentTime)?media.currentTime:0,
  duration:Number.isFinite(media.duration)?media.duration:0,
  readyState:media.readyState|0,
  src:String(media.currentSrc||media.src||'').slice(0,1024)
});
const mediaEvents=['loadstart','loadedmetadata','durationchange','play','playing','pause','ended','emptied','abort','error'];
const onMedia=event=>{
  const media=event.target;
  if(media instanceof HTMLMediaElement&&!emitRedux('redux:media:'+event.type,true)){
    send('media:'+event.type,{media:describeMedia(media)});
  }
};
for(const name of mediaEvents){
  document.addEventListener(name,onMedia,true);
  cleanups.push(()=>document.removeEventListener(name,onMedia,true));
}
const titleRoot=document.head||document.documentElement;
if(titleRoot){
  let previousTitle=String(document.title||'');
  const observer=new MutationObserver(()=>{
    const title=String(document.title||'');
    if(title!==previousTitle){previousTitle=title;
      if(!emitRedux('redux:title',true))send('title');}
  });
  observer.observe(titleRoot,{subtree:true,childList:true,characterData:true});
  cleanups.push(()=>observer.disconnect());
}
const heartbeat=setInterval(()=>{
  if(!emitRedux('redux:heartbeat',true))send('heartbeat');
},15000);
cleanups.push(()=>clearInterval(heartbeat));
const watcher={
  version:VERSION,
  emit:type=>send(type),
  refresh:()=>{if(!emitRedux('redux:refresh',true))send('ensure')},
  dispose:()=>{if(retryTimer)clearTimeout(retryTimer);if(typeof unsubscribeStore==='function')try{unsubscribeStore()}catch(_){}
    for(const cleanup of cleanups){try{cleanup()}catch(_){}}}
};
globalThis.__AWOO_NCM_TRACK_WATCHER__=watcher;
send('ready',{mediaCount:document.querySelectorAll('audio,video').length});
attachStore();
}catch(_){}})();)AWOO";
}

bool InstallTrackWatcher() {
  std::string response;
  if (!PostRawDevToolsMessage(
          BuildRuntimeAddBindingMessage(),
          &response)) {
    std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
    g_watcher_install_status = "add-binding:" + response;
    return false;
  }
  const bool posted = PostDevToolsMessage(
      BuildTrackWatcherScript(),
      &response);
  {
    std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
    g_watcher_install_status = "evaluate:" + response;
  }
  return posted;
}

unsigned long long TrackEventAgeMilliseconds() {
  std::lock_guard<std::mutex> lock(g_track_event_mutex);
  if (g_track_event_tick == 0) {
    return ULLONG_MAX;
  }
  return GetTickCount64() - g_track_event_tick;
}

std::string ReadTrackEventResponse() {
  std::lock_guard<std::mutex> lock(g_track_event_mutex);
  const auto age = g_track_event_tick == 0
      ? ULLONG_MAX
      : GetTickCount64() - g_track_event_tick;
  if (g_track_event_sequence == 0 || g_track_event_payload.empty()) {
    return "OK NO_EVENT 0";
  }
  return
      "OK EVENT " + std::to_string(g_track_event_sequence)
      + " " + std::to_string(age)
      + " " + g_track_event_payload;
}

std::string WaitForTrackEventResponse(
    unsigned long long after_sequence,
    unsigned long timeout_milliseconds) {
  std::unique_lock<std::mutex> lock(g_track_event_mutex);
  if (g_track_event_sequence <= after_sequence) {
    g_track_event_condition.wait_for(
        lock,
        std::chrono::milliseconds(timeout_milliseconds),
        [after_sequence] {
          return g_track_event_sequence > after_sequence;
        });
  }

  if (g_track_event_sequence <= after_sequence) {
    return "OK NO_CHANGE " + std::to_string(g_track_event_sequence);
  }
  const auto age = g_track_event_tick == 0
      ? ULLONG_MAX
      : GetTickCount64() - g_track_event_tick;
  if (g_track_event_sequence == 0 || g_track_event_payload.empty()) {
    return "OK NO_EVENT 0";
  }
  return
      "OK EVENT " + std::to_string(g_track_event_sequence)
      + " " + std::to_string(age)
      + " " + g_track_event_payload;
}

std::string ReadDevToolsDiagnostics() {
  std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
  return
      "OK DIAGNOSTICS messages="
      + std::to_string(
          g_devtools_message_count.load(std::memory_order_acquire))
      + " watcher=" + g_watcher_install_status
      + " exception-code="
      + std::to_string(
          g_last_devtools_exception_code.load(std::memory_order_acquire))
      + " exception-address="
      + std::to_string(
          g_last_devtools_exception_address.load(std::memory_order_acquire))
      + " last=" + g_last_devtools_message;
}

bool IsPositiveDecimal(const std::string& value) {
  if (value.empty()
      || !std::all_of(
          value.begin(),
          value.end(),
          [](unsigned char character) {
            return std::isdigit(character) != 0;
          })) {
    return false;
  }
  return value.find_first_not_of('0') != std::string::npos;
}

std::wstring AsWideAscii(const std::string& value) {
  return std::wstring(value.begin(), value.end());
}

std::string HandleRequest(std::string request) {
  while (!request.empty()
         && (request.back() == '\r'
             || request.back() == '\n'
             || request.back() == '\0')) {
    request.pop_back();
  }

  if (request == "HELLO 1") {
    if (g_bridge_state.load(std::memory_order_acquire) == -1) {
      return "REFUSED " + StatusAsAscii();
    }
    if (ResolveLiveHostObject() != nullptr) {
      if (g_cef_validation_mode.load(std::memory_order_acquire) == 2
          && g_bridge_state.load(std::memory_order_acquire) != 1) {
        return "WAIT compatibility-probe-in-progress";
      }
      SetStatus(
          g_cef_validation_mode.load(std::memory_order_acquire) == 2
              ? L"ready: compatible CEF patch passed runtime probe"
              : L"ready: exact CEF build + internal DevTools");
      g_bridge_state.store(1, std::memory_order_release);
      return
          "OK READY cef=91.2.2+4472.169 "
          "validation="
          + std::string(
              g_cef_validation_mode.load(std::memory_order_acquire) == 2
                  ? "compatible-api-hash+runtime-probe"
                  : "exact")
          + " "
          "route=internal-devtools events="
          + std::string(
              g_event_watcher_state.load(std::memory_order_acquire) == 1
                  ? "ready"
                  : "initializing");
    }
    g_bridge_state.store(0);
    return "WAIT " + StatusAsAscii();
  }

  if (request == "GET_TRACK_EVENT") {
    return ReadTrackEventResponse();
  }
  if (request == "GET_DEVTOOLS_DIAGNOSTICS") {
    return ReadDevToolsDiagnostics();
  }

  if (g_bridge_state.load() != 1
      || ResolveLiveHostObject() == nullptr) {
    g_bridge_state.store(0);
    return "ERR bridge-not-ready";
  }

  std::wstring payload;
  if (request == "PAUSE") {
    payload = L"{cmd:'pause'}";
  } else if (request == "RESUME") {
    payload = L"{cmd:'resume'}";
  } else if (request.rfind("PLAY ", 0) == 0) {
    const std::string id = request.substr(5);
    if (!IsPositiveDecimal(id)) {
      return "ERR invalid-song-id";
    }
    payload =
        L"{cmd:'play',type:'song',id:'"
        + AsWideAscii(id)
        + L"'}";
  } else if (request.rfind("ADD_NEXT ", 0) == 0) {
    const std::string id = request.substr(9);
    if (!IsPositiveDecimal(id)) {
      return "ERR invalid-song-id";
    }
    payload =
        L"{cmd:'playingList',type:'addToNext',value:'"
        + AsWideAscii(id)
        + L"'}";
  } else {
    return "ERR unknown-command";
  }

  std::string response;
  PostDevToolsMessage(
      BuildChannelDispatchScript(payload),
      &response);
  return response;
}

void RunPipeServer() {
  wchar_t pipe_name[128]{};
  swprintf_s(
      pipe_name,
      L"\\\\.\\pipe\\AwooNcmCefBridge-v1-%lu",
      GetCurrentProcessId());

  for (;;) {
    HANDLE pipe = CreateNamedPipeW(
        pipe_name,
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1,
        2048,
        2048,
        0,
        nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
      SetStatus(L"error: CreateNamedPipe failed");
      return;
    }

    const BOOL connected =
        ConnectNamedPipe(pipe, nullptr)
        || GetLastError() == ERROR_PIPE_CONNECTED;
    if (connected) {
      char buffer[1024]{};
      DWORD bytes_read = 0;
      if (ReadFile(
              pipe,
              buffer,
              static_cast<DWORD>(sizeof(buffer) - 1),
              &bytes_read,
              nullptr)
          && bytes_read > 0) {
        buffer[bytes_read] = '\0';
        const std::string response =
            HandleRequest(std::string(buffer, bytes_read)) + "\n";
        DWORD bytes_written = 0;
        WriteFile(
            pipe,
            response.data(),
            static_cast<DWORD>(response.size()),
            &bytes_written,
            nullptr);
        FlushFileBuffers(pipe);
      }
      DisconnectNamedPipe(pipe);
    }
    CloseHandle(pipe);
  }
}

void RunEventPipeServer() {
  wchar_t pipe_name[128]{};
  swprintf_s(
      pipe_name,
      L"\\\\.\\pipe\\AwooNcmCefBridge-events-v1-%lu",
      GetCurrentProcessId());

  for (;;) {
    HANDLE pipe = CreateNamedPipeW(
        pipe_name,
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
        1,
        1024,
        1024,
        0,
        nullptr);
    if (pipe == INVALID_HANDLE_VALUE) {
      return;
    }

    const BOOL connected =
        ConnectNamedPipe(pipe, nullptr)
        || GetLastError() == ERROR_PIPE_CONNECTED;
    if (connected) {
      char buffer[256]{};
      DWORD bytes_read = 0;
      if (ReadFile(
              pipe,
              buffer,
              static_cast<DWORD>(sizeof(buffer) - 1),
              &bytes_read,
              nullptr)
          && bytes_read > 0) {
        buffer[bytes_read] = '\0';
        unsigned long long after_sequence = 0;
        unsigned long timeout_milliseconds = 30000;
        std::string response;
        if (sscanf_s(
                buffer,
                "WAIT_EVENT %llu %lu",
                &after_sequence,
                &timeout_milliseconds) == 2) {
          timeout_milliseconds = std::clamp(
              timeout_milliseconds,
              1000UL,
              60000UL);
          response = WaitForTrackEventResponse(
              after_sequence,
              timeout_milliseconds);
        } else {
          response = "ERR invalid-event-request";
        }
        response.push_back('\n');
        DWORD bytes_written = 0;
        WriteFile(
            pipe,
            response.data(),
            static_cast<DWORD>(response.size()),
            &bytes_written,
            nullptr);
        FlushFileBuffers(pipe);
      }
      DisconnectNamedPipe(pipe);
    }
    CloseHandle(pipe);
  }
}

DWORD WINAPI EventPipeWorker(void*) {
  RunEventPipeServer();
  return 0;
}

DWORD WINAPI TrackWatcherWorker(void*) {
  for (;;) {
    if (g_event_watcher_state.load(std::memory_order_acquire) != 1
        || TrackEventAgeMilliseconds() > 25000) {
      g_event_watcher_state.store(0, std::memory_order_release);
      {
        std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
        g_watcher_install_status = "attempting";
      }
      const bool installed = InstallTrackWatcher();
      if (installed
          && g_cef_validation_mode.load(std::memory_order_acquire) == 2
          && ResolveLiveHostObject() != nullptr) {
        SetStatus(L"ready: compatible CEF patch passed runtime probe");
        g_bridge_state.store(1, std::memory_order_release);
      }
    }
    Sleep(2000);
  }
}

DWORD WINAPI BridgeWorker(void*) {
  HANDLE event_pipe_worker = CreateThread(
      nullptr,
      0,
      EventPipeWorker,
      nullptr,
      0,
      nullptr);
  if (event_pipe_worker != nullptr) {
    CloseHandle(event_pipe_worker);
  }

  if (!ValidateCefVersion()) {
    g_bridge_state.store(-1);
    {
      std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
      g_watcher_install_status =
          "validation:" + StatusAsAscii();
    }
    RunPipeServer();
    return 0;
  }

  if (ResolveLiveHostObject() != nullptr) {
    if (g_cef_validation_mode.load(std::memory_order_acquire) == 2) {
      SetStatus(L"probing: unknown CEF patch through internal DevTools");
      if (!InstallTrackWatcher()) {
        SetStatus(L"refused: compatible CEF runtime probe failed");
        g_bridge_state.store(-1, std::memory_order_release);
        RunPipeServer();
        return 0;
      }
      SetStatus(L"ready: compatible CEF patch passed runtime probe");
    } else {
      SetStatus(L"ready: exact CEF build + internal DevTools");
    }
    g_bridge_state.store(1, std::memory_order_release);
  } else {
    g_bridge_state.store(0, std::memory_order_release);
  }

  {
    std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
    g_watcher_install_status = "creating-thread";
  }
  HANDLE watcher = CreateThread(
      nullptr,
      0,
      TrackWatcherWorker,
      nullptr,
      0,
      nullptr);
  if (watcher != nullptr) {
    CloseHandle(watcher);
  } else {
    std::lock_guard<std::mutex> lock(g_devtools_diagnostics_mutex);
    g_watcher_install_status =
        "thread-error:" + std::to_string(GetLastError());
  }
  RunPipeServer();
  return 0;
}

}  // namespace

BOOL WINAPI DllMain(
    HINSTANCE instance,
    DWORD reason,
    LPVOID) {
  if (reason == DLL_PROCESS_ATTACH) {
    DisableThreadLibraryCalls(instance);
    HANDLE worker = CreateThread(
        nullptr,
        0,
        BridgeWorker,
        nullptr,
        0,
        nullptr);
    if (worker != nullptr) {
      CloseHandle(worker);
    }
  }
  return TRUE;
}
