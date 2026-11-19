// TamrielMergeNative: an SKSE plugin giving the converted games' scripts the parts of Skyrim's engine Papyrus can't
// reach (user, 2026-10-23: "keep them" -> build the native plugin for the script items Papyrus can't do).
//
// Skyrim still carries the classic script engine its console uses: two tables of commands (script functions and
// console functions), each with a name, its parameters and its handlers - "eval" for the ones conditions can test,
// "execute" for the rest. Many of Oblivion's functions are there under the same names (GetKnockedState, IsSpellTarget,
// SetForceRun, PickIdle, SetRigidBodyMass, ...). This plugin finds them by name in those tables (SKSE 2.3.1's
// addresses for runtime 1.7.104, g_firstObScriptCommand/g_firstConsoleCommand) and gives Papyrus two functions:
//
//   TMNative.Condition(name, subject, form, number)   a condition's value, through its eval handler
//   TMNative.Command(name, subject, a, b, count)      a command run on subject, through its execute handler
//
// A command's arguments are what the game's own argument reader (ObScript_ExtractArgs, SKSE 2.3.1: 0x344CA0) takes,
// read from its code (disassembled 2026-10-23): after the opcode and length, the number of arguments (u16), then each
// one: 'n' (0x6E) and an int32 for an integer, 'z' (0x7A) and a double for a float. The reader stores a value only
// when the containing reference is initialized (form flag 1 << 3) and refuses a null script, so the subject is both
// the reference acted on and the containing one, with a script and locals of our own (no variables are read for
// constants). Commands with parameters of other kinds (forms, strings) are refused.
//
// Only numbers and forms pass through; no hooks, no patches, nothing runs unless a script calls it. With
// [Test] RunOnLoad=1 in TamrielMergeNative.ini the plugin checks itself after a save loads (in TamrielMergeNative.log
// beside it): every function it is asked for is found, the conditions are read on the player, and SetForceRun's
// argument is seen to reach the actor (its flag at +0xCC, bit 1, set by 1 and cleared by 0, read back).

#include "common/IDebugLog.h"
#include "skse64/PluginAPI.h"
#include "skse64_common/skse_version.h"
#include "skse64/PapyrusNativeFunctions.h"
#include "skse64/PapyrusVM.h"
#include "skse64/GameAPI.h"
#include "skse64/GameReferences.h"
#include "skse64/GameForms.h"
#include "skse64/ObScript.h"
#include "skse64/GameCamera.h"
#include "skse64/gamethreads.h"
#include "skse64/GameMenus.h"
#include "skse64/GameObjects.h"
#include "skse64/NiNodes.h"
#include "skse64/NiGeometry.h"
#include "skse64/NiMaterial.h"
#include "skse64/NiProperties.h"
#include "skse64/GameRTTI.h"

#include <shlobj.h>
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <string>
#include <vector>

IDebugLog gLog;

namespace
{
    PluginHandle g_plugin = kPluginHandle_Invalid;
    SKSEPapyrusInterface* g_papyrus = nullptr;
    SKSEMessagingInterface* g_messaging = nullptr;
    SKSETaskInterface* g_task = nullptr;

    // A script and locals of our own for the argument reader (it refuses null; constants read neither).
    alignas(16) unsigned char g_script[0x100] = {};
    alignas(16) unsigned char g_locals[0x100] = {};

    // ---- the command tables ----

    ObScriptCommand* Find(const char* name)
    {
        if (!name || !*name) return nullptr;
        ObScriptCommand* tables[2] = { g_firstObScriptCommand, g_firstConsoleCommand };
        const UInt32 counts[2] = { kObScript_NumObScriptCommands, kObScript_NumConsoleCommands };
        for (int t = 0; t < 2; ++t)
            for (UInt32 i = 0; i < counts[t]; ++i)
            {
                ObScriptCommand* c = tables[t] + i;
                if ((c->longName && _stricmp(c->longName, name) == 0) || (c->shortName && *c->shortName && _stricmp(c->shortName, name) == 0))
                    return c;
            }
        return nullptr;
    }

    bool IsFormParam(UInt32 type)
    {
        switch (type)
        {
        case ObScriptParam::kType_String: case ObScriptParam::kType_Integer: case ObScriptParam::kType_Float:
        case ObScriptParam::kType_ActorValue: case ObScriptParam::kType_Axis: case ObScriptParam::kType_AnimationGroup:
        case ObScriptParam::kType_Sex: case ObScriptParam::kType_VariableName: case ObScriptParam::kType_QuestStage:
        case ObScriptParam::kType_CrimeType: case ObScriptParam::kType_MiscellaneousStat: case ObScriptParam::kType_Alignment:
        case ObScriptParam::kType_EquipType: case ObScriptParam::kType_FurnitureAnimType: case ObScriptParam::kType_FurnitureEntryType:
        case ObScriptParam::kType_VMVariableName: case ObScriptParam::kType_Hand: case ObScriptParam::kType_WardState:
            return false;
        default:
            return true;
        }
    }

