// The Tamriel menu (TamrielMerge, docs/progression.md). ActionScript 2 for Skyrim's Scaleform, compiled with FFDec
// (Skyrim.Ui.FlashMenus) and opened by TMProgression through SKSE's UI.OpenCustomMenu. Everything is drawn in code.
// Papyrus fills it: ClearFactions / AddFaction("game|name|rank|ranks|title") / ShowFactions, and ClearMoney /
// AddMoney("game|coin|amount|carried|story") / ShowMoney; it asks for that data when loaded (mod event TM_MenuReady) and
// reports closing (TM_MenuClose). Opened by a money changer or bank (TMMoneyChanger), it also gets OpenService("kind|
// title|fee"), BankState(...) and ServiceStatus(text), and sends each button as mod event TM_Service(action, amount).
// Travel (TMTravel, docs/travel.md: every converted world, Daggerfall's travel made in real time): it sends TM_Travel
// ("worlds"; "world:w"; "group:w:g"; "pick:w:g:t"; "opt:cautious|inn|ship|horse"; "speed" with 1-10; "go"; "continue")
// and gets ClearTravel / AddTravel("w|w|name|status", "g|w:g|name|towns" or "t|w:g:t|name|time") / ShowTravel(title) /
// TravelStatus(text) / TripInfo("name|kind|time|cost|gold|cautious|inn|ship|horse|horse here|speed|note|world|era"); each
// destination is labelled with its time's era and year ("3E 427").

var root = this;
var W = 1280;
var H = 720;
var PAD = 60;
var factions = [];
var money = [];
var tab = 0;
var page = 0;
var PER_PAGE = 18;

function box(mc, x, y, w, h, colour, alpha) {
    mc.beginFill(colour, alpha);
    mc.moveTo(x, y); mc.lineTo(x + w, y); mc.lineTo(x + w, y + h); mc.lineTo(x, y + h); mc.lineTo(x, y);
    mc.endFill();
}

function text(parent, name, depth, x, y, w, h, size, colour, bold, align) {
    parent.createTextField(name, depth, x, y, w, h);
    var t = parent[name];
    var f = new TextFormat();
    f.font = bold ? "$EverywhereBoldFont" : "$EverywhereFont";
    f.size = size;
    f.color = colour;
    if (align != undefined) f.align = align;
    t.embedFonts = true;
    t.selectable = false;
    t.setNewTextFormat(f);
    return t;
}

function button(parent, name, depth, x, y, w, h, label, onPress) {
    var b = parent.createEmptyMovieClip(name, depth);
    box(b, 0, 0, w, h, 0x2A2A30, 90);
    b._x = x; b._y = y;
    var t = text(b, "label", 1, 0, 6, w, h - 6, 18, 0xE8E2D0, false, "center");
    t.text = label;
    b.onRelease = onPress;
    b.onRollOver = function() { this._alpha = 80; };
    b.onRollOut = function() { this._alpha = 100; };
    return b;
}

function close() {
    skse.SendModEvent("TM_MenuClose", "", 0);
    skse.CloseMenu("CustomMenu");
}

// ---- frame ----
var back = this.createEmptyMovieClip("back", 1);
box(back, 0, 0, W, H, 0x000000, 60);
box(back, PAD, PAD - 10, W - 2 * PAD, H - 2 * PAD + 20, 0x121216, 92);
var title = text(this, "title", 2, PAD + 20, PAD, 400, 40, 30, 0xF0E6C8, true);
title.text = "Tamriel";
var purse = text(this, "purse", 3, PAD + 20, PAD + 38, 380, 24, 16, 0xD9B76A, false);
purse.text = "";
var tabNames = ["Factions", "Coin", "Travel", "Skills", "Passives"];
for (var i = 0; i < tabNames.length; i++) {
    var b = button(this, "tab" + i, 10 + i, PAD + 330 + i * 160, PAD + 4, 150, 34, tabNames[i], null);
    b.index = i;
    b.onRelease = function() {
        tab = this.index; page = 0;
        if (tab == 2) { trip = null; askTravel("worlds", 0); }
        draw();
    };
}
button(this, "closeButton", 20, W - PAD - 130, H - PAD - 34, 110, 34, "Close", function() { close(); });
button(this, "prevButton", 21, PAD + 20, H - PAD - 34, 110, 34, "< Previous", function() { if (page > 0) { page--; draw(); } });
button(this, "nextButton", 22, PAD + 140, H - PAD - 34, 110, 34, "Next >", function() { page++; draw(); });
var body = this.createEmptyMovieClip("body", 5);

