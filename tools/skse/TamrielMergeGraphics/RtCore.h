// TamrielMergeGraphics' ray tracing core: a Direct3D 12 device of its own beside the game's Direct3D 11 one, the world
// as acceleration structures, inline ray queries in compute (shadows, ambient occlusion, one bounce of diffuse light,
// reflections), and the results put into the game's picture. It knows nothing of Skyrim: the plugin hands it the
// meshes, the instances, the camera and the game's textures, and the test harness (test\RtHarness.cpp) does the same
// with a scene of its own. TamrielMerge's own work, from Microsoft's Direct3D 12 / DXR documentation.
//
// Safety (the 2026-10-09 GPU hang): the CPU never waits on the GPU in a frame (each frame's command memory is reused
// only once the GPU is done with it; otherwise that frame isn't ray traced); nothing is freed or rewritten while the GPU
// may still read it; each frame's work is bounded (meshes built per frame, triangles, instances, rays per pixel, ray
// lengths, candidates per ray); the meshes are checked before they're built; Direct3D 11 only waits for Direct3D 12
// work already submitted; a removed device, a failed call or work taking over 2 s turns ray tracing off.
#pragma once
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <cstdint>
#include <string>
#include <vector>
#include <unordered_map>
#include <unordered_set>
#include "RtShared.h"

namespace rt
{
    struct Camera
    {
        float forward[3], right[3], up[3];   // world axes
        float frustum[4];                    // left, right, top, bottom at distance 1
        float nearZ, farZ;                   // the depth buffer's (standard D3D depth)
    };

    /// A static mesh's triangles, already copied out of the game and checked (positions finite, indices in range).
    struct Mesh
    {
        std::uint64_t key = 0;
        std::vector<float> positions;          // x, y, z per vertex
        std::vector<std::uint16_t> indices;    // three per triangle
        bool alphaTested = false;              // leaves and the like: covers half of the time
    };

    struct Instance
    {
        std::uint64_t key;
        float transform[3][4];                 // object to camera-relative world, row-major 3 x 4
    };

    struct Options
    {
        int quality = 1;                       // 0 low, 1 medium, 2 high, 3 ultra
        float shadows = 0.6f, occlusion = 0.7f, bounce = 1.0f, reflections = 0.5f;
        int debugView = 0;                     // 0 picture, 1 shadows, 2 occlusion, 3 bounce, 4 reflections, 5 normals
        float occlusionRadius = 110.0f, bounceDistance = 1500.0f, reflectionDistance = 3000.0f, shadowDistance = 8000.0f;
        std::uint64_t memoryBudget = 512ull << 20;   // acceleration structures
    };

    struct FrameInputs
    {
        ID3D11Resource* depth = nullptr;       // the game's depth buffer (R24G8, R32, D32S8 or R16 families)
        ID3D11Resource* motion = nullptr;      // motion vectors (R16G16 float, uv: previous = current + motion)
        ID3D11Resource* hdr = nullptr;         // the lit picture before tonemapping; the composite draws into it
        UINT drawnWidth = 0, drawnHeight = 0;  // the part drawn (top left), full resolution
        Camera camera{};
        float sun[3]{};
        bool sunOn = false;
    };

    using LogFn = void (*)(const char*);

    class Engine
    {
    public:
        bool Start(ID3D11Device* device, bool debugLayer, LogFn log);
        bool Running() const { return running; }
        const std::string& Status() const { return status; }

        /// True when this mesh has no acceleration structure yet and should be handed over with Queue.
        bool Wants(std::uint64_t key) const;
        bool HasMesh(std::uint64_t key) const { return blases.count(key) != 0; }
        /// How many more meshes this frame takes (the rest wait for the next frames).
        int Room() const;
        void Queue(Mesh&& mesh);

        /// Inside the game's frame, before tonemapping: traces and puts the results into in.hdr. False when this frame
        /// wasn't ray traced (the picture is then left as the game drew it).
        bool Render(ID3D11DeviceContext* ctx, const FrameInputs& in, const std::vector<Instance>& instances,
                    const std::vector<RtCapsule>& capsules, const Options& o);
        /// Once a frame (at Present): the device's health, the GPU's progress, the statistics, freeing what's done with.
        void EndFrame();
        void Fail(const std::string& why);
        void Stop();

        std::string Report() const;            // one line: what's traced, the GPU's time, the line-up check
        double GpuMs() const { return gpuMs; }
        double LineUp() const { return lineUp; }       // the share of camera rays that end where the depth buffer says
        unsigned LineUpSamples() const { return lineUpSamples; }
        bool LinedUp() const { return !paused; }
        std::size_t BlasCount() const { return blases.size(); }
        std::uint64_t BlasBytes() const { return arenaUsed; }
        ID3D12Device5* Device12() const { return dev; }
        unsigned FramesTraced() const { return framesTraced; }
        unsigned FramesSkipped() const { return framesSkipped; }
        // For the test harness: the shared results (shadow/occlusion, bounce, reflection) and fences as Direct3D 11 sees them.
        ID3D11Texture2D* Result11(int i) const { return out[i].tex11; }
        ID3D11Fence* FenceOut11() const { return fenceOut11; }
        std::uint64_t LastSubmitted() const { return valueOut; }
        UINT TracedWidth() const { return rtTexW; }
        UINT TracedHeight() const { return rtTexH; }

