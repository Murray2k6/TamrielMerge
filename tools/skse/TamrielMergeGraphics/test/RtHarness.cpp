// The ray tracing's test harness: the same Direct3D 11 + Direct3D 12 pair the plugin makes in the game, with a small
// scene of its own (a ground, two boxes and a capsule standing in for a person) drawn the way Skyrim's renderer leaves
// its buffers (an R11G11B10 lit picture, R16G16 motion vectors, an R24G8 depth buffer), then RtCore's ray tracing and
// composite, checked by reading the results back. Runs with the Direct3D 11 debug device and the Direct3D 12 debug
// layer with GPU-based validation, and fails on any message from them. A few seconds of light GPU work.
//
//   RtHarness.exe [--no-debug] [--out DIR]     (exit code 0: every check passed)
#include "../RtCore.h"
#include "../gen/FullscreenVS.h"
#include "../gen/SharpenPS.h"
#include <d3dcompiler.h>
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace
{
    int failures = 0;
    void Check(bool ok, const char* what, double value)
    {
        std::printf("  [%s] %s (%.4f)\n", ok ? "ok" : "FAIL", what, value);
        if (!ok) ++failures;
    }
    void LogLine(const char* s) { std::printf("    log: %s\n", s); }

    struct V3 { float x, y, z; };
    V3 operator+(V3 a, V3 b) { return { a.x + b.x, a.y + b.y, a.z + b.z }; }
    V3 operator-(V3 a, V3 b) { return { a.x - b.x, a.y - b.y, a.z - b.z }; }
    V3 operator*(V3 a, float s) { return { a.x * s, a.y * s, a.z * s }; }
    float Dot(V3 a, V3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
    V3 Cross(V3 a, V3 b) { return { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; }
    V3 Norm(V3 a) { float l = std::sqrt(Dot(a, a)); return a * (1.0f / l); }

    const UINT TexW = 1280, TexH = 720;
    const float Near = 15.0f, Far = 100000.0f;
    V3 camPos{ 0, -300, 220 }, fwd, right, up;
    float frus[4];
    V3 sun = Norm({ -0.4f, -0.5f, 0.75f });

    struct Vertex { V3 p, n, c; };
    struct Object { std::vector<float> local; std::vector<std::uint16_t> idx; V3 scale, pos, color; bool alpha = false; };

    Object Box(V3 size, V3 centre, V3 color)
    {
        Object o;
        o.scale = size; o.pos = centre; o.color = color;
        const float c[8][3] = { {-.5f,-.5f,-.5f},{.5f,-.5f,-.5f},{.5f,.5f,-.5f},{-.5f,.5f,-.5f},{-.5f,-.5f,.5f},{.5f,-.5f,.5f},{.5f,.5f,.5f},{-.5f,.5f,.5f} };
        for (auto& v : c) o.local.insert(o.local.end(), { v[0], v[1], v[2] });
        const std::uint16_t f[36] = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 1,2,6, 1,6,5, 2,3,7, 2,7,6, 3,0,4, 3,4,7 };
        o.idx.assign(f, f + 36);
        return o;
    }

    // The scene drawn the way the game leaves its buffers.
    const char* SceneHlsl = R"(
cbuffer K : register(b0) { float4 camPos; float4 fwd; float4 right; float4 up; float4 frus; float4 depth; float4 sun; };
struct VI { float3 p : POSITION; float3 n : NORMAL; float3 c : COLOR; };
struct VO { float4 pos : SV_Position; float3 n : NORMAL; float3 c : COLOR; };
VO VS(VI i) {
    VO o; float3 d = i.p - camPos.xyz;
    float x = dot(d, right.xyz), y = dot(d, up.xyz), z = dot(d, fwd.xyz);
    float n = depth.x, f = depth.y;
    o.pos = float4((2 * x - (frus.y + frus.x) * z) / (frus.y - frus.x), (2 * y - (frus.z + frus.w) * z) / (frus.z - frus.w),
                   f / (f - n) * z - f * n / (f - n), z);
    o.n = i.n; o.c = i.c; return o; }
struct PO { float4 hdr : SV_Target0; float2 mv : SV_Target1; };
PO PS(VO i) { PO o; float l = 0.25 + 1.2 * saturate(dot(normalize(i.n), sun.xyz)); o.hdr = float4(i.c * l, 1); o.mv = 0; return o; }
)";

    template <class T> void Release(T*& p) { if (p) { p->Release(); p = nullptr; } }

    bool Project(V3 p, UINT dw, UINT dh, int& px, int& py)
    {
        V3 d = p - camPos;
        float z = Dot(d, fwd);
        if (z < Near) return false;
        float x = Dot(d, right) / z, y = Dot(d, up) / z;
        float u = (x - frus[0]) / (frus[1] - frus[0]), v = (frus[2] - y) / (frus[2] - frus[3]);
        px = int(u * dw); py = int(v * dh);
        return px >= 0 && py >= 0 && px < int(dw) && py < int(dh);
    }

    struct Readback { std::vector<std::uint8_t> data; UINT pitch = 0, w = 0, h = 0; DXGI_FORMAT f{}; };

    bool Read(ID3D11Device* dev, ID3D11DeviceContext* ctx, ID3D11Texture2D* tex, Readback& r)
    {
        D3D11_TEXTURE2D_DESC d{};
        tex->GetDesc(&d);
        d.Usage = D3D11_USAGE_STAGING; d.BindFlags = 0; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ; d.MiscFlags = 0;
        ID3D11Texture2D* s = nullptr;
        if (FAILED(dev->CreateTexture2D(&d, nullptr, &s))) return false;
        ctx->CopyResource(s, tex);
        D3D11_MAPPED_SUBRESOURCE m{};
        if (FAILED(ctx->Map(s, 0, D3D11_MAP_READ, 0, &m))) { s->Release(); return false; }
        r.pitch = m.RowPitch; r.w = d.Width; r.h = d.Height; r.f = d.Format;
        r.data.assign(static_cast<std::uint8_t*>(m.pData), static_cast<std::uint8_t*>(m.pData) + size_t(m.RowPitch) * d.Height);
        ctx->Unmap(s, 0);
        s->Release();
        return true;
    }

    float Half(std::uint16_t h)
    {
        std::uint32_t s = (h & 0x8000u) << 16, e = (h >> 10) & 31, m = h & 1023, b;
        if (e == 0) b = s; else if (e == 31) b = s | 0x7F800000u | (m << 13); else b = s | ((e + 112) << 23) | (m << 13);
        float f; std::memcpy(&f, &b, 4); return f;
    }
    float Rgba16(const Readback& r, int x, int y, int c) { return Half(reinterpret_cast<const std::uint16_t*>(r.data.data() + size_t(y) * r.pitch)[x * 4 + c]); }
    // R11G11B10 float: 6-bit mantissa / 5-bit exponent channels.
    float Small(std::uint32_t bits, int mant) { std::uint32_t e = bits >> mant, m = bits & ((1u << mant) - 1); if (e == 0) return std::ldexp(float(m), -14 - mant); return std::ldexp(1.0f + float(m) / (1u << mant), int(e) - 15); }
    V3 Rgb111110(const Readback& r, int x, int y)
    {
        std::uint32_t v = reinterpret_cast<const std::uint32_t*>(r.data.data() + size_t(y) * r.pitch)[x];
        return { Small(v & 0x7FF, 6), Small((v >> 11) & 0x7FF, 6), Small(v >> 22, 5) };
    }

    void WriteBmp(const std::string& path, UINT w, UINT h, auto pixel)
    {
        FILE* f = std::fopen(path.c_str(), "wb");
        if (!f) return;
        UINT row = (w * 3 + 3) & ~3u, size = 54 + row * h;
        std::uint8_t hd[54]{ 'B', 'M' };
        std::memcpy(hd + 2, &size, 4); hd[10] = 54; hd[14] = 40;
        std::memcpy(hd + 18, &w, 4); std::int32_t nh = -int(h); std::memcpy(hd + 22, &nh, 4);
        hd[26] = 1; hd[28] = 24;
        std::fwrite(hd, 1, 54, f);
        std::vector<std::uint8_t> line(row);
        for (UINT y = 0; y < h; ++y)
        {
            for (UINT x = 0; x < w; ++x)
            {
                V3 c = pixel(int(x), int(y));
                auto q = [](float v) { v = v / (1 + v); return std::uint8_t(std::clamp(std::pow(v, 1 / 2.2f), 0.0f, 1.0f) * 255); };
                line[x * 3] = q(c.z); line[x * 3 + 1] = q(c.y); line[x * 3 + 2] = q(c.x);
            }
            std::fwrite(line.data(), 1, row, f);
        }
        std::fclose(f);
    }

    bool WaitGpu(ID3D11Device* dev, ID3D11DeviceContext* ctx, DWORD ms)
    {
        D3D11_QUERY_DESC qd{ D3D11_QUERY_EVENT, 0 };
        ID3D11Query* q = nullptr;
        if (FAILED(dev->CreateQuery(&qd, &q))) return false;
        ctx->End(q);
        ctx->Flush();
        BOOL done = FALSE;
        ULONGLONG until = GetTickCount64() + ms;
        while (ctx->GetData(q, &done, sizeof(done), 0) != S_OK && GetTickCount64() < until) Sleep(1);
        q->Release();
        return done == TRUE;
    }

    int DebugMessages(ID3D11Device* dev, rt::Engine& e, bool print)
    {
        int bad = 0;
        ID3D11InfoQueue* q11 = nullptr;
        if (SUCCEEDED(dev->QueryInterface(__uuidof(ID3D11InfoQueue), reinterpret_cast<void**>(&q11))) && q11)
        {
            for (UINT64 i = 0, n = q11->GetNumStoredMessages(); i < n; ++i)
            {
                SIZE_T len = 0; q11->GetMessage(i, nullptr, &len);
                std::vector<char> b(len); auto* m = reinterpret_cast<D3D11_MESSAGE*>(b.data());
                if (SUCCEEDED(q11->GetMessage(i, m, &len)) && m->Severity <= D3D11_MESSAGE_SEVERITY_WARNING)
                { ++bad; if (print) std::printf("    D3D11 %s: %.*s\n", m->Severity <= D3D11_MESSAGE_SEVERITY_ERROR ? "error" : "warning", int(m->DescriptionByteLength), m->pDescription); }
            }
            q11->ClearStoredMessages();
            q11->Release();
        }
        ID3D12InfoQueue* q12 = nullptr;
        if (e.Device12() && SUCCEEDED(e.Device12()->QueryInterface(__uuidof(ID3D12InfoQueue), reinterpret_cast<void**>(&q12))) && q12)
        {
            for (UINT64 i = 0, n = q12->GetNumStoredMessages(); i < n; ++i)
            {
                SIZE_T len = 0; q12->GetMessage(i, nullptr, &len);
                std::vector<char> b(len); auto* m = reinterpret_cast<D3D12_MESSAGE*>(b.data());
                if (SUCCEEDED(q12->GetMessage(i, m, &len)) && m->Severity <= D3D12_MESSAGE_SEVERITY_WARNING)
                { ++bad; if (print) std::printf("    D3D12 %s: %.*s\n", m->Severity <= D3D12_MESSAGE_SEVERITY_ERROR ? "error" : "warning", int(m->DescriptionByteLength), m->pDescription); }
            }
            q12->ClearStoredMessages();
            q12->Release();
        }
        return bad;
    }
}

