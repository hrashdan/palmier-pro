#include "ExportReadback.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <cstring>
#include <fstream>
#include <sstream>

using Microsoft::WRL::ComPtr;

namespace
{
    // Mirrors GpuCompositor.cpp / Scopes.cpp's ID3DInclude handler — duplicated so this class has no
    // dependency on their private members. Resolves #include "ExportColor.hlsl" next to the DLL.
    class ShaderIncludeHandler : public ID3DInclude
    {
    public:
        explicit ShaderIncludeHandler(const std::string& shadersDir) : shadersDir_(shadersDir) {}

        HRESULT __stdcall Open(D3D_INCLUDE_TYPE, LPCSTR pFileName, LPCVOID, LPCVOID* ppData, UINT* pBytes) override
        {
            std::string path = shadersDir_ + pFileName;
            std::ifstream file(path, std::ios::in | std::ios::binary);
            if (!file)
            {
                return E_FAIL;
            }
            std::ostringstream ss;
            ss << file.rdbuf();
            std::string contents = ss.str();
            char* buffer = new char[contents.size()];
            std::memcpy(buffer, contents.data(), contents.size());
            *ppData = buffer;
            *pBytes = static_cast<UINT>(contents.size());
            return S_OK;
        }

        HRESULT __stdcall Close(LPCVOID pData) override
        {
            delete[] static_cast<const char*>(pData);
            return S_OK;
        }

    private:
        std::string shadersDir_;
    };

    bool ReadFile(const std::string& path, std::string& outContents)
    {
        std::ifstream file(path, std::ios::in | std::ios::binary);
        if (!file)
        {
            return false;
        }
        std::ostringstream ss;
        ss << file.rdbuf();
        outContents = ss.str();
        return true;
    }

    void AnchorFunction() {}

    struct ExportParamsCb
    {
        uint32_t width;
        uint32_t height;
        uint32_t chromaWidth;
        uint32_t chromaHeight;
    };
}

ExportReadback::ExportReadback(ID3D11Device* device, ID3D11DeviceContext* context, Format format,
    int32_t width, int32_t height, int32_t ringDepth)
    : device_(device),
      context_(context),
      format_(format),
      width_(width),
      height_(height),
      ringDepth_(std::max(ringDepth, static_cast<int32_t>(kMapLag) + 1))
{
    chromaWidth_ = width_ / 2;
    if (format_ == Format::NV12)
    {
        chromaHeight_ = height_ / 2;
        planes_[0] = {DXGI_FORMAT_R8_UNORM, width_, height_, 1};
        planes_[1] = {DXGI_FORMAT_R8G8_UNORM, width_ / 2, height_ / 2, 2};
        planeCount_ = 2;
    }
    else // Yuv422p10le — 4:2:2, no vertical subsampling; U/V are full height.
    {
        chromaHeight_ = height_;
        planes_[0] = {DXGI_FORMAT_R16_UNORM, width_, height_, 2};
        planes_[1] = {DXGI_FORMAT_R16_UNORM, width_ / 2, height_, 2};
        planes_[2] = {DXGI_FORMAT_R16_UNORM, width_ / 2, height_, 2};
        planeCount_ = 3;
    }
}

ExportReadback::~ExportReadback() = default;

int64_t ExportReadback::PackedFrameBytes(Format format, int32_t width, int32_t height)
{
    if (width <= 0 || height <= 0 || (width & 1) != 0 || (height & 1) != 0)
    {
        return 0;
    }
    int64_t w = width;
    int64_t h = height;
    if (format == Format::NV12)
    {
        return w * h /* Y R8 */ + (w / 2) * (h / 2) * 2 /* UV R8G8 */;
    }
    return w * h * 2 /* Y R16 */ + (w / 2) * h * 2 * 2 /* U + V R16 */;
}

bool ExportReadback::ResolveShadersDir(std::string& outError)
{
    if (!shadersDir_.empty())
    {
        return true;
    }
    HMODULE hModule = nullptr;
    if (!GetModuleHandleExA(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCSTR>(&AnchorFunction), &hModule))
    {
        outError = "GetModuleHandleExA failed while resolving the shaders directory";
        return false;
    }
    char path[MAX_PATH]{};
    DWORD len = GetModuleFileNameA(hModule, path, MAX_PATH);
    if (len == 0 || len == MAX_PATH)
    {
        outError = "GetModuleFileNameA failed while resolving the shaders directory";
        return false;
    }
    std::string full(path, len);
    size_t slash = full.find_last_of("\\/");
    std::string dir = slash == std::string::npos ? "" : full.substr(0, slash + 1);
    shadersDir_ = dir + "shaders\\";
    return true;
}