    bool IsNumberParam(UInt32 type) { return type == ObScriptParam::kType_Integer || type == ObScriptParam::kType_Float; }

    // A condition's value: its eval handler on subject, with the form (or the number) as its first parameter.
    bool EvalCondition(ObScriptCommand* c, TESObjectREFR* subject, TESForm* form, SInt32 number, double& result)
    {
        result = 0.0;
        if (!c || !c->eval) return false;
        if (c->needsParent && !subject) return false;
        void* arg1 = nullptr;
        if (c->numParams > 0)
        {
            const ObScriptParam& p = c->params[0];
            if (IsFormParam(p.typeID))
            {
                if (!form && !p.isOptional) return false;
                arg1 = form;
            }
            else if (p.typeID == ObScriptParam::kType_Integer || p.typeID == ObScriptParam::kType_ActorValue || p.typeID == ObScriptParam::kType_Sex
                     || p.typeID == ObScriptParam::kType_CrimeType || p.typeID == ObScriptParam::kType_Axis)
                arg1 = reinterpret_cast<void*>(static_cast<intptr_t>(number));
            else
                return false;
        }
        if (c->numParams > 1 && !c->params[1].isOptional) return false;
        return c->eval(subject, arg1, nullptr, result);
    }

    void Put16(std::vector<UInt8>& b, UInt16 v) { b.push_back(v & 0xFF); b.push_back(v >> 8); }

    // A command run on subject with up to two numbers: its execute handler on the data the argument reader takes.
    bool RunCommand(ObScriptCommand* c, TESObjectREFR* subject, float a, float b, SInt32 count, double& result)
    {
        result = 0.0;
        if (!c || !c->execute) return false;
        if (c->needsParent && !subject) return false;
        UInt32 n = c->numParams;
        if (count >= 0 && static_cast<UInt32>(count) < n) n = count;
        if (n > 2) return false;
        for (UInt32 i = n; i < c->numParams; ++i)
            if (!c->params[i].isOptional) return false;
        for (UInt32 i = 0; i < n; ++i)
            if (!IsNumberParam(c->params[i].typeID)) return false;
        // The containing reference: the subject, else the player (the reader checks its flags when it reads numbers).
        TESObjectREFR* container = subject ? subject : static_cast<TESObjectREFR*>(*g_thePlayer);
        if (n > 0 && !container) return false;
        std::vector<UInt8> data;
        Put16(data, static_cast<UInt16>(c->opcode));
        Put16(data, 0);
        Put16(data, static_cast<UInt16>(n));
        for (UInt32 i = 0; i < n; ++i)
        {
            float v = i == 0 ? a : b;
            if (c->params[i].typeID == ObScriptParam::kType_Integer)
            {
                SInt32 iv = static_cast<SInt32>(v < 0 ? v - 0.5f : v + 0.5f);
                data.push_back('n');
                for (int k = 0; k < 4; ++k) data.push_back((iv >> (8 * k)) & 0xFF);
            }
            else
            {
                double dv = v;
                UInt8 raw[8];
                std::memcpy(raw, &dv, 8);
                data.push_back('z');
                data.insert(data.end(), raw, raw + 8);
            }
        }
        UInt16 length = static_cast<UInt16>(data.size() - 4);
        data[2] = length & 0xFF;
        data[3] = length >> 8;
        UInt32 offset = 4;
        return c->execute(c->params, reinterpret_cast<ScriptData*>(data.data()), subject, container,
                          reinterpret_cast<Script*>(g_script), reinterpret_cast<ScriptLocals*>(g_locals), result, offset);
    }

    // ---- Papyrus: TMNative ----

    float Condition(StaticFunctionTag*, BSFixedString name, TESObjectREFR* subject, TESForm* form, SInt32 number)
    {
        double result = 0.0;
        ObScriptCommand* c = Find(name.data);
        if (!EvalCondition(c, subject, form, number, result))
            _MESSAGE("Condition %s: %s", name.data ? name.data : "", c ? "refused (its parameters, or no subject)" : "not found");
        return static_cast<float>(result);
    }

    bool Command(StaticFunctionTag*, BSFixedString name, TESObjectREFR* subject, float a, float b, SInt32 count)
    {
        double result = 0.0;
        ObScriptCommand* c = Find(name.data);
        bool ok = RunCommand(c, subject, a, b, count, result);
        if (!ok) _MESSAGE("Command %s: %s", name.data ? name.data : "", c ? "refused or failed" : "not found");
        return ok;
    }

    bool HasFunction(StaticFunctionTag*, BSFixedString name, bool condition)
    {
        ObScriptCommand* c = Find(name.data);
        return c && (condition ? c->eval != nullptr : c->execute != nullptr);
    }

    SInt32 Version(StaticFunctionTag*) { return 4; }

