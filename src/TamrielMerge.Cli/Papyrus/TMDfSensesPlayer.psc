ScriptName TMDfSensesPlayer extends ReferenceAlias
{The player, for TMDfSensesControl: menu registrations don't survive loading a save, so they are made again; and a hit
from one of Daggerfall's monsters (not a spell) is passed on for what its kind does to the player (Daggerfall Unity
FormulaHelper.OnMonsterHit: diseases, paralysis, fatigue, vampirism).}

Event OnPlayerLoadGame()
  (GetOwningQuest() as TMDfSensesControl).Setup()
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  TMDfSenses monster = akAggressor as TMDfSenses
  If !monster || monster.Mobile < 0 || akSource as Spell || akSource as Enchantment
    Return
  EndIf
  (GetOwningQuest() as TMDfSensesControl).MonsterHit(monster, monster.Mobile)
EndEvent