int main(int argc, char** argv)
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    bool debug = true;
    std::string outDir = ".";
    for (int i = 1; i < argc; ++i)
    {
        if (!std::strcmp(argv[i], "--no-debug")) debug = false;
        else if (!std::strcmp(argv[i], "--out") && i + 1 < argc) outDir = argv[++i];
    }
    fwd = Norm({ 0, 1, -0.22f });
    right = { 1, 0, 0 };
    up = Cross(right, fwd);
    frus[0] = -1; frus[1] = 1; frus[2] = float(TexH) / TexW; frus[3] = -float(TexH) / TexW;

    // The "game's" device.
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl = D3D_FEATURE_LEVEL_11_1;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, debug ? D3D11_CREATE_DEVICE_DEBUG : 0, &fl, 1, D3D11_SDK_VERSION, &dev, nullptr, &ctx);
    if (FAILED(hr)) { std::printf("no D3D11 device (0x%08X)\n", unsigned(hr)); return 2; }
    {
        IDXGIDevice* x = nullptr; IDXGIAdapter* a = nullptr; DXGI_ADAPTER_DESC ad{};
        dev->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&x)); x->GetAdapter(&a); a->GetDesc(&ad);
        std::printf("GPU: %ls; debug layers %s\n", ad.Description, debug ? "on (D3D11 debug device, D3D12 debug layer + GPU-based validation)" : "off");
        a->Release(); x->Release();
    }

    // Its buffers: the lit picture, the motion vectors, the depth.
    auto tex = [&](DXGI_FORMAT f, UINT bind) {
        D3D11_TEXTURE2D_DESC d{}; d.Width = TexW; d.Height = TexH; d.MipLevels = 1; d.ArraySize = 1; d.Format = f;
        d.SampleDesc.Count = 1; d.BindFlags = bind; ID3D11Texture2D* t = nullptr; dev->CreateTexture2D(&d, nullptr, &t); return t;
    };
    ID3D11Texture2D* hdr = tex(DXGI_FORMAT_R11G11B10_FLOAT, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
    ID3D11Texture2D* motion = tex(DXGI_FORMAT_R16G16_FLOAT, D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE);
    ID3D11Texture2D* depth = tex(DXGI_FORMAT_R24G8_TYPELESS, D3D11_BIND_DEPTH_STENCIL | D3D11_BIND_SHADER_RESOURCE);
    ID3D11RenderTargetView* hdrRtv = nullptr, * mvRtv = nullptr;
    ID3D11DepthStencilView* dsv = nullptr;
    dev->CreateRenderTargetView(hdr, nullptr, &hdrRtv);
    dev->CreateRenderTargetView(motion, nullptr, &mvRtv);
    D3D11_DEPTH_STENCIL_VIEW_DESC dd{}; dd.Format = DXGI_FORMAT_D24_UNORM_S8_UINT; dd.ViewDimension = D3D11_DSV_DIMENSION_TEXTURE2D;
    dev->CreateDepthStencilView(depth, &dd, &dsv);

    // The scene.
    std::vector<Object> objects;
    {
        Object g;
        g.scale = { 1, 1, 1 }; g.pos = { 0, 0, 0 }; g.color = { 0.45f, 0.42f, 0.36f };
        g.local = { -3000, -600, 0, 3000, -600, 0, 3000, 6000, 0, -3000, 6000, 0 };
        g.idx = { 0, 1, 2, 0, 2, 3 };
        objects.push_back(g);
        objects.push_back(Box({ 200, 200, 300 }, { 0, 900, 150 }, { 0.6f, 0.25f, 0.2f }));
        objects.push_back(Box({ 160, 600, 30 }, { -420, 700, 50 }, { 0.2f, 0.4f, 0.6f }));    // a low bench: the ground under it occluded
    }
    std::vector<Vertex> verts;
    for (auto& o : objects)
        for (size_t t = 0; t < o.idx.size(); t += 3)
        {
            V3 w[3];
            for (int k = 0; k < 3; ++k)
            {
                const float* l = &o.local[o.idx[t + k] * 3];
                w[k] = { l[0] * o.scale.x + o.pos.x, l[1] * o.scale.y + o.pos.y, l[2] * o.scale.z + o.pos.z };
            }
            V3 n = Norm(Cross(w[1] - w[0], w[2] - w[0]));
            for (auto& p : w) verts.push_back({ p, n, o.color });
        }
    const rt::RtCapsule person{ { 260, 650, 30 }, 28, { 260, 650, 150 }, 0 };

    ID3D11Buffer* vb = nullptr, * cb = nullptr;
    {
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = UINT(verts.size() * sizeof(Vertex)); bd.BindFlags = D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA sd{ verts.data() };
        dev->CreateBuffer(&bd, &sd, &vb);
        float k[7][4] = { { camPos.x, camPos.y, camPos.z, 0 }, { fwd.x, fwd.y, fwd.z, 0 }, { right.x, right.y, right.z, 0 }, { up.x, up.y, up.z, 0 },
                          { frus[0], frus[1], frus[2], frus[3] }, { Near, Far, 0, 0 }, { sun.x, sun.y, sun.z, 0 } };
        bd.ByteWidth = sizeof(k); bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; sd.pSysMem = k;
        dev->CreateBuffer(&bd, &sd, &cb);
    }
    ID3D11VertexShader* sceneVs = nullptr; ID3D11PixelShader* scenePs = nullptr; ID3D11InputLayout* layout = nullptr;
    {
        ID3DBlob* v = nullptr, * p = nullptr, * err = nullptr;
        if (FAILED(D3DCompile(SceneHlsl, std::strlen(SceneHlsl), "scene", nullptr, nullptr, "VS", "vs_5_0", 0, 0, &v, &err)) ||
            FAILED(D3DCompile(SceneHlsl, std::strlen(SceneHlsl), "scene", nullptr, nullptr, "PS", "ps_5_0", 0, 0, &p, &err)))
        { std::printf("scene shader: %s\n", err ? static_cast<char*>(err->GetBufferPointer()) : "?"); return 2; }
        dev->CreateVertexShader(v->GetBufferPointer(), v->GetBufferSize(), nullptr, &sceneVs);
        dev->CreatePixelShader(p->GetBufferPointer(), p->GetBufferSize(), nullptr, &scenePs);
        D3D11_INPUT_ELEMENT_DESC ie[] = { { "POSITION", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 0 }, { "NORMAL", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 12 }, { "COLOR", 0, DXGI_FORMAT_R32G32B32_FLOAT, 0, 24 } };
        dev->CreateInputLayout(ie, 3, v->GetBufferPointer(), v->GetBufferSize(), &layout);
        v->Release(); p->Release();
    }
    ID3D11RasterizerState* noCull = nullptr;
    { D3D11_RASTERIZER_DESC rd{}; rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE; dev->CreateRasterizerState(&rd, &noCull); }

    auto drawScene = [&](UINT dw, UINT dh) {
        float clear[4] = { 0.5f, 0.6f, 0.8f, 1 }, zero[4]{};
        ctx->ClearRenderTargetView(hdrRtv, clear);
        ctx->ClearRenderTargetView(mvRtv, zero);
        ctx->ClearDepthStencilView(dsv, D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 1.0f, 0);
        ID3D11RenderTargetView* rtvs[2] = { hdrRtv, mvRtv };
        ctx->OMSetRenderTargets(2, rtvs, dsv);
        D3D11_VIEWPORT vp{ 0, 0, float(dw), float(dh), 0, 1 };
        ctx->RSSetViewports(1, &vp);
        ctx->RSSetState(noCull);
        UINT stride = sizeof(Vertex), off = 0;
        ctx->IASetVertexBuffers(0, 1, &vb, &stride, &off);
        ctx->IASetInputLayout(layout);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(sceneVs, nullptr, 0); ctx->VSSetConstantBuffers(0, 1, &cb);
        ctx->PSSetShader(scenePs, nullptr, 0); ctx->PSSetConstantBuffers(0, 1, &cb);
        ctx->Draw(UINT(verts.size()), 0);
        ctx->OMSetRenderTargets(0, nullptr, nullptr);
    };

    // The engine, fed as the plugin feeds it.
    rt::Engine e;
    if (!e.Start(dev, debug, LogLine)) { std::printf("engine didn't start: %s\n", e.Status().c_str()); return 3; }
    std::vector<rt::Instance> inst;
    for (size_t i = 0; i < objects.size(); ++i)
    {
        rt::Instance x{ 1000 + i, {} };
        const Object& o = objects[i];
        x.transform[0][0] = o.scale.x; x.transform[1][1] = o.scale.y; x.transform[2][2] = o.scale.z;
        V3 t = o.pos - camPos;
        x.transform[0][3] = t.x; x.transform[1][3] = t.y; x.transform[2][3] = t.z;
        inst.push_back(x);
    }
    std::vector<rt::RtCapsule> caps;
    {
        rt::RtCapsule c = person;
        for (int k = 0; k < 3; ++k) { c.a[k] -= (&camPos.x)[k]; c.b[k] -= (&camPos.x)[k]; }
        caps.push_back(c);
    }
    rt::FrameInputs in;
    in.depth = depth; in.motion = motion; in.hdr = hdr;
    in.camera = { { fwd.x, fwd.y, fwd.z }, { right.x, right.y, right.z }, { up.x, up.y, up.z }, { frus[0], frus[1], frus[2], frus[3] }, Near, Far };
    in.sun[0] = sun.x; in.sun[1] = sun.y; in.sun[2] = sun.z; in.sunOn = true;
    rt::Options opt;
    opt.quality = 2;

    auto runFrames = [&](int frames, UINT dw, UINT dh, bool feedMeshes) {
        in.drawnWidth = dw; in.drawnHeight = dh;
        int traced = 0;
        for (int f = 0; f < frames; ++f)
        {
            if (feedMeshes)
                for (size_t i = 0; i < objects.size(); ++i)
                    if (e.Wants(1000 + i) && e.Room() > 0)
                    {
                        rt::Mesh m; m.key = 1000 + i; m.positions = objects[i].local; m.indices = objects[i].idx;
                        e.Queue(std::move(m));
                    }
            drawScene(dw, dh);
            if (e.Render(ctx, in, inst, caps, opt)) ++traced;
            e.EndFrame();
            if (!WaitGpu(dev, ctx, 3000)) { std::printf("  the GPU didn't finish a frame within 3 s\n"); ++failures; break; }
        }
        return traced;
    };

    std::printf("1. Full resolution, quality High, 40 frames\n");
    ULONGLONG t0 = GetTickCount64();
    // The picture before ray tracing, for comparison.
    drawScene(TexW, TexH);
    Readback before; Read(dev, ctx, hdr, before);
    int traced = runFrames(40, TexW, TexH, true);
    std::printf("  %d frames traced in %llu ms; %s\n", traced, GetTickCount64() - t0, e.Report().c_str());
    Check(traced >= 38, "frames traced (of 40)", traced);
    Check(e.Running(), "engine still running", e.Running());
    if (!e.Running()) { std::printf("  status: %s\nFAILED: the engine stopped\n", e.Status().c_str()); DebugMessages(dev, e, true); ExitProcess(1); }
    // More frames so the line-up window (60 frames) has closed at least once.
    runFrames(30, TexW, TexH, true);
    Check(e.LineUp() >= 0.95, "line-up: camera rays ending where the depth buffer says", e.LineUp());

    Readback a, b, c, after;
    Read(dev, ctx, e.Result11(0), a); Read(dev, ctx, e.Result11(1), b); Read(dev, ctx, e.Result11(2), c); Read(dev, ctx, hdr, after);
    float sx = float(e.TracedWidth()) / TexW;   // traced texels per full-resolution pixel
    auto at = [&](const Readback& r, V3 p, int ch) {
        int x, y; if (!Project(p, TexW, TexH, x, y)) return -1.0f;
        return Rgba16(r, int((x + 0.5f) * sx), int((y + 0.5f) * sx), ch);
    };
    // The box's shadow: its top centre along the sun to the ground.
    V3 top{ 0, 900, 300 };
    V3 inShadow = top - sun * (300 / sun.z);
    V3 lit{ -900, 1500, 0 }, openGround{ 700, 400, 0 };
    V3 underTable{ -420, 388, 0 }, boxFoot{ 0, 795, 0 };
    V3 capShadow = V3{ 260, 650, 90 } - sun * (90 / sun.z);
    float sh = at(a, inShadow, 0), sl = at(a, lit, 0), sc = at(a, capShadow, 0);
    Check(sh >= 0 && sh < 0.2f, "shadow of the box on the ground (0 = shadowed)", sh);
    Check(sl > 0.8f, "open ground in sunlight (1 = lit)", sl);
    Check(sc >= 0 && sc < 0.5f, "the capsule's (a person's) shadow on the ground", sc);
    float aoOpen = at(a, openGround, 1), aoTable = at(a, underTable, 1), aoFoot = at(a, boxFoot, 1);
    Check(aoOpen > 0.85f, "occlusion on open ground (1 = open)", aoOpen);
    Check(aoTable >= 0 && aoTable < aoOpen - 0.1f, "occlusion on the ground just under the bench's front edge", aoTable);
    Check(aoFoot >= 0 && aoFoot < aoOpen - 0.1f, "occlusion where the box meets the ground", aoFoot);
    {
        int x, y; Project(boxFoot - V3{ 0, 60, 0 }, TexW, TexH, x, y);
        int tx = int((x + 0.5f) * sx), ty = int((y + 0.5f) * sx);
        float gi = Rgba16(b, tx, ty, 0);
        Check(gi > 0.01f, "bounce light (red) in front of the red box", gi);
        int fx, fy; Project(V3{ 1500, 4500, 0 }, TexW, TexH, fx, fy);
        float fr = Rgba16(c, int((fx + 0.5f) * sx), int((fy + 0.5f) * sx), 3);
        Check(fr > 0.05f, "reflection weight on far (grazing) ground", fr);
    }
    {
        int x, y; Project(inShadow, TexW, TexH, x, y);
        V3 c0 = Rgb111110(before, x, y), c1 = Rgb111110(after, x, y);
        Check(c1.x < c0.x * 0.9f, "composite: the shadowed ground darker than the game drew it", c1.x / std::max(c0.x, 1e-6f));
        Project(V3{ 0, 2000, 3000 }, TexW, TexH, x, y);   // the sky
        V3 s0 = Rgb111110(before, 2, 2), s1 = Rgb111110(after, 2, 2);
        Check(std::fabs(s0.z - s1.z) < 1e-3f, "composite: the sky left alone", std::fabs(s0.z - s1.z));
    }
    WriteBmp(outDir + "\\rt-before.bmp", TexW, TexH, [&](int x, int y) { return Rgb111110(before, x, y); });
    WriteBmp(outDir + "\\rt-after.bmp", TexW, TexH, [&](int x, int y) { return Rgb111110(after, x, y); });
    WriteBmp(outDir + "\\rt-shadow-occlusion.bmp", a.w, a.h, [&](int x, int y) { float s = Rgba16(a, x, y, 0), o = Rgba16(a, x, y, 1); return V3{ s * 3, o * 3, o * 3 }; });
    WriteBmp(outDir + "\\rt-reflection.bmp", c.w, c.h, [&](int x, int y) { float w = Rgba16(c, x, y, 3); return V3{ Rgba16(c, x, y, 0) * w * 4, Rgba16(c, x, y, 1) * w * 4, w * 2 }; });
    WriteBmp(outDir + "\\rt-bounce.bmp", b.w, b.h, [&](int x, int y) { return V3{ Rgba16(b, x, y, 0) * 4, Rgba16(b, x, y, 1) * 4, Rgba16(b, x, y, 2) * 4 }; });
    int msgs = DebugMessages(dev, e, true);
    Check(msgs == 0, "debug layer messages (errors and warnings)", msgs);

    std::printf("2. Every quality and debug view, and a drawn part of 960x540 (dynamic resolution)\n");
    for (int q = 0; q < 4; ++q) { opt.quality = q; runFrames(6, TexW, TexH, false); }
    for (int v = 1; v <= 5; ++v) { opt.debugView = v; runFrames(3, TexW, TexH, false); }
    opt.debugView = 0; opt.quality = 1;
    runFrames(70, 960, 540, false);
    Check(e.Running(), "engine still running", e.Running());
    Check(e.LineUp() >= 0.95, "line-up at 960x540 drawn of 1280x720", e.LineUp());
    {
        Readback a2; Read(dev, ctx, e.Result11(0), a2);
        int x, y; Project(inShadow, 960, 540, x, y);
        float s2 = Rgba16(a2, int((x + 0.5f) * 0.5f), int((y + 0.5f) * 0.5f), 0);
        Check(s2 >= 0 && s2 < 0.2f, "the box's shadow where it should be, drawn smaller and traced at half", s2);
    }
    msgs = DebugMessages(dev, e, true);
    Check(msgs == 0, "debug layer messages", msgs);
    std::printf("  %s\n", e.Report().c_str());

    std::printf("3. A town's worth: 600 new meshes (half alpha-tested), 4000 instances behind the camera, 40 frames\n");
    {
        std::vector<rt::Instance> many = inst;
        std::vector<rt::Mesh> pending;
        std::uint32_t seed = 12345;
        auto rnd = [&]() { seed = seed * 1664525u + 1013904223u; return float(seed >> 8) / 16777216.0f; };
        for (int m = 0; m < 600; ++m)
        {
            rt::Mesh mesh;
            mesh.key = 50000 + m;
            mesh.alphaTested = (m & 1) != 0;
            const int n = 10;   // a 10 x 10 grid of quads, a bumpy patch: 200 triangles
            for (int y = 0; y <= n; ++y) for (int x = 0; x <= n; ++x) mesh.positions.insert(mesh.positions.end(), { x * 10.0f, y * 10.0f, rnd() * 20.0f });
            for (int y = 0; y < n; ++y) for (int x = 0; x < n; ++x)
            {
                std::uint16_t a = std::uint16_t(y * (n + 1) + x), b = std::uint16_t(a + 1), c = std::uint16_t(a + n + 1), d = std::uint16_t(c + 1);
                mesh.indices.insert(mesh.indices.end(), { a, b, d, a, d, c });
            }
            pending.push_back(std::move(mesh));
        }
        for (int i = 0; i < 4000; ++i)
        {
            rt::Instance x{ std::uint64_t(50000 + i % 600), {} };
            x.transform[0][0] = x.transform[1][1] = x.transform[2][2] = 1;
            V3 t = V3{ (rnd() - 0.5f) * 6000, -700 - rnd() * 4000, rnd() * 100 } - camPos;
            x.transform[0][3] = t.x; x.transform[1][3] = t.y; x.transform[2][3] = t.z;
            many.push_back(x);
        }
        opt.quality = 1;
        in.drawnWidth = TexW; in.drawnHeight = TexH;
        ULONGLONG s0 = GetTickCount64();
        for (int f = 0; f < 40; ++f)
        {
            for (auto it = pending.begin(); it != pending.end() && e.Room() > 0;)
                if (e.Wants(it->key)) { e.Queue(std::move(*it)); it = pending.erase(it); } else ++it;
            drawScene(TexW, TexH);
            e.Render(ctx, in, many, caps, opt);
            e.EndFrame();
            if (!WaitGpu(dev, ctx, 3000)) { std::printf("  the GPU didn't finish a frame within 3 s\n"); ++failures; break; }
        }
        std::printf("  %llu ms; %s\n", GetTickCount64() - s0, e.Report().c_str());
        Check(pending.empty() && e.BlasCount() >= 603, "every mesh built, a frame's share at a time", double(e.BlasCount()));
        Check(e.Running(), "engine still running", e.Running());
        msgs = DebugMessages(dev, e, true);
        Check(msgs == 0, "debug layer messages", msgs);
        runFrames(62, TexW, TexH, false);
        Check(e.LineUp() >= 0.95, "line-up with them all in the TLAS", e.LineUp());
    }

    std::printf("4. Sharpening (after DLSS)\n");
    {
        const UINT W = 64, H = 16;
        std::vector<std::uint32_t> px(W * H);
        for (UINT y = 0; y < H; ++y) for (UINT x = 0; x < W; ++x)
        {
            float t = 0.5f + 0.5f * std::tanh((float(x) - 32.0f) / 3.0f);   // a soft edge
            std::uint8_t v = std::uint8_t(40 + t * 160);
            px[y * W + x] = v | (v << 8) | (v << 16) | 0xFF000000u;
        }
        D3D11_TEXTURE2D_DESC d{}; d.Width = W; d.Height = H; d.MipLevels = 1; d.ArraySize = 1; d.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        d.SampleDesc.Count = 1; d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        D3D11_SUBRESOURCE_DATA sd{ px.data(), W * 4 };
        ID3D11Texture2D* src = nullptr, * dst = nullptr;
        dev->CreateTexture2D(&d, &sd, &src); dev->CreateTexture2D(&d, nullptr, &dst);
        ID3D11ShaderResourceView* srv = nullptr; ID3D11RenderTargetView* rtv = nullptr;
        dev->CreateShaderResourceView(src, nullptr, &srv); dev->CreateRenderTargetView(dst, nullptr, &rtv);
        ID3D11VertexShader* fvs = nullptr; ID3D11PixelShader* sps = nullptr;
        dev->CreateVertexShader(g_FullscreenVS, sizeof(g_FullscreenVS), nullptr, &fvs);
        dev->CreatePixelShader(g_SharpenPS, sizeof(g_SharpenPS), nullptr, &sps);
        rt::SharpenConstants k{ { 0.8f, 0, 0, 0 }, { W, H, 0, 0 } };
        D3D11_BUFFER_DESC bd{}; bd.ByteWidth = sizeof(k); bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        D3D11_SUBRESOURCE_DATA kd{ &k };
        ID3D11Buffer* kb = nullptr; dev->CreateBuffer(&bd, &kd, &kb);
        ctx->ClearState();
        ctx->OMSetRenderTargets(1, &rtv, nullptr);
        D3D11_VIEWPORT vp{ 0, 0, float(W), float(H), 0, 1 };
        ctx->RSSetViewports(1, &vp);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(fvs, nullptr, 0); ctx->PSSetShader(sps, nullptr, 0);
        ctx->PSSetConstantBuffers(1, 1, &kb); ctx->PSSetShaderResources(0, 1, &srv);
        ctx->Draw(3, 0);
        ctx->ClearState();
        Readback r; Read(dev, ctx, dst, r);
        auto lum = [&](int x) { return float(r.data[size_t(8) * r.pitch + x * 4]); };
        float flat = lum(4), lowSide = lum(30), highSide = lum(34), mid = lum(32);
        auto srcAt = [&](int x) { return float(px[8 * W + x] & 0xFF); };
        Check(std::fabs(flat - srcAt(4)) < 1.5f, "flat area unchanged", flat - srcAt(4));
        Check(lowSide < srcAt(30) - 1 && highSide > srcAt(34) + 1, "edge steeper (dark side darker, bright side brighter)", (highSide - lowSide) - (srcAt(34) - srcAt(30)));
        bool within = true;
        for (int x = 1; x < 63; ++x) within &= lum(x) >= std::min({ srcAt(x - 1), srcAt(x), srcAt(x + 1) }) - 1 && lum(x) <= std::max({ srcAt(x - 1), srcAt(x), srcAt(x + 1) }) + 1;
        Check(within, "no overshoot past each pixel's neighbourhood (no halos)", mid);
        for (auto* p : std::initializer_list<IUnknown*>{ src, dst, srv, rtv, fvs, sps, kb }) p->Release();
    }

    std::printf("5. The Direct3D 12 device removed: ray tracing turns off, Direct3D 11 carries on\n");
    {
        // A Direct3D 11 wait on Direct3D 12 work that was submitted but the device is removed before it finishes.
        ID3D11DeviceContext4* c4 = nullptr;
        ctx->QueryInterface(__uuidof(ID3D11DeviceContext4), reinterpret_cast<void**>(&c4));
        drawScene(TexW, TexH);
        e.Render(ctx, in, inst, caps, opt);
        e.Device12()->RemoveDevice();
        bool ok11 = WaitGpu(dev, ctx, 4000);
        Check(ok11, "Direct3D 11 finished its frame (its wait on Direct3D 12 released)", ok11);
        e.EndFrame();
        Check(!e.Running(), "ray tracing turned itself off", !e.Running());
        std::printf("  status: %s\n", e.Status().c_str());
        drawScene(TexW, TexH);
        bool again = e.Render(ctx, in, inst, caps, opt);
        Check(!again, "no more ray tracing after", again);
        Check(WaitGpu(dev, ctx, 2000), "Direct3D 11 still draws", 1);
        HRESULT removed = dev->GetDeviceRemovedReason();
        Check(removed == S_OK, "the game's Direct3D 11 device unharmed", double(removed));
        if (c4) c4->Release();
    }

    std::printf("\n%s: %d check%s failed\n", failures ? "FAILED" : "PASSED", failures, failures == 1 ? "" : "s");
    std::fflush(stdout);
    // The removed device's objects aren't released one by one; the process ends.
    ExitProcess(failures ? 1 : 0);
}