var keys = new Object();
keys.onKeyDown = function() { if (Key.getCode() == Key.ESCAPE) close(); };
Key.addListener(keys);

// ---- Papyrus ----
function ClearFactions(s) { factions = []; }
function AddFaction(line) { factions.push(line.split("|")); }
function ShowFactions(s) { if (tab == 0) draw(); }
function ClearMoney(s) { money = []; }
function AddMoney(line) { money.push(line.split("|")); }
function ShowMoney(s) {
    purse.text = "";
    for (var i = 0; i < money.length; i++)
        if (money[i][3] == "1") purse.text = "Carrying " + money[i][2] + " " + money[i][1] + " (" + money[i][0] + ")";
    if (tab == 1) draw();
}

// ---- tabs ----
function draw() {
    body.removeMovieClip();
    body = root.createEmptyMovieClip("body", 5);
    for (var i = 0; i < tabNames.length; i++) root["tab" + i]._alpha = i == tab ? 100 : 55;
    if (tab == 0) drawFactions();
    else if (tab == 1) drawMoney();
    else if (tab == 2) drawTravel();
    else drawLater(tab == 3 ? "Skills" : "Passives");
}

// Each converted game's coin and how much of it the player has; the one carried is the coin of the time they're in.
// With a money changer's or bank's service open, other purses can be changed and the bank's business done below.
function drawMoney() {
    if (money.length == 0) { drawLater("Coin from the converted games"); return; }
    var head = text(body, "note", 1, PAD + 20, PAD + 66, W - 2 * PAD - 40, 30, 16, 0x8C877C, false);
    head.text = "Each time keeps its own coin. You carry the coin of the time you are in; the rest waits in its purse.";
    for (var i = 0; i < money.length; i++) {
        var m = money[i];
        var y = PAD + 104 + i * 40;
        var carried = m[3] == "1";
        if (carried) box(body, PAD + 14, y - 4, W - 2 * PAD - 28, 36, 0x2A2A30, 80);
        var g = text(body, "g" + i, 100 + i * 4, PAD + 30, y + 2, 300, 32, 20, 0xD9B76A, true);
        g.text = m[0];
        var c = text(body, "c" + i, 101 + i * 4, PAD + 340, y + 2, 420, 32, 20, carried ? 0xF0E6C8 : 0xA8A090, false);
        c.text = m[2] + " " + m[1];
        if (service != null && !carried && Number(m[2]) > 0) {
            var x = button(body, "x" + i, 102 + i * 4, W - PAD - 200, y - 2, 170, 32, "Exchange", null);
            x.story = m[4];
            x.onRelease = function() { serve("exchange:" + this.story); };
        } else {
            var s = text(body, "s" + i, 103 + i * 4, W - PAD - 330, y + 4, 300, 32, 16, 0x8C877C, false, "right");
            s.text = carried ? "carried" : "in its purse";
        }
    }
    if (service != null) drawService(PAD + 104 + money.length * 40 + 6);
}

// ---- services: a money changer's or a bank's (TMMoneyChanger) ----
var service = null;
var bank = null;
var amount = 100;
var status = "";
function OpenService(line) {
    var p = line.split("|");
    service = { kind: p[0], title: p[1], fee: Number(p[2]) };
    if (service.kind != "bank") bank = null;
    tab = 1;
    draw();
}
function BankState(line) { bank = line.split("|"); if (tab == 1) draw(); }
function ServiceStatus(s) { status = s; if (tab == 1) draw(); }
function serve(action) { skse.SendModEvent("TM_Service", action, amount); }

