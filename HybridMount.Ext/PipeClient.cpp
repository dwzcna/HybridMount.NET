#include "Extension.h"
#include <sddl.h>
#include <wincrypt.h>
#include <appmodel.h>
#include <roapi.h>
#include <windows.data.json.h>
#include <wrl/wrappers/corewrappers.h>
#include <memory>
#include <stdexcept>
#include <cstdint>

using Microsoft::WRL::ComPtr;
using Microsoft::WRL::Wrappers::HString;
using Microsoft::WRL::Wrappers::HStringReference;
namespace {
	constexpr DWORD MaximumFrame = 1024 * 1024;
	struct Handle {
		HANDLE value = INVALID_HANDLE_VALUE;
		explicit Handle(HANDLE handle) noexcept : value(handle) {}
		~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
		Handle(const Handle&) = delete;
		Handle& operator=(const Handle&) = delete;
	};
	void Check(bool success) { if (!success) throw std::runtime_error("Windows operation failed"); }
	/// <summary>和托管端一致地使用当前用户 SID 和会话隔离管道；不依赖本地化用户名。</summary>
	std::wstring PipeName() {
		HANDLE rawToken{};
		Check(OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &rawToken) != FALSE);
		Handle token(rawToken);
		DWORD size = 0;
		GetTokenInformation(token.value, TokenUser, nullptr, 0, &size);
		std::vector<BYTE> storage(size);
		Check(GetTokenInformation(token.value, TokenUser, storage.data(), size, &size) != FALSE);
		LPWSTR rawSid = nullptr;
		Check(ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(storage.data())->User.Sid, &rawSid) != FALSE);
		std::wstring sid(rawSid);
		LocalFree(rawSid);
		DWORD session = 0;
		Check(ProcessIdToSessionId(GetCurrentProcessId(), &session) != FALSE);
        auto name=L"\\\\.\\pipe\\"+hm::ProductScope()+L".v2."+sid+L"."+std::to_wstring(session);
#ifdef HYBRIDMOUNT_SHELL_PROBE_TAG
#define HM_STRING2(value) #value
#define HM_STRING(value) HM_STRING2(value)
#define HM_WIDE2(value) L##value
#define HM_WIDE(value) HM_WIDE2(value)
        name+=L".shell-probe." HM_WIDE(HM_STRING(HYBRIDMOUNT_SHELL_PROBE_TAG));
#elif defined(HYBRIDMOUNT_ISOLATED_TEST_PIPE)
        name+=L".test."+std::to_wstring(GetCurrentProcessId());