    private:
        struct Range { std::uint64_t offset, size; };
        struct Chunk { ID3D12Resource* buffer = nullptr; std::uint64_t size = 0; std::vector<Range> free; };
        struct Blas { int chunk = -1; std::uint64_t offset = 0, size = 0; D3D12_GPU_VIRTUAL_ADDRESS address = 0; unsigned lastUsed = 0; std::uint64_t lastFence = 0; bool alpha = false; };
        struct FrameContext
        {
            ID3D12CommandAllocator* allocator = nullptr;
            ID3D12GraphicsCommandList4* listA = nullptr, * listB = nullptr;
            ID3D12Resource* upload = nullptr; std::uint8_t* mapped = nullptr; std::uint64_t used = 0;
            ID3D12Resource* readback = nullptr;
            std::uint64_t fence = 0;
            bool pendingStats = false;
            double submitted = 0;
        };
        struct Texture { ID3D12Resource* res = nullptr; D3D12_RESOURCE_STATES state = D3D12_RESOURCE_STATE_COMMON; ID3D11Texture2D* tex11 = nullptr; ID3D11RenderTargetView* rtv = nullptr; ID3D11ShaderResourceView* srv = nullptr; };

        void Log(const char* fmt, ...) const;
        bool CreateRootAndPipelines();
        bool CreateD3D11Side();
        bool EnsureTextures(UINT texW, UINT texH, float scale);
        void ReleaseTextures();
        bool CreateTexture(Texture& t, UINT w, UINT h, DXGI_FORMAT f, bool shared, D3D12_RESOURCE_FLAGS flags);
        void WriteDescriptors();
        bool WaitIdle(DWORD ms);
        D3D12_GPU_VIRTUAL_ADDRESS UploadAlloc(FrameContext& fc, std::uint64_t size, void** cpu);
        bool ArenaAlloc(std::uint64_t size, Blas& b);
        void ArenaFree(const Blas& b);
        void Defer(IUnknown* p);
        void Transition(ID3D12GraphicsCommandList4* l, Texture& t, D3D12_RESOURCE_STATES to);
        ID3D11ShaderResourceView* GameView(ID3D11Resource* r);
        ID3D11RenderTargetView* GameTarget(ID3D11Resource* r);
        void ReadStats(FrameContext& fc);

        LogFn log = nullptr;
        bool running = false, debug = false;
        std::string status = "off";

        ID3D11Device* d11 = nullptr;
        ID3D11Device5* d11_5 = nullptr;
        ID3D12Device5* dev = nullptr;
        ID3D12CommandQueue* queue = nullptr;
        ID3D12Fence* fenceIn = nullptr, * fenceOut = nullptr;      // in: Direct3D 11 signals; out: Direct3D 12 signals
        ID3D11Fence* fenceIn11 = nullptr, * fenceOut11 = nullptr;
        std::uint64_t valueIn = 0, valueOut = 0;
        HANDLE idleEvent = nullptr;
        IDXGIAdapter3* adapter = nullptr;

        ID3D12RootSignature* root = nullptr;
        ID3D12PipelineState* traceState = nullptr, * temporalState = nullptr, * spatialState = nullptr;
        ID3D12DescriptorHeap* heap = nullptr, * cpuHeap = nullptr;
        UINT descSize = 0;
        ID3D12QueryHeap* timestamps = nullptr;
        std::uint64_t timestampFrequency = 0;
        static constexpr int Contexts = 4;
        FrameContext contexts[Contexts];
        ID3D12Resource* stats = nullptr;

        std::vector<Chunk> chunks;
        std::uint64_t arenaUsed = 0, arenaSize = 0, budget = 512ull << 20;
        std::unordered_map<std::uint64_t, Blas> blases;
        std::unordered_set<std::uint64_t> failedKeys;
        std::vector<Mesh> queued;
        ID3D12Resource* scratch = nullptr; std::uint64_t scratchSize = 0;
        ID3D12Resource* tlas = nullptr; UINT tlasCapacity = 0; std::uint64_t tlasScratchOffset = 0;
        ID3D12Resource* capsuleBlas = nullptr; std::uint64_t capsuleScratch = 0;
        std::vector<std::pair<std::uint64_t, IUnknown*>> deferred;

        // Textures: shared inputs (full resolution), traced results (raw, two histories, shared outputs).
        UINT texW = 0, texH = 0, rtTexW = 0, rtTexH = 0;
        float rtScale = 0;
        Texture inDepth, inMotion, inHdr, raw[3], hist[2][3], out[3];
        int parity = 0;
        bool historyValid = false;
        float prevScale[2]{};

        // Direct3D 11 objects.
        ID3DDeviceContextState* state11 = nullptr;
        ID3D11VertexShader* vs = nullptr;
        ID3D11PixelShader* preparePs = nullptr, * compositePs = nullptr, * normalsPs = nullptr;
        ID3D11Buffer* compositeCb = nullptr;
        struct GameRes { ID3D11Resource* res; ID3D11ShaderResourceView* srv; ID3D11RenderTargetView* rtv; };
        std::vector<GameRes> gameViews;

        unsigned frameNo = 0, framesTraced = 0, framesSkipped = 0;
        double gpuMs = 0, lineUp = -1;
        unsigned lineUpSamples = 0, lowWindows = 0;
        unsigned statSamples = 0, statAgree = 0, statFrames = 0;
        bool paused = false, memoryFull = false;
        int builtThisFrame = 0, instancesThisFrame = 0, capsulesThisFrame = 0;
        std::uint64_t trianglesThisFrame = 0;
    };
}
