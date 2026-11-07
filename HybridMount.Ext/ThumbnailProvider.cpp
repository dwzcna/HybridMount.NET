#include "Extension.h"
#include <thumbcache.h>
#include <shellapi.h>
#include <wincodec.h>
#include <algorithm>

using namespace Microsoft::WRL;
namespace {
    /// <summary>创建有界的 32 位顶向下位图；调用方负责 DeleteObject。</summary>
    HBITMAP Bitmap(UINT size,void** pixels,UINT height=0) noexcept {
        if(!height)height=size;
        BITMAPINFO info{};info.bmiHeader.biSize=sizeof(BITMAPINFOHEADER);info.bmiHeader.biWidth=static_cast<LONG>(size);
        info.bmiHeader.biHeight=-static_cast<LONG>(height);info.bmiHeader.biPlanes=1;info.bmiHeader.biBitCount=32;
        return CreateDIBSection(nullptr,&info,DIB_RGB_COLORS,pixels,nullptr,0);
    }
    /// <summary>只解码有界 PNG 缓存；不打开 Shell 项目的内容流。</summary>
    HRESULT Decode(std::vector<BYTE>& png,HBITMAP* result) {
        if(png.size()<8||png.size()>384*1024)return E_INVALIDARG;
        const BYTE signature[]{137,80,78,71,13,10,26,10};if(memcmp(png.data(),signature,sizeof(signature))!=0)return E_INVALIDARG;
        ComPtr<IWICImagingFactory> factory;ComPtr<IWICStream> stream;ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> converter;
        auto status=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));if(FAILED(status))return status;
        if(FAILED(status=factory->CreateStream(&stream))||FAILED(status=stream->InitializeFromMemory(png.data(),static_cast<DWORD>(png.size())))||
            FAILED(status=factory->CreateDecoderFromStream(stream.Get(),nullptr,WICDecodeMetadataCacheOnDemand,&decoder))||FAILED(status=decoder->GetFrame(0,&frame)))return status;
        UINT width=0,height=0;if(FAILED(status=frame->GetSize(&width,&height)))return status;
        if(width<1||height<1||width>256||height>256)return E_INVALIDARG;
        if(FAILED(status=factory->CreateFormatConverter(&converter))||FAILED(status=converter->Initialize(frame.Get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom)))return status;
        void* pixels=nullptr;auto bitmap=Bitmap(width,&pixels,height);if(!bitmap)return E_OUTOFMEMORY;
        status=converter->CopyPixels(nullptr,width*4,width*height*4,static_cast<BYTE*>(pixels));
        if(FAILED(status)){DeleteObject(bitmap);return status;}*result=bitmap;return S_OK;
    }
    /// <summary>系统通用程序图标不引用占位路径，不触发目标 EXE 的图标抽取或水合。</summary>
    HRESULT Generic(UINT size,HBITMAP* result,const wchar_t* extension) {
        SHSTOCKICONINFO stock{sizeof(stock)};SHFILEINFOW associated{};HRESULT status=S_OK;
        const auto dummy=std::wstring(L"file")+extension;
        if(_wcsicmp(extension,L".exe")!=0&&SHGetFileInfoW(dummy.c_str(),FILE_ATTRIBUTE_NORMAL,&associated,sizeof(associated),SHGFI_USEFILEATTRIBUTES|SHGFI_ICON|SHGFI_LARGEICON))stock.hIcon=associated.hIcon;
        else status=SHGetStockIconInfo(SIID_APPLICATION,SHGSI_ICON|SHGSI_LARGEICON,&stock);
        if(FAILED(status))return status;
        void* pixels=nullptr;auto bitmap=Bitmap(size,&pixels);auto dc=CreateCompatibleDC(nullptr);
        if(!bitmap||!dc){if(bitmap)DeleteObject(bitmap);if(dc)DeleteDC(dc);DestroyIcon(stock.hIcon);return E_OUTOFMEMORY;}
        memset(pixels,0,size*size*4);auto previous=SelectObject(dc,bitmap);
        const auto drawn=DrawIconEx(dc,0,0,stock.hIcon,static_cast<int>(size),static_cast<int>(size),0,nullptr,DI_NORMAL);
        SelectObject(dc,previous);DeleteDC(dc);DestroyIcon(stock.hIcon);
        if(!drawn){DeleteObject(bitmap);return E_FAIL;}
        bool alpha=false;auto bytes=static_cast<BYTE*>(pixels);for(UINT offset=3;offset<size*size*4;offset+=4)alpha|=bytes[offset]!=0;
        if(!alpha)for(UINT offset=3;offset<size*size*4;offset+=4)bytes[offset]=255;
        *result=bitmap;return S_OK;
    }
    /// <summary>初始化仅复制文件系统身份；只处理本产品 EXE，其余类型交还系统。</summary>
    class ThumbnailProvider final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,IInitializeWithItem,IThumbnailProvider>,private ModuleLifetime {
        std::wstring _path;
    public:
        IFACEMETHODIMP Initialize(IShellItem* item,DWORD mode) override {
            TraceShellProbe(L"Initialize");UNREFERENCED_PARAMETER(mode);if(!item)return E_INVALIDARG;if(!_path.empty())return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            PWSTR path=nullptr;auto status=item->GetDisplayName(SIGDN_FILESYSPATH,&path);if(FAILED(status))return status;
            try{_path=path;CoTaskMemFree(path);return S_OK;}catch(...){CoTaskMemFree(path);return E_OUTOFMEMORY;}
        }
        IFACEMETHODIMP GetThumbnail(UINT size,HBITMAP* bitmap,WTS_ALPHATYPE* alpha) override {
            TraceShellProbe(L"GetThumbnail");if(!bitmap||!alpha)return E_POINTER;*bitmap=nullptr;*alpha=WTSAT_UNKNOWN;
            if(_path.empty()||size==0)return E_INVALIDARG;
            const auto extension=PathFindExtensionW(_path.c_str());bool supported=false;
            for(const auto candidate:{L".exe",L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".mp4"})supported|=_wcsicmp(extension,candidate)==0;
            if(!supported)return E_NOTIMPL;
            GUID mount{};if(!IsOwnedMountPath(_path,mount))return E_NOTIMPL;
            TraceShellProbe(L"Owned");try {
                const auto bounded=std::clamp(size,16u,256u);std::vector<BYTE> png;
                auto status=E_FAIL;
                if(QueryCachedPreview(_path,bounded,png))status=Decode(png,bitmap);
                if(FAILED(status))status=Generic(bounded,bitmap,extension);
                if(SUCCEEDED(status))*alpha=WTSAT_ARGB;return status;
            }catch(...){return E_OUTOFMEMORY;}
        }
    };
}
HRESULT CreateThumbnailHandler(REFIID iid,void** result) noexcept {
    auto provider=Make<ThumbnailProvider>();return provider?provider.CopyTo(iid,result):E_OUTOFMEMORY;
}