    // Flags kept outside the saves (user, 2026-11-02: "the welcome popup to tamriel merge pops up too many times": a global
    // lives in the save, so every new game showed it again): Documents\My Games\Skyrim Special Edition\SKSE\TamrielMerge.ini,
    // [Flags] name=1, beside SKSE's own logs.
    std::string FlagsPath()
    {
        char docs[MAX_PATH]{};
        if (FAILED(SHGetFolderPathA(nullptr, CSIDL_PERSONAL, nullptr, SHGFP_TYPE_CURRENT, docs))) return {};
        std::string dir = std::string(docs) + "\\My Games\\Skyrim Special Edition\\SKSE";
        SHCreateDirectoryExA(nullptr, dir.c_str(), nullptr);
        return dir + "\\TamrielMerge.ini";
    }

    bool GetFlag(StaticFunctionTag*, BSFixedString name)
    {
        std::string path = FlagsPath();
        return !path.empty() && name.data && *name.data && GetPrivateProfileIntA("Flags", name.data, 0, path.c_str()) != 0;
    }

    void SetFlag(StaticFunctionTag*, BSFixedString name, bool on)
    {
        std::string path = FlagsPath();
        if (path.empty() || !name.data || !*name.data) return;
        WritePrivateProfileStringA("Flags", name.data, on ? "1" : "0", path.c_str());
    }

    // ---- the free camera (Redguard's camera shots: a camera placed and aimed by a script) ----
    //
    // Skyrim's scripts can't place the camera; its free camera (the console's ToggleFreeCamera) can be anywhere. The
    // camera's state object (PlayerCamera::cameraStates[kCameraState_Free], SKSE's FreeCameraState) keeps, after its
    // input handler, seven floats at +0x30: the position (x, y, z) and the rotation (pitch at +0x3C, yaw at +0x40, in
    // radians), then the stick input. The self-test logs them beside the player's position and heading.

    FreeCameraState* FreeState()
    {
        PlayerCamera* cam = PlayerCamera::GetSingleton();
        return cam ? static_cast<FreeCameraState*>(cam->cameraStates[PlayerCamera::kCameraState_Free]) : nullptr;
    }

    bool InFreeCamera()
    {
        PlayerCamera* cam = PlayerCamera::GetSingleton();
        return cam && cam->cameraState && cam->cameraState == cam->cameraStates[PlayerCamera::kCameraState_Free];
    }

    // The camera's state changed with the game's own TESCamera::SetCameraState (SKSE 2.3.1: 0x558F90), to the free
    // state and back to the state it left (the console's ToggleFreeCamera, run through the command table, refused in
    // the game, 2026-11-11). On the game's own thread: Papyrus calls arrive on the script engine's.
    TESCameraState* g_stateBefore = nullptr;

    void ApplyFreeCamera(bool on)
    {
        PlayerCamera* cam = PlayerCamera::GetSingleton();
        if (!cam || InFreeCamera() == on) return;
        if (on)
        {
            g_stateBefore = cam->cameraState;
            CALL_MEMBER_FN(cam, SetCameraState)(cam->cameraStates[PlayerCamera::kCameraState_Free]);
        }
        else
        {
            TESCameraState* to = g_stateBefore ? g_stateBefore : cam->cameraStates[PlayerCamera::kCameraState_ThirdPerson2];
            g_stateBefore = nullptr;
            CALL_MEMBER_FN(cam, SetCameraState)(to);
        }
    }

    class FreeCameraTask : public TaskDelegate
    {
    public:
        explicit FreeCameraTask(bool on) : m_on(on) {}
        void Run() override { ApplyFreeCamera(m_on); }
        void Dispose() override { delete this; }
    private:
        bool m_on;
    };

    bool FreeCamera(StaticFunctionTag*, bool on)
    {
        if (!PlayerCamera::GetSingleton()) return false;
        if (InFreeCamera() == on) return true;
        if (!g_task) return false;
        g_task->AddTask(new FreeCameraTask(on));
        // The game runs the task on its next frame: waited for here (half a second at most).
        for (int i = 0; i < 100 && InFreeCamera() != on; ++i) Sleep(5);
        return InFreeCamera() == on;
    }

    bool IsFreeCamera(StaticFunctionTag*) { return InFreeCamera(); }

    bool SetFreeCamera(StaticFunctionTag*, float x, float y, float z, float pitch, float yaw)
    {
        FreeCameraState* s = FreeState();
        if (!s || !InFreeCamera()) return false;
        s->unk30[0] = x; s->unk30[1] = y; s->unk30[2] = z;
        s->unk30[3] = pitch; s->unk30[4] = yaw;
        return true;
    }

    float GetFreeCamera(StaticFunctionTag*, SInt32 i)
    {
        FreeCameraState* s = FreeState();
        return s && i >= 0 && i < 5 ? s->unk30[i] : 0.0f;
    }