function drawService(top) {
    box(body, PAD + 14, top, W - 2 * PAD - 28, 2, 0x5A5040, 100);
    var t = text(body, "svTitle", 200, PAD + 30, top + 8, 700, 30, 20, 0xF0E6C8, true);
    t.text = service.title + "  -  exchange fee " + service.fee + "%";
    // The amount every action uses.
    var a = text(body, "svAmount", 201, PAD + 30, top + 48, 260, 30, 18, 0xF0E6C8, false);
    a.text = "Amount: " + amount;
    var steps = [-1000, -100, -10, 10, 100, 1000];
    for (var i = 0; i < steps.length; i++) {
        var b = button(body, "step" + i, 210 + i, PAD + 300 + i * 92, top + 44, 84, 32, (steps[i] > 0 ? "+" : "") + steps[i], null);
        b.step = steps[i];
        b.onRelease = function() { amount += this.step; if (amount < 0) amount = 0; draw(); };
    }
    var y = top + 88;
    if (bank != null) {
        var loan = Number(bank[1]);
        var due = loan <= 0 ? "no loan" : "loan " + loan + (bank[3] == "1" ? " (overdue)" : " (due in " + bank[2] + " days)");
        var info = text(body, "svBank", 220, PAD + 30, y, W - 2 * PAD - 60, 30, 17, 0xD9B76A, false);
        info.text = "Account " + bank[0] + "   |   " + due + "   |   letters of credit " + bank[4] + "   |   you may borrow up to " + bank[5] + (bank[6] == "1" ? " (refused: an old loan defaulted)" : "");
        var acts = [["Deposit", "deposit"], ["Withdraw", "withdraw"], ["Letter of credit", "letter"], ["Cash letters", "cash"], ["Borrow", "borrow"], ["Repay", "repay"]];
        for (var j = 0; j < acts.length; j++) {
            var ab = button(body, "act" + j, 230 + j, PAD + 30 + j * 182, y + 36, 172, 34, acts[j][0], null);
            ab.action = acts[j][1];
            ab.onRelease = function() { serve(this.action); };
        }
        y += 80;
    }
    var st = text(body, "svStatus", 240, PAD + 30, y, W - 2 * PAD - 60, 30, 17, 0xA8A090, false);
    st.text = status;
}

// ---- travel: Daggerfall's, in every converted world (TMTravel) ----
var travel = [];
var travelTitle = "";
var travelStatus = "";
var trip = null;
var TRAVEL_ROWS = 12;
function ClearTravel(s) { travel = []; travelStatus = ""; trip = null; }
function AddTravel(line) { travel.push(line.split("|")); }
function ShowTravel(s) { travelTitle = s; page = 0; if (tab == 2) draw(); }
function TravelStatus(s) { travelStatus = s; if (tab == 2) draw(); }
function TripInfo(line) {
    var p = line.split("|");
    trip = { name: p[0], kind: p[1], time: p[2], cost: Number(p[3]), gold: Number(p[4]), cautious: p[5] == "1", inn: p[6] == "1",
             ship: p[7] == "1", horse: p[8] == "1", horseHere: p[9] == "1", speed: Number(p[10]), note: p[11],
             world: p[12], era: p[13] };
    if (tab == 2) draw();
}
function askTravel(what, n) { skse.SendModEvent("TM_Travel", what, n); }