bool ExportReadback::EnsureResources(std::string& outError)
{
    if (resourcesReady_)
    {
        return true;
    }
    if (!device_ || !context_)
    {
        outError = "ExportReadback: null device/context";
        return false;
    }
    if (width_ <= 0 || height_ <= 0 || (width_ & 1) != 0 || (height_ & 1) != 0)
    {
        outError = "ExportReadback: width/height must be positive and even";
        return false;
    }
    if (!ResolveShadersDir(outError))
    {
        return false;
    }

    const char* file = format_ == Format::NV12 ? "ExportNV12.hlsl" : "ExportYuv422p10.hlsl";
    const char* entry = format_ == Format::NV12 ? "ExportNV12CS" : "ExportYuv422p10CS";
    std::string source;
    std::string path = shadersDir_ + file;
    if (!ReadFile(path, source))
    {
        outError = "cannot read shader: " + path;
        return false;
    }
    ShaderIncludeHandler includeHandler(shadersDir_);
    ComPtr<ID3DBlob> blob, errorBlob;
    HRESULT hr = D3DCompile(source.data(), source.size(), path.c_str(), nullptr, &includeHandler,
        entry, "cs_5_0", 0, 0, &blob, &errorBlob);
    if (FAILED(hr))
    {
        outError = std::string("D3DCompile(") + file + ":" + entry + ") failed";
        if (errorBlob) outError += ": " + std::string(static_cast<const char*>(errorBlob->GetBufferPointer()), errorBlob->GetBufferSize());
        return false;
    }
    if (FAILED(device_->CreateComputeShader(blob->GetBufferPointer(), blob->GetBufferSize(), nullptr, &convertCs_)))
    {
        outError = std::string("CreateComputeShader(") + entry + ") failed";
        return false;
    }

    D3D11_BUFFER_DESC cbd{};
    cbd.ByteWidth = sizeof(ExportParamsCb);
    cbd.Usage = D3D11_USAGE_DEFAULT;
    cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    if (FAILED(device_->CreateBuffer(&cbd, nullptr, &constantBuffer_)))
    {
        outError = "CreateBuffer(export params) failed";
        return false;
    }

    for (int32_t p = 0; p < planeCount_; ++p)
    {
        const PlaneDesc& d = planes_[p];
        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(d.width);
        td.Height = static_cast<UINT>(d.height);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = d.format;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
        if (FAILED(device_->CreateTexture2D(&td, nullptr, &convertTex_[p])))
        {
            outError = "CreateTexture2D(export convert plane) failed";
            return false;
        }
        if (FAILED(device_->CreateUnorderedAccessView(convertTex_[p].Get(), nullptr, &convertUav_[p])))
        {
            outError = "CreateUnorderedAccessView(export convert plane) failed";
            return false;
        }
    }

    ring_.resize(static_cast<size_t>(ringDepth_));
    for (int32_t slot = 0; slot < ringDepth_; ++slot)
    {
        for (int32_t p = 0; p < planeCount_; ++p)
        {
            const PlaneDesc& d = planes_[p];
            D3D11_TEXTURE2D_DESC td{};
            td.Width = static_cast<UINT>(d.width);
            td.Height = static_cast<UINT>(d.height);
            td.MipLevels = 1;
            td.ArraySize = 1;
            td.Format = d.format;
            td.SampleDesc.Count = 1;
            td.Usage = D3D11_USAGE_STAGING;
            td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (FAILED(device_->CreateTexture2D(&td, nullptr, &ring_[static_cast<size_t>(slot)][p])))
            {
                outError = "CreateTexture2D(export staging ring) failed";
                return false;
            }
        }
    }

    resourcesReady_ = true;
    return true;
}

void ExportReadback::UpdateConstantBuffer()
{
    ExportParamsCb cb{static_cast<uint32_t>(width_), static_cast<uint32_t>(height_),
        static_cast<uint32_t>(chromaWidth_), static_cast<uint32_t>(chromaHeight_)};
    context_->UpdateSubresource(constantBuffer_.Get(), 0, nullptr, &cb, 0, 0);
}

