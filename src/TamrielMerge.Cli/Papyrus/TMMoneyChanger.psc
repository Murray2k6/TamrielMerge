ScriptName TMMoneyChanger extends Quest
{Money changers and, with IsBank, Daggerfall's banks (docs/progression.md, "Coin by game"). TMExchangeTopic opens the
Tamriel menu's Coin tab with this service, and the menu's buttons come back as mod event TM_Service (what, amount).
- Exchange: any other time's purse into the coin of this time (Story), for BaseFee% less 1% per 20 Speech (at least 2%).
- A Daggerfall bank (Daggerfall Unity's DaggerfallBankManager) keeps an account in each region, found from the town the
  player is in (TMDfTalk): deposits and withdrawals; letters of credit written from the account for 1% more (Letter of
  Credit items, one per gold piece of credit) and cashed into an account at any bank; and loans of up to level x 50,000,
  repaid with 10% within 360 days. An unpaid loan defaults, and that region's bank lends nothing more until it is paid.}

Int Property Story Auto
{Whose coin this changer pays out: 0 Daggerfall, 1 Redguard, 2 Morrowind, 3 Oblivion, 4 Skyrim.}
Quest Property StoryOrderQuest Auto
Int Property BaseFee = 10 Auto
String Property Title = "Money changer" Auto
Bool Property IsBank = False Auto
MiscObject Property LetterOfCredit Auto
Quest Property Talk Auto
Int[] Property TownRegions Auto
String[] Property RegionNames Auto
TMMoneyChanger Property Accounts Auto
{The bank that keeps the accounts, when it isn't this one: towns built in batches ("Daggerfall Towns N.esm") have their
own conversation quests, and their banks keep the player's accounts in Daggerfall.esm's, so every bank shares them.}

Int[] Property Account Auto Hidden
Int[] Property Loan Auto Hidden
Float[] Property LoanDue Auto Hidden
Bool[] Property Defaulted Auto Hidden
Bool Property Serving = False Auto Hidden
Int Property Region = -1 Auto Hidden

String Property MenuName = "CustomMenu" AutoReadOnly
Float Property LoanDays = 360.0 AutoReadOnly

TMStoryOrder Function Order()
  Return StoryOrderQuest as TMStoryOrder
EndFunction

Function Open(Actor changer)
  If SKSE.GetVersion() <= 0
    Debug.MessageBox("Changing coin needs SKSE.")
    Return
  EndIf
  Region = -1
  If IsBank
    EnsureAccounts()
    TMDfTalk towns = Talk as TMDfTalk
    If towns && TownRegions
      Int town = towns.TownIndex()
      If town >= 0 && town < TownRegions.Length
        Region = TownRegions[town]
      EndIf
    EndIf
    CheckLoan()
  EndIf
  Serving = True
  RegisterForModEvent("TM_MenuReady", "OnServiceReady")
  RegisterForModEvent("TM_Service", "OnService")
  RegisterForModEvent("TM_MenuClose", "OnServiceClose")
  UI.OpenCustomMenu("TamrielMerge/tamriel")
EndFunction

Event OnServiceReady(String eventName, String strArg, Float numArg, Form sender)
  If Serving
    SendService()
  EndIf
EndEvent

Event OnService(String eventName, String strArg, Float numArg, Form sender)
  If !Serving
    Return
  EndIf
  String said = Serve(strArg, numArg as Int)
  TMStoryOrder order = Order()
  If order
    order.SendMoneyToMenu()
  EndIf
  SendService()
  UI.InvokeString(MenuName, "_root.ServiceStatus", said)
EndEvent

Event OnServiceClose(String eventName, String strArg, Float numArg, Form sender)
  Serving = False
  UnregisterForModEvent("TM_MenuReady")
  UnregisterForModEvent("TM_Service")
  UnregisterForModEvent("TM_MenuClose")
EndEvent

Int Function Fee()
  Int fee = BaseFee - (Game.GetPlayer().GetActorValue("Speechcraft") as Int) / 20
  If fee < 2
    fee = 2
  EndIf
  Return fee
EndFunction

Bool Function AtBank()
  Return IsBank && Region >= 0 && Account && Region < Account.Length
EndFunction

; The menu's service panel: "kind|title|fee", then for a bank "account|loan|days left|overdue|letters|most you may
; borrow|defaulted".
Function SendService()
  If AtBank()
    String place = "your region"
    If RegionNames && Region < RegionNames.Length
      place = RegionNames[Region]
    EndIf
    UI.InvokeString(MenuName, "_root.OpenService", "bank|The Bank of " + place + "|" + Fee())
    Int days = (LoanDue[Region] - Utility.GetCurrentGameTime()) as Int
    Int overdue = 0
    If Loan[Region] > 0 && days < 0
      overdue = 1
    EndIf
    Int refused = 0
    If Defaulted[Region]
      refused = 1
    EndIf
    Int letters = 0
    If LetterOfCredit
      letters = Game.GetPlayer().GetItemCount(LetterOfCredit)
    EndIf
    UI.InvokeString(MenuName, "_root.BankState", Account[Region] + "|" + Loan[Region] + "|" + days + "|" + overdue + "|" + letters + "|" + MaxLoan() + "|" + refused)
  Else
    UI.InvokeString(MenuName, "_root.OpenService", "exchange|" + Title + "|" + Fee())
  EndIf
EndFunction

Int Function Least(Int a, Int b)
  If a < b
    Return a
  EndIf
  Return b
EndFunction

Int Function MaxLoan()
  Return Game.GetPlayer().GetLevel() * 50000
EndFunction

Function EnsureAccounts()
  If Accounts && Accounts != Self
    Accounts.EnsureAccounts()
    Account = Accounts.Account
    Loan = Accounts.Loan
    LoanDue = Accounts.LoanDue
    Defaulted = Accounts.Defaulted
    Return
  EndIf
  If !Account
    Account = new Int[128]
    Loan = new Int[128]
    LoanDue = new Float[128]
    Defaulted = new Bool[128]
  EndIf
EndFunction

; A loan past its day defaults (once): the bank of that region lends no more until it is repaid.
Function CheckLoan()
  If !AtBank()
    Return
  EndIf
  If Loan[Region] > 0 && !Defaulted[Region] && Utility.GetCurrentGameTime() > LoanDue[Region]
    Defaulted[Region] = True
    Debug.Notification("Your loan is overdue. This bank will lend you nothing more until it is repaid.")
  EndIf
EndFunction

; Carries out a menu what; returns what to tell the player.
String Function Serve(String what, Int amount)
  TMStoryOrder order = Order()
  If !order
    Return ""
  EndIf
  String coin = order.CoinName(Story)
  If order.MoneyHere() != Story
    Return "Only " + coin + " are changed here."
  EndIf
  Actor player = Game.GetPlayer()
  If what == "cash"
    If !AtBank() || !LetterOfCredit
      Return ""
    EndIf
    Int letters = player.GetItemCount(LetterOfCredit)
    If letters <= 0
      Return "You have no letters of credit."
    EndIf
    player.RemoveItem(LetterOfCredit, letters, True)
    Account[Region] = Account[Region] + letters
    Return "Your letters of credit for " + letters + " " + coin + " are paid into your account."
  EndIf
  If amount <= 0
    Return "Choose an amount first."
  EndIf
  If StringUtil.Find(what, "exchange:") == 0
    Int from = StringUtil.Substring(what, 9) as Int
    Int paid = order.Exchange(from, amount, Fee())
    If paid <= 0
      Return "You have nothing of that coin to change."
    EndIf
    Return "You receive " + paid + " " + coin + "."
  EndIf
  If !AtBank()
    Return ""
  EndIf
  MiscObject gold = order.Gold
  Int carried = player.GetItemCount(gold)
  Int n = 0
  If what == "deposit"
    n = Least(amount, carried)
    If n <= 0
      Return "You carry no " + coin + "."
    EndIf
    player.RemoveItem(gold, n, True)
    Account[Region] = Account[Region] + n
    Return "You deposit " + n + " " + coin + "."
  ElseIf what == "withdraw"
    n = Least(amount, Account[Region])
    If n <= 0
      Return "Your account is empty."
    EndIf
    Account[Region] = Account[Region] - n
    player.AddItem(gold, n, True)
    Return "You withdraw " + n + " " + coin + "."
  ElseIf what == "letter"
    Int cost = amount + (amount + 99) / 100
    If !LetterOfCredit || cost > Account[Region]
      Return "A letter of credit for " + amount + " costs " + cost + " from your account."
    EndIf
    Account[Region] = Account[Region] - cost
    player.AddItem(LetterOfCredit, amount, True)
    Return "You are given a letter of credit for " + amount + " " + coin + "."
  ElseIf what == "borrow"
    If Defaulted[Region]
      Return "This bank will not lend to you until your old loan is repaid."
    ElseIf Loan[Region] > 0
      Return "You must repay your loan before you borrow again."
    EndIf
    n = Least(amount, MaxLoan())
    Loan[Region] = n + n / 10
    LoanDue[Region] = Utility.GetCurrentGameTime() + LoanDays
    player.AddItem(gold, n, True)
    Return "You borrow " + n + " " + coin + ". Repay " + Loan[Region] + " within " + (LoanDays as Int) + " days."
  ElseIf what == "repay"
    n = Least(Least(amount, Loan[Region]), carried)
    If Loan[Region] <= 0
      Return "You owe this bank nothing."
    ElseIf n <= 0
      Return "You carry no " + coin + "."
    EndIf
    player.RemoveItem(gold, n, True)
    Loan[Region] = Loan[Region] - n
    If Loan[Region] <= 0
      Loan[Region] = 0
      Defaulted[Region] = False
      Return "Your loan is repaid."
    EndIf
    Return "You repay " + n + ". You still owe " + Loan[Region] + "."
  EndIf
  Return ""
EndFunction
