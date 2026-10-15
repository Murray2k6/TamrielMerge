// TamrielMergeGraphics' ray tracing core (see RtCore.h). TamrielMerge's own, written from Microsoft's Direct3D 12 and
// DXR documentation; no other mod's code.
#include "RtCore.h"
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>

#include "gen/RtTrace.h"
#include "gen/RtTemporal.h"
#include "gen/RtSpatial.h"
#include "gen/FullscreenVS.h"
#include "gen/PreparePS.h"
#include "gen/CompositePS.h"
#include "gen/NormalsPS.h"

namespace rt
{
    namespace
    {
        // Per-frame limits: the work one frame may give the GPU.
        constexpr int MaxBuildsPerFrame = 24;
        constexpr std::uint64_t MaxTrianglesPerFrame = 262144;
        constexpr UINT MaxInstances = 40000;
        constexpr UINT MaxCapsules = 4096;
        constexpr std::uint64_t UploadPerFrame = 12ull << 20;
        constexpr std::uint64_t ChunkSize = 64ull << 20;
        constexpr std::uint64_t ScratchMax = 96ull << 20;
        constexpr double WatchdogSeconds = 2.0;

        // Descriptor tables: 12 shader resources (t2..t13), 6 unordered access views (u0..u5).
        constexpr UINT SrvCount = 12, UavCount = 6;
        enum Block { TraceBlock, Temporal0, Temporal1, Spatial0, Spatial1, Blocks };
        constexpr UINT BlockSize = SrvCount + UavCount;

        double Now()
        {
            static LARGE_INTEGER f{};
            if (!f.QuadPart) QueryPerformanceFrequency(&f);
            LARGE_INTEGER c{};
            QueryPerformanceCounter(&c);
            return double(c.QuadPart) / double(f.QuadPart);
        }

        template <class T> void SafeRelease(T*& p) { if (p) { p->Release(); p = nullptr; } }

