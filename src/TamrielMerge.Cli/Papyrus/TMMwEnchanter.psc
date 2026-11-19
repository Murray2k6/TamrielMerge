ScriptName TMMwEnchanter extends Actor
{A Morrowind enchanter (an NPC with the Enchanting service in AIDT; MwEnchanters): its record's Enchant skill, Intelligence
and Luck for the services of TMMwEnchanting.}

Quest Property Service Auto
{MWEnchantingService (TMMwEnchanting).}
Int Property Enchant = 0 Auto
Int Property Intelligence = 0 Auto
Int Property Luck = 0 Auto

Function OpenEnchanting()
  TMMwEnchanting e = Service as TMMwEnchanting
  If e
    e.OpenTable(Self, Enchant)
  EndIf
EndFunction

Function OfferRecharge()
  TMMwEnchanting e = Service as TMMwEnchanting
  If e
    e.Recharge(Self, Enchant, Intelligence, Luck)
  EndIf
EndFunction