#endif
        return name;
	}
	/// <summary>只转义 JSON 字符串；路径作为数据，不拼接进命令行。</summary>
	std::wstring Quote(const std::wstring& input) {
		std::wstring result = L"\"";
		constexpr wchar_t hex[] = L"0123456789abcdef";
		for (const wchar_t ch : input) {
			if (ch == L'"' || ch == L'\\') { result += L'\\'; result += ch; }
			else if (ch < 0x20) { result += L"\\u00"; result += hex[(ch >> 4) & 15]; result += hex[ch & 15]; }
			else result += ch;
		}
		return result + L'"';
	}
	std::string Utf8(const std::wstring& input) {
		const auto size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0, nullptr, nullptr);
		Check(size > 0);
		std::string output(size, '\0');
		Check(WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), output.data(), size, nullptr, nullptr) > 0);
		return output;
	}
	std::wstring Utf16(const std::vector<char>& input) {
		const auto size = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), nullptr, 0);
		Check(size > 0);
		std::wstring output(size, L'\0');
		Check(MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, input.data(), static_cast<int>(input.size()), output.data(), size) > 0);
		return output;
	}
	/// <summary>一次事务共享截止时间；超时取消重叠 I/O 后等待完成，避免悬空缓冲区。</summary>
	void Transfer(HANDLE pipe, void* buffer, DWORD length, bool write, ULONGLONG deadline) {
		auto cursor = static_cast<BYTE*>(buffer);
		while (length) {
			const auto now = GetTickCount64();
			Check(now < deadline);
			Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
			Check(event.value != nullptr);
			OVERLAPPED overlap{};
			overlap.hEvent = event.value;
			DWORD count = 0;
			const BOOL done = write ? WriteFile(pipe, cursor, length, &count, &overlap) : ReadFile(pipe, cursor, length, &count, &overlap);
			if (!done) {
				Check(GetLastError() == ERROR_IO_PENDING);
				if (WaitForSingleObject(event.value, static_cast<DWORD>(deadline - now)) != WAIT_OBJECT_0) {
					CancelIoEx(pipe, &overlap);
					GetOverlappedResult(pipe, &overlap, &count, TRUE);
					throw std::runtime_error("Pipe timeout");
				}
				Check(GetOverlappedResult(pipe, &overlap, &count, FALSE) != FALSE);
			}
			Check(count > 0);
			cursor += count;
			length -= count;
		}
	}
	/// <summary>优先用包激活；开发运行退回同目录的固定可执行文件。</summary>
	void LaunchApp() {
		UINT32 length = 0;
		if (GetCurrentPackageFamilyName(&length, nullptr) == ERROR_INSUFFICIENT_BUFFER) {
			std::wstring family(length, L'\0');
			if (GetCurrentPackageFamilyName(&length, family.data()) == ERROR_SUCCESS) {
				family.resize(length - 1);
				ComPtr<IApplicationActivationManager> activation;
				DWORD process = 0;
				if (SUCCEEDED(CoCreateInstance(CLSID_ApplicationActivationManager, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&activation)))
					&& SUCCEEDED(activation->ActivateApplication((family + L"!App").c_str(), L"--background", AO_NOERRORUI, &process))) return;
			}
		}
		std::wstring modulePath(32768, L'\0');
		const auto count = GetModuleFileNameW(g_module, modulePath.data(), static_cast<DWORD>(modulePath.size()));
		Check(count > 0 && count < modulePath.size());
		modulePath.resize(count);
		const auto exe = modulePath.substr(0, modulePath.find_last_of(L'\\') + 1) + L"HybridMount.NET.exe";
		auto command = L"\"" + exe + L"\" --background";
		STARTUPINFOW startup{ sizeof(startup) };
		PROCESS_INFORMATION process{};
		Check(CreateProcessW(exe.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &startup, &process) != FALSE);
		CloseHandle(process.hThread); CloseHandle(process.hProcess);
	}
	struct Work { std::wstring operation; std::vector<std::wstring> paths; HMODULE module; };
	/// <summary>只执行命名管道通信；响应必须匹配版本和请求 ID，不读写所选文件。</summary>
	void Send(const Work& work) {
		const auto pipeName = PipeName();
		HANDLE raw = CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
		if (raw == INVALID_HANDLE_VALUE) {
			const auto error = GetLastError();
			if (error == ERROR_FILE_NOT_FOUND) LaunchApp();
			else Check(error == ERROR_PIPE_BUSY);
			const auto start = GetTickCount64();
			do {
				WaitNamedPipeW(pipeName.c_str(), 100);
				raw = CreateFileW(pipeName.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr);
				if (raw != INVALID_HANDLE_VALUE) break;
				const auto retryError = GetLastError();
				Check(retryError == ERROR_FILE_NOT_FOUND || retryError == ERROR_PIPE_BUSY);
				Sleep(50);
			} while (GetTickCount64() - start < 10000);
		}
		Check(raw != INVALID_HANDLE_VALUE);
		Handle pipe(raw);
		GUID guid{};
		Check(SUCCEEDED(CoCreateGuid(&guid)));
		wchar_t id[40]{};
		Check(StringFromGUID2(guid, id, ARRAYSIZE(id)) > 0);
		std::wstring json = L"{\"version\":2,\"requestId\":" + Quote(id) + L",\"operation\":\"" + work.operation + L"\",\"paths\":[";
		for (size_t index = 0; index < work.paths.size(); ++index) { if (index) json += L','; json += Quote(work.paths[index]); }
		json += L"]}";
		auto bytes = Utf8(json);
		Check(bytes.size() <= MaximumFrame);
		auto size = static_cast<DWORD>(bytes.size());
		const auto deadline = GetTickCount64() + 5000;
		Transfer(pipe.value, &size, sizeof(size), true, deadline);
		Transfer(pipe.value, bytes.data(), size, true, deadline);
		Transfer(pipe.value, &size, sizeof(size), false, deadline);
		Check(size > 0 && size <= MaximumFrame);
		std::vector<char> response(size);
		Transfer(pipe.value, response.data(), size, false, deadline);
		ComPtr<ABI::Windows::Data::Json::IJsonObjectStatics> jsonFactory;
		Check(SUCCEEDED(RoGetActivationFactory(HStringReference(RuntimeClass_Windows_Data_Json_JsonObject).Get(), IID_PPV_ARGS(&jsonFactory))));
		ComPtr<ABI::Windows::Data::Json::IJsonObject> root;
		const auto text = Utf16(response);
		Check(SUCCEEDED(jsonFactory->Parse(HStringReference(text.c_str()).Get(), &root)));
		HString correlation, status;
		double version = 0;
		Check(SUCCEEDED(root->GetNamedNumber(HStringReference(L"version").Get(), &version)) && version == 2);
		Check(SUCCEEDED(root->GetNamedString(HStringReference(L"requestId").Get(), correlation.GetAddressOf())));
		Check(wcscmp(correlation.GetRawBuffer(nullptr), id) == 0);
		Check(SUCCEEDED(root->GetNamedString(HStringReference(L"status").Get(), status.GetAddressOf())));
		Check(wcscmp(status.GetRawBuffer(nullptr), L"accepted") == 0 || wcscmp(status.GetRawBuffer(nullptr), L"notImplemented") == 0);
	}
	DWORD WINAPI RunWork(void* parameter) noexcept {
		auto work = std::unique_ptr<Work>(static_cast<Work*>(parameter));
		const auto module = work->module;
		const HRESULT initialized = RoInitialize(RO_INIT_MULTITHREADED);
		try { Check(SUCCEEDED(initialized)); Send(*work); }
		catch (...) { MessageBoxW(nullptr, L"无法确认任务结果。请在 HybridMount.NET 挂载页查看；已接纳的任务可能继续执行。", L"HybridMount.NET", MB_OK | MB_ICONWARNING); }
		if (SUCCEEDED(initialized)) RoUninitialize();
		work.reset();
		FreeLibraryAndExitThread(module, 0);
	}
}
/// <summary>复制固定操作和路径，后台线程持有模块引用；所有菜单命令共用同一有界IPC传输。</summary>
static HRESULT QueueMenuRequest(const wchar_t* operation, std::vector<std::wstring> paths) noexcept {
	HMODULE hold = nullptr;
	if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, reinterpret_cast<LPCWSTR>(&QueueMenuRequest), &hold)) return HRESULT_FROM_WIN32(GetLastError());
	try {
		auto work = std::make_unique<Work>(operation, std::move(paths), hold);
		Handle thread(CreateThread(nullptr, 0, RunWork, work.get(), 0, nullptr));
		if (!thread.value) { const auto error = HRESULT_FROM_WIN32(GetLastError()); FreeLibrary(hold); return error; }
		work.release();
		return S_OK;
	}
	catch (...) { FreeLibrary(hold); return E_OUTOFMEMORY; }
}

HRESULT QueuePlaceholderRequest(bool keepOnDevice, std::vector<std::wstring> paths) noexcept { return QueueMenuRequest(keepOnDevice ? L"keepOnDevice" : L"freeSpace", std::move(paths)); }
HRESULT QueueSyncIssuesRequest(std::vector<std::wstring> paths) noexcept { return QueueMenuRequest(L"showSyncIssues", std::move(paths)); }
