#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <cstdio>
#include <cstring>
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
constexpr size_t kLastValidatedHostSlot = 59;

struct CefBaseRefCounted {
  size_t size;
  void(__stdcall* add_ref)(CefBaseRefCounted* self);
  int(__stdcall* release)(CefBaseRefCounted* self);
  int(__stdcall* has_one_ref)(CefBaseRefCounted* self);
  int(__stdcall* has_at_least_one_ref)(CefBaseRefCounted* self);
};

struct CefTask {
  CefBaseRefCounted base;
  void(__stdcall* execute)(CefTask* self);
};

using CefPostTask = int(__cdecl*)(int thread_id, CefTask* task);
using CefVersionInfo = int(__cdecl*)(int entry);
using CefApiHash = const char* (__cdecl*)(int entry);
using SendDevToolsMessage = bool(__fastcall*)(
    void* self,
    const void* message,
    size_t message_size);

HMODULE g_libcef = nullptr;
uintptr_t g_libcef_begin = 0;
uintptr_t g_libcef_end = 0;
CefPostTask g_cef_post_task = nullptr;
std::atomic<long> g_bridge_state{0};
std::atomic<int> g_message_id{1};
wchar_t g_status[256] = L"initializing";

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

  for (int index = 0; index < 8; ++index) {
    if (version_info(index) != kExpectedCefVersion[index]) {
      SetStatus(L"refused: unsupported CEF build");
      return false;
    }
  }

  const char* platform = api_hash(0);
  const char* universal = api_hash(1);
  if (universal == nullptr
      || platform == nullptr
      || std::strcmp(universal, kExpectedUniversalApiHash) != 0
      || std::strcmp(platform, kExpectedPlatformApiHash) != 0) {
    SetStatus(L"refused: CEF API hash mismatch");
    return false;
  }
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

struct DevToolsTask {
  CefTask task;
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

DevToolsTask* ToDevToolsTask(CefBaseRefCounted* base) {
  return reinterpret_cast<DevToolsTask*>(base);
}

DevToolsTask* ToDevToolsTask(CefTask* task) {
  return reinterpret_cast<DevToolsTask*>(task);
}

void __stdcall TaskAddRef(CefBaseRefCounted* base) {
  ToDevToolsTask(base)->references.fetch_add(
      1,
      std::memory_order_relaxed);
}

int __stdcall TaskRelease(CefBaseRefCounted* base) {
  DevToolsTask* task = ToDevToolsTask(base);
  if (task->references.fetch_sub(
          1,
          std::memory_order_acq_rel) != 1) {
    return 0;
  }
  delete task;
  return 1;
}

int __stdcall TaskHasOneRef(CefBaseRefCounted* base) {
  return ToDevToolsTask(base)->references.load(
      std::memory_order_acquire) == 1;
}

int __stdcall TaskHasAtLeastOneRef(CefBaseRefCounted* base) {
  return ToDevToolsTask(base)->references.load(
      std::memory_order_acquire) >= 1;
}

void __stdcall TaskExecute(CefTask* raw_task) {
  DevToolsTask* task = ToDevToolsTask(raw_task);
  int result = -2;
  void* host = ResolveLiveHostObject();
  if (host != nullptr) {
    __try {
      auto** vtable = *reinterpret_cast<void***>(host);
      const auto send = reinterpret_cast<SendDevToolsMessage>(
          vtable[kSendDevToolsMessageSlot]);
      result = send(
          host,
          task->message.data(),
          task->message.size())
          ? 1
          : 0;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
      result = -3;
    }
  }
  task->result.store(result, std::memory_order_release);
  SetEvent(task->completed);
}

bool PostDevToolsMessage(
    const std::wstring& source,
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
  task->task.base.size = sizeof(CefTask);
  task->task.base.add_ref = TaskAddRef;
  task->task.base.release = TaskRelease;
  task->task.base.has_one_ref = TaskHasOneRef;
  task->task.base.has_at_least_one_ref = TaskHasAtLeastOneRef;
  task->task.execute = TaskExecute;
  task->message = BuildRuntimeEvaluateMessage(source);

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
  } else {
    *response = "ERR devtools-call-fault";
  }
  return false;
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
    if (ResolveLiveHostObject() != nullptr) {
      SetStatus(L"ready: live CEF host + internal DevTools");
      g_bridge_state.store(1);
      return
          "OK READY cef=91.2.2+4472.169 "
          "route=internal-devtools";
    }
    g_bridge_state.store(0);
    return "WAIT " + StatusAsAscii();
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

DWORD WINAPI BridgeWorker(void*) {
  if (!ValidateCefVersion()) {
    g_bridge_state.store(-1);
    RunPipeServer();
    return 0;
  }

  if (ResolveLiveHostObject() != nullptr) {
    SetStatus(L"ready: live CEF host + internal DevTools");
    g_bridge_state.store(1);
  } else {
    g_bridge_state.store(0);
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

