ScriptName TMNative Hidden
{TamrielMergeNative.dll (an SKSE plugin, tools/skse/TamrielMergeNative): Skyrim's own classic script functions, the
ones its console and conditions still carry, called by name. Many of Oblivion's are there under the same names
(GetKnockedState, IsSpellTarget, SetForceRun, PickIdle, SetRigidBodyMass, ...), so the converted scripts call them
here rather than standing in for them.}

; A condition's value on subject: form is its first parameter when that is a form (a spell, an actor), number when it
; is a number; 0 when the condition isn't there or doesn't take what was given (logged).
Float Function Condition(String name, ObjectReference subject, Form arg = None, Int number = 0) Global Native

; A command run on subject with its numbers (count: how many to pass, -1 for all it takes); False when it isn't there,
; takes something other than numbers, or fails (logged).
Bool Function Command(String name, ObjectReference subject, Float a = 0.0, Float b = 0.0, Int count = -1) Global Native

; The function is there: as a condition (True) or a command (False).
Bool Function HasFunction(String name, Bool condition) Global Native

; The free camera (the console's ToggleFreeCamera) on or off; True when it is as asked.
Bool Function FreeCamera(Bool on) Global Native
Bool Function IsFreeCamera() Global Native

; The free camera placed and aimed: a world position, pitch (down positive) and yaw (the heading, clockwise from
; north) in radians. False when the free camera isn't on.
Bool Function SetFreeCamera(Float x, Float y, Float z, Float pitch, Float yaw) Global Native

; The free camera's position (0, 1, 2), pitch (3) and yaw (4).
Float Function GetFreeCamera(Int i) Global Native

; The plugin's version (2: GetFlag and SetFlag; 3: the free camera); a script can test for the plugin with it.
; Dev aid: every reference of the player's cell written to the plugin's log as the game holds it (from the console:
; cgf "TMNative.DumpCell"). Returns how many.
Int Function DumpCell() Global Native
Int Function Version() Global Native

; A flag kept outside the saves (Documents\My Games\Skyrim Special Edition\SKSE\TamrielMerge.ini): the same for every
; game the player starts. False when it was never set.
Bool Function GetFlag(String name) Global Native
Function SetFlag(String name, Bool on) Global Native