// Worlds, then a world's regions, then a region's towns with the time on the road; a town opens its trip.
function drawTravel() {
    if (trip != null) { drawTrip(); return; }
    if (travel.length == 0) {
        var none = text(body, "none", 1, PAD + 20, PAD + 70, W - 2 * PAD - 40, 40, 20, 0xA8A090, false);
        none.text = travelStatus != "" ? travelStatus : "Travel is about the converted games' worlds.";
        return;
    }
    var kind = travel[0][0];
    var head = text(body, "head", 1, PAD + 20, PAD + 66, 760, 30, 20, 0xD9B76A, true);
    head.text = travelTitle + (kind == "w" ? "  -  choose a world" : kind == "g" ? "  -  choose a region" : "  -  choose a town");
    if (kind != "w") {
        var ids = travel[0][1].split(":");
        var backButton = button(body, "back", 2, W - PAD - 200, PAD + 62, 170, 32, kind == "g" ? "< Worlds" : "< Regions", null);
        backButton.world = ids[0];
        if (kind == "g") backButton.onRelease = function() { askTravel("worlds", 0); };
        else backButton.onRelease = function() { askTravel("world:" + this.world, 0); };
    }
    var per = TRAVEL_ROWS * 2;
    var pages = Math.ceil(travel.length / per);
    if (page >= pages) page = pages - 1;
    for (var n = 0; n < per && page * per + n < travel.length; n++) {
        var e = travel[page * per + n];
        var column = Math.floor(n / TRAVEL_ROWS);
        var x = PAD + 20 + column * ((W - 2 * PAD - 40) / 2);
        var y = PAD + 104 + (n % TRAVEL_ROWS) * 38;
        var label = e[2];
        if (kind == "w") label += "  (" + e[3] + ")";
        else if (kind == "g") label += "  (" + e[3] + " towns)";
        else if (e[3] != "") label += "  -  " + e[3];
        var b = button(body, "e" + n, 100 + n, x, y, (W - 2 * PAD - 60) / 2, 34, label, null);
        b.id = e[1];
        if (kind == "w") b.onRelease = function() { askTravel("world:" + this.id, 0); };
        else if (kind == "g") b.onRelease = function() { askTravel("group:" + this.id, 0); };
        else b.onRelease = function() { lastPick = this.id; askTravel("pick:" + this.id, 0); };
    }
    var p = text(body, "pageNo", 400, W / 2 - 100, H - PAD - 30, 200, 30, 16, 0x8C877C, false, "center");
    p.text = "page " + (page + 1) + " of " + pages;
    if (travelStatus != "") {
        var st = text(body, "status", 401, PAD + 20, H - PAD - 64, W - 2 * PAD - 300, 30, 16, 0xA8A090, false);
        st.text = travelStatus;
        var cont = button(body, "continue", 402, W - PAD - 270, H - PAD - 70, 250, 32, "Continue the journey", null);
        cont.onRelease = function() { skse.SendModEvent("TM_Travel", "continue", 0); close(); };
    }
}

