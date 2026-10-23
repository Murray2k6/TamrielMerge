// Daggerfall's riddles (TamrielMerge; Daggerfall Unity DaggerfallAction.ShowTextWithInput, MIT): the riddle's text and a
// line to type the answer on (DaggerfallInputMessageBox: 20 characters after " > "). ActionScript 2 for Skyrim's
// Scaleform, compiled with FFDec (Skyrim.Ui.FlashMenus) and opened by TMDfAction through SKSE's UI.OpenCustomMenu. It asks
// for the text when loaded (mod event TM_RiddleReady), gets Ask(text), and sends what was typed, on Enter, as mod event
// TM_RiddleAnswer (0; Escape sends "" with -1). TMDfAction checks the answer.

var root = this;
var W = 1280;
var H = 720;
var MAX_CHARS = 20;

function box(mc, x, y, w, h, colour, alpha) {
    mc.beginFill(colour, alpha);
    mc.moveTo(x, y); mc.lineTo(x + w, y); mc.lineTo(x + w, y + h); mc.lineTo(x, y + h); mc.lineTo(x, y);
    mc.endFill();
}

function format(size, colour, bold, align) {
    var f = new TextFormat();
    f.font = bold ? "$EverywhereBoldFont" : "$EverywhereFont";
    f.size = size;
    f.color = colour;
    if (align != undefined) f.align = align;
    return f;
}

function text(parent, name, depth, x, y, w, h, f) {
    parent.createTextField(name, depth, x, y, w, h);
    var t = parent[name];
    t.embedFonts = true;
    t.selectable = false;
    t.setNewTextFormat(f);
    return t;
}

var back = this.createEmptyMovieClip("back", 1);
box(back, 0, 0, W, H, 0x000000, 50);
var panel = this.createEmptyMovieClip("panel", 2);
box(panel, 240, 170, 800, 380, 0x121216, 92);
box(panel, 300, 470, 680, 40, 0x2A2A30, 90);

var question = text(this, "question", 3, 270, 195, 740, 260, format(20, 0xF0E6C8, false, "center"));
question.multiline = true;
question.wordWrap = true;
question.text = "";

var prompt = text(this, "prompt", 4, 304, 476, 40, 34, format(22, 0xD9B76A, true));
prompt.text = " > ";

var answer = text(this, "answer", 5, 344, 476, 630, 34, format(22, 0xE8E2D0, false));
answer.type = "input";
answer.selectable = true;
answer.maxChars = MAX_CHARS;
answer.text = "";

var hint = text(this, "hint", 6, 270, 518, 740, 24, format(14, 0xA8A090, false, "center"));
hint.text = "Type your answer and press Enter.";

var done = false;

function finish(typed, n) {
    if (done) return;
    done = true;
    skse.AllowTextInput(false);
    skse.SendModEvent("TM_RiddleAnswer", typed, n);
    skse.CloseMenu("CustomMenu");
}

var keys = new Object();
keys.onKeyDown = function() {
    var code = Key.getCode();
    if (code == Key.ENTER) finish(answer.text, 0);
    else if (code == Key.ESCAPE) finish("", -1);
};
Key.addListener(keys);

// Papyrus: the riddle's text.
function Ask(s) {
    question.text = s;
    Selection.setFocus(answer);
}

skse.AllowTextInput(true);
Selection.setFocus(answer);
skse.SendModEvent("TM_RiddleReady", "", 0);