    // Every reference of the player's cell written to the log as the game holds it now: FormID, base and its form
    // type, position, angles (degrees), the reference's flags and whether its 3D is loaded. For measuring where
    // things are in the game against where the plugin puts them (the console's readings by ID were not to be
    // trusted, 2026-11-12); run from the console: cgf "TMNative.DumpCell". Returns the number written.
    SInt32 DumpCell(StaticFunctionTag*)
    {
        PlayerCharacter* player = *g_thePlayer;
        TESObjectCELL* cell = player ? player->parentCell : nullptr;
        if (!cell) { _MESSAGE("DumpCell: no cell"); return 0; }
        _MESSAGE("DumpCell: cell %08X, player at %.1f %.1f %.1f angle %.1f", cell->formID, player->pos.x, player->pos.y, player->pos.z, player->rot.z * 57.29578f);
        SInt32 n = 0;
        TESObjectCELL::ReferenceData& d = cell->refData;
        if (!d.refArray) return 0;
        for (UInt32 i = 0; i < d.maxSize; ++i)
        {
            if (!d.refArray[i].unk08) continue;
            TESObjectREFR* r = d.refArray[i].ref;
            if (!r) continue;
            TESForm* b = r->baseForm;
            _MESSAGE("  ref %08X base %08X type %3u at %10.1f %10.1f %9.1f rot %6.1f %6.1f %6.1f flags %08X %s",
                r->formID, b ? b->formID : 0, b ? (unsigned)b->formType : 0u, r->pos.x, r->pos.y, r->pos.z,
                r->rot.x * 57.29578f, r->rot.y * 57.29578f, r->rot.z * 57.29578f, r->flags, r->GetNiNode() ? "3D" : "no 3D");
            ++n;
        }
        _MESSAGE("DumpCell: %d references", n);
        return n;
    }

    // A temporary log for the faces (user, 2026-11-15: "make a temp log for it"; a test's word "heads" in the .cmd
    // file): every person of the player's cell with the head parts its record lists, the ones its race gives by
    // default, and what the game has actually put on its head: each shape under the face node by name, with its
    // shader's kind, flags, textures and whether a tint is set. It shows at once whether the game took a person's own
    // face file (its shapes are named after the record's parts and no others) or built the head from parts itself.
    void LogHeadShapes(NiAVObject* o, int depth)
    {
        if (!o || depth > 6) return;
        if (BSGeometry* g = o->GetAsBSGeometry())
        {
            BSShaderProperty* sp = static_cast<BSShaderProperty*>(static_cast<NiProperty*>(g->m_spEffectState));
            BSLightingShaderMaterial* m = sp ? sp->material : nullptr;
            UInt32 kind = m ? m->GetShaderType() : 999u;
            _MESSAGE("      shape '%s' at %.1f %.1f %.1f, shader kind %u flags %08X %08X", o->m_name ? o->m_name : "",
                     o->m_worldTransform.pos.x, o->m_worldTransform.pos.y, o->m_worldTransform.pos.z, kind, sp ? sp->shaderFlags1 : 0u, sp ? sp->shaderFlags2 : 0u);
            if (m)
            {
                BSTextureSet* set = m->textureSet;
                if (set)
                    for (UInt32 i = 0; i < 8; ++i)
                    {
                        const char* t = set->GetTexturePath(i);
                        if (t && *t) _MESSAGE("        texture %u: %s", i, t);
                    }
                _MESSAGE("        diffuse %s, normal %s%s", m->texture1 ? "loaded" : "NOT loaded", m->texture2 ? "loaded" : "NOT loaded",
                         kind == 4 ? (static_cast<BSLightingShaderMaterialFacegen*>(m)->renderedTexture ? ", tint set" : ", NO tint") : "");
            }
        }
        if (NiNode* n = o->GetAsNiNode())
        {
            if (depth > 0) _MESSAGE("      node '%s'", o->m_name ? o->m_name : "");
            for (UInt32 i = 0; i < n->m_children.m_emptyRunStart; ++i) LogHeadShapes(n->m_children.m_data[i], depth + 1);
        }
    }

    const char* PartKind(UInt32 type)
    {
        static const char* names[] = { "Misc", "Face", "Eyes", "Hair", "FacialHair", "Scar", "Brows" };
        return type < 7 ? names[type] : "?";
    }

