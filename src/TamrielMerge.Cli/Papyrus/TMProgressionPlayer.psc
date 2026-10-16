ScriptName TMProgressionPlayer extends ReferenceAlias
{The player, for TMProgression: key and menu events don't survive loading a save, so they are registered again; and
what the new skills rise with (TMSkills): spells cast, being struck, what is worn, gold changing hands.}

Event OnInit()
  AddInventoryEventFilter(Game.GetFormFromFile(0x00000F, "Skyrim.esm"))
EndEvent

Event OnPlayerLoadGame()
  RemoveAllInventoryEventFilters()
  AddInventoryEventFilter(Game.GetFormFromFile(0x00000F, "Skyrim.esm"))
  (GetOwningQuest() as TMProgression).Setup()
EndEvent

Event OnSpellCast(Form akSpell)
  TMSkills skills = GetOwningQuest() as TMSkills
  If skills
    skills.SpellCast(akSpell)
  EndIf
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  TMSkills skills = GetOwningQuest() as TMSkills
  If skills
    skills.Struck(akAggressor, akSource)
  EndIf
EndEvent

Event OnObjectEquipped(Form akBaseObject, ObjectReference akReference)
  If akBaseObject as Armor
    (GetOwningQuest() as TMSkills).UpdateArmor()
  EndIf
EndEvent

Event OnObjectUnequipped(Form akBaseObject, ObjectReference akReference)
  If akBaseObject as Armor
    (GetOwningQuest() as TMSkills).UpdateArmor()
  EndIf
EndEvent

; Gold only (the filter): Mercantile counts the coins while the barter menu is open.
Event OnItemAdded(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akSourceContainer)
  (GetOwningQuest() as TMSkills).GoldChanged(aiItemCount)
EndEvent

Event OnItemRemoved(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akDestContainer)
  (GetOwningQuest() as TMSkills).GoldChanged(aiItemCount)
EndEvent
