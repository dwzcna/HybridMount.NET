#include "Extension.h"
#include "../HybridMount.Stock/ThumbnailCache.h"
#include "../HybridMount.Stock/LocalThumbnail.h"
#include "../HybridMount.Stock/NativeCommon.h"
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
    HRESULT Decode(std::vector<BYTE>& png,HBITMAP* result,UINT maximum=0) {
        if(png.size()<8||png.size()>384*1024)return E_INVALIDARG;
        const BYTE signature[]{137,80,78,71,13,10,26,10};if(memcmp(png.data(),signature,sizeof(signature))!=0)return E_INVALIDARG;
        ComPtr<IWICImagingFactory> factory;ComPtr<IWICStream> stream;ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;ComPtr<IWICFormatConverter> converter;
        auto status=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));if(FAILED(status))return status;
        if(FAILED(status=factory->CreateStream(&stream))||FAILED(status=stream->InitializeFromMemory(png.data(),static_cast<DWORD>(png.size())))||
            FAILED(status=factory->CreateDecoderFromStream(stream.Get(),nullptr,WICDecodeMetadataCacheOnDemand,&decoder))||FAILED(status=decoder->GetFrame(0,&frame)))return status;
        UINT width=0,height=0;if(FAILED(status=frame->GetSize(&width,&height)))return status;
        if(width<1||height<1||width>256||height>256)return E_INVALIDARG;
        ComPtr<IWICBitmapSource> source=frame;ComPtr<IWICBitmapScaler> scaler;
        if(maximum&&std::max(width,height)>maximum){const auto longest=std::max(width,height);width=std::max(1u,width*maximum/longest);height=std::max(1u,height*maximum/longest);if(FAILED(status=factory->CreateBitmapScaler(&scaler))||FAILED(status=scaler->Initialize(frame.Get(),width,height,WICBitmapInterpolationModeFant)))return status;source=scaler;}
        if(FAILED(status=factory->CreateFormatConverter(&converter))||FAILED(status=converter->Initialize(source.Get(),GUID_WICPixelFormat32bppPBGRA,WICBitmapDitherTypeNone,nullptr,0,WICBitmapPaletteTypeCustom)))return status;
        void* pixels=nullptr;auto bitmap=Bitmap(width,&pixels,height);if(!bitmap)return E_OUTOFMEMORY;
        status=converter->CopyPixels(nullptr,width*4,width*height*4,static_cast<BYTE*>(pixels));
        if(FAILED(status)){DeleteObject(bitmap);return status;}*result=bitmap;return S_OK;
    }
    /// <summary>初始化仅复制文件系统身份；只处理本产品根内支持的媒体，其余类型交还系统。</summary>
    class ThumbnailProvider final : public RuntimeClass<RuntimeClassFlags<ClassicCom>,IInitializeWithItem,IThumbnailProvider>,private ModuleLifetime {
        std::wstring _path;ThumbnailCacheSource _source;
    public:
        explicit ThumbnailProvider(const ThumbnailCacheSource* source):_source(source?*source:ThumbnailCacheSource{}){}
        IFACEMETHODIMP Initialize(IShellItem* item,DWORD mode) override {
            TraceShellProbe(L"Initialize");UNREFERENCED_PARAMETER(mode);if(!item)return E_INVALIDARG;if(!_path.empty())return HRESULT_FROM_WIN32(ERROR_ALREADY_INITIALIZED);
            PWSTR path=nullptr;auto status=item->GetDisplayName(SIGDN_FILESYSPATH,&path);if(FAILED(status))return status;
            try{_path=path;CoTaskMemFree(path);return S_OK;}catch(...){CoTaskMemFree(path);return E_OUTOFMEMORY;}
        }
        IFACEMETHODIMP GetThumbnail(UINT size,HBITMAP* bitmap,WTS_ALPHATYPE* alpha) override {
            TraceShellProbe(L"GetThumbnail");if(!bitmap||!alpha)return E_POINTER;*bitmap=nullptr;*alpha=WTSAT_UNKNOWN;
            if(_path.empty()||size==0)return E_INVALIDARG;
            const auto extension=PathFindExtensionW(_path.c_str());bool supported=false;
            for(const auto candidate:{L".exe",L".jpg",L".jpeg",L".png",L".bmp",L".gif",L".tif",L".tiff",L".heic",L".heif",L".webp",L".avif",L".mp4"})supported|=_wcsicmp(extension,candidate)==0;
            if(!supported)return E_NOTIMPL;
            GUID mount{};if(!IsOwnedMountPath(_path,mount))return E_NOTIMPL;
            TraceShellProbe(L"Owned");try {
                const auto bounded=std::clamp(size,16u,256u);std::vector<BYTE> png;
                auto status=E_FAIL;
                if(_source.Read){
                    if(_source.Closing&&_source.Closing->load())return CO_E_SERVER_STOPPING;
                    BYTE* bytes=nullptr;DWORD length=0;status=_source.Read(_source.Context,_path.c_str(),bounded,&bytes,&length);
                    // 回调分配的内存无论结果如何均由本侧归还；失败绝不缓存通用图标为成功缩略图。
                    if(SUCCEEDED(status)&&bytes&&length>=8&&length<=384*1024){try{png.assign(bytes,bytes+length);}catch(...){CoTaskMemFree(bytes);return E_OUTOFMEMORY;}}
                    CoTaskMemFree(bytes);if(FAILED(status))return status;if(png.empty())return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
                    status=Decode(png,bitmap,std::min(size,256u));
                }else{png=hm::ReadAvailableThumbnail(_path,bounded);status=Decode(png,bitmap,std::min(size,256u));}
                if(SUCCEEDED(status))*alpha=WTSAT_ARGB;return status;
            }catch(const hm::Failure& error){return error.Code;}catch(...){return E_FAIL;}
        }
    };
}
HRESULT CreateThumbnailHandler(REFIID iid,void** result,const ThumbnailCacheSource* source) noexcept {
    if(!result)return E_POINTER;*result=nullptr;
    auto provider=Make<ThumbnailProvider>(source);return provider?provider.CopyTo(iid,result):E_OUTOFMEMORY;
}
