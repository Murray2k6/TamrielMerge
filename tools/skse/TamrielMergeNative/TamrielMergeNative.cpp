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

#include <shlobj.h>
#include <cstring>
#include <cstdio>
#include <string>
#include <vector>

IDebugLog gLog;

namespace
{
    PluginHandle g_plugin = kPluginHandle_Invalid;
    SKSEPapyrusInterface* g_papyrus = nullptr;
    SKSEMessagingInterface* g_messaging = nullptr;

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

    SInt32 Version(StaticFunctionTag*) { return 1; }

    bool RegisterFunctions(VMClassRegistry* registry)
    {
        registry->RegisterFunction(new NativeFunction4<StaticFunctionTag, float, BSFixedString, TESObjectREFR*, TESForm*, SInt32>("Condition", "TMNative", Condition, registry));
        registry->RegisterFunction(new NativeFunction5<StaticFunctionTag, bool, BSFixedString, TESObjectREFR*, float, float, SInt32>("Command", "TMNative", Command, registry));
        registry->RegisterFunction(new NativeFunction2<StaticFunctionTag, bool, BSFixedString, bool>("HasFunction", "TMNative", HasFunction, registry));
        registry->RegisterFunction(new NativeFunction0<StaticFunctionTag, SInt32>("Version", "TMNative", Version, registry));
        registry->SetFunctionFlags("TMNative", "HasFunction", VMClassRegistry::kFunctionFlag_NoWait);
        registry->SetFunctionFlags("TMNative", "Version", VMClassRegistry::kFunctionFlag_NoWait);
        _MESSAGE("TMNative: 4 functions registered");
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
        _MESSAGE("test: SetForceRun 1 -> %s, flag %08X -> %08X; SetForceRun 0 -> %s, flag %08X: %s", on ? "ok" : "failed", before, afterOn,
                 off ? "ok" : "failed", afterOff, (afterOn & 2) && !(afterOff & 2) ? "PASS" : "FAIL");
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
        1,
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
        if (!g_papyrus || !g_papyrus->Register(RegisterFunctions)) { _MESSAGE("no Papyrus interface"); return false; }
        if (g_messaging) g_messaging->RegisterListener(g_plugin, "SKSE", OnMessage);
        // The tables' first names, logged: the same as the executable's own (obtable.py reads them offline).
        _MESSAGE("tables: script[0] %s, console[0] %s", g_firstObScriptCommand->longName ? g_firstObScriptCommand->longName : "?",
                 g_firstConsoleCommand->longName ? g_firstConsoleCommand->longName : "?");
        return true;
    }
}
