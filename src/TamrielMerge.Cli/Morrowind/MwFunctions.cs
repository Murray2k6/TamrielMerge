namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's script functions with how many arguments each takes (Morrowind, Tribunal and Bloodmoon), which the
/// parser needs: arguments follow a function without parentheses, so "GetPos z - 5" is (GetPos z) - 5 only because
/// GetPos takes one. Names are case-insensitive. Functions that take a variable number (MessageBox, Choice, AIWander's
/// idles) take what follows on the line.
/// </summary>
public static class MwFunctions
{
    public sealed record Info(string Name, int MinArgs, int MaxArgs);

    static readonly Dictionary<string, Info> byName = new(StringComparer.OrdinalIgnoreCase);

    public static Info? Find(string name) => byName.GetValueOrDefault(name);
    public static IEnumerable<Info> All => byName.Values;

    // Name:min:max, or Name alone for a function without arguments.
    const string Table = """
        Activate:0:1 AddItem:2:2 AddSoulGem:2:2 AddSpell:1:1 AddTopic:1:1 AIActivate:1:2 AIEscort:5:6 AIEscortCell:6:7
        AIFollow:5:6 AIFollowCell:6:7 AITravel:3:4 AIWander:1:99 BecomeWerewolf Cast:2:2 CellChanged CenterOnCell:1:1
        CenterOnExterior:2:2 ChangeWeather:2:2 Choice:1:99 ClearForceJump ClearForceMoveJump ClearForceRun ClearForceSneak
        ClearInfoActor COC:1:1 COE:2:2 Disable DisableLevitation DisablePlayerControls DisablePlayerFighting DisablePlayerJumping
        DisablePlayerLooking DisablePlayerMagic DisablePlayerViewSwitch DisableTeleporting DisableVanityMode DontSaveObject
        Drop:1:2 DropSoulGem:1:1 Enable EnableBirthMenu EnableClassMenu EnableInventoryMenu EnableLevelUpMenu EnableLevitation
        EnableMagicMenu EnableMapMenu EnableNameMenu EnablePlayerControls EnablePlayerFighting EnablePlayerJumping
        EnablePlayerLooking EnablePlayerMagic EnablePlayerViewSwitch EnableRaceMenu EnableRest EnableStatReviewMenu EnableStatsMenu
        EnableTeleporting EnableVanityMode Equip:1:1 ExplodeSpell:1:1 Face:2:3 FadeIn:1:1 FadeOut:1:1 FadeTo:2:2 Fall ForceGreeting
        ForceJump ForceMoveJump ForceRun ForceSneak GetAcrobatics GetAgility GetAIPackageDone GetAlarm GetAlchemy GetAlteration
        GetAngle:1:1 GetArmorBonus GetArmorer GetArmorType:1:1 GetAthletics GetAttackBonus GetAttacked GetAxe GetBlightDisease
        GetBlindness GetBlock GetBluntWeapon GetButtonPressed GetCastPenalty GetChameleon GetCollidingActor GetCollidingPC
        GetCommonDisease GetConjuration GetCurrentAIPackage GetCurrentTime GetCurrentWeather GetDeadCount:1:1 GetDefendBonus
        GetDestruction GetDetected:1:1 GetDisabled GetDisposition GetDistance:1:1 GetEffect:1:1 GetEnchant GetEndurance GetFactionReaction:2:2
        GetFatigue GetFatigueGetRatio GetFight GetFlee GetFlying GetForceJump GetForceMoveJump GetForceRun GetForceSneak GetHandToHand
        GetHealth GetHealthGetRatio GetHeavyArmor GetHello GetIllusion GetIntelligence GetInterior GetItemCount:1:1
        GetJournalIndex:1:1 GetLevel GetLightArmor GetLineOfSight:1:1 GetLOS:1:1 GetLocked GetLongBlade GetLuck GetMagicka
        GetMagickaGetRatio GetMarksman GetMasserPhase GetMediumArmor GetMercantile GetMysticism GetParalysis GetPCCell:1:1
        GetPCCrimeLevel GetPCFacRep:0:1 GetPCInJail GetPCJumping GetPCRank:0:1 GetPCRunning GetPCSleep GetPCSneaking
        GetPCTraveling GetPCVisionBonus GetPersonality GetPlayerControlsDisabled GetPlayerFightingDisabled GetPlayerJumpingDisabled
        GetPlayerLookingDisabled GetPlayerMagicDisabled GetPlayerViewSwitch GetPos:1:1 GetRace:1:1 GetReputation GetResistBlight
        GetResistCorprus GetResistDisease GetResistFire GetResistFrost GetResistMagicka GetResistNormalWeapons GetResistParalysis
        GetResistPoison GetResistShock GetRestoration GetScale GetSecondsPassed GetSecundaPhase GetSecurity GetShortBlade
        GetSilence GetSneak GetSoundPlaying:1:1 GetSpear GetSpeechcraft GetSpeed GetSpell:1:1 GetSpellEffects:1:1
        GetSpellReadied GetSpellReadied GetSquareRoot:1:1 GetStandingActor GetStandingPC GetStartingAngle:1:1 GetStartingPos:1:1
        GetStrength GetSuperJump GetSwimSpeed GetTarget:1:1 GetUnarmored GetVanityModeDisabled GetWaterBreathing GetWaterLevel
        GetWaterWalking GetWeaponDrawn GetWeaponType GetWerewolfKills GetWillpower GetWindSpeed GoToJail HasItemEquipped:1:1
        HasSoulGem:1:1 HitAttemptOnMe:1:1 HitOnMe:1:1 HurtCollidingActor:1:1 HurtStandingActor:1:1 IsWerewolf Journal:2:3
        Lock:0:1 LoopGroup:2:3 LowerRank MenuMode MenuTest:0:1 ModAcrobatics:1:1 ModAgility:1:1 ModAlarm:1:1 ModAlchemy:1:1
        ModAlteration:1:1 ModArmorBonus:1:1 ModArmorer:1:1 ModAthletics:1:1 ModAttackBonus:1:1 ModAxe:1:1 ModBlindness:1:1
        ModBlock:1:1 ModBluntWeapon:1:1 ModCastPenalty:1:1 ModChameleon:1:1 ModConjuration:1:1 ModCurrentFatigue:1:1
        ModCurrentHealth:1:1 ModCurrentMagicka:1:1 ModDefendBonus:1:1 ModDestruction:1:1 ModDisposition:1:1 ModEnchant:1:1
        ModEndurance:1:1 ModFactionReaction:3:3 ModFatigue:1:1 ModFight:1:1 ModFlee:1:1 ModFlying:1:1 ModHandToHand:1:1
        ModHealth:1:1 ModHeavyArmor:1:1 ModHello:1:1 ModIllusion:1:1 ModIntelligence:1:1 ModInvisible:1:1 ModLightArmor:1:1
        ModLongBlade:1:1 ModLuck:1:1 ModMagicka:1:1 ModMarksman:1:1 ModMediumArmor:1:1 ModMercantile:1:1 ModMysticism:1:1
        ModParalysis:1:1 ModPCCrimeLevel:1:1 ModPCFacRep:1:2 ModPersonality:1:1 ModRegion:2:12 ModReputation:1:1
        ModResistBlight:1:1 ModResistCorprus:1:1 ModResistDisease:1:1 ModResistFire:1:1 ModResistFrost:1:1 ModResistMagicka:1:1
        ModResistNormalWeapons:1:1 ModResistParalysis:1:1 ModResistPoison:1:1 ModResistShock:1:1 ModRestoration:1:1
        ModScale:1:1 ModSecurity:1:1 ModShortBlade:1:1 ModSilence:1:1 ModSneak:1:1 ModSpear:1:1 ModSpeechcraft:1:1 ModSpeed:1:1
        ModStrength:1:1 ModSuperJump:1:1 ModSwimSpeed:1:1 ModUnarmored:1:1 ModWaterBreathing:1:1 ModWaterLevel:1:1
        ModWaterWalking:1:1 ModWillpower:1:1 Move:2:2 MoveWorld:2:2 OnActivate OnDeath OnKnockout OnMurder OnPCAdd OnPCDrop
        OnPCEquip OnPCHitMe OnPCRepair OnPCSoulGemUse OnRepair PayFine PayFineThief PCClearExpelled:0:1 PCExpell:0:1
        PCExpelled:0:1 PCForce1stPerson PCForce3rdPerson PCGet3rdPerson PCJoinFaction:0:1 PCLowerRank:0:1 PCRaiseRank:0:1
        PlaceAtMe:4:4 PlaceAtPC:4:4 PlaceItem:5:5 PlaceItemCell:6:6 PlayBink:2:2 PlayGroup:1:2 PlayLoopSound3D:1:1
        PlayLoopSound3DVP:3:3 PlaySound:1:1 PlaySound3D:1:1 PlaySound3DVP:3:3 PlaySoundVP:3:3 Position:4:4 PositionCell:5:5
        RaiseRank Random:1:1 RemoveEffects:1:1 RemoveItem:2:2 RemoveSoulGem:1:2 RemoveSpell:1:1 RemoveSpellEffects:1:1
        RepairedOnMe:1:1 ResetActors Resurrect RotateWorld:2:2 Rotate:2:2 SameFaction Say:2:2 SayDone ScriptRunning:1:1
        SetAcrobatics:1:1 SetAgility:1:1 SetAlarm:1:1 SetAlchemy:1:1 SetAlteration:1:1 SetAngle:2:2 SetArmorBonus:1:1
        SetArmorer:1:1 SetAthletics:1:1 SetAtStart SetAttackBonus:1:1 SetAxe:1:1 SetBlindness:1:1 SetBlock:1:1 SetBluntWeapon:1:1
        SetCastPenalty:1:1 SetChameleon:1:1 SetConjuration:1:1 SetDefendBonus:1:1 SetDelete:1:1 SetDestruction:1:1
        SetDisposition:1:1 SetEnchant:1:1 SetEndurance:1:1 SetFactionReaction:3:3 SetFatigue:1:1 SetFight:1:1 SetFlee:1:1
        SetFlying:1:1 SetHandToHand:1:1 SetHealth:1:1 SetHeavyArmor:1:1 SetHello:1:1 SetIllusion:1:1 SetIntelligence:1:1
        SetInvisible:1:1 SetJournalIndex:2:2 SetLevel:1:1 SetLightArmor:1:1 SetLongBlade:1:1 SetLuck:1:1 SetMagicka:1:1
        SetMarksman:1:1 SetMediumArmor:1:1 SetMercantile:1:1 SetMysticism:1:1 SetParalysis:1:1 SetPCCrimeLevel:1:1
        SetPCFacRep:1:2 SetPersonality:1:1 SetPos:2:2 SetReputation:1:1 SetResistBlight:1:1 SetResistCorprus:1:1
        SetResistDisease:1:1 SetResistFire:1:1 SetResistFrost:1:1 SetResistMagicka:1:1 SetResistNormalWeapons:1:1
        SetResistParalysis:1:1 SetResistPoison:1:1 SetResistShock:1:1 SetRestoration:1:1 SetScale:1:1 SetSecurity:1:1
        SetShortBlade:1:1 SetSilence:1:1 SetSneak:1:1 SetSpear:1:1 SetSpeechcraft:1:1 SetSpeed:1:1 SetStrength:1:1
        SetSuperJump:1:1 SetSwimSpeed:1:1 SetUnarmored:1:1 SetWaterBreathing:1:1 SetWaterLevel:1:1 SetWaterWalking:1:1
        SetWerewolfAcrobatics SetWillpower:1:1 ShowMap:1:1 ShowRestMenu SkipAnim StartCombat:1:1 StartScript:1:1 StopCombat
        StopScript:1:1 StopSound:1:1 StreamMusic:1:1 TurnMoonRed TurnMoonWhite UndoWerewolf Unlock WakeUpPC Xbox
        Goodbye ModCurrentFatigue:1:1 GetHealthGetRatio GetSecondsPassed GetWeaponDrawn ForceGreeting MessageBox:1:99
        ToggleAI TAI ToggleCollision TCL ToggleGodMode TGM ToggleMenus TM FixMe OutputObjCounts SetWerewolfAcrobatics
        GetDeadCount:1:1 GetSoundPlaying:1:1 GetEffect:1:1 GetButtonPressed GetStat:1:1 StopCombat
        """;

    static MwFunctions()
    {
        foreach (var entry in Table.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = entry.Split(':');
            var info = parts.Length == 3 ? new Info(parts[0], int.Parse(parts[1]), int.Parse(parts[2])) : new Info(parts[0], 0, 0);
            byName[info.Name] = info;
        }
    }
}
