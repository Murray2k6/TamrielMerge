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

; The plugin's version (1); a script can test for the plugin with it.
Int Function Version() Global Native
