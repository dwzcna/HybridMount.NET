#include "Extension.h"
#include <wincodec.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <algorithm>
#include <limits>
using namespace Microsoft::WRL;

namespace {
    constexpr UINT64 PixelLimit=64000000;
    /// <summary>有界 WIC 缩放及 PNG 编码；仅处理工作进程提供的字节流，不打开挂载路径。</summary>
    HRESULT Encode(IWICImagingFactory* factory,IWICBitmapSource* source,UINT size,BYTE** png,DWORD* length) {
        UINT width=0,height=0;auto hr=source->GetSize(&width,&height);if(FAILED(hr))return hr;
        if(!width||!height||static_cast<UINT64>(width)*height>PixelLimit)return E_INVALIDARG;
        const auto factor=std::min(1.0,static_cast<double>(size)/std::max(width,height));
        const auto targetWidth=std::max(1u,static_cast<UINT>(width*factor));const auto targetHeight=std::max(1u,static_cast<UINT>(height*factor));
        ComPtr<IWICBitmapScaler> scaler;if(FAILED(hr=factory->CreateBitmapScaler(&scaler))||FAILED(hr=scaler->Initialize(source,targetWidth,targetHeight,WICBitmapInterpolationModeFant)))return hr;
        ComPtr<IStream> output;if(FAILED(hr=CreateStreamOnHGlobal(nullptr,TRUE,&output)))return hr;
        ComPtr<IWICBitmapEncoder> encoder;ComPtr<IWICBitmapFrameEncode> frame;ComPtr<IPropertyBag2> properties;
        if(FAILED(hr=factory->CreateEncoder(GUID_ContainerFormatPng,nullptr,&encoder))||FAILED(hr=encoder->Initialize(output.Get(),WICBitmapEncoderNoCache))||
           FAILED(hr=encoder->CreateNewFrame(&frame,&properties))||FAILED(hr=frame->Initialize(properties.Get()))||FAILED(hr=frame->SetSize(targetWidth,targetHeight)))return hr;
        WICPixelFormatGUID format=GUID_WICPixelFormat32bppBGRA;if(FAILED(hr=frame->SetPixelFormat(&format))||FAILED(hr=frame->WriteSource(scaler.Get(),nullptr))||FAILED(hr=frame->Commit())||FAILED(hr=encoder->Commit()))return hr;
        STATSTG stat{};if(FAILED(hr=output->Stat(&stat,STATFLAG_NONAME)))return hr;
        if(stat.cbSize.QuadPart<8||stat.cbSize.QuadPart>384*1024)return E_INVALIDARG;
        LARGE_INTEGER zero{};if(FAILED(hr=output->Seek(zero,STREAM_SEEK_SET,nullptr)))return hr;
        auto bytes=static_cast<BYTE*>(CoTaskMemAlloc(static_cast<SIZE_T>(stat.cbSize.QuadPart)));if(!bytes)return E_OUTOFMEMORY;
        ULONG read=0;hr=output->Read(bytes,static_cast<ULONG>(stat.cbSize.QuadPart),&read);
        if(FAILED(hr)||read!=stat.cbSize.QuadPart){CoTaskMemFree(bytes);return E_FAIL;}*png=bytes;*length=read;return S_OK;
    }
    /// <summary>由系统 WIC 读取图片第一帧；尺寸在像素分配前验证，动画不展开全部帧。</summary>
    HRESULT Image(IStream* input,UINT size,BYTE** png,DWORD* length) {
        ComPtr<IWICImagingFactory> factory;ComPtr<IWICBitmapDecoder> decoder;ComPtr<IWICBitmapFrameDecode> frame;
        auto hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory));if(FAILED(hr))return hr;
        if(FAILED(hr=factory->CreateDecoderFromStream(input,nullptr,WICDecodeMetadataCacheOnDemand,&decoder))||FAILED(hr=decoder->GetFrame(0,&frame)))return hr;
        // 优先用容器自带的小图；方向变换仍采用原帧元数据，避免大照片为小图分配全部像素。
        ComPtr<IWICBitmapSource> source=frame;ComPtr<IWICBitmapSource> embedded;
        if(FAILED(frame->GetThumbnail(&embedded)))decoder->GetPreview(&embedded);
        if(embedded){UINT width{},height{};if(SUCCEEDED(embedded->GetSize(&width,&height))&&std::max(width,height)>=size&&static_cast<UINT64>(width)*height<=PixelLimit)source=embedded;}
        ComPtr<IWICMetadataQueryReader> metadata;PROPVARIANT orientation{};PropVariantInit(&orientation);
        if(SUCCEEDED(frame->GetMetadataQueryReader(&metadata))&&SUCCEEDED(metadata->GetMetadataByName(L"/app1/ifd/{ushort=274}",&orientation))&&orientation.vt==VT_UI2) {
            WICBitmapTransformOptions transform=WICBitmapTransformRotate0;
            switch(orientation.uiVal){case 2:transform=WICBitmapTransformFlipHorizontal;break;case 3:transform=WICBitmapTransformRotate180;break;case 4:transform=WICBitmapTransformFlipVertical;break;
            case 5:transform=static_cast<WICBitmapTransformOptions>(WICBitmapTransformRotate90|WICBitmapTransformFlipHorizontal);break;case 6:transform=WICBitmapTransformRotate90;break;
            case 7:transform=static_cast<WICBitmapTransformOptions>(WICBitmapTransformRotate270|WICBitmapTransformFlipHorizontal);break;case 8:transform=WICBitmapTransformRotate270;break;default:break;}
            PropVariantClear(&orientation);
            if(transform!=WICBitmapTransformRotate0){ComPtr<IWICBitmapFlipRotator> rotated;if(FAILED(hr=factory->CreateBitmapFlipRotator(&rotated))||FAILED(hr=rotated->Initialize(source.Get(),transform)))return hr;return Encode(factory.Get(),rotated.Get(),size,png,length);}
        }else PropVariantClear(&orientation);
        return Encode(factory.Get(),source.Get(),size,png,length);
    }
    /// <summary>从可寻址流取得首个视频帧；不访问 URL，不启用网络解析或硬件变换。</summary>
    HRESULT Video(IStream* input,UINT size,BYTE** png,DWORD* length) {
        ComPtr<IMFByteStream> stream;ComPtr<IMFAttributes> attributes;ComPtr<IMFSourceReader> reader;ComPtr<IMFMediaType> type;
        auto hr=MFCreateMFByteStreamOnStreamEx(input,&stream);if(FAILED(hr))return hr;
        if(FAILED(hr=MFCreateAttributes(&attributes,3))||FAILED(hr=attributes->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING,TRUE))||
           FAILED(hr=attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS,FALSE))||FAILED(hr=MFCreateSourceReaderFromByteStream(stream.Get(),attributes.Get(),&reader)))return hr;
        if(FAILED(hr=reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS),FALSE))||FAILED(hr=reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),TRUE))||FAILED(hr=MFCreateMediaType(&type)))return hr;
        if(FAILED(hr=type->SetGUID(MF_MT_MAJOR_TYPE,MFMediaType_Video))||FAILED(hr=type->SetGUID(MF_MT_SUBTYPE,MFVideoFormat_RGB32))||FAILED(hr=reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),nullptr,type.Get())))return hr;
        if(FAILED(hr=reader->GetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),&type)))return hr;
        UINT32 width=0,height=0;if(FAILED(hr=MFGetAttributeSize(type.Get(),MF_MT_FRAME_SIZE,&width,&height)))return hr;
        if(!width||!height||static_cast<UINT64>(width)*height>PixelLimit)return E_INVALIDARG;
        ComPtr<IMFSample> sample;
        for(UINT count=0;count<32&&!sample;count++) {DWORD flags=0;hr=reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),0,nullptr,&flags,nullptr,&sample);if(FAILED(hr))return hr;if(flags&MF_SOURCE_READERF_ENDOFSTREAM)return E_FAIL;}
        if(!sample)return E_FAIL;
        ComPtr<IMFMediaBuffer> buffer;if(FAILED(hr=sample->ConvertToContiguousBuffer(&buffer)))return hr;
        std::vector<BYTE> pixels(static_cast<size_t>(width)*height*4);ComPtr<IMF2DBuffer> planar;
        if(SUCCEEDED(buffer.As(&planar))) {
            BYTE* scan=nullptr;LONG pitch=0;if(FAILED(hr=planar->Lock2D(&scan,&pitch)))return hr;
            if(static_cast<UINT64>(pitch<0?-static_cast<LONGLONG>(pitch):pitch)<static_cast<UINT64>(width)*4){planar->Unlock2D();return E_INVALIDARG;}
            for(UINT y=0;y<height;y++)memcpy(pixels.data()+static_cast<size_t>(y)*width*4,scan+static_cast<ptrdiff_t>(y)*pitch,static_cast<size_t>(width)*4);planar->Unlock2D();
        }else {
            BYTE* data=nullptr;DWORD used=0;if(FAILED(hr=buffer->Lock(&data,nullptr,&used)))return hr;
            if(used<static_cast<UINT64>(width)*height*4){buffer->Unlock();return E_INVALIDARG;}
            // RGB32 的标准连续缓冲区为底向上；2D 缓冲区走上面的显式扫描行路径。
            for(UINT y=0;y<height;y++)memcpy(pixels.data()+static_cast<size_t>(y)*width*4,data+static_cast<size_t>(height-1-y)*width*4,static_cast<size_t>(width)*4);buffer->Unlock();
        }
        for(size_t alpha=3;alpha<pixels.size();alpha+=4)pixels[alpha]=255;
        ComPtr<IWICImagingFactory> factory;ComPtr<IWICBitmap> bitmap;
        if(FAILED(hr=CoCreateInstance(CLSID_WICImagingFactory,nullptr,CLSCTX_INPROC_SERVER,IID_PPV_ARGS(&factory)))||
           FAILED(hr=factory->CreateBitmapFromMemory(width,height,GUID_WICPixelFormat32bppBGRA,width*4,static_cast<UINT>(pixels.size()),pixels.data(),&bitmap)))return hr;
        return Encode(factory.Get(),bitmap.Get(),size,png,length);
    }
}
/// <summary>仅供隔离工作进程调用；输入 IStream 为父进程预算代理，输出由调用方 CoTaskMemFree，失败不返回半张图。</summary>
extern "C" HRESULT __stdcall HybridMountDecodePreview(IStream* input,const wchar_t* extension,UINT size,BYTE** png,DWORD* length) noexcept {
    if(!input||!extension||!png||!length||size<16||size>256)return E_INVALIDARG;*png=nullptr;*length=0;
    const auto initialized=CoInitializeEx(nullptr,COINIT_MULTITHREADED);
    try {
        const bool video=_wcsicmp(extension,L".mp4")==0;
        HMODULE mediaPlatform=nullptr,mediaReader=nullptr;
        if(video){mediaPlatform=LoadLibraryExW(L"mfplat.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);mediaReader=LoadLibraryExW(L"mfreadwrite.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32);}
        if(video&&(!mediaPlatform||!mediaReader)){if(mediaPlatform)FreeLibrary(mediaPlatform);if(mediaReader)FreeLibrary(mediaReader);if(SUCCEEDED(initialized))CoUninitialize();return HRESULT_FROM_WIN32(ERROR_MOD_NOT_FOUND);}
        auto hr=video?MFStartup(MF_VERSION,MFSTARTUP_FULL):S_OK;
        if(SUCCEEDED(hr)){hr=video?Video(input,size,png,length):Image(input,size,png,length);if(video)MFShutdown();}
        if(mediaPlatform)FreeLibrary(mediaPlatform);if(mediaReader)FreeLibrary(mediaReader);
        if(SUCCEEDED(initialized))CoUninitialize();return hr;
    }catch(...){if(SUCCEEDED(initialized))CoUninitialize();return E_OUTOFMEMORY;}
}