bool ExportReadback::MapSlot(int32_t slot, int64_t frameIndex, Frame& outFrame, std::string& outError)
{
    outFrame.frameIndex = frameIndex;
    outFrame.planeCount = planeCount_;
    for (int32_t p = 0; p < planeCount_; ++p)
    {
        const PlaneDesc& d = planes_[p];
        int32_t rowBytes = d.width * d.bytesPerTexel;
        outFrame.rowBytes[static_cast<size_t>(p)] = rowBytes;
        outFrame.rowCount[static_cast<size_t>(p)] = d.height;
        std::vector<uint8_t>& dst = outFrame.planes[static_cast<size_t>(p)];
        dst.resize(static_cast<size_t>(rowBytes) * d.height);

        D3D11_MAPPED_SUBRESOURCE mapped{};
        HRESULT hr = context_->Map(ring_[static_cast<size_t>(slot)][p].Get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr))
        {
            outError = "Map(export staging ring) failed";
            return false;
        }
        const auto* src = static_cast<const uint8_t*>(mapped.pData);
        for (int32_t y = 0; y < d.height; ++y)
        {
            std::memcpy(dst.data() + static_cast<size_t>(y) * rowBytes,
                src + static_cast<size_t>(y) * mapped.RowPitch,
                static_cast<size_t>(rowBytes));
        }
        context_->Unmap(ring_[static_cast<size_t>(slot)][p].Get(), 0);
    }
    return true;
}

bool ExportReadback::Submit(ID3D11ShaderResourceView* accumSrv, int64_t frameIndex,
    bool& outHasFrame, Frame& outFrame, std::string& outError)
{
    outHasFrame = false;
    if (!accumSrv)
    {
        outError = "ExportReadback::Submit: null accumulator SRV";
        return false;
    }
    if (frameIndex != submitCount_)
    {
        outError = "ExportReadback::Submit: frameIndex must increase by 1 from 0";
        return false;
    }
    if (!EnsureResources(outError))
    {
        return false;
    }

    UpdateConstantBuffer();

    ID3D11ShaderResourceView* srvs[] = {accumSrv};
    ID3D11Buffer* cbs[] = {constantBuffer_.Get()};
    ID3D11UnorderedAccessView* uavs[3] = {};
    for (int32_t p = 0; p < planeCount_; ++p)
    {
        uavs[p] = convertUav_[p].Get();
    }

    context_->CSSetShader(convertCs_.Get(), nullptr, 0);
    context_->CSSetConstantBuffers(0, 1, cbs);
    context_->CSSetShaderResources(0, 1, srvs);
    context_->CSSetUnorderedAccessViews(0, static_cast<UINT>(planeCount_), uavs, nullptr);

    UINT groupsX = (static_cast<UINT>(chromaWidth_) + 7) / 8;
    UINT groupsY = (static_cast<UINT>(chromaHeight_) + 7) / 8;
    context_->Dispatch(groupsX, groupsY, 1);

    ID3D11UnorderedAccessView* nullUavs[3] = {};
    ID3D11ShaderResourceView* nullSrv[1] = {nullptr};
    context_->CSSetUnorderedAccessViews(0, static_cast<UINT>(planeCount_), nullUavs, nullptr);
    context_->CSSetShaderResources(0, 1, nullSrv);
    context_->CSSetShader(nullptr, nullptr, 0);

    // Issue frame N's copy into its slot (not waited on) — §5.4.
    int32_t writeSlot = static_cast<int32_t>(frameIndex % ringDepth_);
    for (int32_t p = 0; p < planeCount_; ++p)
    {
        context_->CopyResource(ring_[static_cast<size_t>(writeSlot)][p].Get(), convertTex_[p].Get());
    }

    submitCount_ = frameIndex + 1;

    // Map frame N-kMapLag, whose copy was issued kMapLag iterations ago — enough GPU slack that the
    // Map returns without stalling (§5.4). The first kMapLag submits have no such frame yet.
    if (frameIndex >= kMapLag)
    {
        int64_t readIndex = frameIndex - kMapLag;
        int32_t readSlot = static_cast<int32_t>(readIndex % ringDepth_);
        if (!MapSlot(readSlot, readIndex, outFrame, outError))
        {
            return false;
        }
        outHasFrame = true;
    }
    return true;
}

bool ExportReadback::Drain(std::vector<Frame>& outFrames, std::string& outError)
{
    if (!resourcesReady_)
    {
        return true; // nothing was ever submitted
    }
    int64_t first = std::max<int64_t>(0, submitCount_ - kMapLag);
    for (int64_t k = first; k < submitCount_; ++k)
    {
        Frame frame;
        int32_t slot = static_cast<int32_t>(k % ringDepth_);
        if (!MapSlot(slot, k, frame, outError))
        {
            return false;
        }
        outFrames.push_back(std::move(frame));
    }
    return true;
}