// Daggerfall's trip: where, how long, what it costs, the options, the pace, and Begin.
var lastPick = "";
function drawTrip() {
    var t = trip;
    var head = text(body, "head", 1, PAD + 20, PAD + 66, 760, 34, 24, 0xD9B76A, true);
    head.text = "Travel to " + t.name + ", " + t.world + (t.era != "" ? "  -  " + t.era : "");
    var backButton = button(body, "back", 2, W - PAD - 200, PAD + 62, 170, 32, "< Towns", null);
    backButton.onRelease = function() {
        trip = null;
        var ids = lastPick.split(":");
        askTravel("group:" + ids[0] + ":" + ids[1], 0);
    };
    var y = PAD + 118;
    var info = text(body, "info", 3, PAD + 30, y, W - 2 * PAD - 60, 34, 20, 0xF0E6C8, false);
    if (t.kind == "time") info.text = "In another time: " + t.era + ".";
    else info.text = (t.time != "" ? "The journey takes " + t.time + "." : "") + "   Cost: " + t.cost + " gold (you have " + t.gold + ").";
    y += 50;
    if (t.kind != "time") {
        var opts = [
            [t.cautious ? "Travel cautiously" : "Travel recklessly", "cautious", true],
            [t.inn ? "Stay at inns" : "Camp out", "inn", true],
            [t.ship ? "By ship when it helps" : "No ships", "ship", true],
            [t.horse && t.horseHere ? "On horseback" : (t.horseHere ? "On foot (horse left)" : "On foot"), "horse", t.horseHere]
        ];
        for (var i = 0; i < opts.length; i++) {
            var ob = button(body, "opt" + i, 10 + i, PAD + 30 + i * 272, y, 260, 34, opts[i][0], null);
            ob.opt = opts[i][1];
            if (opts[i][2]) ob.onRelease = function() { askTravel("opt:" + this.opt, 0); };
            else ob._alpha = 45;
        }
        y += 56;
        // The pace: 1 the natural pace, 10 the fastest the land can be loaded at.
        var sp = text(body, "speed", 20, PAD + 30, y + 4, 520, 30, 18, 0xF0E6C8, false);
        sp.text = "Pace: " + t.speed + " of 10  (" + (t.speed <= 1 ? "walking pace" : t.speed >= 10 ? "as fast as the land loads" : "faster") + ")";
        var minus = button(body, "slower", 21, PAD + 560, y, 60, 32, "-", null);
        minus.onRelease = function() { askTravel("speed", Math.max(1, trip.speed - 1)); };
        var plus = button(body, "faster", 22, PAD + 630, y, 60, 32, "+", null);
        plus.onRelease = function() { askTravel("speed", Math.min(10, trip.speed + 1)); };
        for (var k = 1; k <= 10; k++) box(body, PAD + 710 + k * 22, y + 10, 18, 14, k <= t.speed ? 0xD9B76A : 0x3A3A40, 100);
        y += 56;
    }
    if (t.note != "") {
        var note = text(body, "note", 30, PAD + 30, y, W - 2 * PAD - 60, 30, 17, 0xA8A090, false);
        note.text = t.note;
        y += 44;
    }
    if (t.kind == "road" || t.kind == "time") {
        var go = button(body, "begin", 40, PAD + 30, y, 240, 38, t.kind == "time" ? "Travel through time" : "Begin the journey", null);
        go.onRelease = function() { skse.SendModEvent("TM_Travel", "go", 0); close(); };
        if (t.kind == "road") {
            var hint = text(body, "hint", 41, PAD + 290, y + 8, W - 2 * PAD - 320, 30, 15, 0x8C877C, false);
            hint.text = "You travel on your own, the days passing; a movement key, or a fight, stops the journey.";
        }
    }
}

function drawLater(what) {
    var t = text(body, "soon", 1, PAD + 20, PAD + 70, W - 2 * PAD - 40, 40, 20, 0xA8A090, false);
    t.text = what + " are on their way.";
}

// Factions game by game: name, then rank and title, or "not a member".
function drawFactions() {
    var lines = [];
    var game = "";
    for (var i = 0; i < factions.length; i++) {
        var f = factions[i];
        if (f[0] != game) { game = f[0]; lines.push({ header: game }); }
        lines.push({ name: f[1], rank: Number(f[2]), ranks: Number(f[3]), title: f[4] });
    }
    if (lines.length == 0) { drawLater("Factions from the converted games"); return; }
    var pages = Math.ceil(lines.length / (PER_PAGE * 2));
    if (page >= pages) page = pages - 1;
    var start = page * PER_PAGE * 2;
    for (var n = 0; n < PER_PAGE * 2 && start + n < lines.length; n++) {
        var l = lines[start + n];
        var column = Math.floor(n / PER_PAGE);
        var x = PAD + 20 + column * ((W - 2 * PAD) / 2);
        var y = PAD + 60 + (n % PER_PAGE) * 30;
        if (l.header != undefined) {
            var h = text(body, "h" + n, 100 + n, x, y + 4, 520, 30, 20, 0xD9B76A, true);
            h.text = l.header;
        } else {
            var t = text(body, "f" + n, 100 + n, x + 16, y + 4, 520, 30, 17, l.rank > 0 ? 0xF0E6C8 : 0x8C877C, false);
            t.text = l.name + "  -  " + (l.rank > 0 ? "rank " + l.rank + " of " + l.ranks + (l.title != "" ? ": " + l.title : "") : "not a member");
        }
    }
    var p = text(body, "pageNo", 400, W / 2 - 100, H - PAD - 30, 200, 30, 16, 0x8C877C, false, "center");
    p.text = "page " + (page + 1) + " of " + pages;
}

draw();
skse.SendModEvent("TM_MenuReady", "", 0);