        ID3D12Resource* MakeBuffer(ID3D12Device* dev, std::uint64_t size, D3D12_HEAP_TYPE heap, D3D12_RESOURCE_FLAGS flags, D3D12_RESOURCE_STATES state)
        {
            D3D12_HEAP_PROPERTIES hp{}; hp.Type = heap;
            D3D12_RESOURCE_DESC rd{};
            rd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER; rd.Width = std::max<std::uint64_t>(size, 256); rd.Height = 1; rd.DepthOrArraySize = 1;
            rd.MipLevels = 1; rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR; rd.Flags = flags;
            ID3D12Resource* r = nullptr;
            if (FAILED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, state, nullptr, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&r)))) return nullptr;
            return r;
        }

        void UavBarrier(ID3D12GraphicsCommandList4* l, ID3D12Resource* r)
        {
            D3D12_RESOURCE_BARRIER b{}; b.Type = D3D12_RESOURCE_BARRIER_TYPE_UAV; b.UAV.pResource = r;
            l->ResourceBarrier(1, &b);
        }

        // An SRV format for a game texture's (often typeless) format.
        DXGI_FORMAT ReadFormat(DXGI_FORMAT f)
        {
            switch (f)
            {
            case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_D24_UNORM_S8_UINT: return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
            case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_D32_FLOAT: return DXGI_FORMAT_R32_FLOAT;
            case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
            case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_D16_UNORM: return DXGI_FORMAT_R16_UNORM;
            case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
            case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
            case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
            case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
            default: return f;
            }
        }
    }

    void Engine::Log(const char* fmt, ...) const
    {
        if (!log) return;
        char buf[1024];
        va_list a;
        va_start(a, fmt);
        std::vsnprintf(buf, sizeof(buf), fmt, a);
        va_end(a);
        log(buf);
    }

    void Engine::Fail(const std::string& why)
    {
        if (!running && status.rfind("off:", 0) == 0) return;
        running = false;
        status = "off: " + why;
        Log("ray tracing: OFF: %s", why.c_str());
    }

    bool Engine::Start(ID3D11Device* device, bool debugLayer, LogFn logFn)
    {
        log = logFn;
        debug = debugLayer;
        d11 = device;
        auto stop = [&](const std::string& why, HRESULT hr = S_OK) {
            char b[64]; std::snprintf(b, sizeof(b), " (0x%08X)", static_cast<unsigned>(hr));
            Fail(why + (hr != S_OK ? b : ""));
            return false;
        };
        if (FAILED(d11->QueryInterface(__uuidof(ID3D11Device5), reinterpret_cast<void**>(&d11_5))) || !d11_5)
            return stop("the game's Direct3D 11 device has no shared fences (needs Windows 10 or later)");

        IDXGIDevice* dxgi = nullptr;
        IDXGIAdapter* a = nullptr;
        DXGI_ADAPTER_DESC ad{};
        if (FAILED(d11->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi))) || FAILED(dxgi->GetAdapter(&a)))
        { SafeRelease(dxgi); return stop("no DXGI adapter"); }
        a->GetDesc(&ad);
        a->QueryInterface(__uuidof(IDXGIAdapter3), reinterpret_cast<void**>(&adapter));
        a->Release(); dxgi->Release();
        IDXGIFactory4* factory = nullptr;
        IDXGIAdapter1* same = nullptr;
        if (FAILED(CreateDXGIFactory2(0, __uuidof(IDXGIFactory4), reinterpret_cast<void**>(&factory))) ||
            FAILED(factory->EnumAdapterByLuid(ad.AdapterLuid, __uuidof(IDXGIAdapter1), reinterpret_cast<void**>(&same))))
        { SafeRelease(factory); return stop("the game's GPU not found for Direct3D 12"); }
        factory->Release();
        if (debug)
        {
            ID3D12Debug1* dbg = nullptr;
            if (SUCCEEDED(D3D12GetDebugInterface(__uuidof(ID3D12Debug1), reinterpret_cast<void**>(&dbg))) && dbg)
            {
                dbg->EnableDebugLayer();
                dbg->SetEnableGPUBasedValidation(TRUE);
                dbg->Release();
                Log("ray tracing: Direct3D 12 debug layer and GPU-based validation on");
            }
            else Log("ray tracing: no Direct3D 12 debug layer (Graphics Tools not installed)");
        }
        HRESULT hr = D3D12CreateDevice(same, D3D_FEATURE_LEVEL_12_0, __uuidof(ID3D12Device5), reinterpret_cast<void**>(&dev));
        same->Release();
        if (FAILED(hr) || !dev) { dev = nullptr; return stop("no Direct3D 12 on this GPU", hr); }
        D3D12_FEATURE_DATA_D3D12_OPTIONS5 o5{};
        dev->CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &o5, sizeof(o5));
        D3D12_FEATURE_DATA_SHADER_MODEL sm{ D3D_SHADER_MODEL_6_5 };
        if (FAILED(dev->CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &sm, sizeof(sm)))) sm.HighestShaderModel = D3D_SHADER_MODEL_5_1;
        int tier = static_cast<int>(o5.RaytracingTier);
        Log("ray tracing: Direct3D 12 device on the game's GPU, DXR tier %d.%d, shader model %d.%d", tier / 10, tier % 10,
            static_cast<int>(sm.HighestShaderModel) >> 4, static_cast<int>(sm.HighestShaderModel) & 15);
        if (o5.RaytracingTier < D3D12_RAYTRACING_TIER_1_1) return stop("the GPU has no inline ray tracing (DXR 1.1)");
        if (sm.HighestShaderModel < D3D_SHADER_MODEL_6_5) return stop("the GPU's driver has no shader model 6.5");

        D3D12_COMMAND_QUEUE_DESC qd{};
        qd.Type = D3D12_COMMAND_LIST_TYPE_COMPUTE;
        if (FAILED(hr = dev->CreateCommandQueue(&qd, __uuidof(ID3D12CommandQueue), reinterpret_cast<void**>(&queue)))) return stop("no compute queue", hr);
        queue->GetTimestampFrequency(&timestampFrequency);
        // The two fences, shared with Direct3D 11.
        for (auto [f, f11] : { std::pair{ &fenceIn, &fenceIn11 }, std::pair{ &fenceOut, &fenceOut11 } })
        {
            if (FAILED(hr = dev->CreateFence(0, D3D12_FENCE_FLAG_SHARED, __uuidof(ID3D12Fence), reinterpret_cast<void**>(f)))) return stop("no shared fence", hr);
            HANDLE h = nullptr;
            if (FAILED(hr = dev->CreateSharedHandle(*f, nullptr, GENERIC_ALL, nullptr, &h))) return stop("the fence can't be shared", hr);
            hr = d11_5->OpenSharedFence(h, __uuidof(ID3D11Fence), reinterpret_cast<void**>(f11));
            CloseHandle(h);
            if (FAILED(hr)) return stop("Direct3D 11 couldn't open the shared fence", hr);
        }
        idleEvent = CreateEventA(nullptr, FALSE, FALSE, nullptr);

        if (!CreateRootAndPipelines()) return false;
        D3D12_DESCRIPTOR_HEAP_DESC hd{};
        hd.Type = D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV; hd.NumDescriptors = Blocks * BlockSize; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE;
        if (FAILED(hr = dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&heap)))) return stop("no descriptor heap", hr);
        hd.NumDescriptors = 1; hd.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;
        if (FAILED(hr = dev->CreateDescriptorHeap(&hd, __uuidof(ID3D12DescriptorHeap), reinterpret_cast<void**>(&cpuHeap)))) return stop("no descriptor heap", hr);
        descSize = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
        D3D12_QUERY_HEAP_DESC qh{}; qh.Type = D3D12_QUERY_HEAP_TYPE_TIMESTAMP; qh.Count = 2 * Contexts;
        if (FAILED(dev->CreateQueryHeap(&qh, __uuidof(ID3D12QueryHeap), reinterpret_cast<void**>(&timestamps)))) timestamps = nullptr;
        stats = MakeBuffer(dev, 256, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
        if (!stats) return stop("no statistics buffer");
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC u{};
            u.Format = DXGI_FORMAT_R32_TYPELESS; u.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
            u.Buffer.NumElements = 4; u.Buffer.Flags = D3D12_BUFFER_UAV_FLAG_RAW;
            dev->CreateUnorderedAccessView(stats, nullptr, &u, cpuHeap->GetCPUDescriptorHandleForHeapStart());
        }
        for (auto& fc : contexts)
        {
            if (FAILED(hr = dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_COMPUTE, __uuidof(ID3D12CommandAllocator), reinterpret_cast<void**>(&fc.allocator)))) return stop("no command allocator", hr);
            for (auto* l : { &fc.listA, &fc.listB })
            {
                if (FAILED(hr = dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_COMPUTE, fc.allocator, nullptr, __uuidof(ID3D12GraphicsCommandList4), reinterpret_cast<void**>(l)))) return stop("no command list", hr);
                (*l)->Close();
            }
            fc.upload = MakeBuffer(dev, UploadPerFrame, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ);
            fc.readback = MakeBuffer(dev, 256, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST);
            D3D12_RANGE none{ 0, 0 };
            if (!fc.upload || !fc.readback || FAILED(fc.upload->Map(0, &none, reinterpret_cast<void**>(&fc.mapped)))) return stop("no upload memory");
        }
        if (!CreateD3D11Side()) return false;
        running = true;
        status = "on";
        Log("ray tracing: ready (inline ray queries in compute; shared textures and fences with Direct3D 11)");
        return true;
    }

    bool Engine::CreateRootAndPipelines()
    {
        D3D12_DESCRIPTOR_RANGE ranges[2]{};
        ranges[0].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_SRV; ranges[0].NumDescriptors = SrvCount; ranges[0].BaseShaderRegister = 2;
        ranges[1].RangeType = D3D12_DESCRIPTOR_RANGE_TYPE_UAV; ranges[1].NumDescriptors = UavCount; ranges[1].BaseShaderRegister = 0;
        D3D12_ROOT_PARAMETER p[5]{};
        p[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_CBV; p[0].Descriptor.ShaderRegister = 0;
        p[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[1].Descriptor.ShaderRegister = 0;   // the TLAS
        p[2].ParameterType = D3D12_ROOT_PARAMETER_TYPE_SRV; p[2].Descriptor.ShaderRegister = 1;   // capsules
        p[3].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[3].DescriptorTable.NumDescriptorRanges = 1; p[3].DescriptorTable.pDescriptorRanges = &ranges[0];
        p[4].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE; p[4].DescriptorTable.NumDescriptorRanges = 1; p[4].DescriptorTable.pDescriptorRanges = &ranges[1];
        for (auto& x : p) x.ShaderVisibility = D3D12_SHADER_VISIBILITY_ALL;
        D3D12_ROOT_SIGNATURE_DESC rd{};
        rd.NumParameters = 5; rd.pParameters = p;
        ID3DBlob* blob = nullptr, * err = nullptr;
        HRESULT hr = D3D12SerializeRootSignature(&rd, D3D_ROOT_SIGNATURE_VERSION_1, &blob, &err);
        if (FAILED(hr)) { Log("ray tracing: root signature: %s", err ? static_cast<const char*>(err->GetBufferPointer()) : "?"); SafeRelease(err); Fail("no root signature"); return false; }
        hr = dev->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), __uuidof(ID3D12RootSignature), reinterpret_cast<void**>(&root));
        blob->Release(); SafeRelease(err);
        if (FAILED(hr)) { Fail("no root signature"); return false; }
        struct { const void* code; size_t size; ID3D12PipelineState** out; const char* name; } pso[] = {
            { g_RtTrace, sizeof(g_RtTrace), &traceState, "trace" },
            { g_RtTemporal, sizeof(g_RtTemporal), &temporalState, "temporal" },
            { g_RtSpatial, sizeof(g_RtSpatial), &spatialState, "spatial" },
        };
        for (auto& s : pso)
        {
            D3D12_COMPUTE_PIPELINE_STATE_DESC cd{};
            cd.pRootSignature = root; cd.CS = { s.code, s.size };
            if (FAILED(hr = dev->CreateComputePipelineState(&cd, __uuidof(ID3D12PipelineState), reinterpret_cast<void**>(s.out))))
            { char b[96]; std::snprintf(b, sizeof(b), "the %s shader didn't load (0x%08X)", s.name, static_cast<unsigned>(hr)); Fail(b); return false; }
        }
        return true;
    }

    bool Engine::CreateD3D11Side()
    {
        ID3D11Device1* d1 = nullptr;
        if (FAILED(d11->QueryInterface(__uuidof(ID3D11Device1), reinterpret_cast<void**>(&d1))) || !d1) { Fail("no ID3D11Device1"); return false; }
        D3D_FEATURE_LEVEL level = d11->GetFeatureLevel();
        HRESULT hr = d1->CreateDeviceContextState(0, &level, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device1), nullptr, &state11);
        d1->Release();
        if (FAILED(hr)) { Fail("no Direct3D 11 state object"); return false; }
        if (FAILED(d11->CreateVertexShader(g_FullscreenVS, sizeof(g_FullscreenVS), nullptr, &vs)) ||
            FAILED(d11->CreatePixelShader(g_PreparePS, sizeof(g_PreparePS), nullptr, &preparePs)) ||
            FAILED(d11->CreatePixelShader(g_CompositePS, sizeof(g_CompositePS), nullptr, &compositePs)) ||
            FAILED(d11->CreatePixelShader(g_NormalsPS, sizeof(g_NormalsPS), nullptr, &normalsPs)))
        { Fail("the Direct3D 11 shaders didn't load"); return false; }
        D3D11_BUFFER_DESC bd{};
        bd.ByteWidth = (sizeof(CompositeConstants) + 15) & ~15u; bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(d11->CreateBuffer(&bd, nullptr, &compositeCb))) { Fail("no constant buffer"); return false; }
        return true;
    }

    bool Engine::CreateTexture(Texture& t, UINT w, UINT h, DXGI_FORMAT f, bool shared, D3D12_RESOURCE_FLAGS flags)
    {
        t.state = D3D12_RESOURCE_STATE_COMMON;
        HRESULT hr;
        if (!shared)
        {
            D3D12_HEAP_PROPERTIES hp{}; hp.Type = D3D12_HEAP_TYPE_DEFAULT;
            D3D12_RESOURCE_DESC rd{};
            rd.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D; rd.Width = w; rd.Height = h; rd.DepthOrArraySize = 1; rd.MipLevels = 1;
            rd.Format = f; rd.SampleDesc.Count = 1; rd.Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN; rd.Flags = flags;
            hr = dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &rd, t.state, nullptr, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&t.res));
            if (FAILED(hr)) { Log("ray tracing: texture %ux%u format %d: 0x%08X", w, h, static_cast<int>(f), static_cast<unsigned>(hr)); t.res = nullptr; return false; }
            return true;
        }
        // Shared: made by Direct3D 11 (an NT handle), opened by Direct3D 12.
        D3D11_TEXTURE2D_DESC d{};
        d.Width = w; d.Height = h; d.MipLevels = 1; d.ArraySize = 1; d.Format = f; d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT;
        d.BindFlags = D3D11_BIND_SHADER_RESOURCE | ((flags & D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) ? D3D11_BIND_RENDER_TARGET : 0) |
                      ((flags & D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS) ? D3D11_BIND_UNORDERED_ACCESS : 0);
        d.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED;
        if (FAILED(hr = d11->CreateTexture2D(&d, nullptr, &t.tex11)))
        { Log("ray tracing: shared texture %ux%u format %d: 0x%08X", w, h, static_cast<int>(f), static_cast<unsigned>(hr)); t.tex11 = nullptr; return false; }
        IDXGIResource1* r1 = nullptr;
        HANDLE handle = nullptr;
        hr = t.tex11->QueryInterface(__uuidof(IDXGIResource1), reinterpret_cast<void**>(&r1));
        if (SUCCEEDED(hr)) hr = r1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &handle);
        if (r1) r1->Release();
        if (SUCCEEDED(hr)) hr = dev->OpenSharedHandle(handle, __uuidof(ID3D12Resource), reinterpret_cast<void**>(&t.res));
        if (handle) CloseHandle(handle);
        if (FAILED(hr)) { Log("ray tracing: Direct3D 12 couldn't open a shared texture: 0x%08X", static_cast<unsigned>(hr)); t.res = nullptr; return false; }
        if (debug) Log("ray tracing: shared texture format %d, Direct3D 12 flags 0x%X", static_cast<int>(f), static_cast<unsigned>(t.res->GetDesc().Flags));
        if ((d.BindFlags & D3D11_BIND_RENDER_TARGET) && FAILED(d11->CreateRenderTargetView(t.tex11, nullptr, &t.rtv))) return false;
        if (FAILED(d11->CreateShaderResourceView(t.tex11, nullptr, &t.srv))) return false;
        return true;
    }

    void Engine::ReleaseTextures()
    {
        auto drop = [&](Texture& t) { SafeRelease(t.rtv); SafeRelease(t.srv); SafeRelease(t.tex11); SafeRelease(t.res); t.state = D3D12_RESOURCE_STATE_COMMON; };
        for (Texture* t : { &inDepth, &inMotion, &inHdr }) drop(*t);
        for (int i = 0; i < 3; ++i) { drop(raw[i]); drop(out[i]); drop(hist[0][i]); drop(hist[1][i]); }
        texW = texH = rtTexW = rtTexH = 0;
        historyValid = false;
    }

    bool Engine::WaitIdle(DWORD ms)
    {
        if (!fenceOut) return true;
        if (fenceOut->GetCompletedValue() >= valueOut) return true;
        fenceOut->SetEventOnCompletion(valueOut, idleEvent);
        return WaitForSingleObject(idleEvent, ms) == WAIT_OBJECT_0;
    }

    bool Engine::EnsureTextures(UINT w, UINT h, float scale)
    {
        UINT rw = std::max(1u, static_cast<UINT>(std::ceil(w * scale))), rh = std::max(1u, static_cast<UINT>(std::ceil(h * scale)));
        if (w == texW && h == texH && rw == rtTexW && rh == rtTexH && inDepth.res) return true;
        // Rare (the first frame, a new resolution or quality): the GPU's work finished first, since the descriptors and
        // textures it reads are replaced.
        if (!WaitIdle(1000)) { Fail("the GPU didn't finish its ray tracing within 1 s"); return false; }
        ReleaseTextures();
        for (auto& g : gameViews) { SafeRelease(g.srv); SafeRelease(g.rtv); SafeRelease(g.res); }
        gameViews.clear();
        const auto rt = D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET, uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        bool ok = CreateTexture(inDepth, w, h, DXGI_FORMAT_R32_FLOAT, true, rt) &&
                  CreateTexture(inMotion, w, h, DXGI_FORMAT_R16G16_FLOAT, true, rt) &&
                  CreateTexture(inHdr, w, h, DXGI_FORMAT_R11G11B10_FLOAT, true, rt);
        for (int i = 0; i < 3 && ok; ++i)
            ok = CreateTexture(raw[i], rw, rh, DXGI_FORMAT_R16G16B16A16_FLOAT, false, uav) &&
                 CreateTexture(hist[0][i], rw, rh, DXGI_FORMAT_R16G16B16A16_FLOAT, false, uav) &&
                 CreateTexture(hist[1][i], rw, rh, DXGI_FORMAT_R16G16B16A16_FLOAT, false, uav) &&
                 CreateTexture(out[i], rw, rh, DXGI_FORMAT_R16G16B16A16_FLOAT, true, D3D12_RESOURCE_FLAG_NONE);
        if (!ok) { ReleaseTextures(); Fail("no memory for the ray tracing's textures"); return false; }
        texW = w; texH = h; rtTexW = rw; rtTexH = rh; rtScale = scale;
        WriteDescriptors();
        Log("ray tracing: textures for %ux%u, traced at %ux%u", w, h, rw, rh);
        return true;
    }

    void Engine::WriteDescriptors()
    {
        D3D12_CPU_DESCRIPTOR_HANDLE base = heap->GetCPUDescriptorHandleForHeapStart();
        auto at = [&](UINT block, UINT i) { D3D12_CPU_DESCRIPTOR_HANDLE h = base; h.ptr += SIZE_T(block * BlockSize + i) * descSize; return h; };
        auto srv = [&](UINT block, UINT reg, ID3D12Resource* r, DXGI_FORMAT f) {
            D3D12_SHADER_RESOURCE_VIEW_DESC d{};
            d.Format = f; d.ViewDimension = D3D12_SRV_DIMENSION_TEXTURE2D; d.Texture2D.MipLevels = 1;
            d.Shader4ComponentMapping = D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING;
            dev->CreateShaderResourceView(r, &d, at(block, reg - 2));
        };
        auto uavTex = [&](UINT block, UINT reg, ID3D12Resource* r) {
            D3D12_UNORDERED_ACCESS_VIEW_DESC d{};
            d.Format = DXGI_FORMAT_R16G16B16A16_FLOAT; d.ViewDimension = D3D12_UAV_DIMENSION_TEXTURE2D;
            dev->CreateUnorderedAccessView(r, nullptr, &d, at(block, SrvCount + reg));
        };
        const DXGI_FORMAT F = DXGI_FORMAT_R16G16B16A16_FLOAT;
        for (UINT b = 0; b < Blocks; ++b)
        {
            for (UINT r = 2; r < 2 + SrvCount; ++r) srv(b, r, nullptr, F);
            for (UINT u = 0; u < UavCount; ++u) uavTex(b, u, nullptr);
            srv(b, 2, inDepth.res, DXGI_FORMAT_R32_FLOAT);
            srv(b, 3, inMotion.res, DXGI_FORMAT_R16G16_FLOAT);
        }
        srv(TraceBlock, 4, inHdr.res, DXGI_FORMAT_R11G11B10_FLOAT);
        for (int i = 0; i < 3; ++i) uavTex(TraceBlock, i, raw[i].res);
        {
            D3D12_UNORDERED_ACCESS_VIEW_DESC u{};
            u.Format = DXGI_FORMAT_R32_TYPELESS; u.ViewDimension = D3D12_UAV_DIMENSION_BUFFER;
            u.Buffer.NumElements = 4; u.Buffer.Flags = D3D12_BUFFER_UAV_FLAG_RAW;
            dev->CreateUnorderedAccessView(stats, nullptr, &u, at(TraceBlock, SrvCount + 3));
        }
        for (int p = 0; p < 2; ++p)
        {
            UINT tb = p ? Temporal1 : Temporal0, sb = p ? Spatial1 : Spatial0;
            for (int i = 0; i < 3; ++i)
            {
                srv(tb, 5 + i, raw[i].res, F);
                srv(tb, 8 + i, hist[1 - p][i].res, F);
                uavTex(tb, i, hist[p][i].res);
                srv(sb, 5 + i, hist[p][i].res, F);
                uavTex(sb, i, raw[i].res);   // the spatial pass writes into raw (free by then), copied into the shared out
            }
        }
    }

    void Engine::Transition(ID3D12GraphicsCommandList4* l, Texture& t, D3D12_RESOURCE_STATES to)
    {
        if (!t.res || t.state == to || t.tex11) return;   // shared textures are simultaneous-access: always COMMON
        D3D12_RESOURCE_BARRIER b{};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = t.res; b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = t.state; b.Transition.StateAfter = to;
        l->ResourceBarrier(1, &b);
        t.state = to;
    }

    D3D12_GPU_VIRTUAL_ADDRESS Engine::UploadAlloc(FrameContext& fc, std::uint64_t size, void** cpu)
    {
        std::uint64_t at = (fc.used + 255) & ~255ull;
        if (at + size > UploadPerFrame) return 0;
        fc.used = at + size;
        *cpu = fc.mapped + at;
        return fc.upload->GetGPUVirtualAddress() + at;
    }

    bool Engine::ArenaAlloc(std::uint64_t size, Blas& b)
    {
        size = (size + 255) & ~255ull;
        for (int pass = 0; pass < 2; ++pass)
        {
            for (int c = 0; c < static_cast<int>(chunks.size()); ++c)
                for (auto it = chunks[c].free.begin(); it != chunks[c].free.end(); ++it)
                    if (it->size >= size)
                    {
                        b.chunk = c; b.offset = it->offset; b.size = size;
                        b.address = chunks[c].buffer->GetGPUVirtualAddress() + it->offset;
                        it->offset += size; it->size -= size;
                        if (it->size == 0) chunks[c].free.erase(it);
                        arenaUsed += size;
                        return true;
                    }
            if (pass == 1) break;
            std::uint64_t want = std::max(ChunkSize, size);
            if (arenaSize + want > budget || memoryFull) return false;
            Chunk ch;
            ch.buffer = MakeBuffer(dev, want, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
            if (!ch.buffer) { memoryFull = true; Log("ray tracing: no more video memory for meshes (%llu MB in use)", static_cast<unsigned long long>(arenaSize >> 20)); return false; }
            ch.size = want;
            ch.free.push_back({ 0, want });
            chunks.push_back(std::move(ch));
            arenaSize += want;
        }
        return false;
    }

    void Engine::ArenaFree(const Blas& b)
    {
        if (b.chunk < 0 || b.chunk >= static_cast<int>(chunks.size())) return;
        auto& f = chunks[b.chunk].free;
        Range r{ b.offset, b.size };
        auto it = std::lower_bound(f.begin(), f.end(), r, [](const Range& x, const Range& y) { return x.offset < y.offset; });
        it = f.insert(it, r);
        if (it + 1 != f.end() && it->offset + it->size == (it + 1)->offset) { it->size += (it + 1)->size; f.erase(it + 1); }
        if (it != f.begin() && (it - 1)->offset + (it - 1)->size == it->offset) { (it - 1)->size += it->size; f.erase(it); }
        arenaUsed -= b.size;
    }

    void Engine::Defer(IUnknown* p) { if (p) deferred.push_back({ valueOut, p }); }

    bool Engine::Wants(std::uint64_t key) const
    {
        if (!running || blases.count(key) || failedKeys.count(key)) return false;
        for (auto& m : queued) if (m.key == key) return false;
        return true;
    }

    int Engine::Room() const { return std::max(0, MaxBuildsPerFrame - static_cast<int>(queued.size())); }

    void Engine::Queue(Mesh&& mesh)
    {
        if (!running || mesh.indices.empty() || mesh.positions.empty() || static_cast<int>(queued.size()) >= MaxBuildsPerFrame) return;
        queued.push_back(std::move(mesh));
    }

    ID3D11ShaderResourceView* Engine::GameView(ID3D11Resource* r)
    {
        for (auto& g : gameViews) if (g.res == r && g.srv) return g.srv;
        ID3D11Texture2D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) || !t) return nullptr;
        D3D11_TEXTURE2D_DESC d{};
        t->GetDesc(&d);
        t->Release();
        if (!(d.BindFlags & D3D11_BIND_SHADER_RESOURCE)) return nullptr;
        D3D11_SHADER_RESOURCE_VIEW_DESC v{};
        v.Format = ReadFormat(d.Format); v.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; v.Texture2D.MipLevels = 1;
        ID3D11ShaderResourceView* srv = nullptr;
        if (FAILED(d11->CreateShaderResourceView(r, &v, &srv))) return nullptr;
        r->AddRef();
        gameViews.push_back({ r, srv, nullptr });
        return srv;
    }

    ID3D11RenderTargetView* Engine::GameTarget(ID3D11Resource* r)
    {
        for (auto& g : gameViews) if (g.res == r && g.rtv) return g.rtv;
        ID3D11Texture2D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) || !t) return nullptr;
        D3D11_TEXTURE2D_DESC d{};
        t->GetDesc(&d);
        t->Release();
        if (!(d.BindFlags & D3D11_BIND_RENDER_TARGET)) return nullptr;
        D3D11_RENDER_TARGET_VIEW_DESC v{};
        v.Format = ReadFormat(d.Format); v.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        ID3D11RenderTargetView* rtv = nullptr;
        if (FAILED(d11->CreateRenderTargetView(r, &v, &rtv))) return nullptr;
        r->AddRef();
        gameViews.push_back({ r, nullptr, rtv });
        return rtv;
    }

    void Engine::ReadStats(FrameContext& fc)
    {
        if (!fc.pendingStats) return;
        fc.pendingStats = false;
        void* p = nullptr;
        D3D12_RANGE r{ 0, 64 };
        if (FAILED(fc.readback->Map(0, &r, &p)) || !p) return;
        const std::uint32_t* s = static_cast<const std::uint32_t*>(p);
        const std::uint64_t* ts = reinterpret_cast<const std::uint64_t*>(static_cast<const std::uint8_t*>(p) + 32);
        statSamples += s[0]; statAgree += s[1];
        if (timestampFrequency && ts[1] > ts[0]) gpuMs = gpuMs * 0.9 + 0.1 * (double(ts[1] - ts[0]) * 1000.0 / double(timestampFrequency));
        D3D12_RANGE none{ 0, 0 };
        fc.readback->Unmap(0, &none);
        // Every 60 traced frames: the line-up check's share. Low three windows running: the results don't match the
        // picture (the camera or the world read wrongly), so they aren't put into it.
        if (++statFrames >= 60)
        {
            lineUpSamples = statSamples;
            lineUp = statSamples ? double(statAgree) / statSamples : -1;
            if (statSamples >= 200)
            {
                if (lineUp < 0.25) { if (++lowWindows >= 3 && !paused) { paused = true; Log("ray tracing: the traced world doesn't line up with the picture (%.0f%% of %u camera rays agree): not shown", lineUp * 100, statSamples); } }
                else { lowWindows = 0; if (paused && lineUp > 0.4) { paused = false; Log("ray tracing: lined up again (%.0f%%): shown", lineUp * 100); } }
            }
            statSamples = statAgree = statFrames = 0;
        }
    }

    bool Engine::Render(ID3D11DeviceContext* ctx, const FrameInputs& in, const std::vector<Instance>& instances,
                        const std::vector<RtCapsule>& capsules, const Options& o)
    {
        if (!running || !ctx || !in.depth || !in.motion || !in.hdr || !in.drawnWidth || !in.drawnHeight) return false;
        budget = o.memoryBudget;
        FrameContext& fc = contexts[frameNo % Contexts];
        std::uint64_t done = fenceOut->GetCompletedValue();
        if (done == UINT64_MAX) { Fail("the GPU removed the ray tracing's device"); return false; }
        if (done < fc.fence) { ++framesSkipped; historyValid = false; queued.clear(); return false; }   // the GPU is behind: no waiting
        ReadStats(fc);

        D3D11_TEXTURE2D_DESC dd{};
        {
            ID3D11Texture2D* t = nullptr;
            if (FAILED(in.depth->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) || !t) return false;
            t->GetDesc(&dd);
            t->Release();
        }
        if (in.drawnWidth > dd.Width || in.drawnHeight > dd.Height) return false;
        static const float Scale[] = { 0.5f, 0.5f, 1.0f, 1.0f };
        static const UINT AoRays[] = { 1, 2, 2, 4 }, BounceRays[] = { 1, 2, 2, 3 };
        int quality = std::clamp(o.quality, 0, 3);
        if (!EnsureTextures(dd.Width, dd.Height, Scale[quality])) return false;
        if (gameViews.size() >= 16)   // the game made new textures: the old ones' views let go
        {
            for (auto& g : gameViews) { SafeRelease(g.srv); SafeRelease(g.rtv); SafeRelease(g.res); }
            gameViews.clear();
        }
        ID3D11ShaderResourceView* depthView = GameView(in.depth), * motionView = GameView(in.motion), * hdrView = GameView(in.hdr);
        ID3D11RenderTargetView* hdrTarget = GameTarget(in.hdr);
        if (!depthView || !motionView || !hdrView || !hdrTarget) { Fail("the game's textures can't be read or drawn into"); return false; }
        UINT rtW = std::max(1u, static_cast<UINT>(in.drawnWidth * rtScale)), rtH = std::max(1u, static_cast<UINT>(in.drawnHeight * rtScale));
        rtW = std::min(rtW, rtTexW); rtH = std::min(rtH, rtTexH);

        ID3D11DeviceContext1* c1 = nullptr;
        ID3D11DeviceContext4* c4 = nullptr;
        if (FAILED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&c1))) ||
            FAILED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext4), reinterpret_cast<void**>(&c4))))
        { SafeRelease(c1); Fail("the game's context has no shared fences"); return false; }
        D3D11_VIEWPORT vp{ 0, 0, float(in.drawnWidth), float(in.drawnHeight), 0, 1 };

        // 1. Direct3D 11: the inputs into the shared textures, then a signal.
        {
            ID3DDeviceContextState* game = nullptr;
            c1->SwapDeviceContextState(state11, &game);
            ID3D11RenderTargetView* targets[3] = { inDepth.rtv, inMotion.rtv, inHdr.rtv };
            ctx->OMSetRenderTargets(3, targets, nullptr);
            ctx->RSSetViewports(1, &vp);
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->VSSetShader(vs, nullptr, 0);
            ctx->PSSetShader(preparePs, nullptr, 0);
            ID3D11ShaderResourceView* srvs[3] = { depthView, motionView, hdrView };
            ctx->PSSetShaderResources(0, 3, srvs);
            ctx->Draw(3, 0);
            ID3D11ShaderResourceView* none[3]{};
            ctx->PSSetShaderResources(0, 3, none);
            ctx->OMSetRenderTargets(0, nullptr, nullptr);
            c1->SwapDeviceContextState(game, nullptr);
            if (game) game->Release();
        }
        c4->Signal(fenceIn11, ++valueIn);
        ctx->Flush();

        // 2. Direct3D 12, list A: new meshes' BLASes, the capsules', the TLAS. Bounded.
        HRESULT hr = fc.allocator->Reset();
        if (SUCCEEDED(hr)) hr = fc.listA->Reset(fc.allocator, nullptr);
        if (FAILED(hr)) { SafeRelease(c1); SafeRelease(c4); Fail("couldn't record commands"); return false; }
        fc.used = 0;
        builtThisFrame = 0; trianglesThisFrame = 0;
        std::uint64_t scratchUsed = 0;
        auto scratchAlloc = [&](std::uint64_t size, D3D12_GPU_VIRTUAL_ADDRESS& at) {
            size = (size + 255) & ~255ull;
            if (scratchUsed + size > scratchSize)
            {
                if (scratchUsed > 0 || size > ScratchMax) return false;   // grown only between frames' builds
                Defer(scratch);
                scratchSize = std::min(ScratchMax, std::max<std::uint64_t>(size, 16ull << 20));
                scratch = MakeBuffer(dev, scratchSize, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COMMON);
                if (!scratch) { scratchSize = 0; return false; }
            }
            at = scratch->GetGPUVirtualAddress() + scratchUsed;
            scratchUsed += size;
            return true;
        };
        for (auto& m : queued)
        {
            std::uint64_t tris = m.indices.size() / 3, verts = m.positions.size() / 3;
            if (builtThisFrame >= MaxBuildsPerFrame || trianglesThisFrame + tris > MaxTrianglesPerFrame) break;
            D3D12_RAYTRACING_GEOMETRY_DESC g{};
            g.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES;
            g.Flags = m.alphaTested ? D3D12_RAYTRACING_GEOMETRY_FLAG_NO_DUPLICATE_ANYHIT_INVOCATION : D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE;
            g.Triangles.VertexFormat = DXGI_FORMAT_R32G32B32_FLOAT; g.Triangles.VertexCount = static_cast<UINT>(verts);
            g.Triangles.VertexBuffer.StrideInBytes = 12;
            g.Triangles.IndexFormat = DXGI_FORMAT_R16_UINT; g.Triangles.IndexCount = static_cast<UINT>(m.indices.size());
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS bi{};
            bi.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
            bi.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE;
            bi.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY; bi.NumDescs = 1; bi.pGeometryDescs = &g;
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO pre{};
            dev->GetRaytracingAccelerationStructurePrebuildInfo(&bi, &pre);
            void* vcpu = nullptr, * icpu = nullptr;
            D3D12_GPU_VIRTUAL_ADDRESS va = UploadAlloc(fc, m.positions.size() * 4, &vcpu);
            D3D12_GPU_VIRTUAL_ADDRESS ia = va ? UploadAlloc(fc, m.indices.size() * 2, &icpu) : 0;
            D3D12_GPU_VIRTUAL_ADDRESS sa = 0;
            if (!va || !ia || !scratchAlloc(pre.ScratchDataSizeInBytes, sa)) break;   // the rest next frame
            Blas b;
            if (!ArenaAlloc(pre.ResultDataMaxSizeInBytes, b)) { failedKeys.insert(m.key); continue; }
            std::memcpy(vcpu, m.positions.data(), m.positions.size() * 4);
            std::memcpy(icpu, m.indices.data(), m.indices.size() * 2);
            g.Triangles.VertexBuffer.StartAddress = va;
            g.Triangles.IndexBuffer = ia;
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC bd{};
            bd.Inputs = bi; bd.DestAccelerationStructureData = b.address; bd.ScratchAccelerationStructureData = sa;
            fc.listA->BuildRaytracingAccelerationStructure(&bd, 0, nullptr);
            b.alpha = m.alphaTested;
            b.lastUsed = frameNo;
            blases[m.key] = b;
            ++builtThisFrame; trianglesThisFrame += tris;
        }
        queued.clear();

        // Capsules: one BLAS of their boxes, rebuilt each frame.
        UINT capCount = static_cast<UINT>(std::min<size_t>(capsules.size(), MaxCapsules));
        D3D12_GPU_VIRTUAL_ADDRESS capData = fc.upload->GetGPUVirtualAddress();
        bool capsOk = false;
        if (capCount)
        {
            void* aabbCpu = nullptr, * dataCpu = nullptr;
            D3D12_GPU_VIRTUAL_ADDRESS aabbs = UploadAlloc(fc, capCount * sizeof(D3D12_RAYTRACING_AABB), &aabbCpu);
            D3D12_GPU_VIRTUAL_ADDRESS data = aabbs ? UploadAlloc(fc, capCount * sizeof(RtCapsule), &dataCpu) : 0;
            if (data)
            {
                auto* box = static_cast<D3D12_RAYTRACING_AABB*>(aabbCpu);
                for (UINT i = 0; i < capCount; ++i)
                {
                    const RtCapsule& c = capsules[i];
                    box[i] = { std::min(c.a[0], c.b[0]) - c.radius, std::min(c.a[1], c.b[1]) - c.radius, std::min(c.a[2], c.b[2]) - c.radius,
                               std::max(c.a[0], c.b[0]) + c.radius, std::max(c.a[1], c.b[1]) + c.radius, std::max(c.a[2], c.b[2]) + c.radius };
                }
                std::memcpy(dataCpu, capsules.data(), capCount * sizeof(RtCapsule));
                D3D12_RAYTRACING_GEOMETRY_DESC g{};
                g.Type = D3D12_RAYTRACING_GEOMETRY_TYPE_PROCEDURAL_PRIMITIVE_AABBS;
                g.AABBs.AABBCount = capCount; g.AABBs.AABBs.StartAddress = aabbs; g.AABBs.AABBs.StrideInBytes = sizeof(D3D12_RAYTRACING_AABB);
                D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS bi{};
                bi.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL;
                bi.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
                bi.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY; bi.NumDescs = 1; bi.pGeometryDescs = &g;
                if (!capsuleBlas)
                {
                    D3D12_RAYTRACING_GEOMETRY_DESC gm = g; gm.AABBs.AABBCount = MaxCapsules;
                    D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS bm = bi; bm.pGeometryDescs = &gm;
                    D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO pre{};
                    dev->GetRaytracingAccelerationStructurePrebuildInfo(&bm, &pre);
                    capsuleBlas = MakeBuffer(dev, pre.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
                    capsuleScratch = pre.ScratchDataSizeInBytes;
                }
                D3D12_GPU_VIRTUAL_ADDRESS sa = 0;
                if (capsuleBlas && scratchAlloc(capsuleScratch, sa))
                {
                    D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC bd{};
                    bd.Inputs = bi; bd.DestAccelerationStructureData = capsuleBlas->GetGPUVirtualAddress(); bd.ScratchAccelerationStructureData = sa;
                    fc.listA->BuildRaytracingAccelerationStructure(&bd, 0, nullptr);
                    capData = data;
                    capsOk = true;
                }
            }
        }
        if (!capsOk) capCount = 0;
        capsulesThisFrame = static_cast<int>(capCount);
        if (builtThisFrame || capsOk) UavBarrier(fc.listA, nullptr);   // the BLASes done before the TLAS reads them

        // The TLAS.
        UINT n = 0;
        void* instCpu = nullptr;
        UINT want = std::min<UINT>(static_cast<UINT>(instances.size()), MaxInstances) + 1;
        D3D12_GPU_VIRTUAL_ADDRESS instGpu = UploadAlloc(fc, std::uint64_t(want) * sizeof(D3D12_RAYTRACING_INSTANCE_DESC), &instCpu);
        if (instGpu)
        {
            auto* d = static_cast<D3D12_RAYTRACING_INSTANCE_DESC*>(instCpu);
            for (const Instance& i : instances)
            {
                if (n + 1 >= want) break;
                auto it = blases.find(i.key);
                if (it == blases.end()) continue;
                D3D12_RAYTRACING_INSTANCE_DESC x{};
                std::memcpy(x.Transform, i.transform, sizeof(x.Transform));
                x.InstanceMask = 0x01;
                x.InstanceID = n;
                x.AccelerationStructure = it->second.address;
                it->second.lastUsed = frameNo;
                d[n++] = x;
            }
            if (capsOk)
            {
                D3D12_RAYTRACING_INSTANCE_DESC x{};
                x.Transform[0][0] = x.Transform[1][1] = x.Transform[2][2] = 1.0f;
                x.InstanceMask = 0x02;
                x.AccelerationStructure = capsuleBlas->GetGPUVirtualAddress();
                d[n++] = x;
            }
        }
        instancesThisFrame = static_cast<int>(n);
        if (!tlas || n > tlasCapacity)
        {
            UINT cap = std::max<UINT>(1024, n + n / 2);
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS ti{};
            ti.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
            ti.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
            ti.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY; ti.NumDescs = cap;
            D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO pre{};
            dev->GetRaytracingAccelerationStructurePrebuildInfo(&ti, &pre);
            Defer(tlas);
            tlas = MakeBuffer(dev, pre.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE);
            tlasCapacity = tlas ? cap : 0;
            tlasScratchOffset = pre.ScratchDataSizeInBytes;
        }
        D3D12_GPU_VIRTUAL_ADDRESS tsa = 0;
        if (!tlas || !scratchAlloc(tlasScratchOffset, tsa))
        {
            // No TLAS this frame: the BLASes recorded are still built (later frames use them), nothing is traced.
            SafeRelease(c1); SafeRelease(c4);
            ++framesSkipped; historyValid = false;
            if (FAILED(fc.listA->Close())) { Fail("couldn't record commands"); return false; }
            ID3D12CommandList* la[] = { fc.listA };
            queue->ExecuteCommandLists(1, la);
            if (FAILED(queue->Signal(fenceOut, ++valueOut))) { Fail("the GPU didn't take the ray tracing's work"); return false; }
            fc.fence = valueOut;
            fc.submitted = Now();
            ++frameNo;
            return false;
        }
        {
            D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC bd{};
            bd.Inputs.Type = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL;
            bd.Inputs.Flags = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD;
            bd.Inputs.DescsLayout = D3D12_ELEMENTS_LAYOUT_ARRAY; bd.Inputs.NumDescs = n;
            bd.Inputs.InstanceDescs = n ? instGpu : 0;
            bd.DestAccelerationStructureData = tlas->GetGPUVirtualAddress();
            bd.ScratchAccelerationStructureData = tsa;
            fc.listA->BuildRaytracingAccelerationStructure(&bd, 0, nullptr);
            UavBarrier(fc.listA, tlas);
        }
        hr = fc.listA->Close();

        // 3. List B: trace, temporal, spatial; statistics; the shared textures handed back.
        RtConstants k{};
        const Camera& cam = in.camera;
        k.forward = { cam.forward[0], cam.forward[1], cam.forward[2], 0 };
        k.right = { cam.right[0], cam.right[1], cam.right[2], 0 };
        k.up = { cam.up[0], cam.up[1], cam.up[2], 0 };
        k.frustum = { cam.frustum[0], cam.frustum[1], cam.frustum[2], cam.frustum[3] };
        k.depth = { cam.nearZ, cam.farZ, cam.nearZ * cam.farZ, cam.farZ - cam.nearZ };
        k.sun = { in.sun[0], in.sun[1], in.sun[2], in.sunOn ? 1.0f : 0.0f };
        k.dist = { o.occlusionRadius, o.bounceDistance, o.reflectionDistance, o.shadowDistance };
        k.strength = { o.shadows, o.occlusion, o.bounce, o.reflections };
        k.size = { in.drawnWidth, in.drawnHeight, rtW, rtH };
        k.tex = { float(texW), float(texH), 1.0f / texW, 1.0f / texH };
        k.rtTex = { float(rtTexW), float(rtTexH), 1.0f / rtTexW, 1.0f / rtTexH };
        k.frame = { frameNo, historyValid ? 1u : 0u, AoRays[quality], BounceRays[quality] };
        k.flags = { capCount, 8u, 16u, 0u };
        k.prevScale = { prevScale[0], prevScale[1], 0, 0 };
        void* cbCpu = nullptr;
        D3D12_GPU_VIRTUAL_ADDRESS cb = UploadAlloc(fc, sizeof(k), &cbCpu);
        if (SUCCEEDED(hr) && cb) { std::memcpy(cbCpu, &k, sizeof(k)); hr = fc.listB->Reset(fc.allocator, traceState); }
        else hr = E_OUTOFMEMORY;
        if (FAILED(hr)) { SafeRelease(c1); SafeRelease(c4); Fail("couldn't record commands"); return false; }
        auto* l = fc.listB;
        l->SetComputeRootSignature(root);
        l->SetDescriptorHeaps(1, &heap);
        l->SetComputeRootConstantBufferView(0, cb);
        l->SetComputeRootShaderResourceView(1, tlas->GetGPUVirtualAddress());
        l->SetComputeRootShaderResourceView(2, capData);
        auto table = [&](UINT block) {
            D3D12_GPU_DESCRIPTOR_HANDLE g = heap->GetGPUDescriptorHandleForHeapStart();
            g.ptr += UINT64(block * BlockSize) * descSize;
            l->SetComputeRootDescriptorTable(3, g);
            g.ptr += UINT64(SrvCount) * descSize;
            l->SetComputeRootDescriptorTable(4, g);
        };
        const auto SRV = D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE, UAV = D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
        int slot = frameNo % Contexts;
        if (timestamps) l->EndQuery(timestamps, D3D12_QUERY_TYPE_TIMESTAMP, slot * 2);
        for (int i = 0; i < 3; ++i) Transition(l, raw[i], UAV);
        {
            const UINT zero[4]{};
            D3D12_GPU_DESCRIPTOR_HANDLE g = heap->GetGPUDescriptorHandleForHeapStart();
            g.ptr += UINT64(TraceBlock * BlockSize + SrvCount + 3) * descSize;
            l->ClearUnorderedAccessViewUint(g, cpuHeap->GetCPUDescriptorHandleForHeapStart(), stats, zero, 0, nullptr);
            UavBarrier(l, stats);
        }
        UINT gx = (rtW + 7) / 8, gy = (rtH + 7) / 8;
        table(TraceBlock);
        l->Dispatch(gx, gy, 1);
        int p = parity;
        for (int i = 0; i < 3; ++i) { Transition(l, raw[i], SRV); Transition(l, hist[1 - p][i], SRV); Transition(l, hist[p][i], UAV); }
        l->SetPipelineState(temporalState);
        table(p ? Temporal1 : Temporal0);
        l->Dispatch(gx, gy, 1);
        for (int i = 0; i < 3; ++i) { Transition(l, hist[p][i], SRV); Transition(l, raw[i], UAV); }
        l->SetPipelineState(spatialState);
        table(p ? Spatial1 : Spatial0);
        l->Dispatch(gx, gy, 1);
        for (int i = 0; i < 3; ++i) { Transition(l, raw[i], D3D12_RESOURCE_STATE_COPY_SOURCE); l->CopyResource(out[i].res, raw[i].res); }
        if (timestamps)
        {
            l->EndQuery(timestamps, D3D12_QUERY_TYPE_TIMESTAMP, slot * 2 + 1);
            l->ResolveQueryData(timestamps, D3D12_QUERY_TYPE_TIMESTAMP, slot * 2, 2, fc.readback, 32);
        }
        {
            D3D12_RESOURCE_BARRIER b{};
            b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION; b.Transition.pResource = stats;
            b.Transition.StateBefore = UAV; b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
            l->ResourceBarrier(1, &b);
            l->CopyBufferRegion(fc.readback, 0, stats, 0, 16);   // a buffer: back to COMMON by itself when the list ends
        }
        hr = l->Close();
        if (FAILED(hr)) { SafeRelease(c1); SafeRelease(c4); Fail("couldn't record commands"); return false; }

        ID3D12CommandList* la[] = { fc.listA };
        ID3D12CommandList* lb[] = { fc.listB };
        queue->ExecuteCommandLists(1, la);
        queue->Wait(fenceIn, valueIn);
        queue->ExecuteCommandLists(1, lb);
        hr = queue->Signal(fenceOut, ++valueOut);
        if (FAILED(hr)) { SafeRelease(c1); SafeRelease(c4); Fail("the GPU didn't take the ray tracing's work"); return false; }
        fc.fence = valueOut;
        fc.pendingStats = true;
        fc.submitted = Now();
        for (auto& [key, b] : blases) if (b.lastUsed == frameNo) b.lastFence = valueOut;

        // 4. Direct3D 11 waits on the GPU (not the CPU) for that work, then puts the results into the picture.
        c4->Wait(fenceOut11, valueOut);
        if (!paused || o.debugView != 0)
        {
            CompositeConstants q{};
            q.strength = { o.shadows, o.occlusion, o.bounce, o.reflections };
            q.drawn = { float(in.drawnWidth), float(in.drawnHeight), 1.0f / in.drawnWidth, 1.0f / in.drawnHeight };
            q.rt = { float(rtW), float(rtH), 1.0f / rtTexW, 1.0f / rtTexH };
            q.depth = k.depth;
            q.flags = { static_cast<std::uint32_t>(std::clamp(o.debugView, 0, 5)), in.sunOn ? 1u : 0u, 0, 0 };
            q.forward = k.forward; q.right = k.right; q.up = k.up; q.frustum = k.frustum;
            D3D11_MAPPED_SUBRESOURCE m{};
            ID3DDeviceContextState* game = nullptr;
            c1->SwapDeviceContextState(state11, &game);
            if (SUCCEEDED(ctx->Map(compositeCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) { std::memcpy(m.pData, &q, sizeof(q)); ctx->Unmap(compositeCb, 0); }
            ctx->OMSetRenderTargets(1, &hdrTarget, nullptr);
            ctx->RSSetViewports(1, &vp);
            ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            ctx->VSSetShader(vs, nullptr, 0);
            ctx->PSSetShader(o.debugView == 5 ? normalsPs : compositePs, nullptr, 0);
            ctx->PSSetConstantBuffers(0, 1, &compositeCb);
            ID3D11ShaderResourceView* srvs[5] = { inHdr.srv, out[0].srv, out[1].srv, out[2].srv, inDepth.srv };
            ctx->PSSetShaderResources(0, 5, srvs);
            ctx->Draw(3, 0);
            ID3D11ShaderResourceView* none[5]{};
            ctx->PSSetShaderResources(0, 5, none);
            ctx->OMSetRenderTargets(0, nullptr, nullptr);
            c1->SwapDeviceContextState(game, nullptr);
            if (game) game->Release();
        }
        SafeRelease(c1); SafeRelease(c4);
        parity ^= 1;
        historyValid = true;
        prevScale[0] = float(rtW) / rtTexW; prevScale[1] = float(rtH) / rtTexH;
        ++frameNo; ++framesTraced;
        return !paused || o.debugView != 0;
    }

    void Engine::EndFrame()
    {
        if (!dev) return;
        if (running)
        {
            HRESULT r = dev->GetDeviceRemovedReason();
            if (r != S_OK) { char b[96]; std::snprintf(b, sizeof(b), "the Direct3D 12 device was removed (0x%08X)", static_cast<unsigned>(r)); Fail(b); }
        }
        std::uint64_t done = fenceOut ? fenceOut->GetCompletedValue() : 0;
        if (running && done != UINT64_MAX)
        {
            // The oldest unfinished frame's age.
            for (auto& fc : contexts)
                if (fc.fence > done && Now() - fc.submitted > WatchdogSeconds) { Fail("the GPU took over 2 s on one frame's ray tracing"); break; }
        }
        // What the GPU is done with is freed.
        for (auto it = deferred.begin(); it != deferred.end();)
            if (done >= it->first) { it->second->Release(); it = deferred.erase(it); }
            else ++it;
        // Meshes unused for 600 frames (10 s at 60 fps).
        if (running && frameNo % 60 == 0)
        {
            for (auto it = blases.begin(); it != blases.end();)
                if (frameNo - it->second.lastUsed > 600 && done >= it->second.lastFence) { ArenaFree(it->second); it = blases.erase(it); }
                else ++it;
            if (frameNo % 1800 == 0) failedKeys.clear();
            if (adapter && frameNo % 300 == 0)
            {
                DXGI_QUERY_VIDEO_MEMORY_INFO mi{};
                if (SUCCEEDED(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &mi)))
                {
                    bool full = mi.CurrentUsage > mi.Budget / 20 * 19;
                    if (full != memoryFull) Log("ray tracing: video memory %s (%llu of %llu MB used)", full ? "nearly full: no new meshes" : "free again",
                                                static_cast<unsigned long long>(mi.CurrentUsage >> 20), static_cast<unsigned long long>(mi.Budget >> 20));
                    memoryFull = full;
                }
            }
        }
    }

    std::string Engine::Report() const
    {
        char b[320];
        std::snprintf(b, sizeof(b), "%d instances, %zu meshes (%.0f MB), %d built, %d capsules, GPU %.2f ms, line-up %s%.0f%% (%u rays), %u traced, %u skipped",
                      instancesThisFrame, blases.size(), arenaUsed / 1048576.0, builtThisFrame, capsulesThisFrame, gpuMs,
                      paused ? "LOW " : "", lineUp < 0 ? 0.0 : lineUp * 100.0, lineUpSamples, framesTraced, framesSkipped);
        return b;
    }

    void Engine::Stop()
    {
        if (dev) WaitIdle(2000);
        running = false;
        for (auto& [p, u] : deferred) u->Release();
        deferred.clear();
        ReleaseTextures();
        for (auto& g : gameViews) { SafeRelease(g.srv); SafeRelease(g.rtv); SafeRelease(g.res); }
        gameViews.clear();
        for (auto& c : chunks) SafeRelease(c.buffer);
        chunks.clear(); blases.clear(); arenaUsed = arenaSize = 0;
        for (auto& fc : contexts) { SafeRelease(fc.listA); SafeRelease(fc.listB); SafeRelease(fc.allocator); SafeRelease(fc.upload); SafeRelease(fc.readback); fc = {}; }
        SafeRelease(scratch); SafeRelease(tlas); SafeRelease(capsuleBlas); SafeRelease(stats);
        SafeRelease(traceState); SafeRelease(temporalState); SafeRelease(spatialState); SafeRelease(root);
        SafeRelease(heap); SafeRelease(cpuHeap); SafeRelease(timestamps);
        SafeRelease(fenceIn11); SafeRelease(fenceOut11); SafeRelease(fenceIn); SafeRelease(fenceOut);
        SafeRelease(queue); SafeRelease(dev); SafeRelease(adapter);
        SafeRelease(state11); SafeRelease(vs); SafeRelease(preparePs); SafeRelease(compositePs); SafeRelease(normalsPs); SafeRelease(compositeCb);
        SafeRelease(d11_5);
        if (idleEvent) { CloseHandle(idleEvent); idleEvent = nullptr; }
    }
}