    void DumpHeads()
    {
        PlayerCharacter* player = *g_thePlayer;
        TESObjectCELL* cell = player ? player->parentCell : nullptr;
        if (!cell || !cell->refData.refArray) { _MESSAGE("DumpHeads: no cell"); return; }
        SInt32 n = 0;
        for (UInt32 i = 0; i < cell->refData.maxSize; ++i)
        {
            if (!cell->refData.refArray[i].unk08) continue;
            TESObjectREFR* r = cell->refData.refArray[i].ref;
            if (!r || !r->baseForm || r->baseForm->formType != kFormType_NPC) continue;
            TESNPC* npc = static_cast<TESNPC*>(r->baseForm);
            bool female = (npc->actorData.flags & 1) != 0;
            TESRace* race = npc->race.race;
            _MESSAGE("DumpHeads: ref %08X base %08X race %08X %s, %u head parts in its record", r->formID, npc->formID, race ? race->formID : 0, female ? "female" : "male", (unsigned)npc->numHeadParts);
            for (UInt32 k = 0; k < npc->numHeadParts; ++k)
            {
                BGSHeadPart* p = npc->headparts ? npc->headparts[k] : nullptr;
                if (!p) continue;
                _MESSAGE("    its part %08X '%s' %s%s, %u extra parts", p->formID, p->partName.data ? p->partName.data : "", PartKind(p->type), p->IsExtraPart() ? " (extra)" : "", p->extraParts.count);
                for (UInt32 e = 0; e < p->extraParts.count; ++e)
                    if (BGSHeadPart* x = p->extraParts[e]) _MESSAGE("      extra %08X '%s' %s", x->formID, x->partName.data ? x->partName.data : "", PartKind(x->type));
            }
            TESRace::CharGenData* cg = race ? race->chargenData[female ? 1 : 0] : nullptr;
            if (cg && cg->headParts)
                for (UInt32 k = 0; k < cg->headParts->count; ++k)
                    if (BGSHeadPart* p = (*cg->headParts)[k]) _MESSAGE("    race default %08X '%s' %s", p->formID, p->partName.data ? p->partName.data : "", PartKind(p->type));
            NiNode* root = r->GetNiNode();
            if (!root) { _MESSAGE("    no 3D"); ++n; continue; }
            const char* faceName = "BSFaceGenNiNodeSkinned";
            BSFixedString fixed(faceName);
            const char* key = fixed.data;
            NiAVObject* face = root->GetObjectByName(&key);
            if (!face) _MESSAGE("    no face node on its 3D");
            else { _MESSAGE("    on its head:"); LogHeadShapes(face, 0); }
            ++n;
        }
        _MESSAGE("DumpHeads: %d people", n);
    }

    // A temporary log for doors (the prisoner's cell gate, 2026-11-15: people stand before it open and are put on its
    // far side; a test's word "bodies <form ID>" in the .cmd file): each node of the reference's 3D that carries
    // collision, with the node's place and turn and its Havok body's, the body's layer and motion type, and whether
    // it is in the physics world. Shows whether the collision goes round with an opening door's leaves.
    // Offsets (Skyrim SE/AE x64; CommonLibSSE's layouts): NiAVObject+0x40 collision object; bhkNiCollisionObject
    // +0x10 its node, +0x18 flags, +0x20 bhkWorldObject; bhkRefObject+0x10 the hkpWorldObject; hkpWorldObject+0x10
    // its world, +0x20 hkpCdBody (+0x10 its hkMotionState: rotation 3 columns, translation at +0x30), +0x4C the
    // collision filter info (layer in the low 7 bits); hkpRigidBody's motion type byte 0x10 before its motion state.
    bool ReadBytes(const void* at, void* out, size_t n)
    {
        __try { memcpy(out, at, n); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    template <class T> bool Read(const UInt8* base, size_t offset, T& out) { return base && ReadBytes(base + offset, &out, sizeof(T)); }

    void LogBodies(NiAVObject* o, int depth)
    {
        if (!o || depth > 12) return;
        const UInt8* self = reinterpret_cast<const UInt8*>(o);
        UInt8* coll = nullptr;
        Read(self, 0x40, coll);
        if (coll)
        {
            UInt16 flags = 0; UInt8* world = nullptr; UInt8* hkp = nullptr; UInt8* inWorld = nullptr; UInt8* motion = nullptr; UInt32 filter = 0; UInt8 type = 0;
            float m[16]{};   // hkTransform: the rotation's three columns, then the translation (each 4 floats)
            Read(coll, 0x18, flags);
            Read(coll, 0x20, world);
            if (world) Read(world, 0x10, hkp);
            if (hkp) { Read(hkp, 0x10, inWorld); Read(hkp, 0x30, motion); Read(hkp, 0x4C, filter); }
            bool haveMotion = motion && ReadBytes(motion, m, sizeof(m));
            if (motion) Read(motion, static_cast<size_t>(-0x10), type);
            const NiTransform& w = o->m_worldTransform;
            float nodeYaw = atan2f(w.rot.data[1][0], w.rot.data[0][0]) * 57.29578f;
            // Havok's units are Skyrim's x 0.0142875 (1/69.99).
            const float k = 69.99125f;
            if (haveMotion)
                _MESSAGE("    '%s': node at %.1f %.1f %.1f turned %.1f; body at %.1f %.1f %.1f turned %.1f, layer %u, motion type %u, %s, collision flags %u",
                         o->m_name ? o->m_name : "", w.pos.x, w.pos.y, w.pos.z, nodeYaw, m[12] * k, m[13] * k, m[14] * k, atan2f(m[1], m[0]) * 57.29578f,
                         filter & 0x7F, type, inWorld ? "in the world" : "NOT in the world", flags);
            else
                _MESSAGE("    '%s': node at %.1f %.1f %.1f turned %.1f; collision object %p, body %p, no motion state read", o->m_name ? o->m_name : "",
                         w.pos.x, w.pos.y, w.pos.z, nodeYaw, coll, hkp);
        }
        if (NiNode* n = o->GetAsNiNode())
            for (UInt32 i = 0; i < n->m_children.m_emptyRunStart; ++i) LogBodies(n->m_children.m_data[i], depth + 1);
    }

    void DumpBodies(UInt32 formID)
    {
        TESForm* f = LookupFormByID(formID);
        TESObjectREFR* r = f ? DYNAMIC_CAST(f, TESForm, TESObjectREFR) : nullptr;
        if (!r) { _MESSAGE("DumpBodies: %08X is no reference", formID); return; }
        NiNode* root = r->GetNiNode();
        _MESSAGE("DumpBodies: %08X at %.1f %.1f %.1f, %s", formID, r->pos.x, r->pos.y, r->pos.z, root ? "its collision:" : "no 3D");
        if (root) LogBodies(root, 0);
    }

    bool RegisterFunctions(VMClassRegistry* registry)
    {
        registry->RegisterFunction(new NativeFunction0<StaticFunctionTag, SInt32>("DumpCell", "TMNative", DumpCell, registry));
        registry->RegisterFunction(new NativeFunction1<StaticFunctionTag, bool, bool>("FreeCamera", "TMNative", FreeCamera, registry));
        registry->RegisterFunction(new NativeFunction0<StaticFunctionTag, bool>("IsFreeCamera", "TMNative", IsFreeCamera, registry));
        registry->RegisterFunction(new NativeFunction5<StaticFunctionTag, bool, float, float, float, float, float>("SetFreeCamera", "TMNative", SetFreeCamera, registry));
        registry->RegisterFunction(new NativeFunction1<StaticFunctionTag, float, SInt32>("GetFreeCamera", "TMNative", GetFreeCamera, registry));
        registry->RegisterFunction(new NativeFunction4<StaticFunctionTag, float, BSFixedString, TESObjectREFR*, TESForm*, SInt32>("Condition", "TMNative", Condition, registry));
        registry->RegisterFunction(new NativeFunction5<StaticFunctionTag, bool, BSFixedString, TESObjectREFR*, float, float, SInt32>("Command", "TMNative", Command, registry));
        registry->RegisterFunction(new NativeFunction2<StaticFunctionTag, bool, BSFixedString, bool>("HasFunction", "TMNative", HasFunction, registry));
        registry->RegisterFunction(new NativeFunction0<StaticFunctionTag, SInt32>("Version", "TMNative", Version, registry));
        registry->RegisterFunction(new NativeFunction1<StaticFunctionTag, bool, BSFixedString>("GetFlag", "TMNative", GetFlag, registry));
        registry->RegisterFunction(new NativeFunction2<StaticFunctionTag, void, BSFixedString, bool>("SetFlag", "TMNative", SetFlag, registry));
        registry->SetFunctionFlags("TMNative", "HasFunction", VMClassRegistry::kFunctionFlag_NoWait);
        registry->SetFunctionFlags("TMNative", "Version", VMClassRegistry::kFunctionFlag_NoWait);
        _MESSAGE("TMNative: 11 functions registered");
        return true;
    }

    // ---- the self-test ----

    std::string IniPath()
    {
        char path[MAX_PATH]{};
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCSTR>(&IniPath), &self);
        GetModuleFileNameA(self, path, MAX_PATH);
        std::string s(path);
        return s.substr(0, s.find_last_of("\\/") + 1) + "TamrielMergeNative.ini";
    }

    // The functions the converted scripts use: each must be found with the handler they call.
    const char* kConditions[] = { "GetKnockedState", "GetKnockStateEnum", "GetWeaponAnimType", "IsIdlePlaying", "GetCurrentAIProcedure", "GetCurrentAIPackage",
                                  "IsSpellTarget", "GetDetected", "GetLineOfSight", "IsSneaking" };
    const char* kCommands[] = { "SetForceRun", "SetForceSneak", "PickIdle", "TriggerHitShader", "SetRigidBodyMass", "SetPackDuration",
                                "SetSceneIsComplex", "ResetFallDamageTimer", "EnableLinkedPathPoints", "SetAllVisible", "SetAllReachable", "SetNoAvoidance" };

    void SelfTest()
    {
        int found = 0, missing = 0;
        for (const char* n : kConditions) { ObScriptCommand* c = Find(n); if (c && c->eval) ++found; else { ++missing; _MESSAGE("test: condition %s missing", n); } }
        for (const char* n : kCommands) { ObScriptCommand* c = Find(n); if (c && c->execute) ++found; else { ++missing; _MESSAGE("test: command %s missing", n); } }
        _MESSAGE("test: %d of %d functions found", found, found + missing);
        PlayerCharacter* player = *g_thePlayer;
        if (!player) { _MESSAGE("test: no player"); return; }
        TESObjectREFR* p = player;
        for (const char* n : { "GetKnockedState", "GetKnockStateEnum", "GetWeaponAnimType", "IsIdlePlaying", "GetCurrentAIProcedure", "IsSneaking" })
        {
            double v = 0.0;
            bool ok = EvalCondition(Find(n), p, nullptr, 0, v);
            _MESSAGE("test: %s on the player = %g (%s)", n, v, ok ? "ok" : "failed");
        }
        // SetForceRun's handler sets bit 1 of the actor's word at +0xCC for a nonzero argument and clears it for 0
        // (its code, 2026-10-23): read back, it shows the argument arrived.
        UInt32* flags = reinterpret_cast<UInt32*>(reinterpret_cast<UInt8*>(player) + 0xCC);
        UInt32 before = *flags;
        double r = 0.0;
        bool on = RunCommand(Find("SetForceRun"), p, 1.0f, 0.0f, -1, r);
        UInt32 afterOn = *flags;
        bool off = RunCommand(Find("SetForceRun"), p, 0.0f, 0.0f, -1, r);
        UInt32 afterOff = *flags;
        if (before & 2) RunCommand(Find("SetForceRun"), p, 1.0f, 0.0f, -1, r);   // as it was
        // The free camera: on, where the game put it (against the player's place and heading), a place of ours written
        // and read back, and off again.
        {
            bool wasFree = InFreeCamera();
            ApplyFreeCamera(true);
            bool onOk = InFreeCamera();
            FreeCameraState* s = FreeState();
            if (s)
            {
                _MESSAGE("test: free camera on %s; camera %.1f %.1f %.1f pitch %.3f yaw %.3f; player %.1f %.1f %.1f heading %.3f",
                         onOk ? "ok" : "FAILED", s->unk30[0], s->unk30[1], s->unk30[2], s->unk30[3], s->unk30[4], p->pos.x, p->pos.y, p->pos.z, p->rot.z);
                bool set = SetFreeCamera(nullptr, p->pos.x + 300.0f, p->pos.y, p->pos.z + 150.0f, 0.25f, -1.5708f);
                _MESSAGE("test: free camera set %s; reads %.1f %.1f %.1f pitch %.3f yaw %.3f", set ? "ok" : "FAILED",
                         GetFreeCamera(nullptr, 0), GetFreeCamera(nullptr, 1), GetFreeCamera(nullptr, 2), GetFreeCamera(nullptr, 3), GetFreeCamera(nullptr, 4));
            }
            else _MESSAGE("test: no free camera state");
            if (!wasFree && GetPrivateProfileIntA("Test", "KeepFreeCamera", 0, IniPath().c_str()) != 1)
            {
                ApplyFreeCamera(false);
                bool offOk = !InFreeCamera();
                _MESSAGE("test: free camera off %s", offOk ? "ok" : "FAILED");
            }
        }
        _MESSAGE("test: SetForceRun 1 -> %s, flag %08X -> %08X; SetForceRun 0 -> %s, flag %08X: %s", on ? "ok" : "failed", before, afterOn,
                 off ? "ok" : "failed", afterOff, (afterOn & 2) && !(afterOff & 2) ? "PASS" : "FAIL");
    }

    // [Test] Watch=1: a file TamrielMergeNative.cmd appearing beside the plugin is taken away and the player's cell
    // written to the log (DumpCell) on the game's own thread: a test's way to ask from outside the game (Skyrim's
    // console can't call a script's global function).
    class DumpTask : public TaskDelegate
    {
    public:
        void Run() override { DumpCell(nullptr); }
        void Dispose() override { delete this; }
    };

    class HeadsTask : public TaskDelegate
    {
    public:
        void Run() override { DumpHeads(); }
        void Dispose() override { delete this; }
    };

    class BodiesTask : public TaskDelegate
    {
    public:
        explicit BodiesTask(UInt32 id) : formID(id) {}
        void Run() override { DumpBodies(formID); }
        void Dispose() override { delete this; }
    private:
        UInt32 formID;
    };

    // The menus a test needs to know about, written to TamrielMergeNative.state beside the plugin (1 open, 0 not), with
    // a count that goes up each time: the console (its key toggles it, so a test must know which way), the loading
    // screen, the main menu, a message box, dialogue, the race menu.
    volatile LONG g_stateCount = 0;

    struct StateTask
    {
        // Read on the watcher's own thread: it must answer under menus and loading screens, when the game's task
        // queue stands still.
        void Run()
        {
            MenuManager* mm = MenuManager::GetSingleton();
            const char* names[] = { "Console", "Loading Menu", "Main Menu", "MessageBoxMenu", "Dialogue Menu", "RaceSex Menu", "Fader Menu", "Cursor Menu" };
            std::string out = "count=" + std::to_string(InterlockedIncrement(&g_stateCount)) + "\n";
            for (const char* n : names)
            {
                BSFixedString s(n);
                out += std::string(n) + "=" + (mm && mm->IsMenuOpen(&s) ? "1" : "0") + "\n";
            }
            PlayerCharacter* player = *g_thePlayer;
            if (player)
            {
                char line[160];
                sprintf_s(line, "player=%.1f %.1f %.1f %.1f\ncell=%08X\n", player->pos.x, player->pos.y, player->pos.z, player->rot.z * 57.29578f, player->parentCell ? player->parentCell->formID : 0);
                out += line;
            }
            std::string ini = IniPath();
            std::string path = ini.substr(0, ini.size() - 4) + ".state";
            FILE* f = nullptr;
            if (fopen_s(&f, path.c_str(), "wb") == 0 && f) { fwrite(out.data(), 1, out.size(), f); fclose(f); }
        }
    };

    DWORD WINAPI Watch(LPVOID)
    {
        std::string ini = IniPath();
        std::string cmd = ini.substr(0, ini.size() - 4) + ".cmd";
        FILETIME seen{};
        for (;;)
        {
            Sleep(300);
            WIN32_FILE_ATTRIBUTE_DATA d{};
            if (!GetFileAttributesExA(cmd.c_str(), GetFileExInfoStandard, &d)) continue;
            bool same = CompareFileTime(&d.ftLastWriteTime, &seen) == 0;
            seen = d.ftLastWriteTime;
            char word[16]{};
            FILE* f = nullptr;
            if (fopen_s(&f, cmd.c_str(), "rb") == 0 && f) { fread(word, 1, sizeof(word) - 1, f); fclose(f); }
            DeleteFileA(cmd.c_str());
            if (same || !g_task) continue;
            if (strncmp(word, "state", 5) == 0) StateTask().Run();
            else if (strncmp(word, "heads", 5) == 0) g_task->AddTask(new HeadsTask());
            else if (strncmp(word, "bodies ", 7) == 0) g_task->AddTask(new BodiesTask(strtoul(word + 7, nullptr, 16)));
            else g_task->AddTask(new DumpTask());
        }
    }

    void OnMessage(SKSEMessagingInterface::Message* msg)
    {
        if (msg->type == SKSEMessagingInterface::kMessage_PostLoadGame && GetPrivateProfileIntA("Test", "RunOnLoad", 0, IniPath().c_str()) == 1)
            SelfTest();
    }
}

extern "C"
{
    __declspec(dllexport) SKSEPluginVersionData SKSEPlugin_Version =
    {
        SKSEPluginVersionData::kVersion,
        3,                          // 2: GetFlag and SetFlag (TMTutorial asks for 2 with SKSE.GetPluginVersion); 3: the free camera
        "TamrielMergeNative",
        "TamrielMerge",
        "",
        0,
        0,
        { RUNTIME_VERSION_1_7_104, 0 },
        0,
    };

    __declspec(dllexport) bool SKSEPlugin_Load(const SKSEInterface* skse)
    {
        // Beside the plugin, as TamrielMerge's other plugins log (the game check reads it there).
        std::string ini = IniPath();
        gLog.Open((ini.substr(0, ini.size() - 4) + ".log").c_str());
        _MESSAGE("TamrielMergeNative 1, runtime %08X", skse->runtimeVersion);
        if (skse->isEditor) return false;
        g_plugin = skse->GetPluginHandle();
        g_papyrus = static_cast<SKSEPapyrusInterface*>(skse->QueryInterface(kInterface_Papyrus));
        g_messaging = static_cast<SKSEMessagingInterface*>(skse->QueryInterface(kInterface_Messaging));
        g_task = static_cast<SKSETaskInterface*>(skse->QueryInterface(kInterface_Task));
        if (!g_papyrus || !g_papyrus->Register(RegisterFunctions)) { _MESSAGE("no Papyrus interface"); return false; }
        if (g_messaging) g_messaging->RegisterListener(g_plugin, "SKSE", OnMessage);
        // The tables' first names, logged: the same as the executable's own (obtable.py reads them offline).
        if (g_task && GetPrivateProfileIntA("Test", "Watch", 0, ini.c_str()) == 1) CreateThread(nullptr, 0, Watch, nullptr, 0, nullptr);
        _MESSAGE("tables: script[0] %s, console[0] %s", g_firstObScriptCommand->longName ? g_firstObScriptCommand->longName : "?",
                 g_firstConsoleCommand->longName ? g_firstConsoleCommand->longName : "?");
        return true;
    }
}
